open System
open System.IO
open System.Net.Http
open System.Net.Http.Headers
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Falco
open Falco.Routing
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open BitThicket.Steward.Ingestion.Akoya

// ── Configuration ────────────────────────────────────────────────────────────

let config = AkoyaConfig.fromEnvironment()

let version =
    let v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version
    if isNull v then "0.0.0" else v.ToString()

// ── JSON helpers ─────────────────────────────────────────────────────────────

let readJsonBody (ctx: Microsoft.AspNetCore.Http.HttpContext) =
    task {
        use reader = new StreamReader(ctx.Request.Body, Encoding.UTF8)
        let! json = reader.ReadToEndAsync()
        return JsonDocument.Parse(json)
    }

let tryGetString (el: JsonElement) (name: string) =
    match el.TryGetProperty(name) with
    | true, p when p.ValueKind <> JsonValueKind.Null -> Some(p.GetString())
    | _ -> None

let tryGetGuid (el: JsonElement) (name: string) =
    match tryGetString el name with
    | Some s -> match Guid.TryParse(s) with true, g -> Some g | _ -> None
    | None -> None

let tryGetDateTime (el: JsonElement) (name: string) =
    match el.TryGetProperty(name) with
    | true, p when p.ValueKind <> JsonValueKind.Null ->
        match DateTimeOffset.TryParse(p.GetString()) with true, d -> Some d | _ -> None
    | _ -> None

let requireGuid (el: JsonElement) (name: string) =
    match tryGetGuid el name with Some g -> g | None -> failwith $"Missing required field: {name}"

let requireString (el: JsonElement) (name: string) =
    match tryGetString el name with Some s -> s | None -> failwith $"Missing required field: {name}"

// ── Service-auth wrapper ─────────────────────────────────────────────────────

let requireServiceToken (next: HttpHandler) : HttpHandler = fun ctx ->
    task {
        let header =
            match ctx.Request.Headers.TryGetValue("Authorization") with
            | true, v when v.Count > 0 -> v.ToString()
            | _ -> ""

        let token =
            if header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) then
                header.Substring(7)
            else
                ""

        let expectedTokenBytes = System.Text.Encoding.UTF8.GetBytes(config.StewardServiceToken)
        let actualTokenBytes = System.Text.Encoding.UTF8.GetBytes(token)
        if expectedTokenBytes.Length <> actualTokenBytes.Length || not (CryptographicOperations.FixedTimeEquals(expectedTokenBytes, actualTokenBytes)) then
            ctx.Response.StatusCode <- 401
            do! Response.ofJson {| error = "Unauthorized" |} ctx
        else
            do! next ctx
    }

// ── Core API internal client ─────────────────────────────────────────────────

