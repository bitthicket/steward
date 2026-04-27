open System
open System.IO
open System.Reflection
open System.Text
open System.Text.Json
open Falco
open Falco.Routing
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Npgsql
open BitThicket.Steward.Api
open BitThicket.Steward.Api.Vault
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

let serviceToken =
    match Environment.GetEnvironmentVariable("STEWARD_SERVICE_TOKEN") with
    | null | "" -> None
    | v -> Some v

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
builder.Services.AddScoped<IAccountRepository>(fun sp ->
    let factory = sp.GetRequiredService<IDbConnectionFactory>()
    let accessor = sp.GetRequiredService<ITenantContextAccessor>()
    AccountRepository.create factory accessor) |> ignore
builder.Services.AddSingleton<IVaultService>(VaultService(DbConnectionFactory(dataSource)) :> IVaultService) |> ignore
builder.Services.AddScoped<ITransactionRepository>(fun sp ->
    let factory = sp.GetRequiredService<IDbConnectionFactory>()
    let accessor = sp.GetRequiredService<ITenantContextAccessor>()
    TransactionRepository.create factory accessor) |> ignore
builder.Services.AddScoped<IDataFeedConnectionRepository>(fun sp ->
    let factory = sp.GetRequiredService<IDbConnectionFactory>()
    let accessor = sp.GetRequiredService<ITenantContextAccessor>()
    DataFeedConnectionRepository.create factory accessor) |> ignore
builder.Services.AddScoped<ISyncEventRepository>(fun sp ->
    let factory = sp.GetRequiredService<IDbConnectionFactory>()
    let accessor = sp.GetRequiredService<ITenantContextAccessor>()
    SyncEventRepository.create factory accessor) |> ignore
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

let requireGuid (el: JsonElement) (name: string) =
    match tryGetGuid el name with Some g -> g | None -> failwith $"Missing required field: {name}"

let requireString (el: JsonElement) (name: string) =
    match tryGetString el name with Some s -> s | None -> failwith $"Missing required field: {name}"

// ── Service-auth wrapper ─────────────────────────────────────────────────────

let requireServiceToken (next: HttpHandler) : HttpHandler = fun ctx ->
    task {
        match serviceToken with
        | None ->
            ctx.Response.StatusCode <- 500
            do! Response.ofJson {| error = "Service token not configured" |} ctx
        | Some expected ->
            let header =
                match ctx.Request.Headers.TryGetValue("Authorization") with
                | true, v when v.Count > 0 -> v.ToString()
                | _ -> ""
            let token =
                if header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) then
                    header.Substring(7)
                else
                    ""
            if token <> expected then
                ctx.Response.StatusCode <- 401
                do! Response.ofJson {| error = "Unauthorized" |} ctx
            else
                do! next ctx
    }

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
        // Role-gated canary endpoint for integration tests
        get "/admin-only" (AuthHelpers.requireRole "owner" (Response.ofJson {| message = "ok" |}))
        // Internal ingestion API
        post "/internal/ingestion/upsert" (requireServiceToken IngestionEndpoints.upsertHandler)
        // Internal vault API
        post "/internal/vault/retrieve" (requireServiceToken (fun ctx ->
            task {
                let vault = ctx.RequestServices.GetRequiredService<IVaultService>()
                let! doc = readJsonBody ctx
                let root = doc.RootElement
                let tenantId = requireGuid root "tenantId"
                let credentialRef = requireString root "credentialRef"
                let! envelopeOpt = vault.RetrieveAsync(tenantId, credentialRef)
                match envelopeOpt with
                | None ->
                    ctx.Response.StatusCode <- 404
                    do! Response.ofJson {| error = "Credential not found" |} ctx
                | Some env ->
                    do! Response.ofJson {| accessToken = env.AccessToken; refreshToken = env.RefreshToken; expiresAt = env.ExpiresAt |} ctx
            }))
        // Internal connection lookup API
        post "/internal/connections/lookup" (requireServiceToken (fun ctx ->
            task {
                let factory = ctx.RequestServices.GetRequiredService<IDbConnectionFactory>()
                let! doc = readJsonBody ctx
                let root = doc.RootElement
                let tenantId = requireGuid root "tenantId"
                let connectionId = requireGuid root "connectionId"
                let! connOpt = DataFeedConnectionRepository.getAsync factory { TenantId = tenantId; UserId = Guid.Empty } connectionId
                match connOpt with
                | None ->
                    ctx.Response.StatusCode <- 404
                    do! Response.ofJson {| error = "Connection not found" |} ctx
                | Some conn ->
                    do! Response.ofJson conn ctx
            }))
    ])
    .Run(Response.ofPlainText "Not found")
