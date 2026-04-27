open Microsoft.AspNetCore.Authentication
open Microsoft.AspNetCore.Authentication.Cookies
open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.DependencyInjection
open Falco
open Falco.Routing
open BitThicket.Steward.Api.Data

let builder = WebApplication.CreateBuilder()

// ─────────────────────────────────────────────────────────────────────────────
// Authentication
// ─────────────────────────────────────────────────────────────────────────────

builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(fun options ->
        options.LoginPath <- "/portal/welcome"
        options.AccessDeniedPath <- "/portal/welcome"
        options.Cookie.HttpOnly <- true
        options.Cookie.SecurePolicy <- Microsoft.AspNetCore.Http.CookieSecurePolicy.SameAsRequest
        options.SlidingExpiration <- true
        options.ExpireTimeSpan <- System.TimeSpan.FromDays(14))
|> ignore

builder.Services.AddAuthorization() |> ignore

let wapp = builder.Build()

// ─────────────────────────────────────────────────────────────────────────────
// Database migrations
// ─────────────────────────────────────────────────────────────────────────────

let loggerFactory = wapp.Services.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>()
ensureDatabase ()
runMigrations (loggerFactory.CreateLogger("Migrations"))

// ─────────────────────────────────────────────────────────────────────────────
// Middleware
// ─────────────────────────────────────────────────────────────────────────────

wapp.UseAuthentication()
    .UseAuthorization()
    .UseRouting()
    .UseStaticFiles()
    .UseFalco(BitThicket.Steward.Api.Routes.apiRoutes @ BitThicket.Steward.Api.Routes.portalRoutes)
    .Run(Response.ofPlainText "Not found")
