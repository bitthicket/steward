namespace BitThicket.Steward.Api

open System
open System.Data.Common
open System.IO
open System.Text
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Falco
open BitThicket.Steward.Api.Domain

// ── CSV helpers ────────────────────────────────────────────────────────────

module private Csv =
    let escape (value: string) : string =
        if String.IsNullOrEmpty(value) then
            ""
        elif value.Contains(",") || value.Contains("\"") || value.Contains("\n") || value.Contains("\r") then
            let escaped = value.Replace("\"", "\"\"")
            $"\"{escaped}\""
        else
            value

    let writeRow (writer: StreamWriter) (values: string list) =
        let line = values |> List.map escape |> String.concat ","
        writer.WriteLine(line)

    let writeHeader (writer: StreamWriter) (columns: string list) =
        writeRow writer columns

// ── Category path builder ──────────────────────────────────────────────────

module private CategoryPath =
    let buildPathMap (categories: Category list) : Map<Guid, string> =
        let catMap = categories |> List.map (fun c -> c.Id, c) |> Map.ofList

        let rec getPath (id: Guid) : string =
            match catMap.TryFind id with
            | None -> ""
            | Some cat ->
                match cat.ParentCategoryId with
                | None -> cat.Name
                | Some pid ->
                    let parentPath = getPath pid
                    if String.IsNullOrEmpty(parentPath) then cat.Name
                    else $"{parentPath} > {cat.Name}"

        categories
        |> List.map (fun c -> c.Id, getPath c.Id)
        |> Map.ofList

// ── Transaction export ─────────────────────────────────────────────────────

