namespace BitThicket.Steward.Api

open System
open System.IO
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Falco
open BitThicket.Steward.Api.Domain

// ── Request / response DTOs ────────────────────────────────────────────────

type CreateTransactionRequest = {
    accountId: Guid
    occurredAt: DateTimeOffset
    postedAt: DateTimeOffset option
    amountMinor: int64
    currency: string
    description: string
    merchant: string option
    categoryId: Guid option
    transferAccountId: Guid option
}

type UpdateTransactionRequest = {
    description: string option
    merchant: string option
    categoryId: Guid option
    notes: string option
    amountMinor: int64 option
    occurredAt: DateTimeOffset option
    postedAt: DateTimeOffset option
}

type TransactionResponse = {
    id: Guid
    accountId: Guid
    occurredAt: DateTimeOffset
    postedAt: DateTimeOffset option
    amount: decimal
    currency: string
    description: string
    merchant: string option
    notes: string option
    categoryId: Guid option
    status: string
    source: string
    transferAccountId: Guid option
    createdAt: DateTimeOffset
    updatedAt: DateTimeOffset
}

type TransactionListResponse = {
    items: TransactionResponse list
    nextCursor: string option
}

// ── JSON helpers ───────────────────────────────────────────────────────────

module private TransactionJson =
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

// ── Cursor helpers ─────────────────────────────────────────────────────────

module private Cursor =
    let encode (occurredAt: DateTimeOffset) (id: Guid) : string =
        let json = $"""{{"o":"{occurredAt:O}","i":"{id}"}}"""
        Convert.ToBase64String(Encoding.UTF8.GetBytes(json))

    let decode (cursor: string) : Result<DateTimeOffset * Guid, string> =
        try
            let bytes = Convert.FromBase64String(cursor)
            let json = Encoding.UTF8.GetString(bytes)
            use doc = JsonDocument.Parse(json)
            let root = doc.RootElement
            let occurredAt = DateTimeOffset.Parse(root.GetProperty("o").GetString())
            let id = Guid.Parse(root.GetProperty("i").GetString())
            Ok (occurredAt, id)
        with ex ->
            Error $"Invalid cursor: {ex.Message}"

// ── Domain helpers ─────────────────────────────────────────────────────────

module private TransactionHelpers =
    let statusToString (s: TransactionStatus) : string =
        match s with
        | TransactionStatus.Pending     -> "pending"
        | TransactionStatus.NeedsReview -> "needsReview"
        | TransactionStatus.Cleared     -> "cleared"
        | TransactionStatus.Reconciled  -> "reconciled"

    let sourceToString (s: TransactionSource) : string =
        match s with
        | TransactionSource.Manual -> "manual"
        | TransactionSource.DataFeed provider -> $"dataFeed:{provider}"
        | TransactionSource.Import format -> $"import:{format}"

    let txnToResponse (txn: Transaction) : TransactionResponse =
        {
            id = txn.Id
            accountId = txn.AccountId
            occurredAt = txn.OccurredAt
            postedAt = txn.PostedAt
            amount = txn.Amount.Amount
            currency = txn.Amount.CurrencyCode
            description = txn.Description
            merchant = txn.Merchant
            notes = txn.Memo
            categoryId = txn.CategoryId
            status = statusToString txn.Status
            source = sourceToString txn.Source
            transferAccountId = txn.TransferAccountId
            createdAt = txn.CreatedAt
            updatedAt = txn.UpdatedAt
        }

    let decimalPlaces (currencyCode: string) : int =
        match currencyCode.ToUpperInvariant() with
        | "BTC" -> 8
        | _ -> 2

    let fromMinor (minor: int64) (currencyCode: string) : Money =
        let places = decimalPlaces currencyCode
        let factor = pown 10m places
        { Amount = decimal minor / factor; CurrencyCode = currencyCode }

    let isWithinEditWindow (txn: Transaction) : bool =
        DateTimeOffset.UtcNow <= txn.CreatedAt.AddDays(30.0)

// ── Endpoints ──────────────────────────────────────────────────────────────

