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

**Currency** — Supports fiat (USD) and crypto (BTC) with appropriate decimal precision. Accounts are single-currency; cross-currency transfers are modeled as linked transactions.

**Account** — A financial account (checking, savings, credit card, investment, loan, cash) denominated in a specific currency. Optionally linked to an external data feed. Credit card accounts carry additional metadata (limit, statement balance, due date).

**Transaction** — A financial event on a single account with a signed amount. Tracks its source (manual, data feed, import), status (pending → cleared → reconciled), and optional category. Supports matching between manual entries and feed data via `MatchedTransactionId`.

**Category** — Hierarchical spending categories for transaction classification and budget allocation.

**Budget** — A named spending plan with a chosen style (zero-based or traditional limits) and period. Contains per-category allocations with optional rollover.

**CreditCardPayment** — Models the payment flow from a funding account to a credit card, generating linked debit/credit transactions.

**DataFeedConnection** — Represents a link to an external data provider (SimpleFin, Plaid). Tracks connection health and sync history.

**Reconciliation** — A session for verifying account records against a bank statement.

### Design Principles

1. **Single-entry with transfer links** — One transaction per account per real-world event. Transfers link two transactions. Simpler UX than double-entry while maintaining integrity. (See [ADR-001](docs/adr/001-single-entry-with-transfer-links.md))

2. **Multi-currency from day one** — USD and BTC supported with extensibility for more. No mixed-currency accounts. (See [ADR-002](docs/adr/002-multi-currency-model.md))

3. **Reconciliation via matching** — Manual entries are matched against feed data automatically, with manual reconciliation for statement verification. (See [ADR-003](docs/adr/003-reconciliation-via-transaction-matching.md))

4. **Flexible budgeting** — User chooses their style. Rollover is per-category, not global. (See [ADR-004](docs/adr/004-flexible-budgeting-with-rollover.md))

5. **Provider-agnostic data feeds** — Domain defines the sync contract; provider adapters implement it. Sync frequency is a user preference bounded by provider capability. (See [ADR-005](docs/adr/005-data-feed-abstraction.md))

## Getting Started

```bash
dotnet restore
dotnet build
dotnet run --project src/BitThicket.Steward.Api
dotnet test
```
