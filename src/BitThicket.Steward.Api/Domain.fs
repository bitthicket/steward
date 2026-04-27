namespace BitThicket.Steward.Api.Domain

open System

// ─────────────────────────────────────────────────────────────────────────────
// Tenant
// ─────────────────────────────────────────────────────────────────────────────

type Tenant = {
    Id: Guid
    DisplayName: string
    CreatedAt: DateTimeOffset
    UpdatedAt: DateTimeOffset
}

// ─────────────────────────────────────────────────────────────────────────────
// User
// ─────────────────────────────────────────────────────────────────────────────

/// Minimal user anchor. Most state hangs off UserId; this type exists so user
/// records are not implicit and the system has a single place to extend with
/// auth, profile, and preferences metadata.
type User = {
    Id: Guid
    Email: string
    PasswordHash: string
    DisplayName: string option
    CreatedAt: DateTimeOffset
    UpdatedAt: DateTimeOffset
}

// ─────────────────────────────────────────────────────────────────────────────
// UserTenantMembership
// ─────────────────────────────────────────────────────────────────────────────

type UserTenantMembership = {
    UserId: Guid
    TenantId: Guid
    Role: string
    CreatedAt: DateTimeOffset
}

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

/// Three-component balance value (see ADR-010). Computed from the ledger;
/// not user-set. Sign convention follows AccountType (deposit-style accounts
/// are non-negative typical, credit/loan accounts are non-positive typical).
type Balance = {
    /// Sum of transactions whose PostedAt is non-None.
    Posted: Money
    /// Institution-reported available balance when present, otherwise
    /// Posted + Pending less any local holds.
    Available: Money
    /// Signed sum of transactions whose PostedAt is None.
    Pending: Money
}

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
    TenantId: Guid
    UserId: Guid
    Name: string
    AccountType: AccountType
    CurrencyCode: string
    InstitutionName: string option
    /// External identifier from data feed (e.g. Akoya account ID)
    ExternalId: string option
    CreditCardInfo: CreditCardInfo option
    /// When true the account participates in budget allocation; when false it
    /// contributes to net worth and reporting only. See ADR-009. Default
    /// values are AccountType-derived (deposit/credit/cash on, investment/loan off)
    /// but the user can override.
    IsOnBudget: bool
    IsActive: bool
    /// Soft-delete timestamp. When Some the account is logically deleted and
    /// excluded from list/get results, but transaction history remains intact.
    DeletedAt: DateTimeOffset option
    CreatedAt: DateTimeOffset
    UpdatedAt: DateTimeOffset
}

// ─────────────────────────────────────────────────────────────────────────────
// Transactions
// ─────────────────────────────────────────────────────────────────────────────

[<RequireQualifiedAccess>]
type TransactionStatus =
    /// Manual entry or in-flight transaction with no posting confirmation yet.
    | Pending
    /// Matched at low-to-medium confidence; awaiting human or agent confirmation.
    | NeedsReview
    /// Feed-confirmed, either auto-matched at high confidence or review-resolved.
    | Cleared
    /// Confirmed against a bank statement via a Reconciliation session.
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
    TenantId: Guid
    AccountId: Guid
    Amount: Money
    Description: string
    Merchant: string option
    Memo: string option
    /// Roll-up category when the transaction has no splits. When TransactionSplit
    /// records exist for this transaction, the splits are authoritative for
    /// categorization and this field is descriptive only. See ADR-008.
    CategoryId: Guid option
    Status: TransactionStatus
    Source: TransactionSource
    ExternalId: string option
    /// Links to the counterpart transaction if matched during reconciliation
    MatchedTransactionId: Guid option
    /// Confidence score in [0.0, 1.0] for the auto-match that produced
    /// MatchedTransactionId. None for unmatched or manually-linked records.
    /// See ADR-003 for thresholds and review-band semantics.
    MatchConfidence: decimal option
    /// For transfers between own accounts. ADR-001 documents the integrity
    /// invariants (matching currency, opposing signs, aligned dates).
    TransferAccountId: Guid option
    /// Back-link to the SyncEvent that produced this transaction, when feed-sourced.
    /// None for manual entries. See ADR-011.
    SyncEventId: Guid option
    /// Posting date — when the institution settled the transaction. Required for
    /// statement reconciliation; None until the institution posts.
    PostedAt: DateTimeOffset option
    /// Transaction date — when the activity happened from the user's perspective
    /// (e.g. the moment of swipe). This is the date users recall and the UI sorts by.
    OccurredAt: DateTimeOffset
    CreatedAt: DateTimeOffset
    UpdatedAt: DateTimeOffset
}

