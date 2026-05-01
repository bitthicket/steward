namespace BitThicket.Steward.Api

open System
open System.IO
open System.Text.Json
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Npgsql
open NpgsqlTypes
open Falco
open BitThicket.Steward.Api.Domain

// ── Request / response DTOs ────────────────────────────────────────────────

type CreateTransferRequest = {
    fromAccountId: Guid
    toAccountId: Guid
    amountMinor: int64
    currency: string
    occurredAt: DateTimeOffset
    description: string option
}

type TransferResponse = {
    debitTransactionId: Guid
    creditTransactionId: Guid
}

type CreateCreditCardPaymentRequest = {
    creditCardAccountId: Guid
    fundingAccountId: Guid
    amountMinor: int64
    currency: string
    scheduledFor: DateOnly
    paymentType: string
}

type CreditCardPaymentResponse = {
    id: Guid
    creditCardAccountId: Guid
    fundingAccountId: Guid
    amount: Money
    paymentType: string
    scheduledDate: DateOnly option
    paidAt: DateTimeOffset option
    debitTransactionId: Guid option
    creditTransactionId: Guid option
    createdAt: DateTimeOffset
}

// ── JSON helpers ───────────────────────────────────────────────────────────

module private TransferJson =
    let readBody (ctx: HttpContext) =
        task {
            use reader = new StreamReader(ctx.Request.Body, System.Text.Encoding.UTF8)
            let! json = reader.ReadToEndAsync()
            return JsonDocument.Parse(json)
        }

    let jsonOptions =
        let opts = JsonSerializerOptions()
        opts.PropertyNamingPolicy <- JsonNamingPolicy.CamelCase
        opts

    let deserialize<'T> (doc: JsonDocument) =
        JsonSerializer.Deserialize<'T>(doc, jsonOptions)

// ── Money helpers ──────────────────────────────────────────────────────────

module private MoneyHelper =
    let decimalPlaces (currencyCode: string) : int =
        match currencyCode.ToUpperInvariant() with
        | "BTC" -> 8
        | _ -> 2

    let fromMinor (minor: int64) (currencyCode: string) : Money =
        let places = decimalPlaces currencyCode
        let factor = pown 10m places
        { Amount = decimal minor / factor; CurrencyCode = currencyCode }

    let toMinor (money: Money) : int64 =
        let places = decimalPlaces money.CurrencyCode
        let factor = pown 10m places
        int64 (Decimal.Round(money.Amount * factor))

// ── Domain helpers ─────────────────────────────────────────────────────────

module private TransferHelpers =
    let paymentTypeToString (t: PaymentType) : string =
        match t with
        | PaymentType.StatementBalance -> "statementBalance"
        | PaymentType.MinimumPayment   -> "minimumPayment"
        | PaymentType.CustomAmount     -> "custom"
        | PaymentType.FullBalance      -> "fullBalance"

    let paymentTypeFromString (s: string) : PaymentType option =
        match s with
        | "statementBalance" | "statementbalance" | "statement_balance" -> Some PaymentType.StatementBalance
        | "minimumPayment" | "minimumpayment" | "minimum_payment" | "minimum" -> Some PaymentType.MinimumPayment
        | "custom" | "customAmount" | "customamount" | "custom_amount" -> Some PaymentType.CustomAmount
        | "fullBalance" | "fullbalance" | "full_balance" -> Some PaymentType.FullBalance
        | _ -> None

    let ccPaymentToResponse (p: CreditCardPayment) : CreditCardPaymentResponse =
        {
            id = p.Id
            creditCardAccountId = p.CreditCardAccountId
            fundingAccountId = p.FundingAccountId
            amount = p.Amount
            paymentType = paymentTypeToString p.PaymentType
            scheduledDate = p.ScheduledDate
            paidAt = p.PaidAt
            debitTransactionId = p.DebitTransactionId
            creditTransactionId = p.CreditTransactionId
            createdAt = p.CreatedAt
        }

// ── Raw SQL helpers for atomic multi-insert operations ─────────────────────

