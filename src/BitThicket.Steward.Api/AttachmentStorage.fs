namespace BitThicket.Steward.Api

open System
open System.IO
open System.Security.Cryptography
open System.Threading.Tasks
open Microsoft.Extensions.Logging

type IAttachmentStorage =
    /// Store the bytes from stream, compute a SHA-256 hash, persist the blob
    /// under that hash, and return the content-addressed storage_ref (hash).
    abstract member StoreAsync : Guid -> string -> Stream -> Task<string>
    /// Retrieve a previously stored blob by its storage_ref.
    abstract member RetrieveAsync : string -> Task<Stream option>
    /// Delete the on-disk blob identified by storage_ref.
    /// Safe to call even if the file does not exist.
    abstract member DeleteAsync : string -> Task<unit>

type LocalAttachmentStorage(basePath: string, log: ILogger<LocalAttachmentStorage>) =
    let ensureDirectory () =
        if not (Directory.Exists basePath) then
            Directory.CreateDirectory(basePath) |> ignore

    interface IAttachmentStorage with
        member _.StoreAsync _id _fileName stream =
            task {
                ensureDirectory()
                use sha256 = SHA256.Create()
                let tempPath = Path.Combine(basePath, $"tmp-{Guid.NewGuid()}")
                // Stream to file while computing hash
                use tempStream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write)
                let buffer = Array.zeroCreate 8192
                let mutable read = true
                while read do
                    let! bytesRead = stream.ReadAsync(buffer, 0, buffer.Length)
                    if bytesRead = 0 then
                        read <- false
                    else
                        do! tempStream.WriteAsync(buffer, 0, bytesRead)
                        sha256.TransformBlock(buffer, 0, bytesRead, null, 0) |> ignore
                sha256.TransformFinalBlock(Array.empty, 0, 0) |> ignore
                let hash = BitConverter.ToString(sha256.Hash).Replace("-", "").ToLowerInvariant()
                let finalPath = Path.Combine(basePath, hash)
                if File.Exists(finalPath) then
                    File.Delete(tempPath)
                    log.LogInformation("Duplicate content; reusing existing blob. storage_ref={StorageRef}", hash)
                else
                    File.Move(tempPath, finalPath)
                    log.LogInformation("Stored attachment blob. storage_ref={StorageRef}", hash)
                return hash
            }

        member _.RetrieveAsync storageRef =
            task {
                let path = Path.Combine(basePath, storageRef)
                if File.Exists(path) then
                    let stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)
                    return Some(stream :> Stream)
                else
                    return None
            }

        member _.DeleteAsync storageRef =
            task {
                let path = Path.Combine(basePath, storageRef)
                if File.Exists(path) then
                    try
                        File.Delete(path)
                        log.LogInformation("Deleted attachment blob. storage_ref={StorageRef}", storageRef)
                    with ex ->
                        log.LogWarning(ex, "Failed to delete attachment blob. storage_ref={StorageRef}", storageRef)
                else
                    log.LogWarning("Attachment blob not found for deletion. storage_ref={StorageRef}", storageRef)
            }

module AttachmentStorage =
    let fromEnvironment (log: ILogger<LocalAttachmentStorage>) : IAttachmentStorage =
        let basePath =
            match System.Environment.GetEnvironmentVariable("STEWARD_ATTACHMENTS_PATH") with
            | null | "" -> Path.Combine(AppContext.BaseDirectory, "attachments")
            | v -> v
        LocalAttachmentStorage(basePath, log) :> IAttachmentStorage
