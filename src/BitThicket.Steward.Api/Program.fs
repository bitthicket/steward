open System
open System.Reflection
open Falco
open Falco.Routing
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting

let port =
    match Environment.GetEnvironmentVariable("PORT") with
    | null | "" -> "8080"
    | v -> v

let version =
    let v = Assembly.GetExecutingAssembly().GetName().Version
    if isNull v then "0.0.0" else v.ToString()

let builder = WebApplication.CreateBuilder()
builder.WebHost.UseUrls($"http://0.0.0.0:{port}") |> ignore

let wapp = builder.Build()

wapp.UseRouting()
    .UseFalco([
        get "/" (Response.ofPlainText "Hello World!")
        get "/health" (Response.ofJson {| status = "ok"; version = version |})
    ])
    .Run(Response.ofPlainText "Not found")
