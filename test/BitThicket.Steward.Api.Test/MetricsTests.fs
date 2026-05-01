module BitThicket.Steward.Api.Test.MetricsTests

open System
open Xunit
open Swensen.Unquote
open BitThicket.Steward.Api

// ── Tests ────────────────────────────────────────────────────────────────────

type MetricsTests() =

    [<Fact>]
    member _.``MetricsState.Format returns valid Prometheus text``() =
        let state = MetricsState()
        state.IncRequest("/api/accounts", 200)
        state.IncRequest("/api/accounts", 200)
        state.IncRequest("/api/accounts", 404)
        state.IncSyncEvent("plaid", "success")
        state.IncSyncEvent("akoya", "failure")
        state.SetFeedHealth("plaid", "healthy", 1.0)
        state.RecordDbQuery("transactions", "list", 0.042)
        state.RecordDbQuery("transactions", "list", 0.038)

        let output = state.Format()

        test <@ output.Contains("# HELP requests_total Total HTTP requests") @>
        test <@ output.Contains("# TYPE requests_total counter") @>
        test <@ output.Contains("requests_total{route=\"/api/accounts\",status=\"200\"} 2") @>
        test <@ output.Contains("requests_total{route=\"/api/accounts\",status=\"404\"} 1") @>

        test <@ output.Contains("# HELP sync_events_total Total sync events") @>
        test <@ output.Contains("# TYPE sync_events_total counter") @>
        test <@ output.Contains("sync_events_total{provider=\"plaid\",outcome=\"success\"} 1") @>
        test <@ output.Contains("sync_events_total{provider=\"akoya\",outcome=\"failure\"} 1") @>

        test <@ output.Contains("# HELP feed_health_status Feed health status gauge") @>
        test <@ output.Contains("# TYPE feed_health_status gauge") @>
        test <@ output.Contains("feed_health_status{provider=\"plaid\",status=\"healthy\"} 1.0") @>

        test <@ output.Contains("# HELP db_query_duration_seconds DB query duration summary") @>
        test <@ output.Contains("# TYPE db_query_duration_seconds summary") @>
        test <@ output.Contains("db_query_duration_seconds_sum{repo=\"transactions\",op=\"list\"}") @>
        test <@ output.Contains("db_query_duration_seconds_count{repo=\"transactions\",op=\"list\"} 2") @>

    [<Fact>]
    member _.``MetricsState.Format handles empty registry``() =
        let state = MetricsState()
        let output = state.Format()
        test <@ output = "" @>

    [<Fact>]
    member _.``MetricsState.IncRequest is thread-safe``() =
        let state = MetricsState()
        let tasks =
            [ for _ in 1 .. 100 ->
                async { state.IncRequest("/test", 200) } ]
        tasks |> Async.Parallel |> Async.RunSynchronously |> ignore

        let output = state.Format()
        test <@ output.Contains("requests_total{route=\"/test\",status=\"200\"} 100") @>

    [<Fact>]
    member _.``MetricsState escapes label values with quotes``() =
        let state = MetricsState()
        state.IncRequest("/test\"path", 200)
        let output = state.Format()
        test <@ output.Contains("route=\"/test\\\"path\"") @>