[<RequireQualifiedAccess>]
type SplitSource =
    | Manual
    /// Split derived from a parsed receipt attachment.
    | Receipt of attachmentId: Guid
    /// Split produced by an external-source enrichment (Amazon, Square, etc.).
    | Enrichment of providerKey: string

/// Line item underneath a parent Transaction. When at least one split exists,
/// the splits are authoritative for categorization and their amounts must sum
/// to the parent transaction's Amount. See ADR-008.
type TransactionSplit = {
    Id: Guid
    TenantId: Guid
    TransactionId: Guid
    Amount: Money
    CategoryId: Guid option
    Description: string option
    Memo: string option
    Source: SplitSource
    SortOrder: int
    CreatedAt: DateTimeOffset
    UpdatedAt: DateTimeOffset
}

[<RequireQualifiedAccess>]
type AttachmentKind =
    | Receipt
    | Statement
    | Other of label: string

/// Binary blob attached to a transaction (or to a specific split). The bytes
/// live in object storage; the domain only carries pointer + content hash.
/// See ADR-008.
type Attachment = {
    Id: Guid
    TenantId: Guid
    TransactionId: Guid
    SplitId: Guid option
    Kind: AttachmentKind
    StorageRef: string
    /// SHA-256 of the bytes (hex-encoded). Used for deduplication.
    ContentHash: string
    ContentType: string
    SizeBytes: int64
    UploadedAt: DateTimeOffset
    UploadedByUserId: Guid option
    UploadedByAgentId: Guid option
}

[<RequireQualifiedAccess>]
type EnrichmentStatus =
    | Pending
    | Succeeded
    | Failed of reason: string

/// Append-only record of an external-source lookup (Amazon order, Square
/// receipt, etc.) that produced structured data for a transaction.
/// See ADR-008.
type TransactionEnrichment = {
    Id: Guid
    TenantId: Guid
    TransactionId: Guid
    /// Open-string identifier for the external source (e.g. "amazon", "square").
    SourceKey: string
    Status: EnrichmentStatus
    /// Source-shaped JSON payload of the result; opaque to the domain.
    ResultPayload: string option
    ProducedSplitIds: Guid list
    ProducedAttachmentIds: Guid list
    RequestedAt: DateTimeOffset
    CompletedAt: DateTimeOffset option
    RequestedByUserId: Guid option
    RequestedByAgentId: Guid option
}

// ─────────────────────────────────────────────────────────────────────────────
// Categories
// ─────────────────────────────────────────────────────────────────────────────

type Category = {
    Id: Guid
    TenantId: Guid
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
    /// Envelope system: similar to zero-based with stricter envelope semantics
    | Envelope
    /// Flexible: allocations may be ≤ income, not required to equal
    | Flexible
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
    TenantId: Guid
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
    TenantId: Guid
    UserId: Guid
    Name: string
    Style: BudgetingStyle
    Period: BudgetPeriod
    CurrencyCode: string
    /// Total income to be allocated (zero-based invariant anchor)
    Income: Money
    IsActive: bool
    StartsOn: DateOnly
    CreatedAt: DateTimeOffset
    UpdatedAt: DateTimeOffset
}

[<RequireQualifiedAccess>]
type BudgetPeriodStatus =
    | Open
    | Closed

/// A concrete budget period instance (e.g. "April 2026").
type BudgetPeriodRecord = {
    Id: Guid
    BudgetId: Guid
    TenantId: Guid
    StartDate: DateOnly
    EndDate: DateOnly
    Status: BudgetPeriodStatus
    CreatedAt: DateTimeOffset
    UpdatedAt: DateTimeOffset
}

