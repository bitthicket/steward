module BitThicket.Steward.Api.Test.AkoyaClientTests

open System
open Xunit
open BitThicket.Steward.Ingestion.Akoya

module NormalizationTests =

    [<Fact>]
    let ``toMinorUnits converts USD correctly`` () =
        let result = AkoyaNormalization.toMinorUnits 42.50m "USD"
        Assert.Equal(4250L, result)

    [<Fact>]
    let ``toMinorUnits converts JPY correctly`` () =
        let result = AkoyaNormalization.toMinorUnits 1000m "JPY"
        Assert.Equal(1000L, result)

    [<Fact>]
    let ``toMinorUnits converts BTC correctly`` () =
        let result = AkoyaNormalization.toMinorUnits 0.12345678m "BTC"
        Assert.Equal(12345678L, result)

    [<Fact>]
    let ``normalize maps FdxTransaction to NormalizedTransaction`` () =
        let fdx = {
            TransactionId = "txn-123"
            AccountId = "acc-456"
            Amount = -42.50m
            Currency = "USD"
            Description = "Coffee Shop"
            TransactionDate = DateTimeOffset(2026, 4, 27, 0, 0, 0, TimeSpan.Zero)
            PostingDate = Some(DateTimeOffset(2026, 4, 28, 0, 0, 0, TimeSpan.Zero))
            Memo = Some("Starbucks")
        }
        let normalized = AkoyaNormalization.normalize fdx
        Assert.Equal("txn-123", normalized.ExternalId)
        Assert.Equal("acc-456", normalized.AccountId)
        Assert.Equal(-4250L, normalized.AmountMinor)
        Assert.Equal("USD", normalized.Currency)
        Assert.Equal("Coffee Shop", normalized.Description)
        Assert.Equal(Some("Starbucks"), normalized.Merchant)
