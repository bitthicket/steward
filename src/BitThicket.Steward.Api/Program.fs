open System
open System.IO
open System.Net.Http
open System.Reflection
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Falco
open Falco.Routing
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.FileProviders
open Microsoft.Extensions.Logging
open Npgsql
open BitThicket.Steward.Api
open BitThicket.Steward.Api.Domain
open BitThicket.Steward.Api.Vault
open BitThicket.Steward.Pricing
open Serilog

// ── Serilog setup with secret redaction ──────────────────────────────────────
let loggerConfig =
    LoggerConfiguration()
        .Destructure.With<SecretMaskingPolicy>()
        .WriteTo.Console()
        .CreateLogger()

// Run DbUp before the web host starts. A failure here throws and the process
// exits non-zero so Northflank surfaces a failed deploy rather than booting an
// API against an unmigrated database.
// Migration connection string may use an admin role (can bypass RLS);
// runtime connection string uses tenant_app (cannot bypass RLS).
let migrationConnectionString = Migrations.getMigrationConnectionString ()
Migrations.apply migrationConnectionString

let connectionString = Migrations.getConnectionString ()

let port =
    match Environment.GetEnvironmentVariable("PORT") with
    | null | "" -> "8080"
    | v -> v

let jwtSecret =
    match Environment.GetEnvironmentVariable("STEWARD_JWT_SECRET") with
    | null | "" ->
        raise (InvalidOperationException(
            "STEWARD_JWT_SECRET is not set. The Steward API requires a JWT secret at startup."))
    | v -> v

let jwtSecretPrevious =
    match Environment.GetEnvironmentVariable("STEWARD_JWT_SECRET_PREVIOUS") with
    | null | "" -> None
    | v -> Some v

let jwtIssuer =
    match Environment.GetEnvironmentVariable("STEWARD_JWT_ISSUER") with
    | null | "" -> "steward"
    | v -> v

let jwtAudience =
    match Environment.GetEnvironmentVariable("STEWARD_JWT_AUDIENCE") with
    | null | "" -> "steward-api"
    | v -> v

let akoyaIngestionUrl =
    match Environment.GetEnvironmentVariable("STEWARD_AKOYA_INGESTION_URL") with
    | null | "" -> None
    | v -> Some v

let serviceToken =
    match Environment.GetEnvironmentVariable("STEWARD_SERVICE_TOKEN") with
    | null | "" -> None
    | v -> Some v

let version =
    let v = Assembly.GetExecutingAssembly().GetName().Version
    if isNull v then "0.0.0" else v.ToString()

let builder = WebApplication.CreateBuilder()
builder.Logging.ClearProviders() |> ignore
builder.Logging.AddSerilog(loggerConfig) |> ignore
builder.WebHost.UseUrls($"http://0.0.0.0:{port}") |> ignore

// ── JSON options ─────────────────────────────────────────────────────────────
builder.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(fun (opts: Microsoft.AspNetCore.Http.Json.JsonOptions) ->
    opts.SerializerOptions.PropertyNamingPolicy <- JsonNamingPolicy.CamelCase
    opts.SerializerOptions.Converters.Add(MoneyConverter())) |> ignore
builder.Services.Configure<Microsoft.AspNetCore.Mvc.JsonOptions>(fun (opts: Microsoft.AspNetCore.Mvc.JsonOptions) ->
    opts.JsonSerializerOptions.PropertyNamingPolicy <- JsonNamingPolicy.CamelCase
    opts.JsonSerializerOptions.Converters.Add(MoneyConverter())) |> ignore

// ── Services ──────────────────────────────────────────────────────────────────

let dataSource = NpgsqlDataSource.Create(connectionString)
builder.Services.AddSingleton<NpgsqlDataSource>(dataSource) |> ignore
TenantContextServices.register builder.Services |> ignore
AuthServices.register builder.Services { JwtSecret = jwtSecret; JwtSecretPrevious = jwtSecretPrevious; Issuer = jwtIssuer; Audience = jwtAudience } |> ignore
builder.Services.AddSingleton<IDbConnectionFactory>(DbConnectionFactory(dataSource)) |> ignore
builder.Services.AddScoped<ITransactionRepository>(fun sp ->
    let factory = sp.GetRequiredService<IDbConnectionFactory>()
    let accessor = sp.GetRequiredService<ITenantContextAccessor>()
    TransactionRepository.create factory accessor) |> ignore
builder.Services.AddScoped<ITransactionMatcher>(fun sp ->
    let repo = sp.GetRequiredService<ITransactionRepository>()
    TransactionMatcher.create repo) |> ignore
builder.Services.AddScoped<IReconciliationRepository>(fun sp ->
    let factory = sp.GetRequiredService<IDbConnectionFactory>()
    let accessor = sp.GetRequiredService<ITenantContextAccessor>()
    ReconciliationRepository.create factory accessor) |> ignore
builder.Services.AddScoped<IBudgetRepository>(fun sp ->
    let factory = sp.GetRequiredService<IDbConnectionFactory>()
    let accessor = sp.GetRequiredService<ITenantContextAccessor>()
    BudgetRepository.create factory accessor) |> ignore
