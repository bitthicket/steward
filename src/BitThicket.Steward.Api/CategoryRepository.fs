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
            CreatedAt = Sql.dateTimeOffset reader 6
        }

    // ── CRUD implementations ─────────────────────────────────────────────────

    let getAsync (factory: IDbConnectionFactory) (tenantContext: TenantContext) (id: Guid) =
        task {
            use! conn = factory.OpenForTenantAsync(tenantContext)
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                """SELECT id, tenant_id, user_id, name, parent_id, is_system, created_at
                   FROM categories WHERE id = $1"""
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
                """SELECT id, tenant_id, user_id, name, parent_id, is_system, created_at
                   FROM categories
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
                       id, tenant_id, user_id, name, parent_id, is_system, created_at
                   ) VALUES ($1, $2, $3, $4, $5, $6, $7)"""
            cmd.Parameters.AddWithValue("$1", category.Id) |> ignore
            cmd.Parameters.AddWithValue("$2", category.TenantId) |> ignore
            cmd.Parameters.AddWithValue("$3", category.UserId) |> ignore
            cmd.Parameters.AddWithValue("$4", category.Name) |> ignore
            match category.ParentCategoryId with
            | Some pid -> cmd.Parameters.AddWithValue("$5", pid) |> ignore
            | None -> cmd.Parameters.AddWithValue("$5", DBNull.Value) |> ignore
            cmd.Parameters.AddWithValue("$6", category.IsSystem) |> ignore
            cmd.Parameters.AddWithValue("$7", category.CreatedAt.UtcDateTime) |> ignore
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
                       is_system = $3
                   WHERE id = $4"""
            cmd.Parameters.AddWithValue("$1", category.Name) |> ignore
            match category.ParentCategoryId with
            | Some pid -> cmd.Parameters.AddWithValue("$2", pid) |> ignore
            | None -> cmd.Parameters.AddWithValue("$2", DBNull.Value) |> ignore
            cmd.Parameters.AddWithValue("$3", category.IsSystem) |> ignore
            cmd.Parameters.AddWithValue("$4", category.Id) |> ignore
            let! _ = cmd.ExecuteNonQueryAsync()
            return ()
        }

    let deleteAsync (factory: IDbConnectionFactory) (tenantContext: TenantContext) (id: Guid) =
        task {
            use! conn = factory.OpenForTenantAsync(tenantContext)
            use cmd = conn.CreateCommand()
            cmd.CommandText <- "DELETE FROM categories WHERE id = $1"
            cmd.Parameters.AddWithValue("$1", id) |> ignore
            let! _ = cmd.ExecuteNonQueryAsync()
            return ()
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
        }
