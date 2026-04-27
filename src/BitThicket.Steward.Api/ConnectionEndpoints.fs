namespace BitThicket.Steward.Api

open System
open System.IO
open System.Text.Json
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Falco
open BitThicket.Steward.Api.Domain

// ── Request / response DTOs ────────────────────────────────────────────────

type FeedHealthResponse = {
    level: string
    lastSuccessAt: DateTimeOffset option
    lastFailureAt: DateTimeOffset option
    consecutiveFailures: int
    evaluatedAt: DateTimeOffset
}

type ConnectionResponse = {
    id: Guid
    provider: string
    status: string
    feedHealth: FeedHealthResponse option
    createdAt: DateTimeOffset
    updatedAt: DateTimeOffset
}

type SyncEventResponse = {
    id: Guid
    startedAt: DateTimeOffset
    completedAt: DateTimeOffset option
    status: string
    transactionsAdded: int
    transactionsUpdated: int
}

type RemediationAttemptResponse = {
    id: Guid
    connectionId: Guid
    startedAt: DateTimeOffset
    completedAt: DateTimeOffset option
    actorKind: string
    actorId: Guid option
    strategy: string
    outcome: string option
    notes: string option
}

type CreateRemediationAttemptRequest = {
    strategy: string
    notes: string option
}

type UpdateRemediationAttemptRequest = {
    outcome: string
    reason: string option
    prompt: string option
    notes: string option
}

type HealthHistoryResponse = {
    syncEvents: SyncEventResponse list
    remediationAttempts: RemediationAttemptResponse option list
}

// ── JSON helpers ───────────────────────────────────────────────────────────

module private ConnectionJson =
    let readBody (ctx: HttpContext) =
        task {
            use reader = new StreamReader(ctx.Request.Body, System.Text.Encoding.UTF8)
            let! json = reader.ReadToEndAsync()
            return JsonDocument.Parse(json)
        }

    let jsonOptions =
        let opts = JsonSerializerOptions()
        opts.PropertyNamingPolicy <- JsonNamingPolicy.CamelCase
        opts

    let deserialize<'T> (doc: JsonDocument) =
        JsonSerializer.Deserialize<'T>(doc, jsonOptions)

// ── Domain helpers ─────────────────────────────────────────────────────────

module private ConnectionHelpers =
    let providerToString (metadata: ProviderMetadata) : string =
        match DataFeedConnection.providerOf metadata with
        | DataFeedProvider.Akoya  -> "akoya"
        | DataFeedProvider.Plaid  -> "plaid"
        | DataFeedProvider.MX     -> "mx"
        | DataFeedProvider.Yodlee -> "yodlee"
        | DataFeedProvider.Intuit -> "intuit"
        | DataFeedProvider.Manual -> "manual"

    let statusToString (status: ConnectionStatus) : string =
        match status with
        | ConnectionStatus.Active       -> "active"
        | ConnectionStatus.NeedsReauth  -> "needsReauth"
        | ConnectionStatus.Disabled     -> "disabled"
        | ConnectionStatus.Error _      -> "error"

    let feedHealthLevelToString (level: FeedHealthLevel) : string =
        match level with
        | FeedHealthLevel.Healthy  -> "healthy"
        | FeedHealthLevel.Degraded -> "degraded"
        | FeedHealthLevel.Failing  -> "failing"
        | FeedHealthLevel.Unknown  -> "unknown"

    let syncStatusToString (status: SyncStatus) : string =
        match status with
        | SyncStatus.Success             -> "success"
        | SyncStatus.PartialSuccess _    -> "partialSuccess"
        | SyncStatus.Failed _            -> "failed"

    let outcomeToString (outcome: RemediationOutcome) : string =
        match outcome with
        | RemediationOutcome.Resolved              -> "resolved"
        | RemediationOutcome.StillFailing _        -> "stillFailing"
        | RemediationOutcome.NeedsHumanInput _     -> "needsHumanInput"

    let feedHealthToResponse (fh: FeedHealth) : FeedHealthResponse =
        {
            level = feedHealthLevelToString fh.Level
            lastSuccessAt = fh.LastSuccessAt
            lastFailureAt = fh.LastFailureAt
            consecutiveFailures = fh.ConsecutiveFailures
            evaluatedAt = fh.EvaluatedAt
        }

    let syncEventToResponse (se: SyncEvent) : SyncEventResponse =
        {
            id = se.Id
            startedAt = se.StartedAt
            completedAt = se.CompletedAt
            status = syncStatusToString se.Status
            transactionsAdded = se.TransactionsAdded
            transactionsUpdated = se.TransactionsUpdated
        }

    let remediationAttemptToResponse (ra: RemediationAttempt) : RemediationAttemptResponse =
        let actorKind, actorId =
            match ra.ActorAgentId, ra.ActorUserId with
            | Some id, None -> "agent", Some id
            | None, Some id -> "user", Some id
            | Some id, _    -> "agent", Some id
            | None, None    -> "unknown", None

        {
            id = ra.Id
            connectionId = ra.ConnectionId
            startedAt = ra.StartedAt
            completedAt = ra.CompletedAt
            actorKind = actorKind
            actorId = actorId
            strategy = ra.Strategy
            outcome = ra.Outcome |> Option.map outcomeToString
            notes = ra.Notes
        }

