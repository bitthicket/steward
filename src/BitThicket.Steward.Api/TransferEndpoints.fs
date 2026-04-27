namespace BitThicket.Steward.Api

open System
open System.IO
open System.Text.Json
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
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

// ── Domain helpers ─────────────────────────────────────────────────────────

module private TransferHelpers =
    let paymentTypeToString (t: PaymentType) : string =
        match t with
        | PaymentType.StatementBalance -> "statementBalance"
        | PaymentType.MinimumPayment   -> "minimumPayment"
        | PaymentType.CustomAmount     -> "custom"
        | PaymentType.FullBalance      -> "fullBalance"

    let paymentTypeFromString (s: string) : PaymentType option =
        match s.ToLowerInvariant() with
        | "statementbalance" -> Some PaymentType.StatementBalance
        | "minimumpayment"   -> Some PaymentType.MinimumPayment
        | "custom"           -> Some PaymentType.CustomAmount
        | "fullbalance"      -> Some PaymentType.FullBalance
        | _                  -> None

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

// ── Endpoints ──────────────────────────────────────────────────────────────

module TransferEndpoints =
    open TransferHelpers

    // POST /api/transfers
    let createTransferHandler : HttpHandler = fun ctx ->
        task {
            let txnRepo = ctx.RequestServices.GetRequiredService<ITransactionRepository>()
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
                            TransferAccountId = Some creditId
                            MatchConfidence = None
                            SyncEventId = None
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
                            TransferAccountId = Some debitId
                            MatchConfidence = None
                            SyncEventId = None
                            CreatedAt = now
                            UpdatedAt = now
                        }

                        let! _ = txnRepo.CreateAsync(debitTxn)
                        let! _ = txnRepo.CreateAsync(creditTxn)

                        let resp: TransferResponse = {
                            debitTransactionId = debitId
                            creditTransactionId = creditId
                        }
                        ctx.Response.StatusCode <- 201
                        do! Response.ofJson resp ctx
        }

    // POST /api/credit-card-payments
    let createCreditCardPaymentHandler : HttpHandler = fun ctx ->
        task {
            let paymentRepo = ctx.RequestServices.GetRequiredService<ICreditCardPaymentRepository>()
            let accountRepo = ctx.RequestServices.GetRequiredService<IAccountRepository>()
            let txnRepo = ctx.RequestServices.GetRequiredService<ITransactionRepository>()
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
                                    CreatedAt = now
                                    UpdatedAt = now
                                }

                                let! _ = txnRepo.CreateAsync(debitTxn)
                                let! _ = txnRepo.CreateAsync(creditTxn)
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

                            let! _ = paymentRepo.CreateAsync(payment)
                            ctx.Response.StatusCode <- 201
                            do! Response.ofJson (ccPaymentToResponse payment) ctx
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
