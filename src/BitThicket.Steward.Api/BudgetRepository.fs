namespace BitThicket.Steward.Api

open System
open System.Data.Common
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading.Tasks
open Npgsql
open BitThicket.Steward.Api.Domain

/// Repository for tenant-scoped budgets and their category allocations.
type IBudgetRepository =
    abstract GetAsync : id:Guid -> Task<Budget option>
    abstract ListAsync : unit -> Task<Budget list>
    abstract CreateAsync : budget:Budget -> Task<Guid>
    abstract UpdateAsync : budget:Budget -> Task<unit>
    abstract DeleteAsync : id:Guid -> Task<unit>
    abstract GetCategoryAsync : id:Guid -> Task<BudgetCategory option>
    abstract ListCategoriesByBudgetAsync : budgetId:Guid -> Task<BudgetCategory list>
    abstract CreateCategoryAsync : category:BudgetCategory -> Task<Guid>
    abstract UpdateCategoryAsync : category:BudgetCategory -> Task<unit>
    abstract DeleteCategoryAsync : id:Guid -> Task<unit>

module BudgetRepository =

    let private jsonOptions =
        let opts = JsonSerializerOptions()
        opts.Converters.Add(JsonFSharpConverter(JsonUnionEncoding.NamedFields))
        opts.PropertyNamingPolicy <- JsonNamingPolicy.CamelCase
        opts

    let private styleToString (s: BudgetingStyle) : string =
        match s with
        | BudgetingStyle.ZeroBased        -> "zero_based"
        | BudgetingStyle.TraditionalLimits -> "traditional_limits"

    let private styleFromString (s: string) : BudgetingStyle =
        match s.ToLowerInvariant() with
        | "zero_based"        -> BudgetingStyle.ZeroBased
        | "traditional_limits" -> BudgetingStyle.TraditionalLimits
        | _                   -> failwith $"Unknown budgeting style: {s}"

    let private periodToJsonb (p: BudgetPeriod) : obj =
        box (JsonSerializer.Serialize(p, jsonOptions))

    let private periodFromJsonb (reader: DbDataReader) (ordinal: int) : BudgetPeriod =
        let json = reader.GetString(ordinal)
        JsonSerializer.Deserialize<BudgetPeriod>(json, jsonOptions)

    let private decimalPlaces (currencyCode: string) : int =
        match currencyCode.ToUpperInvariant() with
        | "BTC" -> 8
        | _ -> 2

    let private toMinor (money: Money) : int64 =
        let places = decimalPlaces money.CurrencyCode
        let factor = pown 10m places
        int64 (Decimal.Round(money.Amount * factor))

    let private fromMinor (minor: int64) (currencyCode: string) : Money =
        let places = decimalPlaces currencyCode
        let factor = pown 10m places
        { Amount = decimal minor / factor; CurrencyCode = currencyCode }

    // ── Budget mapping ──────────────────────────────────────────────────────

    let private mapBudget (reader: DbDataReader) : Budget =
        {
            Id = reader.GetGuid(0)
            TenantId = reader.GetGuid(1)
            UserId = reader.GetGuid(2)
            Name = reader.GetString(3)
            Style = styleFromString (reader.GetString(4))
            Period = periodFromJsonb reader 5
            CurrencyCode = reader.GetString(6)
            IsActive = reader.GetBoolean(7)
            StartsOn = DateOnly.FromDateTime(reader.GetDateTime(8))
            CreatedAt = Sql.dateTimeOffset reader 9
            UpdatedAt = Sql.dateTimeOffset reader 10
        }

    // ── BudgetCategory mapping ──────────────────────────────────────────────

    let private mapBudgetCategory (reader: DbDataReader) : BudgetCategory =
        {
            Id = reader.GetGuid(0)
            TenantId = reader.GetGuid(1)
            BudgetId = reader.GetGuid(2)
            CategoryId = reader.GetGuid(3)
            AllocatedAmount = fromMinor (reader.GetInt64(4)) (reader.GetString(5))
            RolloverEnabled = reader.GetBoolean(6)
            RolloverBalance = fromMinor (reader.GetInt64(7)) (reader.GetString(8))
        }

    // ── Budget CRUD ─────────────────────────────────────────────────────────

    let getAsync (factory: IDbConnectionFactory) (tenantContext: TenantContext) (id: Guid) =
        task {
            use! conn = factory.OpenForTenantAsync(tenantContext)
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                """SELECT id, tenant_id, user_id, name, style, period,
                          currency, is_active, starts_on, created_at, updated_at
                   FROM budgets WHERE id = $1"""
            cmd.Parameters.AddWithValue("$1", id) |> ignore
            let! reader = cmd.ExecuteReaderAsync()
            use reader = reader
            let! hasRow = reader.ReadAsync()
            return if hasRow then Some(mapBudget reader) else None
        }

    let listAsync (factory: IDbConnectionFactory) (tenantContext: TenantContext) =
        task {
            use! conn = factory.OpenForTenantAsync(tenantContext)
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                """SELECT id, tenant_id, user_id, name, style, period,
                          currency, is_active, starts_on, created_at, updated_at
                   FROM budgets
                   ORDER BY created_at DESC"""
            let! reader = cmd.ExecuteReaderAsync()
            use reader = reader
            let budgets = ResizeArray<Budget>()
            while! reader.ReadAsync() do
                budgets.Add(mapBudget reader)
            return budgets |> Seq.toList
        }

    let createAsync (factory: IDbConnectionFactory) (budget: Budget) =
        task {
            let ctx = { TenantId = budget.TenantId; UserId = budget.UserId }
            use! conn = factory.OpenForTenantAsync(ctx)
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                """INSERT INTO budgets (
                       id, tenant_id, user_id, name, style, period,
                       currency, is_active, starts_on, created_at, updated_at
                   ) VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11)"""
            cmd.Parameters.AddWithValue("$1", budget.Id) |> ignore
            cmd.Parameters.AddWithValue("$2", budget.TenantId) |> ignore
            cmd.Parameters.AddWithValue("$3", budget.UserId) |> ignore
            cmd.Parameters.AddWithValue("$4", budget.Name) |> ignore
            cmd.Parameters.AddWithValue("$5", styleToString budget.Style) |> ignore
            let periodParam = cmd.CreateParameter()
            periodParam.ParameterName <- "$6"
            periodParam.NpgsqlDbType <- NpgsqlTypes.NpgsqlDbType.Jsonb
            periodParam.Value <- periodToJsonb budget.Period
            cmd.Parameters.Add(periodParam) |> ignore
            cmd.Parameters.AddWithValue("$7", budget.CurrencyCode) |> ignore
            cmd.Parameters.AddWithValue("$8", budget.IsActive) |> ignore
            cmd.Parameters.AddWithValue("$9", budget.StartsOn.ToDateTime(TimeOnly.MinValue)) |> ignore
            cmd.Parameters.AddWithValue("$10", budget.CreatedAt.UtcDateTime) |> ignore
            cmd.Parameters.AddWithValue("$11", budget.UpdatedAt.UtcDateTime) |> ignore
            let! _ = cmd.ExecuteNonQueryAsync()
            return budget.Id
        }

    let updateAsync (factory: IDbConnectionFactory) (budget: Budget) =
        task {
            let ctx = { TenantId = budget.TenantId; UserId = budget.UserId }
            use! conn = factory.OpenForTenantAsync(ctx)
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                """UPDATE budgets SET
                       name = $1,
                       style = $2,
                       period = $3,
                       currency = $4,
                       is_active = $5,
                       starts_on = $6,
                       updated_at = $7
                   WHERE id = $8"""
            cmd.Parameters.AddWithValue("$1", budget.Name) |> ignore
            cmd.Parameters.AddWithValue("$2", styleToString budget.Style) |> ignore
            let periodParam = cmd.CreateParameter()
            periodParam.ParameterName <- "$3"
            periodParam.NpgsqlDbType <- NpgsqlTypes.NpgsqlDbType.Jsonb
            periodParam.Value <- periodToJsonb budget.Period
            cmd.Parameters.Add(periodParam) |> ignore
            cmd.Parameters.AddWithValue("$4", budget.CurrencyCode) |> ignore
            cmd.Parameters.AddWithValue("$5", budget.IsActive) |> ignore
            cmd.Parameters.AddWithValue("$6", budget.StartsOn.ToDateTime(TimeOnly.MinValue)) |> ignore
            cmd.Parameters.AddWithValue("$7", DateTimeOffset.UtcNow.UtcDateTime) |> ignore
            cmd.Parameters.AddWithValue("$8", budget.Id) |> ignore
            let! _ = cmd.ExecuteNonQueryAsync()
            return ()
        }

    let deleteAsync (factory: IDbConnectionFactory) (tenantContext: TenantContext) (id: Guid) =
        task {
            use! conn = factory.OpenForTenantAsync(tenantContext)
            use cmd = conn.CreateCommand()
            cmd.CommandText <- "DELETE FROM budgets WHERE id = $1"
            cmd.Parameters.AddWithValue("$1", id) |> ignore
            let! _ = cmd.ExecuteNonQueryAsync()
            return ()
        }

    // ── BudgetCategory CRUD ─────────────────────────────────────────────────

    let getCategoryAsync (factory: IDbConnectionFactory) (tenantContext: TenantContext) (id: Guid) =
        task {
            use! conn = factory.OpenForTenantAsync(tenantContext)
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                """SELECT id, tenant_id, budget_id, category_id,
                          allocated_minor, currency,
                          rollover_enabled, rollover_balance_minor, rollover_currency
                   FROM budget_categories WHERE id = $1"""
            cmd.Parameters.AddWithValue("$1", id) |> ignore
            let! reader = cmd.ExecuteReaderAsync()
            use reader = reader
            let! hasRow = reader.ReadAsync()
            return if hasRow then Some(mapBudgetCategory reader) else None
        }

    let listCategoriesByBudgetAsync (factory: IDbConnectionFactory) (tenantContext: TenantContext) (budgetId: Guid) =
        task {
            use! conn = factory.OpenForTenantAsync(tenantContext)
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                """SELECT id, tenant_id, budget_id, category_id,
                          allocated_minor, currency,
                          rollover_enabled, rollover_balance_minor, rollover_currency
                   FROM budget_categories
                   WHERE budget_id = $1"""
            cmd.Parameters.AddWithValue("$1", budgetId) |> ignore
            let! reader = cmd.ExecuteReaderAsync()
            use reader = reader
            let categories = ResizeArray<BudgetCategory>()
            while! reader.ReadAsync() do
                categories.Add(mapBudgetCategory reader)
            return categories |> Seq.toList
        }

    let createCategoryAsync (factory: IDbConnectionFactory) (category: BudgetCategory) =
        task {
            let ctx = { TenantId = category.TenantId; UserId = Guid.Empty }
            use! conn = factory.OpenForTenantAsync(ctx)
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                """INSERT INTO budget_categories (
                       id, tenant_id, budget_id, category_id,
                       allocated_minor, currency,
                       rollover_enabled, rollover_balance_minor, rollover_currency
                   ) VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9)"""
            cmd.Parameters.AddWithValue("$1", category.Id) |> ignore
            cmd.Parameters.AddWithValue("$2", category.TenantId) |> ignore
            cmd.Parameters.AddWithValue("$3", category.BudgetId) |> ignore
            cmd.Parameters.AddWithValue("$4", category.CategoryId) |> ignore
            cmd.Parameters.AddWithValue("$5", toMinor category.AllocatedAmount) |> ignore
            cmd.Parameters.AddWithValue("$6", category.AllocatedAmount.CurrencyCode) |> ignore
            cmd.Parameters.AddWithValue("$7", category.RolloverEnabled) |> ignore
            cmd.Parameters.AddWithValue("$8", toMinor category.RolloverBalance) |> ignore
            cmd.Parameters.AddWithValue("$9", category.RolloverBalance.CurrencyCode) |> ignore
            let! _ = cmd.ExecuteNonQueryAsync()
            return category.Id
        }

    let updateCategoryAsync (factory: IDbConnectionFactory) (category: BudgetCategory) =
        task {
            let ctx = { TenantId = category.TenantId; UserId = Guid.Empty }
            use! conn = factory.OpenForTenantAsync(ctx)
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                """UPDATE budget_categories SET
                       allocated_minor = $1,
                       currency = $2,
                       rollover_enabled = $3,
                       rollover_balance_minor = $4,
                       rollover_currency = $5
                   WHERE id = $6"""
            cmd.Parameters.AddWithValue("$1", toMinor category.AllocatedAmount) |> ignore
            cmd.Parameters.AddWithValue("$2", category.AllocatedAmount.CurrencyCode) |> ignore
            cmd.Parameters.AddWithValue("$3", category.RolloverEnabled) |> ignore
            cmd.Parameters.AddWithValue("$4", toMinor category.RolloverBalance) |> ignore
            cmd.Parameters.AddWithValue("$5", category.RolloverBalance.CurrencyCode) |> ignore
            cmd.Parameters.AddWithValue("$6", category.Id) |> ignore
            let! _ = cmd.ExecuteNonQueryAsync()
            return ()
        }

    let deleteCategoryAsync (factory: IDbConnectionFactory) (tenantContext: TenantContext) (id: Guid) =
        task {
            use! conn = factory.OpenForTenantAsync(tenantContext)
            use cmd = conn.CreateCommand()
            cmd.CommandText <- "DELETE FROM budget_categories WHERE id = $1"
            cmd.Parameters.AddWithValue("$1", id) |> ignore
            let! _ = cmd.ExecuteNonQueryAsync()
            return ()
        }

    /// Create an IBudgetRepository backed by the given connection factory and
    /// tenant context accessor.
    let create (factory: IDbConnectionFactory) (accessor: ITenantContextAccessor) : IBudgetRepository =
        let requireCtx () =
            match accessor.Context with
            | Some ctx -> ctx
            | None -> failwith "No tenant context available for the current operation"

        { new IBudgetRepository with
            member _.GetAsync(id) = getAsync factory (requireCtx()) id
            member _.ListAsync() = listAsync factory (requireCtx())
            member _.CreateAsync(budget) = createAsync factory budget
            member _.UpdateAsync(budget) = updateAsync factory budget
            member _.DeleteAsync(id) = deleteAsync factory (requireCtx()) id
            member _.GetCategoryAsync(id) = getCategoryAsync factory (requireCtx()) id
            member _.ListCategoriesByBudgetAsync(budgetId) = listCategoriesByBudgetAsync factory (requireCtx()) budgetId
            member _.CreateCategoryAsync(category) = createCategoryAsync factory category
            member _.UpdateCategoryAsync(category) = updateCategoryAsync factory category
            member _.DeleteCategoryAsync(id) = deleteCategoryAsync factory (requireCtx()) id
        }
