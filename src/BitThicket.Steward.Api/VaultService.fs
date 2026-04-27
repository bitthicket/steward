namespace BitThicket.Steward.Api.Vault

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Npgsql
open BitThicket.Steward.Api
open BitThicket.Steward.Api.Domain

// ─────────────────────────────────────────────────────────────────────────────
// Vault key resolution and rotation support
// ─────────────────────────────────────────────────────────────────────────────

[<RequireQualifiedAccess>]
type VaultKey =
    | Current of version: int * bytes: byte[]
    | Previous of version: int * bytes: byte[]

/// Thrown when the ciphertext cannot be authenticated or the key is wrong.
/// Never exposes plaintext or key material in the message.
type VaultDecryptionException(message: string) =
    inherit Exception(message)

module VaultKeyResolver =
    let private tryDecodeBase64 (envVar: string) : byte[] option =
        match Environment.GetEnvironmentVariable(envVar) with
        | null | "" -> None
        | v ->
            try Some(Convert.FromBase64String(v))
            with :? FormatException -> None

    let private validateKeyLength (key: byte[]) : unit =
        if key.Length <> 32 then
            raise (InvalidOperationException(
                $"Vault key must be 32 bytes (AES-256) but received {key.Length} bytes. Check STEWARD_VAULT_KEY is a valid base64-encoded 32-byte key."))

    let currentKey () : VaultKey =
        match tryDecodeBase64 "STEWARD_VAULT_KEY" with
        | None ->
            raise (InvalidOperationException(
                "STEWARD_VAULT_KEY is not set. The vault requires a 32-byte AES-256 key (base64-encoded) at startup."))
        | Some key ->
            validateKeyLength key
            let version =
                match Environment.GetEnvironmentVariable("STEWARD_VAULT_KEY_VERSION") with
                | null | "" -> 1
                | v ->
                    match Int32.TryParse(v) with
                    | true, n when n > 0 -> n
                    | _ ->
                        raise (InvalidOperationException(
                            "STEWARD_VAULT_KEY_VERSION must be a positive integer."))
            VaultKey.Current(version, key)

    let previousKey () : VaultKey option =
        match tryDecodeBase64 "STEWARD_VAULT_KEY_PREVIOUS" with
        | None -> None
        | Some key ->
            validateKeyLength key
            let version =
                match Environment.GetEnvironmentVariable("STEWARD_VAULT_KEY_PREVIOUS_VERSION") with
                | null | "" -> 0
                | v ->
                    match Int32.TryParse(v) with
                    | true, n when n >= 0 -> n
                    | _ ->
                        raise (InvalidOperationException(
                            "STEWARD_VAULT_KEY_PREVIOUS_VERSION must be a non-negative integer."))
            Some (VaultKey.Previous(version, key))

// ─────────────────────────────────────────────────────────────────────────────
// AES-256-GCM primitives
// ─────────────────────────────────────────────────────────────────────────────

[<RequireQualifiedAccess>]
module AesGcm256 =
    let private nonceSize = 12
    let private tagSize = 16

    let private randomNonce () : byte[] =
        RandomNumberGenerator.GetBytes(nonceSize)

    /// Encrypt plaintext. Returns (nonce, ciphertextWithTag, keyVersion).
    let encrypt (key: byte[]) (plaintext: byte[]) : byte[] * byte[] * int =
        use aes = new AesGcm(key, tagSize)
        let nonce = randomNonce ()
        let ciphertext = Array.zeroCreate<byte> plaintext.Length
        let tag = Array.zeroCreate<byte> tagSize
        aes.Encrypt(nonce, plaintext, ciphertext, tag)
        // Append tag to ciphertext for compact storage
        let combined = Array.zeroCreate<byte> (ciphertext.Length + tagSize)
        Buffer.BlockCopy(ciphertext, 0, combined, 0, ciphertext.Length)
        Buffer.BlockCopy(tag, 0, combined, ciphertext.Length, tagSize)
        (nonce, combined, 0)

    /// Decrypt ciphertext (with tag appended). Throws VaultDecryptionException on failure.
    let decrypt (key: byte[]) (nonce: byte[]) (ciphertextWithTag: byte[]) : byte[] =
        if ciphertextWithTag.Length < tagSize then
            raise (VaultDecryptionException("Ciphertext too short to contain authentication tag."))
        let ctLen = ciphertextWithTag.Length - tagSize
        let ciphertext = Array.zeroCreate<byte> ctLen
        let tag = Array.zeroCreate<byte> tagSize
        Buffer.BlockCopy(ciphertextWithTag, 0, ciphertext, 0, ctLen)
        Buffer.BlockCopy(ciphertextWithTag, ctLen, tag, 0, tagSize)
        use aes = new AesGcm(key, tagSize)
        let plaintext = Array.zeroCreate<byte> ctLen
        try
            aes.Decrypt(nonce, ciphertext, tag, plaintext)
            plaintext
        with
        | :? CryptographicException as ex ->
            raise (VaultDecryptionException($"Decryption failed: {ex.GetType().Name}. Key mismatch or tampered ciphertext."))