/// Per-category allocation within a budget period.
type BudgetPeriodCategoryAllocation = {
    BudgetPeriodId: Guid
    CategoryId: Guid
    AllocatedAmount: Money
    OpeningBalance: Money
    RolloverBalance: Money
    RolloverEnabled: bool
}

// ─────────────────────────────────────────────────────────────────────────────
// Data Feeds & Sync
// ─────────────────────────────────────────────────────────────────────────────

/// Coarse provider identity. Used for filtering, UI labels, and config lookups
/// when no connection record is in hand. The rich per-connection state lives
/// in ProviderMetadata; the tag is derivable from a metadata value via
/// DataFeedConnection.providerOf.
[<RequireQualifiedAccess>]
type DataFeedProvider =
    | Akoya
    | Plaid
    | MX
    | Yodlee
    | Intuit
    | Manual

/// Coarse classification of a provider's connect/handoff flow. Hint for the
/// UI/service layer; not a domain invariant. The detailed flow (redirect URLs,
/// widget config, refresh semantics) lives in the per-provider adapter.
[<RequireQualifiedAccess>]
type AuthFlowKind =
    /// Server-side OAuth 2.0 redirect handoff (Akoya FDX, Intuit OAuth).
    | OAuthRedirect
    /// Frontend-mediated link widget that returns a token to exchange (Plaid Link, MX Connect, Yodlee FastLink).
    | LinkWidget
    /// No external auth — local-only manual connections.
    | None

/// Per-connection non-secret state, parameterised by provider. Captures the
/// stable identifiers each aggregator uses to address a connection. Secrets
/// (access tokens, refresh tokens) never live here — they sit behind
/// DataFeedConnection.CredentialRef in the vault. See ADR-005 for the auth-flow
/// modeling rationale.
[<RequireQualifiedAccess>]
type ProviderMetadata =
    /// Akoya FDX: customerId is the Akoya-side user identifier; institutionId
    /// names the connected bank. OAuth redirect handoff.
    | Akoya of customerId: string * institutionId: string
    /// Plaid: itemId is per-institution connection; institutionId is Plaid's bank id.
    /// Link Widget handoff.
    | Plaid of itemId: string * institutionId: string * cursor: string option
    /// MX Atrium: memberGuid identifies the connection; institutionCode names the institution.
    /// Connect Widget handoff.
    | MX of memberGuid: string * institutionCode: string
    /// Yodlee: providerAccountId identifies the connection; loginFormId is the
    /// optional reference to the form used for re-auth prompts. FastLink widget handoff.
    | Yodlee of providerAccountId: string * loginFormId: string option
    /// Intuit/QuickBooks: realmId is the QuickBooks company id (required for every
    /// API call). companyName is descriptive metadata for the UI. OAuth redirect handoff.
    | Intuit of realmId: string * companyName: string option
    /// Manual: no external connection metadata.
    | Manual

[<RequireQualifiedAccess>]
type ConnectionStatus =
    | Active
    | NeedsReauth
    | Disabled
    | Error of message: string

type DataFeedConnection = {
    Id: Guid
    TenantId: Guid
    UserId: Guid
    /// Provider identity AND non-secret per-connection state, in one DU value.
    /// Use DataFeedConnection.providerOf to extract the coarse provider tag.
    Metadata: ProviderMetadata
    /// Opaque token/credentials reference (never stored in plaintext in domain).
    /// The vault behind this ref is responsible for secret rotation, refresh,
    /// and per-provider token shape.
    CredentialRef: string
    Status: ConnectionStatus
    LinkedAccountIds: Guid list
    CreatedAt: DateTimeOffset
    UpdatedAt: DateTimeOffset
}