builder.Services.AddScoped<IBudgetPeriodRepository>(fun sp ->
    let factory = sp.GetRequiredService<IDbConnectionFactory>()
    let accessor = sp.GetRequiredService<ITenantContextAccessor>()
    BudgetPeriodRepository.create factory accessor) |> ignore
builder.Services.AddScoped<ICategoryRepository>(fun sp ->
    let factory = sp.GetRequiredService<IDbConnectionFactory>()
    let accessor = sp.GetRequiredService<ITenantContextAccessor>()
    CategoryRepository.create factory accessor) |> ignore
builder.Services.AddSingleton<IApiKeyRepository>(fun sp ->
    let factory = sp.GetRequiredService<IDbConnectionFactory>()
    let accessor = sp.GetRequiredService<ITenantContextAccessor>()
    ApiKeyRepository.create factory accessor) |> ignore
builder.Services.AddSingleton<IAccountRepository>(fun sp ->
    let factory = sp.GetRequiredService<IDbConnectionFactory>()
    let accessor = sp.GetRequiredService<ITenantContextAccessor>()
    AccountRepository.create factory accessor) |> ignore
let sharedHttpClient = new System.Net.Http.HttpClient()
builder.Services.AddSingleton<HttpClient>(sharedHttpClient) |> ignore
builder.Services.AddSingleton<IPriceProvider>(fun sp ->
    let db = sp.GetRequiredService<NpgsqlDataSource>()
    let log = sp.GetRequiredService<ILogger<CoinGeckoPriceProvider>>()
    CoinGeckoPriceProvider(sharedHttpClient, db, log) :> IPriceProvider) |> ignore
builder.Services.AddSingleton<IVaultService>(VaultService(DbConnectionFactory(dataSource)) :> IVaultService) |> ignore
builder.Services.AddScoped<IDataFeedConnectionRepository>(fun sp ->
    let factory = sp.GetRequiredService<IDbConnectionFactory>()
    let accessor = sp.GetRequiredService<ITenantContextAccessor>()
    DataFeedConnectionRepository.create factory accessor) |> ignore
builder.Services.AddScoped<ISyncEventRepository>(fun sp ->
    let factory = sp.GetRequiredService<IDbConnectionFactory>()
    let accessor = sp.GetRequiredService<ITenantContextAccessor>()
    SyncEventRepository.create factory accessor) |> ignore
builder.Services.AddScoped<IFeedHealthRepository>(fun sp ->
    let factory = sp.GetRequiredService<IDbConnectionFactory>()
    let accessor = sp.GetRequiredService<ITenantContextAccessor>()
    FeedHealthRepository.create factory accessor) |> ignore
builder.Services.AddScoped<IRemediationAttemptRepository>(fun sp ->
    let factory = sp.GetRequiredService<IDbConnectionFactory>()
    let accessor = sp.GetRequiredService<ITenantContextAccessor>()
    RemediationAttemptRepository.create factory accessor) |> ignore
builder.Services.AddScoped<IUserPreferencesRepository>(fun sp ->
    let factory = sp.GetRequiredService<IDbConnectionFactory>()
    let accessor = sp.GetRequiredService<ITenantContextAccessor>()
    UserPreferencesRepository.create factory accessor) |> ignore
builder.Services.AddSingleton<IPlaidService>(fun sp ->
    let config = PlaidConfig.fromEnvironment()
    let http = sp.GetRequiredService<HttpClient>()
    let factory = sp.GetRequiredService<IDbConnectionFactory>()
    let vault = sp.GetRequiredService<IVaultService>()
    let log = sp.GetRequiredService<ILogger<PlaidService>>()
    PlaidService(config, http, factory, vault, log) :> IPlaidService) |> ignore
builder.Services.AddSingleton<IAkoyaOAuthService>(fun sp ->
    let config = AkoyaOAuthConfig.fromEnvironment()
    let http = sp.GetRequiredService<HttpClient>()
    let factory = sp.GetRequiredService<IDbConnectionFactory>()
    let vault = sp.GetRequiredService<IVaultService>()
    let log = sp.GetRequiredService<ILogger<AkoyaOAuthService>>()
    AkoyaOAuthService(config, http, factory, vault, log) :> IAkoyaOAuthService) |> ignore
builder.Services.AddHostedService<PricingWorker>() |> ignore
builder.Services.AddHostedService<FeedHealthWorker>() |> ignore
builder.Services.AddHostedService<AkoyaTokenRefreshWorker>() |> ignore

let wapp = builder.Build()

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

let tryGetInt64 (el: JsonElement) (name: string) =
    match el.TryGetProperty(name) with
    | true, p when p.ValueKind = JsonValueKind.Number -> Some(p.GetInt64())
    | _ -> None

let requireGuid (el: JsonElement) (name: string) =
    match tryGetGuid el name with Some g -> g | None -> failwith $"Missing required field: {name}"

