# ADR-005: Data feed abstraction for multi-provider sync

## Status

Accepted

## Context

The product goal is hourly or more frequent transaction sync so that AI agents and the portal see fresh balances and recent activity. We also need to support multiple aggregators over time without coupling the domain model to any single integration.

We initially evaluated SimpleFin but ruled it out: its sync model is a periodic batch pull (typically once per day), which is too high-latency to support agent workflows that act on near-real-time balance and transaction information. We need a primary aggregator with on-demand, low-latency API access.

Aggregators also disagree about which date matters for a transaction:

- **Transaction date** — when the user actually made the purchase (the swipe/transfer/check moment). This is the date the user will recall and search by.
- **Posting date** — when the institution settled the transaction onto the account ledger. This is the date the institution reports against the available balance and is required to reconcile against statements.

Most providers expose both; some surface only one. The domain has to record both whenever they are available so we can show the user-friendly date in the UI while still reconciling against bank statements.

## Decision

- Model data feeds as `DataFeedConnection` records, each linked to a `DataFeedProvider` (Akoya, Plaid, MX, Manual). Akoya is the launch provider per ADR-006; SimpleFin is intentionally not in the provider set.
- Each connection tracks its own `ConnectionStatus` (Active, NeedsReauth, Disabled, Error) independently.
- Sync operations produce `SyncEvent` records for observability: when a sync ran, what it found, and what changed.
- The domain model defines the sync contract; provider-specific logic lives in infrastructure adapters behind a common interface.
- Adapters MUST populate two date fields on every imported transaction:
  - `OccurredAt` — the transaction date (when the activity happened from the user's perspective). Required.
  - `PostedAt` — the institution's posting date. Optional, set whenever the provider exposes it; left `None` for transactions that have not yet posted.
  When a provider only returns one date, the adapter records it as `OccurredAt` and leaves `PostedAt` empty until the posting date is observed on a later sync.
- `UserPreferences.PreferredSyncFrequency` expresses the user's desired update cadence. The scheduler respects this but is bounded by provider capabilities. The supported aggregators (Akoya, Plaid, MX) are all near-real-time; we deliberately exclude providers whose sync floor is daily or coarser.

## Consequences

- **Provider-agnostic**: Adding a new data source means implementing an adapter — no domain model changes.
- **Sync visibility**: `SyncEvent` gives users and operators insight into data freshness without inspecting provider logs.
- **Credential safety**: `CredentialRef` is an opaque reference (vault key, encrypted blob ID) — actual secrets never live in the domain layer.
- **Latency floor**: By excluding SimpleFin-class batch providers from the provider set, the system can guarantee a sync floor compatible with agent workflows. Adding a new provider that does not meet this latency floor is a domain-shaping decision, not just a config change.
- **Dual dates as a hard contract**: Both `OccurredAt` and `PostedAt` are first-class on the transaction. This costs two columns and a small amount of adapter complexity, and in return the UI can sort by transaction date (what users recall) while reconciliation and statement matching use posting date (what institutions report).
