#nowarn "0044"

module BitThicket.Steward.Api.Test.VaultServiceTests

open System
open System.Security.Cryptography
open System.Text
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open Testcontainers.PostgreSql
open Npgsql
open System.Collections.Generic
open Serilog
open Serilog.Core
open Serilog.Events
open BitThicket.Steward.Api
open BitThicket.Steward.Api.Vault
open BitThicket.Steward.Api.Domain

let private runMigrations (cs: string) =
    if String.IsNullOrWhiteSpace(cs) then ()
    else BitThicket.Steward.Api.Migrations.apply cs

let private sharedContainer : PostgreSqlContainer option =
    try
        let c =
            PostgreSqlBuilder()
                .WithImage("postgres:16-alpine")
                .Build()
        c.StartAsync().GetAwaiter().GetResult()
        Some c
    with _ ->
        None

let private connectionString () =
    match sharedContainer with
    | Some c -> c.GetConnectionString()
    | None -> null

let private canConnect () : bool =
    let cs = connectionString ()
    if String.IsNullOrWhiteSpace(cs) then false
    else
        try
            use dataSource = NpgsqlDataSource.Create(cs)
            use conn = dataSource.OpenConnection()
            true
        with _ -> false

let private setupVaultEnv (currentKey: byte[]) (previousKey: byte[] option) =
    Environment.SetEnvironmentVariable("STEWARD_VAULT_KEY", Convert.ToBase64String(currentKey))
    Environment.SetEnvironmentVariable("STEWARD_VAULT_KEY_VERSION", "1")
    match previousKey with
    | Some k ->
        Environment.SetEnvironmentVariable("STEWARD_VAULT_KEY_PREVIOUS", Convert.ToBase64String(k))
        Environment.SetEnvironmentVariable("STEWARD_VAULT_KEY_PREVIOUS_VERSION", "0")
    | None ->
        Environment.SetEnvironmentVariable("STEWARD_VAULT_KEY_PREVIOUS", null)
        Environment.SetEnvironmentVariable("STEWARD_VAULT_KEY_PREVIOUS_VERSION", null)

let private clearVaultEnv () =
    Environment.SetEnvironmentVariable("STEWARD_VAULT_KEY", null)
    Environment.SetEnvironmentVariable("STEWARD_VAULT_KEY_VERSION", null)
    Environment.SetEnvironmentVariable("STEWARD_VAULT_KEY_PREVIOUS", null)
    Environment.SetEnvironmentVariable("STEWARD_VAULT_KEY_PREVIOUS_VERSION", null)

let private makeFactory (cs: string) =
    let dataSource = NpgsqlDataSource.Create(cs)
    DbConnectionFactory(dataSource) :> IDbConnectionFactory

let private seedTenant (conn: NpgsqlConnection) (tenantId: Guid) (userId: Guid) =
    use cmd = conn.CreateCommand()
    cmd.CommandText <-
        """INSERT INTO tenants (id, display_name, created_at, updated_at)
           VALUES ($1, $2, now(), now());
           INSERT INTO users (id, email, password_hash, display_name, created_at, updated_at)
           VALUES ($3, $4, 'hash', 'User', now(), now());
           INSERT INTO user_tenant_memberships (user_id, tenant_id, role, created_at)
           VALUES ($3, $1, 'owner', now());"""
    cmd.Parameters.AddWithValue("$1", tenantId) |> ignore
    cmd.Parameters.AddWithValue("$2", $"Tenant {tenantId.ToString()[..7]}") |> ignore
    cmd.Parameters.AddWithValue("$3", userId) |> ignore
    cmd.Parameters.AddWithValue("$4", $"{userId}@test.com") |> ignore
    cmd.ExecuteNonQuery() |> ignore

// ── In-memory Serilog sink for redaction assertions ─────────────────────────

type InMemorySink() =
    let events = System.Collections.Generic.List<LogEvent>()
    interface ILogEventSink with
        member _.Emit(logEvent) = events.Add(logEvent)
    member _.Events = events :> seq<LogEvent>
    member _.Formatted() =
        events
        |> Seq.map (fun e ->
            use sw = new System.IO.StringWriter()
            e.RenderMessage(sw)
            sw.ToString())
        |> String.concat "\n"

