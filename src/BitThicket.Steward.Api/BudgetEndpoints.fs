namespace BitThicket.Steward.Api

open System
open System.IO
open System.Text.Json
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Falco
open BitThicket.Steward.Api.Domain
open BitThicket.Steward.Pricing

// ── Request / response DTOs ────────────────────────────────────────────────

type CreateBudgetRequest = {
    name: string
    period: string
    currency: string
    style: string
    income: decimal option
    startsOn: string option
}

type BudgetResponse = {
    id: Guid
    name: string
    period: string
    currency: string
    style: string
    incomeMinor: int64
    isActive: bool
    startsOn: string
    currentPeriod: BudgetPeriodResponse option
}

and BudgetPeriodResponse = {
    id: Guid
    startDate: string
    endDate: string
    status: string
    allocations: BudgetPeriodCategoryResponse list
}

and BudgetPeriodCategoryResponse = {
    categoryId: Guid
    allocatedMinor: int64
    openingBalanceMinor: int64
    rolloverBalanceMinor: int64
    currency: string
    rolloverEnabled: bool
}

type CreatePeriodRequest = {
    startDate: string
    allocations: PeriodAllocationRequest list
}

and PeriodAllocationRequest = {
    categoryId: Guid
    amountMinor: int64
}

type UpdateAllocationRequest = {
    amountMinor: int64
    rolloverEnabled: bool option
}

// ── Report DTOs ────────────────────────────────────────────────────────────

type BudgetReportTotals = {
    allocatedMinor: int64
    spentMinor: int64
    remainingMinor: int64
    currency: string
}

type BudgetReportCategoryItem = {
    categoryId: Guid
    name: string
    allocatedMinor: int64
    spentMinor: int64
    remainingMinor: int64
    rolloverBalanceMinor: int64
    percentUsed: decimal
    currency: string
}

type BudgetReportResponse = {
    periodId: Guid
    totals: BudgetReportTotals
    byCategory: BudgetReportCategoryItem list
    displayCurrency: string
}

// ── JSON helpers ───────────────────────────────────────────────────────────

module private BudgetJson =
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

