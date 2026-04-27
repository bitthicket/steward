module BitThicket.Steward.Api.Data

open System
open System.Data
open Npgsql
open DbUp
open Microsoft.Extensions.Logging

let connectionString () =
    Environment.GetEnvironmentVariable("DATABASE_URL")
    |> Option.ofObj
    |> Option.defaultValue "Host=localhost;Database=steward;Username=steward;Password=steward"

let mkConn () = new NpgsqlConnection(connectionString ())

let ensureDatabase () =
    let cs = connectionString ()
    EnsureDatabase.For.PostgresqlDatabase(cs)

let runMigrations (logger: ILogger) =
    let cs = connectionString ()
    let upgrader =
        DeployChanges.To
            .PostgresqlDatabase(cs)
            .WithScriptsEmbeddedInAssembly(System.Reflection.Assembly.GetExecutingAssembly())
            .LogToConsole()
            .Build()

    let result = upgrader.PerformUpgrade()
    if not result.Successful then
        logger.LogError(result.Error, "Database migration failed")
        raise result.Error
    else
        logger.LogInformation("Database migrations applied successfully")

module Onboarding =
    open BitThicket.Steward.Api.Domain

    let getState (tenantId: Guid) : OnboardingState option =
        use conn = mkConn ()
        conn.Open()
        use cmd = new NpgsqlCommand(
            """
            SELECT tenant_id, current_step, started_at, completed_at, completed_steps, skipped
            FROM tenant_onboarding
            WHERE tenant_id = @tenant_id
            """, conn)
        cmd.Parameters.AddWithValue("@tenant_id", tenantId) |> ignore
        use reader = cmd.ExecuteReader()
        if reader.Read() then
            Some {
                TenantId = reader.GetGuid(0)
                CurrentStep = reader.GetInt32(1)
                StartedAt = reader.GetDateTime(2) |> DateTimeOffset
                CompletedAt =
                    if reader.IsDBNull(3) then None
                    else Some (reader.GetDateTime(3) |> DateTimeOffset)
                CompletedSteps =
                    let json = reader.GetString(4)
                    System.Text.Json.JsonSerializer.Deserialize<int list>(json)
                Skipped = reader.GetBoolean(5)
            }
        else
            None

    let upsertState (state: OnboardingState) : unit =
        use conn = mkConn ()
        conn.Open()
        use cmd = new NpgsqlCommand(
            """
            INSERT INTO tenant_onboarding (tenant_id, current_step, started_at, completed_at, completed_steps, skipped)
            VALUES (@tenant_id, @current_step, @started_at, @completed_at, @completed_steps::jsonb, @skipped)
            ON CONFLICT (tenant_id)
            DO UPDATE SET
                current_step = EXCLUDED.current_step,
                completed_at = EXCLUDED.completed_at,
                completed_steps = EXCLUDED.completed_steps,
                skipped = EXCLUDED.skipped,
                updated_at = NOW()
            """, conn)
        cmd.Parameters.AddWithValue("@tenant_id", state.TenantId) |> ignore
        cmd.Parameters.AddWithValue("@current_step", state.CurrentStep) |> ignore
        cmd.Parameters.AddWithValue("@started_at", state.StartedAt) |> ignore
        match state.CompletedAt with
        | Some dt -> cmd.Parameters.AddWithValue("@completed_at", dt) |> ignore
        | None -> cmd.Parameters.AddWithValue("@completed_at", DBNull.Value) |> ignore
        let stepsJson = System.Text.Json.JsonSerializer.Serialize(state.CompletedSteps)
        cmd.Parameters.AddWithValue("@completed_steps", stepsJson) |> ignore
        cmd.Parameters.AddWithValue("@skipped", state.Skipped) |> ignore
        cmd.ExecuteNonQuery() |> ignore

