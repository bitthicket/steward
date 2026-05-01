namespace BitThicket.Steward.Api

open System
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Serilog.Context

/// Adds tenantId, userId, requestId, and route to the Serilog log context.
type RequestLogEnrichmentMiddleware(next: RequestDelegate) =
    member _.InvokeAsync(ctx: HttpContext) =
        task {
            let requestId = ctx.TraceIdentifier
            let route = ctx.Request.Path.ToString()

            let tenantId, userId =
                match ctx.Items.TryGetValue("TenantContext") with
                | true, (:? TenantContext as tc) -> tc.TenantId.ToString(), tc.UserId.ToString()
                | _ -> "anonymous", "anonymous"

            use _tenant = LogContext.PushProperty("tenantId", tenantId)
            use _user = LogContext.PushProperty("userId", userId)
            use _reqId = LogContext.PushProperty("requestId", requestId)
            use _route = LogContext.PushProperty("route", route)

            do! next.Invoke(ctx)
        }

/// Records request metrics after each request completes.
type MetricsMiddleware(next: RequestDelegate) =
    member _.InvokeAsync(ctx: HttpContext) =
        task {
            try
                do! next.Invoke(ctx)
            finally
                let route = ctx.Request.Path.ToString()
                let status = ctx.Response.StatusCode
                Metrics.state.IncRequest(route, status)
        }
