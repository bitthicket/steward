namespace BitThicket.Steward.Api

open System
open System.IO
open System.Text.Json
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Falco
open BitThicket.Steward.Api.Domain

// ── DTOs ────────────────────────────────────────────────────────────────────

type PreferencesResponse = {
    defaultDisplayCurrency: string
    defaultBudgetingStyle: string
    preferredSyncFrequencyMinutes: int
}

type UpdatePreferencesRequest = {
    defaultDisplayCurrency: string option
    defaultBudgetingStyle: string option
    preferredSyncFrequencyMinutes: int option
}

// ── JSON helpers ───────────────────────────────────────────────────────────

module private PreferencesJson =
    let readBody (ctx: HttpContext) =
        task {
            use reader = new StreamReader(ctx.Request.Body, System.Text.Encoding.UTF8)
            let! json = reader.ReadToEndAsync()
            return JsonDocument.Parse(json)
        }

    let jsonOptions =
        let opts = JsonSerializerOptions()
        opts.PropertyNamingPolicy <- JsonNamingPolicy.CamelCase
        opts

    let deserialize<'T> (doc: JsonDocument) = JsonSerializer.Deserialize<'T>(doc, jsonOptions)

// ── Domain helpers ─────────────────────────────────────────────────────────

module private PreferencesHelpers =
    let parseBudgetingStyle (s: string) : BudgetingStyle option =
        match s.ToLowerInvariant() with
        | "zerobased" | "zero_based" -> Some BudgetingStyle.ZeroBased
        | "envelope" -> Some BudgetingStyle.Envelope
        | "flexible" -> Some BudgetingStyle.Flexible
        | "traditionallimits" | "traditional_limits" -> Some BudgetingStyle.TraditionalLimits
        | _ -> None

    let budgetingStyleToString (s: BudgetingStyle) : string =
        match s with
        | BudgetingStyle.ZeroBased -> "zeroBased"
        | BudgetingStyle.Envelope -> "envelope"
        | BudgetingStyle.Flexible -> "flexible"
        | BudgetingStyle.TraditionalLimits -> "traditionalLimits"

    let toResponse (prefs: UserPreferences) : PreferencesResponse =
        {
            defaultDisplayCurrency = prefs.DefaultCurrencyCode
            defaultBudgetingStyle = budgetingStyleToString prefs.DefaultBudgetingStyle
            preferredSyncFrequencyMinutes = int prefs.PreferredSyncFrequency.TotalMinutes
        }

    let clampSyncFrequency (minutes: int) : TimeSpan =
        let clamped = Math.Max(15, Math.Min(1440, minutes))
        TimeSpan.FromMinutes(float clamped)

// ── Endpoints ──────────────────────────────────────────────────────────────

module UserPreferencesEndpoints =
    open PreferencesHelpers

    // GET /api/preferences
    let getPreferencesHandler : HttpHandler = fun ctx ->
        task {
            let repo = ctx.RequestServices.GetRequiredService<IUserPreferencesRepository>()
            let accessor = ctx.RequestServices.GetRequiredService<ITenantContextAccessor>()
            match accessor.Context with
            | None ->
                ctx.Response.StatusCode <- 401
                do! Response.ofJson {| error = "Unauthorized" |} ctx
            | Some tc ->
                let! prefsOpt = repo.GetAsync(tc.UserId, tc.TenantId)
                let resp =
                    match prefsOpt with
                    | Some prefs -> toResponse prefs
                    | None ->
                        // Return sensible defaults when no row exists yet
                        {
                            defaultDisplayCurrency = "USD"
                            defaultBudgetingStyle = "flexible"
                            preferredSyncFrequencyMinutes = 60
                        }
                do! Response.ofJson resp ctx
        }

    // PATCH /api/preferences
    let updatePreferencesHandler : HttpHandler = fun ctx ->
        task {
            let repo = ctx.RequestServices.GetRequiredService<IUserPreferencesRepository>()
            let accessor = ctx.RequestServices.GetRequiredService<ITenantContextAccessor>()
            let! doc = PreferencesJson.readBody ctx
            let req = PreferencesJson.deserialize<UpdatePreferencesRequest> doc

            match accessor.Context with
            | None ->
                ctx.Response.StatusCode <- 401
                do! Response.ofJson {| error = "Unauthorized" |} ctx
            | Some tc ->
                let! existingOpt = repo.GetAsync(tc.UserId, tc.TenantId)

                let existing =
                    match existingOpt with
                    | Some e -> e
                    | None ->
                        {
                            UserId = tc.UserId
                            TenantId = tc.TenantId
                            DefaultCurrencyCode = "USD"
                            DefaultBudgetingStyle = BudgetingStyle.Flexible
                            PreferredSyncFrequency = TimeSpan.FromHours(1.0)
                        }

                let updated =
                    {
                        existing with
                            DefaultCurrencyCode =
                                req.defaultDisplayCurrency
                                |> Option.map (fun c -> c.ToUpperInvariant())
                                |> Option.defaultValue existing.DefaultCurrencyCode
                            DefaultBudgetingStyle =
                                req.defaultBudgetingStyle
                                |> Option.bind parseBudgetingStyle
                                |> Option.defaultValue existing.DefaultBudgetingStyle
                            PreferredSyncFrequency =
                                req.preferredSyncFrequencyMinutes
                                |> Option.map clampSyncFrequency
                                |> Option.defaultValue existing.PreferredSyncFrequency
                    }

                // Validate currency code
                if updated.DefaultCurrencyCode.Length <> 3 then
                    ctx.Response.StatusCode <- 400
                    do! Response.ofJson {| error = "defaultDisplayCurrency must be a 3-character code." |} ctx
                else
                    do! repo.UpsertAsync(updated)
                    do! Response.ofJson (toResponse updated) ctx
        }
