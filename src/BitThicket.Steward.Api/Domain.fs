namespace BitThicket.Steward.Api.Domain

open System

/// Core domain types for Steward personal finance tool.

[<RequireQualifiedAccess>]
module Currency =
    type Code = Code of string

type Money = {
    Amount: decimal
    Currency: Currency.Code
}

[<RequireQualifiedAccess>]
type AccountType =
    | Checking
    | Savings
    | CreditCard
    | Investment
    | Cash

type Account = {
    Id: Guid
    Name: string
    AccountType: AccountType
    Currency: Currency.Code
    CreatedAt: DateTimeOffset
}

[<RequireQualifiedAccess>]
type TransactionKind =
    | Debit
    | Credit
    | Transfer

type Transaction = {
    Id: Guid
    AccountId: Guid
    Kind: TransactionKind
    Amount: Money
    Description: string
    Category: string option
    OccurredAt: DateTimeOffset
    CreatedAt: DateTimeOffset
}

type BudgetPeriod =
    | Monthly
    | Weekly
    | Custom of startDate: DateOnly * endDate: DateOnly

type Budget = {
    Id: Guid
    Name: string
    Category: string
    Limit: Money
    Period: BudgetPeriod
    CreatedAt: DateTimeOffset
}
