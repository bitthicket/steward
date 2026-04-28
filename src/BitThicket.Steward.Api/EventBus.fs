namespace BitThicket.Steward.Api

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Microsoft.Extensions.Logging

/// A serialised envelope that travels through the in-process bus.
/// Consumers decode the JSON payload themselves.
type EventEnvelope =
    { Topic: string
      JsonPayload: string
      OccurredAt: DateTimeOffset
      CausationId: Guid option }

/// Minimal in-process message bus.  Subscribers per topic are invoked in
/// parallel; if one throws the remaining still run and the error is logged.
type IEventBus =
    abstract Publish: envelope:EventEnvelope -> Task
    abstract Subscribe: topic:string -> handler:(EventEnvelope -> Task) -> IDisposable

module EventBusTopics =
    let syncRequested = "sync.requested"
    let syncCompleted = "sync.completed"
    let connectionStatusChanged = "connection.status_changed"

/// In-process event bus backed by a per-subscriber Channel.
/// Each subscriber gets its own unbounded queue so that slow consumers do
/// not block fast ones.
type InProcessEventBus(logger: ILogger<InProcessEventBus>) =

    let subscribers =
        ConcurrentDictionary<string, ConcurrentDictionary<Guid, Channel<EventEnvelope>>>()

    interface IEventBus with
        member _.Publish(envelope) =
            task {
                match subscribers.TryGetValue(envelope.Topic) with
                | false, _ ->
                    // No subscribers — fire-and-forget drop.  Ingestion
                    // services pull via HTTP in later tickets.
                    logger.LogDebug("No subscribers for topic {Topic}; dropping envelope",
                                    envelope.Topic)
                    return ()
                | true, topicSubs ->
                    let subs = topicSubs.Values |> Seq.toList
                    let mutable delivered = 0
                    for ch in subs do
                        do! ch.Writer.WriteAsync(envelope).AsTask()
                        delivered <- delivered + 1
                    logger.LogDebug(
                        "Published to topic {Topic}: {Delivered}/{Total} subscribers",
                        envelope.Topic, delivered, subs.Length)
            }

        member _.Subscribe topic handler =
            let topicSubs = subscribers.GetOrAdd(topic, fun _ -> ConcurrentDictionary())
            let ch = Channel.CreateUnbounded<EventEnvelope>()
            let subId = Guid.NewGuid()
            topicSubs[subId] <- ch

            let cts = new CancellationTokenSource()

            // Background reader loop for this subscriber.
            task {
                try
                    while not cts.IsCancellationRequested do
                        let! envelope = ch.Reader.ReadAsync(cts.Token).AsTask()
                        try
                            do! handler envelope
                        with ex ->
                            logger.LogError(
                                ex,
                                "Handler failed for topic {Topic}; continuing with other subscribers",
                                topic)
                with
                | :? OperationCanceledException ->
                    logger.LogDebug("Subscriber cancelled for topic {Topic}", topic)
                | ex ->
                    logger.LogError(ex, "Subscriber reader crashed for topic {Topic}", topic)
            }
            |> ignore

            logger.LogDebug(
                "Subscribed to topic {Topic}; total subscribers: {Count}",
                topic, topicSubs.Count)

            { new IDisposable with
                member _.Dispose() =
                    cts.Cancel()
                    cts.Dispose()
                    topicSubs.TryRemove(subId) |> ignore
                    if topicSubs.IsEmpty then
                        subscribers.TryRemove(topic) |> ignore
                    logger.LogDebug(
                        "Unsubscribed from topic {Topic}; remaining subscribers: {Count}",
                        topic, max 0 (topicSubs.Count - 1)) }
