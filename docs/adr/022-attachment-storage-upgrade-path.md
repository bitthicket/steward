# ADR-022: Attachment storage upgrade path

## Status

Accepted

## Context

STE-49 introduces binary attachment storage (receipts, statements, documents) linked to transactions and splits. The MVP needs a working storage backend today, but the long-term target is an S3-compatible object store (AWS S3, MinIO, R2, etc.) for durability, CDN offload, and cross-region replication.

We need a design that:
1. Works out of the box in local dev and small self-hosted deployments.
2. Can migrate to object storage without rewriting the domain or API layers.
3. Preserves tenant isolation and content-addressed deduplication.
4. Supports future encryption-at-rest without re-architecting.

## Decision

### 1. Storage abstraction

Introduce `IAttachmentStorage` with three operations: `StoreAsync`, `LoadAsync`, `DeleteAsync`. The domain (`AttachmentRepository`) never touches the filesystem directly; it only stores opaque `StorageRef` strings and calls the interface.

### 2. Local filesystem strategy (MVP)

The default implementation (`LocalAttachmentStorage`) stores files on local disk under `STEWARD_ATTACHMENT_ROOT`:

```
<root>/<tenant_id_nodash>/<sha256_first2>/<sha256_full>
```

- **Tenant isolation**: Every tenant has its own directory prefix.
- **Content addressing**: Files are named by SHA-256 hash, giving natural deduplication.
- **Sharded prefix**: The first two hex characters of the hash prevent any single directory from growing unbounded.
- **No extension**: The filename is the raw hash; content type is stored in the domain table for serving.

### 3. SHA-256 for deduplication and integrity

Every upload computes SHA-256 before writing. If the hash already exists for that tenant, the store can short-circuit (not yet implemented in the local strategy, but the interface supports it). The hash is also stored in `attachments.content_hash` for audit and integrity verification.

### 4. Validation

- **10 MB size limit** enforced in the endpoint layer.
- **MIME type whitelist** enforced before storage: `image/*`, `application/pdf`, `text/*`, and common Office Open XML types.
- **HTTP 415 Unsupported Media Type** returned for disallowed types.

### 5. Upgrade path to S3-compatible storage

A future `S3AttachmentStorage` implementation will:
1. Implement the same `IAttachmentStorage` interface.
2. Use the same SHA-256 hash as the S3 object key, prefixed by tenant: `attachments/<tenant_id_nodash>/<sha256_first2>/<sha256_full>`.
3. Be swapped in via DI configuration (`STEWARD_ATTACHMENT_STORAGE=s3`) without touching repositories or endpoints.

Migration from local to S3 can be performed offline:
1. Backfill S3 bucket by walking the local tree and `PutObject`-ing each file.
2. Update `attachments.storage_ref` to strip any local-specific prefix (or keep it if the S3 key matches).
3. Cut over the DI registration.
4. Delete local files after verifying.

### 6. Encryption-at-rest (future)

Because the storage layer is abstracted, a `EncryptingAttachmentStorage` decorator can wrap any underlying `IAttachmentStorage`, encrypting bytes before `StoreAsync` and decrypting after `LoadAsync`. Key management is out of scope for STE-49.

## Consequences

- **Local dev is trivial**: No external services needed; files land in `./attachments` by default.
- **Production self-hosting is viable**: A persistent volume mounted at `STEWARD_ATTACHMENT_ROOT` is sufficient for small deployments.
- **Cloud migration is incremental**: The interface boundary means S3 support is a pure infrastructure addition.
- **Cross-tenant leakage is prevented**: Both the local path layout and the planned S3 key prefix enforce tenant isolation.
- **Trade-off — no CDN**: Local storage cannot serve files via CDN; this is acceptable for MVP and resolved by the S3 upgrade.

## Related Decisions

- [ADR-008](008-transaction-splits-attachments-and-enrichment.md) — domain model for attachments.
- [ADR-013](013-rls-tenant-isolation.md) — tenant isolation rationale.
