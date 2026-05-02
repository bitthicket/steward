namespace BitThicket.Steward.Api

open System
open System.Data.Common
open System.Threading.Tasks
open Npgsql
open BitThicket.Steward.Api.Domain

/// Repository for tenant-scoped reconciliations.
type IReconciliationRepository =
    abstract CreateAsync : reconciliation:Reconciliation -> Task<Guid>
    abstract GetAsync : id:Guid -> Task<Reconciliation option>
    abstract GetWithTransactionsAsync : id:Guid -> Task<(Reconciliation * Transaction list) option>
    abstract ListAsync : unit -> Task<Reconciliation list>
    abstract ListCandidateTransactionsAsync : accountId:Guid * statementDate:DateOnly -> Task<Transaction list>
    abstract UpdateIncludedTransactionsAsync : id:Guid * included:Guid list * excluded:Guid list -> Task<unit>
    abstract CompleteAsync : id:Guid * force:bool * note:string option -> Task<Result<int64, string>>
    abstract AbortAsync : id:Guid -> Task<unit>

module ReconciliationRepository =

    // ── Domain helpers ───────────────────────────────────────────────────────

    let private statusToString (s: ReconciliationStatus) : string =
        match s with
        | ReconciliationStatus.Open     -> "open"
        | ReconciliationStatus.Completed -> "completed"
        | ReconciliationStatus.Aborted  -> "aborted"

    let private statusFromString (s: string) : ReconciliationStatus =
        match s.ToLowerInvariant() with
        | "open"      -> ReconciliationStatus.Open
        | "completed" -> ReconciliationStatus.Completed
        | "aborted"   -> ReconciliationStatus.Aborted
        | _           -> failwith $"Unknown reconciliation status: {s}"

    // ── Row mapping ──────────────────────────────────────────────────────────

    let internal mapReconciliation (reader: DbDataReader) : Reconciliation =
        {
            Id = reader.GetGuid(0)
            TenantId = reader.GetGuid(1)
            AccountId = reader.GetGuid(2)
            StatementDate = Sql.dateOnly reader 3
            StatementBalance = MoneyHelpers.fromMinorUnits (reader.GetInt64(4)) (reader.GetString(5))
            Status = statusFromString (reader.GetString(6))
            Note = Sql.nullableString reader 7
            CreatedByUserId = reader.GetGuid(8)
            StartedAt = Sql.dateTimeOffset reader 9
            CompletedAt = Sql.nullableDateTimeOffset reader 10
        }

    // ── CRUD implementations ─────────────────────────────────────────────────

    let createAsync (factory: IDbConnectionFactory) (recon: Reconciliation) =
        task {
            let ctx = { TenantId = recon.TenantId; UserId = recon.CreatedByUserId }
            use! conn = factory.OpenForTenantAsync(ctx)
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                """INSERT INTO reconciliations (
                       id, tenant_id, account_id, statement_date, statement_balance_minor,
                       currency, status, note, created_by_user_id, started_at, completed_at
                   ) VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11)"""
            cmd.Parameters.AddWithValue("$1", recon.Id) |> ignore
            cmd.Parameters.AddWithValue("$2", recon.TenantId) |> ignore
            cmd.Parameters.AddWithValue("$3", recon.AccountId) |> ignore
            cmd.Parameters.AddWithValue("$4", recon.StatementDate) |> ignore
            cmd.Parameters.AddWithValue("$5", MoneyHelpers.toMinorUnits recon.StatementBalance) |> ignore
            cmd.Parameters.AddWithValue("$6", recon.StatementBalance.CurrencyCode) |> ignore
            cmd.Parameters.AddWithValue("$7", statusToString recon.Status) |> ignore
            match recon.Note with
            | Some n -> cmd.Parameters.AddWithValue("$8", n) |> ignore
            | None -> cmd.Parameters.AddWithValue("$8", DBNull.Value) |> ignore
            cmd.Parameters.AddWithValue("$9", recon.CreatedByUserId) |> ignore
            cmd.Parameters.AddWithValue("$10", recon.StartedAt.UtcDateTime) |> ignore
            match recon.CompletedAt with
            | Some d -> cmd.Parameters.AddWithValue("$11", d.UtcDateTime) |> ignore
            | None -> cmd.Parameters.AddWithValue("$11", DBNull.Value) |> ignore
            let! _ = cmd.ExecuteNonQueryAsync()
            return recon.Id
        }

    let getAsync (factory: IDbConnectionFactory) (tenantContext: TenantContext) (id: Guid) =
        task {
            use! conn = factory.OpenForTenantAsync(tenantContext)
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                """SELECT id, tenant_id, account_id, statement_date, statement_balance_minor,
                          currency, status, note, created_by_user_id, started_at, completed_at
                   FROM reconciliations WHERE id = $1"""
            cmd.Parameters.AddWithValue("$1", id) |> ignore
            let! reader = cmd.ExecuteReaderAsync()
            use reader = reader
            let! hasRow = reader.ReadAsync()
            return if hasRow then Some(mapReconciliation reader) else None
        }

    let listAsync (factory: IDbConnectionFactory) (tenantContext: TenantContext) =
        task {
            use! conn = factory.OpenForTenantAsync(tenantContext)
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                """SELECT id, tenant_id, account_id, statement_date, statement_balance_minor,
                          currency, status, note, created_by_user_id, started_at, completed_at
                   FROM reconciliations ORDER BY started_at DESC"""
            let! reader = cmd.ExecuteReaderAsync()
            use reader = reader
            let recons = ResizeArray<Reconciliation>()
            while! reader.ReadAsync() do
                recons.Add(mapReconciliation reader)
            return recons |> Seq.toList
        }

    let getWithTransactionsAsync (factory: IDbConnectionFactory) (tenantContext: TenantContext) (id: Guid) =
        task {
            use! conn = factory.OpenForTenantAsync(tenantContext)
            // Fetch reconciliation
            use reconCmd = conn.CreateCommand()
            reconCmd.CommandText <-
                """SELECT id, tenant_id, account_id, statement_date, statement_balance_minor,
                          currency, status, note, created_by_user_id, started_at, completed_at
                   FROM reconciliations WHERE id = $1"""
            reconCmd.Parameters.AddWithValue("$1", id) |> ignore
            let! reconReader = reconCmd.ExecuteReaderAsync()
            use reconReader = reconReader
            let! hasRecon = reconReader.ReadAsync()
            if not hasRecon then return None else

            let recon = mapReconciliation reconReader
            do! reconReader.CloseAsync()

            // Fetch linked transactions
            use txnsCmd = conn.CreateCommand()
            txnsCmd.CommandText <-
                """SELECT t.id, t.tenant_id, t.account_id, t.occurred_at, t.posted_at,
                          t.amount_minor, t.currency, t.description, t.merchant, t.memo,
                          t.category_id, t.source, t.external_id, t.matched_transaction_id, t.transfer_account_id,
                          t.status, t.match_confidence, t.sync_event_id, t.created_at, t.updated_at
                   FROM transactions t
                   JOIN reconciliation_transactions rt ON rt.transaction_id = t.id
                   WHERE rt.reconciliation_id = $1
                   ORDER BY t.posted_at DESC"""
            txnsCmd.Parameters.AddWithValue("$1", id) |> ignore
            let! txnReader = txnsCmd.ExecuteReaderAsync()
            use txnReader = txnReader
            let txns = ResizeArray<Transaction>()
            while! txnReader.ReadAsync() do
                txns.Add(TransactionRepository.mapTransaction txnReader)
            return Some(recon, txns |> Seq.toList)
        }

    let listCandidateTransactionsAsync (factory: IDbConnectionFactory) (tenantContext: TenantContext) (accountId: Guid, statementDate: DateOnly) =
        task {
            use! conn = factory.OpenForTenantAsync(tenantContext)
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                """SELECT t.id, t.tenant_id, t.account_id, t.occurred_at, t.posted_at,
                          t.amount_minor, t.currency, t.description, t.merchant, t.memo,
                          t.category_id, t.source, t.external_id, t.matched_transaction_id, t.transfer_account_id,
                          t.status, t.match_confidence, t.sync_event_id, t.created_at, t.updated_at
                   FROM transactions t
                   WHERE t.account_id = $1
                     AND t.posted_at IS NOT NULL
                     AND t.posted_at::date <= $2
                     AND t.status = 'cleared'
                     AND t.id NOT IN (
                         SELECT rt.transaction_id
                         FROM reconciliation_transactions rt
                         JOIN reconciliations r ON r.id = rt.reconciliation_id
                         WHERE r.account_id = $1 AND r.status = 'completed'
                     )
                   ORDER BY t.posted_at DESC"""
            cmd.Parameters.AddWithValue("$1", accountId) |> ignore
            cmd.Parameters.AddWithValue("$2", statementDate) |> ignore
            let! reader = cmd.ExecuteReaderAsync()
            use reader = reader
            let txns = ResizeArray<Transaction>()
            while! reader.ReadAsync() do
                txns.Add(TransactionRepository.mapTransaction reader)
            return txns |> Seq.toList
        }

    let updateIncludedTransactionsAsync (factory: IDbConnectionFactory) (tenantContext: TenantContext) (id: Guid, included: Guid list, excluded: Guid list) =
        task {
            use! conn = factory.OpenForTenantAsync(tenantContext)
            use tx = conn.BeginTransaction()

            // Add newly included transactions
            for txnId in included do
                use cmd = conn.CreateCommand()
                cmd.Transaction <- tx
                cmd.CommandText <-
                    """INSERT INTO reconciliation_transactions (reconciliation_id, transaction_id)
                       VALUES ($1, $2)
                       ON CONFLICT (reconciliation_id, transaction_id) DO NOTHING"""
                cmd.Parameters.AddWithValue("$1", id) |> ignore
                cmd.Parameters.AddWithValue("$2", txnId) |> ignore
                let! _ = cmd.ExecuteNonQueryAsync()
                ()

            // Remove excluded transactions
            for txnId in excluded do
                use cmd = conn.CreateCommand()
                cmd.Transaction <- tx
                cmd.CommandText <- "DELETE FROM reconciliation_transactions WHERE reconciliation_id = $1 AND transaction_id = $2"
                cmd.Parameters.AddWithValue("$1", id) |> ignore
                cmd.Parameters.AddWithValue("$2", txnId) |> ignore
                let! _ = cmd.ExecuteNonQueryAsync()
                ()

            do! tx.CommitAsync()
            return ()
        }

    let completeAsync (factory: IDbConnectionFactory) (tenantContext: TenantContext) (id: Guid, force: bool, note: string option) =
        task {
            use! conn = factory.OpenForTenantAsync(tenantContext)
            use tx = conn.BeginTransaction()

            // Fetch reconciliation and ensure it's open
            use reconCmd = conn.CreateCommand()
            reconCmd.Transaction <- tx
            reconCmd.CommandText <-
                """SELECT id, tenant_id, account_id, statement_date, statement_balance_minor,
                          currency, status, note, created_by_user_id, started_at, completed_at
                   FROM reconciliations WHERE id = $1 FOR UPDATE"""
            reconCmd.Parameters.AddWithValue("$1", id) |> ignore
            let! reconReader = reconCmd.ExecuteReaderAsync()
            use reconReader = reconReader
            let! hasRecon = reconReader.ReadAsync()
            if not hasRecon then
                do! tx.RollbackAsync()
                return Error "Reconciliation not found"
            else
                let recon = mapReconciliation reconReader
                if recon.Status <> ReconciliationStatus.Open then
                    do! tx.RollbackAsync()
                    return Error "Reconciliation is not open"
                else
                    do! reconReader.CloseAsync()

                    // Compute sum of included transactions
                    use sumCmd = conn.CreateCommand()
                    sumCmd.Transaction <- tx
                    sumCmd.CommandText <-
                        """SELECT COALESCE(SUM(t.amount_minor), 0)
                           FROM transactions t
                           JOIN reconciliation_transactions rt ON rt.transaction_id = t.id
                           WHERE rt.reconciliation_id = $1"""
                    sumCmd.Parameters.AddWithValue("$1", id) |> ignore
                    let! includedSumObj = sumCmd.ExecuteScalarAsync()
                    let includedSum =
                        match includedSumObj with
                        | :? int64 as v -> v
                        | :? int32 as v -> int64 v
                        | _ -> 0L

                    let statementBalanceMinor = MoneyHelpers.toMinorUnits recon.StatementBalance
                    let diffMinor = includedSum - statementBalanceMinor

                    if diffMinor <> 0L && not force then
                        do! tx.RollbackAsync()
                        return Error $"diff:{diffMinor}"
                    else
                        // Mark reconciliation completed
                        use completeCmd = conn.CreateCommand()
                        completeCmd.Transaction <- tx
                        let noteValue =
                            if diffMinor <> 0L then
                                let discrepancyNote = $"Force-completed with discrepancy of {diffMinor} minor units."
                                match note with
                                | Some n -> $"{n} | {discrepancyNote}"
                                | None -> discrepancyNote
                            else
                                note |> Option.defaultValue null

                        completeCmd.CommandText <-
                            """UPDATE reconciliations
                               SET status = 'completed',
                                   completed_at = now(),
                                   note = $2
                               WHERE id = $1"""
                        completeCmd.Parameters.AddWithValue("$1", id) |> ignore
                        if isNull noteValue then
                            completeCmd.Parameters.AddWithValue("$2", DBNull.Value) |> ignore
                        else
                            completeCmd.Parameters.AddWithValue("$2", noteValue) |> ignore
                        let! _ = completeCmd.ExecuteNonQueryAsync()

                        // Mark all linked transactions as reconciled
                        use updateTxnsCmd = conn.CreateCommand()
                        updateTxnsCmd.Transaction <- tx
                        updateTxnsCmd.CommandText <-
                            """UPDATE transactions
                               SET status = 'reconciled', updated_at = now()
                               WHERE id IN (
                                   SELECT transaction_id FROM reconciliation_transactions WHERE reconciliation_id = $1
                               )"""
                        updateTxnsCmd.Parameters.AddWithValue("$1", id) |> ignore
                        let! _ = updateTxnsCmd.ExecuteNonQueryAsync()

                        do! tx.CommitAsync()
                        return Ok diffMinor
        }

    let abortAsync (factory: IDbConnectionFactory) (tenantContext: TenantContext) (id: Guid) =
        task {
            use! conn = factory.OpenForTenantAsync(tenantContext)
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                """UPDATE reconciliations
                   SET status = 'aborted', completed_at = now()
                   WHERE id = $1 AND status = 'open'"""
            cmd.Parameters.AddWithValue("$1", id) |> ignore
            let! _ = cmd.ExecuteNonQueryAsync()
            return ()
        }

    /// Create an IReconciliationRepository backed by the given connection factory and
    /// tenant context accessor.
    let create (factory: IDbConnectionFactory) (accessor: ITenantContextAccessor) : IReconciliationRepository =
        let requireCtx () =
            match accessor.Context with
            | Some ctx -> ctx
            | None -> failwith "No tenant context available for the current operation"

        { new IReconciliationRepository with
            member _.CreateAsync(recon) = createAsync factory recon
            member _.GetAsync(id) = getAsync factory (requireCtx()) id
            member _.GetWithTransactionsAsync(id) = getWithTransactionsAsync factory (requireCtx()) id
            member _.ListAsync() = listAsync factory (requireCtx())
            member _.ListCandidateTransactionsAsync(accountId, statementDate) = listCandidateTransactionsAsync factory (requireCtx()) (accountId, statementDate)
            member _.UpdateIncludedTransactionsAsync(id, included, excluded) = updateIncludedTransactionsAsync factory (requireCtx()) (id, included, excluded)
            member _.CompleteAsync(id, force, note) = completeAsync factory (requireCtx()) (id, force, note)
            member _.AbortAsync(id) = abortAsync factory (requireCtx()) id
        }