module DataFeedConnection =
    let providerOf (metadata: ProviderMetadata) : DataFeedProvider =
        match metadata with
        | ProviderMetadata.Akoya _ -> DataFeedProvider.Akoya
        | ProviderMetadata.Plaid _ -> DataFeedProvider.Plaid
        | ProviderMetadata.MX _ -> DataFeedProvider.MX
        | ProviderMetadata.Yodlee _ -> DataFeedProvider.Yodlee
        | ProviderMetadata.Intuit _ -> DataFeedProvider.Intuit
        | ProviderMetadata.Manual -> DataFeedProvider.Manual

    let authFlowKindOf (metadata: ProviderMetadata) : AuthFlowKind =
        match metadata with
        | ProviderMetadata.Akoya _ -> AuthFlowKind.OAuthRedirect
        | ProviderMetadata.Intuit _ -> AuthFlowKind.OAuthRedirect
        | ProviderMetadata.Plaid _ -> AuthFlowKind.LinkWidget
        | ProviderMetadata.MX _ -> AuthFlowKind.LinkWidget
        | ProviderMetadata.Yodlee _ -> AuthFlowKind.LinkWidget
        | ProviderMetadata.Manual -> AuthFlowKind.None

[<RequireQualifiedAccess>]
type SyncStatus =
    | Success
    | PartialSuccess of errors: string list
    | Failed of reason: string

type SyncEvent = {
    Id: Guid
    TenantId: Guid
    ConnectionId: Guid
    StartedAt: DateTimeOffset
    CompletedAt: DateTimeOffset option
    Status: SyncStatus
    TransactionsAdded: int
    TransactionsUpdated: int
}

[<RequireQualifiedAccess>]
type FeedHealthLevel =
    | Healthy
    | Degraded
    | Failing
    | Unknown

/// Coarse, computed projection of feed connection health. See ADR-011.
type FeedHealth = {
    ConnectionId: Guid
    TenantId: Guid
    Level: FeedHealthLevel
    LastSuccessAt: DateTimeOffset option
    LastFailureAt: DateTimeOffset option
    ConsecutiveFailures: int
    OpenRemediationAttemptId: Guid option
    EvaluatedAt: DateTimeOffset
}

[<RequireQualifiedAccess>]
type RemediationOutcome =
    | Resolved
    | StillFailing of reason: string
    | NeedsHumanInput of prompt: string

/// Append-only record of an attempt (by an agent or human) to recover a broken
/// or degraded feed connection. See ADR-011.
type RemediationAttempt = {
    Id: Guid
    TenantId: Guid
    ConnectionId: Guid
    StartedAt: DateTimeOffset
    CompletedAt: DateTimeOffset option
    ActorAgentId: Guid option
    ActorUserId: Guid option
    /// Open-string strategy identifier ("refresh-token", "reauth-prompt", ...).
    Strategy: string
    Outcome: RemediationOutcome option
    Notes: string option
}

// ─────────────────────────────────────────────────────────────────────────────
// Reconciliation
// ─────────────────────────────────────────────────────────────────────────────

[<RequireQualifiedAccess>]
type ReconciliationStatus =
    | Open
    | Completed
    | Aborted

type Reconciliation = {
    Id: Guid
    TenantId: Guid
    AccountId: Guid
    StatementBalance: Money
    StatementDate: DateOnly
    Status: ReconciliationStatus
    Note: string option
    CreatedByUserId: Guid
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
/// Invariant: when PaidAt is Some, both DebitTransactionId and
/// CreditTransactionId MUST be Some. The service layer enforces this; the
/// type stays loose so the in-flight (scheduled, not yet executed) state
/// is representable.
type CreditCardPayment = {
    Id: Guid
    TenantId: Guid
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
// API Keys
// ─────────────────────────────────────────────────────────────────────────────

type ApiKey = {
    Id: Guid
    TenantId: Guid
    UserId: Guid
    DisplayName: string
    KeyHash: string
    KeyPrefix: string
    Role: string
    Scopes: string list
    ExpiresAt: DateTimeOffset option
    LastUsedAt: DateTimeOffset option
    RevokedAt: DateTimeOffset option
    CreatedAt: DateTimeOffset
}

// ─────────────────────────────────────────────────────────────────────────────
// User Preferences
// ─────────────────────────────────────────────────────────────────────────────

type UserPreferences = {
    UserId: Guid
    TenantId: Guid
    DefaultCurrencyCode: string
    DefaultBudgetingStyle: BudgetingStyle
    /// User's desired sync cadence. Bounded by the service layer to
    /// [15 minutes, 24 hours]; values outside that range are clamped.
    /// See ADR-005 for the rationale.
    PreferredSyncFrequency: TimeSpan
}
