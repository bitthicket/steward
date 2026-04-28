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

type IngestionTransactionDto = {
    externalId: string
    accountId: Guid
    occurredAt: DateTimeOffset
    postedAt: DateTimeOffset option
    amount: decimal
    currency: string
    description: string
    merchant: string option
    memo: string option
}

type IngestionUpsertRequest = {
    connectionId: Guid
    transactions: IngestionTransactionDto list
}

type IngestionUpsertResponse = {
    syncEventId: Guid
    added: int
    updated: int
}

// ── JSON helpers ───────────────────────────────────────────────────────────

module private IngestionJson =
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

// ── Endpoints ──────────────────────────────────────────────────────────────

module IngestionEndpoints =

    /// POST /internal/ingestion/upsert
    /// Authenticated by API key with `ingestion:write` scope.
    /// Upserts transactions for a data feed connection and records a sync event.
    let upsertHandler : HttpHandler = fun ctx ->
        task {
            let factory = ctx.RequestServices.GetRequiredService<IDbConnectionFactory>()
            let accessor = ctx.RequestServices.GetRequiredService<ITenantContextAccessor>()
            let accountRepo = ctx.RequestServices.GetRequiredService<IAccountRepository>()
            let txnRepo = ctx.RequestServices.GetRequiredService<ITransactionRepository>()
            let connRepo = ctx.RequestServices.GetRequiredService<IDataFeedConnectionRepository>()
            let syncRepo = ctx.RequestServices.GetRequiredService<ISyncEventRepository>()

            let! doc = IngestionJson.readBody ctx
            let req = IngestionJson.deserialize<IngestionUpsertRequest> doc

            match accessor.Context with
            | None ->
                ctx.Response.StatusCode <- 401
                do! Response.ofJson {| error = "Unauthorized" |} ctx
                return ()
            | Some tc ->
                // Validate connection exists and belongs to tenant
                let! connOpt = connRepo.GetAsync(req.connectionId)
                match connOpt with
                | None ->
                    ctx.Response.StatusCode <- 404
                    do! Response.ofJson {| error = "Connection not found" |} ctx
                | Some conn when conn.TenantId <> tc.TenantId ->
                    ctx.Response.StatusCode <- 404
                    do! Response.ofJson {| error = "Connection not found" |} ctx
                | Some conn ->
                    let providerName =
                        match DataFeedConnection.providerOf conn.Metadata with
                        | DataFeedProvider.Akoya -> "akoya"
                        | DataFeedProvider.Plaid -> "plaid"
                        | DataFeedProvider.MX -> "mx"
                        | DataFeedProvider.Yodlee -> "yodlee"
                        | DataFeedProvider.Intuit -> "intuit"
                        | DataFeedProvider.Manual -> "manual"

                    let startedAt = DateTimeOffset.UtcNow
                    let mutable added = 0
                    let mutable updated = 0
                    let mutable errors = ResizeArray<string>()

                    for dto in req.transactions do
                        try
                            // Validate account exists and belongs to tenant
                            let! accountOpt = accountRepo.GetAsync(dto.accountId)
                            match accountOpt with
                            | None ->
                                errors.Add($"Account {dto.accountId} not found")
                            | Some account when account.TenantId <> tc.TenantId ->
                                errors.Add($"Account {dto.accountId} not found")
                            | Some account ->
                                let! existingOpt = txnRepo.GetByExternalIdAsync dto.externalId account.Id
                                let now = DateTimeOffset.UtcNow

                                match existingOpt with
                                | Some existing ->
                                    let updatedTxn = {
                                        existing with
                                            OccurredAt = dto.occurredAt
                                            PostedAt = dto.postedAt |> Option.orElse existing.PostedAt
                                            Amount = { Amount = dto.amount; CurrencyCode = dto.currency.ToUpperInvariant() }
                                            Description = dto.description
                                            Merchant = dto.merchant |> Option.orElse existing.Merchant
                                            Memo = dto.memo |> Option.orElse existing.Memo
                                            Source = TransactionSource.DataFeed providerName
                                            UpdatedAt = now
                                    }
                                    do! txnRepo.UpdateAsync(updatedTxn)
                                    updated <- updated + 1
                                | None ->
                                    let newTxn: Transaction = {
                                        Id = Guid.NewGuid()
                                        TenantId = tc.TenantId
                                        AccountId = account.Id
                                        Amount = { Amount = dto.amount; CurrencyCode = dto.currency.ToUpperInvariant() }
                                        Description = dto.description
                                        Merchant = dto.merchant
                                        Memo = dto.memo
                                        CategoryId = None
                                        Status = TransactionStatus.Cleared
                                        Source = TransactionSource.DataFeed providerName
                                        ExternalId = Some dto.externalId
                                        MatchedTransactionId = None
                                        TransferAccountId = None
                                        MatchConfidence = None
                                        SyncEventId = None
                                        DeletedAt = None
                                        PostedAt = dto.postedAt
                                        OccurredAt = dto.occurredAt
                                        CreatedAt = now
                                        UpdatedAt = now
                                    }
                                    let! _ = txnRepo.CreateAsync(newTxn)
                                    added <- added + 1
                        with ex ->
                            errors.Add($"Transaction {dto.externalId} failed: {ex.Message}")

                    let status =
                        if errors.Count = 0 then SyncStatus.Success
                        elif added = 0 && updated = 0 then SyncStatus.Failed("All transactions failed")
                        else SyncStatus.PartialSuccess(errors |> Seq.toList)

                    let syncEvent: SyncEvent = {
                        Id = Guid.NewGuid()
                        TenantId = tc.TenantId
                        ConnectionId = conn.Id
                        StartedAt = startedAt
                        CompletedAt = Some(DateTimeOffset.UtcNow)
                        Status = status
                        TransactionsAdded = added
                        TransactionsUpdated = updated
                    }

                    let! syncEventId = syncRepo.CreateAsync(syncEvent)

                    let resp: IngestionUpsertResponse = {
                        syncEventId = syncEventId
                        added = added
                        updated = updated
                    }

                    ctx.Response.StatusCode <- 200
                    do! Response.ofJson resp ctx
        }
