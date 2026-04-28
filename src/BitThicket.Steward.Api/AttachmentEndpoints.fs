namespace BitThicket.Steward.Api

open System
open System.IO
open System.Linq
open System.Security.Cryptography
open System.Text.Json
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Falco
open BitThicket.Steward.Api.Domain

// ── Validation ─────────────────────────────────────────────────────────────

module private AttachmentValidation =
    let maxSizeBytes = 10L * 1024L * 1024L // 10 MB

    let allowedMimeTypes = [
        // Images
        "image/jpeg"; "image/png"; "image/gif"; "image/webp"
        // Documents
        "application/pdf"
        // Text
        "text/plain"; "text/csv"
        // Common office
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document"
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
    ]

    let isAllowedMimeType (contentType: string) : bool =
        if String.IsNullOrWhiteSpace(contentType) then false
        else
            let ct = contentType.Trim().ToLowerInvariant()
            allowedMimeTypes |> List.contains ct
            || ct.StartsWith("image/")
            || ct.StartsWith("text/")

    let validateUpload (contentType: string) (size: int64) : Result<unit, string> =
        if not (isAllowedMimeType contentType) then
            Error $"Unsupported media type: {contentType}"
        elif size > maxSizeBytes then
            Error $"File exceeds 10MB limit ({size} bytes)"
        else
            Ok ()

// ── Response DTOs ──────────────────────────────────────────────────────────

type AttachmentResponse = {
    id: Guid
    transactionId: Guid
    splitId: Guid option
    kind: string
    contentType: string
    sizeBytes: int64
    uploadedAt: DateTimeOffset
}

// ── Helpers ────────────────────────────────────────────────────────────────

module private AttachmentHelpers =
    let attachmentToResponse (a: Attachment) : AttachmentResponse =
        {
            id = a.Id
            transactionId = a.TransactionId
            splitId = a.SplitId
            kind =
                match a.Kind with
                | AttachmentKind.Receipt -> "receipt"
                | AttachmentKind.Statement -> "statement"
                | AttachmentKind.Other label -> $"other:{label}"
            contentType = a.ContentType
            sizeBytes = a.SizeBytes
            uploadedAt = a.UploadedAt
        }

    let sha256Hex (bytes: byte[]) : string =
        use sha = SHA256.Create()
        let hash = sha.ComputeHash(bytes)
        Convert.ToHexString(hash).ToLowerInvariant()

    let readFormFileAsync (file: Microsoft.AspNetCore.Http.IFormFile) =
        task {
            use ms = new MemoryStream()
            do! file.CopyToAsync(ms)
            return ms.ToArray()
        }

    let processAttachmentUpload
        (ctx: HttpContext)
        (tc: TenantContext)
        (txnId: Guid)
        (splitId: Guid option)
        (file: Microsoft.AspNetCore.Http.IFormFile)
        (kindStr: string)
        =
        task {
            let attachmentRepo = ctx.RequestServices.GetRequiredService<IAttachmentRepository>()
            let storage = ctx.RequestServices.GetRequiredService<IAttachmentStorage>()

            let! bytes = readFormFileAsync file
            match AttachmentValidation.validateUpload file.ContentType (int64 bytes.Length) with
            | Error msg when msg.StartsWith("Unsupported media type") ->
                ctx.Response.StatusCode <- 415
                do! Response.ofJson {| error = msg |} ctx
            | Error msg ->
                ctx.Response.StatusCode <- 400
                do! Response.ofJson {| error = msg |} ctx
            | Ok () ->
                let contentHash = sha256Hex bytes
                let! storageRef = storage.StoreAsync tc.TenantId file.ContentType bytes
                let kind =
                    match kindStr.ToLowerInvariant() with
                    | "receipt" -> AttachmentKind.Receipt
                    | "statement" -> AttachmentKind.Statement
                    | _ -> AttachmentKind.Other kindStr
                let attachment: Attachment = {
                    Id = Guid.NewGuid()
                    TenantId = tc.TenantId
                    TransactionId = txnId
                    SplitId = splitId
                    Kind = kind
                    StorageRef = storageRef
                    ContentHash = contentHash
                    ContentType = file.ContentType
                    SizeBytes = int64 bytes.Length
                    UploadedAt = DateTimeOffset.UtcNow
                    UploadedByUserId = Some tc.UserId
                    UploadedByAgentId = None
                }
                try
                    let! _ = attachmentRepo.CreateAsync(attachment)
                    ctx.Response.StatusCode <- 201
                    do! Response.ofJson (attachmentToResponse attachment) ctx
                with
                | ex ->
                    do! storage.DeleteAsync tc.TenantId storageRef
                    raise ex
        }