module private BudgetHelpers =
    let parseBudgetPeriod (s: string) : BudgetPeriod option =
        match s.ToLowerInvariant() with
        | "monthly"   -> Some BudgetPeriod.Monthly
        | "biweekly"  -> Some BudgetPeriod.BiWeekly
        | "weekly"    -> Some BudgetPeriod.Weekly
        | _ -> None

    let budgetPeriodToString (p: BudgetPeriod) : string =
        match p with
        | BudgetPeriod.Monthly   -> "monthly"
        | BudgetPeriod.BiWeekly  -> "biweekly"
        | BudgetPeriod.Weekly    -> "weekly"
        | BudgetPeriod.Custom d  -> $"custom:{d}"

    let parseBudgetingStyle (s: string) : BudgetingStyle option =
        match s.ToLowerInvariant() with
        | "zerobased" | "zero_based" -> Some BudgetingStyle.ZeroBased
        | "envelope"  -> Some BudgetingStyle.Envelope
        | "flexible"  -> Some BudgetingStyle.Flexible
        | "traditionallimits" | "traditional_limits" -> Some BudgetingStyle.TraditionalLimits
        | _ -> None

    let budgetingStyleToString (s: BudgetingStyle) : string =
        match s with
        | BudgetingStyle.ZeroBased        -> "zeroBased"
        | BudgetingStyle.Envelope         -> "envelope"
        | BudgetingStyle.Flexible         -> "flexible"
        | BudgetingStyle.TraditionalLimits -> "traditionalLimits"

    let computePeriodEnd (startDate: DateOnly) (period: BudgetPeriod) : DateOnly =
        match period with
        | BudgetPeriod.Monthly  -> startDate.AddMonths(1).AddDays(-1)
        | BudgetPeriod.BiWeekly -> startDate.AddDays(13)
        | BudgetPeriod.Weekly   -> startDate.AddDays(6)
        | BudgetPeriod.Custom d -> startDate.AddDays(d - 1)

    let moneyFromMinor (minor: int64) (currency: string) : Money =
        MoneyHelpers.fromMinorUnits minor currency

    let toMinor (money: Money) : int64 =
        MoneyHelpers.toMinorUnits money

    let validateAllocations (style: BudgetingStyle) (income: Money) (allocations: BudgetPeriodCategoryAllocation list) : Result<unit, string> =
        let totalAllocated =
            allocations
            |> List.map (fun a -> a.AllocatedAmount.Amount)
            |> List.sum
        match style with
        | BudgetingStyle.ZeroBased ->
            if totalAllocated <> income.Amount then
                Error $"Zero-based budget requires allocations to equal income ({income.Amount}). Allocated: {totalAllocated}."
            else Ok ()
        | BudgetingStyle.Envelope ->
            if totalAllocated <> income.Amount then
                Error $"Envelope budget requires allocations to equal income ({income.Amount}). Allocated: {totalAllocated}."
            else Ok ()
        | BudgetingStyle.Flexible ->
            if totalAllocated > income.Amount then
                Error $"Flexible budget requires allocations to be ≤ income ({income.Amount}). Allocated: {totalAllocated}."
            else Ok ()
        | BudgetingStyle.TraditionalLimits ->
            Ok ()

    let budgetToResponse (budget: Budget) (currentPeriod: BudgetPeriodResponse option) : BudgetResponse =
        {
            id = budget.Id
            name = budget.Name
            period = budgetPeriodToString budget.Period
            currency = budget.CurrencyCode
            style = budgetingStyleToString budget.Style
            incomeMinor = toMinor budget.Income
            isActive = budget.IsActive
            startsOn = budget.StartsOn.ToString("yyyy-MM-dd")
            currentPeriod = currentPeriod
        }

    let periodToResponse (period: BudgetPeriodRecord) (allocs: BudgetPeriodCategoryAllocation list) : BudgetPeriodResponse =
        {
            id = period.Id
            startDate = period.StartDate.ToString("yyyy-MM-dd")
            endDate = period.EndDate.ToString("yyyy-MM-dd")
            status = match period.Status with BudgetPeriodStatus.Open -> "Open" | BudgetPeriodStatus.Closed -> "Closed"
            allocations =
                allocs
                |> List.map (fun a -> {
                    categoryId = a.CategoryId
                    allocatedMinor = toMinor a.AllocatedAmount
                    openingBalanceMinor = toMinor a.OpeningBalance
                    rolloverBalanceMinor = toMinor a.RolloverBalance
                    currency = a.AllocatedAmount.CurrencyCode
                    rolloverEnabled = a.RolloverEnabled
                })
        }

// ── Endpoints ──────────────────────────────────────────────────────────────

