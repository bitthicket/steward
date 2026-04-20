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
    Domain.fs        -- Core domain types: Account, Transaction, Budget
    Program.fs       -- HTTP routes and app entry point
test/
  BitThicket.Steward.Api.Test/
    Tests.fs         -- Test suite
```

### Domain Model

**Account** — a financial account (checking, savings, credit card, investment, cash) denominated in a specific currency.

**Transaction** — a debit, credit, or transfer against an account with an amount, optional category, and timestamp.

**Budget** — a spending limit for a category over a period (monthly, weekly, or custom date range).

All monetary values use a `Money` record (decimal amount + currency code) to avoid unit confusion.

## Getting Started

```bash
dotnet restore
dotnet build
dotnet run --project src/BitThicket.Steward.Api
dotnet test
```

## Key Decisions

1. **F# + Falco over heavier frameworks** — Bias toward shipping fast. Falco is a thin wrapper over ASP.NET Core; we get full .NET ecosystem access without framework lock-in.
2. **Domain types first** — Define the core model before adding persistence. This lets us iterate on the API shape without migration churn.
3. **Algebraic types for variants** — `AccountType`, `TransactionKind`, and `BudgetPeriod` are discriminated unions. The compiler enforces exhaustive handling — no forgotten cases.
4. **Defer persistence** — Start with in-memory or simple file-based state. Pick a database when the access patterns are clearer.
