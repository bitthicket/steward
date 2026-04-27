namespace BitThicket.Steward.Api

open System
open System.Collections.Concurrent
open System.Net.Http
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open BitThicket.Steward.Api.Domain
open BitThicket.Steward.Api.Vault

// ─────────────────────────────────────────────────────────────────────────────
// Plaid configuration
// ─────────────────────────────────────────────────────────────────────────────

type PlaidConfig = {
    ClientId: string
    Secret: string
    BaseUrl: string
}

module PlaidConfig =
    let fromEnvironment () : PlaidConfig =
        let clientId =
            match Environment.GetEnvironmentVariable("STEWARD_PLAID_CLIENT_ID") with
            | null | "" -> failwith "STEWARD_PLAID_CLIENT_ID is not set"
            | v -> v
        let secret =
            match Environment.GetEnvironmentVariable("STEWARD_PLAID_SECRET") with
            | null | "" -> failwith "STEWARD_PLAID_SECRET is not set"
            | v -> v
        let baseUrl =
            match Environment.GetEnvironmentVariable("STEWARD_PLAID_BASE_URL") with
            | null | "" -> "https://sandbox.plaid.com"
            | v -> v
        { ClientId = clientId; Secret = secret; BaseUrl = baseUrl }

// ─────────────────────────────────────────────────────────────────────────────
// Normalized transaction (internal representation after Plaid parsing)
// ─────────────────────────────────────────────────────────────────────────────

type private NormalizedTxn = {
    externalId: string
    accountId: string
    amount: decimal
    currency: string
    date: string
    authorizedDate: string option
    name: string
    merchantName: string option
}

// ─────────────────────────────────────────────────────────────────────────────
// Plaid API client helpers
// ─────────────────────────────────────────────────────────────────────────────

module private PlaidHttp =
    let postJson (http: HttpClient) (url: string) (json: string) =
        task {
            use content = new StringContent(json, Encoding.UTF8, "application/json")
            let! resp = http.PostAsync(url, content)
            let! body = resp.Content.ReadAsStringAsync()
            if not resp.IsSuccessStatusCode then
                failwith $"Plaid API error {(int)resp.StatusCode}: {body}"
            return JsonDocument.Parse(body)
        }

    let buildSyncRequest (config: PlaidConfig) (accessToken: string) (cursor: string option) =
        let obj = System.Text.Json.Nodes.JsonObject()
        obj["client_id"] <- System.Text.Json.Nodes.JsonValue.Create(config.ClientId)
        obj["secret"] <- System.Text.Json.Nodes.JsonValue.Create(config.Secret)
        obj["access_token"] <- System.Text.Json.Nodes.JsonValue.Create(accessToken)
        obj["count"] <- System.Text.Json.Nodes.JsonValue.Create(100)
        match cursor with
        | Some c -> obj["cursor"] <- System.Text.Json.Nodes.JsonValue.Create(c)
        | None -> ()
        obj.ToJsonString()

    let parseTransactions (root: JsonElement) =
        let parseTxn (el: JsonElement) : NormalizedTxn =
            let amount = el.GetProperty("amount").GetDecimal()
            let currency =
                match el.TryGetProperty("iso_currency_code") with
                | true, (c: JsonElement) when c.ValueKind <> JsonValueKind.Null -> c.GetString()
                | _ ->
                    match el.TryGetProperty("unofficial_currency_code") with
                    | true, (c: JsonElement) when c.ValueKind <> JsonValueKind.Null -> c.GetString()
                    | _ -> "USD"
            {
                externalId = el.GetProperty("transaction_id").GetString()
                accountId = el.GetProperty("account_id").GetString()
                amount = amount
                currency = currency
                date = el.GetProperty("date").GetString()
                authorizedDate =
                    match el.TryGetProperty("authorized_date") with
                    | true, (d: JsonElement) when d.ValueKind <> JsonValueKind.Null -> Some(d.GetString())
                    | _ -> None
                name = el.GetProperty("name").GetString()
                merchantName =
                    match el.TryGetProperty("merchant_name") with
                    | true, (m: JsonElement) when m.ValueKind <> JsonValueKind.Null -> Some(m.GetString())
                    | _ -> None
            }

        let readArray (name: string) =
            match root.TryGetProperty(name) with
            | true, (arr: JsonElement) -> arr.EnumerateArray() |> Seq.map parseTxn |> Seq.toList
            | _ -> []

        let removed =
            match root.TryGetProperty("removed") with
            | true, (arr: JsonElement) ->
                arr.EnumerateArray()
                |> Seq.map (fun (el: JsonElement) -> el.GetProperty("transaction_id").GetString())
                |> Seq.toList
            | _ -> []

        let nextCursor =
            match root.TryGetProperty("next_cursor") with
            | true, (c: JsonElement) when c.ValueKind <> JsonValueKind.Null -> Some(c.GetString())
            | _ -> None

        let hasMore =
            match root.TryGetProperty("has_more") with
            | true, (b: JsonElement) -> b.GetBoolean()
            | _ -> false

        (readArray "added", readArray "modified", removed, nextCursor, hasMore)

