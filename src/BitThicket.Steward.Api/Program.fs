open System
open System.IO
open System.Reflection
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
        // Role-gated canary endpoint for integration tests
        get "/admin-only" (AuthHelpers.requireRole "owner" (Response.ofJson {| message = "ok" |}))
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
