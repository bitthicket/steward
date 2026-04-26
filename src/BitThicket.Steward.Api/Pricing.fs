namespace BitThicket.Steward.Pricing

open System
open System.Net.Http
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Npgsql

type SpotPrice = {
    Base: string
    Quote: string
    AsOf: DateTimeOffset
    Value: decimal
    Source: string
    FetchedAt: DateTimeOffset
}

type IPriceProvider =
    abstract GetSpotAsync : baseCurrency: string * quoteCurrency: string -> Task<SpotPrice>
    abstract GetHistoricalAsync : baseCurrency: string * quoteCurrency: string * asOf: DateTimeOffset -> Task<SpotPrice option>

// Token bucket rate limiter. Capacity = max burst, refillPerSecond = sustained rate.
// CoinGecko free tier: 50 calls/min → capacity=10 (small burst), refill=50/60 per second.
type TokenBucket(capacity: int, refillPerSecond: float) =
    let mutable tokens = float capacity
    let mutable lastRefill = DateTime.UtcNow
    let gate = new SemaphoreSlim(1, 1)

    member _.WaitAsync(ct: CancellationToken) : Task<unit> =
        task {
            let mutable acquired = false
            while not acquired do
                do! gate.WaitAsync(ct)
                try
                    let now = DateTime.UtcNow
                    let elapsed = (now - lastRefill).TotalSeconds
                    tokens <- Math.Min(float capacity, tokens + elapsed * refillPerSecond)
                    lastRefill <- now
                    if tokens >= 1.0 then
                        tokens <- tokens - 1.0
                        acquired <- true
                finally
                    gate.Release() |> ignore
                if not acquired then
                    do! Task.Delay(200, ct)
        }

// Currency code → CoinGecko coin ID for supported assets
module private CoinGeckoIds =
    let map = Map.ofList [ ("BTC", "bitcoin"); ("ETH", "ethereum") ]

