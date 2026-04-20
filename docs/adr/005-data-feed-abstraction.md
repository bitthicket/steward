# ADR-005: Data feed abstraction for multi-provider sync

## Status

Accepted

## Context

SimpleFin provides daily account updates, but the product goal is hourly or more frequent sync. We also need to support future providers (Plaid, direct bank APIs) without coupling the domain to any single integration.

## Decision

- Model data feeds as `DataFeedConnection` records, each linked to a `DataFeedProvider` (SimpleFin | Plaid | Manual).
- Each connection tracks its own `ConnectionStatus` (Active, NeedsReauth, Disabled, Error) independently.
- Sync operations produce `SyncEvent` records for observability: when a sync ran, what it found, and what changed.
- The domain model defines the sync contract; provider-specific logic lives in infrastructure adapters behind a common interface.
- `UserPreferences.PreferredSyncFrequency` expresses the user's desired update cadence. The scheduler respects this but is bounded by provider capabilities (SimpleFin's daily limit is a provider constraint, not a domain constraint).

## Consequences

- **Provider-agnostic**: Adding a new data source means implementing an adapter — no domain model changes.
- **Sync visibility**: `SyncEvent` gives users and operators insight into data freshness without inspecting provider logs.
- **Credential safety**: `CredentialRef` is an opaque reference (vault key, encrypted blob ID) — actual secrets never live in the domain layer.
- **Frequency decoupled from provider**: The user states intent (e.g., "sync hourly"); the system does its best given provider constraints. This future-proofs for when we add faster providers.
