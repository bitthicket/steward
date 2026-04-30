namespace BitThicket.Steward.Api

open System
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection

/// Identifies the tenant and user for the current request/operation.
/// The persistence layer uses this to set `steward.tenant_id` and
/// `steward.user_id` on every Npgsql connection so that downstream RLS
/// policies can enforce isolation.
type TenantContext = {
    TenantId: Guid
    UserId: Guid
}

/// Provides access to the current request's tenant context.
type ITenantContextAccessor =
    abstract Context : TenantContext option

/// Reads TenantContext from the current HttpContext.Items (populated by
/// TenantContextMiddleware via validated JWT claims).
type TenantContextAccessor(httpContextAccessor: IHttpContextAccessor) =
    [<DefaultValue>] val mutable Context : TenantContext option

    interface ITenantContextAccessor with
        member _.Context =
            match httpContextAccessor.HttpContext with
            | null -> None
            | ctx ->
                match ctx.Items.TryGetValue("TenantContext") with
                | true, (:? TenantContext as tc) -> Some tc
                | _ -> None

module TenantContextServices =
    /// Register tenant-context services in the DI container.
    let register (services: IServiceCollection) =
        services.AddHttpContextAccessor() |> ignore
        services.AddScoped<ITenantContextAccessor, TenantContextAccessor>() |> ignore
        services
