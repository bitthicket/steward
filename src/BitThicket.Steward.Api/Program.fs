open System
open System.Reflection
open Falco
open Falco.Routing
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Npgsql
open BitThicket.Steward.Api
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
builder.Services.AddSingleton<IBudgetRepository>(fun sp ->
    let factory = sp.GetRequiredService<IDbConnectionFactory>()
    let accessor = sp.GetRequiredService<ITenantContextAccessor>()
    BudgetRepository.create factory accessor) |> ignore
builder.Services.AddSingleton<IBudgetPeriodRepository>(fun sp ->
    let factory = sp.GetRequiredService<IDbConnectionFactory>()
    let accessor = sp.GetRequiredService<ITenantContextAccessor>()
    BudgetPeriodRepository.create factory accessor) |> ignore
builder.Services.AddSingleton<ICategoryRepository>(fun sp ->
    let factory = sp.GetRequiredService<IDbConnectionFactory>()
    let accessor = sp.GetRequiredService<ITenantContextAccessor>()
    CategoryRepository.create factory accessor) |> ignore
let sharedHttpClient = new System.Net.Http.HttpClient()
builder.Services.AddSingleton<IPriceProvider>(fun sp ->
    let db = sp.GetRequiredService<NpgsqlDataSource>()
    let log = sp.GetRequiredService<ILogger<CoinGeckoPriceProvider>>()
    CoinGeckoPriceProvider(sharedHttpClient, db, log) :> IPriceProvider) |> ignore
builder.Services.AddHostedService<PricingWorker>() |> ignore

let wapp = builder.Build()

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
        // Role-gated canary endpoint for integration tests
        get "/admin-only" (AuthHelpers.requireRole "owner" (Response.ofJson {| message = "ok" |}))
    ])
    .Run(Response.ofPlainText "Not found")