module private AtomicSql =
    let addNullableGuid (cmd: NpgsqlCommand) (name: string) (value: Guid option) =
        match value with
        | Some v -> cmd.Parameters.AddWithValue(name, v) |> ignore
        | None -> cmd.Parameters.AddWithValue(name, DBNull.Value) |> ignore

    let addNullableDateTime (cmd: NpgsqlCommand) (name: string) (value: DateTimeOffset option) =
        match value with
        | Some v -> cmd.Parameters.AddWithValue(name, v.UtcDateTime) |> ignore
        | None -> cmd.Parameters.AddWithValue(name, DBNull.Value) |> ignore

    let addNullableString (cmd: NpgsqlCommand) (name: string) (value: string option) =
        match value with
        | Some v -> cmd.Parameters.AddWithValue(name, v) |> ignore
        | None -> cmd.Parameters.AddWithValue(name, DBNull.Value) |> ignore

    let insertTransaction (conn: NpgsqlConnection) (tx: NpgsqlTransaction) (txn: Transaction) =
        task {
            use cmd = conn.CreateCommand()
            cmd.Transaction <- tx
            cmd.CommandText <-
                """INSERT INTO transactions (
                       id, tenant_id, account_id, occurred_at, posted_at,
                       amount_minor, currency, description, merchant, memo,
                       category_id, source, external_id, matched_transaction_id, transfer_account_id,
                       status, match_confidence, sync_event_id, created_at, updated_at
                   ) VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10,
                             $11, $12, $13, $14, $15, $16, $17, $18, $19, $20)"""
            cmd.Parameters.AddWithValue("$1", txn.Id) |> ignore
            cmd.Parameters.AddWithValue("$2", txn.TenantId) |> ignore
            cmd.Parameters.AddWithValue("$3", txn.AccountId) |> ignore
            addNullableDateTime cmd "$4" (Some txn.OccurredAt)
            addNullableDateTime cmd "$5" txn.PostedAt
            cmd.Parameters.AddWithValue("$6", MoneyHelper.toMinor txn.Amount) |> ignore
            cmd.Parameters.AddWithValue("$7", txn.Amount.CurrencyCode) |> ignore
            cmd.Parameters.AddWithValue("$8", txn.Description) |> ignore
            addNullableString cmd "$9" txn.Merchant
            addNullableString cmd "$10" txn.Memo
            addNullableGuid cmd "$11" txn.CategoryId
            let sourceParam = cmd.CreateParameter()
            sourceParam.ParameterName <- "$12"
            sourceParam.NpgsqlDbType <- NpgsqlDbType.Jsonb
            sourceParam.Value <-
                match txn.Source with
                | TransactionSource.Manual -> box """{"type":"manual"}"""
                | TransactionSource.DataFeed provider -> box $"""{{"type":"data_feed","provider":"{provider}"}}"""
                | TransactionSource.Import format -> box $"""{{"type":"import","format":"{format}"}}"""
            cmd.Parameters.Add(sourceParam) |> ignore
            addNullableString cmd "$13" txn.ExternalId
            addNullableGuid cmd "$14" txn.MatchedTransactionId
            addNullableGuid cmd "$15" txn.TransferAccountId
            cmd.Parameters.AddWithValue("$16", "cleared") |> ignore
            match txn.MatchConfidence with
            | Some c -> cmd.Parameters.AddWithValue("$17", c) |> ignore
            | None -> cmd.Parameters.AddWithValue("$17", DBNull.Value) |> ignore
            addNullableGuid cmd "$18" txn.SyncEventId
            cmd.Parameters.AddWithValue("$19", txn.CreatedAt.UtcDateTime) |> ignore
            cmd.Parameters.AddWithValue("$20", txn.UpdatedAt.UtcDateTime) |> ignore
            let! _ = cmd.ExecuteNonQueryAsync()
            return txn.Id
        }

    let insertCreditCardPayment (conn: NpgsqlConnection) (tx: NpgsqlTransaction) (payment: CreditCardPayment) =
        task {
            use cmd = conn.CreateCommand()
            cmd.Transaction <- tx
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
            cmd.Parameters.AddWithValue("$5", MoneyHelper.toMinor payment.Amount) |> ignore
            cmd.Parameters.AddWithValue("$6", payment.Amount.CurrencyCode) |> ignore
            cmd.Parameters.AddWithValue("$7", TransferHelpers.paymentTypeToString payment.PaymentType) |> ignore
            match payment.ScheduledDate with
            | Some d -> cmd.Parameters.AddWithValue("$8", d.ToDateTime(TimeOnly.MinValue)) |> ignore
            | None -> cmd.Parameters.AddWithValue("$8", DBNull.Value) |> ignore
            addNullableDateTime cmd "$9" payment.PaidAt
            addNullableGuid cmd "$10" payment.DebitTransactionId
            addNullableGuid cmd "$11" payment.CreditTransactionId
            cmd.Parameters.AddWithValue("$12", payment.CreatedAt.UtcDateTime) |> ignore
            let! _ = cmd.ExecuteNonQueryAsync()
            return payment.Id
        }