// ─────────────────────────────────────────────────────────────────────────────
// Credential reference generation
// ─────────────────────────────────────────────────────────────────────────────

module CredentialRefGenerator =
    /// Generate an opaque credential reference of the form prv_<provider>_<ulid>.
    /// Uses Guid for the random component (no external ULID dependency).
    let generate (provider: string) : string =
        let safeProvider =
            provider.ToLowerInvariant()
                .Replace(" ", "-")
                .Replace("_", "-")
        let randomPart = Guid.NewGuid().ToString("N")[..25]
        $"prv_{safeProvider}_{randomPart}"

// ─────────────────────────────────────────────────────────────────────────────
// Plaintext envelope
// ─────────────────────────────────────────────────────────────────────────────

/// JSON envelope stored encrypted in the vault. NEVER log this whole type.
type CredentialEnvelope = {
    AccessToken: string
    RefreshToken: string option
    ExpiresAt: DateTimeOffset option
    ProviderSpecific: System.Text.Json.Nodes.JsonObject option
}

module CredentialEnvelope =
    let private jsonOptions =
        let opts = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)
        opts

    let toBytes (env: CredentialEnvelope) : byte[] =
        JsonSerializer.SerializeToUtf8Bytes(env, jsonOptions)

    let fromBytes (bytes: byte[]) : CredentialEnvelope =
        JsonSerializer.Deserialize<CredentialEnvelope>(bytes, jsonOptions)

// ─────────────────────────────────────────────────────────────────────────────
// Vault service interface
// ─────────────────────────────────────────────────────────────────────────────

type IVaultService =
    abstract StoreAsync : tenantId:Guid * ref:string * envelope:CredentialEnvelope -> Task<unit>
    abstract RetrieveAsync : tenantId:Guid * ref:string -> Task<CredentialEnvelope option>
    abstract RotateAsync : tenantId:Guid * ref:string -> Task<bool>
    abstract DeleteAsync : tenantId:Guid * ref:string -> Task<bool>

// ─────────────────────────────────────────────────────────────────────────────
// Vault service implementation
// ─────────────────────────────────────────────────────────────────────────────

