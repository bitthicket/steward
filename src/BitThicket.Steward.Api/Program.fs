open System
open System.IO
open System.Reflection
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Falco
open Falco.Routing
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Npgsql
open BitThicket.Steward.Api
open BitThicket.Steward.Api.Domain
open BitThicket.Steward.Pricing

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

let version =
    let v = Assembly.GetExecutingAssembly().GetName().Version
    if isNull v then "0.0.0" else v.ToString()

let builder = WebApplication.CreateBuilder()
builder.WebHost.UseUrls($"http://0.0.0.0:{port}") |> ignore

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
let sharedHttpClient = new System.Net.Http.HttpClient()
builder.Services.AddSingleton<IPriceProvider>(fun sp ->
    let db = sp.GetRequiredService<NpgsqlDataSource>()
    let log = sp.GetRequiredService<ILogger<CoinGeckoPriceProvider>>()
    CoinGeckoPriceProvider(sharedHttpClient, db, log) :> IPriceProvider) |> ignore
builder.Services.AddHostedService<PricingWorker>() |> ignore

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
            let! existingOpt = repo.GetByExternalIdAsync(externalId)
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

// ── Application pipeline ──────────────────────────────────────────────────────

wapp.UseMiddleware<TenantContextMiddleware>() |> ignore

wapp.UseRouting()
    .UseFalco([
        get "/" (Response.ofPlainText "Hello World!")
        get "/health" (Response.ofJson {| status = "ok"; version = version |})
        post "/auth/register" Auth.registerHandler
        post "/auth/login" Auth.loginHandler
        get "/me" (AuthHelpers.requireAuth Auth.meHandler)
        get "/api/prices" pricesHandler
        get "/api/transactions/needs-review" (AuthHelpers.requireAuth needsReviewHandler)
        post "/api/transactions/resolve" (AuthHelpers.requireAuth resolveHandler)
        post "/internal/transactions/upsert" internalUpsertHandler
        // Role-gated canary endpoint for integration tests
        get "/admin-only" (AuthHelpers.requireRole "owner" (Response.ofJson {| message = "ok" |}))
    ])
    .Run(Response.ofPlainText "Not found")
