namespace BitThicket.Steward.Api

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Konscious.Security.Cryptography
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Falco
open Npgsql
open BitThicket.Steward.Api.Domain

// ── Password hashing ─────────────────────────────────────────────────────────

module PasswordHash =
    /// Argon2id parameters chosen for a ~100 ms hash time on modern hardware
    /// with moderate memory usage. See ADR-014 for the rationale.
    let private parallelism = 4
    let private memorySize = 65536   // 64 MB
    let private iterations = 3
    let private hashLength = 32
    let private saltLength = 16

    let private formatHash (salt: byte[]) (hash: byte[]) =
        let saltB64 = Convert.ToBase64String(salt)
        let hashB64 = Convert.ToBase64String(hash)
        $"$argon2id$v=19$m={memorySize},t={iterations},p={parallelism}${saltB64}${hashB64}"

    let hash (password: string) =
        let salt = RandomNumberGenerator.GetBytes(saltLength)
        use argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
        argon2.Salt <- salt
        argon2.DegreeOfParallelism <- parallelism
        argon2.MemorySize <- memorySize
        argon2.Iterations <- iterations
        let hash = argon2.GetBytes(hashLength)
        formatHash salt hash

    let verify (password: string) (hashStr: string) =
        let parts = hashStr.Split('$')
        if parts.Length <> 6 then false
        else
            let salt = Convert.FromBase64String(parts.[4])
            let expectedHash = Convert.FromBase64String(parts.[5])
            use argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
            argon2.Salt <- salt
            argon2.DegreeOfParallelism <- parallelism
            argon2.MemorySize <- memorySize
            argon2.Iterations <- iterations
            let computedHash = argon2.GetBytes(hashLength)
            CryptographicOperations.FixedTimeEquals(expectedHash, computedHash)

// ── JWT ──────────────────────────────────────────────────────────────────────

module Jwt =
    let private base64UrlEncode (bytes: byte[]) =
        Convert.ToBase64String(bytes)
            .Replace("+", "-")
            .Replace("/", "_")
            .TrimEnd('=')

    let private base64UrlDecode (str: string) =
        let padding = 4 - (str.Length % 4)
        let padded = if padding = 4 then str else str + String('=', padding)
        let normal = padded.Replace("-", "+").Replace("_", "/")
        Convert.FromBase64String(normal)

    let private escapeJsonString (s: string) =
        s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r")

    type ValidationResult =
        | Valid of JsonDocument
        | InvalidSignature
        | Expired
        | InvalidIssuer
        | InvalidAudience
        | Malformed

    let createToken (secret: string) (issuer: string) (audience: string) (claims: (string * string) list) (expiry: TimeSpan) =
        let header = """{"alg":"HS256","typ":"JWT"}"""
        let now = DateTimeOffset.UtcNow
        let exp = now.Add(expiry).ToUnixTimeSeconds()
        let iat = now.ToUnixTimeSeconds()
        let claimJson =
            claims
            |> List.map (fun (k, v) -> $"\"{k}\":\"{escapeJsonString v}\"")
            |> String.concat ","
        let payload =
            if claimJson = "" then
                $"{{\"iss\":\"{escapeJsonString issuer}\",\"aud\":\"{escapeJsonString audience}\",\"iat\":{iat},\"exp\":{exp}}}"
            else
                $"{{\"iss\":\"{escapeJsonString issuer}\",\"aud\":\"{escapeJsonString audience}\",\"iat\":{iat},\"exp\":{exp},{claimJson}}}"
        let headerB64 = base64UrlEncode (Encoding.UTF8.GetBytes(header))
        let payloadB64 = base64UrlEncode (Encoding.UTF8.GetBytes(payload))
        let signingInput = $"{headerB64}.{payloadB64}"
        use hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret))
        let signature = hmac.ComputeHash(Encoding.UTF8.GetBytes(signingInput))
        $"{signingInput}.{base64UrlEncode(signature)}"

    let private verifySignature (secret: string) (token: string) : byte[] option =
        let parts = token.Split('.')
        if parts.Length <> 3 then None
        else
            let signingInput = $"{parts.[0]}.{parts.[1]}"
            use hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret))
            let signature = hmac.ComputeHash(Encoding.UTF8.GetBytes(signingInput))
            let expectedSig = base64UrlEncode(signature)
            if expectedSig = parts.[2] then
                try
                    Some(base64UrlDecode(parts.[1]))
                with _ -> None
            else
                None

    let tryReadToken (secret: string) (previousSecret: string option) (issuer: string) (audience: string) (token: string) : ValidationResult =
        let payloadOpt =
            match verifySignature secret token with
            | Some payload -> Some payload
            | None ->
                match previousSecret with
                | Some prev -> verifySignature prev token
                | None -> None
        match payloadOpt with
        | None -> InvalidSignature
        | Some payloadBytes ->
            try
                let doc = JsonDocument.Parse(payloadBytes)
                let root = doc.RootElement
                let now = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                // Validate expiration
                match root.TryGetProperty("exp") with
                | true, expEl when expEl.ValueKind = JsonValueKind.Number ->
                    let exp = expEl.GetInt64()
                    if exp <= now then Expired
                    else
                        // Validate issuer
                        match root.TryGetProperty("iss") with
                        | true, issEl when issEl.ValueKind = JsonValueKind.String ->
                            if issEl.GetString() <> issuer then InvalidIssuer
                            else
                                // Validate audience
                                match root.TryGetProperty("aud") with
                                | true, audEl when audEl.ValueKind = JsonValueKind.String ->
                                    if audEl.GetString() <> audience then InvalidAudience
                                    else Valid doc
                                | _ -> InvalidAudience
                        | _ -> InvalidIssuer
                | _ -> Malformed
            with _ -> Malformed

