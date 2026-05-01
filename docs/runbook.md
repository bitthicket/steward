# Steward Production Runbook

## Table of contents

- [Logging](#logging)
- [Health checks](#health-checks)
- [Metrics](#metrics)
- [Backups and restore testing](#backups-and-restore-testing)
- [Alerting](#alerting)

---

## Logging

### Format

The Steward API, `steward-ingestion-akoya`, and future ingestion services emit structured JSON logs to **stdout** via Serilog + `CompactJsonFormatter`. Northflank collects stdout from each container and makes it searchable in the service dashboard.

### Enriched fields

Every authenticated request carries these extra log properties:

| Property   | Source                              |
|------------|-------------------------------------|
| `tenantId` | JWT `tid` claim                     |
| `userId`   | JWT `sub` claim                     |
| `requestId`| ASP.NET Core `TraceIdentifier`      |
| `route`    | Request path                        |

Anonymous requests show `tenantId=anonymous` and `userId=anonymous`.

### Sensitive data redaction

The `SecretMaskingPolicy` (in `Logging.fs`) automatically replaces values whose property names contain:
`accessToken`, `refreshToken`, `password`, `secret`, `apiSecret`, `apiKey`, `privateKey`.

### Searching logs in Northflank

1. Open the Northflank dashboard for the target project.
2. Select the service (`steward-api`, `steward-ingestion-akoya`, etc.).
3. Go to **Logs**.
4. Use the search bar with JSON field queries:
   - `@tenantId:"abc-123"`
   - `@route:"/api/accounts"`
   - `@level:"Error"`
   - `@message:"Database connection failed"`

### Local development

When running locally the same JSON format is written to the console. Pipe through `jq` for readability:

```bash
dotnet run --project src/BitThicket.Steward.Api | jq .
```

---

## Health checks

### Endpoints

| Endpoint        | Purpose      | Auth required | Behaviour                                 |
|-----------------|--------------|---------------|-------------------------------------------|
| `GET /health`   | Liveness     | No            | Always returns `200 {"status":"ok"}`      |
| `GET /health/ready` | Readiness | No        | Returns `200` if healthy, `503` if not    |

### Readiness checks (`/health/ready`)

The readiness endpoint verifies three subsystems:

1. **Database** — runs `SELECT 1` against the runtime connection string.
2. **Vault** — performs an AES-256-GCM encrypt/decrypt roundtrip with the current `STEWARD_VAULT_KEY`.
3. **Migrations** — asks DbUp whether any pending scripts remain.

If any check fails the response is `503` with a JSON body listing each subsystem:

```json
{
  "status": "unhealthy",
  "checks": [
    { "name": "database", "status": "healthy", "message": "Database connection OK" },
    { "name": "vault", "status": "healthy", "message": "Vault encrypt/decrypt roundtrip OK" },
    { "name": "migrations", "status": "unhealthy", "message": "2 pending migration(s)" }
  ]
}
```

### Northflank probe configuration

Configure the API service with:

- **Liveness probe**: `GET /health` — restarts the container if it fails.
- **Readiness probe**: `GET /health/ready` — removes the container from the load balancer if it fails.

The Akoya ingestion service already exposes `GET /health` for liveness. Add readiness there when the service gains a database dependency.

---

## Metrics

### Endpoint

`GET /metrics` — returns Prometheus exposition format. Protected by a **service token** (`Authorization: Bearer <STEWARD_SERVICE_TOKEN>`). Returns `503` if no service token is configured.

### Metric families

| Metric name                        | Type    | Labels                        |
|------------------------------------|---------|-------------------------------|
| `requests_total`                   | counter | `route`, `status`             |
| `sync_events_total`                | counter | `provider`, `outcome`         |
| `feed_health_status`               | gauge   | `provider`, `status`          |
| `db_query_duration_seconds_sum`    | summary | `repo`, `op`                  |
| `db_query_duration_seconds_count`  | summary | `repo`, `op`                  |

### Scraping

A future Prometheus or Grafana Agent sidecar can poll `https://<api>/metrics` with the service token. For now the endpoint is the seam where external scraping plugs in.

---

## Backups and restore testing

### Automated snapshots

Northflank managed Postgres addons take daily snapshots by default. Retention is handled by Northflank (currently 7 days for standard plans — confirm in the addon settings).

### Restore-test script

`tools/restore-test.sh` automates a end-to-end restore smoke test:

1. Queries the Northflank API for the latest snapshot of the production Postgres addon.
2. Provisions a **scratch** Postgres addon from that snapshot.
3. Waits for the scratch addon to become ready.
4. Runs `SELECT count(*)` against `tenants`, `users`, and `transactions`.
5. Tears down the scratch addon.

### Running on demand

```bash
export STEWARD_NF_API_TOKEN="nf_api_..."
export STEWARD_NF_PROJECT="steward-prod"
export STEWARD_NF_POSTGRES_ADDON="postgres-prod"
./tools/restore-test.sh
```

### Running via Paperclip routine

Create a monthly routine that:
- Injects the three env vars above.
- Runs `./tools/restore-test.sh`.
- Fails the routine execution if the script exits non-zero.

---

## Alerting

### Northflank built-in alerts

Enable Northflank alerts for the following services:

| Service                      | Alert type                | Threshold | Destination |
|------------------------------|---------------------------|-----------|-------------|
| `steward-api`                | Container restart loop    | > 3 / 5m  | founder email |
| `steward-api`                | Error rate                | > 5% / 5m | founder email |
| `steward-ingestion-akoya`    | Container restart loop    | > 3 / 5m  | founder email |
| `steward-ingestion-akoya`    | Error rate                | > 5% / 5m | founder email |
| `steward-ingestion-plaid`    | Container restart loop    | > 3 / 5m  | founder email |
| `steward-ingestion-plaid`    | Error rate                | > 5% / 5m | founder email |

> **Note:** `steward-ingestion-plaid` does not exist yet (pending D1/D2). Configure its alerts once the service is deployed.

### Forcing a crash test

To verify alerts fire, temporarily deploy a canary image that exits immediately:

```dockerfile
FROM alpine
CMD ["sh", "-c", "echo 'crash test' && exit 1"]
```

Push to a separate Northflank service, trigger a deploy, and confirm the alert email arrives. Roll back immediately after verification.

### Future APM

Post-MVP we will evaluate Sentry, OpenTelemetry + Honeycomb, or Datadog for distributed tracing and richer alerting. The `/metrics` endpoint and JSON logging are the integration seams for any of these tools.
