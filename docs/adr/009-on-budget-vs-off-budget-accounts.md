# ADR-009: On-budget vs off-budget accounts

## Status

Accepted

## Context

Requirement 13 asks that accounts be classifiable as on-budget or off-budget. On-budget accounts (checking, credit card, cash) participate in budget allocation: their inflows fund categories and their outflows draw against allocations. Off-budget accounts (long-term investments, mortgages, an HSA the user does not actively manage day-to-day) contribute to net worth and overall planning but are not visible to budget calculations — their activity should not be assigned to spending categories.

Without an explicit flag, every account is implicitly on-budget. That forces investment activity through the budget envelope (clearly wrong) or pushes users to model investments as non-accounts (loses net-worth tracking). We need a first-class distinction.

## Decision

Add a single boolean flag to `Account`:

```fsharp
type Account = {
    // ...existing fields...
    /// When true, this account participates in budget allocation. When false,
    /// it contributes to net worth and reporting only.
    IsOnBudget: bool
    // ...
}
```

Default values per `AccountType`:

| AccountType  | Default `IsOnBudget` |
|--------------|----------------------|
| `Checking`   | `true`               |
| `Savings`    | `true`               |
| `CreditCard` | `true`               |
| `Cash`       | `true`               |
| `Investment` | `false`              |
| `Loan`       | `false`              |

Defaults are advisory at account creation — the user can flip them. A user who treats one savings account as their long-term emergency fund (and does not want it cluttering monthly budgeting) can set it off-budget; a user who actively manages an investment account's contributions through the budget can pull it in.

### Budget interaction

- Budget allocation, category roll-up, and ZBB "every-dollar-assigned" calculations only consider transactions on accounts with `IsOnBudget = true`.
- Net-worth reporting uses all active accounts regardless of `IsOnBudget`.
- Transfers between an on-budget and an off-budget account are valid; on the off-budget side, the transaction does not consume any category allocation; on the on-budget side it does (or, configurably, can be marked as a transfer-out so the budget sees the movement as out-of-scope).
- Reconciliation and statement matching are unaffected by this flag — both on- and off-budget accounts can be reconciled.

## Consequences

- **Net worth without budget noise**: Investment and loan activity stay out of the user's daily budget views but still count for net worth and long-term planning.
- **Sensible defaults**: Most users will not need to touch the flag; defaults match common consumer mental models (checking and credit card are budgeted, investments and loans are not).
- **Transfer semantics need care**: The on-budget / off-budget transfer case is the most error-prone. The service layer is responsible for correctly tagging the on-budget leg as a transfer-out so it does not double-count as spending.
- **Trade-off — single flag, no tiers**: We could imagine a richer model (e.g. semi-on-budget for "track but don't enforce ZBB"), but we are deliberately starting with a binary flag. If the boolean proves insufficient, this can grow into a small DU later without breaking stored data (boolean → DU migration is straightforward).

## Related Decisions

- [ADR-004](004-flexible-budgeting-with-rollover.md) — budgeting style and category rollover; only operates on on-budget accounts.
- [ADR-010](010-account-balance-shape-and-sign-convention.md) — balance representation; applies to both on- and off-budget accounts.
