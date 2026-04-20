# ADR-003: Reconciliation via transaction matching

## Status

Accepted

## Context

Users can enter transactions manually before they appear in data feeds. When the feed eventually syncs, we need to match feed transactions against manual entries to avoid duplicates. Additionally, users need formal reconciliation against bank statements to verify their records are accurate.

## Decision

Two-level reconciliation:

### 1. Automatic matching (on sync)

When a data feed imports transactions, the sync service attempts to match each incoming transaction against existing manual entries on the same account using:

- Amount (exact match)
- Date (within a configurable tolerance window, default ±2 days)
- Description similarity (fuzzy, for confidence scoring)

Matched pairs are linked via `Transaction.MatchedTransactionId` (bidirectional). The manual entry is promoted to `Reconciled` status; the feed entry is discarded or merged.

### 2. Manual reconciliation (user-initiated)

The user starts a reconciliation session for an account with a statement balance and date. They mark transactions as matched against the statement. The session tracks matched/unmatched counts and completes when the computed balance equals the statement balance.

`Transaction.Status` progresses: `Pending` → `Cleared` (appeared in feed) → `Reconciled` (confirmed against statement).

## Consequences

- **No duplicates**: Manual entries are matched before feed entries are fully committed.
- **User control**: Automatic matching is a suggestion; the user can reject matches and reconcile manually.
- **Audit trail**: `MatchedTransactionId` preserves the link between manual and feed-sourced records.
- **Status progression**: The three-state model (Pending/Cleared/Reconciled) gives users clear visibility into transaction confidence.