module private TransactionExport =
    let exportTransactionsCsv (ctx: HttpContext) =
        task {
            let accessor = ctx.RequestServices.GetRequiredService<ITenantContextAccessor>()
            match accessor.Context with
            | None ->
                ctx.Response.StatusCode <- 401
                do! Response.ofJson {| error = "Unauthorized" |} ctx
            | Some tenantContext ->
                try
                    let factory = ctx.RequestServices.GetRequiredService<IDbConnectionFactory>()
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

                    // Validation: from/to required when no accountId
                    match accountIdOpt, fromOpt, toOpt with
                    | None, None, _ | None, _, None ->
                        ctx.Response.StatusCode <- 400
                        do! Response.ofJson {| error = "from and to are required when accountId is not provided" |} ctx
                    | _ ->
                        ctx.Response.ContentType <- "text/csv; charset=utf-8"
                        ctx.Response.Headers.["Content-Disposition"] <- "attachment; filename=\"transactions.csv\""

                        use! conn = factory.OpenForTenantAsync(tenantContext)

                        // Load categories for path resolution
                        use catCmd = conn.CreateCommand()
                        catCmd.CommandText <-
                            """SELECT id, tenant_id, user_id, name, parent_id, is_system,
                                      currency, rollover_enabled, deleted_at, created_at, updated_at
                               FROM categories
                               WHERE tenant_id = $1 AND deleted_at IS NULL"""
                        catCmd.Parameters.AddWithValue("$1", tenantContext.TenantId) |> ignore
                        let! catReader = catCmd.ExecuteReaderAsync(ctx.RequestAborted)
                        use catReader = catReader
                        let categories = ResizeArray<Category>()
                        while! catReader.ReadAsync(ctx.RequestAborted) do
                            categories.Add({
                                Id = catReader.GetGuid(0)
                                TenantId = catReader.GetGuid(1)
                                UserId = catReader.GetGuid(2)
                                Name = catReader.GetString(3)
                                ParentCategoryId = Sql.nullableGuid catReader 4
                                IsSystem = catReader.GetBoolean(5)
                                CurrencyCode = catReader.GetString(6)
                                RolloverEnabled = catReader.GetBoolean(7)
                                DeletedAt = Sql.nullableDateTimeOffset catReader 8
                                CreatedAt = Sql.dateTimeOffset catReader 9
                                UpdatedAt = Sql.dateTimeOffset catReader 10
                            })
                        let pathMap = CategoryPath.buildPathMap (categories |> Seq.toList)

                        // Build transaction query
                        use txnCmd = conn.CreateCommand()
                        let conditions = ResizeArray<string>()
                        conditions.Add("t.deleted_at IS NULL")

                        match accountIdOpt with
                        | Some aid ->
                            conditions.Add("t.account_id = $1")
                            txnCmd.Parameters.AddWithValue("$1", aid) |> ignore
                        | None -> ()

                        let mutable paramIndex = 2

                        match fromOpt with
                        | Some f ->
                            conditions.Add($"t.occurred_at >= ${paramIndex}")
                            txnCmd.Parameters.AddWithValue($"${paramIndex}", f.UtcDateTime) |> ignore
                            paramIndex <- paramIndex + 1
                        | None -> ()

                        match toOpt with
                        | Some t ->
                            conditions.Add($"t.occurred_at <= ${paramIndex}")
                            txnCmd.Parameters.AddWithValue($"${paramIndex}", t.UtcDateTime) |> ignore
                            paramIndex <- paramIndex + 1
                        | None -> ()

                        let whereClause = String.concat " AND " conditions

                        txnCmd.CommandText <-
                            $"""SELECT
                                   t.id,
                                   t.occurred_at,
                                   t.posted_at,
                                   a.name AS account_name,
                                   t.amount_minor,
                                   t.currency,
                                   t.description,
                                   t.merchant,
                                   t.category_id,
                                   t.status,
                                   t.source,
                                   t.external_id
                                FROM transactions t
                                JOIN accounts a ON a.id = t.account_id AND a.deleted_at IS NULL
                                WHERE {whereClause}
                                ORDER BY t.occurred_at DESC, t.id DESC"""

                        let! txnReader = txnCmd.ExecuteReaderAsync(ctx.RequestAborted)
                        use txnReader = txnReader

                        use writer = new StreamWriter(ctx.Response.Body, Encoding.UTF8, leaveOpen = true)

                        Csv.writeHeader writer [
                            "id"
                            "occurred_at"
                            "posted_at"
                            "account_name"
                            "amount_minor"
                            "currency"
                            "description"
                            "merchant"
                            "category_path"
                            "status"
                            "source"
                            "external_id"
                        ]

                        let mutable rowCount = 0
                        while! txnReader.ReadAsync(ctx.RequestAborted) do
                            let id = txnReader.GetGuid(0)
                            let occurredAt = Sql.dateTimeOffset txnReader 1
                            let postedAt = Sql.nullableDateTimeOffset txnReader 2
                            let accountName = txnReader.GetString(3)
                            let amountMinor = txnReader.GetInt64(4)
                            let currency = txnReader.GetString(5)
                            let description = Sql.nullableString txnReader 6 |> Option.defaultValue ""
                            let merchant = Sql.nullableString txnReader 7 |> Option.defaultValue ""
                            let categoryId = Sql.nullableGuid txnReader 8
                            let status = txnReader.GetString(9)
                            let sourceJson = txnReader.GetString(10)
                            let externalId = Sql.nullableString txnReader 11 |> Option.defaultValue ""

                            let categoryPath =
                                match categoryId with
                                | Some cid -> pathMap |> Map.tryFind cid |> Option.defaultValue ""
                                | None -> ""

                            let sourceStr =
                                try
                                    use doc = System.Text.Json.JsonDocument.Parse(sourceJson)
                                    let root = doc.RootElement
                                    match root.GetProperty("type").GetString() with
                                    | "manual" -> "manual"
                                    | "data_feed" ->
                                        let provider = root.GetProperty("provider").GetString()
                                        "dataFeed:" + provider
                                    | "import" ->
                                        let format = root.GetProperty("format").GetString()
                                        "import:" + format
                                    | _ -> sourceJson
                                with _ -> sourceJson

                            Csv.writeRow writer [
                                id.ToString()
                                occurredAt.ToString("yyyy-MM-dd HH:mm:ss")
                                match postedAt with Some d -> d.ToString("yyyy-MM-dd HH:mm:ss") | None -> ""
                                accountName
                                amountMinor.ToString()
                                currency
                                description
                                merchant
                                categoryPath
                                status
                                sourceStr
                                externalId
                            ]

                            rowCount <- rowCount + 1
                            if rowCount % 1000 = 0 then
                                do! writer.FlushAsync()

                        do! writer.FlushAsync()
                with ex ->
                    if not ctx.Response.HasStarted then
                        ctx.Response.StatusCode <- 500
                        do! Response.ofJson {| error = "Export failed"; detail = ex.Message |} ctx
                    else
                        raise ex
        }

