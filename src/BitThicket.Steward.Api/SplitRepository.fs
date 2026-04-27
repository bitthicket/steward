namespace BitThicket.Steward.Api

open System
open System.Data.Common
open System.Text.Json
open System.Threading.Tasks
open Npgsql
open BitThicket.Steward.Api.Domain

/// Repository for tenant-scoped transaction splits.
type ISplitRepository =
    abstract GetAsync : id:Guid -> Task<TransactionSplit option>
    abstract ListByTransactionAsync : transactionId:Guid -> Task<TransactionSplit list>
    abstract CreateAsync : split:TransactionSplit -> Task<Guid>
    abstract UpdateAsync : split:TransactionSplit -> Task<unit>
    abstract DeleteAsync : id:Guid -> Task<unit>

module SplitRepository =

    // ── Money helpers ────────────────────────────────────────────────────────

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

    // ── Source helpers ───────────────────────────────────────────────────────

    let private sourceToJsonb (source: SplitSource) : obj =
        match source with
        | SplitSource.Manual ->
            box """{"type":"manual"}"""
        | SplitSource.Receipt attachmentId ->
            box $"""{{"type":"receipt","attachmentId":"{attachmentId}"}}"""
        | SplitSource.Enrichment providerKey ->
            box $"""{{"type":"enrichment","providerKey":"{providerKey}"}}"""

    let private sourceFromJsonb (reader: DbDataReader) (ordinal: int) : SplitSource =
        if reader.IsDBNull(ordinal) then SplitSource.Manual
        else
            let json = reader.GetString(ordinal)
            use doc = JsonDocument.Parse(json)
            let root = doc.RootElement
            match root.GetProperty("type").GetString() with
            | "manual" -> SplitSource.Manual
            | "receipt" ->
                let aid = Guid.Parse(root.GetProperty("attachmentId").GetString())
                SplitSource.Receipt aid
            | "enrichment" ->
                let pk = root.GetProperty("providerKey").GetString()
                SplitSource.Enrichment pk
            | _ -> failwith $"Unknown split source type in JSON: {json}"

    // ── Row mapping ──────────────────────────────────────────────────────────

    let internal mapSplit (reader: DbDataReader) : TransactionSplit =
        {
            Id = reader.GetGuid(0)
            TenantId = reader.GetGuid(1)
            TransactionId = reader.GetGuid(2)
            Amount = fromMinor (reader.GetInt64(3)) (reader.GetString(4))
            CategoryId = Sql.nullableGuid reader 5
            Description = Sql.nullableString reader 6
            Memo = Sql.nullableString reader 7
            Source = sourceFromJsonb reader 8
            SortOrder = reader.GetInt32(9)
            CreatedAt = Sql.dateTimeOffset reader 10
            UpdatedAt = Sql.dateTimeOffset reader 11
        }

    let private selectColumns =
        "id, tenant_id, transaction_id, amount_minor, currency, category_id, description, memo, source, sort_order, created_at, updated_at"

    // ── CRUD implementations ─────────────────────────────────────────────────

    let getAsync (factory: IDbConnectionFactory) (tenantContext: TenantContext) (id: Guid) =
        task {
            use! conn = factory.OpenForTenantAsync(tenantContext)
            use cmd = conn.CreateCommand()
            cmd.CommandText <- $"SELECT {selectColumns} FROM transaction_splits WHERE id = $1"
            cmd.Parameters.AddWithValue("$1", id) |> ignore
            let! reader = cmd.ExecuteReaderAsync()
            use reader = reader
            let! hasRow = reader.ReadAsync()
            return if hasRow then Some(mapSplit reader) else None
        }

    let listByTransactionAsync (factory: IDbConnectionFactory) (tenantContext: TenantContext) (transactionId: Guid) =
        task {
            use! conn = factory.OpenForTenantAsync(tenantContext)
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                $"SELECT {selectColumns} FROM transaction_splits WHERE transaction_id = $1 ORDER BY sort_order, created_at"
            cmd.Parameters.AddWithValue("$1", transactionId) |> ignore
            let! reader = cmd.ExecuteReaderAsync()
            use reader = reader
            let splits = ResizeArray<TransactionSplit>()
            while! reader.ReadAsync() do
                splits.Add(mapSplit reader)
            return splits |> Seq.toList
        }

    let createAsync (factory: IDbConnectionFactory) (split: TransactionSplit) =
        task {
            let ctx = { TenantId = split.TenantId; UserId = Guid.Empty }
            use! conn = factory.OpenForTenantAsync(ctx)
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                """INSERT INTO transaction_splits (
                       id, tenant_id, transaction_id, amount_minor, currency,
                       category_id, description, memo, source, sort_order, created_at, updated_at
                   ) VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12)"""
            cmd.Parameters.AddWithValue("$1", split.Id) |> ignore
            cmd.Parameters.AddWithValue("$2", split.TenantId) |> ignore
            cmd.Parameters.AddWithValue("$3", split.TransactionId) |> ignore
            cmd.Parameters.AddWithValue("$4", toMinor split.Amount) |> ignore
            cmd.Parameters.AddWithValue("$5", split.Amount.CurrencyCode) |> ignore
            match split.CategoryId with
            | Some cid -> cmd.Parameters.AddWithValue("$6", cid) |> ignore
            | None -> cmd.Parameters.AddWithValue("$6", DBNull.Value) |> ignore
            match split.Description with
            | Some d -> cmd.Parameters.AddWithValue("$7", d) |> ignore
            | None -> cmd.Parameters.AddWithValue("$7", DBNull.Value) |> ignore
            match split.Memo with
            | Some m -> cmd.Parameters.AddWithValue("$8", m) |> ignore
            | None -> cmd.Parameters.AddWithValue("$8", DBNull.Value) |> ignore
            let sourceParam = cmd.CreateParameter()
            sourceParam.ParameterName <- "$9"
            sourceParam.NpgsqlDbType <- NpgsqlTypes.NpgsqlDbType.Jsonb
            sourceParam.Value <- sourceToJsonb split.Source
            cmd.Parameters.Add(sourceParam) |> ignore
            cmd.Parameters.AddWithValue("$10", split.SortOrder) |> ignore
            cmd.Parameters.AddWithValue("$11", split.CreatedAt.UtcDateTime) |> ignore
            cmd.Parameters.AddWithValue("$12", split.UpdatedAt.UtcDateTime) |> ignore
            let! _ = cmd.ExecuteNonQueryAsync()
            return split.Id
        }

    let updateAsync (factory: IDbConnectionFactory) (split: TransactionSplit) =
        task {
            let ctx = { TenantId = split.TenantId; UserId = Guid.Empty }
            use! conn = factory.OpenForTenantAsync(ctx)
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                """UPDATE transaction_splits SET
                       amount_minor = $1,
                       currency = $2,
                       category_id = $3,
                       description = $4,
                       memo = $5,
                       source = $6,
                       sort_order = $7,
                       updated_at = $8
                   WHERE id = $9"""
            cmd.Parameters.AddWithValue("$1", toMinor split.Amount) |> ignore
            cmd.Parameters.AddWithValue("$2", split.Amount.CurrencyCode) |> ignore
            match split.CategoryId with
            | Some cid -> cmd.Parameters.AddWithValue("$3", cid) |> ignore
            | None -> cmd.Parameters.AddWithValue("$3", DBNull.Value) |> ignore
            match split.Description with
            | Some d -> cmd.Parameters.AddWithValue("$4", d) |> ignore
            | None -> cmd.Parameters.AddWithValue("$4", DBNull.Value) |> ignore
            match split.Memo with
            | Some m -> cmd.Parameters.AddWithValue("$5", m) |> ignore
            | None -> cmd.Parameters.AddWithValue("$5", DBNull.Value) |> ignore
            let sourceParam = cmd.CreateParameter()
            sourceParam.ParameterName <- "$6"
            sourceParam.NpgsqlDbType <- NpgsqlTypes.NpgsqlDbType.Jsonb
            sourceParam.Value <- sourceToJsonb split.Source
            cmd.Parameters.Add(sourceParam) |> ignore
            cmd.Parameters.AddWithValue("$7", split.SortOrder) |> ignore
            cmd.Parameters.AddWithValue("$8", DateTimeOffset.UtcNow.UtcDateTime) |> ignore
            cmd.Parameters.AddWithValue("$9", split.Id) |> ignore
            let! _ = cmd.ExecuteNonQueryAsync()
            return ()
        }

    let deleteAsync (factory: IDbConnectionFactory) (tenantContext: TenantContext) (id: Guid) =
        task {
            use! conn = factory.OpenForTenantAsync(tenantContext)
            use cmd = conn.CreateCommand()
            cmd.CommandText <- "DELETE FROM transaction_splits WHERE id = $1"
            cmd.Parameters.AddWithValue("$1", id) |> ignore
            let! _ = cmd.ExecuteNonQueryAsync()
            return ()
        }

    let create (factory: IDbConnectionFactory) (accessor: ITenantContextAccessor) : ISplitRepository =
        let requireCtx () =
            match accessor.Context with
            | Some ctx -> ctx
            | None -> failwith "No tenant context available for the current operation"

        { new ISplitRepository with
            member _.GetAsync(id) = getAsync factory (requireCtx()) id
            member _.ListByTransactionAsync(transactionId) = listByTransactionAsync factory (requireCtx()) transactionId
            member _.CreateAsync(split) = createAsync factory split
            member _.UpdateAsync(split) = updateAsync factory split
            member _.DeleteAsync(id) = deleteAsync factory (requireCtx()) id
        }
