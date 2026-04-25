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
- Date (within a configurable tolerance window, default ±2 days). Compare on `OccurredAt` (the transaction date) since that is what the user supplied for the manual entry. Posting date (`PostedAt`) is not yet known on a manual entry, so it cannot be used at this step.
- Description similarity (fuzzy, for confidence scoring)

Each candidate match produces a confidence score in `[0.0, 1.0]`, which is stored on the matched record as `Transaction.MatchConfidence: decimal option`. Two thresholds govern outcomes:

- `confidence >= autoAcceptThreshold` (default `0.9`) — auto-link as `Cleared`, no review required.
- `lowAcceptThreshold <= confidence < autoAcceptThreshold` (default `0.6`) — link the records but mark the transaction `NeedsReview`. The transaction lands in a review queue for the user (or an agent acting on the user's behalf) to confirm or reject.
- `confidence < lowAcceptThreshold` — do not auto-link. The feed transaction is created standalone and the manual entry remains untouched.

Matched pairs are linked via `Transaction.MatchedTransactionId` (bidirectional). For auto-accepted matches the manual entry is promoted to `Cleared` status; the feed entry is discarded or merged. The match operation also copies `PostedAt` from the feed transaction onto the matched record so future statement reconciliation has the institution's posting date. Statement-level reconciliation (step 2 below) is what promotes a transaction from `Cleared` to `Reconciled`.

### 2. Manual reconciliation (user-initiated)

The user starts a reconciliation session for an account with a statement balance and date. They mark transactions as matched against the statement. The session tracks matched/unmatched counts and completes when the computed balance equals the statement balance. Statement matching uses `PostedAt` (the institution's posting date) for the date filter, since that is what appears on the statement; transactions still pending (`PostedAt` is `None`) are excluded from the reconcilable set.

`Transaction.Status` is a four-state DU: `Pending` (manual or in-flight, no posting confirmation) → `NeedsReview` (matched at low-to-medium confidence; awaiting human or agent confirmation) → `Cleared` (feed-confirmed and either auto-matched at high confidence or review-resolved) → `Reconciled` (confirmed against a bank statement). `NeedsReview` is reachable from either `Pending` (low-confidence auto-match against an existing manual entry) or directly from a freshly-imported feed transaction whose match confidence falls in the review band. Resolving `NeedsReview` (accept or reject) returns the transaction to `Cleared` or `Pending` respectively.

## Consequences

- **No duplicates**: Manual entries are matched before feed entries are fully committed.
- **User control**: Automatic matching is a suggestion; the user can reject matches and reconcile manually.
- **Audit trail**: `MatchedTransactionId` preserves the link between manual and feed-sourced records, and `MatchConfidence` is stored on the transaction so the basis for the link is auditable later.
- **Low-confidence matches do not silently land**: The `NeedsReview` state and the review-band thresholds prevent fuzzy-but-not-confident matches from being filed as `Cleared` without a human or agent ever seeing them. This satisfies the "flag for human review" half of req 7.
- **Status progression**: The four-state model (Pending → NeedsReview → Cleared → Reconciled) gives users clear visibility into transaction confidence at every step.
