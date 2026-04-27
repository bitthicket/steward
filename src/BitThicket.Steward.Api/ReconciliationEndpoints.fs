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

type CreateReconciliationRequest = {
    accountId: Guid
    statementDate: string
    statementBalanceMinor: int64
    currency: string
}

type UpdateReconciliationTransactionsRequest = {
    included: Guid list
    excluded: Guid list
}

type ReconciliationResponse = {
    id: Guid
    accountId: Guid
    statementDate: string
    statementBalanceMinor: int64
    currency: string
    status: string
    note: string option
    createdByUserId: Guid
    startedAt: DateTimeOffset
    completedAt: DateTimeOffset option
}

type ReconciliationWithTransactionsResponse = {
    id: Guid
    accountId: Guid
    statementDate: string
    statementBalanceMinor: int64
    currency: string
    status: string
    note: string option
    createdByUserId: Guid
    startedAt: DateTimeOffset
    completedAt: DateTimeOffset option
    includedTransactions: Transaction list
    diffMinor: int64
}

type CreateReconciliationResponse = {
    reconciliation: ReconciliationResponse
    candidateTransactions: Transaction list
}

// ── JSON helpers ───────────────────────────────────────────────────────────

module private ReconciliationJson =
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

module private ReconciliationHelpers =
    let statusToString (s: ReconciliationStatus) : string =
        match s with
        | ReconciliationStatus.Open     -> "open"
        | ReconciliationStatus.Completed -> "completed"
        | ReconciliationStatus.Aborted  -> "aborted"

    let reconToResponse (recon: Reconciliation) : ReconciliationResponse =
        {
            id = recon.Id
            accountId = recon.AccountId
            statementDate = recon.StatementDate.ToString("yyyy-MM-dd")
            statementBalanceMinor = MoneyHelpers.toMinorUnits recon.StatementBalance
            currency = recon.StatementBalance.CurrencyCode
            status = statusToString recon.Status
            note = recon.Note
            createdByUserId = recon.CreatedByUserId
            startedAt = recon.StartedAt
            completedAt = recon.CompletedAt
        }

    let computeDiffMinor (recon: Reconciliation) (txns: Transaction list) : int64 =
        let includedSum =
            txns
            |> List.sumBy (fun t -> MoneyHelpers.toMinorUnits t.Amount)
        includedSum - MoneyHelpers.toMinorUnits recon.StatementBalance

// ── Endpoints ──────────────────────────────────────────────────────────────

