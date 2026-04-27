module BitThicket.Steward.Api.Test.IngestionEndpointsTests

open System
open System.IO
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Xunit
open Swensen.Unquote
open Npgsql
open BitThicket.Steward.Api
open BitThicket.Steward.Api.Domain
open BitThicket.Steward.Api.Vault

/// Helper to create a minimal HttpContext with a JSON body.
let private makeContext (services: IServiceProvider) (json: string) =
    let ctx = new DefaultHttpContext()
    ctx.RequestServices <- services
    ctx.Request.Method <- "POST"
    ctx.Request.ContentType <- "application/json"
    ctx.Request.Body <- new MemoryStream(Encoding.UTF8.GetBytes(json))
    ctx.Response.Body <- new MemoryStream()
    ctx

/// Helper to read the response body as string.
let private readResponse (ctx: HttpContext) =
    ctx.Response.Body.Seek(0L, SeekOrigin.Begin) |> ignore
    use reader = new StreamReader(ctx.Response.Body)
    reader.ReadToEnd()

/// Creates an in-memory service provider with all required services.
let private makeServices (dataSource: NpgsqlDataSource) =
    let services = new Microsoft.Extensions.DependencyInjection.ServiceCollection()
    services.AddSingleton<NpgsqlDataSource>(dataSource) |> ignore
    TenantContextServices.register services |> ignore
    services.AddSingleton<IDbConnectionFactory>(DbConnectionFactory(dataSource)) |> ignore
    services.AddScoped<IAccountRepository>(fun sp ->
        let factory = sp.GetRequiredService<IDbConnectionFactory>()
        let accessor = sp.GetRequiredService<ITenantContextAccessor>()
        AccountRepository.create factory accessor) |> ignore
    services.AddSingleton<IVaultService>(VaultService(DbConnectionFactory(dataSource)) :> IVaultService) |> ignore
    services.AddScoped<ITransactionRepository>(fun sp ->
        let factory = sp.GetRequiredService<IDbConnectionFactory>()
        let accessor = sp.GetRequiredService<ITenantContextAccessor>()
        TransactionRepository.create factory accessor) |> ignore
    services.AddScoped<IDataFeedConnectionRepository>(fun sp ->
        let factory = sp.GetRequiredService<IDbConnectionFactory>()
        let accessor = sp.GetRequiredService<ITenantContextAccessor>()
        DataFeedConnectionRepository.create factory accessor) |> ignore
    services.AddScoped<ISyncEventRepository>(fun sp ->
        let factory = sp.GetRequiredService<IDbConnectionFactory>()
        let accessor = sp.GetRequiredService<ITenantContextAccessor>()
        SyncEventRepository.create factory accessor) |> ignore
    services.BuildServiceProvider()

type IngestionEndpointsTests() =

    [<Fact>]
    let ``upsertHandler returns 404 when connection not found`` () =
        task {
            let services = new Microsoft.Extensions.DependencyInjection.ServiceCollection()
            services.AddSingleton<ITenantContextAccessor>({ new ITenantContextAccessor with member _.Context = None }) |> ignore
            services.AddScoped<IAccountRepository>(fun _ ->
                { new IAccountRepository with
                    member _.GetAsync(_) = Task.FromResult(None)
                    member _.ListAsync() = Task.FromResult([])
                    member _.CreateAsync(_) = Task.FromResult(Guid.NewGuid())
                    member _.UpdateAsync(_) = Task.FromResult(())
                    member _.DeleteAsync(_) = Task.FromResult(()) }) |> ignore
            services.AddScoped<ITransactionRepository>(fun _ ->
                { new ITransactionRepository with
                    member _.GetAsync(_) = Task.FromResult(None)
                    member _.GetByExternalIdAsync(_, _) = Task.FromResult(None)
                    member _.ListByAccountAsync(_) = Task.FromResult([])
                    member _.CreateAsync(_) = Task.FromResult(Guid.NewGuid())
                    member _.UpdateAsync(_) = Task.FromResult(())
                    member _.DeleteAsync(_) = Task.FromResult(()) }) |> ignore
            services.AddScoped<IDataFeedConnectionRepository>(fun _ ->
                { new IDataFeedConnectionRepository with
                    member _.GetAsync(_) = Task.FromResult(None)
                    member _.CreateAsync(_) = Task.FromResult(Guid.NewGuid())
                    member _.UpdateAsync(_) = Task.FromResult(()) }) |> ignore
            services.AddScoped<ISyncEventRepository>(fun _ ->
                { new ISyncEventRepository with
                    member _.CreateAsync(_) = Task.FromResult(Guid.NewGuid())
                    member _.GetAsync(_) = Task.FromResult(None) }) |> ignore
            let sp = services.BuildServiceProvider()
            let tenantId = Guid.NewGuid().ToString()
            let userId = Guid.NewGuid().ToString()
            let connectionId = Guid.NewGuid().ToString()
            let json = $"""{{"tenantId":"{tenantId}","userId":"{userId}","connectionId":"{connectionId}","transactions":[]}}"""
            let ctx = makeContext sp json
            do! IngestionEndpoints.upsertHandler ctx
            test <@ ctx.Response.StatusCode = 404 @>
        }