let private buildTestLogger () : Logger * InMemorySink =
    let sink = InMemorySink()
    let logger =
        LoggerConfiguration()
            .Destructure.With<Program.SecretMaskingPolicy>()
            .WriteTo.Sink(sink)
            .CreateLogger()
    (logger, sink)

// ── AES-256-GCM primitive tests ─────────────────────────────────────────────

type AesGcm256Tests() =

    [<Fact>]
    member _.``Encrypt-decrypt round-trip succeeds``() =
        let key = RandomNumberGenerator.GetBytes(32)
        let plaintext = "s3cr3t-t0k3n-12345" |> Encoding.UTF8.GetBytes
        let nonce, ciphertext, _ = AesGcm256.encrypt key plaintext
        let decrypted = AesGcm256.decrypt key nonce ciphertext
        test <@ plaintext = decrypted @>

    [<Fact>]
    member _.``Decrypt with wrong key throws VaultDecryptionException``() =
        let key = RandomNumberGenerator.GetBytes(32)
        let wrongKey = RandomNumberGenerator.GetBytes(32)
        let plaintext = "s3cr3t-t0k3n-12345" |> Encoding.UTF8.GetBytes
        let nonce, ciphertext, _ = AesGcm256.encrypt key plaintext
        let exn = Assert.Throws<VaultDecryptionException>(fun () -> AesGcm256.decrypt wrongKey nonce ciphertext |> ignore)
        test <@ exn.Message.Contains("Key mismatch") || exn.Message.Contains("Decryption failed") @>

    [<Fact>]
    member _.``Decrypt with tampered ciphertext throws VaultDecryptionException``() =
        let key = RandomNumberGenerator.GetBytes(32)
        let plaintext = "s3cr3t-t0k3n-12345" |> Encoding.UTF8.GetBytes
        let nonce, ciphertext, _ = AesGcm256.encrypt key plaintext
        ciphertext.[0] <- ciphertext.[0] + 1uy
        let exn = Assert.Throws<VaultDecryptionException>(fun () -> AesGcm256.decrypt key nonce ciphertext |> ignore)
        test <@ exn.Message.Contains("Key mismatch") || exn.Message.Contains("Decryption failed") @>

// ── Vault service integration tests ─────────────────────────────────────────