let requireString (el: JsonElement) (name: string) =
    match tryGetString el name with Some s -> s | None -> failwith $"Missing required field: {name}"

let requireDateTime (el: JsonElement) (name: string) =
    match tryGetDateTime el name with Some d -> d | None -> failwith $"Missing required field: {name}"

let requireInt64 (el: JsonElement) (name: string) =
    match tryGetInt64 el name with Some i -> i | None -> failwith $"Missing required field: {name}"

// ── Helpers ──────────────────────────────────────────────────────────────────

let makeManualAccessor (tenantId: Guid) : ITenantContextAccessor =
    { new ITenantContextAccessor with
        member _.Context = Some { TenantId = tenantId; UserId = Guid.Empty } }

// ── Route handlers ─────────────────────────────────────────────────────────────

// GET /api/prices?base=BTC&quote=USD[&asOf=<ISO8601>]
// Returns spot price when asOf is omitted; historical price for a specific date otherwise.
let pricesHandler : HttpHandler = fun ctx ->
    task {
        let pricing = ctx.RequestServices.GetRequiredService<IPriceProvider>()
        let q = ctx.Request.Query

        let baseCurr =
            match q.TryGetValue("base") with
            | true, v when v.Count > 0 -> v.ToString().ToUpperInvariant()
            | _ -> "BTC"

        let quoteCurr =
            match q.TryGetValue("quote") with
            | true, v when v.Count > 0 -> v.ToString().ToUpperInvariant()
            | _ -> "USD"

        let asOfOpt =
            match q.TryGetValue("asOf") with
            | true, v when v.Count > 0 && not (String.IsNullOrWhiteSpace(v.ToString())) ->
                match DateTimeOffset.TryParse(v.ToString()) with
                | true, dt -> Some dt
                | _ -> None
            | _ -> None

        match asOfOpt with
        | Some asOf ->
            let! result = pricing.GetHistoricalAsync(baseCurr, quoteCurr, asOf)
            match result with
            | Some price -> do! Response.ofJson price ctx
            | None ->
                ctx.Response.StatusCode <- 404
                let dateStr = asOf.ToString("yyyy-MM-dd")
                do! Response.ofJson {| error = $"No price found for {baseCurr}/{quoteCurr} on {dateStr}" |} ctx
        | None ->
            let! price = pricing.GetSpotAsync(baseCurr, quoteCurr)
            do! Response.ofJson price ctx
    }

// GET /api/transactions/needs-review
let needsReviewHandler : HttpHandler = fun ctx ->
    task {
        let repo = ctx.RequestServices.GetRequiredService<ITransactionRepository>()
        let! txns = repo.ListNeedsReviewAsync()
        do! Response.ofJson txns ctx
    }

// POST /api/transactions/resolve
let resolveHandler : HttpHandler = fun ctx ->
    task {
        let repo = ctx.RequestServices.GetRequiredService<ITransactionRepository>()
        let! doc = readJsonBody ctx
        let root = doc.RootElement

        let action = requireString root "action"
        let txnId = requireGuid root "id"
        let manualTxnIdOpt = tryGetGuid root "manualTxnId"

        let! txnOpt = repo.GetAsync(txnId)
        match txnOpt with
        | None ->
            ctx.Response.StatusCode <- 404
            do! Response.ofJson {| error = "Transaction not found" |} ctx
        | Some txn when txn.Status <> TransactionStatus.NeedsReview ->
            ctx.Response.StatusCode <- 400
            do! Response.ofJson {| error = "Transaction is not in NeedsReview status" |} ctx
        | Some txn ->
            match action with
            | "accept" ->
                let manualIdResult =
                    match manualTxnIdOpt with
                    | Some m -> Ok m
                    | None ->
                        match txn.MatchedTransactionId with
                        | Some m -> Ok m
                        | None -> Error "No manual transaction to accept"

                match manualIdResult with
                | Error msg ->
                    ctx.Response.StatusCode <- 400
                    do! Response.ofJson {| error = msg |} ctx
                | Ok manualId ->
                    let! manualOpt = repo.GetAsync(manualId)
                    match manualOpt with
                    | None ->
                        ctx.Response.StatusCode <- 404
                        do! Response.ofJson {| error = "Manual transaction not found" |} ctx
                    | Some manual ->
                        let now = DateTimeOffset.UtcNow
                        let updatedFeed =
                            { txn with
                                Status = TransactionStatus.Cleared
                                MatchedTransactionId = Some manualId
                                UpdatedAt = now }
                        let updatedManual =
                            { manual with
                                Status = TransactionStatus.Cleared
                                MatchedTransactionId = Some txn.Id
                                UpdatedAt = now }
                        do! repo.UpdateAsync(updatedFeed)
                        do! repo.UpdateAsync(updatedManual)
                        do! Response.ofJson {| status = "resolved"; action = "accept"; manualTxnId = manualId |} ctx
            | "reject" ->
                let updated =
                    { txn with
                        Status = TransactionStatus.Cleared
                        MatchedTransactionId = None
                        MatchConfidence = None
                        UpdatedAt = DateTimeOffset.UtcNow }
                do! repo.UpdateAsync(updated)
                do! Response.ofJson {| status = "resolved"; action = "reject" |} ctx
            | _ ->
                ctx.Response.StatusCode <- 400
                do! Response.ofJson {| error = "Invalid action; expected 'accept' or 'reject'" |} ctx
    }

