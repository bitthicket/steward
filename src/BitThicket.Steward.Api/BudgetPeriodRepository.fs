namespace BitThicket.Steward.Api

open System
open System.Data.Common
open System.Threading.Tasks
open Npgsql
open BitThicket.Steward.Api.Domain

/// Repository for budget periods and per-period category allocations.
type IBudgetPeriodRepository =
    abstract GetPeriodAsync : id:Guid -> Task<BudgetPeriodRecord option>
    abstract ListPeriodsByBudgetAsync : budgetId:Guid -> Task<BudgetPeriodRecord list>
    abstract GetOpenPeriodAsync : budgetId:Guid -> Task<BudgetPeriodRecord option>
    abstract CreatePeriodAsync : period:BudgetPeriodRecord * allocations:BudgetPeriodCategoryAllocation list -> Task<Guid>
    abstract UpdatePeriodAsync : period:BudgetPeriodRecord -> Task<unit>
    abstract ClosePeriodAsync : id:Guid -> Task<unit>
    abstract DeletePeriodAsync : id:Guid -> Task<unit>
    abstract GetAllocationAsync : periodId:Guid * categoryId:Guid -> Task<BudgetPeriodCategoryAllocation option>
    abstract ListAllocationsByPeriodAsync : periodId:Guid -> Task<BudgetPeriodCategoryAllocation list>
    abstract UpdateAllocationAsync : allocation:BudgetPeriodCategoryAllocation -> Task<unit>
    abstract GetActualSpendByCategoryAsync : periodId:Guid -> Task<Map<Guid, Money>>

