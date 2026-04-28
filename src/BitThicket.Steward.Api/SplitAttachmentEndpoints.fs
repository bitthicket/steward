namespace BitThicket.Steward.Api

open System
open Microsoft.AspNetCore.Http
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

    let deleteAttachmentHandler (_attachmentId: Guid) : HttpHandler = fun ctx ->
        ctx.Response.StatusCode <- 501
        Response.ofJson {| error = "Not implemented" |} ctx