// ── Configuration ────────────────────────────────────────────────────────────

type AuthConfig = {
    JwtSecret: string
    JwtSecretPrevious: string option
    Issuer: string
    Audience: string
}

module AuthServices =
    let register (services: IServiceCollection) (config: AuthConfig) =
        services.AddSingleton<AuthConfig>(config) |> ignore
        services

// ── Auth helpers ─────────────────────────────────────────────────────────────

module AuthHelpers =
    /// Wrap a handler so that it returns 401 when no authenticated tenant context
    /// is present. Downstream handlers can safely assume a valid TenantContext.
    let requireAuth (handler: HttpHandler) : HttpHandler = fun ctx ->
        let accessor = ctx.RequestServices.GetRequiredService<ITenantContextAccessor>()
        match accessor.Context with
        | Some _ -> handler ctx
        | None ->
            ctx.Response.StatusCode <- 401
            Response.ofJson {| error = "Unauthorized" |} ctx

    /// Wrap a handler so that it returns 401 when unauthenticated and 403 when
    /// the authenticated user's role does not match the required role.
    let requireRole (requiredRole: string) (handler: HttpHandler) : HttpHandler = fun ctx ->
        let accessor = ctx.RequestServices.GetRequiredService<ITenantContextAccessor>()
        match accessor.Context with
        | None ->
            ctx.Response.StatusCode <- 401
            Response.ofJson {| error = "Unauthorized" |} ctx
        | Some _ ->
            match ctx.Items.TryGetValue("TenantRole") with
            | true, (:? string as role) when role = requiredRole -> handler ctx
            | _ ->
                ctx.Response.StatusCode <- 403
                Response.ofJson {| error = "Forbidden" |} ctx

    /// Wrap a handler so that it returns 401 when unauthenticated and 403 when
    /// the authenticated API key does not have the required scope.
    /// JWT-authenticated requests are allowed through (scopes are API-key only for now).
    let requireScope (requiredScope: string) (handler: HttpHandler) : HttpHandler = fun ctx ->
        let accessor = ctx.RequestServices.GetRequiredService<ITenantContextAccessor>()
        match accessor.Context with
        | None ->
            ctx.Response.StatusCode <- 401
            Response.ofJson {| error = "Unauthorized" |} ctx
        | Some _ ->
            // If authenticated via API key, check scopes
            match ctx.Items.TryGetValue("ApiKeyScopes") with
            | true, value ->
                match Microsoft.FSharp.Core.Operators.tryUnbox<string list> value with
                | Some scopes when scopes |> List.contains requiredScope -> handler ctx
                | _ ->
                    ctx.Response.StatusCode <- 403
                    Response.ofJson {| error = "Forbidden: missing required scope" |} ctx
            // JWT auth has no scopes yet — allow through if endpoint is otherwise protected
            | false, _ -> handler ctx