// POST /internal/transactions/upsert
let internalUpsertHandler : HttpHandler = fun ctx ->
    task {
        let factory = ctx.RequestServices.GetRequiredService<IDbConnectionFactory>()
        let matcher = ctx.RequestServices.GetRequiredService<ITransactionMatcher>()

        let! doc = readJsonBody ctx
        let root = doc.RootElement

        let tenantId = requireGuid root "tenantId"
        let syncEventIdOpt = tryGetGuid root "syncEventId"

        let txnsEl = root.GetProperty("transactions")
        let items = txnsEl.EnumerateArray() |> Seq.toArray
        let results = ResizeArray<(string * Guid * DateTimeOffset * DateTimeOffset option * int64 * string * string * string option)>()

        for txnEl in items do
            let externalId = requireString txnEl "externalId"
            let accountId = requireGuid txnEl "accountId"
            let occurredAt = requireDateTime txnEl "occurredAt"
            let postedAtOpt = tryGetDateTime txnEl "postedAt"
            let amountMinor = requireInt64 txnEl "amountMinor"
            let currency = requireString txnEl "currency"
            let description = requireString txnEl "description"
            let merchantOpt = tryGetString txnEl "merchant"
            results.Add((externalId, accountId, occurredAt, postedAtOpt, amountMinor, currency, description, merchantOpt))

        let manualAccessor = makeManualAccessor tenantId
        let repo = TransactionRepository.create factory manualAccessor
        let mutable created = 0
        let mutable updated = 0
        let mutable matched = 0

        for (externalId, accountId, occurredAt, postedAtOpt, amountMinor, currency, description, merchantOpt) in results do
            let! existingOpt = repo.GetByExternalIdAsync(externalId, accountId)
            match existingOpt with
            | Some existing ->
                let now = DateTimeOffset.UtcNow
                let updatedTxn =
                    { existing with
                        OccurredAt = occurredAt
                        PostedAt = postedAtOpt |> Option.orElse existing.PostedAt
                        Description = description
                        Merchant = merchantOpt |> Option.orElse existing.Merchant
                        UpdatedAt = now }
                do! repo.UpdateAsync(updatedTxn)
                updated <- updated + 1
            | None ->
                let amount =
                    let places =
                        match currency.ToUpperInvariant() with
                        | "BTC" -> 8
                        | _ -> 2
                    let factor = pown 10m places
                    { Amount = decimal amountMinor / factor; CurrencyCode = currency }

                let candidate =
                    { ExternalId = externalId
                      AccountId = accountId
                      OccurredAt = occurredAt
                      PostedAt = postedAtOpt
                      Amount = amount
                      Description = description
                      Merchant = merchantOpt }

                let! matchResult = matcher.MatchAsync tenantId accountId candidate
                let now = DateTimeOffset.UtcNow

                let newTxn =
                    { Id = Guid.NewGuid()
                      TenantId = tenantId
                      AccountId = accountId
                      OccurredAt = occurredAt
                      PostedAt = postedAtOpt
                      Amount = amount
                      Description = description
                      Merchant = merchantOpt
                      Memo = None
                      CategoryId = None
                      Source = TransactionSource.DataFeed "unknown"
                      ExternalId = Some externalId
                      MatchedTransactionId = None
                      TransferAccountId = None
                      Status = TransactionStatus.Cleared
                      MatchConfidence = None
                      SyncEventId = syncEventIdOpt
                      DeletedAt = None
                      CreatedAt = now
                      UpdatedAt = now }

                let! finalTxn =
                    task {
                        match matchResult with
                        | AutoMatched(manualId, conf) ->
                            matched <- matched + 1
                            let! manualOpt = repo.GetAsync(manualId)
                            match manualOpt with
                            | Some manual ->
                                let updatedManual =
                                    { manual with
                                        Status = TransactionStatus.Cleared
                                        MatchedTransactionId = Some newTxn.Id
                                        UpdatedAt = now }
                                do! repo.UpdateAsync(updatedManual)
                            | None -> ()
                            return
                                { newTxn with
                                    Status = TransactionStatus.Cleared
                                    MatchedTransactionId = Some manualId
                                    MatchConfidence = Some conf }
                        | NeedsReview(manualId, conf) ->
                            return
                                { newTxn with
                                    Status = TransactionStatus.NeedsReview
                                    MatchedTransactionId = Some manualId
                                    MatchConfidence = Some conf }
                        | NoMatch ->
                            return newTxn
                    }

                let! _ = repo.CreateAsync(finalTxn)
                created <- created + 1

        do! Response.ofJson {| created = created; updated = updated; matched = matched |} ctx
    }