// ── Account export ─────────────────────────────────────────────────────────

module private AccountExport =
    let exportAccountsCsv (ctx: HttpContext) =
        task {
            let accessor = ctx.RequestServices.GetRequiredService<ITenantContextAccessor>()
            match accessor.Context with
            | None ->
                ctx.Response.StatusCode <- 401
                do! Response.ofJson {| error = "Unauthorized" |} ctx
            | Some tenantContext ->
                try
                    let factory = ctx.RequestServices.GetRequiredService<IDbConnectionFactory>()

                    ctx.Response.ContentType <- "text/csv; charset=utf-8"
                    ctx.Response.Headers.["Content-Disposition"] <- "attachment; filename=\"accounts.csv\""

                    use! conn = factory.OpenForTenantAsync(tenantContext)
                    use cmd = conn.CreateCommand()
                    cmd.CommandText <-
                        """SELECT
                               a.id,
                               a.name,
                               a.account_type,
                               a.currency,
                               a.institution_name,
                               a.is_on_budget,
                               a.is_active,
                               COALESCE(SUM(t.amount_minor) FILTER (WHERE t.posted_at IS NOT NULL AND t.status IN ('cleared', 'reconciled')), 0) AS posted_balance_minor,
                               COALESCE(SUM(t.amount_minor) FILTER (WHERE t.status = 'pending'), 0) AS pending_balance_minor
                           FROM accounts a
                           LEFT JOIN transactions t ON t.account_id = a.id AND t.deleted_at IS NULL
                           WHERE a.deleted_at IS NULL
                           GROUP BY a.id, a.name, a.account_type, a.currency, a.institution_name, a.is_on_budget, a.is_active
                           ORDER BY a.name"""

                    let! reader = cmd.ExecuteReaderAsync(ctx.RequestAborted)
                    use reader = reader

                    use writer = new StreamWriter(ctx.Response.Body, Encoding.UTF8, leaveOpen = true)

                    Csv.writeHeader writer [
                        "id"
                        "name"
                        "account_type"
                        "currency"
                        "institution_name"
                        "is_on_budget"
                        "is_active"
                        "posted_balance_minor"
                        "pending_balance_minor"
                    ]

                    let mutable rowCount = 0
                    while! reader.ReadAsync(ctx.RequestAborted) do
                        let id = reader.GetGuid(0)
                        let name = reader.GetString(1)
                        let accountType = reader.GetString(2)
                        let currency = reader.GetString(3)
                        let institutionName = Sql.nullableString reader 4 |> Option.defaultValue ""
                        let isOnBudget = reader.GetBoolean(5)
                        let isActive = reader.GetBoolean(6)
                        let postedBalance = reader.GetInt64(7)
                        let pendingBalance = reader.GetInt64(8)

                        Csv.writeRow writer [
                            id.ToString()
                            name
                            accountType
                            currency
                            institutionName
                            isOnBudget.ToString().ToLowerInvariant()
                            isActive.ToString().ToLowerInvariant()
                            postedBalance.ToString()
                            pendingBalance.ToString()
                        ]

                        rowCount <- rowCount + 1
                        if rowCount % 1000 = 0 then
                            do! writer.FlushAsync()

                    do! writer.FlushAsync()
                with ex ->
                    if not ctx.Response.HasStarted then
                        ctx.Response.StatusCode <- 500
                        do! Response.ofJson {| error = "Export failed"; detail = ex.Message |} ctx
                    else
                        raise ex
        }

