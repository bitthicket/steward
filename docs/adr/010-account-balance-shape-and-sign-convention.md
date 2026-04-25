# ADR-010: Account balance shape and sign convention

## Status

Accepted

## Context

Two pieces of "financial reality" (req 12) are not yet expressed in the model:

1. **Balance is not a single number.** Real accounts expose at least three distinct balances at any moment:
   - **Posted balance** — what the institution has settled. Reconciliation uses this.
   - **Available balance** — what the user can spend right now (posted balance ± holds and pending activity, varies by account type).
   - **Pending net** — the signed total of transactions in flight that have not yet posted.
   Treating "balance" as a single decimal forces every consumer to guess which one is meant and produces subtle UI bugs (e.g. showing the available balance to a reconciliation flow).

2. **Sign convention varies by account type.** A checking account's balance is a positive number representing money the user holds. A credit card account's balance is conventionally the amount owed — many users (and most institutions' UIs) display it as a positive "you owe X" number, but inside our ledger it has to be negative so that signed transaction amounts compose correctly: a $100 charge on the card debits the credit card account, the resulting balance is `-100`, paying it off credits the card by `+100` and zeroes the balance. The convention has to be consistent or balance arithmetic breaks.

## Decision

### Balance value type

Introduce a `Balance` record with three signed `Money` components. Compose it onto `Account` as a value derived from the ledger; it is not user-set.

```fsharp
type Balance = {
    Posted: Money
    Available: Money
    Pending: Money
}
```

Semantics:
- `Posted` is the sum of all transactions on the account whose `PostedAt` is non-`None`, expressed in the account's currency. This is what reconciliation matches against.
- `Available` is the institution-reported available balance when present, else `Posted + Pending` minus any local holds. The service layer is the source of truth here; the domain just carries the value.
- `Pending` is the signed sum of transactions with `PostedAt = None`.

`Balance` is computed, not stored as ground truth on `Account`. Implementations may cache it, but the canonical source is the transaction ledger plus institution-reported supplements.

### Sign convention per `AccountType`

The sign of an account's balance follows the standard ledger convention:

| AccountType  | Balance sign         | Interpretation                                   |
|--------------|----------------------|--------------------------------------------------|
| `Checking`   | non-negative typical | money the user holds                             |
| `Savings`    | non-negative typical | money the user holds                             |
| `Cash`       | non-negative typical | money the user holds                             |
| `Investment` | non-negative typical | money/positions the user holds                   |
| `CreditCard` | non-positive typical | amount owed (more negative = more debt)          |
| `Loan`       | non-positive typical | principal owed (more negative = more debt)       |

"Typical" because nothing prevents an overdrawn checking account from carrying a negative posted balance, or a credit-card account from briefly going positive after an over-payment. The convention is a default and a UI hint, not a hard invariant.

The sign convention is enforced by transaction direction:
- A purchase on a credit card creates a transaction with negative `Amount` on the credit-card account.
- A payment to a credit card creates a positive `Amount` on the credit-card account (and the matching debit on the funding account, per ADR-001).

UIs are free to invert the display sign for credit and loan accounts ("you owe $1,234.56" rather than "−$1,234.56"). The inversion happens at the presentation layer; the domain stays consistent.

### Holds and APR accrual (deferred)

Holds (a temporary reduction in available balance for an authorised but unposted transaction) are deferred — they will surface as a special pending transaction with a `Hold` source tag if and when we model them. APR accrual on credit cards and loans is also deferred; `CreditCardInfo.Apr` is descriptive metadata until we add an interest-accrual service.

## Consequences

- **Three balances, one type**: Consumers see a single `Balance` value with explicit fields and cannot accidentally pull the wrong one.
- **Sign convention is uniform**: Reporting, transfers, and credit-card payment logic all operate on a single arithmetic convention. UIs can flip the display sign per account type without affecting the math.
- **Computed, not stored**: We avoid the classic "stale balance" bug class by deriving balance from the ledger. Caching is allowed but explicitly secondary.
- **Trade-off — convention vs invariant**: Sign rules are defaults, not enforced. A bug elsewhere could land an account on the "wrong" side of zero. We accept this rather than ban legitimate edge cases (overdraft, over-payment).
- **Trade-off — holds and accrual deferred**: We acknowledge these are part of "financial reality" but do not yet model them. Calling that out explicitly here so we do not pretend they are out of scope.

## Related Decisions

- [ADR-001](001-single-entry-with-transfer-links.md) — signed transaction amounts; this ADR completes the picture by saying what those signed amounts roll up into.
- [ADR-009](009-on-budget-vs-off-budget-accounts.md) — `IsOnBudget` flag; balance shape applies to both on- and off-budget accounts.
- [ADR-011](011-feed-health-and-remediation.md) — balance freshness depends on feed health.
