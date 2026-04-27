namespace BitThicket.Steward.Api

open System
open System.Data.Common
open System.Threading.Tasks
open Npgsql
open BitThicket.Steward.Api.Domain

/// Repository for tenant-scoped attachments.
type IAttachmentRepository =
    abstract GetAsync : id:Guid -> Task<Attachment option>
    abstract ListByTransactionAsync : transactionId:Guid -> Task<Attachment list>
    abstract ListBySplitAsync : splitId:Guid -> Task<Attachment list>
    abstract CreateAsync : attachment:Attachment -> Task<Guid>
    abstract DeleteAsync : id:Guid -> Task<unit>

module AttachmentRepository =

    // ── Kind helpers ─────────────────────────────────────────────────────────

    let private kindToString (kind: AttachmentKind) : string =
        match kind with
        | AttachmentKind.Receipt -> "receipt"
        | AttachmentKind.Statement -> "statement"
        | AttachmentKind.Other label -> $"other:{label}"

    let private kindFromString (s: string) : AttachmentKind =
        match s with
        | "receipt" -> AttachmentKind.Receipt
        | "statement" -> AttachmentKind.Statement
        | _ when s.StartsWith("other:") -> AttachmentKind.Other(s.Substring(6))
        | _ -> AttachmentKind.Other(s)

    // ── Row mapping ──────────────────────────────────────────────────────────

    let internal mapAttachment (reader: DbDataReader) : Attachment =
        {
            Id = reader.GetGuid(0)
            TenantId = reader.GetGuid(1)
            TransactionId = reader.GetGuid(2)
            SplitId = Sql.nullableGuid reader 3
            Kind = kindFromString (reader.GetString(4))
            StorageRef = reader.GetString(5)
            ContentHash = reader.GetString(6)
            ContentType = reader.GetString(7)
            SizeBytes = reader.GetInt64(8)
            UploadedAt = Sql.dateTimeOffset reader 9
            UploadedByUserId = Sql.nullableGuid reader 10
            UploadedByAgentId = Sql.nullableGuid reader 11
        }

    let private selectColumns =
        "id, tenant_id, transaction_id, split_id, kind, storage_ref, content_hash, content_type, size_bytes, uploaded_at, uploaded_by_user_id, uploaded_by_agent_id"

    // ── CRUD implementations ─────────────────────────────────────────────────

    let getAsync (factory: IDbConnectionFactory) (tenantContext: TenantContext) (id: Guid) =
        task {
            use! conn = factory.OpenForTenantAsync(tenantContext)
            use cmd = conn.CreateCommand()
            cmd.CommandText <- $"SELECT {selectColumns} FROM attachments WHERE id = $1"
            cmd.Parameters.AddWithValue("$1", id) |> ignore
            let! reader = cmd.ExecuteReaderAsync()
            use reader = reader
            let! hasRow = reader.ReadAsync()
            return if hasRow then Some(mapAttachment reader) else None
        }

    let listByTransactionAsync (factory: IDbConnectionFactory) (tenantContext: TenantContext) (transactionId: Guid) =
        task {
            use! conn = factory.OpenForTenantAsync(tenantContext)
            use cmd = conn.CreateCommand()
            cmd.CommandText <- $"SELECT {selectColumns} FROM attachments WHERE transaction_id = $1 ORDER BY uploaded_at"
            cmd.Parameters.AddWithValue("$1", transactionId) |> ignore
            let! reader = cmd.ExecuteReaderAsync()
            use reader = reader
            let attachments = ResizeArray<Attachment>()
            while! reader.ReadAsync() do
                attachments.Add(mapAttachment reader)
            return attachments |> Seq.toList
        }

    let listBySplitAsync (factory: IDbConnectionFactory) (tenantContext: TenantContext) (splitId: Guid) =
        task {
            use! conn = factory.OpenForTenantAsync(tenantContext)
            use cmd = conn.CreateCommand()
            cmd.CommandText <- $"SELECT {selectColumns} FROM attachments WHERE split_id = $1 ORDER BY uploaded_at"
            cmd.Parameters.AddWithValue("$1", splitId) |> ignore
            let! reader = cmd.ExecuteReaderAsync()
            use reader = reader
            let attachments = ResizeArray<Attachment>()
            while! reader.ReadAsync() do
                attachments.Add(mapAttachment reader)
            return attachments |> Seq.toList
        }

    let createAsync (factory: IDbConnectionFactory) (attachment: Attachment) =
        task {
            let ctx = { TenantId = attachment.TenantId; UserId = Guid.Empty }
            use! conn = factory.OpenForTenantAsync(ctx)
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                """INSERT INTO attachments (
                       id, tenant_id, transaction_id, split_id, kind, storage_ref,
                       content_hash, content_type, size_bytes, uploaded_at,
                       uploaded_by_user_id, uploaded_by_agent_id
                   ) VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12)"""
            cmd.Parameters.AddWithValue("$1", attachment.Id) |> ignore
            cmd.Parameters.AddWithValue("$2", attachment.TenantId) |> ignore
            cmd.Parameters.AddWithValue("$3", attachment.TransactionId) |> ignore
            match attachment.SplitId with
            | Some sid -> cmd.Parameters.AddWithValue("$4", sid) |> ignore
            | None -> cmd.Parameters.AddWithValue("$4", DBNull.Value) |> ignore
            cmd.Parameters.AddWithValue("$5", kindToString attachment.Kind) |> ignore
            cmd.Parameters.AddWithValue("$6", attachment.StorageRef) |> ignore
            cmd.Parameters.AddWithValue("$7", attachment.ContentHash) |> ignore
            cmd.Parameters.AddWithValue("$8", attachment.ContentType) |> ignore
            cmd.Parameters.AddWithValue("$9", attachment.SizeBytes) |> ignore
            cmd.Parameters.AddWithValue("$10", attachment.UploadedAt.UtcDateTime) |> ignore
            match attachment.UploadedByUserId with
            | Some uid -> cmd.Parameters.AddWithValue("$11", uid) |> ignore
            | None -> cmd.Parameters.AddWithValue("$11", DBNull.Value) |> ignore
            match attachment.UploadedByAgentId with
            | Some aid -> cmd.Parameters.AddWithValue("$12", aid) |> ignore
            | None -> cmd.Parameters.AddWithValue("$12", DBNull.Value) |> ignore
            let! _ = cmd.ExecuteNonQueryAsync()
            return attachment.Id
        }

    let deleteAsync (factory: IDbConnectionFactory) (tenantContext: TenantContext) (id: Guid) =
        task {
            use! conn = factory.OpenForTenantAsync(tenantContext)
            use cmd = conn.CreateCommand()
            cmd.CommandText <- "DELETE FROM attachments WHERE id = $1 AND tenant_id = $2"
            cmd.Parameters.AddWithValue("$1", id) |> ignore
            cmd.Parameters.AddWithValue("$2", tenantContext.TenantId) |> ignore
            let! _ = cmd.ExecuteNonQueryAsync()
            return ()
        }

    let create (factory: IDbConnectionFactory) (accessor: ITenantContextAccessor) : IAttachmentRepository =
        let requireCtx () =
            match accessor.Context with
            | Some ctx -> ctx
            | None -> failwith "No tenant context available for the current operation"

        { new IAttachmentRepository with
            member _.GetAsync(id) = getAsync factory (requireCtx()) id
            member _.ListByTransactionAsync(transactionId) = listByTransactionAsync factory (requireCtx()) transactionId
            member _.ListBySplitAsync(splitId) = listBySplitAsync factory (requireCtx()) splitId
            member _.CreateAsync(attachment) = createAsync factory attachment
            member _.DeleteAsync(id) = deleteAsync factory (requireCtx()) id
        }
