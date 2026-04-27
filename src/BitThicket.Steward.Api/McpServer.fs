namespace BitThicket.Steward.Api

open System
open System.IO
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Falco
open BitThicket.Steward.Api.Domain

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

    let private jsonOptions = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)

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
        description = "List all accounts for the current tenant."
        inputSchema = {
            ``type`` = "object"
            properties = Map []
            required = []
        }
    }

    let allTools = [ echoTool; listAccountsTool ]

    let callEcho (args: JsonElement) : ToolCallResult =
        let message =
            match args.TryGetProperty("message") with
            | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
            | _ -> ""
        {
            content = [ {| ``type`` = "text"; text = message |} :> obj ]
            isError = false
        }

    let callListAccounts (ctx: HttpContext) : ToolCallResult =
        let factory = ctx.RequestServices.GetRequiredService<IDbConnectionFactory>()
        let accessor = ctx.RequestServices.GetRequiredService<ITenantContextAccessor>()
        match accessor.Context with
        | None ->
            { content = [ {| ``type`` = "text"; text = "Unauthorized: no tenant context" |} :> obj ]; isError = true }
        | Some tc ->
            // For scaffold, return a placeholder. Real implementation queries AccountRepository.
            let repo = AccountRepository.create factory accessor
            let accounts =
                repo.ListAsync().GetAwaiter().GetResult()
                |> List.map (fun a -> {| name = a.Name; accountType = string a.AccountType; currency = a.CurrencyCode |})
            {
                content = [ {| ``type`` = "text"; text = JsonSerializer.Serialize(accounts) |} :> obj ]
                isError = false
            }

// ── MCP Resource Registry ─────────────────────────────────────────────────────

module McpResources =

    open McpProtocol

    let allResources : Resource list = [
        { uri = "steward://accounts"; name = "Accounts"; mimeType = Some "application/json"; description = Some "List of financial accounts" }
        { uri = "steward://transactions"; name = "Transactions"; mimeType = Some "application/json"; description = Some "Recent transactions" }
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
            | "list_accounts" -> McpTools.callListAccounts ctx
            | _ -> { content = [ {| ``type`` = "text"; text = $"Unknown tool: {name}" |} :> obj ]; isError = true }
        makeResult id result

    let private handleResourcesList (id: JsonElement) : JsonDocument =
        makeResult id {| resources = McpResources.allResources |}

    let private handleResourcesRead (id: JsonElement) (paramsEl: JsonElement) : JsonDocument =
        let uri =
            match paramsEl.TryGetProperty("uri") with
            | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
            | _ -> ""
        let contents : ResourceContent list =
            match uri with
            | "steward://accounts" ->
                [ { uri = uri; mimeType = Some "application/json"; text = Some "[]"; blob = None } ]
            | "steward://transactions" ->
                [ { uri = uri; mimeType = Some "application/json"; text = Some "[]"; blob = None } ]
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
                                | Some p -> handleResourcesRead id p
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