// ── Endpoints ──────────────────────────────────────────────────────────────

module AttachmentEndpoints =
    open AttachmentHelpers

    // POST /api/transactions/{txnId}/attachments
    let createTransactionAttachmentHandler (txnId: Guid) : HttpHandler = fun ctx ->
        task {
            let txnRepo = ctx.RequestServices.GetRequiredService<ITransactionRepository>()
            let accessor = ctx.RequestServices.GetRequiredService<ITenantContextAccessor>()

            match accessor.Context with
            | None ->
                ctx.Response.StatusCode <- 401
                do! Response.ofJson {| error = "Unauthorized" |} ctx
            | Some tc ->
                let! txnOpt = txnRepo.GetAsync(txnId)
                match txnOpt with
                | None ->
                    ctx.Response.StatusCode <- 404
                    do! Response.ofJson {| error = "Transaction not found" |} ctx
                | Some _ ->
                    if not ctx.Request.HasFormContentType then
                        ctx.Response.StatusCode <- 400
                        do! Response.ofJson {| error = "Expected multipart/form-data" |} ctx
                    else
                        let form = ctx.Request.Form
                        let fileOpt =
                            if form.Files.Count > 0 then Some(form.Files.[0])
                            else None
                        match fileOpt with
                        | None ->
                            ctx.Response.StatusCode <- 400
                            do! Response.ofJson {| error = "No file provided" |} ctx
                        | Some file ->
                            let kindStr = form.TryGetValue("kind") |> fst |> (fun b -> if b then form["kind"].ToString() else "other")
                            do! processAttachmentUpload ctx tc txnId None file kindStr
        }

    // POST /api/transactions/{txnId}/splits/{splitId}/attachments
    let createSplitAttachmentHandler (txnId: Guid) (splitId: Guid) : HttpHandler = fun ctx ->
        task {
            let txnRepo = ctx.RequestServices.GetRequiredService<ITransactionRepository>()
            let splitRepo = ctx.RequestServices.GetRequiredService<ISplitRepository>()
            let accessor = ctx.RequestServices.GetRequiredService<ITenantContextAccessor>()

            match accessor.Context with
            | None ->
                ctx.Response.StatusCode <- 401
                do! Response.ofJson {| error = "Unauthorized" |} ctx
            | Some tc ->
                let! txnOpt = txnRepo.GetAsync(txnId)
                match txnOpt with
                | None ->
                    ctx.Response.StatusCode <- 404
                    do! Response.ofJson {| error = "Transaction not found" |} ctx
                | Some _ ->
                    let! splitOpt = splitRepo.GetAsync(splitId)
                    match splitOpt with
                    | None ->
                        ctx.Response.StatusCode <- 404
                        do! Response.ofJson {| error = "Split not found" |} ctx
                    | Some split when split.TransactionId <> txnId ->
                        ctx.Response.StatusCode <- 404
                        do! Response.ofJson {| error = "Split not found" |} ctx
                    | Some _ ->
                        if not ctx.Request.HasFormContentType then
                            ctx.Response.StatusCode <- 400
                            do! Response.ofJson {| error = "Expected multipart/form-data" |} ctx
                        else
                            let form = ctx.Request.Form
                            let fileOpt =
                                if form.Files.Count > 0 then Some(form.Files.[0])
                                else None
                            match fileOpt with
                            | None ->
                                ctx.Response.StatusCode <- 400
                                do! Response.ofJson {| error = "No file provided" |} ctx
                            | Some file ->
                                let kindStr = form.TryGetValue("kind") |> fst |> (fun b -> if b then form["kind"].ToString() else "other")
                                do! processAttachmentUpload ctx tc txnId (Some splitId) file kindStr
        }

    // GET /api/attachments/{id}
    let getAttachmentHandler (id: Guid) : HttpHandler = fun ctx ->
        task {
            let attachmentRepo = ctx.RequestServices.GetRequiredService<IAttachmentRepository>()
            let storage = ctx.RequestServices.GetRequiredService<IAttachmentStorage>()
            let accessor = ctx.RequestServices.GetRequiredService<ITenantContextAccessor>()

            match accessor.Context with
            | None ->
                ctx.Response.StatusCode <- 401
                do! Response.ofJson {| error = "Unauthorized" |} ctx
            | Some tc ->
                let! attachmentOpt = attachmentRepo.GetAsync(id)
                match attachmentOpt with
                | None ->
                    ctx.Response.StatusCode <- 404
                    do! Response.ofJson {| error = "Attachment not found" |} ctx
                | Some attachment ->
                    let! bytesOpt = storage.LoadAsync tc.TenantId attachment.StorageRef
                    match bytesOpt with
                    | None ->
                        ctx.Response.StatusCode <- 404
                        do! Response.ofJson {| error = "Attachment content not found" |} ctx
                    | Some bytes ->
                        ctx.Response.ContentType <- attachment.ContentType
                        ctx.Response.ContentLength <- int64 bytes.Length
                        ctx.Response.Headers["Content-Disposition"] <- $"attachment; filename=\"{attachment.Id}\""
                        do! ctx.Response.Body.WriteAsync(bytes, 0, bytes.Length)
        }

    // GET /api/transactions/{txnId}/attachments
    let listTransactionAttachmentsHandler (txnId: Guid) : HttpHandler = fun ctx ->
        task {
            let attachmentRepo = ctx.RequestServices.GetRequiredService<IAttachmentRepository>()
            let txnRepo = ctx.RequestServices.GetRequiredService<ITransactionRepository>()
            let accessor = ctx.RequestServices.GetRequiredService<ITenantContextAccessor>()

            match accessor.Context with
            | None ->
                ctx.Response.StatusCode <- 401
                do! Response.ofJson {| error = "Unauthorized" |} ctx
            | Some _ ->
                let! txnOpt = txnRepo.GetAsync(txnId)
                match txnOpt with
                | None ->
                    ctx.Response.StatusCode <- 404
                    do! Response.ofJson {| error = "Transaction not found" |} ctx
                | Some _ ->
                    let! attachments = attachmentRepo.ListByTransactionAsync(txnId)
                    do! Response.ofJson {| attachments = attachments |> List.map attachmentToResponse |} ctx
        }

    // DELETE /api/attachments/{id}
    let deleteAttachmentHandler (id: Guid) : HttpHandler = fun ctx ->
        task {
            let attachmentRepo = ctx.RequestServices.GetRequiredService<IAttachmentRepository>()
            let storage = ctx.RequestServices.GetRequiredService<IAttachmentStorage>()
            let accessor = ctx.RequestServices.GetRequiredService<ITenantContextAccessor>()

            match accessor.Context with
            | None ->
                ctx.Response.StatusCode <- 401
                do! Response.ofJson {| error = "Unauthorized" |} ctx
            | Some tc ->
                let! attachmentOpt = attachmentRepo.GetAsync(id)
                match attachmentOpt with
                | None ->
                    ctx.Response.StatusCode <- 404
                    do! Response.ofJson {| error = "Attachment not found" |} ctx
                | Some attachment ->
                    do! storage.DeleteAsync tc.TenantId attachment.StorageRef
                    do! attachmentRepo.DeleteAsync(id)
                    ctx.Response.StatusCode <- 204
                    do! Response.ofEmpty ctx
        }
