# ADR-004: Flexible budgeting with per-category rollover

## Status

Accepted

## Context

The reference point is zero-based budgeting (ZBB), where every dollar gets assigned a job. However, the system must not be so rigid that it prevents common user workflows like rolling over unspent funds. Different users have different mental models for budgeting — some want strict envelopes, others just want soft spending limits.

## Decision

- Support two `BudgetingStyle` variants: `ZeroBased` and `TraditionalLimits`.
- `ZeroBased`: Every dollar of income must be allocated. Categories are envelopes. Unspent funds can optionally roll over.
- `TraditionalLimits`: Set spending caps per category. No requirement to allocate all income. Simpler mental model.
- Rollover is a **per-category setting** (`BudgetCategory.RolloverEnabled`), not a global toggle. This lets users have some categories that reset monthly (e.g., dining out) and others that accumulate (e.g., vacation fund).
- `RolloverBalance` on each `BudgetCategory` tracks the accumulated unspent amount from prior periods.

## Consequences

- **User choice**: The budgeting style is user-selected, not system-imposed. Users can switch styles (though this resets rollover balances).
- **Granular rollover**: Per-category control avoids the all-or-nothing rollover limitation of many budgeting tools.
- **Complexity trade-off**: Period transitions must calculate rollover per category. This is bounded computation (number of categories is small per user) and runs once per period.
- **Extensible**: Additional styles (e.g., pay-yourself-first, 50/30/20) can be added as new DU cases without breaking existing budgets.
