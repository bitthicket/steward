namespace BitThicket.Steward.Api

open System
open System.IO
open System.Text.Json
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Falco
open BitThicket.Steward.Api.Domain
open BitThicket.Steward.Pricing

// ── Request / response DTOs ────────────────────────────────────────────────

type CreateAccountRequest = {
    name: string
    accountType: string
    currency: string
    isOnBudget: bool option
    institutionName: string option
    externalId: string option
}

type UpdateAccountRequest = {
    name: string option
    isOnBudget: bool option
}

type AccountResponse = {
    id: Guid
    name: string
    accountType: string
    currency: string
    institutionName: string option
    externalId: string option
    isOnBudget: bool
    isActive: bool
    createdAt: DateTimeOffset
    updatedAt: DateTimeOffset
}

type BalanceResponse = {
    posted: Money
    available: Money
    pending: Money
    displayCurrency: string option
    converted: {| posted: Money; available: Money; pending: Money |} option
}

// ── JSON helpers ───────────────────────────────────────────────────────────

module private AccountJson =
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

// ── Domain helpers ─────────────────────────────────────────────────────────

module private AccountHelpers =
    let accountTypeToString (t: AccountType) : string =
        match t with
        | AccountType.Checking    -> "checking"
        | AccountType.Savings     -> "savings"
        | AccountType.CreditCard  -> "creditCard"
        | AccountType.Investment  -> "investment"
        | AccountType.Loan        -> "loan"
        | AccountType.Cash        -> "cash"

    let accountToResponse (account: Account) : AccountResponse =
        {
            id = account.Id
            name = account.Name
            accountType = accountTypeToString account.AccountType
            currency = account.CurrencyCode
            institutionName = account.InstitutionName
            externalId = account.ExternalId
            isOnBudget = account.IsOnBudget
            isActive = account.IsActive
            createdAt = account.CreatedAt
            updatedAt = account.UpdatedAt
        }

    let validateCurrency (currency: string) : bool =
        not (String.IsNullOrWhiteSpace(currency)) && currency.Length = 3

    let validateName (name: string) : bool =
        not (String.IsNullOrWhiteSpace(name))

// ── Endpoints ──────────────────────────────────────────────────────────────

