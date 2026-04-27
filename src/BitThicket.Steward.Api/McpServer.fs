namespace BitThicket.Steward.Api

open System
open System.IO
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Falco
open BitThicket.Steward.Api.Domain
open BitThicket.Steward.Pricing

// ── MCP Protocol Types (JSON-RPC 2.0 subset) ────────────────────────────────

module McpProtocol =

    type JsonRpcMessage = {
        Jsonrpc: string
        Id: JsonElement option
        Method: string option
        Params: JsonElement option
        Result: JsonElement option
        Error: JsonElement option
    }

    let internal jsonOptions = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)

    let serialize (value: 'a) : string = JsonSerializer.Serialize(value, jsonOptions)
    let deserialize<'a> (json: string) : 'a = JsonSerializer.Deserialize<'a>(json, jsonOptions)

    let tryReadMessage (json: string) : JsonRpcMessage option =
        try
            let doc = JsonDocument.Parse(json)
            let root = doc.RootElement
            let id =
                match root.TryGetProperty("id") with
                | true, v -> Some v
                | _ -> None
            let method =
                match root.TryGetProperty("method") with
                | true, v when v.ValueKind = JsonValueKind.String -> Some(v.GetString())
                | _ -> None
            let paramsEl =
                match root.TryGetProperty("params") with
                | true, v -> Some v
                | _ -> None
            let resultEl =
                match root.TryGetProperty("result") with
                | true, v -> Some v
                | _ -> None
            let errorEl =
                match root.TryGetProperty("error") with
                | true, v -> Some v
                | _ -> None
            Some {
                Jsonrpc = "2.0"
                Id = id
                Method = method
                Params = paramsEl
                Result = resultEl
                Error = errorEl
            }
        with _ -> None

    let makeResult (id: JsonElement) (result: 'a) : JsonDocument =
        let resultJson = serialize result
        let json = $"{{\"jsonrpc\":\"2.0\",\"id\":{id.GetRawText()},\"result\":{resultJson}}}"
        JsonDocument.Parse(json)

    let makeError (id: JsonElement) (code: int) (message: string) : JsonDocument =
        let safeMessage = message.Replace("\\", "\\\\").Replace("\"", "\\\"")
        let json = $"{{\"jsonrpc\":\"2.0\",\"id\":{id.GetRawText()},\"error\":{{\"code\":{code},\"message\":\"{safeMessage}\"}}}}"
        JsonDocument.Parse(json)

    // ── Capabilities ─────────────────────────────────────────────────────────

    type ServerCapabilities = {
        tools: obj option
        resources: obj option
        prompts: obj option
    }

    type ServerInfo = {
        name: string
        version: string
    }

    type InitializeResult = {
        protocolVersion: string
        capabilities: ServerCapabilities
        serverInfo: ServerInfo
    }

    // ── Tool types ───────────────────────────────────────────────────────────

    type ToolParameterProperty = {
        ``type``: string
        description: string
    }

    type ToolInputSchema = {
        ``type``: string
        properties: Map<string, ToolParameterProperty>
        required: string list
    }

    type Tool = {
        name: string
        description: string
        inputSchema: ToolInputSchema
    }

    type ToolCallResult = {
        content: obj list
        isError: bool
    }

    // ── Resource types ───────────────────────────────────────────────────────

    type Resource = {
        uri: string
        name: string
        mimeType: string option
        description: string option
    }

    type ResourceContent = {
        uri: string
        mimeType: string option
        text: string option
        blob: string option
    }

// ── MCP Tool Registry ─────────────────────────────────────────────────────────

module McpTools =

    open McpProtocol

    let echoTool : Tool = {
        name = "echo"
        description = "Echo back the provided message."
        inputSchema = {
            ``type`` = "object"
            properties = Map [
                "message", { ``type`` = "string"; description = "The message to echo back." }
            ]
            required = ["message"]
        }
    }

    let listAccountsTool : Tool = {
        name = "list_accounts"
        description = "List all accounts for the current tenant. Optionally converts balances to a display currency."
        inputSchema = {
            ``type`` = "object"
            properties = Map [
                "displayCurrency", { ``type`` = "string"; description = "Optional currency code (e.g. USD, BTC) to convert balances." }
            ]
            required = []
        }
    }

    let getAccountBalanceTool : Tool = {
        name = "get_account_balance"
        description = "Get the balance for a specific account. Optionally converts to a display currency."
        inputSchema = {
            ``type`` = "object"
            properties = Map [
                "accountId", { ``type`` = "string"; description = "The account UUID." }
                "displayCurrency", { ``type`` = "string"; description = "Optional currency code (e.g. USD, BTC) to convert balances." }
            ]
            required = ["accountId"]
        }
    }

    let categorizeTransactionTool : Tool = {
        name = "categorize_transaction"
        description = "Set the category for a specific transaction."
        inputSchema = {
            ``type`` = "object"
            properties = Map [
                "transactionId", { ``type`` = "string"; description = "The transaction UUID." }
                "categoryId", { ``type`` = "string"; description = "The category UUID." }
            ]
            required = ["transactionId"; "categoryId"]
        }
    }

    let createBudgetTool : Tool = {
        name = "create_budget"
        description = "Create a new budget with its first period and allocations."
        inputSchema = {
            ``type`` = "object"
            properties = Map [
                "name", { ``type`` = "string"; description = "Budget name." }
                "period", { ``type`` = "string"; description = "Period type: monthly, biweekly, weekly." }
                "currency", { ``type`` = "string"; description = "Currency code (e.g. USD)." }
                "style", { ``type`` = "string"; description = "Budgeting style: zeroBased, envelope, flexible, traditionalLimits." }
                "allocations", { ``type`` = "array"; description = "Category allocations for the first period. Each item: { categoryId: string, amountMinor: number }" }
            ]
            required = ["name"; "period"; "currency"; "style"; "allocations"]
        }
    }

    let triggerSyncTool : Tool = {
        name = "trigger_sync"
        description = "Trigger an on-demand sync for a data feed connection."
        inputSchema = {
            ``type`` = "object"
            properties = Map [
                "connectionId", { ``type`` = "string"; description = "The connection UUID." }
            ]
            required = ["connectionId"]
        }
    }

    let reconcileAccountTool : Tool = {
        name = "reconcile_account"
        description = "Create a reconciliation for an account, link transactions, and complete it."
        inputSchema = {
            ``type`` = "object"
            properties = Map [
                "accountId", { ``type`` = "string"; description = "The account UUID." }
                "statementDate", { ``type`` = "string"; description = "Statement date (yyyy-MM-dd)." }
                "statementBalanceMinor", { ``type`` = "number"; description = "Statement balance in minor units." }
                "currency", { ``type`` = "string"; description = "Currency code." }
                "transactionIds", { ``type`` = "array"; description = "List of transaction UUIDs to include." }
                "force", { ``type`` = "boolean"; description = "Force complete even if balance differs." }
            ]
            required = ["accountId"; "statementDate"; "statementBalanceMinor"; "currency"; "transactionIds"]
        }
    }

    let acceptMatchTool : Tool = {
        name = "accept_match"
        description = "Accept a proposed match between a feed transaction and a manual transaction."
        inputSchema = {
            ``type`` = "object"
            properties = Map [
                "transactionId", { ``type`` = "string"; description = "The feed transaction UUID." }
                "manualTxnId", { ``type`` = "string"; description = "Optional manual transaction UUID (defaults to the matched one)." }
            ]
            required = ["transactionId"]
        }
    }

    let rejectMatchTool : Tool = {
        name = "reject_match"
        description = "Reject a proposed match for a feed transaction."
        inputSchema = {
            ``type`` = "object"
            properties = Map [
                "transactionId", { ``type`` = "string"; description = "The feed transaction UUID." }
            ]
            required = ["transactionId"]
        }
    }

    let recordRemediationTool : Tool = {
        name = "record_remediation"
        description = "Record a remediation attempt for a failing connection."
        inputSchema = {
            ``type`` = "object"
            properties = Map [
                "connectionId", { ``type`` = "string"; description = "The connection UUID." }
                "strategy", { ``type`` = "string"; description = "Remediation strategy identifier." }
                "notes", { ``type`` = "string"; description = "Optional notes." }
            ]
            required = ["connectionId"; "strategy"]
        }
    }

    let allTools = [
        echoTool
        listAccountsTool
        getAccountBalanceTool
        categorizeTransactionTool
        createBudgetTool
        triggerSyncTool
        reconcileAccountTool
        acceptMatchTool
        rejectMatchTool
        recordRemediationTool
    ]

    let callEcho (args: JsonElement) : ToolCallResult =
        let message =
            match args.TryGetProperty("message") with
            | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
            | _ -> ""
        {
            content = [ {| ``type`` = "text"; text = message |} :> obj ]
            isError = false
        }

    let tryGetDisplayCurrency (args: JsonElement) : string option =
        match args.TryGetProperty("displayCurrency") with
        | true, v when v.ValueKind = JsonValueKind.String -> Some(v.GetString().ToUpperInvariant())
        | _ -> None

    let callListAccounts (ctx: HttpContext) (args: JsonElement) : ToolCallResult =
        let factory = ctx.RequestServices.GetRequiredService<IDbConnectionFactory>()
        let accessor = ctx.RequestServices.GetRequiredService<ITenantContextAccessor>()
        let pricing = ctx.RequestServices.GetRequiredService<IPriceProvider>()
        match accessor.Context with
        | None ->
            { content = [ {| ``type`` = "text"; text = "Unauthorized: no tenant context" |} :> obj ]; isError = true }
        | Some _tc ->
            let repo = AccountRepository.create factory accessor
            let accounts = repo.ListAsync().GetAwaiter().GetResult()
            let displayCurrencyOpt = tryGetDisplayCurrency args

            let accountItems =
                accounts
                |> List.map (fun a ->
                    let balanceOpt = repo.GetBalanceAsync(a.Id).GetAwaiter().GetResult()
                    let balance = balanceOpt |> Option.defaultValue { Posted = Money.zero a.CurrencyCode; Available = Money.zero a.CurrencyCode; Pending = Money.zero a.CurrencyCode }
                    let raw = {| posted = balance.Posted; available = balance.Available; pending = balance.Pending |}
                    let converted =
                        match displayCurrencyOpt with
                        | None -> None
                        | Some target ->
                            let postedConv = PriceConversion.convertMoneyAsync pricing balance.Posted target |> Async.AwaitTask |> Async.RunSynchronously
                            let pendingConv = PriceConversion.convertMoneyAsync pricing balance.Pending target |> Async.AwaitTask |> Async.RunSynchronously
                            let availableConv = { Amount = postedConv.Amount + pendingConv.Amount; CurrencyCode = target }
                            Some {| posted = postedConv; available = availableConv; pending = pendingConv |}
                    {| name = a.Name; accountType = string a.AccountType; currency = a.CurrencyCode; balance = raw; converted = converted |}
                )

            {
                content = [ {| ``type`` = "text"; text = JsonSerializer.Serialize(accountItems, McpProtocol.jsonOptions) |} :> obj ]
                isError = false
            }

    let callGetAccountBalance (ctx: HttpContext) (args: JsonElement) : ToolCallResult =
        let factory = ctx.RequestServices.GetRequiredService<IDbConnectionFactory>()
        let accessor = ctx.RequestServices.GetRequiredService<ITenantContextAccessor>()
        let pricing = ctx.RequestServices.GetRequiredService<IPriceProvider>()
        match accessor.Context with
        | None ->
            { content = [ {| ``type`` = "text"; text = "Unauthorized: no tenant context" |} :> obj ]; isError = true }
        | Some _tc ->
            let accountIdStr =
                match args.TryGetProperty("accountId") with
                | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
                | _ -> ""
            match Guid.TryParse(accountIdStr) with
            | false, _ ->
                { content = [ {| ``type`` = "text"; text = "Invalid accountId" |} :> obj ]; isError = true }
            | true, accountId ->
                let repo = AccountRepository.create factory accessor
                let balanceOpt = repo.GetBalanceAsync(accountId).GetAwaiter().GetResult()
                match balanceOpt with
                | None ->
                    { content = [ {| ``type`` = "text"; text = "Account not found" |} :> obj ]; isError = true }
                | Some balance ->
                    let displayCurrencyOpt = tryGetDisplayCurrency args
                    let converted =
                        match displayCurrencyOpt with
                        | None -> None
                        | Some target ->
                            let postedConv = PriceConversion.convertMoneyAsync pricing balance.Posted target |> Async.AwaitTask |> Async.RunSynchronously
                            let pendingConv = PriceConversion.convertMoneyAsync pricing balance.Pending target |> Async.AwaitTask |> Async.RunSynchronously
                            let availableConv = { Amount = postedConv.Amount + pendingConv.Amount; CurrencyCode = target }
                            Some {| posted = postedConv; available = availableConv; pending = pendingConv |}
                    let result = {| balance = balance; converted = converted; displayCurrency = displayCurrencyOpt |}
                    { content = [ {| ``type`` = "text"; text = JsonSerializer.Serialize(result, McpProtocol.jsonOptions) |} :> obj ]; isError = false }

    // ── Mutation tools ───────────────────────────────────────────────────────

    let private requireTenant (accessor: ITenantContextAccessor) f =
        match accessor.Context with
        | None -> { content = [ {| ``type`` = "text"; text = "Unauthorized: no tenant context" |} :> obj ]; isError = true }
        | Some tc -> f tc

    let private tryGetGuidArg (args: JsonElement) (name: string) : Guid option =
        match args.TryGetProperty(name) with
        | true, v when v.ValueKind = JsonValueKind.String ->
            match Guid.TryParse(v.GetString()) with true, g -> Some g | _ -> None
        | _ -> None

    let private tryGetStringArg (args: JsonElement) (name: string) : string option =
        match args.TryGetProperty(name) with
        | true, v when v.ValueKind = JsonValueKind.String -> Some(v.GetString())
        | _ -> None

    let private tryGetBoolArg (args: JsonElement) (name: string) : bool option =
        match args.TryGetProperty(name) with
        | true, v when v.ValueKind = JsonValueKind.True -> Some true
        | true, v when v.ValueKind = JsonValueKind.False -> Some false
        | _ -> None

    let private tryGetInt64Arg (args: JsonElement) (name: string) : int64 option =
        match args.TryGetProperty(name) with
        | true, v when v.ValueKind = JsonValueKind.Number ->
            match v.TryGetInt64() with true, n -> Some n | _ -> None
        | _ -> None

    let private fromMinor (minor: int64) (currency: string) : Money =
        MoneyHelpers.fromMinorUnits minor currency

    let callCategorizeTransaction (ctx: HttpContext) (args: JsonElement) : ToolCallResult =
        let factory = ctx.RequestServices.GetRequiredService<IDbConnectionFactory>()
        let accessor = ctx.RequestServices.GetRequiredService<ITenantContextAccessor>()
        let categoryRepo = CategoryRepository.create factory accessor
        requireTenant accessor (fun _tc ->
            match tryGetGuidArg args "transactionId", tryGetGuidArg args "categoryId" with
            | None, _ -> { content = [ {| ``type`` = "text"; text = "Invalid transactionId" |} :> obj ]; isError = true }
            | _, None -> { content = [ {| ``type`` = "text"; text = "Invalid categoryId" |} :> obj ]; isError = true }
            | Some txnId, Some catId ->
                let txnRepo = TransactionRepository.create factory accessor
                let catOpt = categoryRepo.GetAsync(catId).GetAwaiter().GetResult()
                match catOpt with
                | None -> { content = [ {| ``type`` = "text"; text = "Category not found" |} :> obj ]; isError = true }
                | Some _ ->
                    let txnOpt = txnRepo.GetAsync(txnId).GetAwaiter().GetResult()
                    match txnOpt with
                    | None -> { content = [ {| ``type`` = "text"; text = "Transaction not found" |} :> obj ]; isError = true }
                    | Some txn ->
                        let updated = { txn with CategoryId = Some catId; UpdatedAt = DateTimeOffset.UtcNow }
                        txnRepo.UpdateAsync(updated).GetAwaiter().GetResult()
                        { content = [ {| ``type`` = "text"; text = JsonSerializer.Serialize({| ok = true; transactionId = txnId; categoryId = catId |}, McpProtocol.jsonOptions) |} :> obj ]; isError = false }
        )

    let callCreateBudget (ctx: HttpContext) (args: JsonElement) : ToolCallResult =
        let factory = ctx.RequestServices.GetRequiredService<IDbConnectionFactory>()
        let accessor = ctx.RequestServices.GetRequiredService<ITenantContextAccessor>()
        requireTenant accessor (fun tc ->
            let name = tryGetStringArg args "name" |> Option.defaultValue ""
            let periodStr = tryGetStringArg args "period" |> Option.defaultValue ""
            let currency = tryGetStringArg args "currency" |> Option.defaultValue "USD"
            let styleStr = tryGetStringArg args "style" |> Option.defaultValue ""
            let periodOpt =
                match periodStr.ToLowerInvariant() with
                | "monthly" -> Some BudgetPeriod.Monthly
                | "biweekly" -> Some BudgetPeriod.BiWeekly
                | "weekly" -> Some BudgetPeriod.Weekly
                | _ -> None
            let styleOpt =
                match styleStr.ToLowerInvariant() with
                | "zerobased" | "zero_based" -> Some BudgetingStyle.ZeroBased
                | "envelope" -> Some BudgetingStyle.Envelope
                | "flexible" -> Some BudgetingStyle.Flexible
                | "traditionallimits" | "traditional_limits" -> Some BudgetingStyle.TraditionalLimits
                | _ -> None
            match periodOpt, styleOpt with
            | None, _ -> { content = [ {| ``type`` = "text"; text = "Invalid period. Use: monthly, biweekly, weekly" |} :> obj ]; isError = true }
            | _, None -> { content = [ {| ``type`` = "text"; text = "Invalid style. Use: zeroBased, envelope, flexible, traditionalLimits" |} :> obj ]; isError = true }
            | Some period, Some style ->
                let budgetRepo = BudgetRepository.create factory accessor
                let periodRepo = BudgetPeriodRepository.create factory accessor
                let budget = {
                    Id = Guid.NewGuid()
                    TenantId = tc.TenantId
                    UserId = tc.UserId
                    Name = name
                    Style = style
                    Period = period
                    CurrencyCode = currency
                    Income = Money.zero currency
                    IsActive = true
                    StartsOn = DateOnly.FromDateTime(DateTime.UtcNow)
                    CreatedAt = DateTimeOffset.UtcNow
                    UpdatedAt = DateTimeOffset.UtcNow
                }
                let budgetId = budgetRepo.CreateAsync(budget).GetAwaiter().GetResult()
                let endDate =
                    match period with
                    | BudgetPeriod.Monthly -> budget.StartsOn.AddMonths(1).AddDays(-1)
                    | BudgetPeriod.BiWeekly -> budget.StartsOn.AddDays(13)
                    | BudgetPeriod.Weekly -> budget.StartsOn.AddDays(6)
                    | BudgetPeriod.Custom d -> budget.StartsOn.AddDays(d - 1)
                let firstPeriod = {
                    Id = Guid.NewGuid()
                    BudgetId = budgetId
                    TenantId = tc.TenantId
                    StartDate = budget.StartsOn
                    EndDate = endDate
                    Status = BudgetPeriodStatus.Open
                    CreatedAt = DateTimeOffset.UtcNow
                    UpdatedAt = DateTimeOffset.UtcNow
                }
                let allocations =
                    match args.TryGetProperty("allocations") with
                    | true, v when v.ValueKind = JsonValueKind.Array ->
                        v.EnumerateArray()
                        |> Seq.choose (fun el ->
                            match tryGetGuidArg el "categoryId", tryGetInt64Arg el "amountMinor" with
                            | Some cid, Some minor ->
                                Some {
                                    BudgetPeriodId = firstPeriod.Id
                                    CategoryId = cid
                                    AllocatedAmount = fromMinor minor currency
                                    OpeningBalance = Money.zero currency
                                    RolloverBalance = Money.zero currency
                                    RolloverEnabled = false
                                }
                            | _ -> None)
                        |> Seq.toList
                    | _ -> []
                periodRepo.CreatePeriodAsync(firstPeriod, allocations).GetAwaiter().GetResult() |> ignore
                { content = [ {| ``type`` = "text"; text = JsonSerializer.Serialize({| ok = true; budgetId = budgetId |}, McpProtocol.jsonOptions) |} :> obj ]; isError = false }
        )

    let callTriggerSync (ctx: HttpContext) (args: JsonElement) : ToolCallResult =
        let factory = ctx.RequestServices.GetRequiredService<IDbConnectionFactory>()
        let accessor = ctx.RequestServices.GetRequiredService<ITenantContextAccessor>()
        let http = ctx.RequestServices.GetRequiredService<HttpClient>()
        requireTenant accessor (fun tc ->
            match tryGetGuidArg args "connectionId" with
            | None -> { content = [ {| ``type`` = "text"; text = "Invalid connectionId" |} :> obj ]; isError = true }
            | Some connId ->
                let connRepo = DataFeedConnectionRepository.create factory accessor
                let connOpt = connRepo.GetAsync(connId).GetAwaiter().GetResult()
                match connOpt with
                | None -> { content = [ {| ``type`` = "text"; text = "Connection not found" |} :> obj ]; isError = true }
                | Some conn ->
                    let plaidUrl = Environment.GetEnvironmentVariable("STEWARD_PLAID_INGESTION_URL")
                    let akoyaUrl = Environment.GetEnvironmentVariable("STEWARD_AKOYA_INGESTION_URL")
                    let token = Environment.GetEnvironmentVariable("STEWARD_SERVICE_TOKEN")
                    let provider = DataFeedConnection.providerOf conn.Metadata
                    let sendSync (url: string) =
                        let req = new HttpRequestMessage(HttpMethod.Post, $"{url.TrimEnd('/')}/sync-trigger")
                        req.Headers.Authorization <- System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token)
                        let payload = System.Text.Json.Nodes.JsonObject()
                        payload["tenantId"] <- System.Text.Json.Nodes.JsonValue.Create(tc.TenantId.ToString())
                        payload["connectionId"] <- System.Text.Json.Nodes.JsonValue.Create(connId.ToString())
                        req.Content <- new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
                        let resp = http.SendAsync(req).GetAwaiter().GetResult()
                        let body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                        if not resp.IsSuccessStatusCode then
                            Error $"Ingestion failed: {body}"
                        else
                            Ok {| status = "sync_triggered"; provider = string provider; connectionId = connId |}
                    let result =
                        match provider with
                        | DataFeedProvider.Plaid ->
                            match String.IsNullOrEmpty(plaidUrl), String.IsNullOrEmpty(token) with
                            | false, false -> sendSync plaidUrl
                            | _ -> Error "Plaid ingestion URL or service token not configured"
                        | DataFeedProvider.Akoya ->
                            match String.IsNullOrEmpty(akoyaUrl), String.IsNullOrEmpty(token) with
                            | false, false -> sendSync akoyaUrl
                            | _ -> Error "Akoya ingestion URL or service token not configured"
                        | _ -> Error "Provider not yet supported for sync trigger"
                    match result with
                    | Ok resp ->
                        { content = [ {| ``type`` = "text"; text = JsonSerializer.Serialize({| ok = true; result = resp |}, McpProtocol.jsonOptions) |} :> obj ]; isError = false }
                    | Error msg ->
                        { content = [ {| ``type`` = "text"; text = JsonSerializer.Serialize({| ok = false; error = msg |}, McpProtocol.jsonOptions) |} :> obj ]; isError = true }
        )

    let callReconcileAccount (ctx: HttpContext) (args: JsonElement) : ToolCallResult =
        let factory = ctx.RequestServices.GetRequiredService<IDbConnectionFactory>()
        let accessor = ctx.RequestServices.GetRequiredService<ITenantContextAccessor>()
        requireTenant accessor (fun tc ->
            match tryGetGuidArg args "accountId", tryGetStringArg args "statementDate", tryGetInt64Arg args "statementBalanceMinor" with
            | None, _, _ -> { content = [ {| ``type`` = "text"; text = "Invalid accountId" |} :> obj ]; isError = true }
            | _, None, _ -> { content = [ {| ``type`` = "text"; text = "Missing statementDate" |} :> obj ]; isError = true }
            | _, _, None -> { content = [ {| ``type`` = "text"; text = "Missing statementBalanceMinor" |} :> obj ]; isError = true }
            | Some accountId, Some dateStr, Some balanceMinor ->
                match DateOnly.TryParse(dateStr) with
                | false, _ -> { content = [ {| ``type`` = "text"; text = "Invalid statementDate format. Use: yyyy-MM-dd" |} :> obj ]; isError = true }
                | true, statementDate ->
                    let currency = tryGetStringArg args "currency" |> Option.defaultValue "USD"
                    let force = tryGetBoolArg args "force" |> Option.defaultValue false
                    let txnIds =
                        match args.TryGetProperty("transactionIds") with
                        | true, v when v.ValueKind = JsonValueKind.Array ->
                            v.EnumerateArray()
                            |> Seq.choose (fun el -> match Guid.TryParse(el.GetString()) with true, g -> Some g | _ -> None)
                            |> Seq.toList
                        | _ -> []
                    let reconRepo = ReconciliationRepository.create factory accessor
                    let recon : Reconciliation = {
                        Id = Guid.NewGuid()
                        TenantId = tc.TenantId
                        AccountId = accountId
                        StatementDate = statementDate
                        StatementBalance = fromMinor balanceMinor currency
                        Status = ReconciliationStatus.Open
                        Note = None
                        CreatedByUserId = tc.UserId
                        StartedAt = DateTimeOffset.UtcNow
                        CompletedAt = None
                    }
                    let reconId = reconRepo.CreateAsync(recon).GetAwaiter().GetResult()
                    reconRepo.UpdateIncludedTransactionsAsync(reconId, txnIds, []).GetAwaiter().GetResult()
                    let completeResult = reconRepo.CompleteAsync(reconId, force, None).GetAwaiter().GetResult()
                    match completeResult with
                    | Ok diffMinor ->
                        { content = [ {| ``type`` = "text"; text = JsonSerializer.Serialize({| ok = true; reconciliationId = reconId; diffMinor = diffMinor |}, McpProtocol.jsonOptions) |} :> obj ]; isError = false }
                    | Error msg when msg.StartsWith("diff:") ->
                        let diffStr = msg.[5..]
                        let diffMinor = int64 diffStr
                        { content = [ {| ``type`` = "text"; text = JsonSerializer.Serialize({| ok = false; error = "Balance mismatch"; diffMinor = diffMinor; reconciliationId = reconId |}, McpProtocol.jsonOptions) |} :> obj ]; isError = true }
                    | Error msg ->
                        { content = [ {| ``type`` = "text"; text = JsonSerializer.Serialize({| ok = false; error = msg |}, McpProtocol.jsonOptions) |} :> obj ]; isError = true }
        )

    let callAcceptMatch (ctx: HttpContext) (args: JsonElement) : ToolCallResult =
        let factory = ctx.RequestServices.GetRequiredService<IDbConnectionFactory>()
        let accessor = ctx.RequestServices.GetRequiredService<ITenantContextAccessor>()
        requireTenant accessor (fun _tc ->
            match tryGetGuidArg args "transactionId" with
            | None -> { content = [ {| ``type`` = "text"; text = "Invalid transactionId" |} :> obj ]; isError = true }
            | Some txnId ->
                let txnRepo = TransactionRepository.create factory accessor
                let txnOpt = txnRepo.GetAsync(txnId).GetAwaiter().GetResult()
                match txnOpt with
                | None -> { content = [ {| ``type`` = "text"; text = "Transaction not found" |} :> obj ]; isError = true }
                | Some txn when txn.Status <> TransactionStatus.NeedsReview ->
                    { content = [ {| ``type`` = "text"; text = "Transaction is not in NeedsReview status" |} :> obj ]; isError = true }
                | Some txn ->
                    let manualIdResult =
                        match tryGetGuidArg args "manualTxnId" with
                        | Some m -> Ok m
                        | None ->
                            match txn.MatchedTransactionId with
                            | Some m -> Ok m
                            | None -> Error "No manual transaction to accept"
                    match manualIdResult with
                    | Error msg -> { content = [ {| ``type`` = "text"; text = msg |} :> obj ]; isError = true }
                    | Ok manualId ->
                        let manualOpt = txnRepo.GetAsync(manualId).GetAwaiter().GetResult()
                        match manualOpt with
                        | None -> { content = [ {| ``type`` = "text"; text = "Manual transaction not found" |} :> obj ]; isError = true }
                        | Some manual ->
                            let now = DateTimeOffset.UtcNow
                            let updatedFeed = { txn with Status = TransactionStatus.Cleared; MatchedTransactionId = Some manualId; UpdatedAt = now }
                            let updatedManual = { manual with Status = TransactionStatus.Cleared; MatchedTransactionId = Some txnId; UpdatedAt = now }
                            txnRepo.UpdateAsync(updatedFeed).GetAwaiter().GetResult()
                            txnRepo.UpdateAsync(updatedManual).GetAwaiter().GetResult()
                            { content = [ {| ``type`` = "text"; text = JsonSerializer.Serialize({| ok = true; action = "accept"; manualTxnId = manualId |}, McpProtocol.jsonOptions) |} :> obj ]; isError = false }
        )

    let callRejectMatch (ctx: HttpContext) (args: JsonElement) : ToolCallResult =
        let factory = ctx.RequestServices.GetRequiredService<IDbConnectionFactory>()
        let accessor = ctx.RequestServices.GetRequiredService<ITenantContextAccessor>()
        requireTenant accessor (fun _tc ->
            match tryGetGuidArg args "transactionId" with
            | None -> { content = [ {| ``type`` = "text"; text = "Invalid transactionId" |} :> obj ]; isError = true }
            | Some txnId ->
                let txnRepo = TransactionRepository.create factory accessor
                let txnOpt = txnRepo.GetAsync(txnId).GetAwaiter().GetResult()
                match txnOpt with
                | None -> { content = [ {| ``type`` = "text"; text = "Transaction not found" |} :> obj ]; isError = true }
                | Some txn when txn.Status <> TransactionStatus.NeedsReview ->
                    { content = [ {| ``type`` = "text"; text = "Transaction is not in NeedsReview status" |} :> obj ]; isError = true }
                | Some txn ->
                    let updated = { txn with Status = TransactionStatus.Cleared; MatchedTransactionId = None; MatchConfidence = None; UpdatedAt = DateTimeOffset.UtcNow }
                    txnRepo.UpdateAsync(updated).GetAwaiter().GetResult()
                    { content = [ {| ``type`` = "text"; text = JsonSerializer.Serialize({| ok = true; action = "reject" |}, McpProtocol.jsonOptions) |} :> obj ]; isError = false }
        )

    let callRecordRemediation (ctx: HttpContext) (args: JsonElement) : ToolCallResult =
        let factory = ctx.RequestServices.GetRequiredService<IDbConnectionFactory>()
        let accessor = ctx.RequestServices.GetRequiredService<ITenantContextAccessor>()
        requireTenant accessor (fun tc ->
            match tryGetGuidArg args "connectionId", tryGetStringArg args "strategy" with
            | None, _ -> { content = [ {| ``type`` = "text"; text = "Invalid connectionId" |} :> obj ]; isError = true }
            | _, None -> { content = [ {| ``type`` = "text"; text = "Missing strategy" |} :> obj ]; isError = true }
            | Some connId, Some strategy ->
                let repo = RemediationAttemptRepository.create factory accessor
                let attempt : RemediationAttempt = {
                    Id = Guid.NewGuid()
                    TenantId = tc.TenantId
                    ConnectionId = connId
                    StartedAt = DateTimeOffset.UtcNow
                    CompletedAt = None
                    ActorAgentId = None
                    ActorUserId = Some tc.UserId
                    Strategy = strategy
                    Outcome = None
                    Notes = tryGetStringArg args "notes"
                }
                let id = repo.CreateAsync(attempt).GetAwaiter().GetResult()
                { content = [ {| ``type`` = "text"; text = JsonSerializer.Serialize({| ok = true; remediationId = id |}, McpProtocol.jsonOptions) |} :> obj ]; isError = false }
        )

// ── MCP Resource Registry ─────────────────────────────────────────────────────

module McpResources =

    open McpProtocol

    let allResources : Resource list = [
        { uri = "steward://accounts"; name = "Accounts"; mimeType = Some "application/json"; description = Some "List of financial accounts" }
        { uri = "steward://accounts/{id}"; name = "Account"; mimeType = Some "application/json"; description = Some "Individual account details" }
        { uri = "steward://transactions"; name = "Transactions"; mimeType = Some "application/json"; description = Some "Recent transactions (optionally filter by accountId, from, to)" }
        { uri = "steward://transactions/{id}"; name = "Transaction"; mimeType = Some "application/json"; description = Some "Individual transaction details" }
        { uri = "steward://budgets"; name = "Budgets"; mimeType = Some "application/json"; description = Some "List of budgets" }
        { uri = "steward://budgets/{id}"; name = "Budget"; mimeType = Some "application/json"; description = Some "Individual budget details" }
        { uri = "steward://budgets/{id}/periods/{periodId}/report"; name = "Budget Report"; mimeType = Some "application/json"; description = Some "Budget period report" }
        { uri = "steward://categories"; name = "Categories"; mimeType = Some "application/json"; description = Some "List of categories (flat or tree)" }
    ]

// ── MCP Server Handler ────────────────────────────────────────────────────────

module McpServer =

    open McpProtocol

    let private readBody (ctx: HttpContext) : Task<string> =
        task {
            use reader = new StreamReader(ctx.Request.Body, Encoding.UTF8)
            return! reader.ReadToEndAsync()
        }

    let private handleInitialize (id: JsonElement) : JsonDocument =
        let result : InitializeResult = {
            protocolVersion = "2024-11-05"
            capabilities = {
                tools = Some {| listChanged = false |}
                resources = Some {| listChanged = false; subscribe = false |}
                prompts = None
            }
            serverInfo = { name = "steward-mcp"; version = "0.1.0" }
        }
        makeResult id result

    let private handleToolsList (id: JsonElement) : JsonDocument =
        let tools = McpTools.allTools
        makeResult id {| tools = tools |}

    let private handleToolsCall (ctx: HttpContext) (id: JsonElement) (paramsEl: JsonElement) : JsonDocument =
        let name =
            match paramsEl.TryGetProperty("name") with
            | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
            | _ -> ""
        let arguments =
            match paramsEl.TryGetProperty("arguments") with
            | true, v -> v
            | _ -> JsonDocument.Parse("{}").RootElement
        let result : ToolCallResult =
            match name with
            | "echo" -> McpTools.callEcho arguments
            | "list_accounts" -> McpTools.callListAccounts ctx arguments
            | "get_account_balance" -> McpTools.callGetAccountBalance ctx arguments
            | "categorize_transaction" -> McpTools.callCategorizeTransaction ctx arguments
            | "create_budget" -> McpTools.callCreateBudget ctx arguments
            | "trigger_sync" -> McpTools.callTriggerSync ctx arguments
            | "reconcile_account" -> McpTools.callReconcileAccount ctx arguments
            | "accept_match" -> McpTools.callAcceptMatch ctx arguments
            | "reject_match" -> McpTools.callRejectMatch ctx arguments
            | "record_remediation" -> McpTools.callRecordRemediation ctx arguments
            | _ -> { content = [ {| ``type`` = "text"; text = $"Unknown tool: {name}" |} :> obj ]; isError = true }
        makeResult id result

    let private handleResourcesList (id: JsonElement) : JsonDocument =
        makeResult id {| resources = McpResources.allResources |}

    // ── URI parsing helpers ──────────────────────────────────────────────────

    let private parseStewardUri (uri: string) =
        if uri.StartsWith("steward://") then
            let rest = uri.Substring("steward://".Length)
            let pathPart, queryPart =
                match rest.IndexOf('?') with
                | -1 -> rest, ""
                | i -> rest.Substring(0, i), rest.Substring(i + 1)
            let segments = pathPart.Split('/') |> Array.toList |> List.filter (String.IsNullOrEmpty >> not)
            let queryParams =
                if String.IsNullOrEmpty(queryPart) then Map.empty
                else
                    queryPart.Split('&')
                    |> Array.choose (fun pair ->
                        match pair.Split('=') with
                        | [| k; v |] -> Some (k, v)
                        | _ -> None)
                    |> Map.ofArray
            Some (segments, queryParams)
        else None

    // ── Response mapping helpers (mirror public API shapes) ──────────────────

    let private accountTypeToString (t: AccountType) : string =
        match t with
        | AccountType.Checking    -> "checking"
        | AccountType.Savings     -> "savings"
        | AccountType.CreditCard  -> "creditCard"
        | AccountType.Investment  -> "investment"
        | AccountType.Loan        -> "loan"
        | AccountType.Cash        -> "cash"

    let private budgetPeriodToString (p: BudgetPeriod) : string =
        match p with
        | BudgetPeriod.Monthly   -> "monthly"
        | BudgetPeriod.BiWeekly  -> "biweekly"
        | BudgetPeriod.Weekly    -> "weekly"
        | BudgetPeriod.Custom d  -> $"custom:{d}"

    let private budgetingStyleToString (s: BudgetingStyle) : string =
        match s with
        | BudgetingStyle.ZeroBased        -> "zeroBased"
        | BudgetingStyle.Envelope         -> "envelope"
        | BudgetingStyle.Flexible         -> "flexible"
        | BudgetingStyle.TraditionalLimits -> "traditionalLimits"

    let private toMinor (money: Money) : int64 =
        MoneyHelpers.toMinorUnits money

    let private accountToResponse (account: Account) =
        {|
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
        |}

    let private periodToResponse (period: BudgetPeriodRecord) (allocs: BudgetPeriodCategoryAllocation list) =
        {|
            id = period.Id
            startDate = period.StartDate.ToString("yyyy-MM-dd")
            endDate = period.EndDate.ToString("yyyy-MM-dd")
            status = match period.Status with BudgetPeriodStatus.Open -> "open" | BudgetPeriodStatus.Closed -> "closed"
            allocations =
                allocs
                |> List.map (fun a -> {|
                    categoryId = a.CategoryId
                    allocatedMinor = toMinor a.AllocatedAmount
                    openingBalanceMinor = toMinor a.OpeningBalance
                    rolloverBalanceMinor = toMinor a.RolloverBalance
                    currency = a.AllocatedAmount.CurrencyCode
                    rolloverEnabled = a.RolloverEnabled
                |})
        |}

    let private budgetToResponse (budget: Budget) (currentPeriod: obj option) =
        {|
            id = budget.Id
            name = budget.Name
            period = budgetPeriodToString budget.Period
            currency = budget.CurrencyCode
            style = budgetingStyleToString budget.Style
            incomeMinor = toMinor budget.Income
            isActive = budget.IsActive
            startsOn = budget.StartsOn.ToString("yyyy-MM-dd")
            currentPeriod = currentPeriod
        |}

    let private categoryToResponse (category: Category) : CategoryResponse =
        {
            id = category.Id
            name = category.Name
            parentId = category.ParentCategoryId
            isSystem = category.IsSystem
            currency = category.CurrencyCode
            rolloverEnabled = category.RolloverEnabled
            createdAt = category.CreatedAt
            updatedAt = category.UpdatedAt
        }

    let private buildCategoryTree (categories: Category list) : CategoryTreeNode list =
        let byParent = categories |> List.groupBy (fun c -> c.ParentCategoryId) |> Map.ofList
        let rec buildNode (cat: Category) =
            let children =
                byParent
                |> Map.tryFind (Some cat.Id)
                |> Option.defaultValue []
                |> List.sortBy (fun (c: Category) -> c.Name)
                |> List.map buildNode
            {
                id = cat.Id
                name = cat.Name
                isSystem = cat.IsSystem
                currency = cat.CurrencyCode
                rolloverEnabled = cat.RolloverEnabled
                children = children
            }
        let roots =
            byParent
            |> Map.tryFind None
            |> Option.defaultValue []
            |> List.sortBy (fun (c: Category) -> c.Name)
            |> List.map buildNode
        roots

    // ── Transaction response mapping (mirror public API shape) ───────────────

    let private txnStatusToString (s: TransactionStatus) : string =
        match s with
        | TransactionStatus.Pending     -> "pending"
        | TransactionStatus.NeedsReview -> "needsReview"
        | TransactionStatus.Cleared     -> "cleared"
        | TransactionStatus.Reconciled  -> "reconciled"

    let private txnSourceToString (s: TransactionSource) : string =
        match s with
        | TransactionSource.Manual -> "manual"
        | TransactionSource.DataFeed provider -> $"dataFeed:{provider}"
        | TransactionSource.Import format -> $"import:{format}"

    let private txnToResponse (txn: Transaction) : TransactionResponse =
        {
            id = txn.Id
            accountId = txn.AccountId
            occurredAt = txn.OccurredAt
            postedAt = txn.PostedAt
            amount = txn.Amount.Amount
            currency = txn.Amount.CurrencyCode
            description = txn.Description
            merchant = txn.Merchant
            notes = txn.Memo
            categoryId = txn.CategoryId
            status = txnStatusToString txn.Status
            source = txnSourceToString txn.Source
            transferAccountId = txn.TransferAccountId
            createdAt = txn.CreatedAt
            updatedAt = txn.UpdatedAt
        }

    let private handleResourcesRead (ctx: HttpContext) (id: JsonElement) (paramsEl: JsonElement) : JsonDocument =
        let uri =
            match paramsEl.TryGetProperty("uri") with
            | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
            | _ -> ""

        let factory = ctx.RequestServices.GetRequiredService<IDbConnectionFactory>()
        let accessor = ctx.RequestServices.GetRequiredService<ITenantContextAccessor>()
        let pricing = ctx.RequestServices.GetRequiredService<IPriceProvider>()

        let contents : ResourceContent list =
            match accessor.Context with
            | None ->
                [ { uri = uri; mimeType = Some "text/plain"; text = Some "Unauthorized"; blob = None } ]
            | Some _tc ->
                match parseStewardUri uri with
                | None ->
                    [ { uri = uri; mimeType = Some "text/plain"; text = Some "Invalid steward URI"; blob = None } ]
                | Some (segments, query) ->
                    match segments with
                    | ["accounts"] ->
                        let repo = AccountRepository.create factory accessor
                        let accounts = repo.ListAsync().GetAwaiter().GetResult()
                        let resp = {| accounts = accounts |> List.map accountToResponse |}
                        [ { uri = uri; mimeType = Some "application/json"; text = Some(JsonSerializer.Serialize(resp, McpProtocol.jsonOptions)); blob = None } ]

                    | ["accounts"; idStr] ->
                        match Guid.TryParse(idStr) with
                        | false, _ ->
                            [ { uri = uri; mimeType = Some "text/plain"; text = Some "Invalid account id"; blob = None } ]
                        | true, accountId ->
                            let repo = AccountRepository.create factory accessor
                            let accountOpt = repo.GetAsync(accountId).GetAwaiter().GetResult()
                            match accountOpt with
                            | None ->
                                [ { uri = uri; mimeType = Some "text/plain"; text = Some "Account not found"; blob = None } ]
                            | Some account ->
                                let resp = accountToResponse account
                                [ { uri = uri; mimeType = Some "application/json"; text = Some(JsonSerializer.Serialize(resp, McpProtocol.jsonOptions)); blob = None } ]

                    | ["transactions"] ->
                        let txnRepo = TransactionRepository.create factory accessor
                        let accountIdOpt = query.TryFind "accountId" |> Option.bind (fun s -> match Guid.TryParse(s) with true, g -> Some g | _ -> None)
                        let fromOpt = query.TryFind "from" |> Option.bind (fun s -> match DateTimeOffset.TryParse(s) with true, d -> Some d | _ -> None)
                        let toOpt = query.TryFind "to" |> Option.bind (fun s -> match DateTimeOffset.TryParse(s) with true, d -> Some d | _ -> None)
                        let statusOpt = query.TryFind "status" |> Option.bind (fun s -> try Some (TransactionRepository.statusFromString s) with _ -> None)
                        let limit = query.TryFind "limit" |> Option.bind (fun s -> match Int32.TryParse(s) with true, n -> Some (Math.Max(1, Math.Min(n, 250))) | _ -> None) |> Option.defaultValue 50

                        let filter : TransactionListFilter = {
                            AccountId = accountIdOpt
                            From = fromOpt
                            To = toOpt
                            Status = statusOpt
                            Limit = limit
                            Cursor = None
                        }
                        let txns = txnRepo.ListAsync(filter).GetAwaiter().GetResult()
                        let resp : TransactionListResponse = {
                            items = txns |> List.map txnToResponse
                            nextCursor = None
                        }
                        [ { uri = uri; mimeType = Some "application/json"; text = Some(JsonSerializer.Serialize(resp, McpProtocol.jsonOptions)); blob = None } ]

                    | ["transactions"; idStr] ->
                        match Guid.TryParse(idStr) with
                        | false, _ ->
                            [ { uri = uri; mimeType = Some "text/plain"; text = Some "Invalid transaction id"; blob = None } ]
                        | true, txnId ->
                            let txnRepo = TransactionRepository.create factory accessor
                            let txnOpt = txnRepo.GetAsync(txnId).GetAwaiter().GetResult()
                            match txnOpt with
                            | None ->
                                [ { uri = uri; mimeType = Some "text/plain"; text = Some "Transaction not found"; blob = None } ]
                            | Some txn ->
                                let resp = txnToResponse txn
                                [ { uri = uri; mimeType = Some "application/json"; text = Some(JsonSerializer.Serialize(resp, McpProtocol.jsonOptions)); blob = None } ]

                    | ["budgets"] ->
                        let budgetRepo = BudgetRepository.create factory accessor
                        let budgets = budgetRepo.ListAsync().GetAwaiter().GetResult()
                        let periodRepo = BudgetPeriodRepository.create factory accessor
                        let resp =
                            budgets
                            |> List.map (fun b ->
                                let openPeriodOpt = periodRepo.GetOpenPeriodAsync(b.Id).GetAwaiter().GetResult()
                                let currentPeriodObj =
                                    match openPeriodOpt with
                                    | None -> None
                                    | Some period ->
                                        let allocs = periodRepo.ListAllocationsByPeriodAsync(period.Id).GetAwaiter().GetResult()
                                        Some (periodToResponse period allocs :> obj)
                                budgetToResponse b currentPeriodObj)
                        [ { uri = uri; mimeType = Some "application/json"; text = Some(JsonSerializer.Serialize({| budgets = resp |}, McpProtocol.jsonOptions)); blob = None } ]

                    | ["budgets"; idStr] ->
                        match Guid.TryParse(idStr) with
                        | false, _ ->
                            [ { uri = uri; mimeType = Some "text/plain"; text = Some "Invalid budget id"; blob = None } ]
                        | true, budgetId ->
                            let budgetRepo = BudgetRepository.create factory accessor
                            let periodRepo = BudgetPeriodRepository.create factory accessor
                            let budgetOpt = budgetRepo.GetAsync(budgetId).GetAwaiter().GetResult()
                            match budgetOpt with
                            | None ->
                                [ { uri = uri; mimeType = Some "text/plain"; text = Some "Budget not found"; blob = None } ]
                            | Some budget ->
                                let openPeriodOpt = periodRepo.GetOpenPeriodAsync(budgetId).GetAwaiter().GetResult()
                                let currentPeriodObj =
                                    match openPeriodOpt with
                                    | None -> None
                                    | Some period ->
                                        let allocs = periodRepo.ListAllocationsByPeriodAsync(period.Id).GetAwaiter().GetResult()
                                        Some (periodToResponse period allocs :> obj)
                                let resp = budgetToResponse budget currentPeriodObj
                                [ { uri = uri; mimeType = Some "application/json"; text = Some(JsonSerializer.Serialize(resp, McpProtocol.jsonOptions)); blob = None } ]

                    | ["budgets"; idStr; "periods"; periodIdStr; "report"] ->
                        match Guid.TryParse(idStr), Guid.TryParse(periodIdStr) with
                        | (false, _), _ | _, (false, _) ->
                            [ { uri = uri; mimeType = Some "text/plain"; text = Some "Invalid budget or period id"; blob = None } ]
                        | (true, budgetId), (true, periodId) ->
                            let budgetRepo = BudgetRepository.create factory accessor
                            let periodRepo = BudgetPeriodRepository.create factory accessor
                            let categoryRepo = CategoryRepository.create factory accessor
                            let budgetOpt = budgetRepo.GetAsync(budgetId).GetAwaiter().GetResult()
                            let periodOpt = periodRepo.GetPeriodAsync(periodId).GetAwaiter().GetResult()
                            match budgetOpt, periodOpt with
                            | None, _ | _, None ->
                                [ { uri = uri; mimeType = Some "text/plain"; text = Some "Budget or period not found"; blob = None } ]
                            | Some budget, Some period when period.BudgetId <> budgetId ->
                                [ { uri = uri; mimeType = Some "text/plain"; text = Some "Period does not belong to budget"; blob = None } ]
                            | Some budget, Some period ->
                                let displayCurrencyOpt = query.TryFind "displayCurrency" |> Option.map (fun s -> s.ToUpperInvariant())
                                let targetCurrency = displayCurrencyOpt |> Option.defaultValue budget.CurrencyCode
                                let allocs = periodRepo.ListAllocationsByPeriodAsync(periodId).GetAwaiter().GetResult()
                                let spendDetail = periodRepo.GetPeriodSpendAsync(periodId).GetAwaiter().GetResult()
                                let categories = categoryRepo.ListAsync().GetAwaiter().GetResult()
                                let categoryNames = categories |> List.map (fun c -> c.Id, c.Name) |> Map.ofList

                                let convertedSpend =
                                    spendDetail
                                    |> List.map (fun (catId: Guid, money: Money) ->
                                        if money.CurrencyCode = targetCurrency then
                                            (catId, money.Amount)
                                        else
                                            let rate = pricing.GetSpotAsync(money.CurrencyCode, targetCurrency).GetAwaiter().GetResult()
                                            (catId, money.Amount * rate.Value))

                                let spendByCategory =
                                    convertedSpend
                                    |> List.groupBy fst
                                    |> List.map (fun (catId, items) -> catId, items |> List.sumBy snd)
                                    |> Map.ofList

                                let reportItems =
                                    allocs
                                    |> List.map (fun alloc ->
                                        let spentSigned = spendByCategory |> Map.tryFind alloc.CategoryId |> Option.defaultValue 0m
                                        let allocated = alloc.AllocatedAmount.Amount
                                        let spentDisplay = -spentSigned
                                        let remaining = allocated + spentSigned
                                        let rollover = alloc.RolloverBalance.Amount
                                        let percentUsed =
                                            if allocated <> 0m then Decimal.Round(Math.Min(100m, Math.Max(0m, -spentSigned / allocated * 100m)), 2)
                                            else 0m
                                        {|
                                            categoryId = alloc.CategoryId
                                            name = categoryNames |> Map.tryFind alloc.CategoryId |> Option.defaultValue "Unknown"
                                            allocatedMinor = toMinor alloc.AllocatedAmount
                                            spentMinor = toMinor { Amount = spentDisplay; CurrencyCode = targetCurrency }
                                            remainingMinor = toMinor { Amount = remaining; CurrencyCode = targetCurrency }
                                            rolloverBalanceMinor = toMinor { Amount = rollover; CurrencyCode = targetCurrency }
                                            percentUsed = percentUsed
                                            currency = targetCurrency
                                        |})

                                let totalAllocated = reportItems |> List.sumBy (fun i -> i.allocatedMinor)
                                let totalSpent = reportItems |> List.sumBy (fun i -> i.spentMinor)
                                let totalRemaining = reportItems |> List.sumBy (fun i -> i.remainingMinor)

                                let resp = {|
                                    periodId = periodId
                                    totals = {|
                                        allocatedMinor = totalAllocated
                                        spentMinor = totalSpent
                                        remainingMinor = totalRemaining
                                        currency = targetCurrency
                                    |}
                                    byCategory = reportItems
                                    displayCurrency = targetCurrency
                                |}
                                [ { uri = uri; mimeType = Some "application/json"; text = Some(JsonSerializer.Serialize(resp, McpProtocol.jsonOptions)); blob = None } ]


                    | ["categories"] ->
                        let categoryRepo = CategoryRepository.create factory accessor
                        let categories = categoryRepo.ListAsync().GetAwaiter().GetResult()
                        let treeRequested = query.TryFind "tree" |> Option.map (fun v -> v.ToLowerInvariant() = "true") |> Option.defaultValue false
                        let resp : obj =
                            if treeRequested then
                                {| categories = buildCategoryTree categories |} :> obj
                            else
                                {| categories = categories |> List.map categoryToResponse |} :> obj
                        [ { uri = uri; mimeType = Some "application/json"; text = Some(JsonSerializer.Serialize(resp, McpProtocol.jsonOptions)); blob = None } ]

                    | _ ->
                        [ { uri = uri; mimeType = Some "text/plain"; text = Some "Resource not found"; blob = None } ]

        makeResult id {| contents = contents |}

    let mcpHandler : HttpHandler = fun ctx ->
        task {
            // Require authentication (JWT or API key)
            let accessor = ctx.RequestServices.GetRequiredService<ITenantContextAccessor>()
            match accessor.Context with
            | None ->
                ctx.Response.StatusCode <- 401
                do! Response.ofJson {| error = "Unauthorized" |} ctx
            | Some _ ->
                let! body = readBody ctx
                match tryReadMessage body with
                | None ->
                    ctx.Response.StatusCode <- 400
                    do! Response.ofJson {| error = "Invalid JSON-RPC message" |} ctx
                | Some msg ->
                    match msg.Id, msg.Method with
                    | Some id, Some method ->
                        let responseDoc =
                            match method with
                            | "initialize" -> handleInitialize id
                            | "tools/list" -> handleToolsList id
                            | "tools/call" ->
                                match msg.Params with
                                | Some p -> handleToolsCall ctx id p
                                | None -> makeError id (-32602) "Invalid params"
                            | "resources/list" -> handleResourcesList id
                            | "resources/read" ->
                                match msg.Params with
                                | Some p -> handleResourcesRead ctx id p
                                | None -> makeError id (-32602) "Invalid params"
                            | _ -> makeError id (-32601) $"Method not found: {method}"
                        ctx.Response.ContentType <- "application/json"
                        let json = responseDoc.RootElement.GetRawText()
                        do! Response.ofPlainText json ctx
                    | _ ->
                        // Notification — no response required
                        ctx.Response.StatusCode <- 202
                        do! Response.ofEmpty ctx
        }
