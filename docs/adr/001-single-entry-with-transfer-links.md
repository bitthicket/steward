# ADR-001: Single-entry ledger with transfer links

## Status

Accepted

## Context

Financial systems typically choose between single-entry bookkeeping (one record per transaction, signed amounts) and double-entry (every movement creates balanced debit/credit pairs). We need to decide which model fits a consumer personal finance tool.

Double-entry is the gold standard for business accounting — it guarantees the books always balance and provides a complete audit trail. However, it introduces significant complexity for consumers: every transaction requires two legs, transfers become four entries, and the mental model doesn't match how users think about their money.

## Decision

Use **single-entry bookkeeping** with explicit transfer links:

- Each transaction belongs to exactly one account.
- Amounts are signed: negative = money out, positive = money in.
- Transfers between own accounts are represented as two linked transactions (one per account) connected via `TransferAccountId`. The two legs MUST satisfy: same currency, opposing signs, equal absolute amount, `OccurredAt` within a small tolerance window (default ±1 day), and each leg's `TransferAccountId` references the other leg's account. Cross-currency transfers are modeled as two transfers in their respective currencies (see ADR-002). The transfer-integrity invariants are enforced in the service layer; the type system carries the link but not the equality.
- Credit card payments are a specialized transfer type with additional metadata (`CreditCardPayment` record). Once `PaidAt` is set, both `DebitTransactionId` and `CreditTransactionId` MUST be present (an unfulfilled payment cannot be marked paid). The optional shape on the type accommodates the in-flight scheduled-but-not-yet-executed state.
- Each transaction carries two timestamps: `OccurredAt` (transaction date — when the activity happened from the user's perspective, e.g. the moment of swipe or transfer) and `PostedAt` (posting date — when the institution settled it onto the account ledger). `OccurredAt` is required; `PostedAt` is optional and is filled in once the institution posts the transaction. The user-facing UI sorts and groups by `OccurredAt`; reconciliation and statement matching use `PostedAt` because that is the date institutions report against. See ADR-005 for the feed-side contract that ensures both dates are populated whenever a provider exposes them.

## Consequences

- **Simpler UX**: Users see one record per real-world event per account, matching bank statement presentation.
- **Transfer integrity**: The `TransferAccountId` link ensures we can always show both sides of a transfer without duplicating the mental model.
- **Reconciliation**: Matching is per-account, aligning with how institutions report data.
- **Dual dates carry both perspectives**: Recording transaction and posting dates separately costs two columns per transaction but eliminates the constant friction of having to choose one. Users get the date they remember; reconciliation and statement balances get the date the institution settled on.
- **Trade-off**: We cannot trivially prove that all money is accounted for across all accounts (no inherent balance equation). If we later need formal double-entry (e.g., for business bookkeeping extension), we'd add a journal layer on top rather than replace this model.
