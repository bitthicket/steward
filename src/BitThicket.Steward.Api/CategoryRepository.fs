namespace BitThicket.Steward.Api

open System
open System.Data.Common
open System.Threading.Tasks
open BitThicket.Steward.Api.Domain

/// Repository for tenant-scoped categories.
type ICategoryRepository =
    abstract GetAsync : id:Guid -> Task<Category option>
    abstract ListAsync : unit -> Task<Category list>
    abstract CreateAsync : category:Category -> Task<Guid>
    abstract UpdateAsync : category:Category -> Task<unit>
    abstract DeleteAsync : id:Guid -> Task<unit>
    abstract ReassignTransactionsAsync : fromId:Guid * toId:Guid -> Task<unit>
    abstract HasTransactionsAsync : id:Guid -> Task<bool>
    abstract WouldCreateCycleAsync : categoryId:Guid * parentId:Guid -> Task<bool>

module CategoryRepository =

    // ── Row mapping ──────────────────────────────────────────────────────────

    let private mapCategory (reader: DbDataReader) : Category =
        {
            Id = reader.GetGuid(0)
            TenantId = reader.GetGuid(1)
            UserId = reader.GetGuid(2)
            Name = reader.GetString(3)
            ParentCategoryId = Sql.nullableGuid reader 4
            IsSystem = reader.GetBoolean(5)
            CurrencyCode = reader.GetString(6)
            RolloverEnabled = reader.GetBoolean(7)
            DeletedAt = Sql.nullableDateTimeOffset reader 8
            CreatedAt = Sql.dateTimeOffset reader 9
            UpdatedAt = Sql.dateTimeOffset reader 10
        }

    // ── CRUD implementations ─────────────────────────────────────────────────

    let getAsync (factory: IDbConnectionFactory) (tenantContext: TenantContext) (id: Guid) =
        task {
            use! conn = factory.OpenForTenantAsync(tenantContext)
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                """SELECT id, tenant_id, user_id, name, parent_id, is_system,
                          currency, rollover_enabled, deleted_at, created_at, updated_at
                   FROM categories WHERE id = $1 AND deleted_at IS NULL"""
            cmd.Parameters.AddWithValue("$1", id) |> ignore
            let! reader = cmd.ExecuteReaderAsync()
            use reader = reader
            let! hasRow = reader.ReadAsync()
            return if hasRow then Some(mapCategory reader) else None
        }

    let listAsync (factory: IDbConnectionFactory) (tenantContext: TenantContext) =
        task {
            use! conn = factory.OpenForTenantAsync(tenantContext)
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                """SELECT id, tenant_id, user_id, name, parent_id, is_system,
                          currency, rollover_enabled, deleted_at, created_at, updated_at
                   FROM categories
                   WHERE deleted_at IS NULL
                   ORDER BY created_at DESC"""
            let! reader = cmd.ExecuteReaderAsync()
            use reader = reader
            let categories = ResizeArray<Category>()
            while! reader.ReadAsync() do
                categories.Add(mapCategory reader)
            return categories |> Seq.toList
        }

    let createAsync (factory: IDbConnectionFactory) (category: Category) =
        task {
            let ctx = { TenantId = category.TenantId; UserId = category.UserId }
            use! conn = factory.OpenForTenantAsync(ctx)
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                """INSERT INTO categories (
                       id, tenant_id, user_id, name, parent_id, is_system,
                       currency, rollover_enabled, deleted_at, created_at, updated_at
                   ) VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11)"""
            cmd.Parameters.AddWithValue("$1", category.Id) |> ignore
            cmd.Parameters.AddWithValue("$2", category.TenantId) |> ignore
            cmd.Parameters.AddWithValue("$3", category.UserId) |> ignore
            cmd.Parameters.AddWithValue("$4", category.Name) |> ignore
            match category.ParentCategoryId with
            | Some pid -> cmd.Parameters.AddWithValue("$5", pid) |> ignore
            | None -> cmd.Parameters.AddWithValue("$5", DBNull.Value) |> ignore
            cmd.Parameters.AddWithValue("$6", category.IsSystem) |> ignore
            cmd.Parameters.AddWithValue("$7", category.CurrencyCode) |> ignore
            cmd.Parameters.AddWithValue("$8", category.RolloverEnabled) |> ignore
            match category.DeletedAt with
            | Some d -> cmd.Parameters.AddWithValue("$9", d.UtcDateTime) |> ignore
            | None -> cmd.Parameters.AddWithValue("$9", DBNull.Value) |> ignore
            cmd.Parameters.AddWithValue("$10", category.CreatedAt.UtcDateTime) |> ignore
            cmd.Parameters.AddWithValue("$11", category.UpdatedAt.UtcDateTime) |> ignore
            let! _ = cmd.ExecuteNonQueryAsync()
            return category.Id
        }

    let updateAsync (factory: IDbConnectionFactory) (category: Category) =
        task {
            let ctx = { TenantId = category.TenantId; UserId = category.UserId }
            use! conn = factory.OpenForTenantAsync(ctx)
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                """UPDATE categories SET
                       name = $1,
                       parent_id = $2,
                       is_system = $3,
                       rollover_enabled = $4,
                       updated_at = $5
                   WHERE id = $6 AND deleted_at IS NULL"""
            cmd.Parameters.AddWithValue("$1", category.Name) |> ignore
            match category.ParentCategoryId with
            | Some pid -> cmd.Parameters.AddWithValue("$2", pid) |> ignore
            | None -> cmd.Parameters.AddWithValue("$2", DBNull.Value) |> ignore
            cmd.Parameters.AddWithValue("$3", category.IsSystem) |> ignore
            cmd.Parameters.AddWithValue("$4", category.RolloverEnabled) |> ignore
            cmd.Parameters.AddWithValue("$5", DateTimeOffset.UtcNow.UtcDateTime) |> ignore
            cmd.Parameters.AddWithValue("$6", category.Id) |> ignore
            let! _ = cmd.ExecuteNonQueryAsync()
            return ()
        }

    let deleteAsync (factory: IDbConnectionFactory) (tenantContext: TenantContext) (id: Guid) =
        task {
            use! conn = factory.OpenForTenantAsync(tenantContext)
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                """UPDATE categories
                   SET deleted_at = now()
                   WHERE id = $1 AND deleted_at IS NULL"""
            cmd.Parameters.AddWithValue("$1", id) |> ignore
            let! _ = cmd.ExecuteNonQueryAsync()
            return ()
        }

    /// Reassign all transactions and splits from one category to another.
    let reassignTransactionsAsync (factory: IDbConnectionFactory) (tenantContext: TenantContext) (fromId: Guid) (toId: Guid) =
        task {
            use! conn = factory.OpenForTenantAsync(tenantContext)
            use tx = conn.BeginTransaction()
            use txnCmd = conn.CreateCommand()
            txnCmd.Transaction <- tx
            txnCmd.CommandText <-
                """UPDATE transactions SET category_id = $1, updated_at = now()
                   WHERE category_id = $2 AND deleted_at IS NULL"""
            txnCmd.Parameters.AddWithValue("$1", toId) |> ignore
            txnCmd.Parameters.AddWithValue("$2", fromId) |> ignore
            do! txnCmd.ExecuteNonQueryAsync() :> Task

            use splitCmd = conn.CreateCommand()
            splitCmd.Transaction <- tx
            splitCmd.CommandText <-
                """UPDATE transaction_splits SET category_id = $1, updated_at = now()
                   WHERE category_id = $2"""
            splitCmd.Parameters.AddWithValue("$1", toId) |> ignore
            splitCmd.Parameters.AddWithValue("$2", fromId) |> ignore
            do! splitCmd.ExecuteNonQueryAsync() :> Task

            do! tx.CommitAsync()
        }

    /// Check whether any non-deleted transactions or splits reference this category.
    let hasTransactionsAsync (factory: IDbConnectionFactory) (tenantContext: TenantContext) (id: Guid) =
        task {
            use! conn = factory.OpenForTenantAsync(tenantContext)
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                """SELECT EXISTS (
                       SELECT 1 FROM transactions
                       WHERE category_id = $1 AND deleted_at IS NULL
                       UNION ALL
                       SELECT 1 FROM transaction_splits
                       WHERE category_id = $1
                       LIMIT 1
                   )"""
            cmd.Parameters.AddWithValue("$1", id) |> ignore
            let! result = cmd.ExecuteScalarAsync()
            return result :?> bool
        }

    /// Walk up the parent chain from `parentId` and return true if `categoryId`
    /// is reachable (i.e. assigning `parentId` as the parent of `categoryId`
    /// would create a cycle).
    let wouldCreateCycleAsync (factory: IDbConnectionFactory) (tenantContext: TenantContext) (categoryId: Guid) (parentId: Guid) =
        task {
            if categoryId = parentId then return true
            else
                let mutable current = Some parentId
                let mutable seen = Set.empty
                let mutable foundCycle = false

                while current.IsSome
                      && not foundCycle
                      && not (seen |> Set.contains current.Value) do
                    seen <- seen |> Set.add current.Value
                    let! parentOpt = getAsync factory tenantContext current.Value
                    match parentOpt with
                    | Some cat when cat.Id = categoryId ->
                        foundCycle <- true
                    | Some cat ->
                        current <- cat.ParentCategoryId
                    | None ->
                        current <- None

                return foundCycle
        }

    /// Create an ICategoryRepository backed by the given connection factory and
    /// tenant context accessor.
    let create (factory: IDbConnectionFactory) (accessor: ITenantContextAccessor) : ICategoryRepository =
        let requireCtx () =
            match accessor.Context with
            | Some ctx -> ctx
            | None -> failwith "No tenant context available for the current operation"

        { new ICategoryRepository with
            member _.GetAsync(id) = getAsync factory (requireCtx()) id
            member _.ListAsync() = listAsync factory (requireCtx())
            member _.CreateAsync(category) = createAsync factory category
            member _.UpdateAsync(category) = updateAsync factory category
            member _.DeleteAsync(id) = deleteAsync factory (requireCtx()) id
            member _.ReassignTransactionsAsync(fromId, toId) = reassignTransactionsAsync factory (requireCtx()) fromId toId
            member _.HasTransactionsAsync(id) = hasTransactionsAsync factory (requireCtx()) id
            member _.WouldCreateCycleAsync(categoryId, parentId) = wouldCreateCycleAsync factory (requireCtx()) categoryId parentId
        }
