namespace BitThicket.Steward.Ingestion.Plaid

open System

/// Plaid ingestion service configuration, read from environment.
type PlaidConfig = {
    /// Base URL of the Steward Core API (e.g. https://api.steward.internal)
    StewardApiBaseUrl: string
    /// Service-to-service bearer token for Core API internal endpoints.
    StewardServiceToken: string
    /// Plaid client ID (global app credential).
    ClientId: string
    /// Plaid secret (global app credential).
    Secret: string
    /// Plaid environment: "sandbox", "development", or "production".
    Env: string
    /// Port to bind the HTTP server on.
    Port: string
    /// Whether to use the stubbed Plaid client instead of real HTTP calls.
    UseStub: bool
}

module PlaidConfig =
    let fromEnvironment () : PlaidConfig =
        let require name =
            match Environment.GetEnvironmentVariable(name) with
            | null | "" -> failwith $"{name} is not set"
            | v -> v

        let optional name defaultValue =
            match Environment.GetEnvironmentVariable(name) with
            | null | "" -> defaultValue
            | v -> v

        {
            StewardApiBaseUrl = require "STEWARD_API_BASE_URL"
            StewardServiceToken = require "STEWARD_SERVICE_TOKEN"
            ClientId = require "PLAID_CLIENT_ID"
            Secret = require "PLAID_SECRET"
            Env = optional "PLAID_ENV" "sandbox"
            Port = optional "PORT" "8080"
            UseStub = (optional "PLAID_USE_STUB" "false").ToLowerInvariant() = "true"
        }

    /// Derives the Plaid API base URL from the environment flag.
    let plaidBaseUrl (config: PlaidConfig) =
        match config.Env.ToLowerInvariant() with
        | "production" -> "https://production.plaid.com"
        | "development" -> "https://development.plaid.com"
        | _ -> "https://sandbox.plaid.com"
