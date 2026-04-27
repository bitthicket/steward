# Service Token Rotation

The Steward Core API and its ingestion services authenticate to each other via a shared service token (`STEWARD_SERVICE_TOKEN`). This document describes how to rotate that token without downtime.

## Scope

Applies to:
- `steward-api` (Core API)
- `steward-ingestion-plaid` (Plaid ingestion service)
- `steward-ingestion-akoya` (Akoya ingestion service)

## Prerequisites

- Northflank CLI access or web console access to the Steward project.
- A new 256-bit random token (base64-encoded or hex). Generate one with:
  ```bash
  openssl rand -base64 32
  ```

## Rotation Procedure (Zero-Downtime)

### 1. Deploy the new token as a secondary secret

In Northflank, add the new token to each service as an **additional** environment variable named `STEWARD_SERVICE_TOKEN_NEW`. Do **not** replace `STEWARD_SERVICE_TOKEN` yet.

### 2. Update services to accept both tokens

For each ingestion service and the Core API, deploy a version that checks both `STEWARD_SERVICE_TOKEN` and `STEWARD_SERVICE_TOKEN_NEW` during the transition window. (This is a one-time code change; after it ships, future rotations only require config updates.)

> **Note:** As of the current skeleton, the services only check `STEWARD_SERVICE_TOKEN`. A follow-up operational patch should add dual-token support.

### 3. Verify cross-service communication

Trigger a sync on a test connection for each provider and confirm the sync completes successfully:

```bash
# Plaid
curl -X POST https://api.steward.internal/internal/sync-trigger \
  -H "Authorization: Bearer $OLD_TOKEN" \
  -d '{"tenantId":"...","connectionId":"..."}'

# Akoya
curl -X POST https://akoya-ingestion.steward.internal/sync-trigger \
  -H "Authorization: Bearer $OLD_TOKEN" \
  -d '{"tenantId":"...","connectionId":"..."}'
```

### 4. Atomically swap the primary token

In Northflank:
1. Set `STEWARD_SERVICE_TOKEN` = value of `STEWARD_SERVICE_TOKEN_NEW` on all services.
2. Remove `STEWARD_SERVICE_TOKEN_NEW`.
3. Redeploy/restart the services so the env var change is picked up.

Northflank rolls out the change per service. There is a brief window (seconds) where services may disagree on the active token. To avoid 401s during this window, keep the dual-token check from step 2 in place.

### 5. Verify again

Repeat the sync trigger tests from step 3 to confirm everything works with the new token.

### 6. Clean up dual-token support (optional)

Once the rotation is verified, remove the fallback to `STEWARD_SERVICE_TOKEN_NEW` in code if desired.

## Emergency Rotation (Compromise Response)

If the current token is compromised:

1. Immediately revoke the old token at the load-balancer or firewall level (e.g., drop requests bearing the old `Authorization` header).
2. Follow steps 1 and 4 above, but skip the dual-token transition — accept a brief 401 window while services restart.
3. Audit sync event logs for unauthorized sync triggers.

## Service-Specific Env Vars

| Service | Token Env Var | Ingestion URL Env Var (Core API → service) |
|---|---|---|
| Core API | `STEWARD_SERVICE_TOKEN` | — |
| Plaid ingestion | `STEWARD_SERVICE_TOKEN` | `STEWARD_PLAID_INGESTION_URL` |
| Akoya ingestion | `STEWARD_SERVICE_TOKEN` | `STEWARD_AKOYA_INGESTION_URL` |