// POST /internal/transactions/remove
let internalTransactionsRemoveHandler : HttpHandler = fun ctx ->
    task {
        let factory = ctx.RequestServices.GetRequiredService<IDbConnectionFactory>()
        let! doc = readJsonBody ctx
        let root = doc.RootElement
        let tenantId = requireGuid root "tenantId"
        let externalIdsEl = root.GetProperty("externalIds")
        let externalIds = externalIdsEl.EnumerateArray() |> Seq.map (fun el -> el.GetString()) |> Seq.toList
        let accessor = makeManualAccessor tenantId
        let repo = TransactionRepository.create factory accessor
        let! count = repo.DeleteByExternalIdsAsync(externalIds)
        do! Response.ofJson {| removed = count |} ctx
    }

// POST /internal/connections/status
let internalConnectionStatusHandler : HttpHandler = fun ctx ->
    task {
        let factory = ctx.RequestServices.GetRequiredService<IDbConnectionFactory>()
        let accessor = ctx.RequestServices.GetRequiredService<ITenantContextAccessor>()
        let connRepo = DataFeedConnectionRepository.create factory accessor
        let! doc = readJsonBody ctx
        let root = doc.RootElement
        let connectionId = requireGuid root "id"
        let statusStr = requireString root "status"
        let messageOpt = tryGetString root "message"
        let! connOpt = connRepo.GetAsync(connectionId)
        match connOpt with
        | None ->
            ctx.Response.StatusCode <- 404
            do! Response.ofJson {| error = "Connection not found" |} ctx
        | Some conn ->
            let newStatus =
                match statusStr.ToLowerInvariant() with
                | "active" -> ConnectionStatus.Active
                | "needsreauth" -> ConnectionStatus.NeedsReauth
                | "disabled" -> ConnectionStatus.Disabled
                | "error" -> ConnectionStatus.Error(messageOpt |> Option.defaultValue "Unknown error")
                | _ -> ConnectionStatus.Error($"Invalid status: {statusStr}")
            let updated = { conn with Status = newStatus; UpdatedAt = DateTimeOffset.UtcNow }
            do! connRepo.UpdateAsync(updated)
            do! Response.ofJson {| status = "updated"; connectionId = connectionId |} ctx
    }

// POST /internal/sync-trigger
// Routes to the appropriate ingestion service based on the connection's provider.
let syncTriggerHandler : HttpHandler = fun ctx ->
    task {
        let! doc = readJsonBody ctx
        let root = doc.RootElement
        let tenantId = requireGuid root "tenantId"
        let connectionId = requireGuid root "connectionId"

        let accessor = makeManualAccessor tenantId
        let factory = ctx.RequestServices.GetRequiredService<IDbConnectionFactory>()
        let connRepo = DataFeedConnectionRepository.create factory accessor
        let! connOpt = connRepo.GetAsync(connectionId)

        match connOpt with
        | None ->
            ctx.Response.StatusCode <- 404
            do! Response.ofJson {| error = "Connection not found" |} ctx
        | Some conn ->
            match DataFeedConnection.providerOf conn.Metadata with
            | DataFeedProvider.Plaid ->
                let plaid = ctx.RequestServices.GetRequiredService<IPlaidService>()
                let! result = plaid.SyncConnectionAsync tenantId connectionId
                do! Response.ofJson result ctx
            | DataFeedProvider.Akoya ->
                match akoyaIngestionUrl, serviceToken with
                | Some url, Some token ->
                    let http = ctx.RequestServices.GetRequiredService<HttpClient>()
                    let req = new HttpRequestMessage(HttpMethod.Post, $"{url.TrimEnd('/')}/sync-trigger")
                    req.Headers.Authorization <- System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token)
                    let payload = System.Text.Json.Nodes.JsonObject()
                    payload["tenantId"] <- System.Text.Json.Nodes.JsonValue.Create(tenantId.ToString())
                    payload["connectionId"] <- System.Text.Json.Nodes.JsonValue.Create(connectionId.ToString())
                    req.Content <- new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
                    let! resp = http.SendAsync(req)
                    let! body = resp.Content.ReadAsStringAsync()
                    if not resp.IsSuccessStatusCode then
                        ctx.Response.StatusCode <- (int)resp.StatusCode
                        do! Response.ofJson {| error = "Akoya ingestion failed"; detail = body |} ctx
                    else
                        do! Response.ofJson {| status = "sync_triggered"; provider = "akoya"; connectionId = connectionId |} ctx
                | _ ->
                    ctx.Response.StatusCode <- 503
                    do! Response.ofJson {| error = "Akoya ingestion URL or service token not configured" |} ctx
            | _ ->
                ctx.Response.StatusCode <- 501
                do! Response.ofJson {| error = "Provider not yet supported for sync trigger" |} ctx
    }