// ── TenantContextMiddleware ──────────────────────────────────────────────────

/// ASP.NET Core middleware that extracts the Bearer token from the
/// Authorization header, validates the JWT signature/lifetime/issuer/audience,
/// and stores a TenantContext value in HttpContext.Items for the scoped
/// ITenantContextAccessor to consume.
type TenantContextMiddleware(next: RequestDelegate, authConfig: AuthConfig, dbFactory: IDbConnectionFactory) =
    member _.InvokeAsync(ctx: HttpContext) =
        task {
            let tryParseGuid (str: string) =
                match Guid.TryParse(str) with
                | true, g -> Some g
                | _ -> None

            let tryGetStringClaim (doc: JsonDocument) (name: string) =
                match doc.RootElement.TryGetProperty(name) with
                | true, el when el.ValueKind = JsonValueKind.String -> Some(el.GetString())
                | _ -> None

            // Try Bearer token first
            match ctx.Request.Headers.TryGetValue("Authorization") with
            | true, values when values.Count > 0 ->
                let header = values.[0]
                if header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) then
                    let token = header.Substring(7)
                    match Jwt.tryReadToken authConfig.JwtSecret authConfig.JwtSecretPrevious authConfig.Issuer authConfig.Audience token with
                    | Jwt.ValidationResult.Valid jwtDoc ->
                        match tryGetStringClaim jwtDoc "sub", tryGetStringClaim jwtDoc "tid" with
                        | Some subStr, Some tidStr ->
                            match tryParseGuid subStr, tryParseGuid tidStr with
                            | Some userId, Some tenantId ->
                                let role = tryGetStringClaim jwtDoc "mr" |> Option.defaultValue ""
                                ctx.Items["TenantContext"] <- { TenantId = tenantId; UserId = userId }
                                ctx.Items["TenantRole"] <- role
                            | _ -> ()
                        | _ -> ()
                    | _ -> ()
            | _ -> ()

            // Fall back to API key auth if no tenant context was set
            if not (ctx.Items.ContainsKey("TenantContext")) then
                match ctx.Request.Headers.TryGetValue("x-api-key") with
                | true, apiKeyValues when apiKeyValues.Count > 0 ->
                    let apiKey = apiKeyValues.[0]
                    let! result = ApiKeyRepository.tryFindByKeyAsync dbFactory apiKey
                    match result with
                    | Some (keyRecord, tc) ->
                        ctx.Items["TenantContext"] <- tc
                        ctx.Items["TenantRole"] <- keyRecord.Role
                        ctx.Items["ApiKeyId"] <- keyRecord.Id
                        ctx.Items["ApiKeyScopes"] <- keyRecord.Scopes
                        // Fire-and-forget update last_used_at
                        do! ApiKeyRepository.updateLastUsedAsync dbFactory tc keyRecord.Id
                    | None -> ()
                | _ -> ()

            return! next.Invoke(ctx)
        }

// ── Request / response helpers ───────────────────────────────────────────────

module private AuthJson =
    let readBody (ctx: HttpContext) =
        task {
            use reader = new StreamReader(ctx.Request.Body, Encoding.UTF8)
            let! json = reader.ReadToEndAsync()
            return JsonDocument.Parse(json)
        }

// ── Handlers ─────────────────────────────────────────────────────────────────

