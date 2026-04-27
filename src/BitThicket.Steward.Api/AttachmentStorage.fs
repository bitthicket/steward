namespace BitThicket.Steward.Api

open System
open System.IO
open System.Security.Cryptography
open System.Threading.Tasks
open BitThicket.Steward.Api.Domain

/// Content-addressed, tenant-isolated attachment storage.
type IAttachmentStorage =
    /// Store bytes and return the storage reference string.
    abstract StoreAsync : tenantId:Guid -> contentType:string -> bytes:byte[] -> Task<string>
    /// Load bytes by storage reference.
    abstract LoadAsync : tenantId:Guid -> storageRef:string -> Task<byte[] option>
    /// Delete bytes by storage reference.
    abstract DeleteAsync : tenantId:Guid -> storageRef:string -> Task<unit>

/// Local-filesystem storage implementation.
/// Layout: <root>/<tenant_id>/<sha256_first2>/<sha256_full>
/// Environment variable: STEWARD_ATTACHMENT_ROOT
module LocalAttachmentStorage =

    let private rootPath () =
        match Environment.GetEnvironmentVariable("STEWARD_ATTACHMENT_ROOT") with
        | null | "" -> Path.Combine(Environment.CurrentDirectory, "attachments")
        | v -> v

    let private sha256Hex (bytes: byte[]) : string =
        use sha = SHA256.Create()
        let hash = sha.ComputeHash(bytes)
        Convert.ToHexString(hash).ToLowerInvariant()

    let private ensureDir (path: string) =
        if not (Directory.Exists(path)) then
            Directory.CreateDirectory(path) |> ignore

    let storeAsync (tenantId: Guid) (contentType: string) (bytes: byte[]) =
        task {
            let hash = sha256Hex bytes
            let root = rootPath ()
            let tenantDir = Path.Combine(root, tenantId.ToString("n"))
            let prefixDir = Path.Combine(tenantDir, hash.Substring(0, 2))
            let filePath = Path.Combine(prefixDir, hash)
            ensureDir prefixDir
            do! File.WriteAllBytesAsync(filePath, bytes)
            return hash
        }

    let loadAsync (tenantId: Guid) (storageRef: string) =
        task {
            let root = rootPath ()
            let filePath = Path.Combine(root, tenantId.ToString("n"), storageRef.Substring(0, 2), storageRef)
            if File.Exists(filePath) then
                let! bytes = File.ReadAllBytesAsync(filePath)
                return Some bytes
            else
                return None
        }

    let deleteAsync (tenantId: Guid) (storageRef: string) =
        task {
            let root = rootPath ()
            let filePath = Path.Combine(root, tenantId.ToString("n"), storageRef.Substring(0, 2), storageRef)
            if File.Exists(filePath) then
                File.Delete(filePath)
        }

    let create () : IAttachmentStorage =
        { new IAttachmentStorage with
            member _.StoreAsync tenantId contentType bytes = storeAsync tenantId contentType bytes
            member _.LoadAsync tenantId storageRef = loadAsync tenantId storageRef
            member _.DeleteAsync tenantId storageRef = deleteAsync tenantId storageRef
        }