// ─────────────────────────────────────────────────────────────────────────────
// Webhook verification
// ─────────────────────────────────────────────────────────────────────────────

module private PlaidWebhookVerifier =
    open System.IdentityModel.Tokens.Jwt
    open Microsoft.IdentityModel.Tokens

    let private keyCache = ConcurrentDictionary<string, JsonWebKey>()

    let private fetchVerificationKey (http: HttpClient) (config: PlaidConfig) (keyId: string) =
        task {
            let url = $"{config.BaseUrl}/webhook_verification_key/get"
            let req = System.Text.Json.Nodes.JsonObject()
            req["client_id"] <- System.Text.Json.Nodes.JsonValue.Create(config.ClientId)
            req["secret"] <- System.Text.Json.Nodes.JsonValue.Create(config.Secret)
            req["key_id"] <- System.Text.Json.Nodes.JsonValue.Create(keyId)
            let! doc = PlaidHttp.postJson http url (req.ToJsonString())
            let keyJson = doc.RootElement.GetProperty("key").GetRawText()
            return JsonWebKey(keyJson)
        }

    let verify (http: HttpClient) (config: PlaidConfig) (bodyBytes: byte[]) (header: string) =
        task {
            try
                let jwt = JwtSecurityTokenHandler().ReadJwtToken(header)
                let kid = jwt.Header.Kid
                let! jwk =
                    match keyCache.TryGetValue(kid) with
                    | true, k -> Task.FromResult(k)
                    | false, _ ->
                        task {
                            let! k = fetchVerificationKey http config kid
                            keyCache[kid] <- k
                            return k
                        }

                let signingInput =
                    let parts = header.Split('.')
                    parts[0] + "." + parts[1]
                let signingInputBytes = Encoding.ASCII.GetBytes(signingInput)
                let signature = Base64UrlEncoder.DecodeBytes(jwt.RawSignature)

                let result =
                    let cryptoProvider = CryptoProviderFactory.Default.CreateForVerifying(jwk, jwt.Header.Alg)
                    try
                        let valid = cryptoProvider.Verify(signingInputBytes, signature)
                        if not valid then
                            false
                        else
                            let bodyHash = SHA256.HashData(bodyBytes)
                            let bodyHashB64 = Convert.ToBase64String(bodyHash)

                            match jwt.Payload.TryGetValue("request_body_sha256") with
                            | true, (:? string as claimHash) -> bodyHashB64 = claimHash
                            | _ -> false
                    finally
                        cryptoProvider.Dispose()

                return result
            with ex ->
                return false
        }

// ─────────────────────────────────────────────────────────────────────────────
// Plaid service interface
// ─────────────────────────────────────────────────────────────────────────────

type PlaidSyncResult = {
    Added: int
    Modified: int
    Removed: int
    NextCursor: string option
}

type IPlaidService =
    abstract SyncConnectionAsync : tenantId:Guid -> connectionId:Guid -> Task<PlaidSyncResult>
    abstract VerifyWebhookAsync : bodyBytes:byte[] -> verificationHeader:string -> Task<bool>

// ─────────────────────────────────────────────────────────────────────────────
// Plaid service implementation
// ─────────────────────────────────────────────────────────────────────────────

