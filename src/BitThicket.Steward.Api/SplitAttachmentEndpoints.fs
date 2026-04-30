namespace BitThicket.Steward.Api

open System
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Falco
open BitThicket.Steward.Api.Domain

module SplitAttachmentEndpoints =
    let listSplitsHandler (_txnId: Guid) : HttpHandler = fun ctx ->
        ctx.Response.StatusCode <- 501
        Response.ofJson {| error = "Not implemented" |} ctx

    let createSplitsHandler (_txnId: Guid) : HttpHandler = fun ctx ->
        ctx.Response.StatusCode <- 501
        Response.ofJson {| error = "Not implemented" |} ctx

    let deleteSplitHandler (_txnId: Guid) (_splitId: Guid) : HttpHandler = fun ctx ->
        ctx.Response.StatusCode <- 501
        Response.ofJson {| error = "Not implemented" |} ctx

    let uploadAttachmentHandler (_txnId: Guid) : HttpHandler = fun ctx ->
        ctx.Response.StatusCode <- 501
        Response.ofJson {| error = "Not implemented" |} ctx

    let uploadSplitAttachmentHandler (_txnId: Guid) (_splitId: Guid) : HttpHandler = fun ctx ->
        ctx.Response.StatusCode <- 501
        Response.ofJson {| error = "Not implemented" |} ctx

    let getAttachmentHandler (_attachmentId: Guid) : HttpHandler = fun ctx ->
        ctx.Response.StatusCode <- 501
        Response.ofJson {| error = "Not implemented" |} ctx

    /// DELETE /api/attachments/{attachmentId}
    ///
    /// Deletes the attachment row first, then checks whether any other row in
    /// the tenant still references the same content-addressed storage_ref.
    /// Only deletes the on-disk file when the ref count drops to zero.
    /// This fixes the shared-content deletion bug described in STE-113.
    let deleteAttachmentHandler (attachmentId: Guid) : HttpHandler = fun ctx ->
        task {
            let repo = ctx.RequestServices.GetRequiredService<IAttachmentRepository>()
            let storage = ctx.RequestServices.GetRequiredService<IAttachmentStorage>()

            let! attachmentOpt = repo.GetAsync attachmentId
            match attachmentOpt with
            | None ->
                ctx.Response.StatusCode <- 404
                do! Response.ofJson {| error = "Attachment not found" |} ctx
            | Some attachment ->
                // 1. Delete the attachment row first.
                do! repo.DeleteAsync attachmentId

                // 2. Check if any other attachment still shares the same file.
                let! remaining = repo.CountByStorageRefAsync attachment.StorageRef

                // 3. Only delete the on-disk content when no refs remain.
                if remaining = 0 then
                    do! storage.DeleteAsync attachment.StorageRef

                ctx.Response.StatusCode <- 204
                do! Response.ofEmpty ctx
        }