// POST /webhooks/plaid
let plaidWebhookHandler : HttpHandler = fun ctx ->
    task {
        let plaid = ctx.RequestServices.GetRequiredService<IPlaidService>()
        let verificationHeader =
            match ctx.Request.Headers.TryGetValue("Plaid-Verification") with
            | true, v when v.Count > 0 -> v.ToString()
            | _ -> ""
        use ms = new MemoryStream()
        do! ctx.Request.Body.CopyToAsync(ms)
        let bodyBytes = ms.ToArray()
        let! verified = plaid.VerifyWebhookAsync bodyBytes verificationHeader
        if not verified then
            ctx.Response.StatusCode <- 401
            do! Response.ofJson {| error = "Webhook verification failed" |} ctx
        else
            let bodyJson = Encoding.UTF8.GetString(bodyBytes)
            use doc = JsonDocument.Parse(bodyJson)
            let root = doc.RootElement
            let webhookType = root.GetProperty("webhook_type").GetString()
            let webhookCode = root.GetProperty("webhook_code").GetString()
            let itemId = root.GetProperty("item_id").GetString()
            match webhookType, webhookCode with
            | "TRANSACTIONS", "SYNC_UPDATES_AVAILABLE" ->
                let connRepo = ctx.RequestServices.GetRequiredService<IDataFeedConnectionRepository>()
                let! connOpt = connRepo.GetByItemIdAsync(itemId)
                match connOpt with
                | None ->
                    ctx.Response.StatusCode <- 404
                    do! Response.ofJson {| error = $"No connection found for item_id {itemId}" |} ctx
                | Some conn ->
                    let! _ = plaid.SyncConnectionAsync conn.TenantId conn.Id
                    do! Response.ofJson {| status = "sync_triggered" |} ctx
            | "ITEM", "ERROR" ->
                let errorCode =
                    match root.TryGetProperty("error") with
                    | true, err -> err.GetProperty("error_code").GetString()
                    | _ -> "UNKNOWN"
                let status =
                    match errorCode with
                    | "ITEM_LOGIN_REQUIRED" -> ConnectionStatus.NeedsReauth
                    | _ -> ConnectionStatus.Error($"Plaid error: {errorCode}")
                let connRepo = ctx.RequestServices.GetRequiredService<IDataFeedConnectionRepository>()
                let! connOpt = connRepo.GetByItemIdAsync(itemId)
                match connOpt with
                | None ->
                    ctx.Response.StatusCode <- 404
                    do! Response.ofJson {| error = $"No connection found for item_id {itemId}" |} ctx
                | Some conn ->
                    let updated = { conn with Status = status; UpdatedAt = DateTimeOffset.UtcNow }
                    do! connRepo.UpdateAsync(updated)
                    do! Response.ofJson {| status = "error_handled" |} ctx
            | "WEBHOOK_UPDATE_ACKNOWLEDGED", _ ->
                do! Response.ofJson {| status = "acknowledged" |} ctx
            | _ ->
                do! Response.ofJson {| status = "ignored"; webhookType = webhookType; webhookCode = webhookCode |} ctx
    }

// ── Static files (portal SPA) ────────────────────────────────────────────────

let portalPath = Path.Combine(wapp.Environment.WebRootPath, "portal")
if Directory.Exists(portalPath) then
    wapp.UseStaticFiles(
        new StaticFileOptions(
            RequestPath = PathString("/portal"),
            FileProvider = new PhysicalFileProvider(portalPath)
        )
    ) |> ignore

// ── Application pipeline ──────────────────────────────────────────────────────

wapp.UseMiddleware<TenantContextMiddleware>() |> ignore

