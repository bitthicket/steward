namespace BitThicket.Steward.Api

open System
open System.Data.Common
open System.Text.Json
open System.Threading.Tasks
open Npgsql
open BitThicket.Steward.Api.Domain

/// Filter criteria for listing transactions.
type TransactionListFilter = {
    AccountId: Guid option
    From: DateTimeOffset option
    To: DateTimeOffset option
    Status: TransactionStatus option
    Limit: int
    Cursor: (DateTimeOffset * Guid) option
}

/// Repository for tenant-scoped transactions.
type ITransactionRepository =
    abstract GetAsync : id:Guid -> Task<Transaction option>
    abstract GetByExternalIdAsync : externalId:string * accountId:Guid -> Task<Transaction option>
    abstract ListAsync : unit -> Task<Transaction list>
    abstract ListByAccountAsync : accountId:Guid -> Task<Transaction list>
    abstract ListAsync : filter:TransactionListFilter -> Task<Transaction list>
    abstract CreateAsync : transaction:Transaction -> Task<Guid>
    abstract UpdateAsync : transaction:Transaction -> Task<unit>
    abstract DeleteAsync : id:Guid -> Task<unit>
    abstract DeleteByExternalIdsAsync : externalIds:string list -> Task<int>
    abstract ListMatchCandidatesAsync : accountId:Guid -> Task<Transaction list>
    abstract ListNeedsReviewAsync : unit -> Task<Transaction list>

