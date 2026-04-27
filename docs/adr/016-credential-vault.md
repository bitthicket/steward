# ADR-016: Credential Vault

## Status
Accepted

## Context
Steward connects to financial data providers (Plaid, Akoya) using OAuth access tokens and refresh tokens. These credentials are long-lived secrets that must be stored securely. We need:

1. **Encrypted-at-rest storage** — database breaches must not expose tokens.
2. **Tenant isolation** — one tenant must never access another tenant's credentials.
3. **Key rotation** — we must be able to rotate the encryption key without service downtime.
4. **Operational safety** — plaintext must never appear in logs.

## Decision
We will store credentials in a dedicated `credential_vault` table with AES-256-GCM encryption, using an environment-supplied key.

### Schema
```sql
CREATE TABLE credential_vault (
    id            uuid        PRIMARY KEY,
    tenant_id     uuid        NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    ref           text        NOT NULL UNIQUE,
    ciphertext    bytea       NOT NULL,
    nonce         bytea       NOT NULL,
    key_version   int         NOT NULL,
    created_at    timestamptz NOT NULL DEFAULT now(),
    updated_at    timestamptz NOT NULL DEFAULT now()
);
```

- `ref` is an opaque pointer (`prv_<provider>_<ulid>`) stored in `data_feed_connections.credential_ref`.
- `key_version` identifies which key wrapped the row, enabling graceful rotation.

### Encryption
- **Algorithm**: AES-256-GCM (authenticated encryption).
- **Key source**: `STEWARD_VAULT_KEY` env var (32 bytes, base64-encoded).
- **Rotation support**: `STEWARD_VAULT_KEY_PREVIOUS` (decrypt-only) allows seamless re-encryption.
- **Plaintext format**: JSON envelope `{ accessToken, refreshToken?, expiresAt?, providerSpecific? }`.

### API
Internal-only (no HTTP surface):

```fsharp
type IVaultService =
    abstract StoreAsync  : TenantContext * CredentialEnvelope -> Task<string>  // returns ref
    abstract LoadAsync   : TenantContext * string -> Task<CredentialEnvelope>
    abstract DeleteAsync : TenantContext * string -> Task<bool>
    abstract RotateAsync : TenantContext * string -> Task<bool>
```

### Logging
- `SecretMaskingPolicy` is registered with Serilog so any destructured object containing `accessToken`, `refreshToken`, `password`, `secret`, `apiSecret`, `apiKey`, or `privateKey` is automatically redacted to `[REDACTED]`.
- The vault service itself logs only `ref` and `key_version`, never plaintext.

## Consequences

### Pros
- Simple and self-contained. No external KMS dependency for MVP.
- RLS + tenant-scoped queries guarantee isolation.
- Key rotation is a single admin operation (re-encrypt all rows).

### Cons
- The encryption key lives in Northflank environment variables. A compromise of the platform could expose it.
- No HSM or external KMS integration yet.

## Future Work
- Evaluate AWS KMS / GCP Cloud KMS / HashiCorp Vault for post-MVP compliance requirements.
- Add automatic scheduled rotation (e.g., quarterly) rather than manual admin triggers.