// ── Endpoints ──────────────────────────────────────────────────────────────

module TransferEndpoints =
    open TransferHelpers

    // POST /api/transfers
    let createTransferHandler : HttpHandler = fun ctx ->
        task {
            let factory = ctx.RequestServices.GetRequiredService<IDbConnectionFactory>()
            let accountRepo = ctx.RequestServices.GetRequiredService<IAccountRepository>()
            let! doc = TransferJson.readBody ctx
            let req = TransferJson.deserialize<CreateTransferRequest> doc

            let accessor = ctx.RequestServices.GetRequiredService<ITenantContextAccessor>()
            match accessor.Context with
            | None ->
                ctx.Response.StatusCode <- 401
                do! Response.ofJson {| error = "Unauthorized" |} ctx
            | Some tc ->
                // Validate accounts exist and are in the same tenant
                let! fromOpt = accountRepo.GetAsync(req.fromAccountId)
                let! toOpt = accountRepo.GetAsync(req.toAccountId)

                match fromOpt, toOpt with
                | None, _ ->
                    ctx.Response.StatusCode <- 400
                    do! Response.ofJson {| error = "fromAccountId not found" |} ctx
                | _, None ->
                    ctx.Response.StatusCode <- 400
                    do! Response.ofJson {| error = "toAccountId not found" |} ctx
                | Some fromAccount, Some toAccount ->
                    let currency = req.currency.ToUpperInvariant()

                    // Invariant: from and to must be different accounts
                    if req.fromAccountId = req.toAccountId then
                        ctx.Response.StatusCode <- 400
                        do! Response.ofJson {| error = "fromAccountId and toAccountId must be different" |} ctx
                    // Invariant: matching currency
                    elif fromAccount.CurrencyCode <> currency then
                        ctx.Response.StatusCode <- 400
                        do! Response.ofJson {| error = $"fromAccount currency {fromAccount.CurrencyCode} does not match transfer currency {currency}" |} ctx
                    elif toAccount.CurrencyCode <> currency then
                        ctx.Response.StatusCode <- 400
                        do! Response.ofJson {| error = $"toAccount currency {toAccount.CurrencyCode} does not match transfer currency {currency}" |} ctx
                    else
                        let now = DateTimeOffset.UtcNow
                        let debitId = Guid.NewGuid()
                        let creditId = Guid.NewGuid()
                        let moneyAmount = MoneyHelper.fromMinor req.amountMinor currency
                        let absAmount = Math.Abs(moneyAmount.Amount)

                        let debitTxn: Transaction = {
                            Id = debitId
                            TenantId = tc.TenantId
                            AccountId = req.fromAccountId
                            OccurredAt = req.occurredAt
                            PostedAt = None
                            Amount = { Amount = -absAmount; CurrencyCode = currency }
                            Description = req.description |> Option.defaultValue "Transfer"
                            Merchant = None
                            Memo = None
                            CategoryId = None
                            Status = TransactionStatus.Cleared
                            Source = TransactionSource.Manual
                            ExternalId = None
                            MatchedTransactionId = None
                            TransferAccountId = Some req.toAccountId
                            MatchConfidence = None
                            SyncEventId = None
                            DeletedAt = None
                            CreatedAt = now
                            UpdatedAt = now
                        }

                        let creditTxn: Transaction = {
                            Id = creditId
                            TenantId = tc.TenantId
                            AccountId = req.toAccountId
                            OccurredAt = req.occurredAt
                            PostedAt = None
                            Amount = { Amount = absAmount; CurrencyCode = currency }
                            Description = req.description |> Option.defaultValue "Transfer"
                            Merchant = None
                            Memo = None
                            CategoryId = None
                            Status = TransactionStatus.Cleared
                            Source = TransactionSource.Manual
                            ExternalId = None
                            MatchedTransactionId = None
                            TransferAccountId = Some req.fromAccountId
                            MatchConfidence = None
                            SyncEventId = None
                            DeletedAt = None
                            CreatedAt = now
                            UpdatedAt = now
                        }

                        use! conn = factory.OpenForTenantAsync(tc)
                        use tx = conn.BeginTransaction()
                        try
                            let! _ = AtomicSql.insertTransaction conn tx debitTxn
                            let! _ = AtomicSql.insertTransaction conn tx creditTxn
                            do! tx.CommitAsync()

                            let resp: TransferResponse = {
                                debitTransactionId = debitId
                                creditTransactionId = creditId
                            }
                            ctx.Response.StatusCode <- 201
                            do! Response.ofJson resp ctx
                        with ex ->
                            do! tx.RollbackAsync()
                            ctx.Response.StatusCode <- 500
                            do! Response.ofJson {| error = "Transfer creation failed" |} ctx
        }

    // POST /api/credit-card-payments
    let createCreditCardPaymentHandler : HttpHandler = fun ctx ->
        task {
            let factory = ctx.RequestServices.GetRequiredService<IDbConnectionFactory>()
            let accountRepo = ctx.RequestServices.GetRequiredService<IAccountRepository>()
            let! doc = TransferJson.readBody ctx
            let req = TransferJson.deserialize<CreateCreditCardPaymentRequest> doc

            let accessor = ctx.RequestServices.GetRequiredService<ITenantContextAccessor>()
            match accessor.Context with
            | None ->
                ctx.Response.StatusCode <- 401
                do! Response.ofJson {| error = "Unauthorized" |} ctx
            | Some tc ->
                let! ccOpt = accountRepo.GetAsync(req.creditCardAccountId)
                let! fundingOpt = accountRepo.GetAsync(req.fundingAccountId)

                match ccOpt, fundingOpt with
                | None, _ ->
                    ctx.Response.StatusCode <- 400
                    do! Response.ofJson {| error = "creditCardAccountId not found" |} ctx
                | _, None ->
                    ctx.Response.StatusCode <- 400
                    do! Response.ofJson {| error = "fundingAccountId not found" |} ctx
                | Some ccAccount, Some fundingAccount ->
                    let currency = req.currency.ToUpperInvariant()

                    // Validate credit card account type
                    if ccAccount.AccountType <> AccountType.CreditCard then
                        ctx.Response.StatusCode <- 400
                        do! Response.ofJson {| error = "creditCardAccountId must be a credit card account" |} ctx
                    // Validate currency match
                    elif ccAccount.CurrencyCode <> currency then
                        ctx.Response.StatusCode <- 400
                        do! Response.ofJson {| error = $"Credit card account currency {ccAccount.CurrencyCode} does not match payment currency {currency}" |} ctx
                    elif fundingAccount.CurrencyCode <> currency then
                        ctx.Response.StatusCode <- 400
                        do! Response.ofJson {| error = $"Funding account currency {fundingAccount.CurrencyCode} does not match payment currency {currency}" |} ctx
                    else
                        match paymentTypeFromString req.paymentType with
                        | None ->
                            ctx.Response.StatusCode <- 400
                            do! Response.ofJson {| error = "Invalid paymentType. Use: statementBalance, minimumPayment, custom, fullBalance" |} ctx
                        | Some paymentType ->
                            let now = DateTimeOffset.UtcNow
                            let scheduledDate = req.scheduledFor
                            let isImmediate = scheduledDate <= DateOnly.FromDateTime(now.DateTime)

                            let paymentMoney = MoneyHelper.fromMinor req.amountMinor currency

                            let paymentId = Guid.NewGuid()
                            let mutable debitTxnId = None
                            let mutable creditTxnId = None

                            use! conn = factory.OpenForTenantAsync(tc)
                            use tx = conn.BeginTransaction()
                            try
                                if isImmediate then
                                    let debitId = Guid.NewGuid()
                                    let creditId = Guid.NewGuid()
                                    let absAmount = Math.Abs(paymentMoney.Amount)

                                    let debitTxn: Transaction = {
                                        Id = debitId
                                        TenantId = tc.TenantId
                                        AccountId = req.fundingAccountId
                                        OccurredAt = now
                                        PostedAt = None
                                        Amount = { Amount = -absAmount; CurrencyCode = currency }
                                        Description = "Credit card payment"
                                        Merchant = None
                                        Memo = None
                                        CategoryId = None
                                        Status = TransactionStatus.Cleared
                                        Source = TransactionSource.Manual
                                        ExternalId = None
                                        MatchedTransactionId = None
                                        TransferAccountId = Some req.creditCardAccountId
                                        MatchConfidence = None
                                        SyncEventId = None
                                        DeletedAt = None
                                        CreatedAt = now
                                        UpdatedAt = now
                                    }

                                    let creditTxn: Transaction = {
                                        Id = creditId
                                        TenantId = tc.TenantId
                                        AccountId = req.creditCardAccountId
                                        OccurredAt = now
                                        PostedAt = None
                                        Amount = { Amount = absAmount; CurrencyCode = currency }
                                        Description = "Credit card payment"
                                        Merchant = None
                                        Memo = None
                                        CategoryId = None
                                        Status = TransactionStatus.Cleared
                                        Source = TransactionSource.Manual
                                        ExternalId = None
                                        MatchedTransactionId = None
                                        TransferAccountId = Some req.fundingAccountId
                                        MatchConfidence = None
                                        SyncEventId = None
                                        DeletedAt = None
                                        CreatedAt = now
                                        UpdatedAt = now
                                    }

                                    let! _ = AtomicSql.insertTransaction conn tx debitTxn
                                    let! _ = AtomicSql.insertTransaction conn tx creditTxn
                                    debitTxnId <- Some debitId
                                    creditTxnId <- Some creditId

                                let payment: CreditCardPayment = {
                                    Id = paymentId
                                    TenantId = tc.TenantId
                                    CreditCardAccountId = req.creditCardAccountId
                                    FundingAccountId = req.fundingAccountId
                                    Amount = paymentMoney
                                    PaymentType = paymentType
                                    ScheduledDate = Some scheduledDate
                                    PaidAt = if isImmediate then Some now else None
                                    DebitTransactionId = debitTxnId
                                    CreditTransactionId = creditTxnId
                                    CreatedAt = now
                                }

                                let! _ = AtomicSql.insertCreditCardPayment conn tx payment
                                do! tx.CommitAsync()

                                ctx.Response.StatusCode <- 201
                                do! Response.ofJson (ccPaymentToResponse payment) ctx
                            with ex ->
                                do! tx.RollbackAsync()
                                ctx.Response.StatusCode <- 500
                                do! Response.ofJson {| error = "Credit card payment creation failed" |} ctx
        }

    // GET /api/credit-card-payments?creditCardAccountId={id}
    let listCreditCardPaymentsHandler : HttpHandler = fun ctx ->
        task {
            let repo = ctx.RequestServices.GetRequiredService<ICreditCardPaymentRepository>()
            let q = ctx.Request.Query

            let ccAccountIdOpt =
                match q.TryGetValue("creditCardAccountId") with
                | true, v when v.Count > 0 ->
                    match Guid.TryParse(v.ToString()) with true, g -> Some g | _ -> None
                | _ -> None

            match ccAccountIdOpt with
            | None ->
                ctx.Response.StatusCode <- 400
                do! Response.ofJson {| error = "creditCardAccountId is required" |} ctx
            | Some ccId ->
                let! payments = repo.ListByCreditCardAccountAsync(ccId)
                let resp = payments |> List.map ccPaymentToResponse
                do! Response.ofJson {| payments = resp |} ctx
        }