type CoinGeckoPriceProvider(http: HttpClient, db: NpgsqlDataSource, logger: ILogger<CoinGeckoPriceProvider>) =
    let bucket = TokenBucket(10, 50.0 / 60.0)

    let tryGetCached (baseCurr: string) (quoteCurr: string) (asOf: DateTime) (spotOnly: bool) =
        task {
            use cmd = db.CreateCommand()
            cmd.CommandText <-
                if spotOnly then
                    // For spot: only return if fetched within the last 60 seconds
                    """SELECT value, source, fetched_at
                       FROM prices
                       WHERE base = $1 AND quote = $2 AND as_of = $3
                       AND fetched_at > now() - interval '60 seconds'"""
                else
                    // For historical: cache forever (immutable data)
                    """SELECT value, source, fetched_at
                       FROM prices
                       WHERE base = $1 AND quote = $2 AND as_of = $3"""
            cmd.Parameters.AddWithValue("$1", baseCurr) |> ignore
            cmd.Parameters.AddWithValue("$2", quoteCurr) |> ignore
            cmd.Parameters.AddWithValue("$3", asOf) |> ignore
            let! reader = cmd.ExecuteReaderAsync()
            use reader = reader
            let! hasRow = reader.ReadAsync()
            if hasRow then
                let value = reader.GetDecimal(0)
                let source = reader.GetString(1)
                let fetchedAt = DateTimeOffset(reader.GetDateTime(2), TimeSpan.Zero)
                return Some {
                    Base = baseCurr
                    Quote = quoteCurr
                    AsOf = DateTimeOffset(asOf, TimeSpan.Zero)
                    Value = value
                    Source = source
                    FetchedAt = fetchedAt
                }
            else
                return None
        }

    let persistPrice (price: SpotPrice) =
        task {
            use cmd = db.CreateCommand()
            cmd.CommandText <-
                """INSERT INTO prices (base, quote, as_of, value, source, fetched_at)
                   VALUES ($1, $2, $3, $4, $5, $6)
                   ON CONFLICT (base, quote, as_of) DO UPDATE
                   SET value = EXCLUDED.value,
                       source = EXCLUDED.source,
                       fetched_at = EXCLUDED.fetched_at"""
            cmd.Parameters.AddWithValue("$1", price.Base) |> ignore
            cmd.Parameters.AddWithValue("$2", price.Quote) |> ignore
            cmd.Parameters.AddWithValue("$3", price.AsOf.UtcDateTime) |> ignore
            cmd.Parameters.AddWithValue("$4", price.Value) |> ignore
            cmd.Parameters.AddWithValue("$5", price.Source) |> ignore
            cmd.Parameters.AddWithValue("$6", price.FetchedAt.UtcDateTime) |> ignore
            let! _ = cmd.ExecuteNonQueryAsync()
            ()
        }

    let fetchSpotFromApi (coinId: string) (quoteCurr: string) : Task<decimal> =
        task {
            do! bucket.WaitAsync(CancellationToken.None)
            let quoteLower = quoteCurr.ToLowerInvariant()
            let url = $"https://api.coingecko.com/api/v3/simple/price?ids={coinId}&vs_currencies={quoteLower}"
            let! json = http.GetStringAsync(url)
            use doc = JsonDocument.Parse(json)
            return doc.RootElement.GetProperty(coinId).GetProperty(quoteLower).GetDecimal()
        }

    let fetchHistoricalFromApi (coinId: string) (quoteCurr: string) (asOf: DateTimeOffset) : Task<decimal option> =
        task {
            do! bucket.WaitAsync(CancellationToken.None)
            let dateStr = asOf.ToString("dd-MM-yyyy")
            let quoteLower = quoteCurr.ToLowerInvariant()
            let url = $"https://api.coingecko.com/api/v3/coins/{coinId}/history?date={dateStr}&localization=false"
            let! json = http.GetStringAsync(url)
            use doc = JsonDocument.Parse(json)
            match doc.RootElement.TryGetProperty("market_data") with
            | true, marketData ->
                match marketData.TryGetProperty("current_price") with
                | true, prices ->
                    match prices.TryGetProperty(quoteLower) with
                    | true, el -> return Some(el.GetDecimal())
                    | _ -> return None
                | _ -> return None
            | _ -> return None
        }

    interface IPriceProvider with
        member _.GetSpotAsync(baseCurr, quoteCurr) =
            task {
                // Snap to start of current minute for the 60-second cache bucket
                let now = DateTime.UtcNow
                let asOf = DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, DateTimeKind.Utc)

                match! tryGetCached baseCurr quoteCurr asOf true with
                | Some cached ->
                    logger.LogDebug("Spot cache hit {Base}/{Quote}", baseCurr, quoteCurr)
                    return cached
                | None ->
                    logger.LogDebug("Spot cache miss {Base}/{Quote} — fetching from CoinGecko", baseCurr, quoteCurr)
                    match Map.tryFind baseCurr CoinGeckoIds.map with
                    | None -> return failwith $"No CoinGecko mapping for currency: {baseCurr}"
                    | Some coinId ->
                        let! value = fetchSpotFromApi coinId quoteCurr
                        let price = {
                            Base = baseCurr
                            Quote = quoteCurr
                            AsOf = DateTimeOffset(asOf, TimeSpan.Zero)
                            Value = value
                            Source = "coingecko"
                            FetchedAt = DateTimeOffset.UtcNow
                        }
                        do! persistPrice price
                        return price
            }

        member _.GetHistoricalAsync(baseCurr, quoteCurr, asOf) =
            task {
                // Normalize to UTC day start — historical prices are immutable per day
                let dayStart = DateTime(asOf.Year, asOf.Month, asOf.Day, 0, 0, 0, DateTimeKind.Utc)

                match! tryGetCached baseCurr quoteCurr dayStart false with
                | Some cached ->
                    logger.LogDebug("Historical cache hit {Base}/{Quote} {Date}", baseCurr, quoteCurr, dayStart.ToShortDateString())
                    return Some cached
                | None ->
                    logger.LogDebug("Historical cache miss {Base}/{Quote} {Date} — fetching from CoinGecko", baseCurr, quoteCurr, dayStart.ToShortDateString())
                    match Map.tryFind baseCurr CoinGeckoIds.map with
                    | None -> return None
                    | Some coinId ->
                        match! fetchHistoricalFromApi coinId quoteCurr asOf with
                        | None -> return None
                        | Some value ->
                            let price = {
                                Base = baseCurr
                                Quote = quoteCurr
                                AsOf = DateTimeOffset(dayStart, TimeSpan.Zero)
                                Value = value
                                Source = "coingecko"
                                FetchedAt = DateTimeOffset.UtcNow
                            }
                            do! persistPrice price
                            return Some price
            }