module InternalApiClient =
    let private buildRequest (method: string) (path: string) (body: string option) =
        let url = $"{config.StewardApiBaseUrl.TrimEnd('/')}{path}"
        let req =
            match method.ToUpperInvariant() with
            | "POST" -> new HttpRequestMessage(HttpMethod.Post, url)
            | "PATCH" -> new HttpRequestMessage(HttpMethod.Patch, url)
            | _ -> new HttpRequestMessage(HttpMethod.Get, url)

        req.Headers.Authorization <- AuthenticationHeaderValue("Bearer", config.StewardServiceToken)

        match body with
        | Some b ->
            req.Content <- new StringContent(b, Encoding.UTF8, "application/json")
        | None -> ()

        req

    /// Fetch connection credentials (provider metadata + vault envelope) from Core API.
    let getConnectionCredentials (http: HttpClient) (connectionId: Guid) =
        task {
            try
                let req = buildRequest "GET" $"/internal/connections/{connectionId}/credentials" None
                let! resp = http.SendAsync(req)
                let! body = resp.Content.ReadAsStringAsync()
                if resp.StatusCode = System.Net.HttpStatusCode.NotFound then
                    return Error $"Connection not found: {connectionId}"
                elif not resp.IsSuccessStatusCode then
                    return Error $"Core API returned {(int)resp.StatusCode}: {body}"
                else
                    use doc = JsonDocument.Parse(body)
                    let root = doc.RootElement
                    let provider =
                        match root.TryGetProperty("provider") with
                        | true, p -> p.GetString()
                        | _ -> ""
                    let providerMetadata =
                        match root.TryGetProperty("providerMetadata") with
                        | true, p -> p.GetRawText()
                        | _ -> "{}"
                    let vaultEnvelope =
                        match root.TryGetProperty("vaultEnvelope") with
                        | true, p -> p.GetRawText()
                        | _ -> "{}"
                    return Ok (provider, providerMetadata, vaultEnvelope)
            with ex ->
                return Error $"Core API request failed: {ex.Message}"
        }

    let getConnection (http: HttpClient) (connectionId: Guid) =
        task {
            try
                let req = buildRequest "GET" $"/internal/connections/{connectionId}" None
                let! resp = http.SendAsync(req)
                let! body = resp.Content.ReadAsStringAsync()
                if resp.StatusCode = System.Net.HttpStatusCode.NotFound then
                    return None
                elif not resp.IsSuccessStatusCode then
                    return None
                else
                    use doc = JsonDocument.Parse(body)
                    let root = doc.RootElement
                    // Parse lastSyncedAt for watermark
                    let lastSyncedAt = tryGetDateTime root "lastSyncedAt"
                    let linkedAccountIds =
                        match root.TryGetProperty("linkedAccountIds") with
                        | true, arr ->
                            arr.EnumerateArray()
                            |> Seq.choose (fun el ->
                                match el.ValueKind with
                                | JsonValueKind.String ->
                                    match Guid.TryParse(el.GetString()) with true, g -> Some g | _ -> None
                                | _ -> None)
                            |> Seq.toList
                        | _ -> []
                    let linkedAccounts =
                        linkedAccountIds
                        |> List.map (fun id -> {| localAccountId = id; externalAccountId = None |})
                    return Some {| linkedAccounts = linkedAccounts; lastSyncedAt = lastSyncedAt |}
            with ex ->
                return None
        }

    let postTransactionsUpsert (http: HttpClient) (tenantId: Guid) (syncEventId: Guid option) (txns: NormalizedTransaction list) =
        task {
            try
                let txnNodes =
                    txns
                    |> List.map (fun t ->
                        let o = System.Text.Json.Nodes.JsonObject()
                        o["externalId"] <- System.Text.Json.Nodes.JsonValue.Create(t.ExternalId)
                        o["accountId"] <- System.Text.Json.Nodes.JsonValue.Create(t.AccountId)
                        o["occurredAt"] <- System.Text.Json.Nodes.JsonValue.Create(t.OccurredAt.ToString("O"))
                        match t.PostedAt with
                        | Some d -> o["postedAt"] <- System.Text.Json.Nodes.JsonValue.Create(d.ToString("O"))
                        | None -> ()
                        o["amountMinor"] <- System.Text.Json.Nodes.JsonValue.Create(t.AmountMinor)
                        o["currency"] <- System.Text.Json.Nodes.JsonValue.Create(t.Currency)
                        o["description"] <- System.Text.Json.Nodes.JsonValue.Create(t.Description)
                        match t.Merchant with
                        | Some m -> o["merchant"] <- System.Text.Json.Nodes.JsonValue.Create(m)
                        | None -> ()
                        o :> System.Text.Json.Nodes.JsonNode)
                    |> List.toArray

                let root = System.Text.Json.Nodes.JsonObject()
                root["tenantId"] <- System.Text.Json.Nodes.JsonValue.Create(tenantId.ToString())
                match syncEventId with
                | Some id -> root["syncEventId"] <- System.Text.Json.Nodes.JsonValue.Create(id.ToString())
                | None -> ()
                let arr = System.Text.Json.Nodes.JsonArray(txnNodes)
                root["transactions"] <- arr

                let req = buildRequest "POST" "/internal/transactions/upsert" (Some(root.ToJsonString()))
                let! resp = http.SendAsync(req)
                let! body = resp.Content.ReadAsStringAsync()
                if not resp.IsSuccessStatusCode then
                    return Error $"Core API returned {(int)resp.StatusCode}: {body}"
                else
                    return Ok body
            with ex ->
                return Error $"Core API request failed: {ex.Message}"
        }

    let postSyncEventStart (http: HttpClient) (tenantId: Guid) (connectionId: Guid) =
        task {
            try
                let root = System.Text.Json.Nodes.JsonObject()
                root["tenantId"] <- System.Text.Json.Nodes.JsonValue.Create(tenantId.ToString())
                root["connectionId"] <- System.Text.Json.Nodes.JsonValue.Create(connectionId.ToString())
                root["status"] <- System.Text.Json.Nodes.JsonValue.Create("started")

                let req = buildRequest "POST" "/internal/sync-events" (Some(root.ToJsonString()))
                let! resp = http.SendAsync(req)
                let! body = resp.Content.ReadAsStringAsync()
                if resp.StatusCode = System.Net.HttpStatusCode.NotFound then
                    return Ok None // Endpoint may not exist yet (STE-26)
                elif not resp.IsSuccessStatusCode then
                    return Error $"Core API returned {(int)resp.StatusCode}: {body}"
                else
                    try
                        use doc = JsonDocument.Parse(body)
                        match doc.RootElement.TryGetProperty("id") with
                        | true, p -> return Ok(Some(Guid.Parse(p.GetString())))
                        | _ -> return Ok None
                    with _ ->
                        return Ok None
            with ex ->
                return Error $"Core API request failed: {ex.Message}"
        }

    let patchSyncEventComplete (http: HttpClient) (syncEventId: Guid) (status: string) (added: int) (updated: int) =
        task {
            try
                let root = System.Text.Json.Nodes.JsonObject()
                root["status"] <- System.Text.Json.Nodes.JsonValue.Create(status)
                root["transactionsAdded"] <- System.Text.Json.Nodes.JsonValue.Create(added)
                root["transactionsUpdated"] <- System.Text.Json.Nodes.JsonValue.Create(updated)

                let req = buildRequest "PATCH" $"/internal/sync-events/{syncEventId}" (Some(root.ToJsonString()))
                let! resp = http.SendAsync(req)
                let! body = resp.Content.ReadAsStringAsync()
                if resp.StatusCode = System.Net.HttpStatusCode.NotFound then
                    return Ok ()
                elif not resp.IsSuccessStatusCode then
                    return Error $"Core API returned {(int)resp.StatusCode}: {body}"
                else
                    return Ok ()
            with ex ->
                return Error $"Core API request failed: {ex.Message}"
        }

    let postConnectionStatus (http: HttpClient) (connectionId: Guid) (status: string) (message: string option) =
        task {
            try
                let root = System.Text.Json.Nodes.JsonObject()
                root["id"] <- System.Text.Json.Nodes.JsonValue.Create(connectionId.ToString())
                root["status"] <- System.Text.Json.Nodes.JsonValue.Create(status)
                match message with
                | Some m -> root["message"] <- System.Text.Json.Nodes.JsonValue.Create(m)
                | None -> ()

                let req = buildRequest "POST" "/internal/connections/status" (Some(root.ToJsonString()))
                let! resp = http.SendAsync(req)
                let! body = resp.Content.ReadAsStringAsync()
                if not resp.IsSuccessStatusCode then
                    return Error $"Core API returned {(int)resp.StatusCode}: {body}"
                else
                    return Ok ()
            with ex ->
                return Error $"Core API request failed: {ex.Message}"
        }

