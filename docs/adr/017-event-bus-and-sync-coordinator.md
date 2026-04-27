# ADR-017: Event Bus and Sync Coordinator

## Status

Accepted — implemented in STE-28.

## Context

Steward needs a lightweight mechanism for decoupling sync scheduling from
sync execution:

- The **sync coordinator** decides *when* a connection needs to be synced.
- **Ingestion services** (Plaid, Akoya, etc.) decide *how* to perform the sync.
- A **public API endpoint** allows users to trigger sync on demand.

Per ADR-006, the initial event bus should be "as simple as an in-process
channel" and only graduate to Redis Streams or NATS when traffic justifies it.

## Decision

### 1. In-process event bus

We implement an `IEventBus` interface backed by `System.Threading.Channels`:

- `Publish(envelope: EventEnvelope)` — fire-and-forget.
- `Subscribe(topic, handler)` — returns `IDisposable`.
- Each subscriber gets its own unbounded `Channel` so slow consumers do not
  block fast ones.
- Handler failures are logged and swallowed; remaining subscribers still run.

The envelope is JSON-serialised:

```json
{
  "topic": "sync.requested",
  "jsonPayload": "{\"tenantId\":\"...\",\"connectionId\":\"...\"}",
  "occurredAt": "2026-04-27T12:00:00Z",
  "causationId": null
}
```

This serialised shape is intentionally identical to what an HTTP-pull adapter
would consume, so the boundary upgrade is a drop-in replacement.

### 2. Topics defined now

| Topic | Payload | Producer | Consumer (future) |
|---|---|---|---|
| `sync.requested` | `{ tenantId, connectionId, accountId? }` | SyncCoordinator, on-demand endpoint | Ingestion services (D1, D4) |
| `sync.completed` | `{ tenantId, connectionId, syncEventId, outcome }` | IngestionEndpoints (I1) | Feed health projection (I5) |
| `connection.status_changed` | `{ tenantId, connectionId, oldStatus, newStatus }` | IngestionEndpoints (I1) | Portal notifications (C4) |

### 3. Sync coordinator

A `BackgroundService` ticks every **60 seconds**:

1. Queries `get_connections_due_for_sync()` (SECURITY DEFINER) to discover
   all `Active` connections whose `last_synced_at` is older than their
   `preferred_sync_frequency`.
2. Clamps `preferred_sync_frequency` to **[15 min, 24 h]** at read time.
3. Emits `sync.requested` for each due connection.

The SECURITY DEFINER function is necessary because the coordinator has no
 tenant context and must read connections across all tenants.

### 4. On-demand sync trigger

`POST /api/connections/{id}/sync` (auth required):

1. Looks up the connection via tenant-scoped `IDataFeedConnectionRepository`.
2. Returns **404** if the connection does not exist or belongs to another tenant.
3. Predicts a `syncEventId` (`Guid.NewGuid()`) for client-side correlation.
4. Publishes `sync.requested` and returns **202** with `{ syncEventId }`.

## Consequences

- **Simple and fast** — no external message broker to operate in the MVP.
- **Bounded by a single process** — if we scale the API to multiple replicas,
  each replica runs its own coordinator. This is acceptable for MVP scale
  (connections are deduplicated per replica, not per cluster).
- **Upgrade path is explicit** — replace `InProcessEventBus` with a Redis
  Streams adapter that implements the same `IEventBus` interface.
- **Testability** — `CapturingEventBus` implements `IEventBus` for unit and
  integration tests.

## Migration

Two migrations were added:

1. `0023-user-preferences.sql` — creates the `user_preferences` table (needed
   for per-user default sync frequency in later UI work).
2. `0024-sync-coordinator.sql` — adds `preferred_sync_frequency` and
   `last_synced_at` to `data_feed_connections`, plus SECURITY DEFINER helpers.