type PlaidService(
    config: PlaidConfig,
    http: HttpClient,
    factory: IDbConnectionFactory,
    vault: IVaultService,
    logger: ILogger<PlaidService>) =

    let makeAccessor (tenantId: Guid) =
        { new ITenantContextAccessor with
            member _.Context = Some { TenantId = tenantId; UserId = Guid.Empty } }

    let normalizeAndUpsert (tenantId: Guid) (syncEventId: Guid option) (txns: NormalizedTxn list) =
        task {
            let accessor = makeAccessor tenantId
            let accountRepo = AccountRepository.create factory accessor
            let txnRepo = TransactionRepository.create factory accessor
            let matcher = TransactionMatcher.create txnRepo
            let mutable created = 0
            let mutable updated = 0
            let mutable matched = 0

            for t in txns do
                let! accountOpt = accountRepo.GetByExternalIdAsync(t.accountId)
                match accountOpt with
                | None ->
                    logger.LogWarning("Skipping Plaid transaction {ExternalId}: no Steward account mapped for Plaid account {PlaidAccountId}", t.externalId, t.accountId)
                | Some account ->
                    let! existingOpt = txnRepo.GetByExternalIdAsync(t.externalId, account.Id)
                    let occurredAt = DateTimeOffset.Parse(t.date)
                    let postedAtOpt = t.authorizedDate |> Option.map DateTimeOffset.Parse
                    let amountMinor = int64 (-t.amount * 100m)
                    let amount = { Amount = decimal amountMinor / 100m; CurrencyCode = t.currency }

                    match existingOpt with
                    | Some existing ->
                        let now = DateTimeOffset.UtcNow
                        let updatedTxn =
                            { existing with
                                OccurredAt = occurredAt
                                PostedAt = postedAtOpt |> Option.orElse existing.PostedAt
                                Description = t.name
                                Merchant = t.merchantName |> Option.orElse existing.Merchant
                                UpdatedAt = now }
                        do! txnRepo.UpdateAsync(updatedTxn)
                        updated <- updated + 1
                    | None ->
                        let candidate =
                            { ExternalId = t.externalId
                              AccountId = account.Id
                              OccurredAt = occurredAt
                              PostedAt = postedAtOpt
                              Amount = amount
                              Description = t.name
                              Merchant = t.merchantName }

                        let! matchResult = matcher.MatchAsync tenantId account.Id candidate
                        let now = DateTimeOffset.UtcNow

                        let newTxn =
                            { Id = Guid.NewGuid()
                              TenantId = tenantId
                              AccountId = account.Id
                              OccurredAt = occurredAt
                              PostedAt = postedAtOpt
                              Amount = amount
                              Description = t.name
                              Merchant = t.merchantName
                              Memo = None
                              CategoryId = None
                              Source = TransactionSource.DataFeed "plaid"
                              ExternalId = Some t.externalId
                              MatchedTransactionId = None
                              TransferAccountId = None
                              Status = TransactionStatus.Cleared
                              MatchConfidence = None
                              SyncEventId = syncEventId
                              CreatedAt = now
                              UpdatedAt = now }

                        let! finalTxn =
                            task {
                                match matchResult with
                                | AutoMatched(manualId, conf) ->
                                    matched <- matched + 1
                                    let! manualOpt = txnRepo.GetAsync(manualId)
                                    match manualOpt with
                                    | Some manual ->
                                        let updatedManual =
                                            { manual with
                                                Status = TransactionStatus.Cleared
                                                MatchedTransactionId = Some newTxn.Id
                                                UpdatedAt = now }
                                        do! txnRepo.UpdateAsync(updatedManual)
                                    | None -> ()
                                    return { newTxn with Status = TransactionStatus.Cleared; MatchedTransactionId = Some manualId; MatchConfidence = Some conf }
                                | NeedsReview(manualId, conf) ->
                                    return { newTxn with Status = TransactionStatus.NeedsReview; MatchedTransactionId = Some manualId; MatchConfidence = Some conf }
                                | NoMatch ->
                                    return newTxn
                            }

                        let! _ = txnRepo.CreateAsync(finalTxn)
                        created <- created + 1

            return (created, updated, matched)
        }

    interface IPlaidService with
        member _.SyncConnectionAsync tenantId connectionId =
            task {
                let accessor = makeAccessor tenantId
                let connRepo = DataFeedConnectionRepository.create factory accessor
                let! connOpt = connRepo.GetAsync(connectionId)
                match connOpt with
                | None -> return failwith $"Connection not found: {connectionId}"
                | Some conn ->
                    let itemId, institutionId, cursor =
                        match conn.Metadata with
                        | ProviderMetadata.Plaid(i, inst, c) -> i, inst, c
                        | _ -> failwith $"Connection {connectionId} is not a Plaid connection"

                    let! envelope = vault.LoadAsync({ TenantId = tenantId; UserId = conn.UserId }, conn.CredentialRef)
                    let accessToken = envelope.AccessToken

                    let rec loop cursor addedAcc modifiedAcc removedAcc =
                        task {
                            let reqJson = PlaidHttp.buildSyncRequest config accessToken cursor
                            let! doc = PlaidHttp.postJson http $"{config.BaseUrl}/transactions/sync" reqJson
                            let added, modified, removed, nextCursor, hasMore = PlaidHttp.parseTransactions doc.RootElement
                            let newAdded = addedAcc @ added
                            let newModified = modifiedAcc @ modified
                            let newRemoved = removedAcc @ removed
                            if hasMore then
                                return! loop nextCursor newAdded newModified newRemoved
                            else
                                return newAdded, newModified, newRemoved, nextCursor
                        }

                    let! added, modified, removed, nextCursor = loop cursor [] [] []

                    let syncEventId = None // Could create a SyncEvent record here in future

                    let! created, updated, matched = normalizeAndUpsert tenantId syncEventId added
                    let! modifiedCreated, modifiedUpdated, modifiedMatched = normalizeAndUpsert tenantId syncEventId modified
                    let! removedCount =
                        if removed.IsEmpty then Task.FromResult 0
                        else
                            let txnRepo = TransactionRepository.create factory accessor
                            txnRepo.DeleteByExternalIdsAsync(removed)

                    let newMetadata = ProviderMetadata.Plaid(itemId, institutionId, nextCursor)
                    let updatedConn = { conn with Metadata = newMetadata; UpdatedAt = DateTimeOffset.UtcNow }
                    do! connRepo.UpdateAsync(updatedConn)

                    return {
                        Added = created + modifiedCreated
                        Modified = updated + modifiedUpdated
                        Removed = removedCount
                        NextCursor = nextCursor
                    }
            }

        member _.VerifyWebhookAsync bodyBytes verificationHeader =
            PlaidWebhookVerifier.verify http config bodyBytes verificationHeader