// ── Endpoints ──────────────────────────────────────────────────────────────

module ConnectionEndpoints =
    open ConnectionHelpers

    // GET /api/connections
    let listConnectionsHandler : HttpHandler = fun ctx ->
        task {
            let connRepo = ctx.RequestServices.GetRequiredService<IDataFeedConnectionRepository>()
            let healthRepo = ctx.RequestServices.GetRequiredService<IFeedHealthRepository>()

            let! connections = connRepo.ListAsync()
            let! healthRows = healthRepo.ListForTenantAsync()
            let healthByConn = healthRows |> List.map (fun h -> h.ConnectionId, h) |> Map.ofList

            let resp =
                connections
                |> List.map (fun conn ->
                    let feedHealthOpt = healthByConn |> Map.tryFind conn.Id |> Option.map feedHealthToResponse
                    {
                        id = conn.Id
                        provider = providerToString conn.Metadata
                        status = statusToString conn.Status
                        feedHealth = feedHealthOpt
                        createdAt = conn.CreatedAt
                        updatedAt = conn.UpdatedAt
                    })

            do! Response.ofJson {| connections = resp |} ctx
        }

    // GET /api/connections/{id}/health-history?from=&to=
    let healthHistoryHandler (connectionId: Guid) : HttpHandler = fun ctx ->
        task {
            let connRepo = ctx.RequestServices.GetRequiredService<IDataFeedConnectionRepository>()
            let syncRepo = ctx.RequestServices.GetRequiredService<ISyncEventRepository>()
            let attemptRepo = ctx.RequestServices.GetRequiredService<IRemediationAttemptRepository>()

            let! connOpt = connRepo.GetAsync(connectionId)
            match connOpt with
            | None ->
                ctx.Response.StatusCode <- 404
                do! Response.ofJson {| error = "Connection not found" |} ctx
            | Some _ ->
                let q = ctx.Request.Query
                let fromOpt =
                    match q.TryGetValue("from") with
                    | true, v when v.Count > 0 ->
                        match DateTimeOffset.TryParse(v.ToString()) with true, d -> Some d | _ -> None
                    | _ -> None
                let toOpt =
                    match q.TryGetValue("to") with
                    | true, v when v.Count > 0 ->
                        match DateTimeOffset.TryParse(v.ToString()) with true, d -> Some d | _ -> None
                    | _ -> None

                let! syncEvents = syncRepo.ListForConnectionAsync(connectionId)
                let filteredSyncs =
                    syncEvents
                    |> List.filter (fun se ->
                        let afterFrom = fromOpt |> Option.forall (fun f -> se.StartedAt >= f)
                        let beforeTo = toOpt |> Option.forall (fun t -> se.StartedAt <= t)
                        afterFrom && beforeTo)
                    |> List.map syncEventToResponse

                let! attempts = attemptRepo.ListForConnectionAsync(connectionId)
                let filteredAttempts =
                    attempts
                    |> List.filter (fun ra ->
                        let afterFrom = fromOpt |> Option.forall (fun f -> ra.StartedAt >= f)
                        let beforeTo = toOpt |> Option.forall (fun t -> ra.StartedAt <= t)
                        afterFrom && beforeTo)
                    |> List.map remediationAttemptToResponse

                do! Response.ofJson {| syncEvents = filteredSyncs; remediationAttempts = filteredAttempts |} ctx
        }

    // POST /api/connections/{id}/remediation-attempts
    let createRemediationAttemptHandler (connectionId: Guid) : HttpHandler = fun ctx ->
        task {
            let connRepo = ctx.RequestServices.GetRequiredService<IDataFeedConnectionRepository>()
            let attemptRepo = ctx.RequestServices.GetRequiredService<IRemediationAttemptRepository>()
            let accessor = ctx.RequestServices.GetRequiredService<ITenantContextAccessor>()

            let! connOpt = connRepo.GetAsync(connectionId)
            match connOpt with
            | None ->
                ctx.Response.StatusCode <- 404
                do! Response.ofJson {| error = "Connection not found" |} ctx
            | Some conn ->
                let! doc = ConnectionJson.readBody ctx
                let req = ConnectionJson.deserialize<CreateRemediationAttemptRequest> doc

                if String.IsNullOrWhiteSpace(req.strategy) then
                    ctx.Response.StatusCode <- 400
                    do! Response.ofJson {| error = "Strategy is required" |} ctx
                else
                    let tc =
                        match accessor.Context with
                        | Some c -> c
                        | None -> { TenantId = conn.TenantId; UserId = conn.UserId }

                    let attempt: RemediationAttempt = {
                        Id = Guid.NewGuid()
                        TenantId = conn.TenantId
                        ConnectionId = connectionId
                        StartedAt = DateTimeOffset.UtcNow
                        CompletedAt = None
                        ActorAgentId = None
                        ActorUserId = Some tc.UserId
                        Strategy = req.strategy.Trim()
                        Outcome = None
                        Notes = req.notes
                    }

                    let! id = attemptRepo.CreateAsync(attempt)
                    let resp = remediationAttemptToResponse attempt
                    ctx.Response.StatusCode <- 201
                    do! Response.ofJson resp ctx
        }

    // PATCH /api/remediation-attempts/{id}
    let updateRemediationAttemptHandler (attemptId: Guid) : HttpHandler = fun ctx ->
        task {
            let attemptRepo = ctx.RequestServices.GetRequiredService<IRemediationAttemptRepository>()

            let! doc = ConnectionJson.readBody ctx
            let req = ConnectionJson.deserialize<UpdateRemediationAttemptRequest> doc

            let outcome =
                match req.outcome.ToLowerInvariant() with
                | "resolved" ->
                    Ok RemediationOutcome.Resolved
                | "stillfailing" ->
                    match req.reason with
                    | Some r when not (String.IsNullOrWhiteSpace(r)) ->
                        Ok (RemediationOutcome.StillFailing(r.Trim()))
                    | _ ->
                        Error "Reason is required when outcome is 'stillFailing'"
                | "needshumaninput" ->
                    match req.prompt with
                    | Some p when not (String.IsNullOrWhiteSpace(p)) ->
                        Ok (RemediationOutcome.NeedsHumanInput(p.Trim()))
                    | _ ->
                        Error "Prompt is required when outcome is 'needsHumanInput'"
                | _ ->
                    Error "Invalid outcome. Use: resolved, stillFailing, needsHumanInput"

            match outcome with
            | Error msg ->
                ctx.Response.StatusCode <- 400
                do! Response.ofJson {| error = msg |} ctx
            | Ok o ->
                let! attemptOpt = attemptRepo.GetAsync(attemptId)
                match attemptOpt with
                | None ->
                    ctx.Response.StatusCode <- 404
                    do! Response.ofJson {| error = "Remediation attempt not found" |} ctx
                | Some attempt when attempt.Outcome.IsSome ->
                    ctx.Response.StatusCode <- 409
                    do! Response.ofJson {| error = "Remediation attempt already has an outcome" |} ctx
                | Some attempt ->
                    do! attemptRepo.UpdateOutcomeAsync attemptId o req.notes
                    let! updated = attemptRepo.GetAsync(attemptId)
                    match updated with
                    | None ->
                        ctx.Response.StatusCode <- 404
                        do! Response.ofJson {| error = "Remediation attempt not found" |} ctx
                    | Some u ->
                        do! Response.ofJson (remediationAttemptToResponse u) ctx
        }