module ReconciliationEndpoints =
    open ReconciliationHelpers

    // POST /api/reconciliations
    let createReconciliationHandler : HttpHandler = fun ctx ->
        task {
            let repo = ctx.RequestServices.GetRequiredService<IReconciliationRepository>()
            let txnRepo = ctx.RequestServices.GetRequiredService<ITransactionRepository>()
            let! doc = ReconciliationJson.readBody ctx
            let req = ReconciliationJson.deserialize<CreateReconciliationRequest> doc

            match DateOnly.TryParse(req.statementDate) with
            | false, _ ->
                ctx.Response.StatusCode <- 400
                do! Response.ofJson {| error = "Invalid statementDate format. Use: yyyy-MM-dd" |} ctx
            | true, statementDate ->
                let accessor = ctx.RequestServices.GetRequiredService<ITenantContextAccessor>()
                match accessor.Context with
                | None ->
                    ctx.Response.StatusCode <- 401
                    do! Response.ofJson {| error = "Unauthorized" |} ctx
                | Some tc ->
                    let now = DateTimeOffset.UtcNow
                    let recon: Reconciliation = {
                        Id = Guid.NewGuid()
                        TenantId = tc.TenantId
                        AccountId = req.accountId
                        StatementDate = statementDate
                        StatementBalance = MoneyHelpers.fromMinorUnits req.statementBalanceMinor req.currency
                        Status = ReconciliationStatus.Open
                        Note = None
                        CreatedByUserId = tc.UserId
                        StartedAt = now
                        CompletedAt = None
                    }
                    let! id = repo.CreateAsync(recon)
                    let! candidates = repo.ListCandidateTransactionsAsync(req.accountId, statementDate)
                    let resp = {
                        reconciliation = reconToResponse recon
                        candidateTransactions = candidates
                    }
                    ctx.Response.StatusCode <- 201
                    do! Response.ofJson resp ctx
        }

    // GET /api/reconciliations/{id}
    let getReconciliationHandler (id: Guid) : HttpHandler = fun ctx ->
        task {
            let repo = ctx.RequestServices.GetRequiredService<IReconciliationRepository>()
            let! resultOpt = repo.GetWithTransactionsAsync(id)
            match resultOpt with
            | None ->
                ctx.Response.StatusCode <- 404
                do! Response.ofJson {| error = "Reconciliation not found" |} ctx
            | Some (recon, txns) ->
                let diffMinor = computeDiffMinor recon txns
                let resp = {
                    id = recon.Id
                    accountId = recon.AccountId
                    statementDate = recon.StatementDate.ToString("yyyy-MM-dd")
                    statementBalanceMinor = MoneyHelpers.toMinorUnits recon.StatementBalance
                    currency = recon.StatementBalance.CurrencyCode
                    status = statusToString recon.Status
                    note = recon.Note
                    createdByUserId = recon.CreatedByUserId
                    startedAt = recon.StartedAt
                    completedAt = recon.CompletedAt
                    includedTransactions = txns
                    diffMinor = diffMinor
                }
                do! Response.ofJson resp ctx
        }

    // PATCH /api/reconciliations/{id}/transactions
    let updateTransactionsHandler (id: Guid) : HttpHandler = fun ctx ->
        task {
            let repo = ctx.RequestServices.GetRequiredService<IReconciliationRepository>()
            let! doc = ReconciliationJson.readBody ctx
            let req = ReconciliationJson.deserialize<UpdateReconciliationTransactionsRequest> doc

            let! reconOpt = repo.GetAsync(id)
            match reconOpt with
            | None ->
                ctx.Response.StatusCode <- 404
                do! Response.ofJson {| error = "Reconciliation not found" |} ctx
            | Some recon when recon.Status <> ReconciliationStatus.Open ->
                ctx.Response.StatusCode <- 409
                do! Response.ofJson {| error = "Reconciliation is not open" |} ctx
            | Some _ ->
                do! repo.UpdateIncludedTransactionsAsync(id, req.included, req.excluded)
                do! Response.ofJson {| success = true |} ctx
        }

    // POST /api/reconciliations/{id}/complete
    let completeHandler (id: Guid) : HttpHandler = fun ctx ->
        task {
            let repo = ctx.RequestServices.GetRequiredService<IReconciliationRepository>()

            let force =
                match ctx.Request.Query.TryGetValue("force") with
                | true, v when v.Count > 0 ->
                    match Boolean.TryParse(v.ToString()) with true, b -> b | _ -> false
                | _ -> false

            let! reconOpt = repo.GetAsync(id)
            match reconOpt with
            | None ->
                ctx.Response.StatusCode <- 404
                do! Response.ofJson {| error = "Reconciliation not found" |} ctx
            | Some recon when recon.Status <> ReconciliationStatus.Open ->
                ctx.Response.StatusCode <- 409
                do! Response.ofJson {| error = "Reconciliation is not open" |} ctx
            | Some _ ->
                let! result = repo.CompleteAsync(id, force, None)
                match result with
                | Ok diffMinor ->
                    do! Response.ofJson {| status = "completed"; diffMinor = diffMinor |} ctx
                | Error msg when msg.StartsWith("diff:") ->
                    let diffStr = msg.[5..]
                    let diffMinor = int64 diffStr
                    ctx.Response.StatusCode <- 409
                    do! Response.ofJson {| error = "Balance mismatch"; diffMinor = diffMinor |} ctx
                | Error msg ->
                    ctx.Response.StatusCode <- 400
                    do! Response.ofJson {| error = msg |} ctx
        }

    // POST /api/reconciliations/{id}/abort
    let abortHandler (id: Guid) : HttpHandler = fun ctx ->
        task {
            let repo = ctx.RequestServices.GetRequiredService<IReconciliationRepository>()

            let! reconOpt = repo.GetAsync(id)
            match reconOpt with
            | None ->
                ctx.Response.StatusCode <- 404
                do! Response.ofJson {| error = "Reconciliation not found" |} ctx
            | Some recon when recon.Status <> ReconciliationStatus.Open ->
                ctx.Response.StatusCode <- 409
                do! Response.ofJson {| error = "Reconciliation is not open" |} ctx
            | Some _ ->
                do! repo.AbortAsync(id)
                do! Response.ofJson {| status = "aborted" |} ctx
        }