module BudgetPeriodRepository =

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

    let private statusToString (s: BudgetPeriodStatus) : string =
        match s with
        | BudgetPeriodStatus.Open   -> "Open"
        | BudgetPeriodStatus.Closed -> "Closed"

    let private statusFromString (s: string) : BudgetPeriodStatus =
        match s with
        | "Open"   -> BudgetPeriodStatus.Open
        | "Closed" -> BudgetPeriodStatus.Closed
        | _        -> failwith $"Unknown budget period status: {s}"

    // ── Row mapping ──────────────────────────────────────────────────────────

    let private mapPeriod (reader: DbDataReader) : BudgetPeriodRecord =
        {
            Id = reader.GetGuid(0)
            BudgetId = reader.GetGuid(1)
            TenantId = reader.GetGuid(2)
            StartDate = DateOnly.FromDateTime(reader.GetDateTime(3))
            EndDate = DateOnly.FromDateTime(reader.GetDateTime(4))
            Status = statusFromString (reader.GetString(5))
            CreatedAt = Sql.dateTimeOffset reader 6
            UpdatedAt = Sql.dateTimeOffset reader 7
        }

    let private mapAllocation (reader: DbDataReader) : BudgetPeriodCategoryAllocation =
        let currency = reader.GetString(4)
        {
            BudgetPeriodId = reader.GetGuid(0)
            CategoryId = reader.GetGuid(1)
            AllocatedAmount = fromMinor (reader.GetInt64(2)) currency
            OpeningBalance = fromMinor (reader.GetInt64(3)) currency
            RolloverBalance = fromMinor (reader.GetInt64(5)) currency
            RolloverEnabled = reader.GetBoolean(6)
        }

    // ── Period CRUD ─────────────────────────────────────────────────────────

    let getPeriodAsync (factory: IDbConnectionFactory) (tenantContext: TenantContext) (id: Guid) =
        task {
            use! conn = factory.OpenForTenantAsync(tenantContext)
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                """SELECT id, budget_id, tenant_id, start_date, end_date, status, created_at, updated_at
                   FROM budget_periods WHERE id = $1"""
            cmd.Parameters.AddWithValue("$1", id) |> ignore
            let! reader = cmd.ExecuteReaderAsync()
            use reader = reader
            let! hasRow = reader.ReadAsync()
            return if hasRow then Some(mapPeriod reader) else None
        }

    let listPeriodsByBudgetAsync (factory: IDbConnectionFactory) (tenantContext: TenantContext) (budgetId: Guid) =
        task {
            use! conn = factory.OpenForTenantAsync(tenantContext)
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                """SELECT id, budget_id, tenant_id, start_date, end_date, status, created_at, updated_at
                   FROM budget_periods
                   WHERE budget_id = $1
                   ORDER BY start_date DESC"""
            cmd.Parameters.AddWithValue("$1", budgetId) |> ignore
            let! reader = cmd.ExecuteReaderAsync()
            use reader = reader
            let periods = ResizeArray<BudgetPeriodRecord>()
            while! reader.ReadAsync() do
                periods.Add(mapPeriod reader)
            return periods |> Seq.toList
        }

    let getOpenPeriodAsync (factory: IDbConnectionFactory) (tenantContext: TenantContext) (budgetId: Guid) =
        task {
            use! conn = factory.OpenForTenantAsync(tenantContext)
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                """SELECT id, budget_id, tenant_id, start_date, end_date, status, created_at, updated_at
                   FROM budget_periods
                   WHERE budget_id = $1 AND status = 'Open'"""
            cmd.Parameters.AddWithValue("$1", budgetId) |> ignore
            let! reader = cmd.ExecuteReaderAsync()
            use reader = reader
            let! hasRow = reader.ReadAsync()
            return if hasRow then Some(mapPeriod reader) else None
        }

    let createPeriodAsync (factory: IDbConnectionFactory) (period: BudgetPeriodRecord) (allocations: BudgetPeriodCategoryAllocation list) =
        task {
            let ctx = { TenantId = period.TenantId; UserId = Guid.Empty }
            use! conn = factory.OpenForTenantAsync(ctx)
            use txn = conn.BeginTransaction()

            use periodCmd = conn.CreateCommand()
            periodCmd.Transaction <- txn
            periodCmd.CommandText <-
                """INSERT INTO budget_periods (
                       id, budget_id, tenant_id, start_date, end_date, status, created_at, updated_at
                   ) VALUES ($1, $2, $3, $4, $5, $6, $7, $8)"""
            periodCmd.Parameters.AddWithValue("$1", period.Id) |> ignore
            periodCmd.Parameters.AddWithValue("$2", period.BudgetId) |> ignore
            periodCmd.Parameters.AddWithValue("$3", period.TenantId) |> ignore
            periodCmd.Parameters.AddWithValue("$4", period.StartDate.ToDateTime(TimeOnly.MinValue)) |> ignore
            periodCmd.Parameters.AddWithValue("$5", period.EndDate.ToDateTime(TimeOnly.MinValue)) |> ignore
            periodCmd.Parameters.AddWithValue("$6", statusToString period.Status) |> ignore
            periodCmd.Parameters.AddWithValue("$7", period.CreatedAt.UtcDateTime) |> ignore
            periodCmd.Parameters.AddWithValue("$8", period.UpdatedAt.UtcDateTime) |> ignore
            let! _ = periodCmd.ExecuteNonQueryAsync()

            for alloc in allocations do
                use allocCmd = conn.CreateCommand()
                allocCmd.Transaction <- txn
                allocCmd.CommandText <-
                    """INSERT INTO budget_period_categories (
                           budget_period_id, category_id, allocated_minor, opening_balance_minor,
                           rollover_balance_minor, currency, rollover_enabled
                       ) VALUES ($1, $2, $3, $4, $5, $6, $7)"""
                allocCmd.Parameters.AddWithValue("$1", alloc.BudgetPeriodId) |> ignore
                allocCmd.Parameters.AddWithValue("$2", alloc.CategoryId) |> ignore
                allocCmd.Parameters.AddWithValue("$3", toMinor alloc.AllocatedAmount) |> ignore
                allocCmd.Parameters.AddWithValue("$4", toMinor alloc.OpeningBalance) |> ignore
                allocCmd.Parameters.AddWithValue("$5", toMinor alloc.RolloverBalance) |> ignore
                allocCmd.Parameters.AddWithValue("$6", alloc.AllocatedAmount.CurrencyCode) |> ignore
                allocCmd.Parameters.AddWithValue("$7", alloc.RolloverEnabled) |> ignore
                let! _ = allocCmd.ExecuteNonQueryAsync()
                ()

            do! txn.CommitAsync()
            return period.Id
        }

    let updatePeriodAsync (factory: IDbConnectionFactory) (period: BudgetPeriodRecord) =
        task {
            let ctx = { TenantId = period.TenantId; UserId = Guid.Empty }
            use! conn = factory.OpenForTenantAsync(ctx)
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                """UPDATE budget_periods SET
                       start_date = $1,
                       end_date = $2,
                       status = $3,
                       updated_at = $4
                   WHERE id = $5"""
            cmd.Parameters.AddWithValue("$1", period.StartDate.ToDateTime(TimeOnly.MinValue)) |> ignore
            cmd.Parameters.AddWithValue("$2", period.EndDate.ToDateTime(TimeOnly.MinValue)) |> ignore
            cmd.Parameters.AddWithValue("$3", statusToString period.Status) |> ignore
            cmd.Parameters.AddWithValue("$4", DateTimeOffset.UtcNow.UtcDateTime) |> ignore
            cmd.Parameters.AddWithValue("$5", period.Id) |> ignore
            let! _ = cmd.ExecuteNonQueryAsync()
            return ()
        }

    let closePeriodAsync (factory: IDbConnectionFactory) (tenantContext: TenantContext) (id: Guid) =
        task {
            use! conn = factory.OpenForTenantAsync(tenantContext)
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                """UPDATE budget_periods SET status = 'Closed', updated_at = $1 WHERE id = $2"""
            cmd.Parameters.AddWithValue("$1", DateTimeOffset.UtcNow.UtcDateTime) |> ignore
            cmd.Parameters.AddWithValue("$2", id) |> ignore
            let! _ = cmd.ExecuteNonQueryAsync()
            return ()
        }

    let deletePeriodAsync (factory: IDbConnectionFactory) (tenantContext: TenantContext) (id: Guid) =
        task {
            use! conn = factory.OpenForTenantAsync(tenantContext)
            use cmd = conn.CreateCommand()
            cmd.CommandText <- "DELETE FROM budget_periods WHERE id = $1"
            cmd.Parameters.AddWithValue("$1", id) |> ignore
            let! _ = cmd.ExecuteNonQueryAsync()
            return ()
        }

    // ── Allocation CRUD ─────────────────────────────────────────────────────

    let getAllocationAsync (factory: IDbConnectionFactory) (tenantContext: TenantContext) (periodId: Guid) (categoryId: Guid) =
        task {
            use! conn = factory.OpenForTenantAsync(tenantContext)
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                """SELECT budget_period_id, category_id, allocated_minor, opening_balance_minor,
                          rollover_balance_minor, currency, rollover_enabled
                   FROM budget_period_categories
                   WHERE budget_period_id = $1 AND category_id = $2"""
            cmd.Parameters.AddWithValue("$1", periodId) |> ignore
            cmd.Parameters.AddWithValue("$2", categoryId) |> ignore
            let! reader = cmd.ExecuteReaderAsync()
            use reader = reader
            let! hasRow = reader.ReadAsync()
            return if hasRow then Some(mapAllocation reader) else None
        }

    let listAllocationsByPeriodAsync (factory: IDbConnectionFactory) (tenantContext: TenantContext) (periodId: Guid) =
        task {
            use! conn = factory.OpenForTenantAsync(tenantContext)
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                """SELECT budget_period_id, category_id, allocated_minor, opening_balance_minor,
                          rollover_balance_minor, currency, rollover_enabled
                   FROM budget_period_categories
                   WHERE budget_period_id = $1"""
            cmd.Parameters.AddWithValue("$1", periodId) |> ignore
            let! reader = cmd.ExecuteReaderAsync()
            use reader = reader
            let allocs = ResizeArray<BudgetPeriodCategoryAllocation>()
            while! reader.ReadAsync() do
                allocs.Add(mapAllocation reader)
            return allocs |> Seq.toList
        }

    let updateAllocationAsync (factory: IDbConnectionFactory) (allocation: BudgetPeriodCategoryAllocation) =
        task {
            let ctx = { TenantId = Guid.Empty; UserId = Guid.Empty }
            use! conn = factory.OpenForTenantAsync(ctx)
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                """UPDATE budget_period_categories SET
                       allocated_minor = $1,
                       opening_balance_minor = $2,
                       rollover_balance_minor = $3,
                       currency = $4,
                       rollover_enabled = $5
                   WHERE budget_period_id = $6 AND category_id = $7"""
            cmd.Parameters.AddWithValue("$1", toMinor allocation.AllocatedAmount) |> ignore
            cmd.Parameters.AddWithValue("$2", toMinor allocation.OpeningBalance) |> ignore
            cmd.Parameters.AddWithValue("$3", toMinor allocation.RolloverBalance) |> ignore
            cmd.Parameters.AddWithValue("$4", allocation.AllocatedAmount.CurrencyCode) |> ignore
            cmd.Parameters.AddWithValue("$5", allocation.RolloverEnabled) |> ignore
            cmd.Parameters.AddWithValue("$6", allocation.BudgetPeriodId) |> ignore
            cmd.Parameters.AddWithValue("$7", allocation.CategoryId) |> ignore
            let! _ = cmd.ExecuteNonQueryAsync()
            return ()
        }

    /// Returns actual spend per category for the given period by summing
    /// transaction amounts in the period's date range. Only cleared/reconciled
    /// transactions are counted.
    let getActualSpendByCategoryAsync (factory: IDbConnectionFactory) (tenantContext: TenantContext) (periodId: Guid) =
        task {
            use! conn = factory.OpenForTenantAsync(tenantContext)
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                """SELECT
                       t.category_id,
                       COALESCE(SUM(t.amount_minor), 0) AS spent_minor,
                       t.currency
                   FROM transactions t
                   JOIN budget_periods bp ON bp.id = $1
                   WHERE t.tenant_id = current_setting('steward.tenant_id')::uuid
                     AND t.occurred_at >= bp.start_date
                     AND t.occurred_at <  bp.end_date + INTERVAL '1 day'
                     AND t.status IN ('cleared', 'reconciled')
                     AND t.category_id IS NOT NULL
                   GROUP BY t.category_id, t.currency"""
            cmd.Parameters.AddWithValue("$1", periodId) |> ignore
            let! reader = cmd.ExecuteReaderAsync()
            use reader = reader
            let mutable result = Map.empty<Guid, Money>
            while! reader.ReadAsync() do
                let categoryId = reader.GetGuid(0)
                let spentMinor = reader.GetInt64(1)
                let currency = reader.GetString(2)
                result <- result.Add(categoryId, fromMinor spentMinor currency)
            return result
        }

    /// Create an IBudgetPeriodRepository backed by the given connection factory and
    /// tenant context accessor.
    let create (factory: IDbConnectionFactory) (accessor: ITenantContextAccessor) : IBudgetPeriodRepository =
        let requireCtx () =
            match accessor.Context with
            | Some ctx -> ctx
            | None -> failwith "No tenant context available for the current operation"

        { new IBudgetPeriodRepository with
            member _.GetPeriodAsync(id) = getPeriodAsync factory (requireCtx()) id
            member _.ListPeriodsByBudgetAsync(budgetId) = listPeriodsByBudgetAsync factory (requireCtx()) budgetId
            member _.GetOpenPeriodAsync(budgetId) = getOpenPeriodAsync factory (requireCtx()) budgetId
            member _.CreatePeriodAsync(period, allocs) = createPeriodAsync factory period allocs
            member _.UpdatePeriodAsync(period) = updatePeriodAsync factory period
            member _.ClosePeriodAsync(id) = closePeriodAsync factory (requireCtx()) id
            member _.DeletePeriodAsync(id) = deletePeriodAsync factory (requireCtx()) id
            member _.GetAllocationAsync(periodId, categoryId) = getAllocationAsync factory (requireCtx()) periodId categoryId
            member _.ListAllocationsByPeriodAsync(periodId) = listAllocationsByPeriodAsync factory (requireCtx()) periodId
            member _.UpdateAllocationAsync(allocation) = updateAllocationAsync factory allocation
            member _.GetActualSpendByCategoryAsync(periodId) = getActualSpendByCategoryAsync factory (requireCtx()) periodId
        }
