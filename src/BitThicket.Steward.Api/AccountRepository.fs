namespace BitThicket.Steward.Api

open System
open System.Data.Common
open System.Text.Json
open System.Threading.Tasks
open Npgsql
open BitThicket.Steward.Api.Domain

/// Repository for tenant-scoped accounts.
type IAccountRepository =
    abstract GetAsync : id:Guid -> Task<Account option>
    abstract GetByExternalIdAsync : externalId:string -> Task<Account option>
    abstract ListAsync : unit -> Task<Account list>
    abstract CreateAsync : account:Account -> Task<Guid>
    abstract UpdateAsync : account:Account -> Task<unit>
    abstract DeleteAsync : id:Guid -> Task<unit>

module AccountRepository =

    // ── Domain helpers ───────────────────────────────────────────────────────

    let defaultIsOnBudget (accountType: AccountType) : bool =
        match accountType with
        | AccountType.Checking    -> true
        | AccountType.Savings     -> true
        | AccountType.CreditCard  -> true
        | AccountType.Cash        -> true
        | AccountType.Investment  -> false
        | AccountType.Loan        -> false

    let private accountTypeToString (t: AccountType) : string =
        match t with
        | AccountType.Checking    -> "checking"
        | AccountType.Savings     -> "savings"
        | AccountType.CreditCard  -> "credit_card"
        | AccountType.Investment  -> "investment"
        | AccountType.Loan        -> "loan"
        | AccountType.Cash        -> "cash"

    let accountTypeFromString (s: string) : AccountType option =
        match s.ToLowerInvariant() with
        | "checking"    -> Some AccountType.Checking
        | "savings"     -> Some AccountType.Savings
        | "credit_card" -> Some AccountType.CreditCard
        | "investment"  -> Some AccountType.Investment
        | "loan"        -> Some AccountType.Loan
        | "cash"        -> Some AccountType.Cash
        | _             -> None

    // ── JSON serialization (lives in repo, not domain) ───────────────────────

    let private jsonOptions =
        JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower)

    let private creditCardInfoToJsonb (info: CreditCardInfo option) : obj =
        match info with
        | None -> box DBNull.Value
        | Some i -> box (JsonSerializer.Serialize(i, jsonOptions))

    let private creditCardInfoFromJsonb (reader: DbDataReader) (ordinal: int) : CreditCardInfo option =
        if reader.IsDBNull(ordinal) then None
        else
            let json = reader.GetString(ordinal)
            Some(JsonSerializer.Deserialize<CreditCardInfo>(json, jsonOptions))

    // ── Row mapping ──────────────────────────────────────────────────────────

    let private mapAccount (reader: DbDataReader) : Account =
        {
            Id = reader.GetGuid(0)
            TenantId = reader.GetGuid(1)
            UserId = reader.GetGuid(2)
            Name = reader.GetString(3)
            AccountType = accountTypeFromString (reader.GetString(4)) |> Option.get
            CurrencyCode = reader.GetString(5)
            InstitutionName = Sql.nullableString reader 6
            ExternalId = Sql.nullableString reader 7
            CreditCardInfo = creditCardInfoFromJsonb reader 8
            IsOnBudget = reader.GetBoolean(9)
            IsActive = reader.GetBoolean(10)
            DeletedAt = Sql.nullableDateTimeOffset reader 11
            CreatedAt = Sql.dateTimeOffset reader 12
            UpdatedAt = Sql.dateTimeOffset reader 13
        }

    // ── CRUD implementations ─────────────────────────────────────────────────

    let getAsync (factory: IDbConnectionFactory) (tenantContext: TenantContext) (id: Guid) =
        task {
            use! conn = factory.OpenForTenantAsync(tenantContext)
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                """SELECT id, tenant_id, user_id, name, account_type, currency,
                          institution_name, external_id, credit_card_info,
                          is_on_budget, is_active, deleted_at, created_at, updated_at
                   FROM accounts WHERE id = $1 AND deleted_at IS NULL"""
            cmd.Parameters.AddWithValue("$1", id) |> ignore
            let! reader = cmd.ExecuteReaderAsync()
            use reader = reader
            let! hasRow = reader.ReadAsync()
            return if hasRow then Some(mapAccount reader) else None
        }

    let getByExternalIdAsync (factory: IDbConnectionFactory) (tenantContext: TenantContext) (externalId: string) =
        task {
            use! conn = factory.OpenForTenantAsync(tenantContext)
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                """SELECT id, tenant_id, user_id, name, account_type, currency,
                          institution_name, external_id, credit_card_info,
                          is_on_budget, is_active, created_at, updated_at
                   FROM accounts WHERE external_id = $1"""
            cmd.Parameters.AddWithValue("$1", externalId) |> ignore
            let! reader = cmd.ExecuteReaderAsync()
            use reader = reader
            let! hasRow = reader.ReadAsync()
            return if hasRow then Some(mapAccount reader) else None
        }

    let listAsync (factory: IDbConnectionFactory) (tenantContext: TenantContext) =
        task {
            use! conn = factory.OpenForTenantAsync(tenantContext)
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                """SELECT id, tenant_id, user_id, name, account_type, currency,
                          institution_name, external_id, credit_card_info,
                          is_on_budget, is_active, deleted_at, created_at, updated_at
                   FROM accounts
                   WHERE deleted_at IS NULL
                   ORDER BY created_at DESC"""
            let! reader = cmd.ExecuteReaderAsync()
            use reader = reader
            let accounts = ResizeArray<Account>()
            while! reader.ReadAsync() do
                accounts.Add(mapAccount reader)
            return accounts |> Seq.toList
        }

    let createAsync (factory: IDbConnectionFactory) (account: Account) =
        task {
            let ctx = { TenantId = account.TenantId; UserId = account.UserId }
            use! conn = factory.OpenForTenantAsync(ctx)
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                """INSERT INTO accounts (
                       id, tenant_id, user_id, name, account_type, currency,
                       institution_name, external_id, credit_card_info,
                       is_on_budget, is_active, deleted_at, created_at, updated_at
                   ) VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14)"""
            cmd.Parameters.AddWithValue("$1", account.Id) |> ignore
            cmd.Parameters.AddWithValue("$2", account.TenantId) |> ignore
            cmd.Parameters.AddWithValue("$3", account.UserId) |> ignore
            cmd.Parameters.AddWithValue("$4", account.Name) |> ignore
            cmd.Parameters.AddWithValue("$5", accountTypeToString account.AccountType) |> ignore
            cmd.Parameters.AddWithValue("$6", account.CurrencyCode) |> ignore
            match account.InstitutionName with
            | Some n -> cmd.Parameters.AddWithValue("$7", n) |> ignore
            | None -> cmd.Parameters.AddWithValue("$7", DBNull.Value) |> ignore
            match account.ExternalId with
            | Some e -> cmd.Parameters.AddWithValue("$8", e) |> ignore
            | None -> cmd.Parameters.AddWithValue("$8", DBNull.Value) |> ignore
            let ccParam = cmd.CreateParameter()
            ccParam.ParameterName <- "$9"
            ccParam.NpgsqlDbType <- NpgsqlTypes.NpgsqlDbType.Jsonb
            ccParam.Value <- creditCardInfoToJsonb account.CreditCardInfo
            cmd.Parameters.Add(ccParam) |> ignore
            cmd.Parameters.AddWithValue("$10", account.IsOnBudget) |> ignore
            cmd.Parameters.AddWithValue("$11", account.IsActive) |> ignore
            match account.DeletedAt with
            | Some d -> cmd.Parameters.AddWithValue("$12", d.UtcDateTime) |> ignore
            | None -> cmd.Parameters.AddWithValue("$12", DBNull.Value) |> ignore
            cmd.Parameters.AddWithValue("$13", account.CreatedAt.UtcDateTime) |> ignore
            cmd.Parameters.AddWithValue("$14", account.UpdatedAt.UtcDateTime) |> ignore
            let! _ = cmd.ExecuteNonQueryAsync()
            return account.Id
        }

    let updateAsync (factory: IDbConnectionFactory) (account: Account) =
        task {
            let ctx = { TenantId = account.TenantId; UserId = account.UserId }
            use! conn = factory.OpenForTenantAsync(ctx)
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                """UPDATE accounts SET
                       name = $1,
                       account_type = $2,
                       currency = $3,
                       institution_name = $4,
                       external_id = $5,
                       credit_card_info = $6,
                       is_on_budget = $7,
                       is_active = $8,
                       deleted_at = $9,
                       updated_at = $10
                   WHERE id = $11 AND deleted_at IS NULL"""
            cmd.Parameters.AddWithValue("$1", account.Name) |> ignore
            cmd.Parameters.AddWithValue("$2", accountTypeToString account.AccountType) |> ignore
            cmd.Parameters.AddWithValue("$3", account.CurrencyCode) |> ignore
            match account.InstitutionName with
            | Some n -> cmd.Parameters.AddWithValue("$4", n) |> ignore
            | None -> cmd.Parameters.AddWithValue("$4", DBNull.Value) |> ignore
            match account.ExternalId with
            | Some e -> cmd.Parameters.AddWithValue("$5", e) |> ignore
            | None -> cmd.Parameters.AddWithValue("$5", DBNull.Value) |> ignore
            let ccParam = cmd.CreateParameter()
            ccParam.ParameterName <- "$6"
            ccParam.NpgsqlDbType <- NpgsqlTypes.NpgsqlDbType.Jsonb
            ccParam.Value <- creditCardInfoToJsonb account.CreditCardInfo
            cmd.Parameters.Add(ccParam) |> ignore
            cmd.Parameters.AddWithValue("$7", account.IsOnBudget) |> ignore
            cmd.Parameters.AddWithValue("$8", account.IsActive) |> ignore
            match account.DeletedAt with
            | Some d -> cmd.Parameters.AddWithValue("$9", d.UtcDateTime) |> ignore
            | None -> cmd.Parameters.AddWithValue("$9", DBNull.Value) |> ignore
            cmd.Parameters.AddWithValue("$10", DateTimeOffset.UtcNow.UtcDateTime) |> ignore
            cmd.Parameters.AddWithValue("$11", account.Id) |> ignore
            let! _ = cmd.ExecuteNonQueryAsync()
            return ()
        }

    let deleteAsync (factory: IDbConnectionFactory) (tenantContext: TenantContext) (id: Guid) =
        task {
            use! conn = factory.OpenForTenantAsync(tenantContext)
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                """UPDATE accounts
                   SET deleted_at = now()
                   WHERE id = $1 AND deleted_at IS NULL"""
            cmd.Parameters.AddWithValue("$1", id) |> ignore
            let! _ = cmd.ExecuteNonQueryAsync()
            return ()
        }

    /// Create an IAccountRepository backed by the given connection factory and
    /// tenant context accessor.  ListAsync and DeleteAsync resolve the tenant
    /// context from the accessor; CreateAsync and UpdateAsync use the tenant
    /// embedded in the Account record.  GetAsync uses the accessor.
    let create (factory: IDbConnectionFactory) (accessor: ITenantContextAccessor) : IAccountRepository =
        let requireCtx () =
            match accessor.Context with
            | Some ctx -> ctx
            | None -> failwith "No tenant context available for the current operation"

        { new IAccountRepository with
            member _.GetAsync(id) = getAsync factory (requireCtx()) id
            member _.GetByExternalIdAsync(externalId) = getByExternalIdAsync factory (requireCtx()) externalId
            member _.ListAsync() = listAsync factory (requireCtx())
            member _.CreateAsync(account) = createAsync factory account
            member _.UpdateAsync(account) = updateAsync factory account
            member _.DeleteAsync(id) = deleteAsync factory (requireCtx()) id
        }
