# ADR-008: Transaction splits, attachments, and agent enrichment

## Status

Accepted

## Context

Real-world consumer transactions are often composite: an Amazon order contains a dozen line items spanning several categories, a Costco receipt mixes groceries with household goods, and a single restaurant charge can include a tip the user wants categorized separately. The original model had a single optional `CategoryId` per `Transaction`, which forced users to either pick the dominant category or split the transaction into manual entries — both of which destroy the link to the institution-reported transaction.

The product also requires receipt support, both as evidence and as the input to splitting (a receipt is the source-of-truth for line items). And one of the strongest requirements (req 9) calls out agentic enrichment: an AI agent should be able to look up an Amazon order and populate the splits automatically, or pull a Square receipt and attach it.

## Decision

Introduce three primitives:

### 1. `TransactionSplit`

A transaction may have zero or more splits. When zero, the transaction's own `CategoryId` and `Amount` apply (the simple, common case). When one or more splits exist, the splits are authoritative for categorization and their amounts must sum to the transaction amount.

```fsharp
type TransactionSplit = {
    Id: Guid
    TransactionId: Guid
    Amount: Money            // signed, same currency as parent transaction
    CategoryId: Guid option
    Description: string option
    Memo: string option
    Source: SplitSource      // Manual | Receipt | Enrichment of providerKey: string
    SortOrder: int
}
```

Splits live on a single account (the parent transaction's account); they are not a way to move money between accounts — that is what transfers are for. A transaction without splits is presented to the user as a single line; a transaction with splits expands to its line items but still rolls up to one record on the account ledger.

### 2. `Attachment`

A binary blob attached to a transaction (or to a split, when the receipt is for a specific line). Attachments are content-addressed by hash and stored in object storage; the domain only references them.

```fsharp
type AttachmentKind =
    | Receipt
    | Statement
    | Other of label: string

type Attachment = {
    Id: Guid
    TransactionId: Guid
    SplitId: Guid option       // when the attachment belongs to a specific split
    Kind: AttachmentKind
    StorageRef: string         // opaque pointer to object storage
    ContentHash: string        // sha-256 of the bytes
    ContentType: string
    SizeBytes: int64
    UploadedAt: DateTimeOffset
    UploadedByUserId: Guid option
    UploadedByAgentId: Guid option
}
```

### 3. `TransactionEnrichment`

A record of an agent (or service) having looked up the transaction in an external source and produced structured data. Enrichments are append-only and tagged with the source so the user can see where the data came from and override it.

```fsharp
type EnrichmentStatus =
    | Pending
    | Succeeded
    | Failed of reason: string

type TransactionEnrichment = {
    Id: Guid
    TransactionId: Guid
    SourceKey: string          // e.g. "amazon", "square", "uber"
    Status: EnrichmentStatus
    ResultPayload: string option   // JSON blob of the source-shaped result
    ProducedSplitIds: Guid list    // splits this enrichment authored
    ProducedAttachmentIds: Guid list
    RequestedAt: DateTimeOffset
    CompletedAt: DateTimeOffset option
    RequestedByUserId: Guid option
    RequestedByAgentId: Guid option
}
```

`SourceKey` is a string (not a closed DU) because enrichment sources are an open extension surface — Amazon today, Square or Uber tomorrow, a long tail of merchant-specific sources later. The registry of supported sources lives outside the domain layer.

## Consequences

- **Splits without rework**: Existing code that reads `Transaction.CategoryId` continues to work for the no-split case. Reporting and budgeting roll up over splits when present, fall back to the parent category when not.
- **Receipts are first-class**: Both manual uploads (user takes a photo) and agent-fetched documents (Amazon order PDF) flow through the same `Attachment` primitive.
- **Agentic enrichment has a domain handle**: `TransactionEnrichment` gives an agent a place to record what it tried, what it found, and what splits/attachments it authored — auditable and reversible.
- **Sum invariant**: When splits exist, `sum(splits.Amount) = transaction.Amount` is a hard invariant. Enforced in the service layer, not the type system, because partial-state UIs (mid-edit) need to be representable.
- **Storage out of scope**: Attachment bytes live in object storage (S3-compatible); the domain only carries a pointer and a content hash. Hash deduplication and access control are infrastructure concerns.
- **Trade-off — schema cost**: Three new tables (or equivalent), plus indexes. Justified by the requirement; no smaller representation captures splits + attachments + provenance.

## Related Decisions

- [ADR-001](001-single-entry-with-transfer-links.md) — single-entry transaction model that splits live underneath.
- [ADR-003](003-reconciliation-via-transaction-matching.md) — reconciliation matches at the parent-transaction level; splits do not affect the match.
