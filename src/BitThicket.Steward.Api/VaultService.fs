namespace BitThicket.Steward.Api.Vault

open System
open System.Collections.Generic
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Npgsql

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
        JsonSerializer.Deserialize<CredentialEnvelope>(ReadOnlySpan<byte>(bytes), jsonOptions)

// ─────────────────────────────────────────────────────────────────────────────
// Vault service interface
// ─────────────────────────────────────────────────────────────────────────────

type IVaultService =
    /// Encrypt and store a credential envelope. Returns the generated opaque ref.
    abstract StoreAsync : BitThicket.Steward.Api.TenantContext * CredentialEnvelope -> Task<string>
    /// Load and decrypt a credential envelope by ref.
    abstract LoadAsync : BitThicket.Steward.Api.TenantContext * string -> Task<CredentialEnvelope>
    /// Delete a vault row by ref. Returns true if the row existed.
    abstract DeleteAsync : BitThicket.Steward.Api.TenantContext * string -> Task<bool>
    /// Re-encrypt an existing credential under the current key.
    /// Returns the same ref (ciphertext is updated in-place).
    abstract RotateAsync : BitThicket.Steward.Api.TenantContext * string -> Task<bool>

// ─────────────────────────────────────────────────────────────────────────────
// Vault repository (SQL)
// ─────────────────────────────────────────────────────────────────────────────

type internal VaultRow = {
    Id: Guid
    TenantId: Guid
    Ref: string
    Ciphertext: byte[]
    Nonce: byte[]
    KeyVersion: int
    CreatedAt: DateTime
    UpdatedAt: DateTime
}

module internal VaultRepository =
    let private mapRow (reader: System.Data.Common.DbDataReader) : VaultRow =
        {
            Id = reader.GetGuid(0)
            TenantId = reader.GetGuid(1)
            Ref = reader.GetString(2)
            Ciphertext = reader.GetFieldValue<byte[]>(3)
            Nonce = reader.GetFieldValue<byte[]>(4)
            KeyVersion = reader.GetInt32(5)
            CreatedAt = reader.GetDateTime(6)
            UpdatedAt = reader.GetDateTime(7)
        }

    let getByRef (conn: NpgsqlConnection) (ref: string) =
        task {
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                "SELECT id, tenant_id, ref, ciphertext, nonce, key_version, created_at, updated_at FROM credential_vault WHERE ref = $1"
            cmd.Parameters.AddWithValue("$1", ref) |> ignore
            let! reader = cmd.ExecuteReaderAsync()
            use reader = reader
            let! hasRow = reader.ReadAsync()
            return if hasRow then Some(mapRow reader) else None
        }

    let insert (conn: NpgsqlConnection) (row: VaultRow) =
        task {
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                """INSERT INTO credential_vault (id, tenant_id, ref, ciphertext, nonce, key_version, created_at, updated_at)
                   VALUES ($1, $2, $3, $4, $5, $6, $7, $8)"""
            cmd.Parameters.AddWithValue("$1", row.Id) |> ignore
            cmd.Parameters.AddWithValue("$2", row.TenantId) |> ignore
            cmd.Parameters.AddWithValue("$3", row.Ref) |> ignore
            cmd.Parameters.AddWithValue("$4", row.Ciphertext) |> ignore
            cmd.Parameters.AddWithValue("$5", row.Nonce) |> ignore
            cmd.Parameters.AddWithValue("$6", row.KeyVersion) |> ignore
            cmd.Parameters.AddWithValue("$7", row.CreatedAt) |> ignore
            cmd.Parameters.AddWithValue("$8", row.UpdatedAt) |> ignore
            let! _ = cmd.ExecuteNonQueryAsync()
            return ()
        }

    let updateCiphertext (conn: NpgsqlConnection) (id: Guid) (ciphertext: byte[]) (nonce: byte[]) (keyVersion: int) =
        task {
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                """UPDATE credential_vault
                   SET ciphertext = $1, nonce = $2, key_version = $3, updated_at = now()
                   WHERE id = $4"""
            cmd.Parameters.AddWithValue("$1", ciphertext) |> ignore
            cmd.Parameters.AddWithValue("$2", nonce) |> ignore
            cmd.Parameters.AddWithValue("$3", keyVersion) |> ignore
            cmd.Parameters.AddWithValue("$4", id) |> ignore
            let! rows = cmd.ExecuteNonQueryAsync()
            return rows > 0
        }

    let deleteByRef (conn: NpgsqlConnection) (ref: string) =
        task {
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                "DELETE FROM credential_vault WHERE ref = $1"
            cmd.Parameters.AddWithValue("$1", ref) |> ignore
            let! rows = cmd.ExecuteNonQueryAsync()
            return rows > 0
        }

