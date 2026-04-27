open System
open System.IO
open System.Net.Http
open System.Net.Http.Headers
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

let requireGuid (el: JsonElement) (name: string) =
    match tryGetGuid el name with Some g -> g | None -> failwith $"Missing required field: {name}"

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

        if token <> config.StewardServiceToken then
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
                    return Ok () // Endpoint may not exist yet (STE-26)
                elif not resp.IsSuccessStatusCode then
                    return Error $"Core API returned {(int)resp.StatusCode}: {body}"
                else
                    return Ok ()
            with ex ->
                return Error $"Core API request failed: {ex.Message}"
        }

// ── Route handlers ───────────────────────────────────────────────────────────

let healthHandler : HttpHandler = fun ctx ->
    Response.ofJson {| status = "ok"; version = version; service = "akoya-ingestion" |} ctx

let syncTriggerHandler (client: IAkoyaClient) (http: HttpClient) (logger: ILogger) : HttpHandler = fun ctx ->
    task {
        let! doc = readJsonBody ctx
        let root = doc.RootElement

        let tenantId = requireGuid root "tenantId"
        let connectionId = requireGuid root "connectionId"
        let accountIdOpt = tryGetString root "accountId"

        logger.LogInformation(
            "Sync triggered for tenant={TenantId} connection={ConnectionId} account={AccountId}",
            tenantId, connectionId, (accountIdOpt |> Option.defaultValue "(all)"))

        // 1. Start sync event (optional — endpoint may not exist yet)
        let! syncEventIdOpt =
            task {
                let! result = InternalApiClient.postSyncEventStart http tenantId connectionId
                match result with
                | Ok id -> return id
                | Error msg ->
                    logger.LogWarning("Failed to record sync start: {Message}", msg)
                    return None
            }

        // 2. Fetch accounts from Akoya (stubbed — returns canned data)
        // In D5/D6 this will use real OAuth tokens from the vault.
        let! accounts = client.FetchAccountsAsync("stub-access-token")

        let accountsToSync =
            match accountIdOpt with
            | Some accountId -> accounts |> List.filter (fun a -> a.AccountId = accountId)
            | None -> accounts

        logger.LogInformation(
            "Fetched {AccountCount} accounts from Akoya for tenant={TenantId}",
            accountsToSync.Length, tenantId)

        // 3. Fetch transactions for each account and normalize
        let mutable allNormalized : NormalizedTransaction list = []

        for account in accountsToSync do
            let! txns = client.FetchTransactionsAsync("stub-access-token", account.AccountId)
            let normalized = txns |> List.map AkoyaNormalization.normalize
            allNormalized <- allNormalized @ normalized
            logger.LogInformation(
                "Fetched {TxnCount} transactions for account={AccountId}",
                txns.Length, account.AccountId)

        // 4. Upsert transactions to Core API
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
            // 5. Complete sync event (optional — endpoint may not exist yet)
            match syncEventIdOpt with
            | Some syncEventId ->
                let! completeResult =
                    InternalApiClient.patchSyncEventComplete http syncEventId "completed" upsertedCount 0
                match completeResult with
                | Ok () -> ()
                | Error msg -> logger.LogWarning("Failed to record sync completion: {Message}", msg)
            | None -> ()

            let response =
                {|
                    status = "completed"
                    accountsFetched = accountsToSync.Length
                    transactionsFetched = allNormalized.Length
                    transactionsUpserted = upsertedCount
                |}
            do! Response.ofJson response ctx
    }

// ── Application bootstrap ────────────────────────────────────────────────────

let builder = WebApplication.CreateBuilder()
builder.WebHost.UseUrls($"http://0.0.0.0:{config.Port}") |> ignore

builder.Services.AddSingleton<HttpClient>(new HttpClient()) |> ignore
builder.Services.AddSingleton<IAkoyaClient>(fun sp ->
    let http = sp.GetRequiredService<HttpClient>()
    StubAkoyaClient(config, http) :> IAkoyaClient) |> ignore

let wapp = builder.Build()

wapp.UseRouting()
    .UseFalco([
        get "/health" healthHandler
        post "/sync-trigger" (requireServiceToken (fun ctx ->
            let client = ctx.RequestServices.GetRequiredService<IAkoyaClient>()
            let http = ctx.RequestServices.GetRequiredService<HttpClient>()
            let logger = ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("AkoyaSync")
            (syncTriggerHandler client http logger) ctx))
    ])
    .Run(Response.ofPlainText "Not found")