module Tenant =
    open BitThicket.Steward.Api.Domain

    let create (tenant: Tenant) : unit =
        use conn = mkConn ()
        conn.Open()
        use cmd = new NpgsqlCommand(
            """
            INSERT INTO tenants (id, owner_user_id, display_name, default_currency_code, created_at, updated_at)
            VALUES (@id, @owner_user_id, @display_name, @default_currency_code, @created_at, @updated_at)
            """, conn)
        cmd.Parameters.AddWithValue("@id", tenant.Id) |> ignore
        cmd.Parameters.AddWithValue("@owner_user_id", tenant.OwnerUserId) |> ignore
        cmd.Parameters.AddWithValue("@display_name", tenant.DisplayName) |> ignore
        cmd.Parameters.AddWithValue("@default_currency_code", tenant.DefaultCurrencyCode) |> ignore
        cmd.Parameters.AddWithValue("@created_at", tenant.CreatedAt) |> ignore
        cmd.Parameters.AddWithValue("@updated_at", tenant.UpdatedAt) |> ignore
        cmd.ExecuteNonQuery() |> ignore

    let getByOwner (userId: Guid) : Tenant option =
        use conn = mkConn ()
        conn.Open()
        use cmd = new NpgsqlCommand(
            "SELECT id, owner_user_id, display_name, default_currency_code, created_at, updated_at FROM tenants WHERE owner_user_id = @owner_user_id",
            conn)
        cmd.Parameters.AddWithValue("@owner_user_id", userId) |> ignore
        use reader = cmd.ExecuteReader()
        if reader.Read() then
            Some {
                Id = reader.GetGuid(0)
                OwnerUserId = reader.GetGuid(1)
                DisplayName = reader.GetString(2)
                DefaultCurrencyCode = reader.GetString(3)
                CreatedAt = reader.GetDateTime(4) |> DateTimeOffset
                UpdatedAt = reader.GetDateTime(5) |> DateTimeOffset
            }
        else
            None

    let getById (id: Guid) : Tenant option =
        use conn = mkConn ()
        conn.Open()
        use cmd = new NpgsqlCommand(
            "SELECT id, owner_user_id, display_name, default_currency_code, created_at, updated_at FROM tenants WHERE id = @id",
            conn)
        cmd.Parameters.AddWithValue("@id", id) |> ignore
        use reader = cmd.ExecuteReader()
        if reader.Read() then
            Some {
                Id = reader.GetGuid(0)
                OwnerUserId = reader.GetGuid(1)
                DisplayName = reader.GetString(2)
                DefaultCurrencyCode = reader.GetString(3)
                CreatedAt = reader.GetDateTime(4) |> DateTimeOffset
                UpdatedAt = reader.GetDateTime(5) |> DateTimeOffset
            }
        else
            None

module User =
    open BitThicket.Steward.Api.Domain

    let create (user: User) : unit =
        use conn = mkConn ()
        conn.Open()
        use cmd = new NpgsqlCommand(
            """
            INSERT INTO users (id, display_name, email, password_hash, created_at, updated_at)
            VALUES (@id, @display_name, @email, @password_hash, @created_at, @updated_at)
            """, conn)
        cmd.Parameters.AddWithValue("@id", user.Id) |> ignore
        cmd.Parameters.AddWithValue("@display_name", user.DisplayName) |> ignore
        cmd.Parameters.AddWithValue("@email", user.Email) |> ignore
        cmd.Parameters.AddWithValue("@password_hash", user.PasswordHash) |> ignore
        cmd.Parameters.AddWithValue("@created_at", user.CreatedAt) |> ignore
        cmd.Parameters.AddWithValue("@updated_at", user.UpdatedAt) |> ignore
        cmd.ExecuteNonQuery() |> ignore

    let getByEmail (email: string) : User option =
        use conn = mkConn ()
        conn.Open()
        use cmd = new NpgsqlCommand(
            "SELECT id, display_name, email, password_hash, created_at, updated_at FROM users WHERE email = @email",
            conn)
        cmd.Parameters.AddWithValue("@email", email) |> ignore
        use reader = cmd.ExecuteReader()
        if reader.Read() then
            Some {
                Id = reader.GetGuid(0)
                DisplayName = reader.GetString(1)
                Email = reader.GetString(2)
                PasswordHash = reader.GetString(3)
                CreatedAt = reader.GetDateTime(4) |> DateTimeOffset
                UpdatedAt = reader.GetDateTime(5) |> DateTimeOffset
            }
        else
            None

    let getById (id: Guid) : User option =
        use conn = mkConn ()
        conn.Open()
        use cmd = new NpgsqlCommand(
            "SELECT id, display_name, email, password_hash, created_at, updated_at FROM users WHERE id = @id",
            conn)
        cmd.Parameters.AddWithValue("@id", id) |> ignore
        use reader = cmd.ExecuteReader()
        if reader.Read() then
            Some {
                Id = reader.GetGuid(0)
                DisplayName = reader.GetString(1)
                Email = reader.GetString(2)
                PasswordHash = reader.GetString(3)
                CreatedAt = reader.GetDateTime(4) |> DateTimeOffset
                UpdatedAt = reader.GetDateTime(5) |> DateTimeOffset
            }
        else
            None