module AccountEndpoints =
    open AccountHelpers

    // GET /api/accounts
    let listAccountsHandler : HttpHandler = fun ctx ->
        task {
            let repo = ctx.RequestServices.GetRequiredService<IAccountRepository>()
            let! accounts = repo.ListAsync()
            let resp = accounts |> List.map accountToResponse
            do! Response.ofJson {| accounts = resp |} ctx
        }

    // GET /api/accounts/{accountId:guid}
    let getAccountHandler (accountId: Guid) : HttpHandler = fun ctx ->
        task {
            let repo = ctx.RequestServices.GetRequiredService<IAccountRepository>()
            let! accountOpt = repo.GetAsync(accountId)
            match accountOpt with
            | None ->
                ctx.Response.StatusCode <- 404
                do! Response.ofJson {| error = "Account not found" |} ctx
            | Some account ->
                do! Response.ofJson (accountToResponse account) ctx
        }

    // POST /api/accounts
    let createAccountHandler : HttpHandler = fun ctx ->
        task {
            let repo = ctx.RequestServices.GetRequiredService<IAccountRepository>()
            let! doc = AccountJson.readBody ctx
            let req = AccountJson.deserialize<CreateAccountRequest> doc

            // Validation
            if not (validateName req.name) then
                ctx.Response.StatusCode <- 400
                do! Response.ofJson {| error = "Name is required and cannot be empty or whitespace." |} ctx
            elif not (validateCurrency req.currency) then
                ctx.Response.StatusCode <- 400
                do! Response.ofJson {| error = "Currency must be a non-empty 3-character code." |} ctx
            else
                match AccountRepository.accountTypeFromString req.accountType with
                | None ->
                    ctx.Response.StatusCode <- 400
                    do! Response.ofJson {| error = "Invalid accountType. Use: checking, savings, creditCard, investment, loan, cash" |} ctx
                | Some accountType ->
                    let accessor = ctx.RequestServices.GetRequiredService<ITenantContextAccessor>()
                    match accessor.Context with
                    | None ->
                        ctx.Response.StatusCode <- 401
                        do! Response.ofJson {| error = "Unauthorized" |} ctx
                    | Some tc ->
                        let isOnBudget = req.isOnBudget |> Option.defaultValue (AccountRepository.defaultIsOnBudget accountType)
                        let now = DateTimeOffset.UtcNow
                        let account: Account = {
                            Id = Guid.NewGuid()
                            TenantId = tc.TenantId
                            UserId = tc.UserId
                            Name = req.name.Trim()
                            AccountType = accountType
                            CurrencyCode = req.currency.ToUpperInvariant()
                            InstitutionName = req.institutionName
                            ExternalId = req.externalId
                            CreditCardInfo = None
                            IsOnBudget = isOnBudget
                            IsActive = true
                            DeletedAt = None
                            CreatedAt = now
                            UpdatedAt = now
                        }
                        let! id = repo.CreateAsync(account)
                        let resp = accountToResponse account
                        ctx.Response.StatusCode <- 201
                        do! Response.ofJson resp ctx
        }

    // PATCH /api/accounts/{accountId:guid}
    let updateAccountHandler (accountId: Guid) : HttpHandler = fun ctx ->
        task {
            let repo = ctx.RequestServices.GetRequiredService<IAccountRepository>()
            let! doc = AccountJson.readBody ctx
            let req = AccountJson.deserialize<UpdateAccountRequest> doc

            let! accountOpt = repo.GetAsync(accountId)
            match accountOpt with
            | None ->
                ctx.Response.StatusCode <- 404
                do! Response.ofJson {| error = "Account not found" |} ctx
            | Some account ->
                // Validate name if provided
                match req.name with
                | Some name when not (validateName name) ->
                    ctx.Response.StatusCode <- 400
                    do! Response.ofJson {| error = "Name cannot be empty or whitespace." |} ctx
                | _ ->
                    let updatedName = req.name |> Option.map (fun n -> n.Trim()) |> Option.defaultValue account.Name
                    let updatedIsOnBudget = req.isOnBudget |> Option.defaultValue account.IsOnBudget
                    let updated = {
                        account with
                            Name = updatedName
                            IsOnBudget = updatedIsOnBudget
                            UpdatedAt = DateTimeOffset.UtcNow
                    }
                    do! repo.UpdateAsync(updated)
                    do! Response.ofJson (accountToResponse updated) ctx
        }

    // GET /api/accounts/{accountId:guid}/balance?displayCurrency=USD|BTC
    let getBalanceHandler (accountId: Guid) : HttpHandler = fun ctx ->
        task {
            let repo = ctx.RequestServices.GetRequiredService<IAccountRepository>()
            let pricing = ctx.RequestServices.GetRequiredService<IPriceProvider>()

            let displayCurrencyOpt =
                match ctx.Request.Query.TryGetValue("displayCurrency") with
                | true, v when v.Count > 0 -> Some(v.ToString().ToUpperInvariant())
                | _ -> None

            let! balanceOpt = repo.GetBalanceAsync(accountId)
            match balanceOpt with
            | None ->
                ctx.Response.StatusCode <- 404
                do! Response.ofJson {| error = "Account not found" |} ctx
            | Some balance ->
                let! convertedOpt =
                    match displayCurrencyOpt with
                    | None -> Task.FromResult None
                    | Some target ->
                        task {
                            let! postedConv = PriceConversion.convertMoneyAsync pricing balance.Posted target
                            let! pendingConv = PriceConversion.convertMoneyAsync pricing balance.Pending target
                            let availableConv = { Amount = postedConv.Amount + pendingConv.Amount; CurrencyCode = target }
                            return Some {| posted = postedConv; available = availableConv; pending = pendingConv |}
                        }

                let resp : BalanceResponse = {
                    posted = balance.Posted
                    available = balance.Available
                    pending = balance.Pending
                    displayCurrency = displayCurrencyOpt
                    converted = convertedOpt
                }
                do! Response.ofJson resp ctx
        }

    // DELETE /api/accounts/{accountId:guid}
    let deleteAccountHandler (accountId: Guid) : HttpHandler = fun ctx ->
        task {
            let repo = ctx.RequestServices.GetRequiredService<IAccountRepository>()
            let! accountOpt = repo.GetAsync(accountId)
            match accountOpt with
            | None ->
                ctx.Response.StatusCode <- 404
                do! Response.ofJson {| error = "Account not found" |} ctx
            | Some _ ->
                do! repo.DeleteAsync(accountId)
                ctx.Response.StatusCode <- 204
                do! Response.ofEmpty ctx
        }
