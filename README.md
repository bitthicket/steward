# Steward

AI-first personal finance tool for budgeting and expense tracking.

## Tech Stack

| Layer | Choice | Rationale |
|-------|--------|-----------|
| Language | F# on .NET 10 | Strong type system for financial domain modeling, algebraic data types for clean state machines, good ecosystem |
| Web framework | [Falco](https://github.com/pimbrouwers/Falco) v5 | Lightweight, functional-first ASP.NET Core wrapper — fast to ship, no ceremony |
| Testing | xUnit + FsUnit + Unquote | Standard .NET test stack with F#-idiomatic assertion libraries |
| Data | TBD (SQLite for local-first, PostgreSQL for hosted) | Defer storage backend choice until we need persistence |

## Architecture

```
src/
  BitThicket.Steward.Api/
    Domain.fs        -- Core domain types
    Program.fs       -- HTTP routes and app entry point
test/
  BitThicket.Steward.Api.Test/
    Tests.fs         -- Test suite
docs/
  adr/               -- Architecture Decision Records
```

## Domain Model

### Core Concepts

**User** — Minimal identity anchor; most state hangs off `UserId`.

**Currency** — Supports fiat (USD) and crypto (BTC) with appropriate decimal precision. Accounts are single-currency; cross-currency transfers are modeled as linked transactions.

**Account** — A financial account (checking, savings, credit card, investment, loan, cash) denominated in a specific currency. Optionally linked to an external data feed. Credit card accounts carry additional metadata (limit, statement balance, due date). Each account has an `IsOnBudget` flag governing whether it participates in budget allocation or only contributes to net-worth tracking.

**Balance** — Three-component value (`Posted`, `Available`, `Pending`) computed from the ledger. Sign convention follows account type — deposit-style accounts trend non-negative, credit and loan accounts trend non-positive.

**Transaction** — A financial event on a single account with a signed amount. Tracks its source (manual, data feed, import), status (pending → needs-review → cleared → reconciled), match confidence, and optional category. Supports matching between manual entries and feed data via `MatchedTransactionId`. Carries both `OccurredAt` (transaction date — UI) and `PostedAt` (posting date — reconciliation).

**TransactionSplit** — Line items underneath a parent transaction. When present, splits are authoritative for categorization and must sum to the parent transaction's amount. Used for receipts (Costco, Amazon) and agent-driven enrichment.

**Attachment** — Receipt, statement, or other binary file attached to a transaction or split. Bytes live in object storage; the domain carries the pointer and content hash.

**TransactionEnrichment** — Append-only record of an external-source lookup (Amazon order, Square receipt, etc.) that produced splits or attachments. Provides the agent provenance trail for req 9.

**Category** — Hierarchical spending categories for transaction classification and budget allocation.

**Budget** — A named spending plan with a chosen style (zero-based or traditional limits) and period. Contains per-category allocations with optional rollover. Operates only over on-budget accounts.

**CreditCardPayment** — Models the payment flow from a funding account to a credit card, generating linked debit/credit transactions.

**DataFeedConnection** — Represents a link to an external data provider (Akoya, Plaid, MX, Yodlee, Intuit). Tracks connection health and sync history. SimpleFin was evaluated and rejected — its batch sync model is too high-latency for agent workflows (see [ADR-005](docs/adr/005-data-feed-abstraction.md)).

**FeedHealth / RemediationAttempt** — Domain handles for AI-driven feed remediation (placeholder shapes; see [ADR-011](docs/adr/011-feed-health-and-remediation.md)).

**Reconciliation** — A session for verifying account records against a bank statement.

### Design Principles

1. **Single-entry with transfer links** — One transaction per account per real-world event. Transfers link two transactions with a documented integrity invariant. Simpler UX than double-entry while maintaining integrity. (See [ADR-001](docs/adr/001-single-entry-with-transfer-links.md))

2. **Multi-currency from day one** — USD and BTC supported with extensibility for more. No mixed-currency accounts. (See [ADR-002](docs/adr/002-multi-currency-model.md))

3. **Reconciliation via matching with confidence-banded review** — Manual entries are matched against feed data automatically; low-confidence matches land in a review queue rather than silently clearing. Statement reconciliation uses posting date. (See [ADR-003](docs/adr/003-reconciliation-via-transaction-matching.md))

4. **Flexible budgeting** — User chooses their style. Rollover is per-category, not global. (See [ADR-004](docs/adr/004-flexible-budgeting-with-rollover.md))

5. **Provider-agnostic data feeds** — Domain defines the sync contract; provider adapters implement it. Sync frequency is a user preference bounded by provider capability and an explicit latency floor. (See [ADR-005](docs/adr/005-data-feed-abstraction.md))

6. **Transaction date and posting date are both first-class** — Users think and search by transaction date; institutions reconcile by posting date. The model carries both. (See [ADR-001](docs/adr/001-single-entry-with-transfer-links.md) and [ADR-005](docs/adr/005-data-feed-abstraction.md))

7. **Splits, receipts, and agent enrichment** — Composite transactions (Amazon orders, mixed receipts) split into line items; receipts attach as files; agents have a domain-level provenance trail for external-source lookups. (See [ADR-008](docs/adr/008-transaction-splits-attachments-and-enrichment.md))

8. **On-budget vs off-budget accounts** — Investment and loan accounts contribute to net worth without polluting the budget envelope. (See [ADR-009](docs/adr/009-on-budget-vs-off-budget-accounts.md))

9. **Balance shape and sign convention** — Three-component balance (Posted/Available/Pending) with a per-account-type sign convention. (See [ADR-010](docs/adr/010-account-balance-shape-and-sign-convention.md))

10. **Feed health and remediation primitives** — `FeedHealth` projection and `RemediationAttempt` records give agents a domain-level handle on broken feeds. Placeholder; full design follows. (See [ADR-011](docs/adr/011-feed-health-and-remediation.md))

## Getting Started

```bash
dotnet restore
dotnet build
dotnet run --project src/BitThicket.Steward.Api
dotnet test
```
