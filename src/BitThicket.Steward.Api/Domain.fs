namespace BitThicket.Steward.Api.Domain

open System

// ─────────────────────────────────────────────────────────────────────────────
// Currency & Money
// ─────────────────────────────────────────────────────────────────────────────

[<RequireQualifiedAccess>]
type CurrencyType =
    | Fiat
    | Crypto

type Currency = {
    Code: string
    Name: string
    CurrencyType: CurrencyType
    /// Smallest subdivision (e.g. 2 for USD cents, 8 for BTC satoshis)
    DecimalPlaces: int
}

type Money = {
    Amount: decimal
    CurrencyCode: string
}

module Money =
    let zero code = { Amount = 0m; CurrencyCode = code }
    let usd amount = { Amount = amount; CurrencyCode = "USD" }
    let btc amount = { Amount = amount; CurrencyCode = "BTC" }

// ─────────────────────────────────────────────────────────────────────────────
// Accounts
// ─────────────────────────────────────────────────────────────────────────────

[<RequireQualifiedAccess>]
type AccountType =
    | Checking
    | Savings
    | CreditCard
    | Investment
    | Loan
    | Cash

/// Tracks statement-level metadata for credit card accounts.
type CreditCardInfo = {
    CreditLimit: Money
    StatementBalance: Money option
    MinimumPayment: Money option
    DueDate: DateOnly option
    Apr: decimal option
}

type Account = {
    Id: Guid
    UserId: Guid
    Name: string
    AccountType: AccountType
    CurrencyCode: string
    InstitutionName: string option
    /// External identifier from data feed (e.g. SimpleFin account ID)
    ExternalId: string option
    CreditCardInfo: CreditCardInfo option
    IsActive: bool
    CreatedAt: DateTimeOffset
    UpdatedAt: DateTimeOffset
}

// ─────────────────────────────────────────────────────────────────────────────
// Transactions
// ─────────────────────────────────────────────────────────────────────────────

[<RequireQualifiedAccess>]
type TransactionStatus =
    | Pending
    | Cleared
    | Reconciled

[<RequireQualifiedAccess>]
type TransactionSource =
    | Manual
    | DataFeed of provider: string
    | Import of format: string

/// When a manual entry and a feed entry represent the same real-world event,
/// we link them via MatchedTransactionId.
type Transaction = {
    Id: Guid
    AccountId: Guid
    Amount: Money
    Description: string
    Memo: string option
    CategoryId: Guid option
    Status: TransactionStatus
    Source: TransactionSource
    /// Links to the counterpart transaction if matched during reconciliation
    MatchedTransactionId: Guid option
    /// For transfers between own accounts
    TransferAccountId: Guid option
    PostedAt: DateTimeOffset option
    OccurredAt: DateTimeOffset
    CreatedAt: DateTimeOffset
    UpdatedAt: DateTimeOffset
}

// ─────────────────────────────────────────────────────────────────────────────
// Categories
// ─────────────────────────────────────────────────────────────────────────────

type Category = {
    Id: Guid
    UserId: Guid
    Name: string
    ParentCategoryId: Guid option
    IsSystem: bool
    CreatedAt: DateTimeOffset
}

// ─────────────────────────────────────────────────────────────────────────────
// Budgeting
// ─────────────────────────────────────────────────────────────────────────────

[<RequireQualifiedAccess>]
type BudgetingStyle =
    /// Every dollar is assigned a job; unspent funds stay in the category
    | ZeroBased
    /// Traditional: set limits, track spending, no envelope semantics
    | TraditionalLimits

[<RequireQualifiedAccess>]
type BudgetPeriod =
    | Monthly
    | BiWeekly
    | Weekly
    | Custom of days: int

type BudgetCategory = {
    Id: Guid
    BudgetId: Guid
    CategoryId: Guid
    AllocatedAmount: Money
    /// When true, unspent balance carries forward to the next period
    RolloverEnabled: bool
    /// Accumulated rollover from prior periods
    RolloverBalance: Money
}

type Budget = {
    Id: Guid
    UserId: Guid
    Name: string
    Style: BudgetingStyle
    Period: BudgetPeriod
    CurrencyCode: string
    IsActive: bool
    StartsOn: DateOnly
    CreatedAt: DateTimeOffset
    UpdatedAt: DateTimeOffset
}

// ─────────────────────────────────────────────────────────────────────────────
// Data Feeds & Sync
// ─────────────────────────────────────────────────────────────────────────────

[<RequireQualifiedAccess>]
type DataFeedProvider =
    | SimpleFin
    | Plaid
    | Manual

[<RequireQualifiedAccess>]
type ConnectionStatus =
    | Active
    | NeedsReauth
    | Disabled
    | Error of message: string

type DataFeedConnection = {
    Id: Guid
    UserId: Guid
    Provider: DataFeedProvider
    /// Opaque token/credentials reference (never stored in plaintext in domain)
    CredentialRef: string
    Status: ConnectionStatus
    LinkedAccountIds: Guid list
    CreatedAt: DateTimeOffset
    UpdatedAt: DateTimeOffset
}

[<RequireQualifiedAccess>]
type SyncStatus =
    | Success
    | PartialSuccess of errors: string list
    | Failed of reason: string

type SyncEvent = {
    Id: Guid
    ConnectionId: Guid
    StartedAt: DateTimeOffset
    CompletedAt: DateTimeOffset option
    Status: SyncStatus
    TransactionsAdded: int
    TransactionsUpdated: int
}

// ─────────────────────────────────────────────────────────────────────────────
// Reconciliation
// ─────────────────────────────────────────────────────────────────────────────

[<RequireQualifiedAccess>]
type ReconciliationStatus =
    | InProgress
    | Completed
    | Abandoned

type Reconciliation = {
    Id: Guid
    AccountId: Guid
    StatementBalance: Money
    StatementDate: DateOnly
    Status: ReconciliationStatus
    MatchedCount: int
    UnmatchedCount: int
    StartedAt: DateTimeOffset
    CompletedAt: DateTimeOffset option
}

// ─────────────────────────────────────────────────────────────────────────────
// Credit Card Payments
// ─────────────────────────────────────────────────────────────────────────────

[<RequireQualifiedAccess>]
type PaymentType =
    | StatementBalance
    | MinimumPayment
    | CustomAmount
    | FullBalance

/// Tracks a payment from a funding account to a credit card.
/// Generates two transactions: a debit on the funding account and
/// a credit on the credit card account.
type CreditCardPayment = {
    Id: Guid
    CreditCardAccountId: Guid
    FundingAccountId: Guid
    Amount: Money
    PaymentType: PaymentType
    ScheduledDate: DateOnly option
    PaidAt: DateTimeOffset option
    DebitTransactionId: Guid option
    CreditTransactionId: Guid option
    CreatedAt: DateTimeOffset
}

// ─────────────────────────────────────────────────────────────────────────────
// User Preferences
// ─────────────────────────────────────────────────────────────────────────────

type UserPreferences = {
    UserId: Guid
    DefaultCurrencyCode: string
    DefaultBudgetingStyle: BudgetingStyle
    PreferredSyncFrequency: TimeSpan
}