module BudgetEndpoints =
    open BudgetHelpers

    // GET /api/budgets
    let listBudgetsHandler : HttpHandler = fun ctx ->
        task {
            let budgetRepo = ctx.RequestServices.GetRequiredService<IBudgetRepository>()
            let periodRepo = ctx.RequestServices.GetRequiredService<IBudgetPeriodRepository>()
            let! budgets = budgetRepo.ListAsync()
            let! responses =
                budgets
                |> List.map (fun budget ->
                    task {
                        let! openPeriodOpt = periodRepo.GetOpenPeriodAsync(budget.Id)
                        let! currentPeriodResp =
                            match openPeriodOpt with
                            | None -> Task.FromResult None
                            | Some period ->
                                task {
                                    let! allocs = periodRepo.ListAllocationsByPeriodAsync(period.Id)
                                    return Some (periodToResponse period allocs)
                                }
                        return budgetToResponse budget currentPeriodResp
                    })
                |> Task.WhenAll
            do! Response.ofJson {| budgets = responses |> Array.toList |} ctx
        }

    // POST /api/budgets
    let createBudgetHandler : HttpHandler = fun ctx ->
        task {
            let budgetRepo = ctx.RequestServices.GetRequiredService<IBudgetRepository>()
            let! doc = BudgetJson.readBody ctx
            let req = BudgetJson.deserialize<CreateBudgetRequest> doc

            let periodOpt = parseBudgetPeriod req.period
            let styleOpt = parseBudgetingStyle req.style
            let startsOnOpt =
                match req.startsOn with
                | Some s ->
                    match DateOnly.TryParse(s) with
                    | true, d -> Some d
                    | _ -> None
                | None -> Some (DateOnly.FromDateTime(DateTime.UtcNow))

            match periodOpt, styleOpt, startsOnOpt with
            | None, _, _ ->
                ctx.Response.StatusCode <- 400
                do! Response.ofJson {| error = "Invalid period. Use: monthly, biweekly, weekly" |} ctx
            | _, None, _ ->
                ctx.Response.StatusCode <- 400
                do! Response.ofJson {| error = "Invalid style. Use: zeroBased, envelope, flexible, traditionalLimits" |} ctx
            | _, _, None ->
                ctx.Response.StatusCode <- 400
                do! Response.ofJson {| error = "Invalid startsOn date format. Use: yyyy-MM-dd" |} ctx
            | Some period, Some style, Some startsOn ->
                let currency = req.currency |> Option.ofObj |> Option.defaultValue "USD"
                let income = req.income |> Option.defaultValue 0m
                let budget = {
                    Id = Guid.NewGuid()
                    TenantId =
                        let accessor = ctx.RequestServices.GetRequiredService<ITenantContextAccessor>()
                        match accessor.Context with Some c -> c.TenantId | None -> Guid.Empty
                    UserId =
                        let accessor = ctx.RequestServices.GetRequiredService<ITenantContextAccessor>()
                        match accessor.Context with Some c -> c.UserId | None -> Guid.Empty
                    Name = req.name
                    Style = style
                    Period = period
                    CurrencyCode = currency
                    Income = { Amount = income; CurrencyCode = currency }
                    IsActive = true
                    StartsOn = startsOn
                    CreatedAt = DateTimeOffset.UtcNow
                    UpdatedAt = DateTimeOffset.UtcNow
                }
                let! id = budgetRepo.CreateAsync(budget)
                let resp = budgetToResponse budget None
                ctx.Response.StatusCode <- 201
                do! Response.ofJson resp ctx
        }

    // GET /api/budgets/{id}
    let getBudgetHandler (budgetId: Guid) : HttpHandler = fun ctx ->
        task {
            let budgetRepo = ctx.RequestServices.GetRequiredService<IBudgetRepository>()
            let periodRepo = ctx.RequestServices.GetRequiredService<IBudgetPeriodRepository>()
            let! budgetOpt = budgetRepo.GetAsync(budgetId)
            match budgetOpt with
            | None ->
                ctx.Response.StatusCode <- 404
                do! Response.ofJson {| error = "Budget not found" |} ctx
            | Some budget ->
                let! openPeriodOpt = periodRepo.GetOpenPeriodAsync(budgetId)
                let! currentPeriodResp =
                    match openPeriodOpt with
                    | None -> Task.FromResult None
                    | Some period ->
                        task {
                            let! allocs = periodRepo.ListAllocationsByPeriodAsync(period.Id)
                            return Some (periodToResponse period allocs)
                        }
                let resp = budgetToResponse budget currentPeriodResp
                do! Response.ofJson resp ctx
        }

    // POST /api/budgets/{id}/periods
    let createPeriodHandler (budgetId: Guid) : HttpHandler = fun ctx ->
        task {
            let budgetRepo = ctx.RequestServices.GetRequiredService<IBudgetRepository>()
            let periodRepo = ctx.RequestServices.GetRequiredService<IBudgetPeriodRepository>()
            let! doc = BudgetJson.readBody ctx
            let req = BudgetJson.deserialize<CreatePeriodRequest> doc

            match DateOnly.TryParse(req.startDate) with
            | false, _ ->
                ctx.Response.StatusCode <- 400
                do! Response.ofJson {| error = "Invalid startDate format. Use: yyyy-MM-dd" |} ctx
            | true, startDate ->
                let! budgetOpt = budgetRepo.GetAsync(budgetId)
                match budgetOpt with
                | None ->
                    ctx.Response.StatusCode <- 404
                    do! Response.ofJson {| error = "Budget not found" |} ctx
                | Some budget ->
                    let! openPeriodOpt = periodRepo.GetOpenPeriodAsync(budgetId)
                    match openPeriodOpt with
                    | Some _ ->
                        ctx.Response.StatusCode <- 409
                        do! Response.ofJson {| error = "Budget already has an open period. Close it first." |} ctx
                    | None ->
                        let endDate = computePeriodEnd startDate budget.Period
                        let period = {
                            Id = Guid.NewGuid()
                            BudgetId = budgetId
                            TenantId = budget.TenantId
                            StartDate = startDate
                            EndDate = endDate
                            Status = BudgetPeriodStatus.Open
                            CreatedAt = DateTimeOffset.UtcNow
                            UpdatedAt = DateTimeOffset.UtcNow
                        }
                        let allocations =
                            req.allocations
                            |> List.map (fun a -> {
                                BudgetPeriodId = period.Id
                                CategoryId = a.categoryId
                                AllocatedAmount = moneyFromMinor a.amountMinor budget.CurrencyCode
                                OpeningBalance = Money.zero budget.CurrencyCode
                                RolloverBalance = Money.zero budget.CurrencyCode
                                RolloverEnabled = false
                            })
                        match validateAllocations budget.Style budget.Income allocations with
                        | Error msg ->
                            ctx.Response.StatusCode <- 422
                            do! Response.ofJson {| error = msg |} ctx
                        | Ok () ->
                            let! id = periodRepo.CreatePeriodAsync(period, allocations)
                            let! allocs = periodRepo.ListAllocationsByPeriodAsync(period.Id)
                            let resp = periodToResponse period allocs
                            ctx.Response.StatusCode <- 201
                            do! Response.ofJson resp ctx
        }

    // GET /api/budgets/{id}/periods/{periodId}
    let getPeriodHandler (budgetId: Guid) (periodId: Guid) : HttpHandler = fun ctx ->
        task {
            let periodRepo = ctx.RequestServices.GetRequiredService<IBudgetPeriodRepository>()
            let! periodOpt = periodRepo.GetPeriodAsync(periodId)
            match periodOpt with
            | None ->
                ctx.Response.StatusCode <- 404
                do! Response.ofJson {| error = "Period not found" |} ctx
            | Some period when period.BudgetId <> budgetId ->
                ctx.Response.StatusCode <- 404
                do! Response.ofJson {| error = "Period not found" |} ctx
            | Some period ->
                let! allocs = periodRepo.ListAllocationsByPeriodAsync(period.Id)
                let resp = periodToResponse period allocs
                do! Response.ofJson resp ctx
        }

    // PATCH /api/budgets/{id}/periods/{periodId}/categories/{categoryId}
    let updateAllocationHandler (budgetId: Guid) (periodId: Guid) (categoryId: Guid) : HttpHandler = fun ctx ->
        task {
            let budgetRepo = ctx.RequestServices.GetRequiredService<IBudgetRepository>()
            let periodRepo = ctx.RequestServices.GetRequiredService<IBudgetPeriodRepository>()
            let! doc = BudgetJson.readBody ctx
            let req = BudgetJson.deserialize<UpdateAllocationRequest> doc

            let! budgetOpt = budgetRepo.GetAsync(budgetId)
            match budgetOpt with
            | None ->
                ctx.Response.StatusCode <- 404
                do! Response.ofJson {| error = "Budget not found" |} ctx
            | Some budget ->
                let! allocOpt = periodRepo.GetAllocationAsync(periodId, categoryId)
                match allocOpt with
                | None ->
                    ctx.Response.StatusCode <- 404
                    do! Response.ofJson {| error = "Allocation not found" |} ctx
                | Some alloc ->
                    let updatedAlloc = {
                        alloc with
                            AllocatedAmount = moneyFromMinor req.amountMinor budget.CurrencyCode
                            RolloverEnabled = req.rolloverEnabled |> Option.defaultValue alloc.RolloverEnabled
                    }
                    let! allAllocs = periodRepo.ListAllocationsByPeriodAsync(periodId)
                    let otherAllocs = allAllocs |> List.filter (fun a -> a.CategoryId <> categoryId)
                    let newAllocs = updatedAlloc :: otherAllocs
                    match validateAllocations budget.Style budget.Income newAllocs with
                    | Error msg ->
                        ctx.Response.StatusCode <- 422
                        do! Response.ofJson {| error = msg |} ctx
                    | Ok () ->
                        do! periodRepo.UpdateAllocationAsync(updatedAlloc)
                        do! Response.ofJson {| success = true |} ctx
        }

    // POST /api/budgets/{id}/periods/{periodId}/close
    let closePeriodHandler (budgetId: Guid) (periodId: Guid) : HttpHandler = fun ctx ->
        task {
            let budgetRepo = ctx.RequestServices.GetRequiredService<IBudgetRepository>()
            let periodRepo = ctx.RequestServices.GetRequiredService<IBudgetPeriodRepository>()
            let categoryRepo = ctx.RequestServices.GetRequiredService<ICategoryRepository>()

            let! budgetOpt = budgetRepo.GetAsync(budgetId)
            match budgetOpt with
            | None ->
                ctx.Response.StatusCode <- 404
                do! Response.ofJson {| error = "Budget not found" |} ctx
            | Some budget ->
                let! periodOpt = periodRepo.GetPeriodAsync(periodId)
                match periodOpt with
                | None ->
                    ctx.Response.StatusCode <- 404
                    do! Response.ofJson {| error = "Period not found" |} ctx
                | Some period when period.BudgetId <> budgetId ->
                    ctx.Response.StatusCode <- 404
                    do! Response.ofJson {| error = "Period not found" |} ctx
                | Some period when period.Status = BudgetPeriodStatus.Closed ->
                    ctx.Response.StatusCode <- 409
                    do! Response.ofJson {| error = "Period is already closed" |} ctx
                | Some period ->
                    // 1. Compute actual spend per category for this period
                    let! actualSpend = periodRepo.GetActualSpendByCategoryAsync(periodId)

                    // 2. Get current allocations and compute rollover
                    let! allocs = periodRepo.ListAllocationsByPeriodAsync(periodId)
                    let! budgetCategories = budgetRepo.ListCategoriesByBudgetAsync(budgetId)

                    let rolloverAllocs =
                        allocs
                        |> List.choose (fun alloc ->
                            let spent = actualSpend |> Map.tryFind alloc.CategoryId |> Option.defaultValue (Money.zero budget.CurrencyCode)
                            let remaining = alloc.AllocatedAmount.Amount - spent.Amount
                            if alloc.RolloverEnabled && remaining > 0m then
                                Some (alloc.CategoryId, remaining)
                            else
                                None
                        )

                    // 3. Close current period
                    do! periodRepo.ClosePeriodAsync(periodId)

                    // 4. Update budget-level rollover balances
                    for pair in rolloverAllocs do
                        let (catId, remainingAmount) = pair
                        match budgetCategories |> List.tryFind (fun bc -> bc.CategoryId = catId) with
                        | Some bc ->
                            let updatedBc = {
                                bc with
                                    RolloverBalance = { bc.RolloverBalance with Amount = bc.RolloverBalance.Amount + remainingAmount }
                            }
                            do! budgetRepo.UpdateCategoryAsync(updatedBc)
                        | None -> ()

                    // 5. Create next period with rollover balances as opening balances
                    let nextStartDate = period.EndDate.AddDays(1)
                    let nextEndDate = computePeriodEnd nextStartDate budget.Period
                    let nextPeriod = {
                        Id = Guid.NewGuid()
                        BudgetId = budgetId
                        TenantId = budget.TenantId
                        StartDate = nextStartDate
                        EndDate = nextEndDate
                        Status = BudgetPeriodStatus.Open
                        CreatedAt = DateTimeOffset.UtcNow
                        UpdatedAt = DateTimeOffset.UtcNow
                    }

                    let nextAllocs =
                        allocs
                        |> List.map (fun alloc ->
                            let openingBalance =
                                match rolloverAllocs |> List.tryFind (fun (cid, _) -> cid = alloc.CategoryId) with
                                | Some (_, amount) -> { Amount = amount; CurrencyCode = budget.CurrencyCode }
                                | None -> Money.zero budget.CurrencyCode
                            {
                                BudgetPeriodId = nextPeriod.Id
                                CategoryId = alloc.CategoryId
                                AllocatedAmount = alloc.AllocatedAmount
                                OpeningBalance = openingBalance
                                RolloverBalance = openingBalance
                                RolloverEnabled = alloc.RolloverEnabled
                            }
                        )

                    let! _ = periodRepo.CreatePeriodAsync(nextPeriod, nextAllocs)

                    let closeResponse =
                        {|
                            closedPeriodId = periodId
                            nextPeriodId = nextPeriod.Id
                            rolloverBalances =
                                rolloverAllocs
                                |> List.map (fun (cid, amount) ->
                                    {| categoryId = cid
                                       rolloverAmountMinor = toMinor { Amount = amount; CurrencyCode = budget.CurrencyCode }
                                       currency = budget.CurrencyCode |})
                        |}
                    do! Response.ofJson closeResponse ctx
        }

    // GET /api/budgets/{id}/periods/{periodId}/report?displayCurrency=USD|BTC
    let getReportHandler (budgetId: Guid) (periodId: Guid) : HttpHandler = fun ctx ->
        task {
            let budgetRepo = ctx.RequestServices.GetRequiredService<IBudgetRepository>()
            let periodRepo = ctx.RequestServices.GetRequiredService<IBudgetPeriodRepository>()
            let categoryRepo = ctx.RequestServices.GetRequiredService<ICategoryRepository>()
            let pricing = ctx.RequestServices.GetRequiredService<IPriceProvider>()

            let displayCurrencyOpt =
                match ctx.Request.Query.TryGetValue("displayCurrency") with
                | true, v when v.Count > 0 -> Some(v.ToString().ToUpperInvariant())
                | _ -> None

            let! budgetOpt = budgetRepo.GetAsync(budgetId)
            match budgetOpt with
            | None ->
                ctx.Response.StatusCode <- 404
                do! Response.ofJson {| error = "Budget not found" |} ctx
            | Some budget ->
                let! periodOpt = periodRepo.GetPeriodAsync(periodId)
                match periodOpt with
                | None ->
                    ctx.Response.StatusCode <- 404
                    do! Response.ofJson {| error = "Period not found" |} ctx
                | Some period when period.BudgetId <> budgetId ->
                    ctx.Response.StatusCode <- 404
                    do! Response.ofJson {| error = "Period not found" |} ctx
                | Some period ->
                    let targetCurrency = displayCurrencyOpt |> Option.defaultValue budget.CurrencyCode

                    let! allocs = periodRepo.ListAllocationsByPeriodAsync(periodId)
                    let! spendDetail = periodRepo.GetPeriodSpendAsync(periodId)
                    let! categories = categoryRepo.ListAsync()

                    let categoryNames =
                        categories |> List.map (fun c -> c.Id, c.Name) |> Map.ofList

                    // Convert each currency amount to the target display currency using the latest spot rate
                    let! convertedSpend =
                        spendDetail
                        |> List.map (fun (catId, money) ->
                            task {
                                if money.CurrencyCode = targetCurrency then
                                    return (catId, money.Amount)
                                else
                                    let! rate = pricing.GetSpotAsync(money.CurrencyCode, targetCurrency)
                                    return (catId, money.Amount * rate.Value)
                            })
                        |> Task.WhenAll

                    // Group by category and sum
                    let spendByCategory =
                        convertedSpend
                        |> Array.toList
                        |> List.groupBy fst
                        |> List.map (fun (catId, items) -> catId, items |> List.sumBy snd)
                        |> Map.ofList

                    let reportItems =
                        allocs
                        |> List.map (fun alloc ->
                            let spentSigned = spendByCategory |> Map.tryFind alloc.CategoryId |> Option.defaultValue 0m
                            let allocated = alloc.AllocatedAmount.Amount
                            let spentDisplay = -spentSigned
                            let remaining = allocated + spentSigned
                            let rollover = alloc.RolloverBalance.Amount
                            let percentUsed =
                                if allocated <> 0m then
                                    Decimal.Round(Math.Min(100m, Math.Max(0m, -spentSigned / allocated * 100m)), 2)
                                else 0m

                            {
                                categoryId = alloc.CategoryId
                                name = categoryNames |> Map.tryFind alloc.CategoryId |> Option.defaultValue "Unknown"
                                allocatedMinor = toMinor alloc.AllocatedAmount
                                spentMinor = toMinor { Amount = spentDisplay; CurrencyCode = targetCurrency }
                                remainingMinor = toMinor { Amount = remaining; CurrencyCode = targetCurrency }
                                rolloverBalanceMinor = toMinor { Amount = rollover; CurrencyCode = targetCurrency }
                                percentUsed = percentUsed
                                currency = targetCurrency
                            }
                        )

                    let totalAllocated = reportItems |> List.sumBy (fun i -> i.allocatedMinor)
                    let totalSpent = reportItems |> List.sumBy (fun i -> i.spentMinor)
                    let totalRemaining = reportItems |> List.sumBy (fun i -> i.remainingMinor)

                    let resp = {
                        periodId = periodId
                        totals = {
                            allocatedMinor = totalAllocated
                            spentMinor = totalSpent
                            remainingMinor = totalRemaining
                            currency = targetCurrency
                        }
                        byCategory = reportItems
                        displayCurrency = targetCurrency
                    }

                    do! Response.ofJson resp ctx
        }

    // GET /api/budgets/{id}/periods/current/report?displayCurrency=USD|BTC
    let getCurrentReportHandler (budgetId: Guid) : HttpHandler = fun ctx ->
        task {
            let budgetRepo = ctx.RequestServices.GetRequiredService<IBudgetRepository>()
            let periodRepo = ctx.RequestServices.GetRequiredService<IBudgetPeriodRepository>()

            let! budgetOpt = budgetRepo.GetAsync(budgetId)
            match budgetOpt with
            | None ->
                ctx.Response.StatusCode <- 404
                do! Response.ofJson {| error = "Budget not found" |} ctx
            | Some _ ->
                let! openPeriodOpt = periodRepo.GetOpenPeriodAsync(budgetId)
                match openPeriodOpt with
                | None ->
                    ctx.Response.StatusCode <- 404
                    do! Response.ofJson {| error = "No open period for this budget" |} ctx
                | Some period ->
                    do! getReportHandler budgetId period.Id ctx
        }