type VaultServiceIntegrationTests() =

    [<Fact>]
    member _.``Store and load round-trip returns original envelope``() =
        task {
            if not (canConnect ()) then return () else

            let key = RandomNumberGenerator.GetBytes(32)
            setupVaultEnv key None
            try
                let cs = connectionString ()
                runMigrations cs
                let factory = makeFactory cs

                let tenantId = Guid.NewGuid()
                let userId = Guid.NewGuid()
                use seedConn = NpgsqlDataSource.Create(cs).OpenConnection()
                seedTenant seedConn tenantId userId

                let vault = VaultService(factory) :> IVaultService
                let ctx = { TenantId = tenantId; UserId = userId }
                let envelope: CredentialEnvelope = {
                    AccessToken = "access_123"
                    RefreshToken = Some "refresh_456"
                    ExpiresAt = Some (DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero))
                    ProviderSpecific = None
                }

                let! ref = vault.StoreAsync(ctx, envelope)
                test <@ ref.StartsWith("prv_vault_") @>

                let! loaded = vault.LoadAsync(ctx, ref)
                test <@ loaded.AccessToken = envelope.AccessToken @>
                test <@ loaded.RefreshToken = envelope.RefreshToken @>
                test <@ loaded.ExpiresAt = envelope.ExpiresAt @>
            finally
                clearVaultEnv ()
        }

    [<Fact>]
    member _.``Delete removes the row``() =
        task {
            if not (canConnect ()) then return () else

            let key = RandomNumberGenerator.GetBytes(32)
            setupVaultEnv key None
            try
                let cs = connectionString ()
                runMigrations cs
                let factory = makeFactory cs

                let tenantId = Guid.NewGuid()
                let userId = Guid.NewGuid()
                use seedConn = NpgsqlDataSource.Create(cs).OpenConnection()
                seedTenant seedConn tenantId userId

                let vault = VaultService(factory) :> IVaultService
                let ctx = { TenantId = tenantId; UserId = userId }
                let envelope: CredentialEnvelope = {
                    AccessToken = "token"
                    RefreshToken = None
                    ExpiresAt = None
                    ProviderSpecific = None
                }

                let! ref = vault.StoreAsync(ctx, envelope)
                let! deleted = vault.DeleteAsync(ctx, ref)
                test <@ deleted = true @>

                let! exn = Assert.ThrowsAsync<KeyNotFoundException>(fun () -> vault.LoadAsync(ctx, ref) :> Task)
                test <@ exn.Message.Contains("not found") @>
            finally
                clearVaultEnv ()
        }

    [<Fact>]
    member _.``Rotate re-encrypts with current key``() =
        task {
            if not (canConnect ()) then return () else

            let oldKey = RandomNumberGenerator.GetBytes(32)
            let newKey = RandomNumberGenerator.GetBytes(32)
            setupVaultEnv newKey (Some oldKey)
            try
                let cs = connectionString ()
                runMigrations cs
                let factory = makeFactory cs

                let tenantId = Guid.NewGuid()
                let userId = Guid.NewGuid()
                use seedConn = NpgsqlDataSource.Create(cs).OpenConnection()
                seedTenant seedConn tenantId userId

                // We need to store with the OLD key directly since the service always encrypts
                // with the current key. Insert a row manually encrypted with old key.
                let envelope: CredentialEnvelope = {
                    AccessToken = "rotated_token"
                    RefreshToken = None
                    ExpiresAt = None
                    ProviderSpecific = None
                }
                let plaintext = CredentialEnvelope.toBytes envelope
                let nonce, ciphertext, _ = AesGcm256.encrypt oldKey plaintext

                let guidPart = Guid.NewGuid().ToString("N")
                let ref = $"prv_test_{guidPart}"
                use rawConn = NpgsqlDataSource.Create(cs).OpenConnection()
                use cmd = rawConn.CreateCommand()
                cmd.CommandText <-
                    """INSERT INTO credential_vault (id, tenant_id, ref, ciphertext, nonce, key_version, created_at, updated_at)
                       VALUES ($1, $2, $3, $4, $5, $6, now(), now())"""
                cmd.Parameters.AddWithValue("$1", Guid.NewGuid()) |> ignore
                cmd.Parameters.AddWithValue("$2", tenantId) |> ignore
                cmd.Parameters.AddWithValue("$3", ref) |> ignore
                cmd.Parameters.AddWithValue("$4", ciphertext) |> ignore
                cmd.Parameters.AddWithValue("$5", nonce) |> ignore
                cmd.Parameters.AddWithValue("$6", 0) |> ignore
                cmd.ExecuteNonQuery() |> ignore

                let vault = VaultService(factory) :> IVaultService
                let ctx = { TenantId = tenantId; UserId = userId }

                // Load with old key should work
                let! loaded = vault.LoadAsync(ctx, ref)
                test <@ loaded.AccessToken = "rotated_token" @>

                // Rotate to new key
                let! rotated = vault.RotateAsync(ctx, ref)
                test <@ rotated = true @>

                // Load still works (now under new key)
                let! loaded2 = vault.LoadAsync(ctx, ref)
                test <@ loaded2.AccessToken = "rotated_token" @>
            finally
                clearVaultEnv ()
        }

    [<Fact>]
    member _.``Cross-tenant isolation: tenant A cannot read tenant B vault row``() =
        task {
            if not (canConnect ()) then return () else

            let key = RandomNumberGenerator.GetBytes(32)
            setupVaultEnv key None
            try
                let cs = connectionString ()
                runMigrations cs
                let factory = makeFactory cs

                let tenantA = Guid.NewGuid()
                let userA = Guid.NewGuid()
                let tenantB = Guid.NewGuid()
                let userB = Guid.NewGuid()

                use seedConn = NpgsqlDataSource.Create(cs).OpenConnection()
                seedTenant seedConn tenantA userA
                seedTenant seedConn tenantB userB

                let vault = VaultService(factory) :> IVaultService
                let ctxA = { TenantId = tenantA; UserId = userA }
                let ctxB = { TenantId = tenantB; UserId = userB }

                let envelope: CredentialEnvelope = {
                    AccessToken = "tenant-a-secret"
                    RefreshToken = None
                    ExpiresAt = None
                    ProviderSpecific = None
                }

                let! refA = vault.StoreAsync(ctxA, envelope)

                // Tenant B trying to load tenant A's ref should get KeyNotFoundException
                // because RLS filters out the row before the app sees it.
                let! exn = Assert.ThrowsAsync<KeyNotFoundException>(fun () -> vault.LoadAsync(ctxB, refA) :> Task)
                test <@ exn.Message.Contains("not found") @>
            finally
                clearVaultEnv ()
        }

    [<Fact>]
    member _.``Wrong current key with no previous key throws on load``() =
        task {
            if not (canConnect ()) then return () else

            let key = RandomNumberGenerator.GetBytes(32)
            setupVaultEnv key None
            try
                let cs = connectionString ()
                runMigrations cs
                let factory = makeFactory cs

                let tenantId = Guid.NewGuid()
                let userId = Guid.NewGuid()
                use seedConn = NpgsqlDataSource.Create(cs).OpenConnection()
                seedTenant seedConn tenantId userId

                let vault = VaultService(factory) :> IVaultService
                let ctx = { TenantId = tenantId; UserId = userId }
                let envelope: CredentialEnvelope = {
                    AccessToken = "token"
                    RefreshToken = None
                    ExpiresAt = None
                    ProviderSpecific = None
                }

                let! ref = vault.StoreAsync(ctx, envelope)

                // Swap the environment key to a different one
                let wrongKey = RandomNumberGenerator.GetBytes(32)
                Environment.SetEnvironmentVariable("STEWARD_VAULT_KEY", Convert.ToBase64String(wrongKey))

                // Force a new VaultService (or we can just test the primitive directly)
                // The vault service reads keys lazily, so we create a new one
                let vault2 = VaultService(factory) :> IVaultService
                let! exn = Assert.ThrowsAsync<VaultDecryptionException>(fun () -> vault2.LoadAsync(ctx, ref) :> Task)
                test <@ exn.Message.Contains("Key mismatch") || exn.Message.Contains("Decryption failed") @>
            finally
                clearVaultEnv ()
        }

