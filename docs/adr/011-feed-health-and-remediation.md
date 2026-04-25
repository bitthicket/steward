# ADR-011: Feed health and remediation primitives

## Status

Proposed (placeholder — full design deferred)

## Context

Requirement 10 is unusually strong: "the AI-enabled parts of our platform do absolutely everything possible to eliminate that burden entirely." Today the model exposes `ConnectionStatus` (`Active | NeedsReauth | Disabled | Error`) and `SyncEvent` (per-sync observability), but there is no domain-level handle for an agent to (a) reason about the long-term health of a feed and (b) record a remediation attempt. Without those handles, an agent has nowhere to write down "I tried to refresh credentials and it failed because of a CAPTCHA prompt", and the next agent loop has no memory of what was tried.

This ADR establishes the shape of the missing primitives so they can be used as anchors in subsequent design and implementation issues. It does not finalize the full design of the remediation system.

## Decision (placeholder)

Add two named gaps to the domain model. The shapes below are the intended starting point; details may shift when the remediation flow is fully designed in a follow-up ADR.

### `FeedHealth`

A coarse, per-`DataFeedConnection` projection that an agent or operator can read without scanning every `SyncEvent`. Computed from recent sync history and current `ConnectionStatus`.

```fsharp
type FeedHealthLevel =
    | Healthy
    | Degraded             // recent failures, but recovering
    | Failing              // sustained failures, user action likely needed
    | Unknown              // never synced or insufficient data

type FeedHealth = {
    ConnectionId: Guid
    Level: FeedHealthLevel
    LastSuccessAt: DateTimeOffset option
    LastFailureAt: DateTimeOffset option
    ConsecutiveFailures: int
    OpenRemediationAttemptId: Guid option
    EvaluatedAt: DateTimeOffset
}
```

### `RemediationAttempt`

An append-only record of an attempt to recover a broken or degraded feed connection. Either a human or an agent can be the actor. Outcomes feed into `FeedHealth` evaluation.

```fsharp
type RemediationOutcome =
    | Resolved
    | StillFailing of reason: string
    | NeedsHumanInput of prompt: string

type RemediationAttempt = {
    Id: Guid
    ConnectionId: Guid
    StartedAt: DateTimeOffset
    CompletedAt: DateTimeOffset option
    ActorAgentId: Guid option
    ActorUserId: Guid option
    Strategy: string                  // open string — "refresh-token", "reauth-prompt", etc.
    Outcome: RemediationOutcome option
    Notes: string option
}
```

### `Transaction.SyncEventId` back-link

Add an optional `SyncEventId: Guid option` to `Transaction` so any record produced by a sync can be traced back to the event that produced it. This is a small but important hook for remediation: when an agent investigates "why is this transaction wrong", it can see which sync introduced it.

## Why this is a placeholder

The shapes above are the minimum we need so the rest of the model is not silent on req 10, but the full remediation flow involves decisions that span scheduling, agent tooling, and user notifications:

- What strategies are available, and which agent owns each?
- How does a `RemediationAttempt` interact with `ConnectionStatus` transitions?
- What is the back-pressure mechanism when remediation keeps failing — do we throttle sync attempts, page the user, or both?
- How do we surface `NeedsHumanInput` to the user with minimal friction?

These are large enough to warrant their own ADR and a sub-issue. The placeholders here ensure that ADR-005 and the model do not need to be reshaped when that work lands.

## Consequences

- **Named gap, not silent gap**: req 10 is acknowledged in the model with a concrete intended shape, even though the operational design is still to come.
- **Trade-off — undelivered surface**: We are introducing types whose behavior is not yet implemented. The risk is that the eventual implementation forces a shape change. We accept that risk because the alternative — leaving req 10 unrepresented — was the SE's specific concern.
- **Backwards compatible insertion**: Both `FeedHealth` and `RemediationAttempt` are additive. The existing `ConnectionStatus` / `SyncEvent` continue to work; this ADR layers on top.

## Related Decisions

- [ADR-005](005-data-feed-abstraction.md) — establishes the feed abstraction these primitives extend.
- ADR-XXX (TBD): Full remediation flow design — agent strategies, escalation, user prompts.
