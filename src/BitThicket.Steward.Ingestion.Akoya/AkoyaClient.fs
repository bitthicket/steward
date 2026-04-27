namespace BitThicket.Steward.Ingestion.Akoya

open System
open System.Net.Http
open System.Threading.Tasks

// ─────────────────────────────────────────────────────────────────────────────
// FDX data shapes (minimal subset for accounts + transactions)
// ─────────────────────────────────────────────────────────────────────────────

type FdxAccount = {
    AccountId: string
    AccountType: string
    DisplayName: string
    Currency: string
}

type FdxTransaction = {
    TransactionId: string
    AccountId: string
    Amount: decimal
    Currency: string
    Description: string
    TransactionDate: DateTimeOffset
    PostingDate: DateTimeOffset option
    Memo: string option
}

type FdxAccountsResponse = {
    Accounts: FdxAccount list
}

type FdxTransactionsResponse = {
    Transactions: FdxTransaction list
}

// ─────────────────────────────────────────────────────────────────────────────
// Normalized transaction (domain shape sent to Core API)
// ─────────────────────────────────────────────────────────────────────────────

type NormalizedTransaction = {
    ExternalId: string
    AccountId: string
    OccurredAt: DateTimeOffset
    PostedAt: DateTimeOffset option
    AmountMinor: int64
    Currency: string
    Description: string
    Merchant: string option
}

type SyncTriggerResult = {
    AccountsFetched: int
    TransactionsFetched: int
    TransactionsUpserted: int
}

// ─────────────────────────────────────────────────────────────────────────────
// IAkoyaClient — abstracts the FDX HTTP interaction so tests can stub it.
// ─────────────────────────────────────────────────────────────────────────────

type IAkoyaClient =
    abstract FetchAccountsAsync : accessToken:string -> Task<FdxAccount list>
    abstract FetchTransactionsAsync : accessToken:string * accountId:string -> Task<FdxTransaction list>

// ─────────────────────────────────────────────────────────────────────────────
// Stubbed FDX client — returns canned data so wiring is testable end-to-end.
// Real HTTP calls and OAuth token management will be implemented in D5/D6.
// ─────────────────────────────────────────────────────────────────────────────

type StubAkoyaClient(config: AkoyaConfig, http: HttpClient) =

    let mutable requestCount = 0

    interface IAkoyaClient with
        member _.FetchAccountsAsync(accessToken) =
            task {
                requestCount <- requestCount + 1
                // Canned accounts for end-to-end wiring tests.
                return [
                    {
                        AccountId = "akoya-stub-checking-001"
                        AccountType = "CHECKING"
                        DisplayName = "Stub Checking"
                        Currency = "USD"
                    }
                    {
                        AccountId = "akoya-stub-savings-001"
                        AccountType = "SAVINGS"
                        DisplayName = "Stub Savings"
                        Currency = "USD"
                    }
                ]
            }

        member _.FetchTransactionsAsync(accessToken, accountId) =
            task {
                requestCount <- requestCount + 1
                let now = DateTimeOffset.UtcNow
                // Canned transactions for end-to-end wiring tests.
                return [
                    {
                        TransactionId = $"akoya-txn-{accountId}-001"
                        AccountId = accountId
                        Amount = -42.50m
                        Currency = "USD"
                        Description = "Stub Coffee Shop"
                        TransactionDate = now.AddDays(-1.0)
                        PostingDate = Some(now.AddDays(-1.0))
                        Memo = None
                    }
                    {
                        TransactionId = $"akoya-txn-{accountId}-002"
                        AccountId = accountId
                        Amount = -123.45m
                        Currency = "USD"
                        Description = "Stub Grocery Store"
                        TransactionDate = now.AddDays(-3.0)
                        PostingDate = Some(now.AddDays(-2.0))
                        Memo = None
                    }
                    {
                        TransactionId = $"akoya-txn-{accountId}-003"
                        AccountId = accountId
                        Amount = 1500.00m
                        Currency = "USD"
                        Description = "Stub Salary Deposit"
                        TransactionDate = now.AddDays(-5.0)
                        PostingDate = Some(now.AddDays(-5.0))
                        Memo = None
                    }
                ]
            }

// ─────────────────────────────────────────────────────────────────────────────
// Normalization helpers
// ─────────────────────────────────────────────────────────────────────────────

module AkoyaNormalization =
    let toMinorUnits (amount: decimal) (currency: string) : int64 =
        let places =
            match currency.ToUpperInvariant() with
            | "BTC" -> 8
            | "JPY" -> 0
            | _ -> 2
        let factor = pown 10m places
        int64 (amount * factor)

    let normalize (fdx: FdxTransaction) : NormalizedTransaction =
        {
            ExternalId = fdx.TransactionId
            AccountId = fdx.AccountId
            OccurredAt = fdx.TransactionDate
            PostedAt = fdx.PostingDate
            AmountMinor = toMinorUnits fdx.Amount fdx.Currency
            Currency = fdx.Currency
            Description = fdx.Description
            Merchant = fdx.Memo
        }