// ── Log redaction tests ─────────────────────────────────────────────────────

type LogRedactionTests() =

    [<Fact>]
    member _.``Serilog masks accessToken and refreshToken in destructured object``() =
        let logger, sink = buildTestLogger ()
        let obj = {| accessToken = "should_be_hidden"; refreshToken = "also_hidden"; publicField = "ok" |}
        logger.Information("Token payload: {@Payload}", obj)

        let text = sink.Formatted()
        test <@ not (text.Contains("should_be_hidden")) @>
        test <@ not (text.Contains("also_hidden")) @>
        test <@ text.Contains("ok") @>
        test <@ text.Contains("[REDACTED]") @>

    [<Fact>]
    member _.``Serilog masks password and secret keys``() =
        let logger, sink = buildTestLogger ()
        let obj = {| password = "hunter2"; apiSecret = "shh"; name = "Alice" |}
        logger.Information("User: {@User}", obj)

        let text = sink.Formatted()
        test <@ not (text.Contains("hunter2")) @>
        test <@ not (text.Contains("shh")) @>
        test <@ text.Contains("Alice") @>
        test <@ text.Contains("[REDACTED]") @>

    [<Fact>]
    member _.``Serilog masks nested secret properties``() =
        let logger, sink = buildTestLogger ()
        let inner = {| secret = "nested_secret_value" |}
        let outer = {| wrapper = inner; id = 42 |}
        logger.Information("Outer: {@Outer}", outer)

        let text = sink.Formatted()
        test <@ not (text.Contains("nested_secret_value")) @>
        test <@ text.Contains("42") @>
        test <@ text.Contains("[REDACTED]") @>