type VaultService(factory: IDbConnectionFactory) =
    let currentKey = VaultKeyResolver.currentKey()
    let previousKey = VaultKeyResolver.previousKey()

    interface IVaultService with
        member _.StoreAsync(tenantId, ref, envelope) =
            task {
                let plaintext = CredentialEnvelope.toBytes envelope
                let (nonce, ciphertext, _) = AesGcm256.encrypt (match currentKey with VaultKey.Current(_, k) -> k | VaultKey.Previous(_, k) -> k) plaintext
                let keyVersion = match currentKey with VaultKey.Current(v, _) -> v | VaultKey.Previous(v, _) -> v

                let ctx : TenantContext = { TenantId = tenantId; UserId = Guid.Empty }
                use! conn = factory.OpenForTenantAsync(ctx)
                use cmd = conn.CreateCommand()
                cmd.CommandText <-
                    """INSERT INTO credential_vault (id, tenant_id, ref, ciphertext, nonce, key_version, created_at, updated_at)
                       VALUES ($1, $2, $3, $4, $5, $6, $7, $8)
                       ON CONFLICT (ref) DO UPDATE SET
                           ciphertext = EXCLUDED.ciphertext,
                           nonce = EXCLUDED.nonce,
                           key_version = EXCLUDED.key_version,
                           updated_at = EXCLUDED.updated_at"""
                cmd.Parameters.AddWithValue("$1", Guid.NewGuid()) |> ignore
                cmd.Parameters.AddWithValue("$2", tenantId) |> ignore
                cmd.Parameters.AddWithValue("$3", ref) |> ignore
                let ctParam = cmd.CreateParameter()
                ctParam.ParameterName <- "$4"
                ctParam.NpgsqlDbType <- NpgsqlTypes.NpgsqlDbType.Bytea
                ctParam.Value <- ciphertext
                cmd.Parameters.Add(ctParam) |> ignore
                let nonceParam = cmd.CreateParameter()
                nonceParam.ParameterName <- "$5"
                nonceParam.NpgsqlDbType <- NpgsqlTypes.NpgsqlDbType.Bytea
                nonceParam.Value <- nonce
                cmd.Parameters.Add(nonceParam) |> ignore
                cmd.Parameters.AddWithValue("$6", keyVersion) |> ignore
                cmd.Parameters.AddWithValue("$7", DateTimeOffset.UtcNow.UtcDateTime) |> ignore
                cmd.Parameters.AddWithValue("$8", DateTimeOffset.UtcNow.UtcDateTime) |> ignore
                let! _ = cmd.ExecuteNonQueryAsync()
                return ()
            }

        member _.RetrieveAsync(tenantId, ref) =
            task {
                let ctx : TenantContext = { TenantId = tenantId; UserId = Guid.Empty }
                use! conn = factory.OpenForTenantAsync(ctx)
                use cmd = conn.CreateCommand()
                cmd.CommandText <-
                    "SELECT ciphertext, nonce, key_version FROM credential_vault WHERE tenant_id = $1 AND ref = $2"
                cmd.Parameters.AddWithValue("$1", tenantId) |> ignore
                cmd.Parameters.AddWithValue("$2", ref) |> ignore
                let! reader = cmd.ExecuteReaderAsync()
                use reader = reader
                let! hasRow = reader.ReadAsync()
                let result =
                    if not hasRow then
                        None
                    else
                        let ciphertext = reader.GetValue(0) :?> byte[]
                        let nonce = reader.GetValue(1) :?> byte[]
                        let keyVersion = reader.GetInt32(2)

                        let key =
                            match currentKey with
                            | VaultKey.Current(v, k) when v = keyVersion -> Some k
                            | _ ->
                                match previousKey with
                                | Some(VaultKey.Previous(v, k)) when v = keyVersion -> Some k
                                | _ -> None

                        match key with
                        | None -> raise (VaultDecryptionException($"No key available for version {keyVersion}."))
                        | Some k ->
                            let plaintext = AesGcm256.decrypt k nonce ciphertext
                            Some(CredentialEnvelope.fromBytes plaintext)
                return result
            }

        member _.RotateAsync(tenantId, ref) =
            task {
                let! existing = (VaultService(factory) :> IVaultService).RetrieveAsync(tenantId, ref)
                match existing with
                | None -> return false
                | Some envelope ->
                    do! (VaultService(factory) :> IVaultService).StoreAsync(tenantId, ref, envelope)
                    return true
            }

        member _.DeleteAsync(tenantId, ref) =
            task {
                let ctx : TenantContext = { TenantId = tenantId; UserId = Guid.Empty }
                use! conn = factory.OpenForTenantAsync(ctx)
                use cmd = conn.CreateCommand()
                cmd.CommandText <- "DELETE FROM credential_vault WHERE tenant_id = $1 AND ref = $2"
                cmd.Parameters.AddWithValue("$1", tenantId) |> ignore
                cmd.Parameters.AddWithValue("$2", ref) |> ignore
                let! rows = cmd.ExecuteNonQueryAsync()
                return rows > 0
            }
