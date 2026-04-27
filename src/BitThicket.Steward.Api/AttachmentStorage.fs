namespace BitThicket.Steward.Api

open System
open System.IO
open System.Threading.Tasks
open Microsoft.Extensions.Logging

type IAttachmentStorage =
    abstract member StoreAsync : Guid -> string -> Stream -> Task<string>
    abstract member RetrieveAsync : string -> Task<Stream option>
    abstract member DeleteAsync : string -> Task<unit>

type LocalAttachmentStorage(log: ILogger<LocalAttachmentStorage>) =
    interface IAttachmentStorage with
        member _.StoreAsync _id _fileName _stream = Task.FromResult("")
        member _.RetrieveAsync _path = Task.FromResult(None)
        member _.DeleteAsync _path = Task.FromResult(() )

module AttachmentStorage =
    let fromEnvironment (log: ILogger<LocalAttachmentStorage>) : IAttachmentStorage =
        LocalAttachmentStorage(log) :> IAttachmentStorage
