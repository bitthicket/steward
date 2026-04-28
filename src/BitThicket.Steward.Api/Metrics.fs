namespace BitThicket.Steward.Api

open System
open System.Collections.Concurrent
open System.Text

/// Lightweight in-memory metrics registry that exposes Prometheus text format.
type MetricsState() =
    let requests = ConcurrentDictionary<string, int64>()
    let syncEvents = ConcurrentDictionary<string, int64>()
    let feedHealth = ConcurrentDictionary<string, float>()
    let dbQueryDurations = ConcurrentDictionary<string, float>()
    let dbQueryCounts = ConcurrentDictionary<string, int64>()

    static let makeKey (labels: (string * string) list) =
        labels
        |> List.map (fun (k, v) ->
            let safe = v.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n")
            $"{k}=\"{safe}\"")
        |> String.concat ","

    /// Increment the requests_total counter.
    member _.IncRequest(route: string, statusCode: int) =
        let k = makeKey [ "route", route; "status", string statusCode ]
        requests.AddOrUpdate(k, 1L, fun _ v -> v + 1L) |> ignore

    /// Increment the sync_events_total counter.
    member _.IncSyncEvent(provider: string, outcome: string) =
        let k = makeKey [ "provider", provider; "outcome", outcome ]
        syncEvents.AddOrUpdate(k, 1L, fun _ v -> v + 1L) |> ignore

    /// Set the feed_health_status gauge.
    member _.SetFeedHealth(provider: string, healthStatus: string, value: float) =
        let k = makeKey [ "provider", provider; "status", healthStatus ]
        feedHealth.[k] <- value

    /// Record a DB query duration observation.
    member _.RecordDbQuery(repo: string, op: string, seconds: float) =
        let k = makeKey [ "repo", repo; "op", op ]
        dbQueryDurations.AddOrUpdate(k, seconds, fun _ v -> v + seconds) |> ignore
        dbQueryCounts.AddOrUpdate(k, 1L, fun _ v -> v + 1L) |> ignore

    /// Render all metrics in Prometheus exposition format.
    member _.Format() : string =
        let sb = StringBuilder()

        if not requests.IsEmpty then
            sb.AppendLine("# HELP requests_total Total HTTP requests") |> ignore
            sb.AppendLine("# TYPE requests_total counter") |> ignore
            for KeyValue(k, v) in requests do
                sb.AppendLine($"requests_total{{{k}}} {v}") |> ignore

        if not syncEvents.IsEmpty then
            sb.AppendLine("# HELP sync_events_total Total sync events") |> ignore
            sb.AppendLine("# TYPE sync_events_total counter") |> ignore
            for KeyValue(k, v) in syncEvents do
                sb.AppendLine($"sync_events_total{{{k}}} {v}") |> ignore

        if not feedHealth.IsEmpty then
            sb.AppendLine("# HELP feed_health_status Feed health status gauge") |> ignore
            sb.AppendLine("# TYPE feed_health_status gauge") |> ignore
            for KeyValue(k, v) in feedHealth do
                sb.AppendLine($"feed_health_status{{{k}}} {v:F1}") |> ignore

        if not dbQueryCounts.IsEmpty then
            sb.AppendLine("# HELP db_query_duration_seconds DB query duration summary") |> ignore
            sb.AppendLine("# TYPE db_query_duration_seconds summary") |> ignore
            for KeyValue(k, count) in dbQueryCounts do
                let sum = dbQueryDurations.[k]
                sb.AppendLine($"db_query_duration_seconds_sum{{{k}}} {sum:F6}") |> ignore
                sb.AppendLine($"db_query_duration_seconds_count{{{k}}} {count}") |> ignore

        sb.ToString()

/// Global metrics instance for the API process.
module Metrics =
    let state = MetricsState()
