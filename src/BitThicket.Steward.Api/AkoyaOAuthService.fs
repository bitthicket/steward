namespace BitThicket.Steward.Api

open System
open System.Net.Http
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Npgsql
open BitThicket.Steward.Api.Domain
open BitThicket.Steward.Api.Vault

// ─────────────────────────────────────────────────────────────────────────────
// Akoya OAuth configuration
// ─────────────────────────────────────────────────────────────────────────────

type AkoyaOAuthConfig = {
    ClientId: string
    ClientSecret: string
    IdPBaseUrl: string
    ApiBaseUrl: string
    RedirectUri: string
}

module AkoyaOAuthConfig =
    let fromEnvironment () : AkoyaOAuthConfig =
        let clientId =
            match Environment.GetEnvironmentVariable("AKOYA_CLIENT_ID") with
            | null | "" -> failwith "AKOYA_CLIENT_ID is not set"
            | v -> v
        let clientSecret =
            match Environment.GetEnvironmentVariable("AKOYA_CLIENT_SECRET") with
            | null | "" -> failwith "AKOYA_CLIENT_SECRET is not set"
            | v -> v
        let env =
            match Environment.GetEnvironmentVariable("AKOYA_ENV") with
            | null | "" -> "sandbox"
            | v -> v.ToLowerInvariant()
        let idpBaseUrl =
            match env with
            | "production" -> "https://idp.akoya.com"
            | _ -> "https://sandbox-idp.akoya.com"
        let apiBaseUrl =
            match env with
            | "production" -> "https://api.akoya.com"
            | _ -> "https://sandbox-api.akoya.com"
        let redirectUri =
            match Environment.GetEnvironmentVariable("AKOYA_REDIRECT_URI") with
            | null | "" -> failwith "AKOYA_REDIRECT_URI is not set"
            | v -> v
        { ClientId = clientId; ClientSecret = clientSecret; IdPBaseUrl = idpBaseUrl; ApiBaseUrl = apiBaseUrl; RedirectUri = redirectUri }

// ─────────────────────────────────────────────────────────────────────────────
// PKCE helpers
// ─────────────────────────────────────────────────────────────────────────────

module private Pkce =
    let randomString length =
        let bytes = RandomNumberGenerator.GetBytes(length)
        Convert.ToBase64String(bytes)
            .Replace("+", "-")
            .Replace("/", "_")
            .Replace("=", "")

    let generateVerifier () : string =
        randomString 32

    let challengeFromVerifier (verifier: string) : string =
        use sha = SHA256.Create()
        let hash = sha.ComputeHash(Encoding.ASCII.GetBytes(verifier))
        Convert.ToBase64String(hash)
            .Replace("+", "-")
            .Replace("/", "_")
            .Replace("=", "")

// ─────────────────────────────────────────────────────────────────────────────
// OAuth state persistence (short-lived PKCE state rows)
// ─────────────────────────────────────────────────────────────────────────────

type OAuthStateRow = {
    State: string
    CodeVerifier: string
    TenantId: Guid
    UserId: Guid
    RedirectUri: string
    InstitutionId: string
    CreatedAt: DateTime
    ExpiresAt: DateTime
}

module private OAuthStateRepository =
    let insert (conn: NpgsqlConnection) (row: OAuthStateRow) =
        task {
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                """INSERT INTO oauth_state (state, code_verifier, tenant_id, user_id, redirect_uri, institution_id, created_at, expires_at)
                   VALUES ($1, $2, $3, $4, $5, $6, $7, $8)"""
            cmd.Parameters.AddWithValue("$1", row.State) |> ignore
            cmd.Parameters.AddWithValue("$2", row.CodeVerifier) |> ignore
            cmd.Parameters.AddWithValue("$3", row.TenantId) |> ignore
            cmd.Parameters.AddWithValue("$4", row.UserId) |> ignore
            cmd.Parameters.AddWithValue("$5", row.RedirectUri) |> ignore
            cmd.Parameters.AddWithValue("$6", row.InstitutionId) |> ignore
            cmd.Parameters.AddWithValue("$7", row.CreatedAt) |> ignore
            cmd.Parameters.AddWithValue("$8", row.ExpiresAt) |> ignore
            let! _ = cmd.ExecuteNonQueryAsync()
            return ()
        }

    let getByState (conn: NpgsqlConnection) (state: string) =
        task {
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                "SELECT state, code_verifier, tenant_id, user_id, redirect_uri, institution_id, created_at, expires_at FROM oauth_state WHERE state = $1"
            cmd.Parameters.AddWithValue("$1", state) |> ignore
            let! reader = cmd.ExecuteReaderAsync()
            use reader = reader
            let! hasRow = reader.ReadAsync()
            if not hasRow then return None else
            return Some {
                State = reader.GetString(0)
                CodeVerifier = reader.GetString(1)
                TenantId = reader.GetGuid(2)
                UserId = reader.GetGuid(3)
                RedirectUri = reader.GetString(4)
                InstitutionId = reader.GetString(5)
                CreatedAt = reader.GetDateTime(6)
                ExpiresAt = reader.GetDateTime(7)
            }
        }

    let deleteByState (conn: NpgsqlConnection) (state: string) =
        task {
            use cmd = conn.CreateCommand()
            cmd.CommandText <- "DELETE FROM oauth_state WHERE state = $1"
            cmd.Parameters.AddWithValue("$1", state) |> ignore
            let! rows = cmd.ExecuteNonQueryAsync()
            return rows > 0
        }

    let deleteExpired (conn: NpgsqlConnection) =
        task {
            use cmd = conn.CreateCommand()
            cmd.CommandText <- "DELETE FROM oauth_state WHERE expires_at < now()"
            let! rows = cmd.ExecuteNonQueryAsync()
            return rows
        }