// ─────────────────────────────────────────────────────────────────────────────
// Vault service implementation
// ─────────────────────────────────────────────────────────────────────────────

type VaultService(factory: BitThicket.Steward.Api.IDbConnectionFactory) =
    let currentKey = lazy (VaultKeyResolver.currentKey ())
    let previousKey = lazy (VaultKeyResolver.previousKey ())

    let resolveKey (keyVersion: int) : byte[] * int =
        match currentKey.Value with
        | VaultKey.Current(v, k) when v = keyVersion -> (k, v)
        | VaultKey.Current(v, _) ->
            match previousKey.Value with
            | Some (VaultKey.Previous(pv, pk)) when pv = keyVersion -> (pk, pv)
            | _ ->
                raise (VaultDecryptionException($"No key available for version {keyVersion}."))
        | _ ->
            raise (VaultDecryptionException($"No key available for version {keyVersion}."))

    interface IVaultService with
        member _.StoreAsync(ctx: BitThicket.Steward.Api.TenantContext, envelope: CredentialEnvelope) =
            task {
                let! conn = factory.OpenForTenantAsync(ctx)
                use _ = conn

                let ref = CredentialRefGenerator.generate "vault"
                let plaintext = CredentialEnvelope.toBytes envelope

                match currentKey.Value with
                | VaultKey.Current(keyVersion, key) ->
                    let nonce, ciphertext, _ = AesGcm256.encrypt key plaintext

                    let row: VaultRow = {
                        Id = Guid.NewGuid()
                        TenantId = ctx.TenantId
                        Ref = ref
                        Ciphertext = ciphertext
                        Nonce = nonce
                        KeyVersion = keyVersion
                        CreatedAt = DateTime.UtcNow
                        UpdatedAt = DateTime.UtcNow
                    }

                    do! VaultRepository.insert conn row
                    return ref
                | _ ->
                    // Current key is always a VaultKey.Current DU case
                    return failwith "Unreachable: current key resolution failed."
            }

        member _.LoadAsync(ctx: BitThicket.Steward.Api.TenantContext, ref: string) =
            task {
                let! conn = factory.OpenForTenantAsync(ctx)
                use _ = conn

                let! rowOpt = VaultRepository.getByRef conn ref
                match rowOpt with
                | None ->
                    return raise (KeyNotFoundException($"Credential ref not found: {ref}"))
                | Some row ->
                    let key, _ = resolveKey row.KeyVersion
                    let plaintext = AesGcm256.decrypt key row.Nonce row.Ciphertext
                    return CredentialEnvelope.fromBytes plaintext
            }

        member _.DeleteAsync(ctx: BitThicket.Steward.Api.TenantContext, ref: string) =
            task {
                let! conn = factory.OpenForTenantAsync(ctx)
                use _ = conn
                return! VaultRepository.deleteByRef conn ref
            }

        member _.RotateAsync(ctx: BitThicket.Steward.Api.TenantContext, ref: string) =
            task {
                let! conn = factory.OpenForTenantAsync(ctx)
                use _ = conn

                let! rowOpt = VaultRepository.getByRef conn ref
                match rowOpt with
                | None -> return false
                | Some row ->
                    let oldKey, _ = resolveKey row.KeyVersion
                    let plaintext = AesGcm256.decrypt oldKey row.Nonce row.Ciphertext

                    match currentKey.Value with
                    | VaultKey.Current(newVersion, newKey) when newVersion = row.KeyVersion ->
                        // Already current key — nothing to do
                        return true
                    | VaultKey.Current(newVersion, newKey) ->
                        let newNonce, newCiphertext, _ = AesGcm256.encrypt newKey plaintext
                        return! VaultRepository.updateCiphertext conn row.Id newCiphertext newNonce newVersion
                    | _ ->
                        return failwith "Unreachable: current key resolution failed."
            }
