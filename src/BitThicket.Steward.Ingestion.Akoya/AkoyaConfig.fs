namespace BitThicket.Steward.Ingestion.Akoya

open System

/// Akoya ingestion service configuration, read from environment.
type AkoyaConfig = {
    /// Base URL of the Steward Core API (e.g. https://api.steward.internal)
    StewardApiBaseUrl: string
    /// Service-to-service bearer token for Core API internal endpoints.
    StewardServiceToken: string
    /// Akoya OAuth client ID.
    ClientId: string
    /// Akoya OAuth client secret.
    ClientSecret: string
    /// Akoya environment: "sandbox" or "production".
    Env: string
    /// Port to bind the HTTP server on.
    Port: string
}

module AkoyaConfig =
    let fromEnvironment () : AkoyaConfig =
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
            ClientId = require "AKOYA_CLIENT_ID"
            ClientSecret = require "AKOYA_CLIENT_SECRET"
            Env = optional "AKOYA_ENV" "sandbox"
            Port = optional "PORT" "8080"
        }

    /// Derives the Akoya FDX base URL from the environment flag.
    let fdxBaseUrl (config: AkoyaConfig) =
        match config.Env.ToLowerInvariant() with
        | "production" -> "https://api.akoya.com"
        | _ -> "https://sandbox-idp.akoya.com"
