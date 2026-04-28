namespace BitThicket.Steward.Api

open System
open System.IO
open System.Text.Json
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Falco
open Npgsql
open BitThicket.Steward.Api.Domain

// ── Request / response DTOs ────────────────────────────────────────────────

type CreateSplitRequest = {
    amountMinor: int64
    currency: string
    categoryId: Guid option
    description: string option
    memo: string option
    sortOrder: int
}

type UpdateSplitRequest = {
    amountMinor: int64 option
    currency: string option
    categoryId: Guid option
    description: string option
    memo: string option
    sortOrder: int option
}

type SplitResponse = {
    id: Guid
    transactionId: Guid
    amount: decimal
    currency: string
    categoryId: Guid option
    description: string option
    memo: string option
    source: string
    sortOrder: int
    createdAt: DateTimeOffset
    updatedAt: DateTimeOffset
}

// ── JSON helpers ───────────────────────────────────────────────────────────

module private SplitJson =
    let jsonOptions =
        let opts = JsonSerializerOptions()
        opts.PropertyNamingPolicy <- JsonNamingPolicy.CamelCase
        opts

    let readBody (ctx: HttpContext) =
        task {
            use reader = new StreamReader(ctx.Request.Body, System.Text.Encoding.UTF8)
            let! json = reader.ReadToEndAsync()
            return JsonDocument.Parse(json)
        }

    let deserialize<'T> (doc: JsonDocument) =
        JsonSerializer.Deserialize<'T>(doc, jsonOptions)

// ── Domain helpers ─────────────────────────────────────────────────────────

module private SplitHelpers =
    let decimalPlaces (currencyCode: string) : int =
        match currencyCode.ToUpperInvariant() with
        | "BTC" -> 8
        | _ -> 2

    let fromMinor (minor: int64) (currencyCode: string) : Money =
        let places = decimalPlaces currencyCode
        let factor = pown 10m places
        { Amount = decimal minor / factor; CurrencyCode = currencyCode }

    let toMinor (money: Money) : int64 =
        let places = decimalPlaces money.CurrencyCode
        let factor = pown 10m places
        int64 (Decimal.Round(money.Amount * factor))

    let sourceToString (source: SplitSource) : string =
        match source with
        | SplitSource.Manual -> "manual"
        | SplitSource.Receipt _ -> "receipt"
        | SplitSource.Enrichment _ -> "enrichment"

    let splitToResponse (split: TransactionSplit) : SplitResponse =
        {
            id = split.Id
            transactionId = split.TransactionId
            amount = split.Amount.Amount
            currency = split.Amount.CurrencyCode
            categoryId = split.CategoryId
            description = split.Description
            memo = split.Memo
            source = sourceToString split.Source
            sortOrder = split.SortOrder
            createdAt = split.CreatedAt
            updatedAt = split.UpdatedAt
        }

// ── Endpoints ──────────────────────────────────────────────────────────────

