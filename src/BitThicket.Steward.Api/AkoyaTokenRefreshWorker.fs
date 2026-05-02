namespace BitThicket.Steward.Api

open System
open System.Data.Common
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open BitThicket.Steward.Api.Domain
open BitThicket.Steward.Api.Vault

/// Background worker that refreshes Akoya access tokens before they expire.
/// Runs every 5 minutes. For each active Akoya connection with a vault entry
/// that expires within the next 15 minutes, attempts a refresh. On failure,
/// marks the connection as NeedsReauth.
type AkoyaTokenRefreshWorker(
    sp: IServiceProvider,
    log: ILogger<AkoyaTokenRefreshWorker>) =
    inherit BackgroundService()

    let refreshWindow = TimeSpan.FromMinutes(15.0)
    let pollInterval = TimeSpan.FromMinutes(5.0)

    let runRefreshLoop (ct: CancellationToken) =
        task {
            use scope = sp.CreateScope()
            let factory = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>()
            let vault = scope.ServiceProvider.GetRequiredService<IVaultService>()
            let akoya = scope.ServiceProvider.GetRequiredService<IAkoyaOAuthService>()

            // GC expired oauth_state rows first
            let! gcCount = akoya.GCExpiredStateAsync()
            if gcCount > 0 then
                log.LogInformation("GC'd {Count} expired oauth_state rows", gcCount)

            // List all active Akoya connections by scanning the database.
            // We use an unscoped connection and set tenant context manually per row.
            use! conn = factory.OpenAsync()
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                """SELECT id, tenant_id, user_id, credential_ref, provider_metadata
                   FROM data_feed_connections
                   WHERE status->>'Case' = 'Active'"""

            let! reader = cmd.ExecuteReaderAsync(ct)
            let reader = reader :> DbDataReader
            let mutable rows = ResizeArray<(Guid * Guid * Guid * string)>()
            while! reader.ReadAsync(ct) do
                let id = reader.GetGuid(0)
                let tenantId = reader.GetGuid(1)
                let userId = reader.GetGuid(2)
                let credRef = reader.GetString(3)
                let metadataJson = reader.GetString(4)
                // Only process Akoya connections
                if metadataJson.Contains("\"Akoya\"") then
                    rows.Add((id, tenantId, userId, credRef))
            reader.Close()

            log.LogInformation("AkoyaTokenRefreshWorker: scanning {Count} active Akoya connections", rows.Count)

            for (connectionId, tenantId, userId, credRef) in rows do
                if ct.IsCancellationRequested then return ()
                let ctx = { TenantId = tenantId; UserId = userId }
                try
                    let! envelope = vault.LoadAsync(ctx, credRef)
                    match envelope.ExpiresAt with
                    | None ->
                        log.LogDebug("Connection {ConnectionId} has no expiry; skipping", connectionId)
                    | Some expiresAt when expiresAt > DateTimeOffset.UtcNow.Add(refreshWindow) ->
                        log.LogDebug("Connection {ConnectionId} expires at {ExpiresAt}; not yet due", connectionId, expiresAt)
                    | Some _ ->
                        log.LogInformation("Refreshing token for connection {ConnectionId}", connectionId)
                        let! _ = akoya.RefreshTokenAsync(ctx, connectionId)
                        log.LogInformation("Refreshed token for connection {ConnectionId}", connectionId)
                with ex ->
                    log.LogError(ex, "Failed to refresh token for connection {ConnectionId}; marking NeedsReauth", connectionId)
                    try
                        let accessor = { new ITenantContextAccessor with member _.Context = Some ctx }
                        let connRepo = DataFeedConnectionRepository.create factory accessor
                        let! connOpt = connRepo.GetAsync(connectionId)
                        match connOpt with
                        | Some connection ->
                            let updated = { connection with Status = ConnectionStatus.NeedsReauth; UpdatedAt = DateTimeOffset.UtcNow }
                            do! connRepo.UpdateAsync(updated)
                        | None -> ()
                    with ex2 ->
                        log.LogError(ex2, "Failed to mark connection {ConnectionId} as NeedsReauth", connectionId)
        }

    override _.ExecuteAsync(ct: CancellationToken) =
        task {
            while not ct.IsCancellationRequested do
                try
                    do! Task.Delay(pollInterval, ct)
                    do! runRefreshLoop ct
                with
                | :? OperationCanceledException -> ()
                | ex ->
                    log.LogError(ex, "AkoyaTokenRefreshWorker loop failed")
                    try
                        do! Task.Delay(TimeSpan.FromMinutes(1.0), ct)
                    with :? OperationCanceledException -> ()
        }
