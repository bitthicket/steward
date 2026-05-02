namespace BitThicket.Steward.Api

open System
open System.Threading.Tasks
open Npgsql
open BitThicket.Steward.Api.Domain

// ─────────────────────────────────────────────────────────────────────────────
// UserPreferences repository
// ─────────────────────────────────────────────────────────────────────────────

type IUserPreferencesRepository =
    abstract GetAsync : userId: Guid * tenantId: Guid -> Task<UserPreferences option>
    abstract UpsertAsync : prefs: UserPreferences -> Task<unit>

module UserPreferencesRepository =

    let private mapPreferences (reader: System.Data.Common.DbDataReader) : UserPreferences =
        {
            UserId = reader.GetGuid(0)
            TenantId = reader.GetGuid(1)
            DefaultCurrencyCode = reader.GetString(2)
            DefaultBudgetingStyle =
                match reader.GetString(3).ToLowerInvariant() with
                | "zerobased" | "zero_based" -> BudgetingStyle.ZeroBased
                | "envelope" -> BudgetingStyle.Envelope
                | "flexible" -> BudgetingStyle.Flexible
                | _ -> BudgetingStyle.TraditionalLimits
            PreferredSyncFrequency =
                // DbDataReader.GetTimeSpan may not be available on all targets;
                // read as string and parse from PostgreSQL interval text.
                match reader.GetValue(4) with
                | :? TimeSpan as ts -> ts
                | :? string as s -> TimeSpan.Parse(s)
                | _ -> TimeSpan.FromHours(1.0)
        }

    let create (factory: IDbConnectionFactory) (accessor: ITenantContextAccessor) : IUserPreferencesRepository =
        { new IUserPreferencesRepository with
            member _.GetAsync(userId, tenantId) =
                task {
                        let! conn = factory.OpenForTenantAsync({ TenantId = tenantId; UserId = userId })
                        use cmd = conn.CreateCommand()
                        cmd.CommandText <-
                            """SELECT user_id, tenant_id, default_currency_code, default_budgeting_style, preferred_sync_frequency
                               FROM user_preferences
                               WHERE user_id = $1 AND tenant_id = $2"""
                        cmd.Parameters.AddWithValue("$1", userId) |> ignore
                        cmd.Parameters.AddWithValue("$2", tenantId) |> ignore
                        let! reader = cmd.ExecuteReaderAsync()
                        use reader = reader
                        let! hasRow = reader.ReadAsync()
                        return if hasRow then Some(mapPreferences reader) else None
                    }

            member _.UpsertAsync(prefs) =
                task {
                    let! conn = factory.OpenForTenantAsync({ TenantId = prefs.TenantId; UserId = prefs.UserId })
                    use cmd = conn.CreateCommand()
                    cmd.CommandText <-
                        """INSERT INTO user_preferences
                             (user_id, tenant_id, default_currency_code, default_budgeting_style, preferred_sync_frequency, updated_at)
                           VALUES ($1, $2, $3, $4, $5, now())
                           ON CONFLICT (user_id, tenant_id) DO UPDATE
                           SET default_currency_code = EXCLUDED.default_currency_code,
                               default_budgeting_style = EXCLUDED.default_budgeting_style,
                               preferred_sync_frequency = EXCLUDED.preferred_sync_frequency,
                               updated_at = now()"""
                    cmd.Parameters.AddWithValue("$1", prefs.UserId) |> ignore
                    cmd.Parameters.AddWithValue("$2", prefs.TenantId) |> ignore
                    cmd.Parameters.AddWithValue("$3", prefs.DefaultCurrencyCode.ToUpperInvariant()) |> ignore
                    let styleStr =
                        match prefs.DefaultBudgetingStyle with
                        | BudgetingStyle.ZeroBased -> "zerobased"
                        | BudgetingStyle.Envelope -> "envelope"
                        | BudgetingStyle.Flexible -> "flexible"
                        | BudgetingStyle.TraditionalLimits -> "traditionallimits"
                    cmd.Parameters.AddWithValue("$4", styleStr) |> ignore
                    cmd.Parameters.AddWithValue("$5", prefs.PreferredSyncFrequency) |> ignore
                    let! _ = cmd.ExecuteNonQueryAsync()
                    ()
                }
        }
