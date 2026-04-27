namespace BitThicket.Steward.Api

open System
open System.Data.Common
open System.Text.Json
open System.Threading.Tasks
open Npgsql
open BitThicket.Steward.Api.Domain

type IOnboardingRepository =
    abstract GetAsync : tenantId:Guid -> Task<OnboardingState option>
    abstract UpsertAsync : state:OnboardingState -> Task<unit>
    abstract CreateInitialAsync : tenantId:Guid -> Task<unit>

module OnboardingRepository =

    let private jsonOptions =
        JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower)

    let private mapState (reader: DbDataReader) : OnboardingState =
        {
            TenantId = reader.GetGuid(0)
            CurrentStep = reader.GetInt32(1)
            StartedAt = Sql.dateTimeOffset reader 2
            CompletedAt = Sql.nullableDateTimeOffset reader 3
            CompletedSteps =
                let json = reader.GetString(4)
                JsonSerializer.Deserialize<int list>(json, jsonOptions)
            Skipped = reader.GetBoolean(5)
        }

    let getAsync (factory: IDbConnectionFactory) (tenantContext: TenantContext) (tenantId: Guid) =
        task {
            use! conn = factory.OpenForTenantAsync(tenantContext)
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                """SELECT tenant_id, current_step, started_at, completed_at, completed_steps, skipped
                   FROM tenant_onboarding WHERE tenant_id = $1"""
            cmd.Parameters.AddWithValue("$1", tenantId) |> ignore
            let! reader = cmd.ExecuteReaderAsync()
            use reader = reader
            let! hasRow = reader.ReadAsync()
            return if hasRow then Some(mapState reader) else None
        }

    let upsertAsync (factory: IDbConnectionFactory) (state: OnboardingState) =
        task {
            let ctx = { TenantId = state.TenantId; UserId = Guid.Empty }
            use! conn = factory.OpenForTenantAsync(ctx)
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                """INSERT INTO tenant_onboarding
                       (tenant_id, current_step, started_at, completed_at, completed_steps, skipped)
                   VALUES ($1, $2, $3, $4, $5, $6)
                   ON CONFLICT (tenant_id) DO UPDATE SET
                       current_step = EXCLUDED.current_step,
                       completed_at = EXCLUDED.completed_at,
                       completed_steps = EXCLUDED.completed_steps,
                       skipped = EXCLUDED.skipped,
                       updated_at = NOW()"""
            cmd.Parameters.AddWithValue("$1", state.TenantId) |> ignore
            cmd.Parameters.AddWithValue("$2", state.CurrentStep) |> ignore
            cmd.Parameters.AddWithValue("$3", state.StartedAt.UtcDateTime) |> ignore
            match state.CompletedAt with
            | Some dt -> cmd.Parameters.AddWithValue("$4", dt.UtcDateTime) |> ignore
            | None -> cmd.Parameters.AddWithValue("$4", DBNull.Value) |> ignore
            let stepsJson = JsonSerializer.Serialize(state.CompletedSteps, jsonOptions)
            let stepsParam = cmd.CreateParameter()
            stepsParam.ParameterName <- "$5"
            stepsParam.NpgsqlDbType <- NpgsqlTypes.NpgsqlDbType.Jsonb
            stepsParam.Value <- stepsJson
            cmd.Parameters.Add(stepsParam) |> ignore
            cmd.Parameters.AddWithValue("$6", state.Skipped) |> ignore
            let! _ = cmd.ExecuteNonQueryAsync()
            return ()
        }

    let createInitialAsync (factory: IDbConnectionFactory) (tenantId: Guid) =
        task {
            let ctx = { TenantId = tenantId; UserId = Guid.Empty }
            use! conn = factory.OpenForTenantAsync(ctx)
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                """INSERT INTO tenant_onboarding
                       (tenant_id, current_step, started_at, completed_at, completed_steps, skipped)
                   VALUES ($1, 2, NOW(), NULL, '[1, 2]'::jsonb, FALSE)
                   ON CONFLICT (tenant_id) DO NOTHING"""
            cmd.Parameters.AddWithValue("$1", tenantId) |> ignore
            let! _ = cmd.ExecuteNonQueryAsync()
            return ()
        }

    let create (factory: IDbConnectionFactory) (accessor: ITenantContextAccessor) : IOnboardingRepository =
        let requireCtx () =
            match accessor.Context with
            | Some ctx -> ctx
            | None -> failwith "No tenant context available for the current operation"

        { new IOnboardingRepository with
            member _.GetAsync(tenantId) = getAsync factory (requireCtx()) tenantId
            member _.UpsertAsync(state) = upsertAsync factory state
            member _.CreateInitialAsync(tenantId) = createInitialAsync factory tenantId
        }
