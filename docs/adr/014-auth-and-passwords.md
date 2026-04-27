# ADR-014: Auth and Passwords

## Status

Accepted

## Context

Steward needs user registration, login, and JWT-based session tokens. Every API
request after login must carry tenant context so that row-level security (RLS)
can scope queries to the caller's organisation. This ADR covers the password
hashing algorithm, JWT shape, and key-rotation strategy.

## Decision

### Password hashing — Argon2id

We hash passwords with **Argon2id**, the current OWASP-recommended algorithm.
The implementation uses the pure-.NET library
`Konscious.Security.Cryptography.Argon2` (no native dependencies, which keeps
container builds simple).

Chosen parameters target ~100 ms per hash on modern server hardware while
resisting GPU/ASIC attacks through high memory usage:

| Parameter | Value | Rationale |
|---|---|---|
| Memory | 64 MB (`m=65536`) | Raises the cost of parallel attacks |
| Iterations | 3 (`t=3`) | Tunes the time cost |
| Parallelism | 4 (`p=4`) | Matches typical CPU core count |
| Salt length | 16 bytes | 128-bit collision resistance |
| Hash length | 32 bytes | 256-bit output |

The hash string uses the standard modular crypt format:

```
$argon2id$v=19$m=65536,t=3,p=4$<salt-base64>$<hash-base64>
```

Verification uses `CryptographicOperations.FixedTimeEquals` to prevent timing
attacks.

### JWT — HS256 with short-lived access tokens

Tokens are signed with **HS256** (HMAC-SHA-256). The signing key is read from
the `STEWARD_JWT_SECRET` environment variable at startup. A secondary key,
`STEWARD_JWT_SECRET_PREVIOUS`, is supported for rotation: newly issued tokens
are always signed with the current secret, but verification (implemented in
STE-21) will accept tokens signed with the previous secret during a rollover
window.

Token lifetime is **1 hour**. There are **no refresh tokens** for the MVP;
the founder is the first user and re-authenticating on expiry is acceptable.
A refresh-token flow is a post-MVP follow-up.

#### Claims

| Claim | Key | Type | Source |
|---|---|---|---|
| Subject | `sub` | string (UUID) | User ID |
| Issued at | `iat` | NumericDate | Server clock |
| Expiration | `exp` | NumericDate | `iat + 1 hour` |
| Tenant ID | `tid` | string (UUID) | Selected tenant at login |
| Tenant name | `tn` | string | Tenant `display_name` |
| Membership role | `mr` | string | `user_tenant_memberships.role` |

### Endpoints

- `POST /auth/register` — creates `users`, `tenants`, and `user_tenant_memberships`
  in a single transaction. Returns `{ userId, tenantId, accessToken }`. Email
collision → 409.
- `POST /auth/login` — validates credentials. If the user has multiple
  memberships and no `tenantId` is supplied, returns `{ memberships: [...] }`
so the client can select one. Otherwise returns `{ accessToken }`.

Both endpoints use `RootRepository` and **do not** set `steward.tenant_id`,
because they operate on global (non-tenant-scoped) tables.

## Consequences

- Argon2id + 64 MB memory makes offline brute force expensive.
- Short-lived HS256 tokens are simple to implement and verify, but key
  distribution is required if the API ever scales horizontally. A move to
  RS256 or ECDSA is possible later without changing the claim shape.
- No refresh tokens means users must re-login every hour. This is acceptable
  for the MVP but will need addressing before general availability.
- The `STEWARD_JWT_SECRET_PREVIOUS` rotation mechanism allows seamless key
  rotation without invalidating all active sessions.
