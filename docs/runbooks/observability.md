# Observability Runbook

## Overview

This runbook covers the production observability stack for Steward: structured logging, health checks, metrics, and backups.

## Endpoints

| Endpoint | Purpose | Auth |
|----------|---------|------|
| `GET /health` | Health check with database connectivity test | None |
| `GET /metrics` | Prometheus-compatible metrics scrape endpoint | None |

### Health Checks

The `/health` endpoint returns a JSON report:

```json
{
  "status": "Healthy",
  "version": "1.0.0.0",
  "checks": {
    "self": { "status": "Healthy", "description": null },
    "postgresql": { "status": "Healthy", "description": "PostgreSQL is reachable" }
  }
}
```

- `self` — always healthy if the process is running.
- `postgresql` — executes `SELECT 1` via the runtime connection pool.

A `Degraded` or `Unhealthy` PostgreSQL check returns HTTP 503.

### Metrics

The `/metrics` endpoint exposes Prometheus-formatted metrics powered by OpenTelemetry:

- ASP.NET Core request metrics (count, duration, active requests)
- .NET runtime metrics (GC, thread pool, memory)
- HTTP client metrics (outgoing request count and duration)

Configure your Prometheus scraper to target `/metrics` every 15–30 seconds.

## Logs

In production (when `ASPNETCORE_ENVIRONMENT=Production` or `DOTNET_RUNNING_IN_CONTAINER=true`), logs are emitted as **compact JSON** via `Serilog.Formatting.Compact`. Each log line is a single JSON object suitable for ingestion by Northflank’s log viewer, Datadog, or any structured log aggregator.

Sensitive fields (`accessToken`, `refreshToken`, `password`, `secret`, `apiSecret`, `apiKey`, `privateKey`) are automatically redacted via `SecretMaskingPolicy`.

Request logging is enabled via `SerilogRequestLoggingMiddleware`, producing one line per HTTP request:

```json
{"@t":"2026-04-27T15:30:00.0000000Z","@m":"HTTP GET /health responded 200 in 12.3456 ms","RequestMethod":"GET","RequestPath":"/health","StatusCode":200,"Elapsed":12.3456}
```

## Backups

### Automated (Northflank)

Northflank’s managed PostgreSQL add-on performs automated daily backups. Refer to the Northflank dashboard for retention settings and point-in-time recovery.

### Manual / Ad-hoc

For manual exports or local restores, use the provided backup script:

```bash
export STEWARD_DATABASE_URL="postgres://user:pass@host:5432/steward"
./tools/backup.sh ./backups
```

This produces a plain SQL dump (`steward_backup_YYYYMMDD_HHMMSS.sql`) suitable for `psql` restore:

```bash
psql $STEWARD_DATABASE_URL < ./backups/steward_backup_20260427_120000.sql
```

## Alerting Suggestions

| Signal | Threshold | Action |
|--------|-----------|--------|
| `/health` status != `Healthy` | 2 consecutive failures | Page on-call |
| `http_server_request_duration_seconds` p99 | > 2s | Investigate latency |
| `http_server_active_requests` | > 100 | Check for overload or stuck requests |
| PostgreSQL check unhealthy | Any | Verify Northflank add-on status |

## Related Decisions

- [ADR-007](../adr/007-deployment-and-hosting.md): deployment and hosting strategy