module TransactionEndpoints =
    open TransactionHelpers

    // GET /api/transactions
    let listTransactionsHandler : HttpHandler = fun ctx ->
        task {
            let repo = ctx.RequestServices.GetRequiredService<ITransactionRepository>()
            let q = ctx.Request.Query

            let accountIdOpt =
                match q.TryGetValue("accountId") with
                | true, v when v.Count > 0 ->
                    match Guid.TryParse(v.ToString()) with true, g -> Some g | _ -> None
                | _ -> None

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

            let statusOpt =
                match q.TryGetValue("status") with
                | true, v when v.Count > 0 ->
                    try Some (TransactionRepository.statusFromString (v.ToString())) with _ -> None
                | _ -> None

            let limit =
                match q.TryGetValue("limit") with
                | true, v when v.Count > 0 ->
                    match Int32.TryParse(v.ToString()) with true, n -> Math.Max(1, Math.Min(n, 250)) | _ -> 50
                | _ -> 50

            let cursorOpt =
                match q.TryGetValue("cursor") with
                | true, v when v.Count > 0 ->
                    match Cursor.decode (v.ToString()) with
                    | Ok c -> Some c
                    | Error _ -> None
                | _ -> None

            // Validation: from/to required when no accountId
            match accountIdOpt, fromOpt, toOpt with
            | None, None, _ | None, _, None ->
                ctx.Response.StatusCode <- 400
                do! Response.ofJson {| error = "from and to are required when accountId is not provided" |} ctx
            | _ ->
                let filter: TransactionListFilter = {
                    AccountId = accountIdOpt
                    From = fromOpt
                    To = toOpt
                    Status = statusOpt
                    Limit = limit
                    Cursor = cursorOpt
                }

                let! txns = repo.ListAsync(filter)
                let hasMore = txns.Length > limit
                let page = if hasMore then txns |> List.take limit else txns
                let nextCursor =
                    if hasMore && not page.IsEmpty then
                        let last = page |> List.last
                        Some (Cursor.encode last.OccurredAt last.Id)
                    else
                        None

                let resp: TransactionListResponse = {
                    items = page |> List.map txnToResponse
                    nextCursor = nextCursor
                }
                do! Response.ofJson resp ctx
        }

    // GET /api/transactions/{id}
    let getTransactionHandler (id: Guid) : HttpHandler = fun ctx ->
        task {
            let repo = ctx.RequestServices.GetRequiredService<ITransactionRepository>()
            let! txnOpt = repo.GetAsync(id)
            match txnOpt with
            | None ->
                ctx.Response.StatusCode <- 404
                do! Response.ofJson {| error = "Transaction not found" |} ctx
            | Some txn ->
                do! Response.ofJson (txnToResponse txn) ctx
        }

    // POST /api/transactions
    let createTransactionHandler : HttpHandler = fun ctx ->
        task {
            let repo = ctx.RequestServices.GetRequiredService<ITransactionRepository>()
            let accountRepo = ctx.RequestServices.GetRequiredService<IAccountRepository>()
            let categoryRepo = ctx.RequestServices.GetRequiredService<ICategoryRepository>()
            let! doc = TransactionJson.readBody ctx
            let req = TransactionJson.deserialize<CreateTransactionRequest> doc

            let accessor = ctx.RequestServices.GetRequiredService<ITenantContextAccessor>()
            match accessor.Context with
            | None ->
                ctx.Response.StatusCode <- 401
                do! Response.ofJson {| error = "Unauthorized" |} ctx
            | Some tc ->
                match! accountRepo.GetAsync(req.accountId) with
                | None ->
                    ctx.Response.StatusCode <- 400
                    do! Response.ofJson {| error = "Account not found" |} ctx
                | Some account ->
                    let currency = req.currency.ToUpperInvariant()
                    if account.CurrencyCode <> currency then
                        ctx.Response.StatusCode <- 400
                        do! Response.ofJson {| error = $"Currency {currency} does not match account currency {account.CurrencyCode}" |} ctx
                    else
                        let! transferValidation =
                            match req.transferAccountId with
                            | None -> Task.FromResult(Ok ())
                            | Some tid ->
                                task {
                                    if tid = req.accountId then
                                        return Error "transferAccountId cannot be the same as accountId"
                                    else
                                        let! transferOpt = accountRepo.GetAsync(tid)
                                        match transferOpt with
                                        | None -> return Error "Transfer account not found"
                                        | Some t ->
                                            if t.CurrencyCode <> currency then
                                                return Error $"Transfer account currency {t.CurrencyCode} does not match transaction currency {currency}"
                                            else
                                                return Ok ()
                                }

                        match transferValidation with
                        | Error msg ->
                            ctx.Response.StatusCode <- 400
                            do! Response.ofJson {| error = msg |} ctx
                        | Ok () ->
                            let! categoryValidation =
                                match req.categoryId with
                                | None -> Task.FromResult(Ok ())
                                | Some cid ->
                                    task {
                                        let! catOpt = categoryRepo.GetAsync(cid)
                                        return if catOpt.IsSome then Ok () else Error "Category not found"
                                    }

                            match categoryValidation with
                            | Error msg ->
                                ctx.Response.StatusCode <- 400
                                do! Response.ofJson {| error = msg |} ctx
                            | Ok () ->
                                let now = DateTimeOffset.UtcNow
                                let txn: Transaction = {
                                    Id = Guid.NewGuid()
                                    TenantId = tc.TenantId
                                    AccountId = req.accountId
                                    OccurredAt = req.occurredAt
                                    PostedAt = req.postedAt
                                    Amount = fromMinor req.amountMinor currency
                                    Description = req.description
                                    Merchant = req.merchant
                                    Memo = None
                                    CategoryId = req.categoryId
                                    Status = TransactionStatus.Cleared
                                    Source = TransactionSource.Manual
                                    ExternalId = None
                                    MatchedTransactionId = None
                                    TransferAccountId = req.transferAccountId
                                    MatchConfidence = None
                                    SyncEventId = None
                                    DeletedAt = None
                                    CreatedAt = now
                                    UpdatedAt = now
                                }
                                let! _ = repo.CreateAsync(txn)
                                ctx.Response.StatusCode <- 201
                                do! Response.ofJson (txnToResponse txn) ctx
        }

    // PATCH /api/transactions/{id}
    let updateTransactionHandler (id: Guid) : HttpHandler = fun ctx ->
        task {
            let repo = ctx.RequestServices.GetRequiredService<ITransactionRepository>()
            let accountRepo = ctx.RequestServices.GetRequiredService<IAccountRepository>()
            let categoryRepo = ctx.RequestServices.GetRequiredService<ICategoryRepository>()
            let! doc = TransactionJson.readBody ctx
            let req = TransactionJson.deserialize<UpdateTransactionRequest> doc

            let! txnOpt = repo.GetAsync(id)
            match txnOpt with
            | None ->
                ctx.Response.StatusCode <- 404
                do! Response.ofJson {| error = "Transaction not found" |} ctx
            | Some txn ->
                // Mutable fields for all transactions
                let updatedDescription = req.description |> Option.defaultValue txn.Description
                let updatedMerchant = req.merchant |> Option.orElse txn.Merchant
                let updatedCategoryId = req.categoryId |> Option.orElse txn.CategoryId
                let updatedMemo = req.notes |> Option.orElse txn.Memo

                // Amount/dates: mutable only for manual entries within 30-day window
                let isManual = match txn.Source with TransactionSource.Manual -> true | _ -> false
                let mutable updatedAmount = txn.Amount
                let mutable updatedOccurredAt = txn.OccurredAt
                let mutable updatedPostedAt = txn.PostedAt
                let mutable editWindowError = None

                if isManual then
                    if req.amountMinor.IsSome || req.occurredAt.IsSome || req.postedAt.IsSome then
                        if not (isWithinEditWindow txn) then
                            editWindowError <- Some "Manual entry is outside the 30-day edit window"
                        else
                            match req.amountMinor with
                            | Some minor -> updatedAmount <- fromMinor minor txn.Amount.CurrencyCode
                            | None -> ()
                            match req.occurredAt with
                            | Some dt -> updatedOccurredAt <- dt
                            | None -> ()
                            match req.postedAt with
                            | Some dt -> updatedPostedAt <- Some dt
                            | None -> ()
                else
                    if req.amountMinor.IsSome || req.occurredAt.IsSome || req.postedAt.IsSome then
                        editWindowError <- Some "Amount and dates are immutable for feed-sourced transactions"

                match editWindowError with
                | Some msg ->
                    ctx.Response.StatusCode <- 422
                    do! Response.ofJson {| error = msg |} ctx
                | None ->
                    // Validate categoryId if changing
                    let! categoryValidation =
                        match req.categoryId with
                        | None -> Task.FromResult(Ok ())
                        | Some cid ->
                            task {
                                let! catOpt = categoryRepo.GetAsync(cid)
                                return if catOpt.IsSome then Ok () else Error "Category not found"
                            }

                    match categoryValidation with
                    | Error msg ->
                        ctx.Response.StatusCode <- 400
                        do! Response.ofJson {| error = msg |} ctx
                    | Ok () ->
                        let updated = {
                            txn with
                                Description = updatedDescription
                                Merchant = updatedMerchant
                                CategoryId = updatedCategoryId
                                Memo = updatedMemo
                                Amount = updatedAmount
                                OccurredAt = updatedOccurredAt
                                PostedAt = updatedPostedAt
                                UpdatedAt = DateTimeOffset.UtcNow
                        }
                        do! repo.UpdateAsync(updated)
                        do! Response.ofJson (txnToResponse updated) ctx
        }

    // DELETE /api/transactions/{id}
    let deleteTransactionHandler (id: Guid) : HttpHandler = fun ctx ->
        task {
            let repo = ctx.RequestServices.GetRequiredService<ITransactionRepository>()
            let! txnOpt = repo.GetAsync(id)
            match txnOpt with
            | None ->
                ctx.Response.StatusCode <- 404
                do! Response.ofJson {| error = "Transaction not found" |} ctx
            | Some _ ->
                do! repo.DeleteAsync(id)
                ctx.Response.StatusCode <- 204
                do! Response.ofEmpty ctx
        }
