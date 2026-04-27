namespace BitThicket.Steward.Api

open System
open System.Data.Common
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Npgsql
open BitThicket.Steward.Api.Domain

/// Lightweight projection returned by the due-for-sync query.
/// Does not include all DataFeedConnection fields — only what the
/// coordinator needs to emit a sync.requested event.
type DueConnection =
    { Id: Guid
      TenantId: Guid
      UserId: Guid
      Metadata: ProviderMetadata
      PreferredSyncFrequency: TimeSpan
      LastSyncedAt: DateTimeOffset option }

/// Background service that ticks every minute and emits `sync.requested`
/// events for connections whose last successful sync is older than their
/// preferred sync frequency.
type SyncCoordinator(
    factory: IDbConnectionFactory,
    bus: IEventBus,
    logger: ILogger<SyncCoordinator>) =

    inherit BackgroundService()

    let jsonOptions =
        let opts = JsonSerializerOptions()
        opts.Converters.Add(JsonFSharpConverter(JsonUnionEncoding.NamedFields))
        opts.PropertyNamingPolicy <- JsonNamingPolicy.CamelCase
        opts

    let clampFrequency (freq: TimeSpan) : TimeSpan =
        let minFreq = TimeSpan.FromMinutes(15.0)
        let maxFreq = TimeSpan.FromHours(24.0)
        if freq < minFreq then minFreq
        elif freq > maxFreq then maxFreq
        else freq

    let mapDueConnection (reader: DbDataReader) : DueConnection =
        let metadataJson = reader.GetString(3)
        let preferredInterval = reader.GetFieldValue<TimeSpan>(7)
        let lastSyncedOpt =
            if reader.IsDBNull(8) then None
            else Some(DateTimeOffset(reader.GetDateTime(8), TimeSpan.Zero))
        { Id = reader.GetGuid(0)
          TenantId = reader.GetGuid(1)
          UserId = reader.GetGuid(2)
          Metadata = JsonSerializer.Deserialize<ProviderMetadata>(metadataJson, jsonOptions)
          PreferredSyncFrequency = preferredInterval
          LastSyncedAt = lastSyncedOpt }

    let listDueConnections () =
        task {
            use! conn = factory.OpenAsync()
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                """SELECT id, tenant_id, user_id, provider_metadata, credential_ref, status,
                          linked_account_ids, preferred_sync_frequency, last_synced_at,
                          created_at, updated_at
                   FROM get_connections_due_for_sync()"""
            let! reader = cmd.ExecuteReaderAsync()
            use reader = reader
            let results = ResizeArray<DueConnection>()
            while! reader.ReadAsync() do
                results.Add(mapDueConnection reader)
            return results |> Seq.toList
        }

    let emitSyncRequested (connection: DueConnection) =
        task {
            let payload =
                {| tenantId = connection.TenantId
                   connectionId = connection.Id
                   accountId = (None : Guid option) |}
            let json = JsonSerializer.Serialize(payload, jsonOptions)
            let envelope =
                { Topic = EventBusTopics.syncRequested
                  JsonPayload = json
                  OccurredAt = DateTimeOffset.UtcNow
                  CausationId = None }
            do! bus.Publish(envelope)
            logger.LogInformation(
                "Emitted sync.requested for connection {ConnectionId} (tenant {TenantId}, provider {Provider})",
                connection.Id, connection.TenantId,
                (DataFeedConnection.providerOf connection.Metadata).ToString())
        }

    override _.ExecuteAsync(ct: CancellationToken) =
        task {
            logger.LogInformation("Sync coordinator started; tick interval = 60 seconds")
            while not ct.IsCancellationRequested do
                try
                    let! dueConnections = listDueConnections()
                    logger.LogDebug("Sync coordinator scan: {Count} connections due", dueConnections.Length)
                    for conn in dueConnections do
                        try
                            do! emitSyncRequested conn
                        with ex ->
                            logger.LogError(
                                ex,
                                "Failed to emit sync.requested for connection {ConnectionId}; continuing",
                                conn.Id)
                with ex ->
                    logger.LogError(ex, "Sync coordinator tick failed")

                try
                    do! Task.Delay(TimeSpan.FromMinutes(1.0), ct)
                with :? OperationCanceledException ->
                    ()
            logger.LogInformation("Sync coordinator stopped")
        }