// ── Budget export ──────────────────────────────────────────────────────────

module private BudgetExport =
    let exportBudgetPeriodCsv (ctx: HttpContext) (budgetId: Guid) (periodId: Guid) =
        task {
            let accessor = ctx.RequestServices.GetRequiredService<ITenantContextAccessor>()
            match accessor.Context with
            | None ->
                ctx.Response.StatusCode <- 401
                do! Response.ofJson {| error = "Unauthorized" |} ctx
            | Some _tenantContext ->
                try
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
                        | Some _period ->
                            let! allocs = periodRepo.ListAllocationsByPeriodAsync(periodId)
                            let! spendDetail = periodRepo.GetPeriodSpendAsync(periodId)
                            let! categories = categoryRepo.ListAsync()

                            let categoryNames =
                                categories |> List.map (fun c -> c.Id, c.Name) |> Map.ofList

                            // Group spend by category (same currency as budget)
                            let spendByCategory =
                                spendDetail
                                |> List.groupBy fst
                                |> List.map (fun (catId, items) ->
                                    let totalSpent =
                                        items |> List.sumBy (fun (_, money) -> money.Amount)
                                    catId, totalSpent)
                                |> Map.ofList

                            ctx.Response.ContentType <- "text/csv; charset=utf-8"
                            ctx.Response.Headers.["Content-Disposition"] <- $"attachment; filename=\"budget-{budgetId}-period-{periodId}.csv\""

                            use writer = new StreamWriter(ctx.Response.Body, Encoding.UTF8, leaveOpen = true)

                            Csv.writeHeader writer [
                                "category_name"
                                "allocated_minor"
                                "spent_minor"
                                "remaining_minor"
                                "rollover_balance_minor"
                                "currency"
                                "percent_used"
                            ]

                            for alloc in allocs do
                                let spentSigned = spendByCategory |> Map.tryFind alloc.CategoryId |> Option.defaultValue 0m
                                let allocated = alloc.AllocatedAmount.Amount
                                let spentDisplay = -spentSigned
                                let remaining = allocated + spentSigned
                                let rollover = alloc.RolloverBalance.Amount
                                let percentUsed =
                                    if allocated <> 0m then
                                        Decimal.Round(Math.Min(100m, Math.Max(0m, -spentSigned / allocated * 100m)), 2)
                                    else 0m

                                Csv.writeRow writer [
                                    categoryNames |> Map.tryFind alloc.CategoryId |> Option.defaultValue "Unknown"
                                    MoneyHelpers.toMinorUnits alloc.AllocatedAmount |> string
                                    MoneyHelpers.toMinorUnits { Amount = spentDisplay; CurrencyCode = budget.CurrencyCode } |> string
                                    MoneyHelpers.toMinorUnits { Amount = remaining; CurrencyCode = budget.CurrencyCode } |> string
                                    MoneyHelpers.toMinorUnits alloc.RolloverBalance |> string
                                    budget.CurrencyCode
                                    percentUsed.ToString("F2")
                                ]

                            do! writer.FlushAsync()
                with ex ->
                    if not ctx.Response.HasStarted then
                        ctx.Response.StatusCode <- 500
                        do! Response.ofJson {| error = "Export failed"; detail = ex.Message |} ctx
                    else
                        raise ex
        }

// ── Public endpoints ───────────────────────────────────────────────────────

module ExportEndpoints =
    let exportTransactionsHandler : HttpHandler = fun ctx ->
        TransactionExport.exportTransactionsCsv ctx

    let exportAccountsHandler : HttpHandler = fun ctx ->
        AccountExport.exportAccountsCsv ctx

    let exportBudgetPeriodHandler (budgetId: Guid) (periodId: Guid) : HttpHandler = fun ctx ->
        BudgetExport.exportBudgetPeriodCsv ctx budgetId periodId
