namespace BitThicket.Steward.Ingestion.Akoya

open System
open System.Collections.Concurrent
open System.Net.Http
open System.Net.Http.Headers
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Microsoft.Extensions.Logging

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
// Token cache
// ─────────────────────────────────────────────────────────────────────────────

type private CachedToken = {
    AccessToken: string
    RefreshToken: string option
    ExpiresAt: DateTimeOffset option
}

module private TokenCache =
    let private cache = ConcurrentDictionary<string, CachedToken>()

    let get (key: string) =
        match cache.TryGetValue(key) with
        | true, token -> Some token
        | false, _ -> None

    let set (key: string) (token: CachedToken) =
        cache[key] <- token

    let remove (key: string) =
        cache.TryRemove(key) |> ignore

// ─────────────────────────────────────────────────────────────────────────────
// Real Akoya FDX HTTP client
// ─────────────────────────────────────────────────────────────────────────────

type AkoyaClient(config: AkoyaConfig, http: HttpClient, logger: ILogger) =

    let fdxBaseUrl = AkoyaConfig.fdxBaseUrl config
    let tokenEndpoint = AkoyaConfig.tokenEndpoint config

    let jsonOptions =
        let opts = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)
        opts

    let parseAccounts (json: string) : FdxAccount list =
        try
            use doc = JsonDocument.Parse(json)
            let root = doc.RootElement
            match root.TryGetProperty("accounts") with
            | true, arr ->
                arr.EnumerateArray()
                |> Seq.map (fun el ->
                    {
                        AccountId = el.GetProperty("accountId").GetString()
                        AccountType =
                            match el.TryGetProperty("accountType") with
                            | true, p -> p.GetString()
                            | _ -> "UNKNOWN"
                        DisplayName =
                            match el.TryGetProperty("displayName") with
                            | true, p -> p.GetString()
                            | _ -> ""
                        Currency =
                            match el.TryGetProperty("currency") with
                            | true, p -> p.GetString()
                            | _ -> "USD"
                    })
                |> Seq.toList
            | _ ->
                // Try top-level array
                root.EnumerateArray()
                |> Seq.map (fun el ->
                    {
                        AccountId = el.GetProperty("accountId").GetString()
                        AccountType =
                            match el.TryGetProperty("accountType") with
                            | true, p -> p.GetString()
                            | _ -> "UNKNOWN"
                        DisplayName =
                            match el.TryGetProperty("displayName") with
                            | true, p -> p.GetString()
                            | _ -> ""
                        Currency =
                            match el.TryGetProperty("currency") with
                            | true, p -> p.GetString()
                            | _ -> "USD"
                    })
                |> Seq.toList
        with ex ->
            logger.LogError(ex, "Failed to parse Akoya accounts response")
            []

    let parseTransactions (json: string) : FdxTransaction list =
        try
            use doc = JsonDocument.Parse(json)
            let root = doc.RootElement
            let txns =
                match root.TryGetProperty("transactions") with
                | true, arr -> arr
                | _ -> root

            txns.EnumerateArray()
            |> Seq.map (fun el ->
                let amount =
                    match el.TryGetProperty("amount") with
                    | true, p -> p.GetDecimal()
                    | _ ->
                        match el.TryGetProperty("totalAmount") with
                        | true, p -> p.GetDecimal()
                        | _ -> 0m

                let currency =
                    match el.TryGetProperty("currency") with
                    | true, p -> p.GetString()
                    | _ -> "USD"

                let description =
                    match el.TryGetProperty("description") with
                    | true, p -> p.GetString()
                    | _ ->
                        match el.TryGetProperty("memo") with
                        | true, p -> p.GetString()
                        | _ -> ""

                let transactionDate =
                    match el.TryGetProperty("transactionDate") with
                    | true, p -> DateTimeOffset.Parse(p.GetString())
                    | _ -> DateTimeOffset.UtcNow

                let postingDate =
                    match el.TryGetProperty("postingDate") with
                    | true, p -> Some(DateTimeOffset.Parse(p.GetString()))
                    | _ -> None

                let memo =
                    match el.TryGetProperty("memo") with
                    | true, p when p.ValueKind <> JsonValueKind.Null -> Some(p.GetString())
                    | _ -> None

                {
                    TransactionId = el.GetProperty("transactionId").GetString()
                    AccountId =
                        match el.TryGetProperty("accountId") with
                        | true, p -> p.GetString()
                        | _ -> ""
                    Amount = amount
                    Currency = currency
                    Description = description
                    TransactionDate = transactionDate
                    PostingDate = postingDate
                    Memo = memo
                })
            |> Seq.toList
        with ex ->
            logger.LogError(ex, "Failed to parse Akoya transactions response")
            []

    let fetchWithAuth (accessToken: string) (url: string) =
        task {
            let req = new HttpRequestMessage(HttpMethod.Get, url)
            req.Headers.Authorization <- AuthenticationHeaderValue("Bearer", accessToken)
            req.Headers.Accept.Add(MediaTypeWithQualityHeaderValue("application/json"))
            let! resp = http.SendAsync(req)
            let! body = resp.Content.ReadAsStringAsync()
            return resp.StatusCode, body
        }

    interface IAkoyaClient with
        member _.FetchAccountsAsync(accessToken) =
            task {
                let url = $"{fdxBaseUrl}/fdx/v6/accounts"
                let! statusCode, body = fetchWithAuth accessToken url
                if statusCode = System.Net.HttpStatusCode.Unauthorized then
                    logger.LogWarning("Akoya accounts request returned 401; token may be expired")
                    return []
                elif int statusCode >= 400 then
                    logger.LogError("Akoya accounts request failed: {StatusCode} {Body}", int statusCode, body)
                    return []
                else
                    return parseAccounts body
            }

        member _.FetchTransactionsAsync(accessToken, accountId) =
            task {
                let url = $"{fdxBaseUrl}/fdx/v6/accounts/{Uri.EscapeDataString(accountId)}/transactions"
                let! statusCode, body = fetchWithAuth accessToken url
                if statusCode = System.Net.HttpStatusCode.Unauthorized then
                    logger.LogWarning("Akoya transactions request returned 401 for account {AccountId}; token may be expired", accountId)
                    return []
                elif int statusCode >= 400 then
                    logger.LogError("Akoya transactions request failed for account {AccountId}: {StatusCode} {Body}", accountId, int statusCode, body)
                    return []
                else
                    return parseTransactions body
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