// ── Route handlers ───────────────────────────────────────────────────────────

let healthHandler : HttpHandler = fun ctx ->
    Response.ofJson {| status = "ok"; version = version; service = "akoya-ingestion" |} ctx

type LinkedAccountMapping = {
    LocalAccountId: Guid
    ExternalAccountId: string option
}

let syncTriggerHandler (clientFactory: HttpClient -> IAkoyaClient) (http: HttpClient) (logger: ILogger) : HttpHandler = fun ctx ->
    task {
        let! doc = readJsonBody ctx
        let root = doc.RootElement

        let tenantId = requireGuid root "tenantId"
        let connectionId = requireGuid root "connectionId"

        logger.LogInformation(
            "Sync triggered for tenant={TenantId} connection={ConnectionId}",
            tenantId, connectionId)

        // 1. Fetch credentials from Core API (like Plaid ingestion does)
        let! credsResult = InternalApiClient.getConnectionCredentials http connectionId

        match credsResult with
        | Error msg ->
            logger.LogError("Failed to fetch connection credentials: {Message}", msg)
            ctx.Response.StatusCode <- 502
            do! Response.ofJson {| error = "Failed to fetch credentials"; detail = msg |} ctx
        | Ok (provider, providerMetadataJson, vaultEnvelopeJson) ->
            if provider <> "akoya" then
                ctx.Response.StatusCode <- 400
                do! Response.ofJson {| error = $"Invalid provider: expected 'akoya' but got '{provider}'" |} ctx
            else
                // Parse provider metadata to get customerId + institutionId
                let parseMetadataResult =
                    try
                        use metadataDoc = JsonDocument.Parse(providerMetadataJson)
                        let mdRoot = metadataDoc.RootElement
                        let customerId =
                            match mdRoot.TryGetProperty("customerId") with true, p -> p.GetString() | _ -> ""
                        let institutionId =
                            match mdRoot.TryGetProperty("institutionId") with true, p -> p.GetString() | _ -> ""
                        if String.IsNullOrWhiteSpace(customerId) || String.IsNullOrWhiteSpace(institutionId) then
                            Error "customerId or institutionId missing from Akoya provider metadata"
                        else
                            Ok (customerId, institutionId)
                    with ex -> Error $"Failed to parse provider metadata: {ex.Message}"

                // Parse vault envelope to extract access token
                let accessTokenResult =
                    try
                        use envDoc = JsonDocument.Parse(vaultEnvelopeJson)
                        match envDoc.RootElement.TryGetProperty("accessToken") with
                        | true, p ->
                            let token = p.GetString()
                            if String.IsNullOrWhiteSpace(token) then
                                Error "accessToken is empty in vault envelope"
                            else
                                Ok token
                        | _ -> Error "accessToken missing from vault envelope"
                    with ex -> Error $"Failed to parse vault envelope: {ex.Message}"

                match parseMetadataResult, accessTokenResult with
                | Error msg, _ ->
                    logger.LogError("Metadata parse failed for connection={ConnectionId}: {Message}", connectionId, msg)
                    ctx.Response.StatusCode <- 502
                    do! Response.ofJson {| error = "Invalid metadata"; detail = msg |} ctx
                | _, Error msg ->
                    logger.LogError("Credential parse failed for connection={ConnectionId}: {Message}", connectionId, msg)
                    ctx.Response.StatusCode <- 502
                    do! Response.ofJson {| error = "Invalid credentials"; detail = msg |} ctx
                | Ok (customerId, institutionId), Ok accessToken ->

                    // 2. Fetch connection details for watermark / linked accounts
                    let! connDetailsOpt = InternalApiClient.getConnection http connectionId

                    let linkedAccounts, lastSyncedAtWatermark =
                        match connDetailsOpt with
                        | Some conn ->
                            let mapped =
                                conn.linkedAccounts
                                |> List.map (fun a ->
                                    { LocalAccountId = a.localAccountId; ExternalAccountId = a.externalAccountId })
                            mapped, conn.lastSyncedAt
                        | None -> [], None

                    // Build mapping from external account ID -> local account ID
                    let externalToLocalMap =
                        linkedAccounts
                        |> List.choose (fun a ->
                            match a.ExternalAccountId with
                            | Some ext -> Some(ext, a.LocalAccountId)
                            | None -> None)
                        |> Map.ofList

                    // 3. Start sync event (optional)
                    let! syncEventIdOpt =
                        task {
                            let! result = InternalApiClient.postSyncEventStart http tenantId connectionId
                            match result with
                            | Ok id -> return id
                            | Error msg ->
                                logger.LogWarning("Failed to record sync start: {Message}", msg)
                                return None
                        }

                    let client = clientFactory http

                    let mutable akoyaError = false
                    let mutable akoyaErrorMessage = ""

                    // 4. Fetch accounts from Akoya FDX
                    let! accounts =
                        task {
                            try
                                return! client.FetchAccountsAsync(customerId, institutionId, accessToken)
                            with ex ->
                                logger.LogError(ex, "Failed to fetch accounts from Akoya for connection={ConnectionId}", connectionId)
                                akoyaError <- true
                                akoyaErrorMessage <- ex.Message
                                return []
                        }

                    if akoyaError then
                        let status =
                            if akoyaErrorMessage.Contains("401") then "needsreauth"
                            else "error"
                        let! _ = InternalApiClient.postConnectionStatus http connectionId status (Some akoyaErrorMessage)
                        ctx.Response.StatusCode <- 502
                        do! Response.ofJson {| error = "Akoya FDX request failed"; detail = akoyaErrorMessage |} ctx
                    else

                        // Filter to only accounts that are linked in Steward
                        let mappedAccounts =
                            accounts
                            |> List.choose (fun a ->
                                match Map.tryFind a.AccountId externalToLocalMap with
                                | Some localId -> Some(a, localId)
                                | None ->
                                    logger.LogWarning("Skipping unlinked Akoya account {AccountId}", a.AccountId)
                                    None)

                        logger.LogInformation(
                            "Fetched {FetchedCount} accounts from Akoya; {MappedCount} mapped to local accounts for tenant={TenantId}",
                            accounts.Length, mappedAccounts.Length, tenantId)

                        // 5. Fetch transactions for each mapped account and normalize
                        let mutable allNormalized : NormalizedTransaction list = []
                        let mutable fetchErrors = ResizeArray<string>()

                        for (account, localAccountId) in mappedAccounts do
                            let! txns =
                                task {
                                    try
                                        // Use watermark for startDate (if available) to avoid fetching
                                        // transactions we've already seen. If never synced, fetch all.
                                        let startDateOpt =
                                            lastSyncedAtWatermark
                                            |> Option.map (fun d -> d.AddDays(-1.0))
                                        return! client.FetchTransactionsAsync(customerId, institutionId, accessToken, account.AccountId, startDateOpt)
                                    with ex ->
                                        logger.LogError(ex, "Failed to fetch transactions for account={AccountId}", account.AccountId)
                                        fetchErrors.Add($"Account {account.AccountId}: {ex.Message}")
                                        return []
                                }

                            let normalized =
                                txns
                                |> List.map (fun t ->
                                    { AkoyaNormalization.normalize t with
                                        AccountId = localAccountId.ToString() })

                            allNormalized <- allNormalized @ normalized
                            logger.LogInformation(
                                "Fetched {TxnCount} transactions for account={AccountId} (local={LocalAccountId})",
                                txns.Length, account.AccountId, localAccountId)

                        // 6. Upsert transactions to Core API
                        if allNormalized.IsEmpty && fetchErrors.Count > 0 then
                            let status = if fetchErrors |> Seq.exists (fun e -> e.Contains("401")) then "needsreauth" else "error"
                            let detail = String.Join("; ", fetchErrors)
                            let! _ = InternalApiClient.postConnectionStatus http connectionId status (Some detail)
                            ctx.Response.StatusCode <- 502
                            do! Response.ofJson {| error = "Failed to fetch transactions from Akoya"; detail = detail |} ctx
                        else
                            let! upsertResult = InternalApiClient.postTransactionsUpsert http tenantId syncEventIdOpt allNormalized

                            let mutable upsertedCount = 0
                            let mutable upsertFailed = false

                            match upsertResult with
                            | Ok json ->
                                try
                                    use doc = JsonDocument.Parse(json)
                                    let created =
                                        match doc.RootElement.TryGetProperty("created") with
                                        | true, p -> p.GetInt32()
                                        | _ -> 0
                                    let updated =
                                        match doc.RootElement.TryGetProperty("updated") with
                                        | true, p -> p.GetInt32()
                                        | _ -> 0
                                    upsertedCount <- created + updated
                                with _ ->
                                    upsertedCount <- allNormalized.Length
                            | Error msg ->
                                logger.LogError("Failed to upsert transactions: {Message}", msg)
                                upsertFailed <- true
                                ctx.Response.StatusCode <- 502
                                do! Response.ofJson {| error = "Core API upsert failed"; detail = msg |} ctx

                            if not upsertFailed then
                                // 7. Complete sync event
                                match syncEventIdOpt with
                                | Some syncEventId ->
                                    let statusStr = if fetchErrors.Count > 0 then "partial" else "completed"
                                    let! completeResult =
                                        InternalApiClient.patchSyncEventComplete http syncEventId statusStr upsertedCount 0
                                    match completeResult with
                                    | Ok () -> ()
                                    | Error msg -> logger.LogWarning("Failed to record sync completion: {Message}", msg)
                                | None -> ()

                                // Update connection status if there were fetch errors
                                if fetchErrors.Count > 0 then
                                    let detail = String.Join("; ", fetchErrors)
                                    let! _ = InternalApiClient.postConnectionStatus http connectionId "error" (Some detail)
                                    ()

                                let response =
                                    {|
                                        status = if fetchErrors.Count > 0 then "partial" else "completed"
                                        accountsFetched = mappedAccounts.Length
                                        transactionsFetched = allNormalized.Length
                                        transactionsUpserted = upsertedCount
                                        errors = if fetchErrors.Count > 0 then Some(fetchErrors |> Seq.toList) else None
                                    |}
                                do! Response.ofJson response ctx
    }

// ── Application bootstrap ────────────────────────────────────────────────────

let builder = WebApplication.CreateBuilder()
builder.WebHost.UseUrls($"http://0.0.0.0:{config.Port}") |> ignore

builder.Services.AddSingleton<HttpClient>(new HttpClient()) |> ignore
builder.Services.AddSingleton<IAkoyaClient>(fun sp ->
    let http = sp.GetRequiredService<HttpClient>()
    AkoyaFdxHttpClient(config, http) :> IAkoyaClient) |> ignore

let wapp = builder.Build()

wapp.UseRouting()
    .UseFalco([
        get "/health" healthHandler
        post "/sync-trigger" (requireServiceToken (fun ctx ->
            let http = ctx.RequestServices.GetRequiredService<HttpClient>()
            let client = ctx.RequestServices.GetRequiredService<IAkoyaClient>()
            let logger = ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("AkoyaSync")
            (syncTriggerHandler (fun _ -> client) http logger) ctx))
    ])
    .Run(Response.ofPlainText "Not found")
