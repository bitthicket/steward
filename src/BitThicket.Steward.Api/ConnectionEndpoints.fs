namespace BitThicket.Steward.Api

open System
open System.IO
open System.Text.Json
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Falco
open BitThicket.Steward.Api.Domain
open BitThicket.Steward.Api.Vault

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

// ── Plaid Link DTOs ────────────────────────────────────────────────────────

type PlaidLinkTokenRequest = {
    clientName: string option
}

type PlaidLinkTokenResponse = {
    linkToken: string
}

type PlaidExchangeAccountDto = {
    id: string
    name: string
    ``type``: string
    subtype: string option
    mask: string option
}

type PlaidExchangeRequest = {
    publicToken: string
    institutionId: string
    institutionName: string
    accounts: PlaidExchangeAccountDto list
}

type PlaidExchangeResponse = {
    connectionId: Guid
    provider: string
    status: string
    accounts: PlaidAccountResponse list
}

and PlaidAccountResponse = {
    id: Guid
    name: string
    accountType: string
    currencyCode: string
    institutionName: string option
    externalId: string option
    isOnBudget: bool
}

type ConnectionDetailResponse = {
    id: Guid
    provider: string
    status: string
    linkedAccountIds: Guid list
    createdAt: DateTimeOffset
    updatedAt: DateTimeOffset
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

    // ── Plaid-specific helpers ───────────────────────────────────────────────

    let private plaidAccountTypeToDomain (plaidType: string) (subtype: string option) : AccountType =
        match plaidType.ToLowerInvariant(), subtype |> Option.map (fun s -> s.ToLowerInvariant()) with
        | "depository", Some "checking" -> AccountType.Checking
        | "depository", Some "savings" -> AccountType.Savings
        | "depository", _ -> AccountType.Checking
        | "credit", _ -> AccountType.CreditCard
        | "loan", _ -> AccountType.Loan
        | "investment", _ -> AccountType.Investment
        | "brokerage", _ -> AccountType.Investment
        | _ -> AccountType.Cash

    let private accountToResponse (account: Account) : PlaidAccountResponse =
        {
            id = account.Id
            name = account.Name
            accountType =
                match account.AccountType with
                | AccountType.Checking -> "checking"
                | AccountType.Savings -> "savings"
                | AccountType.CreditCard -> "creditCard"
                | AccountType.Investment -> "investment"
                | AccountType.Loan -> "loan"
                | AccountType.Cash -> "cash"
            currencyCode = account.CurrencyCode
            institutionName = account.InstitutionName
            externalId = account.ExternalId
            isOnBudget = account.IsOnBudget
        }

    // POST /api/connections/plaid/link-token
    let plaidLinkTokenHandler : HttpHandler = fun ctx ->
        task {
            let accessor = ctx.RequestServices.GetRequiredService<ITenantContextAccessor>()
            let plaid = ctx.RequestServices.GetRequiredService<IPlaidService>()

            match accessor.Context with
            | None ->
                ctx.Response.StatusCode <- 401
                do! Response.ofJson {| error = "Unauthorized" |} ctx
            | Some tc ->
                let! linkToken = plaid.CreateLinkTokenAsync tc.TenantId tc.UserId
                do! Response.ofJson {| linkToken = linkToken |} ctx
        }

    // POST /api/connections/plaid/exchange
    let plaidExchangeHandler : HttpHandler = fun ctx ->
        task {
            let accessor = ctx.RequestServices.GetRequiredService<ITenantContextAccessor>()
            let plaid = ctx.RequestServices.GetRequiredService<IPlaidService>()
            let vault = ctx.RequestServices.GetRequiredService<IVaultService>()
            let connRepo = ctx.RequestServices.GetRequiredService<IDataFeedConnectionRepository>()
            let accountRepo = ctx.RequestServices.GetRequiredService<IAccountRepository>()

            match accessor.Context with
            | None ->
                ctx.Response.StatusCode <- 401
                do! Response.ofJson {| error = "Unauthorized" |} ctx
            | Some tc ->
                let! doc = ConnectionJson.readBody ctx
                let req = ConnectionJson.deserialize<PlaidExchangeRequest> doc

                if String.IsNullOrWhiteSpace(req.publicToken) then
                    ctx.Response.StatusCode <- 400
                    do! Response.ofJson {| error = "publicToken is required" |} ctx
                elif String.IsNullOrWhiteSpace(req.institutionId) then
                    ctx.Response.StatusCode <- 400
                    do! Response.ofJson {| error = "institutionId is required" |} ctx
                elif req.accounts.IsEmpty then
                    ctx.Response.StatusCode <- 400
                    do! Response.ofJson {| error = "At least one account is required" |} ctx
                else
                    let! exchangeResult = plaid.ExchangePublicTokenAsync req.publicToken
                    let now = DateTimeOffset.UtcNow

                    // Store access token in vault
                    let envelope: BitThicket.Steward.Api.Vault.CredentialEnvelope = {
                        AccessToken = exchangeResult.AccessToken
                        RefreshToken = None
                        ExpiresAt = None
                        ProviderSpecific = None
                    }
                    let! credentialRef = vault.StoreAsync(tc, envelope)

                    // Create connection
                    let connectionId = Guid.NewGuid()
                    let connection: DataFeedConnection = {
                        Id = connectionId
                        TenantId = tc.TenantId
                        UserId = tc.UserId
                        Metadata = ProviderMetadata.Plaid(exchangeResult.ItemId, req.institutionId, None)
                        CredentialRef = credentialRef
                        Status = ConnectionStatus.Active
                        LinkedAccountIds = []
                        CreatedAt = now
                        UpdatedAt = now
                    }
                    let! _ = connRepo.CreateAsync(connection)
                    ()

                    // Create accounts
                    let mutable createdAccounts = ResizeArray<Account>()
                    for acctDto in req.accounts do
                        let accountType = plaidAccountTypeToDomain acctDto.``type`` acctDto.subtype
                        let account: Account = {
                            Id = Guid.NewGuid()
                            TenantId = tc.TenantId
                            UserId = tc.UserId
                            Name = acctDto.name
                            AccountType = accountType
                            CurrencyCode = "USD"
                            InstitutionName = Some req.institutionName
                            ExternalId = Some acctDto.id
                            CreditCardInfo = None
                            IsOnBudget = AccountRepository.defaultIsOnBudget accountType
                            IsActive = true
                            DeletedAt = None
                            CreatedAt = now
                            UpdatedAt = now
                        }
                        let! accountId = accountRepo.CreateAsync(account)
                        createdAccounts.Add({ account with Id = accountId })

                    // Update connection with linked accounts
                    let linkedIds = createdAccounts |> Seq.map (fun a -> a.Id) |> Seq.toList
                    let updatedConn = { connection with LinkedAccountIds = linkedIds; UpdatedAt = DateTimeOffset.UtcNow }
                    do! connRepo.UpdateAsync(updatedConn)

                    let resp: PlaidExchangeResponse = {
                        connectionId = connectionId
                        provider = "plaid"
                        status = "active"
                        accounts = createdAccounts |> Seq.map accountToResponse |> Seq.toList
                    }
                    ctx.Response.StatusCode <- 201
                    do! Response.ofJson resp ctx
        }

    // DELETE /api/connections/{id}
    let deleteConnectionHandler (connectionId: Guid) : HttpHandler = fun ctx ->
        task {
            let accessor = ctx.RequestServices.GetRequiredService<ITenantContextAccessor>()
            let plaid = ctx.RequestServices.GetRequiredService<IPlaidService>()
            let vault = ctx.RequestServices.GetRequiredService<IVaultService>()
            let connRepo = ctx.RequestServices.GetRequiredService<IDataFeedConnectionRepository>()
            let accountRepo = ctx.RequestServices.GetRequiredService<IAccountRepository>()

            match accessor.Context with
            | None ->
                ctx.Response.StatusCode <- 401
                do! Response.ofJson {| error = "Unauthorized" |} ctx
            | Some tc ->
                let! connOpt = connRepo.GetAsync(connectionId)
                match connOpt with
                | None ->
                    ctx.Response.StatusCode <- 404
                    do! Response.ofJson {| error = "Connection not found" |} ctx
                | Some conn when conn.TenantId <> tc.TenantId ->
                    ctx.Response.StatusCode <- 404
                    do! Response.ofJson {| error = "Connection not found" |} ctx
                | Some conn ->
                    // Revoke item via Plaid
                    let! envelope = vault.LoadAsync(tc, conn.CredentialRef)
                    let! _ = plaid.RemoveItemAsync envelope.AccessToken

                    // Delete vault entry
                    let! _ = vault.DeleteAsync(tc, conn.CredentialRef)

                    // Soft-delete linked accounts
                    for accountId in conn.LinkedAccountIds do
                        let! accountOpt = accountRepo.GetAsync(accountId)
                        match accountOpt with
                        | Some account ->
                            do! accountRepo.DeleteAsync(accountId)
                        | None -> ()

                    // Soft-delete connection
                    let deletedConn = { conn with Status = ConnectionStatus.Disabled; UpdatedAt = DateTimeOffset.UtcNow }
                    do! connRepo.UpdateAsync(deletedConn)

                    ctx.Response.StatusCode <- 204
                    do! Response.ofEmpty ctx
        }

    // POST /api/connections/{id}/reauth
    let reauthConnectionHandler (connectionId: Guid) : HttpHandler = fun ctx ->
        task {
            let accessor = ctx.RequestServices.GetRequiredService<ITenantContextAccessor>()
            let plaid = ctx.RequestServices.GetRequiredService<IPlaidService>()
            let connRepo = ctx.RequestServices.GetRequiredService<IDataFeedConnectionRepository>()

            match accessor.Context with
            | None ->
                ctx.Response.StatusCode <- 401
                do! Response.ofJson {| error = "Unauthorized" |} ctx
            | Some tc ->
                let! connOpt = connRepo.GetAsync(connectionId)
                match connOpt with
                | None ->
                    ctx.Response.StatusCode <- 404
                    do! Response.ofJson {| error = "Connection not found" |} ctx
                | Some conn when conn.TenantId <> tc.TenantId ->
                    ctx.Response.StatusCode <- 404
                    do! Response.ofJson {| error = "Connection not found" |} ctx
                | Some conn ->
                    match DataFeedConnection.providerOf conn.Metadata with
                    | DataFeedProvider.Plaid ->
                        let! linkToken = plaid.CreateReauthTokenAsync tc.TenantId connectionId
                        do! Response.ofJson {| linkToken = linkToken |} ctx
                    | _ ->
                        ctx.Response.StatusCode <- 400
                        do! Response.ofJson {| error = "Re-auth is only supported for Plaid connections" |} ctx
        }
