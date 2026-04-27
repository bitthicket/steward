module BitThicket.Steward.Api.Test.FeedHealthWorkerTests

open System
open Xunit
open Swensen.Unquote
open BitThicket.Steward.Api
open BitThicket.Steward.Api.Domain

let private now = DateTimeOffset(2026, 4, 26, 12, 0, 0, TimeSpan.Zero)
let private connId = Guid.NewGuid()
let private tenantId = Guid.NewGuid()

let private successEvent (minutesAgo: int) =
    { Id = Guid.NewGuid()
      TenantId = tenantId
      ConnectionId = connId
      StartedAt = now.AddMinutes(-float minutesAgo)
      CompletedAt = Some(now.AddMinutes(-float minutesAgo + 1.0))
      Status = SyncStatus.Success
      TransactionsAdded = 1
      TransactionsUpdated = 0 }

let private failedEvent (minutesAgo: int) =
    { Id = Guid.NewGuid()
      TenantId = tenantId
      ConnectionId = connId
      StartedAt = now.AddMinutes(-float minutesAgo)
      CompletedAt = Some(now.AddMinutes(-float minutesAgo + 1.0))
      Status = SyncStatus.Failed("timeout")
      TransactionsAdded = 0
      TransactionsUpdated = 0 }

let private partialEvent (minutesAgo: int) =
    { Id = Guid.NewGuid()
      TenantId = tenantId
      ConnectionId = connId
      StartedAt = now.AddMinutes(-float minutesAgo)
      CompletedAt = Some(now.AddMinutes(-float minutesAgo + 1.0))
      Status = SyncStatus.PartialSuccess(["rate limited"])
      TransactionsAdded = 1
      TransactionsUpdated = 0 }

[<Fact>]
let ``Unknown when no sync events``() =
    let health = FeedHealthService.evaluateConnectionHealth connId tenantId [] None now
    test <@ health.Level = FeedHealthLevel.Unknown @>
    test <@ health.ConsecutiveFailures = 0 @>
    test <@ health.LastSuccessAt = None @>
    test <@ health.LastFailureAt = None @>

[<Fact>]
let ``Healthy after recent success with zero failures``() =
    let events = [ successEvent 30 ]
    let health = FeedHealthService.evaluateConnectionHealth connId tenantId events None now
    test <@ health.Level = FeedHealthLevel.Healthy @>
    test <@ health.ConsecutiveFailures = 0 @>
    test <@ health.LastSuccessAt = Some(events.[0].StartedAt) @>

[<Fact>]
let ``Degraded after one failure``() =
    let events = [ failedEvent 10; successEvent 60 ]
    let health = FeedHealthService.evaluateConnectionHealth connId tenantId events None now
    test <@ health.Level = FeedHealthLevel.Degraded @>
    test <@ health.ConsecutiveFailures = 1 @>

[<Fact>]
let ``Degraded after three consecutive failures``() =
    let events = [ failedEvent 10; failedEvent 20; failedEvent 30; successEvent 60 ]
    let health = FeedHealthService.evaluateConnectionHealth connId tenantId events None now
    test <@ health.Level = FeedHealthLevel.Degraded @>
    test <@ health.ConsecutiveFailures = 3 @>

[<Fact>]
let ``Failing after four consecutive failures``() =
    let events = [ failedEvent 10; failedEvent 20; failedEvent 30; failedEvent 40; successEvent 90 ]
    let health = FeedHealthService.evaluateConnectionHealth connId tenantId events None now
    test <@ health.Level = FeedHealthLevel.Failing @>
    test <@ health.ConsecutiveFailures = 4 @>

[<Fact>]
let ``Failing when last success is very old``() =
    let events = [ successEvent 300 ] // 5 hours ago, > 4x default 1h frequency
    let health = FeedHealthService.evaluateConnectionHealth connId tenantId events None now
    test <@ health.Level = FeedHealthLevel.Failing @>

[<Fact>]
let ``Degraded when last success is stale but no failures``() =
    let events = [ successEvent 150 ] // 2.5 hours ago, between 2x and 4x
    let health = FeedHealthService.evaluateConnectionHealth connId tenantId events None now
    test <@ health.Level = FeedHealthLevel.Degraded @>
    test <@ health.ConsecutiveFailures = 0 @>

[<Fact>]
let ``PartialSuccess counts as failure``() =
    let events = [ partialEvent 10; successEvent 60 ]
    let health = FeedHealthService.evaluateConnectionHealth connId tenantId events None now
    test <@ health.Level = FeedHealthLevel.Degraded @>
    test <@ health.ConsecutiveFailures = 1 @>

[<Fact>]
let ``Open remediation attempt is preserved``() =
    let openId = Guid.NewGuid()
    let events = [ successEvent 30 ]
    let health = FeedHealthService.evaluateConnectionHealth connId tenantId events (Some openId) now
    test <@ health.OpenRemediationAttemptId = Some openId @>

[<Fact>]
let ``Consecutive failures reset after success``() =
    // Most recent is success, so consecutive failures should be 0
    let events = [ successEvent 10; failedEvent 20; failedEvent 30 ]
    let health = FeedHealthService.evaluateConnectionHealth connId tenantId events None now
    test <@ health.ConsecutiveFailures = 0 @>
    test <@ health.Level = FeedHealthLevel.Healthy @>