module Auth =

    // POST /auth/register
    let registerHandler : HttpHandler = fun ctx ->
        task {
            let config = ctx.RequestServices.GetRequiredService<AuthConfig>()
            let factory = ctx.RequestServices.GetRequiredService<IDbConnectionFactory>()
            let! doc = AuthJson.readBody ctx

            let email = doc.RootElement.GetProperty("email").GetString()
            let password = doc.RootElement.GetProperty("password").GetString()
            let displayName =
                match doc.RootElement.TryGetProperty("displayName") with
                | true, el when el.ValueKind <> JsonValueKind.Null -> Some(el.GetString())
                | _ -> None
            let tenantDisplayName = doc.RootElement.GetProperty("tenantDisplayName").GetString()

            match! RootRepository.getUserByEmail factory email with
            | Some _ ->
                ctx.Response.StatusCode <- 409
                do! Response.ofJson {| error = "Email already registered" |} ctx
            | None ->
                let passwordHash = PasswordHash.hash password
                let! result = RootRepository.registerUserWithTenant factory email passwordHash displayName tenantDisplayName
                match result with
                | Error msg ->
                    ctx.Response.StatusCode <- 409
                    do! Response.ofJson {| error = msg |} ctx
                | Ok created ->
                    let token =
                        Jwt.createToken config.JwtSecret config.Issuer config.Audience [
                            "sub", created.UserId.ToString()
                            "tid", created.TenantId.ToString()
                            "tn", tenantDisplayName
                            "mr", "owner"
                        ] (TimeSpan.FromHours(1.0))
                    do! Response.ofJson {| userId = created.UserId; tenantId = created.TenantId; accessToken = token |} ctx
        }

    // POST /auth/login
    let loginHandler : HttpHandler = fun ctx ->
        task {
            let config = ctx.RequestServices.GetRequiredService<AuthConfig>()
            let factory = ctx.RequestServices.GetRequiredService<IDbConnectionFactory>()
            let! doc = AuthJson.readBody ctx

            let email = doc.RootElement.GetProperty("email").GetString()
            let password = doc.RootElement.GetProperty("password").GetString()
            let requestedTenantId =
                match doc.RootElement.TryGetProperty("tenantId") with
                | true, el when el.ValueKind = JsonValueKind.String ->
                    match Guid.TryParse(el.GetString()) with
                    | true, g -> Some g
                    | _ -> None
                | _ -> None

            match! RootRepository.getUserByEmail factory email with
            | None ->
                ctx.Response.StatusCode <- 401
                do! Response.ofJson {| error = "Invalid credentials" |} ctx
            | Some user ->
                if not (PasswordHash.verify password user.PasswordHash) then
                    ctx.Response.StatusCode <- 401
                    do! Response.ofJson {| error = "Invalid credentials" |} ctx
                else
                    let! memberships = RootRepository.listMembershipsByUser factory user.Id
                    let! enriched =
                        memberships
                        |> List.map (fun m -> task {
                            let! tenant = RootRepository.getTenantById factory m.TenantId
                            return {|
                                tenantId = m.TenantId
                                tenantDisplayName = tenant |> Option.map (fun t -> t.DisplayName) |> Option.defaultValue ""
                                role = m.Role
                            |}
                        })
                        |> Task.WhenAll

                    match requestedTenantId with
                    | None when enriched.Length > 1 ->
                        do! Response.ofJson {| memberships = enriched |> Array.toList |} ctx
                    | None ->
                        let m = enriched.[0]
                        let token =
                            Jwt.createToken config.JwtSecret config.Issuer config.Audience [
                                "sub", user.Id.ToString()
                                "tid", m.tenantId.ToString()
                                "tn", m.tenantDisplayName
                                "mr", m.role
                            ] (TimeSpan.FromHours(1.0))
                        do! Response.ofJson {| accessToken = token |} ctx
                    | Some tid ->
                        match enriched |> Array.tryFind (fun m -> m.tenantId = tid) with
                        | None ->
                            ctx.Response.StatusCode <- 401
                            do! Response.ofJson {| error = "Invalid tenant" |} ctx
                        | Some m ->
                            let token =
                                Jwt.createToken config.JwtSecret config.Issuer config.Audience [
                                    "sub", user.Id.ToString()
                                    "tid", m.tenantId.ToString()
                                    "tn", m.tenantDisplayName
                                    "mr", m.role
                                ] (TimeSpan.FromHours(1.0))
                            do! Response.ofJson {| accessToken = token |} ctx
        }

    // POST /api/api-keys
    let createApiKeyHandler : HttpHandler = fun ctx ->
        task {
            let factory = ctx.RequestServices.GetRequiredService<IDbConnectionFactory>()
            let! doc = AuthJson.readBody ctx
            let displayName =
                match doc.RootElement.TryGetProperty("displayName") with
                | true, el when el.ValueKind <> JsonValueKind.Null -> el.GetString()
                | _ -> "API Key"
            let accessor = ctx.RequestServices.GetRequiredService<ITenantContextAccessor>()
            match accessor.Context with
            | None ->
                ctx.Response.StatusCode <- 401
                do! Response.ofJson {| error = "Unauthorized" |} ctx
            | Some tc ->
                let fullKey, prefix, hash = ApiKeyRepository.generateKey()
                let apiKey: ApiKey = {
                    Id = Guid.NewGuid()
                    TenantId = tc.TenantId
                    UserId = tc.UserId
                    DisplayName = displayName
                    KeyHash = hash
                    KeyPrefix = prefix
                    Role =
                        match ctx.Items.TryGetValue("TenantRole") with
                        | true, (:? string as r) -> r
                        | _ -> "member"
                    Scopes = []
                    ExpiresAt = None
                    LastUsedAt = None
                    RevokedAt = None
                    CreatedAt = DateTimeOffset.UtcNow
                }
                let! _ = ApiKeyRepository.createAsync factory apiKey
                do! Response.ofJson {| id = apiKey.Id; displayName = apiKey.DisplayName; keyPrefix = apiKey.KeyPrefix; key = fullKey |} ctx
        }

    // GET /api/api-keys
    let listApiKeysHandler : HttpHandler = fun ctx ->
        task {
            let factory = ctx.RequestServices.GetRequiredService<IDbConnectionFactory>()
            let accessor = ctx.RequestServices.GetRequiredService<ITenantContextAccessor>()
            match accessor.Context with
            | None ->
                ctx.Response.StatusCode <- 401
                do! Response.ofJson {| error = "Unauthorized" |} ctx
            | Some tc ->
                let! keys = ApiKeyRepository.listByTenantAsync factory tc
                let masked =
                    keys
                    |> List.map (fun k -> {|
                        id = k.Id
                        displayName = k.DisplayName
                        keyPrefix = k.KeyPrefix
                        role = k.Role
                        scopes = k.Scopes
                        expiresAt = k.ExpiresAt
                        lastUsedAt = k.LastUsedAt
                        revokedAt = k.RevokedAt
                        createdAt = k.CreatedAt
                    |})
                do! Response.ofJson {| keys = masked |} ctx
        }

    // DELETE /api/api-keys/{keyId:guid}
    let revokeApiKeyHandler (keyId: Guid) : HttpHandler = fun ctx ->
        task {
            let factory = ctx.RequestServices.GetRequiredService<IDbConnectionFactory>()
            let accessor = ctx.RequestServices.GetRequiredService<ITenantContextAccessor>()
            match accessor.Context with
            | None ->
                ctx.Response.StatusCode <- 401
                do! Response.ofJson {| error = "Unauthorized" |} ctx
            | Some tc ->
                let! ok = ApiKeyRepository.revokeAsync factory tc keyId
                if ok then
                    ctx.Response.StatusCode <- 204
                    do! Response.ofEmpty ctx
                else
                    ctx.Response.StatusCode <- 404
                    do! Response.ofJson {| error = "API key not found or already revoked" |} ctx
        }

    // GET /me
    let meHandler : HttpHandler = fun ctx ->
        task {
            let accessor = ctx.RequestServices.GetRequiredService<ITenantContextAccessor>()
            let factory = ctx.RequestServices.GetRequiredService<IDbConnectionFactory>()
            match accessor.Context with
            | None ->
                ctx.Response.StatusCode <- 401
                do! Response.ofJson {| error = "Unauthorized" |} ctx
            | Some tc ->
                let! userOpt = RootRepository.getUserById factory tc.UserId
                let email, displayName =
                    match userOpt with
                    | Some u -> u.Email, (u.DisplayName |> Option.defaultValue u.Email)
                    | None -> "", ""
                let role =
                    match ctx.Items.TryGetValue("TenantRole") with
                    | true, (:? string as r) -> r
                    | _ -> ""
                let responseBody = {|
                    userId = tc.UserId
                    tenantId = tc.TenantId
                    role = role
                    email = email
                    displayName = displayName
                |}
                do! Response.ofJson responseBody ctx
        }
