module BitThicket.Steward.Api.Observability

open System
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open OpenTelemetry.Metrics
open OpenTelemetry.Resources
open Serilog
open Serilog.Core
open Serilog.Events
open Serilog.Formatting.Compact

/// Builds a Serilog logger configuration suitable for production.
/// Uses compact JSON formatting when DOTNET_RUNNING_IN_CONTAINER or ASPNETCORE_ENVIRONMENT=Production.
let createLoggerConfig () : LoggerConfiguration =
    let isProduction =
        let env = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
        let container = Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER")
        (not (String.IsNullOrWhiteSpace(env)) && env.Equals("Production", StringComparison.OrdinalIgnoreCase))
        || (not (String.IsNullOrWhiteSpace(container)) && container.Equals("true", StringComparison.OrdinalIgnoreCase))

    let config = LoggerConfiguration().Destructure.With<SecretMaskingPolicy>()

    let config =
        if isProduction then
            config.MinimumLevel.Information()
                  .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
                  .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
                  .WriteTo.Console(CompactJsonFormatter())
        else
            config.MinimumLevel.Debug()
                  .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
                  .WriteTo.Console()

    config

/// Registers OpenTelemetry metrics with the service collection.
let registerMetrics (services: IServiceCollection) (serviceName: string) (serviceVersion: string) =
    services.AddOpenTelemetry().WithMetrics(fun metricsBuilder ->
        metricsBuilder
            .SetResourceBuilder(
                ResourceBuilder.CreateDefault()
                    .AddService(serviceName = serviceName, serviceVersion = serviceVersion))
            .AddAspNetCoreInstrumentation()
            .AddRuntimeInstrumentation()
            .AddHttpClientInstrumentation()
            .AddPrometheusExporter()
        |> ignore)
    |> ignore

/// Adds the Prometheus metrics scraping endpoint.
let useMetrics (app: WebApplication) =
    app.UseOpenTelemetryPrometheusScrapingEndpoint() |> ignore

/// Adds Serilog request logging middleware.
let useRequestLogging (app: WebApplication) =
    app.UseSerilogRequestLogging(fun opts ->
        opts.MessageTemplate <- "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0000} ms"
        opts.GetLevel <- fun ctx elapsed ex ->
            match ex with
            | null when ctx.Response.StatusCode < 500 -> LogEventLevel.Information
            | null -> LogEventLevel.Warning
            | _ -> LogEventLevel.Error)
    |> ignore