module SplitEndpoints =
    open SplitHelpers

    // GET /api/transactions/{txnId}/splits
    let listSplitsHandler (txnId: Guid) : HttpHandler = fun ctx ->
        task {
            let splitRepo = ctx.RequestServices.GetRequiredService<ISplitRepository>()
            let txnRepo = ctx.RequestServices.GetRequiredService<ITransactionRepository>()
            let! txnOpt = txnRepo.GetAsync(txnId)
            match txnOpt with
            | None ->
                ctx.Response.StatusCode <- 404
                do! Response.ofJson {| error = "Transaction not found" |} ctx
            | Some _ ->
                let! splits = splitRepo.ListByTransactionAsync(txnId)
                do! Response.ofJson {| splits = splits |> List.map splitToResponse |} ctx
        }

    // POST /api/transactions/{txnId}/splits
    let createSplitHandler (txnId: Guid) : HttpHandler = fun ctx ->
        task {
            let splitRepo = ctx.RequestServices.GetRequiredService<ISplitRepository>()
            let txnRepo = ctx.RequestServices.GetRequiredService<ITransactionRepository>()
            let categoryRepo = ctx.RequestServices.GetRequiredService<ICategoryRepository>()
            let! doc = SplitJson.readBody ctx
            let req = SplitJson.deserialize<CreateSplitRequest> doc

            let accessor = ctx.RequestServices.GetRequiredService<ITenantContextAccessor>()
            match accessor.Context with
            | None ->
                ctx.Response.StatusCode <- 401
                do! Response.ofJson {| error = "Unauthorized" |} ctx
            | Some tc ->
                let! txnOpt = txnRepo.GetAsync(txnId)
                match txnOpt with
                | None ->
                    ctx.Response.StatusCode <- 404
                    do! Response.ofJson {| error = "Transaction not found" |} ctx
                | Some txn ->
                    let currency = req.currency.ToUpperInvariant()
                    if txn.Amount.CurrencyCode <> currency then
                        ctx.Response.StatusCode <- 400
                        do! Response.ofJson {| error = $"Currency {currency} does not match transaction currency {txn.Amount.CurrencyCode}" |} ctx
                    else
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
                            let split: TransactionSplit = {
                                Id = Guid.NewGuid()
                                TenantId = tc.TenantId
                                TransactionId = txnId
                                Amount = fromMinor req.amountMinor currency
                                CategoryId = req.categoryId
                                Description = req.description
                                Memo = req.memo
                                Source = SplitSource.Manual
                                SortOrder = req.sortOrder
                                CreatedAt = now
                                UpdatedAt = now
                            }
                            try
                                let! _ = splitRepo.CreateAsync(split)
                                ctx.Response.StatusCode <- 201
                                do! Response.ofJson (splitToResponse split) ctx
                            with
                            | :? PostgresException as ex when ex.SqlState = "P0001" ->
                                ctx.Response.StatusCode <- 400
                                do! Response.ofJson {| error = ex.Message |} ctx
        }

    // PATCH /api/transactions/{txnId}/splits/{splitId}
    let updateSplitHandler (txnId: Guid) (splitId: Guid) : HttpHandler = fun ctx ->
        task {
            let splitRepo = ctx.RequestServices.GetRequiredService<ISplitRepository>()
            let txnRepo = ctx.RequestServices.GetRequiredService<ITransactionRepository>()
            let categoryRepo = ctx.RequestServices.GetRequiredService<ICategoryRepository>()
            let! doc = SplitJson.readBody ctx
            let root = doc.RootElement

            // Helpers to distinguish absent fields from explicit null
            let tryGetGuidOpt (name: string) =
                match root.TryGetProperty(name) with
                | true, p when p.ValueKind = JsonValueKind.Null -> Some None
                | true, p -> match Guid.TryParse(p.GetString()) with true, g -> Some (Some g) | _ -> None
                | _ -> None

            let tryGetStringOpt (name: string) =
                match root.TryGetProperty(name) with
                | true, p when p.ValueKind = JsonValueKind.Null -> Some None
                | true, p -> Some (Some (p.GetString()))
                | _ -> None

            let tryGetInt64Opt (name: string) =
                match root.TryGetProperty(name) with
                | true, p when p.ValueKind = JsonValueKind.Null -> Some None
                | true, p when p.ValueKind = JsonValueKind.Number -> Some (Some (p.GetInt64()))
                | _ -> None

            let tryGetIntOpt (name: string) =
                match root.TryGetProperty(name) with
                | true, p when p.ValueKind = JsonValueKind.Null -> Some None
                | true, p when p.ValueKind = JsonValueKind.Number -> Some (Some (p.GetInt32()))
                | _ -> None

            let currencyOpt = tryGetStringOpt "currency"
            let categoryIdOpt = tryGetGuidOpt "categoryId"
            let descriptionOpt = tryGetStringOpt "description"
            let memoOpt = tryGetStringOpt "memo"
            let amountMinorOpt = tryGetInt64Opt "amountMinor"
            let sortOrderOpt = tryGetIntOpt "sortOrder"

            let! txnOpt = txnRepo.GetAsync(txnId)
            match txnOpt with
            | None ->
                ctx.Response.StatusCode <- 404
                do! Response.ofJson {| error = "Transaction not found" |} ctx
            | Some txn ->
                let! splitOpt = splitRepo.GetAsync(splitId)
                match splitOpt with
                | None ->
                    ctx.Response.StatusCode <- 404
                    do! Response.ofJson {| error = "Split not found" |} ctx
                | Some split when split.TransactionId <> txnId ->
                    ctx.Response.StatusCode <- 404
                    do! Response.ofJson {| error = "Split not found" |} ctx
                | Some split ->
                    let updatedCurrency = currencyOpt |> Option.defaultValue (Some split.Amount.CurrencyCode) |> Option.defaultValue split.Amount.CurrencyCode
                    if txn.Amount.CurrencyCode <> updatedCurrency then
                        ctx.Response.StatusCode <- 400
                        do! Response.ofJson {| error = $"Currency {updatedCurrency} does not match transaction currency {txn.Amount.CurrencyCode}" |} ctx
                    else
                        let! categoryValidation =
                            match categoryIdOpt with
                            | Some (Some cid) ->
                                task {
                                    let! catOpt = categoryRepo.GetAsync(cid)
                                    return if catOpt.IsSome then Ok () else Error "Category not found"
                                }
                            | _ -> Task.FromResult(Ok ())
                        match categoryValidation with
                        | Error msg ->
                            ctx.Response.StatusCode <- 400
                            do! Response.ofJson {| error = msg |} ctx
                        | Ok () ->
                            let updatedAmount =
                                match amountMinorOpt with
                                | Some (Some minor) -> fromMinor minor updatedCurrency
                                | _ -> split.Amount
                            let updated = {
                                split with
                                    Amount = updatedAmount
                                    CategoryId = categoryIdOpt |> Option.defaultValue split.CategoryId
                                    Description = descriptionOpt |> Option.defaultValue split.Description
                                    Memo = memoOpt |> Option.defaultValue split.Memo
                                    SortOrder = sortOrderOpt |> Option.defaultValue (Some split.SortOrder) |> Option.defaultValue split.SortOrder
                                    UpdatedAt = DateTimeOffset.UtcNow
                            }
                            try
                                do! splitRepo.UpdateAsync(updated)
                                do! Response.ofJson (splitToResponse updated) ctx
                            with
                            | :? PostgresException as ex when ex.SqlState = "P0001" ->
                                ctx.Response.StatusCode <- 400
                                do! Response.ofJson {| error = ex.Message |} ctx
        }

    // DELETE /api/transactions/{txnId}/splits/{splitId}
    let deleteSplitHandler (txnId: Guid) (splitId: Guid) : HttpHandler = fun ctx ->
        task {
            let splitRepo = ctx.RequestServices.GetRequiredService<ISplitRepository>()
            let txnRepo = ctx.RequestServices.GetRequiredService<ITransactionRepository>()
            let! txnOpt = txnRepo.GetAsync(txnId)
            match txnOpt with
            | None ->
                ctx.Response.StatusCode <- 404
                do! Response.ofJson {| error = "Transaction not found" |} ctx
            | Some _ ->
                let! splitOpt = splitRepo.GetAsync(splitId)
                match splitOpt with
                | None ->
                    ctx.Response.StatusCode <- 404
                    do! Response.ofJson {| error = "Split not found" |} ctx
                | Some split when split.TransactionId <> txnId ->
                    ctx.Response.StatusCode <- 404
                    do! Response.ofJson {| error = "Split not found" |} ctx
                | Some _ ->
                    do! splitRepo.DeleteAsync(splitId)
                    ctx.Response.StatusCode <- 204
                    do! Response.ofEmpty ctx
        }
