namespace BitThicket.Steward.Pricing

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging

/// Background service that pre-warms the BTC/USD spot price cache every 5 minutes.
/// This ensures the /api/prices endpoint always returns a near-live value without
/// waiting on a CoinGecko round-trip at request time.
type PricingWorker(pricing: IPriceProvider, logger: ILogger<PricingWorker>) =
    inherit BackgroundService()

    override _.ExecuteAsync(ct: CancellationToken) : Task =
        task {
            logger.LogInformation("BTC/USD price worker started — polling every 5 minutes")
            while not ct.IsCancellationRequested do
                try
                    let! price = pricing.GetSpotAsync("BTC", "USD")
                    logger.LogInformation("BTC/USD spot refreshed: {Value} as of {AsOf}", price.Value, price.AsOf)
                with ex ->
                    logger.LogWarning(ex, "BTC/USD spot fetch failed — will retry next cycle")
                try
                    do! Task.Delay(TimeSpan.FromMinutes(5.0), ct)
                with :? OperationCanceledException ->
                    ()
        } :> Task