wapp.UseRouting()
    .UseFalco([
        get "/" (Response.ofPlainText "Hello World!")
        get "/health" (Response.ofJson {| status = "ok"; version = version |})
        post "/auth/register" Auth.registerHandler
        post "/auth/login" Auth.loginHandler
        post "/api/auth/cookie-set" Auth.cookieSetHandler
        get "/me" (AuthHelpers.requireAuth Auth.meHandler)
        get "/api/prices" pricesHandler
        // Accounts
        get "/api/accounts" (AuthHelpers.requireAuth AccountEndpoints.listAccountsHandler)
        get "/api/accounts/{accountId:guid}" (AuthHelpers.requireAuth (fun ctx ->
            let accountId = ctx.Request.RouteValues.["accountId"] :?> Guid
            AccountEndpoints.getAccountHandler accountId ctx))
        post "/api/accounts" (AuthHelpers.requireAuth AccountEndpoints.createAccountHandler)
        patch "/api/accounts/{accountId:guid}" (AuthHelpers.requireAuth (fun ctx ->
            let accountId = ctx.Request.RouteValues.["accountId"] :?> Guid
            AccountEndpoints.updateAccountHandler accountId ctx))
        get "/api/accounts/{accountId:guid}/balance" (AuthHelpers.requireAuth (fun ctx ->
            let accountId = ctx.Request.RouteValues.["accountId"] :?> Guid
            AccountEndpoints.getBalanceHandler accountId ctx))
        delete "/api/accounts/{accountId:guid}" (AuthHelpers.requireAuth (fun ctx ->
            let accountId = ctx.Request.RouteValues.["accountId"] :?> Guid
            AccountEndpoints.deleteAccountHandler accountId ctx))
        // Categories
        get "/api/categories" (AuthHelpers.requireAuth CategoryEndpoints.listCategoriesHandler)
        get "/api/categories/{categoryId:guid}" (AuthHelpers.requireAuth (fun ctx ->
            let categoryId = ctx.Request.RouteValues.["categoryId"] :?> Guid
            CategoryEndpoints.getCategoryHandler categoryId ctx))
        post "/api/categories" (AuthHelpers.requireAuth CategoryEndpoints.createCategoryHandler)
        patch "/api/categories/{categoryId:guid}" (AuthHelpers.requireAuth (fun ctx ->
            let categoryId = ctx.Request.RouteValues.["categoryId"] :?> Guid
            CategoryEndpoints.updateCategoryHandler categoryId ctx))
        delete "/api/categories/{categoryId:guid}" (AuthHelpers.requireAuth (fun ctx ->
            let categoryId = ctx.Request.RouteValues.["categoryId"] :?> Guid
            CategoryEndpoints.deleteCategoryHandler categoryId ctx))
        // Transactions
        get "/api/transactions" (AuthHelpers.requireAuth TransactionEndpoints.listTransactionsHandler)
        get "/api/transactions/{txnId:guid}" (AuthHelpers.requireAuth (fun ctx ->
            let txnId = ctx.Request.RouteValues.["txnId"] :?> Guid
            TransactionEndpoints.getTransactionHandler txnId ctx))
        post "/api/transactions" (AuthHelpers.requireAuth TransactionEndpoints.createTransactionHandler)
        patch "/api/transactions/{txnId:guid}" (AuthHelpers.requireAuth (fun ctx ->
            let txnId = ctx.Request.RouteValues.["txnId"] :?> Guid
            TransactionEndpoints.updateTransactionHandler txnId ctx))
        delete "/api/transactions/{txnId:guid}" (AuthHelpers.requireAuth (fun ctx ->
            let txnId = ctx.Request.RouteValues.["txnId"] :?> Guid
            TransactionEndpoints.deleteTransactionHandler txnId ctx))
        // Connections
        get "/api/connections" (AuthHelpers.requireAuth ConnectionEndpoints.listConnectionsHandler)
        get "/api/connections/{connectionId:guid}/health-history" (AuthHelpers.requireAuth (fun ctx ->
            let connectionId = ctx.Request.RouteValues.["connectionId"] :?> Guid
            ConnectionEndpoints.healthHistoryHandler connectionId ctx))
        post "/api/connections/{connectionId:guid}/remediation-attempts" (AuthHelpers.requireAuth (fun ctx ->
            let connectionId = ctx.Request.RouteValues.["connectionId"] :?> Guid
            ConnectionEndpoints.createRemediationAttemptHandler connectionId ctx))
        patch "/api/remediation-attempts/{attemptId:guid}" (AuthHelpers.requireAuth (fun ctx ->
            let attemptId = ctx.Request.RouteValues.["attemptId"] :?> Guid
            ConnectionEndpoints.updateRemediationAttemptHandler attemptId ctx))
        // Plaid Link
        post "/api/connections/plaid/link-token" (AuthHelpers.requireAuth ConnectionEndpoints.plaidLinkTokenHandler)
        post "/api/connections/plaid/exchange" (AuthHelpers.requireAuth ConnectionEndpoints.plaidExchangeHandler)
        post "/api/connections/akoya/authorize-url" (AuthHelpers.requireAuth ConnectionEndpoints.akoyaAuthorizeUrlHandler)
        get "/api/connections/akoya/callback" ConnectionEndpoints.akoyaCallbackHandler
        delete "/api/connections/{connectionId:guid}" (AuthHelpers.requireAuth (fun ctx ->
            let connectionId = ctx.Request.RouteValues.["connectionId"] :?> Guid
            ConnectionEndpoints.deleteConnectionHandler connectionId ctx))
        post "/api/connections/{connectionId:guid}/reauth" (AuthHelpers.requireAuth (fun ctx ->
            let connectionId = ctx.Request.RouteValues.["connectionId"] :?> Guid
            ConnectionEndpoints.reauthConnectionHandler connectionId ctx))
        get "/api/transactions/needs-review" (AuthHelpers.requireAuth needsReviewHandler)
        post "/api/transactions/resolve" (AuthHelpers.requireAuth resolveHandler)
        post "/internal/transactions/upsert" internalUpsertHandler
        post "/internal/transactions/remove" internalTransactionsRemoveHandler
        post "/internal/connections/status" internalConnectionStatusHandler
        post "/internal/sync-trigger" syncTriggerHandler
        post "/webhooks/plaid" plaidWebhookHandler
        post "/api/budgets" (AuthHelpers.requireAuth BudgetEndpoints.createBudgetHandler)
        get "/api/budgets/{budgetId:guid}" (AuthHelpers.requireAuth (fun ctx ->
            let budgetId = ctx.Request.RouteValues.["budgetId"] :?> Guid
            BudgetEndpoints.getBudgetHandler budgetId ctx))
        post "/api/budgets/{budgetId:guid}/periods" (AuthHelpers.requireAuth (fun ctx ->
            let budgetId = ctx.Request.RouteValues.["budgetId"] :?> Guid
            BudgetEndpoints.createPeriodHandler budgetId ctx))
        get "/api/budgets/{budgetId:guid}/periods/{periodId:guid}" (AuthHelpers.requireAuth (fun ctx ->
            let budgetId = ctx.Request.RouteValues.["budgetId"] :?> Guid
            let periodId = ctx.Request.RouteValues.["periodId"] :?> Guid
            BudgetEndpoints.getPeriodHandler budgetId periodId ctx))
        patch "/api/budgets/{budgetId:guid}/periods/{periodId:guid}/categories/{categoryId:guid}" (AuthHelpers.requireAuth (fun ctx ->
            let budgetId = ctx.Request.RouteValues.["budgetId"] :?> Guid
            let periodId = ctx.Request.RouteValues.["periodId"] :?> Guid
            let categoryId = ctx.Request.RouteValues.["categoryId"] :?> Guid
            BudgetEndpoints.updateAllocationHandler budgetId periodId categoryId ctx))
        post "/api/budgets/{budgetId:guid}/periods/{periodId:guid}/close" (AuthHelpers.requireAuth (fun ctx ->
            let budgetId = ctx.Request.RouteValues.["budgetId"] :?> Guid
            let periodId = ctx.Request.RouteValues.["periodId"] :?> Guid
            BudgetEndpoints.closePeriodHandler budgetId periodId ctx))
        get "/api/budgets/{budgetId:guid}/periods/{periodId:guid}/report" (AuthHelpers.requireAuth (fun ctx ->
            let budgetId = ctx.Request.RouteValues.["budgetId"] :?> Guid
            let periodId = ctx.Request.RouteValues.["periodId"] :?> Guid
            BudgetEndpoints.getReportHandler budgetId periodId ctx))
        get "/api/budgets/{budgetId:guid}/periods/current/report" (AuthHelpers.requireAuth (fun ctx ->
            let budgetId = ctx.Request.RouteValues.["budgetId"] :?> Guid
            BudgetEndpoints.getCurrentReportHandler budgetId ctx))
        get "/api/preferences" (AuthHelpers.requireAuth UserPreferencesEndpoints.getPreferencesHandler)
        patch "/api/preferences" (AuthHelpers.requireAuth UserPreferencesEndpoints.updatePreferencesHandler)
        // Reconciliations
        post "/api/reconciliations" (AuthHelpers.requireAuth ReconciliationEndpoints.createReconciliationHandler)
        get "/api/reconciliations/{reconciliationId:guid}" (AuthHelpers.requireAuth (fun ctx ->
            let id = ctx.Request.RouteValues.["reconciliationId"] :?> Guid
            ReconciliationEndpoints.getReconciliationHandler id ctx))
        patch "/api/reconciliations/{reconciliationId:guid}/transactions" (AuthHelpers.requireAuth (fun ctx ->
            let id = ctx.Request.RouteValues.["reconciliationId"] :?> Guid
            ReconciliationEndpoints.updateTransactionsHandler id ctx))
        post "/api/reconciliations/{reconciliationId:guid}/complete" (AuthHelpers.requireAuth (fun ctx ->
            let id = ctx.Request.RouteValues.["reconciliationId"] :?> Guid
            ReconciliationEndpoints.completeHandler id ctx))
        post "/api/reconciliations/{reconciliationId:guid}/abort" (AuthHelpers.requireAuth (fun ctx ->
            let id = ctx.Request.RouteValues.["reconciliationId"] :?> Guid
            ReconciliationEndpoints.abortHandler id ctx))
        // Role-gated canary endpoint for integration tests
        get "/admin-only" (AuthHelpers.requireRole "owner" (Response.ofJson {| message = "ok" |}))
        // API key management
        post "/api/api-keys" (AuthHelpers.requireAuth Auth.createApiKeyHandler)
        get "/api/api-keys" (AuthHelpers.requireAuth Auth.listApiKeysHandler)
        delete "/api/api-keys/{keyId:guid}" (AuthHelpers.requireAuth (fun ctx ->
            let keyId = ctx.Request.RouteValues.["keyId"] :?> Guid
            Auth.revokeApiKeyHandler keyId ctx))
        // MCP server route group
        post "/mcp" (AuthHelpers.requireAuth McpServer.mcpHandler)
        // SPA fallthrough for portal routes
        get "/portal/{*path}" (fun ctx ->
            let indexPath = Path.Combine(portalPath, "index.html")
            if File.Exists(indexPath) then
                task {
                    ctx.Response.ContentType <- "text/html"
                    let! bytes = File.ReadAllBytesAsync(indexPath)
                    do! ctx.Response.Body.WriteAsync(bytes, 0, bytes.Length)
                }
            else
                ctx.Response.StatusCode <- 404
                Response.ofPlainText "Portal not built" ctx
        )
    ])
    .Run(Response.ofPlainText "Not found")