// ─────────────────────────────────────────────────────────────────────────────
// Token exchange response
// ─────────────────────────────────────────────────────────────────────────────

type AkoyaTokenResponse = {
    AccessToken: string
    RefreshToken: string
    ExpiresIn: int
    TokenType: string
}

module private AkoyaTokenResponse =
    let fromJson (doc: JsonDocument) : AkoyaTokenResponse =
        let root = doc.RootElement
        {
            AccessToken = root.GetProperty("access_token").GetString()
            RefreshToken =
                match root.TryGetProperty("refresh_token") with
                | true, p -> p.GetString()
                | _ -> ""
            ExpiresIn =
                match root.TryGetProperty("expires_in") with
                | true, p -> p.GetInt32()
                | _ -> 0
            TokenType =
                match root.TryGetProperty("token_type") with
                | true, p -> p.GetString()
                | _ -> "Bearer"
        }

// ─────────────────────────────────────────────────────────────────────────────
// FDX account shape
// ─────────────────────────────────────────────────────────────────────────────

type AkoyaAccount = {
    AccountId: string
    AccountType: string
    DisplayName: string
    Currency: string
}

module private AkoyaAccount =
    let fromJson (el: JsonElement) : AkoyaAccount option =
        try
            Some {
                AccountId = el.GetProperty("accountId").GetString()
                AccountType =
                    match el.TryGetProperty("accountType") with
                    | true, p -> p.GetString()
                    | _ -> "UNKNOWN"
                DisplayName =
                    match el.TryGetProperty("nickname") with
                    | true, p -> p.GetString()
                    | _ ->
                        match el.TryGetProperty("accountType") with
                        | true, p -> p.GetString()
                        | _ -> "Account"
                Currency =
                    match el.TryGetProperty("currency") with
                    | true, p -> p.GetString()
                    | _ -> "USD"
            }
        with _ -> None

// ─────────────────────────────────────────────────────────────────────────────
// Service interface
// ─────────────────────────────────────────────────────────────────────────────

type IAkoyaOAuthService =
    /// Generate an Akoya authorize URL and stash the PKCE verifier server-side.
    /// Returns the URL the frontend should redirect the user to.
    abstract BuildAuthorizeUrlAsync : TenantContext * institutionId:string * redirectUri:string -> Task<string>
    /// Complete the OAuth callback: exchange code for tokens, fetch accounts,
    /// store credentials in vault, create connection + accounts. Returns the
    /// connection id and created account ids.
    abstract HandleCallbackAsync : state:string * code:string -> Task<Guid * Guid list>
    /// Refresh an access token for an existing connection. Updates the vault entry.
    /// Returns the new access token.
    abstract RefreshTokenAsync : TenantContext * connectionId:Guid -> Task<string>
    /// Generate a re-auth authorize URL for an existing Akoya connection.
    abstract BuildReauthUrlAsync : TenantContext * connectionId:Guid -> Task<string>
    /// Delete expired oauth_state rows. Returns count deleted.
    abstract GCExpiredStateAsync : unit -> Task<int>

// ─────────────────────────────────────────────────────────────────────────────
// Manual tenant context accessor (for use outside HTTP request scope)
// ─────────────────────────────────────────────────────────────────────────────

module private AkoyaOAuthHelpers =
    let makeAccessor (ctx: TenantContext) : ITenantContextAccessor =
        { new ITenantContextAccessor with
            member _.Context = Some ctx }

