namespace BitThicket.Steward.Api

open System
open System.Threading.Tasks
open BitThicket.Steward.Api.Domain

type IAttachmentRepository =
    abstract member GetAsync : Guid -> Task<Attachment option>
    abstract member ListByTransactionAsync : Guid -> Task<Attachment list>
    abstract member ListBySplitAsync : Guid -> Task<Attachment list>
    abstract member CreateAsync : Attachment -> Task<unit>
    abstract member DeleteAsync : Guid -> Task<unit>

module AttachmentRepository =
    let create (_factory: IDbConnectionFactory) (_accessor: ITenantContextAccessor) : IAttachmentRepository =
        { new IAttachmentRepository with
            member _.GetAsync(_id) = Task.FromResult(None)
            member _.ListByTransactionAsync(_txnId) = Task.FromResult([])
            member _.ListBySplitAsync(_splitId) = Task.FromResult([])
            member _.CreateAsync(_attachment) = Task.FromResult(() )
            member _.DeleteAsync(_id) = Task.FromResult(() )
        }
