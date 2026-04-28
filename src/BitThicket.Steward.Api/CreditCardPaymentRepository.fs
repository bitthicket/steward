namespace BitThicket.Steward.Api

open System
open System.Data.Common
open System.Threading.Tasks
open Npgsql
open BitThicket.Steward.Api.Domain

/// Repository for credit card payments.
type ICreditCardPaymentRepository =
    abstract GetAsync : id:Guid -> Task<CreditCardPayment option>
    abstract ListByCreditCardAccountAsync : creditCardAccountId:Guid -> Task<CreditCardPayment list>
    abstract CreateAsync : payment:CreditCardPayment -> Task<Guid>
    abstract UpdateAsync : payment:CreditCardPayment -> Task<unit>
    abstract DeleteAsync : id:Guid -> Task<unit>

module CreditCardPaymentRepository =

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

    let private paymentTypeToString (t: PaymentType) : string =
        match t with
        | PaymentType.StatementBalance -> "statement_balance"
        | PaymentType.MinimumPayment   -> "minimum_payment"
        | PaymentType.CustomAmount     -> "custom_amount"
        | PaymentType.FullBalance      -> "full_balance"

    let private paymentTypeFromString (s: string) : PaymentType =
        match s.ToLowerInvariant() with
        | "statement_balance" -> PaymentType.StatementBalance
        | "minimum_payment"   -> PaymentType.MinimumPayment
        | "custom_amount"     -> PaymentType.CustomAmount
        | "full_balance"      -> PaymentType.FullBalance
        | _                   -> failwith $"Unknown payment type: {s}"

    // ── Row mapping ──────────────────────────────────────────────────────────

    let internal mapPayment (reader: DbDataReader) : CreditCardPayment =
        {
            Id = reader.GetGuid(0)
            TenantId = reader.GetGuid(1)
            CreditCardAccountId = reader.GetGuid(2)
            FundingAccountId = reader.GetGuid(3)
            Amount = fromMinor (reader.GetInt64(4)) (reader.GetString(5))
            PaymentType = paymentTypeFromString (reader.GetString(6))
            ScheduledDate =
                if reader.IsDBNull(7) then None
                else Some (DateOnly.FromDateTime(reader.GetDateTime(7)))
            PaidAt = Sql.nullableDateTimeOffset reader 8
            DebitTransactionId = Sql.nullableGuid reader 9
            CreditTransactionId = Sql.nullableGuid reader 10
            CreatedAt = Sql.dateTimeOffset reader 11
        }

    // ── CRUD implementations ─────────────────────────────────────────────────

    let getAsync (factory: IDbConnectionFactory) (tenantContext: TenantContext) (id: Guid) =
        task {
            use! conn = factory.OpenForTenantAsync(tenantContext)
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                """SELECT id, tenant_id, credit_card_account_id, funding_account_id,
                          amount_minor, currency, payment_type, scheduled_date, paid_at,
                          debit_transaction_id, credit_transaction_id, created_at
                   FROM credit_card_payments WHERE id = $1"""
            cmd.Parameters.AddWithValue("$1", id) |> ignore
            let! reader = cmd.ExecuteReaderAsync()
            use reader = reader
            let! hasRow = reader.ReadAsync()
            return if hasRow then Some(mapPayment reader) else None
        }

    let listByCreditCardAccountAsync (factory: IDbConnectionFactory) (tenantContext: TenantContext) (creditCardAccountId: Guid) =
        task {
            use! conn = factory.OpenForTenantAsync(tenantContext)
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                """SELECT id, tenant_id, credit_card_account_id, funding_account_id,
                          amount_minor, currency, payment_type, scheduled_date, paid_at,
                          debit_transaction_id, credit_transaction_id, created_at
                   FROM credit_card_payments
                   WHERE credit_card_account_id = $1
                   ORDER BY created_at DESC"""
            cmd.Parameters.AddWithValue("$1", creditCardAccountId) |> ignore
            let! reader = cmd.ExecuteReaderAsync()
            use reader = reader
            let payments = ResizeArray<CreditCardPayment>()
            while! reader.ReadAsync() do
                payments.Add(mapPayment reader)
            return payments |> Seq.toList
        }

    let createAsync (factory: IDbConnectionFactory) (payment: CreditCardPayment) =
        task {
            let ctx = { TenantId = payment.TenantId; UserId = Guid.Empty }
            use! conn = factory.OpenForTenantAsync(ctx)
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                """INSERT INTO credit_card_payments (
                       id, tenant_id, credit_card_account_id, funding_account_id,
                       amount_minor, currency, payment_type, scheduled_date, paid_at,
                       debit_transaction_id, credit_transaction_id, created_at
                   ) VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12)"""
            cmd.Parameters.AddWithValue("$1", payment.Id) |> ignore
            cmd.Parameters.AddWithValue("$2", payment.TenantId) |> ignore
            cmd.Parameters.AddWithValue("$3", payment.CreditCardAccountId) |> ignore
            cmd.Parameters.AddWithValue("$4", payment.FundingAccountId) |> ignore
            cmd.Parameters.AddWithValue("$5", toMinor payment.Amount) |> ignore
            cmd.Parameters.AddWithValue("$6", payment.Amount.CurrencyCode) |> ignore
            cmd.Parameters.AddWithValue("$7", paymentTypeToString payment.PaymentType) |> ignore
            match payment.ScheduledDate with
            | Some d -> cmd.Parameters.AddWithValue("$8", d.ToDateTime(TimeOnly.MinValue)) |> ignore
            | None -> cmd.Parameters.AddWithValue("$8", DBNull.Value) |> ignore
            match payment.PaidAt with
            | Some d -> cmd.Parameters.AddWithValue("$9", d.UtcDateTime) |> ignore
            | None -> cmd.Parameters.AddWithValue("$9", DBNull.Value) |> ignore
            match payment.DebitTransactionId with
            | Some tid -> cmd.Parameters.AddWithValue("$10", tid) |> ignore
            | None -> cmd.Parameters.AddWithValue("$10", DBNull.Value) |> ignore
            match payment.CreditTransactionId with
            | Some tid -> cmd.Parameters.AddWithValue("$11", tid) |> ignore
            | None -> cmd.Parameters.AddWithValue("$11", DBNull.Value) |> ignore
            cmd.Parameters.AddWithValue("$12", payment.CreatedAt.UtcDateTime) |> ignore
            let! _ = cmd.ExecuteNonQueryAsync()
            return payment.Id
        }

    let updateAsync (factory: IDbConnectionFactory) (payment: CreditCardPayment) =
        task {
            let ctx = { TenantId = payment.TenantId; UserId = Guid.Empty }
            use! conn = factory.OpenForTenantAsync(ctx)
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                """UPDATE credit_card_payments SET
                       credit_card_account_id = $1,
                       funding_account_id = $2,
                       amount_minor = $3,
                       currency = $4,
                       payment_type = $5,
                       scheduled_date = $6,
                       paid_at = $7,
                       debit_transaction_id = $8,
                       credit_transaction_id = $9
                   WHERE id = $10"""
            cmd.Parameters.AddWithValue("$1", payment.CreditCardAccountId) |> ignore
            cmd.Parameters.AddWithValue("$2", payment.FundingAccountId) |> ignore
            cmd.Parameters.AddWithValue("$3", toMinor payment.Amount) |> ignore
            cmd.Parameters.AddWithValue("$4", payment.Amount.CurrencyCode) |> ignore
            cmd.Parameters.AddWithValue("$5", paymentTypeToString payment.PaymentType) |> ignore
            match payment.ScheduledDate with
            | Some d -> cmd.Parameters.AddWithValue("$6", d.ToDateTime(TimeOnly.MinValue)) |> ignore
            | None -> cmd.Parameters.AddWithValue("$6", DBNull.Value) |> ignore
            match payment.PaidAt with
            | Some d -> cmd.Parameters.AddWithValue("$7", d.UtcDateTime) |> ignore
            | None -> cmd.Parameters.AddWithValue("$7", DBNull.Value) |> ignore
            match payment.DebitTransactionId with
            | Some tid -> cmd.Parameters.AddWithValue("$8", tid) |> ignore
            | None -> cmd.Parameters.AddWithValue("$8", DBNull.Value) |> ignore
            match payment.CreditTransactionId with
            | Some tid -> cmd.Parameters.AddWithValue("$9", tid) |> ignore
            | None -> cmd.Parameters.AddWithValue("$9", DBNull.Value) |> ignore
            cmd.Parameters.AddWithValue("$10", payment.Id) |> ignore
            let! _ = cmd.ExecuteNonQueryAsync()
            return ()
        }

    let deleteAsync (factory: IDbConnectionFactory) (tenantContext: TenantContext) (id: Guid) =
        task {
            use! conn = factory.OpenForTenantAsync(tenantContext)
            use cmd = conn.CreateCommand()
            cmd.CommandText <- "DELETE FROM credit_card_payments WHERE id = $1"
            cmd.Parameters.AddWithValue("$1", id) |> ignore
            let! _ = cmd.ExecuteNonQueryAsync()
            return ()
        }

    /// Create an ICreditCardPaymentRepository backed by the given connection factory and
    /// tenant context accessor.
    let create (factory: IDbConnectionFactory) (accessor: ITenantContextAccessor) : ICreditCardPaymentRepository =
        let requireCtx () =
            match accessor.Context with
            | Some ctx -> ctx
            | None -> failwith "No tenant context available for the current operation"

        { new ICreditCardPaymentRepository with
            member _.GetAsync(id) = getAsync factory (requireCtx()) id
            member _.ListByCreditCardAccountAsync(ccId) = listByCreditCardAccountAsync factory (requireCtx()) ccId
            member _.CreateAsync(payment) = createAsync factory payment
            member _.UpdateAsync(payment) = updateAsync factory payment
            member _.DeleteAsync(id) = deleteAsync factory (requireCtx()) id
        }