// ─────────────────────────────────────────────────────────────────────────────
// Service implementation
// ─────────────────────────────────────────────────────────────────────────────

type AkoyaOAuthService(
    config: AkoyaOAuthConfig,
    http: HttpClient,
    factory: IDbConnectionFactory,
    vault: IVaultService,
    log: ILogger<AkoyaOAuthService>) =

    let makeAuthorizeUrl (state: string) (challenge: string) (institutionId: string) (redirectUri: string) =
        let b = StringBuilder()
        b.Append(config.IdPBaseUrl.TrimEnd('/')) |> ignore
        b.Append("/auth?") |> ignore
        b.Append($"client_id={Uri.EscapeDataString(config.ClientId)}") |> ignore
        b.Append("&response_type=code") |> ignore
        b.Append("&scope=openid accounts") |> ignore
        b.Append($"&redirect_uri={Uri.EscapeDataString(redirectUri)}") |> ignore
        b.Append($"&state={Uri.EscapeDataString(state)}") |> ignore
        b.Append($"&code_challenge={Uri.EscapeDataString(challenge)}") |> ignore
        b.Append("&code_challenge_method=S256") |> ignore
        b.Append($"&institution_id={Uri.EscapeDataString(institutionId)}") |> ignore
        b.ToString()

    let exchangeCodeAsync (code: string) (verifier: string) (redirectUri: string) =
        task {
            let body =
                $"grant_type=authorization_code&code={Uri.EscapeDataString(code)}&redirect_uri={Uri.EscapeDataString(redirectUri)}&client_id={Uri.EscapeDataString(config.ClientId)}&client_secret={Uri.EscapeDataString(config.ClientSecret)}&code_verifier={Uri.EscapeDataString(verifier)}"
            use content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded")
            let! resp = http.PostAsync($"{config.IdPBaseUrl.TrimEnd('/')}/token", content)
            let! respBody = resp.Content.ReadAsStringAsync()
            if not resp.IsSuccessStatusCode then
                log.LogError("Akoya token exchange failed: {Status} {Body}", int resp.StatusCode, respBody)
                failwith $"Akoya token exchange failed: {int resp.StatusCode}"
            use doc = JsonDocument.Parse(respBody)
            return AkoyaTokenResponse.fromJson doc
        }

    let refreshTokenAsync (refreshToken: string) =
        task {
            let body =
                $"grant_type=refresh_token&refresh_token={Uri.EscapeDataString(refreshToken)}&client_id={Uri.EscapeDataString(config.ClientId)}&client_secret={Uri.EscapeDataString(config.ClientSecret)}"
            use content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded")
            let! resp = http.PostAsync($"{config.IdPBaseUrl.TrimEnd('/')}/token", content)
            let! respBody = resp.Content.ReadAsStringAsync()
            if not resp.IsSuccessStatusCode then
                log.LogError("Akoya refresh token failed: {Status} {Body}", int resp.StatusCode, respBody)
                failwith $"Akoya refresh token failed: {int resp.StatusCode}"
            use doc = JsonDocument.Parse(respBody)
            return AkoyaTokenResponse.fromJson doc
        }

    let fetchAccountsAsync (accessToken: string) =
        task {
            let req = new HttpRequestMessage(HttpMethod.Get, $"{config.ApiBaseUrl.TrimEnd('/')}/fdx/v5/accounts")
            req.Headers.Authorization <- System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken)
            let! resp = http.SendAsync(req)
            let! respBody = resp.Content.ReadAsStringAsync()
            if not resp.IsSuccessStatusCode then
                log.LogError("Akoya fetch accounts failed: {Status} {Body}", int resp.StatusCode, respBody)
                failwith $"Akoya fetch accounts failed: {int resp.StatusCode}"
            use doc = JsonDocument.Parse(respBody)
            let accountsEl =
                if doc.RootElement.TryGetProperty("accounts") |> fst then
                    doc.RootElement.GetProperty("accounts")
                else
                    doc.RootElement
            let accounts =
                accountsEl.EnumerateArray()
                |> Seq.choose AkoyaAccount.fromJson
                |> Seq.toList
            return accounts
        }

    let fetchCustomerIdAsync (accessToken: string) =
        task {
            let req = new HttpRequestMessage(HttpMethod.Get, $"{config.ApiBaseUrl.TrimEnd('/')}/fdx/v5/customer")
            req.Headers.Authorization <- System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken)
            let! resp = http.SendAsync(req)
            let! respBody = resp.Content.ReadAsStringAsync()
            if not resp.IsSuccessStatusCode then
                log.LogWarning("Akoya fetch customer failed: {Status} {Body}", int resp.StatusCode, respBody)
                return None
            else
                use doc = JsonDocument.Parse(respBody)
                match doc.RootElement.TryGetProperty("customerId") with
                | true, p -> return Some(p.GetString())
                | _ -> return None
        }

    let akoyaAccountTypeToDomain (akoyaType: string) : AccountType =
        match akoyaType.ToUpperInvariant() with
        | "CHECKING" -> AccountType.Checking
        | "SAVINGS" -> AccountType.Savings
        | "CREDITCARD" | "CREDIT_CARD" -> AccountType.CreditCard
        | "INVESTMENT" -> AccountType.Investment
        | "LOAN" | "MORTGAGE" | "AUTO" -> AccountType.Loan
        | _ -> AccountType.Cash

    interface IAkoyaOAuthService with
        member _.BuildAuthorizeUrlAsync(ctx, institutionId, redirectUri) =
            task {
                let! conn = factory.OpenForTenantAsync(ctx)
                use _ = conn

                let state = Guid.NewGuid().ToString("N")
                let verifier = Pkce.generateVerifier ()
                let challenge = Pkce.challengeFromVerifier verifier

                let row: OAuthStateRow = {
                    State = state
                    CodeVerifier = verifier
                    TenantId = ctx.TenantId
                    UserId = ctx.UserId
                    RedirectUri = redirectUri
                    InstitutionId = institutionId
                    CreatedAt = DateTime.UtcNow
                    ExpiresAt = DateTime.UtcNow.AddMinutes(10.0)
                }

                do! OAuthStateRepository.insert conn row
                let url = makeAuthorizeUrl state challenge institutionId redirectUri
                return url
            }

        member _.HandleCallbackAsync(state, code) =
            task {
                // Look up the state row. We use an unscoped connection here because
                // the callback arrives without an auth context (user is mid-OAuth flow).
                let! conn = factory.OpenAsync()
                use conn = conn
                let! rowOpt = OAuthStateRepository.getByState conn state
                match rowOpt with
                | None ->
                    return failwith "Invalid or expired OAuth state"
                | Some row when row.ExpiresAt < DateTime.UtcNow ->
                    let! _ = OAuthStateRepository.deleteByState conn state
                    return failwith "OAuth state expired"
                | Some row ->
                    let! tokenResponse = exchangeCodeAsync code row.CodeVerifier row.RedirectUri

                    let expiresAt =
                        if tokenResponse.ExpiresIn > 0 then
                            Some (DateTimeOffset.UtcNow.AddSeconds(float tokenResponse.ExpiresIn))
                        else
                            None

                    let envelope: CredentialEnvelope = {
                        AccessToken = tokenResponse.AccessToken
                        RefreshToken = (if String.IsNullOrEmpty(tokenResponse.RefreshToken) then None else Some tokenResponse.RefreshToken)
                        ExpiresAt = expiresAt
                        ProviderSpecific = None
                    }

                    let ctx = { TenantId = row.TenantId; UserId = row.UserId }
                    let! credentialRef = vault.StoreAsync(ctx, envelope)

                    // Fetch accounts from Akoya
                    let! accounts = fetchAccountsAsync tokenResponse.AccessToken
                    let! customerIdOpt = fetchCustomerIdAsync tokenResponse.AccessToken
                    let customerId = customerIdOpt |> Option.defaultValue ""

                    let now = DateTimeOffset.UtcNow
                    let connectionId = Guid.NewGuid()

                    let connection: DataFeedConnection = {
                        Id = connectionId
                        TenantId = row.TenantId
                        UserId = row.UserId
                        Metadata = ProviderMetadata.Akoya(customerId, row.InstitutionId)
                        CredentialRef = credentialRef
                        Status = ConnectionStatus.Active
                        LinkedAccountIds = []
                        CreatedAt = now
                        UpdatedAt = now
                        LastSyncedAt = None
                    }

                    // Create manual accessor and repositories for callback (no HTTP context)
                    let accessor = AkoyaOAuthHelpers.makeAccessor ctx
                    let manualConnRepo = DataFeedConnectionRepository.create factory accessor
                    let manualAccountRepo = AccountRepository.create factory accessor

                    let! _ = manualConnRepo.CreateAsync(connection)

                    // Create Steward accounts
                    let mutable createdIds = ResizeArray<Guid>()
                    for acct in accounts do
                        let accountType = akoyaAccountTypeToDomain acct.AccountType
                        let account: Account = {
                            Id = Guid.NewGuid()
                            TenantId = row.TenantId
                            UserId = row.UserId
                            Name = acct.DisplayName
                            AccountType = accountType
                            CurrencyCode = acct.Currency.ToUpperInvariant()
                            InstitutionName = None
                            ExternalId = Some acct.AccountId
                            CreditCardInfo = None
                            IsOnBudget = AccountRepository.defaultIsOnBudget accountType
                            IsActive = true
                            DeletedAt = None
                            CreatedAt = now
                            UpdatedAt = now
                        }
                        let! accountId = manualAccountRepo.CreateAsync(account)
                        createdIds.Add(accountId)

                    // Update connection with linked accounts
                    let updatedConn = { connection with LinkedAccountIds = createdIds |> Seq.toList; UpdatedAt = DateTimeOffset.UtcNow }
                    do! manualConnRepo.UpdateAsync(updatedConn)

                    // Clean up state row
                    let! _ = OAuthStateRepository.deleteByState conn state

                    return (connectionId, createdIds |> Seq.toList)
            }

        member _.RefreshTokenAsync(ctx, connectionId) =
            task {
                let accessor = AkoyaOAuthHelpers.makeAccessor ctx
                let manualConnRepo = DataFeedConnectionRepository.create factory accessor

                let! connOpt = manualConnRepo.GetAsync(connectionId)
                match connOpt with
                | None -> return failwith $"Connection not found: {connectionId}"
                | Some connection ->
                    match DataFeedConnection.providerOf connection.Metadata with
                    | DataFeedProvider.Akoya ->
                        let! envelope = vault.LoadAsync(ctx, connection.CredentialRef)
                        match envelope.RefreshToken with
                        | None -> return failwith "No refresh token available for this connection"
                        | Some refreshToken ->
                            let! tokenResponse = refreshTokenAsync refreshToken
                            let newExpiresAt =
                                if tokenResponse.ExpiresIn > 0 then
                                    Some (DateTimeOffset.UtcNow.AddSeconds(float tokenResponse.ExpiresIn))
                                else
                                    None
                            let newEnvelope: CredentialEnvelope = {
                                AccessToken = tokenResponse.AccessToken
                                RefreshToken = (if String.IsNullOrEmpty(tokenResponse.RefreshToken) then Some refreshToken else Some tokenResponse.RefreshToken)
                                ExpiresAt = newExpiresAt
                                ProviderSpecific = None
                            }
                            let! newRef = vault.StoreAsync(ctx, newEnvelope)
                            let! _ = vault.DeleteAsync(ctx, connection.CredentialRef)
                            let updatedConn = { connection with CredentialRef = newRef; UpdatedAt = DateTimeOffset.UtcNow }
                            do! manualConnRepo.UpdateAsync(updatedConn)
                            return tokenResponse.AccessToken
                    | _ ->
                        return failwith "Connection is not an Akoya connection"
            }

        member _.BuildReauthUrlAsync(ctx, connectionId) =
            task {
                let accessor = AkoyaOAuthHelpers.makeAccessor ctx
                let manualConnRepo = DataFeedConnectionRepository.create factory accessor

                let! connOpt = manualConnRepo.GetAsync(connectionId)
                match connOpt with
                | None -> return failwith $"Connection not found: {connectionId}"
                | Some connection when connection.TenantId <> ctx.TenantId ->
                    return failwith "Connection not found"
                | Some connection ->
                    match connection.Metadata with
                    | ProviderMetadata.Akoya(_, institutionId) ->
                        let state = Guid.NewGuid().ToString("N")
                        let verifier = Pkce.generateVerifier ()
                        let challenge = Pkce.challengeFromVerifier verifier

                        let! conn = factory.OpenForTenantAsync(ctx)
                        use conn = conn

                        let row: OAuthStateRow = {
                            State = state
                            CodeVerifier = verifier
                            TenantId = ctx.TenantId
                            UserId = ctx.UserId
                            RedirectUri = config.RedirectUri
                            InstitutionId = institutionId
                            CreatedAt = DateTime.UtcNow
                            ExpiresAt = DateTime.UtcNow.AddMinutes(10.0)
                        }

                        do! OAuthStateRepository.insert conn row
                        let url = makeAuthorizeUrl state challenge institutionId config.RedirectUri
                        return url
                    | _ ->
                        return failwith "Connection is not an Akoya connection"
            }

        member _.GCExpiredStateAsync() =
            task {
                use! conn = factory.OpenAsync()
                let! count = OAuthStateRepository.deleteExpired conn
                log.LogInformation("GC'd {Count} expired oauth_state rows", count)
                return count
            }
