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
    abstract FetchAccountsAsync : customerId:string * institutionId:string * accessToken:string -> Task<FdxAccount list>
    abstract FetchTransactionsAsync : customerId:string * institutionId:string * accessToken:string * accountId:string -> Task<FdxTransaction list>

// ─────────────────────────────────────────────────────────────────────────────
// Stubbed FDX client — returns canned data so wiring is testable end-to-end.
// ─────────────────────────────────────────────────────────────────────────────

type StubAkoyaClient(config: AkoyaConfig, http: HttpClient) =

    let mutable requestCount = 0

    interface IAkoyaClient with
        member _.FetchAccountsAsync(customerId, institutionId, accessToken) =
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

        member _.FetchTransactionsAsync(customerId, institutionId, accessToken, accountId) =
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
// Real FDX HTTP client — calls Akoya FDX API for accounts and transactions.
// ─────────────────────────────────────────────────────────────────────────────

type AkoyaFdxHttpClient(config: AkoyaConfig, http: HttpClient) =

    let baseUrl = AkoyaConfig.fdxBaseUrl config

    let parseAccount (el: System.Text.Json.JsonElement) : FdxAccount =
        {
            AccountId =
                match el.TryGetProperty("accountId") with
                | true, p -> p.GetString()
                | _ ->
                    match el.TryGetProperty("account_id") with
                    | true, p -> p.GetString()
                    | _ -> ""
            AccountType =
                match el.TryGetProperty("accountType") with
                | true, p -> p.GetString()
                | _ ->
                    match el.TryGetProperty("account_type") with
                    | true, p -> p.GetString()
                    | _ -> "UNKNOWN"
            DisplayName =
                match el.TryGetProperty("displayName") with
                | true, p -> p.GetString()
                | _ ->
                    match el.TryGetProperty("nickname") with
                    | true, p -> p.GetString()
                    | _ -> ""
            Currency =
                match el.TryGetProperty("currency") with
                | true, p -> p.GetString()
                | _ ->
                    match el.TryGetProperty("currencyCode") with
                    | true, p -> p.GetString()
                    | _ -> "USD"
        }

    let parseTransaction (el: System.Text.Json.JsonElement) : FdxTransaction =
        let tryGetDateTime (el: System.Text.Json.JsonElement) (name: string) =
            match el.TryGetProperty(name) with
            | true, p when p.ValueKind <> System.Text.Json.JsonValueKind.Null ->
                match DateTimeOffset.TryParse(p.GetString()) with
                | true, d -> Some d
                | _ -> None
            | _ -> None

        {
            TransactionId =
                match el.TryGetProperty("transactionId") with
                | true, p -> p.GetString()
                | _ ->
                    match el.TryGetProperty("transaction_id") with
                    | true, p -> p.GetString()
                    | _ -> ""
            AccountId =
                match el.TryGetProperty("accountId") with
                | true, p -> p.GetString()
                | _ ->
                    match el.TryGetProperty("account_id") with
                    | true, p -> p.GetString()
                    | _ -> ""
            Amount =
                match el.TryGetProperty("amount") with
                | true, p -> p.GetDecimal()
                | _ -> 0m
            Currency =
                match el.TryGetProperty("currency") with
                | true, p -> p.GetString()
                | _ ->
                    match el.TryGetProperty("currencyCode") with
                    | true, p -> p.GetString()
                    | _ -> "USD"
            Description =
                match el.TryGetProperty("description") with
                | true, p -> p.GetString()
                | _ ->
                    match el.TryGetProperty("memo") with
                    | true, p -> p.GetString()
                    | _ -> ""
            TransactionDate =
                tryGetDateTime el "transactionDate"
                |> Option.defaultValue (tryGetDateTime el "transaction_date" |> Option.defaultValue DateTimeOffset.UtcNow)
            PostingDate =
                tryGetDateTime el "postingDate"
                |> Option.orElse (tryGetDateTime el "posting_date")
            Memo =
                match el.TryGetProperty("memo") with
                | true, p when p.ValueKind <> System.Text.Json.JsonValueKind.Null -> Some(p.GetString())
                | _ -> None
        }

    let parseAccountsResponse (doc: System.Text.Json.JsonDocument) : FdxAccount list =
        let root = doc.RootElement
        // Try object-wrapped array first, then bare array
        match root.ValueKind with
        | System.Text.Json.JsonValueKind.Array ->
            root.EnumerateArray() |> Seq.map parseAccount |> Seq.toList
        | _ ->
            match root.TryGetProperty("accounts") with
            | true, arr -> arr.EnumerateArray() |> Seq.map parseAccount |> Seq.toList
            | _ -> []

    let parseTransactionsResponse (doc: System.Text.Json.JsonDocument) : FdxTransaction list =
        let root = doc.RootElement
        match root.ValueKind with
        | System.Text.Json.JsonValueKind.Array ->
            root.EnumerateArray() |> Seq.map parseTransaction |> Seq.toList
        | _ ->
            match root.TryGetProperty("transactions") with
            | true, arr -> arr.EnumerateArray() |> Seq.map parseTransaction |> Seq.toList
            | _ -> []

    interface IAkoyaClient with
        member _.FetchAccountsAsync(customerId: string, institutionId: string, accessToken: string) =
            task {
                let url = $"{baseUrl}/fdx/v1/accounts"
                let req = new HttpRequestMessage(HttpMethod.Get, url)
                req.Headers.Authorization <- Headers.AuthenticationHeaderValue("Bearer", accessToken)
                req.Headers.Add("x-akoya-institution-id", institutionId)
                let! resp = http.SendAsync(req)
                let! body = resp.Content.ReadAsStringAsync()
                if not resp.IsSuccessStatusCode then
                    failwith $"Akoya FDX accounts error {(int)resp.StatusCode}: {body}"
                use doc = System.Text.Json.JsonDocument.Parse(body)
                return parseAccountsResponse doc
            }

        member _.FetchTransactionsAsync(customerId: string, institutionId: string, accessToken: string, accountId: string) =
            task {
                let url = $"{baseUrl}/fdx/v1/accounts/{accountId}/transactions"
                let req = new HttpRequestMessage(HttpMethod.Get, url)
                req.Headers.Authorization <- Headers.AuthenticationHeaderValue("Bearer", accessToken)
                req.Headers.Add("x-akoya-institution-id", institutionId)
                let! resp = http.SendAsync(req)
                let! body = resp.Content.ReadAsStringAsync()
                if not resp.IsSuccessStatusCode then
                    failwith $"Akoya FDX transactions error {(int)resp.StatusCode}: {body}"
                use doc = System.Text.Json.JsonDocument.Parse(body)
                return parseTransactionsResponse doc
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
