module BitThicket.Steward.Api.Test.EventBusTests

open System
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Xunit
open Swensen.Unquote
open BitThicket.Steward.Api

let private nullLogger = Microsoft.Extensions.Logging.Abstractions.NullLogger<InProcessEventBus>.Instance

[<Fact>]
let ``Publish drops envelope when no subscribers`` () =
    let bus = InProcessEventBus(nullLogger) :> IEventBus
    let envelope =
        { Topic = EventBusTopics.syncRequested
          JsonPayload = "{}"
          OccurredAt = DateTimeOffset.UtcNow
          CausationId = None }
    bus.Publish(envelope).Wait()
    test <@ true @> // no exception thrown

[<Fact>]
let ``Subscriber receives published envelope`` () =
    let bus = InProcessEventBus(nullLogger) :> IEventBus
    let tcs = TaskCompletionSource<EventEnvelope>()
    use _sub = bus.Subscribe EventBusTopics.syncRequested (fun env ->
        tcs.SetResult(env)
        Task.CompletedTask)
    let envelope =
        { Topic = EventBusTopics.syncRequested
          JsonPayload = """{"tenantId":"00000000-0000-0000-0000-000000000001"}"""
          OccurredAt = DateTimeOffset.UtcNow
          CausationId = None }
    bus.Publish(envelope).Wait()
    let received = tcs.Task.Result
    test <@ received.Topic = EventBusTopics.syncRequested @>
    test <@ received.JsonPayload = envelope.JsonPayload @>

[<Fact>]
let ``Fan-out delivers to multiple subscribers`` () =
    let bus = InProcessEventBus(nullLogger) :> IEventBus
    let tcs1 = TaskCompletionSource<EventEnvelope>()
    let tcs2 = TaskCompletionSource<EventEnvelope>()
    use _sub1 = bus.Subscribe EventBusTopics.syncRequested (fun env ->
        tcs1.SetResult(env)
        Task.CompletedTask)
    use _sub2 = bus.Subscribe EventBusTopics.syncRequested (fun env ->
        tcs2.SetResult(env)
        Task.CompletedTask)
    let envelope =
        { Topic = EventBusTopics.syncRequested
          JsonPayload = "{}"
          OccurredAt = DateTimeOffset.UtcNow
          CausationId = None }
    bus.Publish(envelope).Wait()
    test <@ tcs1.Task.IsCompletedSuccessfully @>
    test <@ tcs2.Task.IsCompletedSuccessfully @>

[<Fact>]
let ``Failed handler does not block other subscribers`` () =
    let bus = InProcessEventBus(nullLogger) :> IEventBus
    let tcs1 = TaskCompletionSource<unit>()
    let tcs2 = TaskCompletionSource<EventEnvelope>()
    use _sub1 = bus.Subscribe EventBusTopics.syncRequested (fun _ ->
        tcs1.SetResult(())
        Task.FromException(Exception("boom")))
    use _sub2 = bus.Subscribe EventBusTopics.syncRequested (fun env ->
        tcs2.SetResult(env)
        Task.CompletedTask)
    let envelope =
        { Topic = EventBusTopics.syncRequested
          JsonPayload = "{}"
          OccurredAt = DateTimeOffset.UtcNow
          CausationId = None }
    bus.Publish(envelope).Wait()
    // Wait for both handlers to finish (with timeout to avoid hanging)
    Task.WaitAll([| tcs1.Task :> Task; tcs2.Task :> Task |], 1000) |> ignore
    test <@ tcs1.Task.IsCompletedSuccessfully @>
    test <@ tcs2.Task.IsCompletedSuccessfully @>

[<Fact>]
let ``Unsubscribe removes subscriber`` () =
    let bus = InProcessEventBus(nullLogger) :> IEventBus
    let mutable received = false
    let sub = bus.Subscribe EventBusTopics.syncRequested (fun _ ->
        received <- true
        Task.CompletedTask)
    sub.Dispose()
    let envelope =
        { Topic = EventBusTopics.syncRequested
          JsonPayload = "{}"
          OccurredAt = DateTimeOffset.UtcNow
          CausationId = None }
    bus.Publish(envelope).Wait()
    test <@ received = false @>
