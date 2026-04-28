namespace BitThicket.Steward.Api

open System
open System.Threading.Tasks
open BitThicket.Steward.Api.Domain

type ITransactionSplitRepository =
    abstract member GetAsync : Guid -> Task<TransactionSplit option>
    abstract member ListByTransactionAsync : Guid -> Task<TransactionSplit list>
    abstract member CreateAsync : TransactionSplit -> Task<unit>
    abstract member UpdateAsync : TransactionSplit -> Task<unit>
    abstract member DeleteAsync : Guid -> Task<unit>

module TransactionSplitRepository =
    let create (_factory: IDbConnectionFactory) (_accessor: ITenantContextAccessor) : ITransactionSplitRepository =
        { new ITransactionSplitRepository with
            member _.GetAsync(_id) = Task.FromResult(None)
            member _.ListByTransactionAsync(_txnId) = Task.FromResult([])
            member _.CreateAsync(_split) = Task.FromResult(() )
            member _.UpdateAsync(_split) = Task.FromResult(() )
            member _.DeleteAsync(_id) = Task.FromResult(() )
        }
