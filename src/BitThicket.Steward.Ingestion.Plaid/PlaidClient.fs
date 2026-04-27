namespace BitThicket.Steward.Ingestion.Plaid

open System
open System.Net.Http
open System.Threading.Tasks

// ─────────────────────────────────────────────────────────────────────────────
// Plaid data shapes (minimal subset for accounts + transactions)
// ─────────────────────────────────────────────────────────────────────────────

type PlaidAccount = {
    AccountId: string
    Name: string
    Type: string
    Subtype: string option
    Mask: string option
}

type PlaidTransaction = {
    TransactionId: string
    AccountId: string
    Amount: decimal
    Currency: string
    Name: string
    MerchantName: string option
    Date: DateTimeOffset
    AuthorizedDate: DateTimeOffset option
}

type PlaidAccountsResponse = {
    Accounts: PlaidAccount list
}

type PlaidTransactionsResponse = {
    Transactions: PlaidTransaction list
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

// ─────────────────────────────────────────────────────────────────────────────
// IPlaidClient — abstracts the Plaid HTTP interaction so tests can stub it.
// ─────────────────────────────────────────────────────────────────────────────

type IPlaidClient =
    abstract FetchAccountsAsync : accessToken:string -> Task<PlaidAccount list>
    abstract FetchTransactionsAsync : accessToken:string * accountId:string -> Task<PlaidTransaction list>

// ─────────────────────────────────────────────────────────────────────────────
// Stubbed Plaid client — returns canned data so wiring is testable end-to-end.
// Real HTTP calls will be implemented in D2/D3.
// ─────────────────────────────────────────────────────────────────────────────

type StubPlaidClient(config: PlaidConfig, http: HttpClient) =

    let mutable requestCount = 0

    interface IPlaidClient with
        member _.FetchAccountsAsync(accessToken) =
            task {
                requestCount <- requestCount + 1
                // Canned accounts for end-to-end wiring tests.
                return [
                    {
                        AccountId = "plaid-stub-checking-001"
                        Name = "Stub Checking"
                        Type = "depository"
                        Subtype = Some "checking"
                        Mask = Some "1234"
                    }
                    {
                        AccountId = "plaid-stub-savings-001"
                        Name = "Stub Savings"
                        Type = "depository"
                        Subtype = Some "savings"
                        Mask = Some "5678"
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
                        TransactionId = $"plaid-txn-{accountId}-001"
                        AccountId = accountId
                        Amount = 12.34m
                        Currency = "USD"
                        Name = "Stub Coffee Shop"
                        MerchantName = Some "Stub Coffee Roasters"
                        Date = now.AddDays(-1.0)
                        AuthorizedDate = Some(now.AddDays(-1.0))
                    }
                    {
                        TransactionId = $"plaid-txn-{accountId}-002"
                        AccountId = accountId
                        Amount = 87.65m
                        Currency = "USD"
                        Name = "Stub Grocery Store"
                        MerchantName = Some "Stub Groceries"
                        Date = now.AddDays(-3.0)
                        AuthorizedDate = Some(now.AddDays(-3.0))
                    }
                    {
                        TransactionId = $"plaid-txn-{accountId}-003"
                        AccountId = accountId
                        Amount = 2500.00m
                        Currency = "USD"
                        Name = "Stub Payroll"
                        MerchantName = Some "Stub Employer"
                        Date = now.AddDays(-5.0)
                        AuthorizedDate = Some(now.AddDays(-5.0))
                    }
                ]
            }

// ─────────────────────────────────────────────────────────────────────────────
// Normalization helpers
// ─────────────────────────────────────────────────────────────────────────────

module PlaidNormalization =
    let toMinorUnits (amount: decimal) (currency: string) : int64 =
        let places =
            match currency.ToUpperInvariant() with
            | "BTC" -> 8
            | "JPY" -> 0
            | _ -> 2
        let factor = pown 10m places
        int64 (amount * factor)

    let normalize (plaid: PlaidTransaction) : NormalizedTransaction =
        {
            ExternalId = plaid.TransactionId
            AccountId = plaid.AccountId
            OccurredAt = plaid.Date
            PostedAt = plaid.AuthorizedDate
            AmountMinor = toMinorUnits plaid.Amount plaid.Currency
            Currency = plaid.Currency
            Description = plaid.Name
            Merchant = plaid.MerchantName
        }