module TransactionRepository =

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

    // ── Domain helpers ───────────────────────────────────────────────────────

    let private statusToString (s: TransactionStatus) : string =
        match s with
        | TransactionStatus.Pending     -> "pending"
        | TransactionStatus.NeedsReview -> "needs_review"
        | TransactionStatus.Cleared     -> "cleared"
        | TransactionStatus.Reconciled  -> "reconciled"

    let internal statusFromString (s: string) : TransactionStatus =
        match s.ToLowerInvariant() with
        | "pending"     -> TransactionStatus.Pending
        | "needs_review"-> TransactionStatus.NeedsReview
        | "cleared"     -> TransactionStatus.Cleared
        | "reconciled"  -> TransactionStatus.Reconciled
        | _             -> failwith $"Unknown transaction status: {s}"

    let private sourceToJsonb (source: TransactionSource) : obj =
        match source with
        | TransactionSource.Manual ->
            box """{"type":"manual"}"""
        | TransactionSource.DataFeed provider ->
            box $"""{{"type":"data_feed","provider":"{provider}"}}"""
        | TransactionSource.Import format ->
            box $"""{{"type":"import","format":"{format}"}}"""

    let private sourceFromJsonb (reader: DbDataReader) (ordinal: int) : TransactionSource =
        if reader.IsDBNull(ordinal) then TransactionSource.Manual
        else
            let json = reader.GetString(ordinal)
            use doc = JsonDocument.Parse(json)
            let root = doc.RootElement
            match root.GetProperty("type").GetString() with
            | "manual"      -> TransactionSource.Manual
            | "data_feed"   -> TransactionSource.DataFeed(root.GetProperty("provider").GetString())
            | "import"      -> TransactionSource.Import(root.GetProperty("format").GetString())
            | _             -> failwith $"Unknown transaction source type in JSON: {json}"

    // ── Row mapping ──────────────────────────────────────────────────────────

    let internal mapTransaction (reader: DbDataReader) : Transaction =
        {
            Id = reader.GetGuid(0)
            TenantId = reader.GetGuid(1)
            AccountId = reader.GetGuid(2)
            OccurredAt = Sql.dateTimeOffset reader 3
            PostedAt = Sql.nullableDateTimeOffset reader 4
            Amount = fromMinor (reader.GetInt64(5)) (reader.GetString(6))
            Description = reader.GetString(7)
            Merchant = Sql.nullableString reader 8
            Memo = Sql.nullableString reader 9
            CategoryId = Sql.nullableGuid reader 10
            Source = sourceFromJsonb reader 11
            ExternalId = Sql.nullableString reader 12
            MatchedTransactionId = Sql.nullableGuid reader 13
            TransferAccountId = Sql.nullableGuid reader 14
            Status = statusFromString (reader.GetString(15))
            MatchConfidence = Sql.nullableDecimal reader 16
            SyncEventId = Sql.nullableGuid reader 17
            CreatedAt = Sql.dateTimeOffset reader 18
            UpdatedAt = Sql.dateTimeOffset reader 19
            DeletedAt = Sql.nullableDateTimeOffset reader 20
        }

    // ── Column list helper ───────────────────────────────────────────────────

    let private selectColumns =
        """id, tenant_id, account_id, occurred_at, posted_at,
           amount_minor, currency, description, merchant, memo,
           category_id, source, external_id, matched_transaction_id, transfer_account_id,
           status, match_confidence, sync_event_id, created_at, updated_at, deleted_at"""

    // ── CRUD implementations ─────────────────────────────────────────────────

    let getAsync (factory: IDbConnectionFactory) (tenantContext: TenantContext) (id: Guid) =
        task {
            use! conn = factory.OpenForTenantAsync(tenantContext)
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                $"""SELECT {selectColumns}
                   FROM transactions WHERE id = $1 AND deleted_at IS NULL"""
            cmd.Parameters.AddWithValue("$1", id) |> ignore
            let! reader = cmd.ExecuteReaderAsync()
            use reader = reader
            let! hasRow = reader.ReadAsync()
            return if hasRow then Some(mapTransaction reader) else None
        }

    let getByExternalIdAsync (factory: IDbConnectionFactory) (tenantContext: TenantContext) (externalId: string, accountId: Guid) =
        task {
            use! conn = factory.OpenForTenantAsync(tenantContext)
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                $"""SELECT {selectColumns}
                   FROM transactions WHERE external_id = $1 AND account_id = $2 AND deleted_at IS NULL"""
            cmd.Parameters.AddWithValue("$1", externalId) |> ignore
            cmd.Parameters.AddWithValue("$2", accountId) |> ignore
            let! reader = cmd.ExecuteReaderAsync()
            use reader = reader
            let! hasRow = reader.ReadAsync()
            return if hasRow then Some(mapTransaction reader) else None
        }

    let listAllAsync (factory: IDbConnectionFactory) (tenantContext: TenantContext) =
        task {
            use! conn = factory.OpenForTenantAsync(tenantContext)
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                $"""SELECT {selectColumns}
                   FROM transactions
                   WHERE deleted_at IS NULL
                   ORDER BY occurred_at DESC
                   LIMIT 100"""
            let! reader = cmd.ExecuteReaderAsync()
            use reader = reader
            let txns = ResizeArray<Transaction>()
            while! reader.ReadAsync() do
                txns.Add(mapTransaction reader)
            return txns |> Seq.toList
        }

    let listByAccountAsync (factory: IDbConnectionFactory) (tenantContext: TenantContext) (accountId: Guid) =
        task {
            use! conn = factory.OpenForTenantAsync(tenantContext)
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                $"""SELECT {selectColumns}
                   FROM transactions
                   WHERE account_id = $1 AND deleted_at IS NULL
                   ORDER BY occurred_at DESC"""
            cmd.Parameters.AddWithValue("$1", accountId) |> ignore
            let! reader = cmd.ExecuteReaderAsync()
            use reader = reader
            let transactions = ResizeArray<Transaction>()
            while! reader.ReadAsync() do
                transactions.Add(mapTransaction reader)
            return transactions |> Seq.toList
        }

    let listAsync (factory: IDbConnectionFactory) (tenantContext: TenantContext) (filter: TransactionListFilter) =
        task {
            use! conn = factory.OpenForTenantAsync(tenantContext)
            use cmd = conn.CreateCommand()

            let conditions = ResizeArray<string>()
            conditions.Add("deleted_at IS NULL")

            match filter.AccountId with
            | Some aid ->
                conditions.Add("account_id = $1")
                cmd.Parameters.AddWithValue("$1", aid) |> ignore
            | None -> ()

            let mutable paramIndex = 2

            match filter.From with
            | Some f ->
                conditions.Add($"occurred_at >= ${paramIndex}")
                cmd.Parameters.AddWithValue($"${paramIndex}", f.UtcDateTime) |> ignore
                paramIndex <- paramIndex + 1
            | None -> ()

            match filter.To with
            | Some t ->
                conditions.Add($"occurred_at <= ${paramIndex}")
                cmd.Parameters.AddWithValue($"${paramIndex}", t.UtcDateTime) |> ignore
                paramIndex <- paramIndex + 1
            | None -> ()

            match filter.Status with
            | Some s ->
                conditions.Add($"status = ${paramIndex}")
                cmd.Parameters.AddWithValue($"${paramIndex}", statusToString s) |> ignore
                paramIndex <- paramIndex + 1
            | None -> ()

            match filter.Cursor with
            | Some (occurredAt, id) ->
                conditions.Add($"(occurred_at, id) < (${paramIndex}, ${paramIndex + 1})")
                cmd.Parameters.AddWithValue($"${paramIndex}", occurredAt.UtcDateTime) |> ignore
                cmd.Parameters.AddWithValue($"${paramIndex + 1}", id) |> ignore
                paramIndex <- paramIndex + 2
            | None -> ()

            let whereClause = String.concat " AND " conditions
            let limit = Math.Max(1, Math.Min(filter.Limit, 250))

            cmd.CommandText <-
                $"""SELECT {selectColumns}
                   FROM transactions
                   WHERE {whereClause}
                   ORDER BY occurred_at DESC, id DESC
                   LIMIT {limit + 1}"""

            let! reader = cmd.ExecuteReaderAsync()
            use reader = reader
            let transactions = ResizeArray<Transaction>()
            while! reader.ReadAsync() do
                transactions.Add(mapTransaction reader)
            return transactions |> Seq.toList
        }

    let createAsync (factory: IDbConnectionFactory) (txn: Transaction) =
        task {
            let ctx = { TenantId = txn.TenantId; UserId = Guid.Empty }
            use! conn = factory.OpenForTenantAsync(ctx)
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                """INSERT INTO transactions (
                       id, tenant_id, account_id, occurred_at, posted_at,
                       amount_minor, currency, description, merchant, memo,
                       category_id, source, external_id, matched_transaction_id, transfer_account_id,
                       status, match_confidence, sync_event_id, created_at, updated_at, deleted_at
                   ) VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10,
                             $11, $12, $13, $14, $15, $16, $17, $18, $19, $20, $21)"""
            cmd.Parameters.AddWithValue("$1", txn.Id) |> ignore
            cmd.Parameters.AddWithValue("$2", txn.TenantId) |> ignore
            cmd.Parameters.AddWithValue("$3", txn.AccountId) |> ignore
            cmd.Parameters.AddWithValue("$4", txn.OccurredAt.UtcDateTime) |> ignore
            match txn.PostedAt with
            | Some d -> cmd.Parameters.AddWithValue("$5", d.UtcDateTime) |> ignore
            | None -> cmd.Parameters.AddWithValue("$5", DBNull.Value) |> ignore
            cmd.Parameters.AddWithValue("$6", toMinor txn.Amount) |> ignore
            cmd.Parameters.AddWithValue("$7", txn.Amount.CurrencyCode) |> ignore
            cmd.Parameters.AddWithValue("$8", txn.Description) |> ignore
            match txn.Merchant with
            | Some m -> cmd.Parameters.AddWithValue("$9", m) |> ignore
            | None -> cmd.Parameters.AddWithValue("$9", DBNull.Value) |> ignore
            match txn.Memo with
            | Some m -> cmd.Parameters.AddWithValue("$10", m) |> ignore
            | None -> cmd.Parameters.AddWithValue("$10", DBNull.Value) |> ignore
            match txn.CategoryId with
            | Some cid -> cmd.Parameters.AddWithValue("$11", cid) |> ignore
            | None -> cmd.Parameters.AddWithValue("$11", DBNull.Value) |> ignore
            let sourceParam = cmd.CreateParameter()
            sourceParam.ParameterName <- "$12"
            sourceParam.NpgsqlDbType <- NpgsqlTypes.NpgsqlDbType.Jsonb
            sourceParam.Value <- sourceToJsonb txn.Source
            cmd.Parameters.Add(sourceParam) |> ignore
            match txn.ExternalId with
            | Some e -> cmd.Parameters.AddWithValue("$13", e) |> ignore
            | None -> cmd.Parameters.AddWithValue("$13", DBNull.Value) |> ignore
            match txn.MatchedTransactionId with
            | Some mid -> cmd.Parameters.AddWithValue("$14", mid) |> ignore
            | None -> cmd.Parameters.AddWithValue("$14", DBNull.Value) |> ignore
            match txn.TransferAccountId with
            | Some tid -> cmd.Parameters.AddWithValue("$15", tid) |> ignore
            | None -> cmd.Parameters.AddWithValue("$15", DBNull.Value) |> ignore
            cmd.Parameters.AddWithValue("$16", statusToString txn.Status) |> ignore
            match txn.MatchConfidence with
            | Some c -> cmd.Parameters.AddWithValue("$17", c) |> ignore
            | None -> cmd.Parameters.AddWithValue("$17", DBNull.Value) |> ignore
            match txn.SyncEventId with
            | Some sid -> cmd.Parameters.AddWithValue("$18", sid) |> ignore
            | None -> cmd.Parameters.AddWithValue("$18", DBNull.Value) |> ignore
            cmd.Parameters.AddWithValue("$19", txn.CreatedAt.UtcDateTime) |> ignore
            cmd.Parameters.AddWithValue("$20", txn.UpdatedAt.UtcDateTime) |> ignore
            match txn.DeletedAt with
            | Some d -> cmd.Parameters.AddWithValue("$21", d.UtcDateTime) |> ignore
            | None -> cmd.Parameters.AddWithValue("$21", DBNull.Value) |> ignore
            let! _ = cmd.ExecuteNonQueryAsync()
            return txn.Id
        }

    let updateAsync (factory: IDbConnectionFactory) (txn: Transaction) =
        task {
            let ctx = { TenantId = txn.TenantId; UserId = Guid.Empty }
            use! conn = factory.OpenForTenantAsync(ctx)
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                """UPDATE transactions SET
                       account_id = $1,
                       occurred_at = $2,
                       posted_at = $3,
                       amount_minor = $4,
                       currency = $5,
                       description = $6,
                       merchant = $7,
                       memo = $8,
                       category_id = $9,
                       source = $10,
                       external_id = $11,
                       matched_transaction_id = $12,
                       transfer_account_id = $13,
                       status = $14,
                       match_confidence = $15,
                       sync_event_id = $16,
                       updated_at = $17,
                       deleted_at = $18
                   WHERE id = $19 AND deleted_at IS NULL"""
            cmd.Parameters.AddWithValue("$1", txn.AccountId) |> ignore
            cmd.Parameters.AddWithValue("$2", txn.OccurredAt.UtcDateTime) |> ignore
            match txn.PostedAt with
            | Some d -> cmd.Parameters.AddWithValue("$3", d.UtcDateTime) |> ignore
            | None -> cmd.Parameters.AddWithValue("$3", DBNull.Value) |> ignore
            cmd.Parameters.AddWithValue("$4", toMinor txn.Amount) |> ignore
            cmd.Parameters.AddWithValue("$5", txn.Amount.CurrencyCode) |> ignore
            cmd.Parameters.AddWithValue("$6", txn.Description) |> ignore
            match txn.Merchant with
            | Some m -> cmd.Parameters.AddWithValue("$7", m) |> ignore
            | None -> cmd.Parameters.AddWithValue("$7", DBNull.Value) |> ignore
            match txn.Memo with
            | Some m -> cmd.Parameters.AddWithValue("$8", m) |> ignore
            | None -> cmd.Parameters.AddWithValue("$8", DBNull.Value) |> ignore
            match txn.CategoryId with
            | Some cid -> cmd.Parameters.AddWithValue("$9", cid) |> ignore
            | None -> cmd.Parameters.AddWithValue("$9", DBNull.Value) |> ignore
            let sourceParam = cmd.CreateParameter()
            sourceParam.ParameterName <- "$10"
            sourceParam.NpgsqlDbType <- NpgsqlTypes.NpgsqlDbType.Jsonb
            sourceParam.Value <- sourceToJsonb txn.Source
            cmd.Parameters.Add(sourceParam) |> ignore
            match txn.ExternalId with
            | Some e -> cmd.Parameters.AddWithValue("$11", e) |> ignore
            | None -> cmd.Parameters.AddWithValue("$11", DBNull.Value) |> ignore
            match txn.MatchedTransactionId with
            | Some mid -> cmd.Parameters.AddWithValue("$12", mid) |> ignore
            | None -> cmd.Parameters.AddWithValue("$12", DBNull.Value) |> ignore
            match txn.TransferAccountId with
            | Some tid -> cmd.Parameters.AddWithValue("$13", tid) |> ignore
            | None -> cmd.Parameters.AddWithValue("$13", DBNull.Value) |> ignore
            cmd.Parameters.AddWithValue("$14", statusToString txn.Status) |> ignore
            match txn.MatchConfidence with
            | Some c -> cmd.Parameters.AddWithValue("$15", c) |> ignore
            | None -> cmd.Parameters.AddWithValue("$15", DBNull.Value) |> ignore
            match txn.SyncEventId with
            | Some sid -> cmd.Parameters.AddWithValue("$16", sid) |> ignore
            | None -> cmd.Parameters.AddWithValue("$16", DBNull.Value) |> ignore
            cmd.Parameters.AddWithValue("$17", DateTimeOffset.UtcNow.UtcDateTime) |> ignore
            match txn.DeletedAt with
            | Some d -> cmd.Parameters.AddWithValue("$18", d.UtcDateTime) |> ignore
            | None -> cmd.Parameters.AddWithValue("$18", DBNull.Value) |> ignore
            cmd.Parameters.AddWithValue("$19", txn.Id) |> ignore
            let! _ = cmd.ExecuteNonQueryAsync()
            return ()
        }

    let deleteAsync (factory: IDbConnectionFactory) (tenantContext: TenantContext) (id: Guid) =
        task {
            use! conn = factory.OpenForTenantAsync(tenantContext)
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                """UPDATE transactions
                   SET deleted_at = now()
                   WHERE id = $1 AND deleted_at IS NULL"""
            cmd.Parameters.AddWithValue("$1", id) |> ignore
            let! _ = cmd.ExecuteNonQueryAsync()
            return ()
        }

    let deleteByExternalIdsAsync (factory: IDbConnectionFactory) (tenantContext: TenantContext) (externalIds: string list) =
        task {
            if externalIds.IsEmpty then return 0
            else
                use! conn = factory.OpenForTenantAsync(tenantContext)
                use cmd = conn.CreateCommand()
                cmd.CommandText <- "DELETE FROM transactions WHERE external_id = ANY($1)"
                let arr = Array.ofList externalIds
                cmd.Parameters.AddWithValue("$1", arr) |> ignore
                let! rows = cmd.ExecuteNonQueryAsync()
                return rows
        }

    let listMatchCandidatesAsync (factory: IDbConnectionFactory) (tenantContext: TenantContext) (accountId: Guid) =
        task {
            use! conn = factory.OpenForTenantAsync(tenantContext)
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                $"""SELECT {selectColumns}
                   FROM transactions
                   WHERE account_id = $1
                     AND source ->> 'type' = 'manual'
                     AND status IN ('pending', 'cleared')
                     AND matched_transaction_id IS NULL
                     AND deleted_at IS NULL
                   ORDER BY occurred_at DESC"""
            cmd.Parameters.AddWithValue("$1", accountId) |> ignore
            let! reader = cmd.ExecuteReaderAsync()
            use reader = reader
            let txns = ResizeArray<Transaction>()
            while! reader.ReadAsync() do
                txns.Add(mapTransaction reader)
            return txns |> Seq.toList
        }

    let listNeedsReviewAsync (factory: IDbConnectionFactory) (tenantContext: TenantContext) =
        task {
            use! conn = factory.OpenForTenantAsync(tenantContext)
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                $"""SELECT {selectColumns}
                   FROM transactions
                   WHERE status = 'needs_review'
                     AND deleted_at IS NULL
                   ORDER BY occurred_at DESC"""
            let! reader = cmd.ExecuteReaderAsync()
            use reader = reader
            let txns = ResizeArray<Transaction>()
            while! reader.ReadAsync() do
                txns.Add(mapTransaction reader)
            return txns |> Seq.toList
        }

    /// Create an ITransactionRepository backed by the given connection factory and
    /// tenant context accessor.
    let create (factory: IDbConnectionFactory) (accessor: ITenantContextAccessor) : ITransactionRepository =
        let requireCtx () =
            match accessor.Context with
            | Some ctx -> ctx
            | None -> failwith "No tenant context available for the current operation"

        { new ITransactionRepository with
            member _.GetAsync(id) = getAsync factory (requireCtx()) id
            member _.GetByExternalIdAsync(externalId, accountId) = getByExternalIdAsync factory (requireCtx()) (externalId, accountId)
            member _.ListAsync() = listAllAsync factory (requireCtx())
            member _.ListByAccountAsync(accountId) = listByAccountAsync factory (requireCtx()) accountId
            member _.ListAsync(filter) = listAsync factory (requireCtx()) filter
            member _.CreateAsync(txn) = createAsync factory txn
            member _.UpdateAsync(txn) = updateAsync factory txn
            member _.DeleteAsync(id) = deleteAsync factory (requireCtx()) id
            member _.DeleteByExternalIdsAsync(externalIds) = deleteByExternalIdsAsync factory (requireCtx()) externalIds
            member _.ListMatchCandidatesAsync(accountId) = listMatchCandidatesAsync factory (requireCtx()) accountId
            member _.ListNeedsReviewAsync() = listNeedsReviewAsync factory (requireCtx())
        }
