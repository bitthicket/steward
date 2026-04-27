module BitThicket.Steward.Api.Test.MoneyTests

open System
open System.Text.Json
open Xunit
open BitThicket.Steward.Api
open BitThicket.Steward.Api.Domain

[<Fact>]
let ``toMinorUnits and fromMinorUnits round-trip USD`` () =
    let money = { Amount = 1234.56m; CurrencyCode = "USD" }
    let minor = MoneyHelpers.toMinorUnits money
    Assert.Equal(123456L, minor)
    let back = MoneyHelpers.fromMinorUnits minor "USD"
    Assert.Equal(money.Amount, back.Amount)
    Assert.Equal("USD", back.CurrencyCode)

[<Fact>]
let ``toMinorUnits and fromMinorUnits round-trip BTC`` () =
    let money = { Amount = 0.12345678m; CurrencyCode = "BTC" }
    let minor = MoneyHelpers.toMinorUnits money
    Assert.Equal(12345678L, minor)
    let back = MoneyHelpers.fromMinorUnits minor "BTC"
    Assert.Equal(money.Amount, back.Amount)
    Assert.Equal("BTC", back.CurrencyCode)

[<Fact>]
let ``formatMoney formats USD correctly`` () =
    let money = { Amount = 1234.56m; CurrencyCode = "USD" }
    Assert.Equal("$1,234.56", MoneyHelpers.formatMoney money)

[<Fact>]
let ``formatMoney formats BTC correctly`` () =
    let money = { Amount = 0.01234567m; CurrencyCode = "BTC" }
    Assert.Equal("0.01234567 ₿", MoneyHelpers.formatMoney money)

[<Fact>]
let ``MoneyConverter serializes to amountMinor and currency`` () =
    let opts = JsonSerializerOptions()
    opts.PropertyNamingPolicy <- JsonNamingPolicy.CamelCase
    opts.Converters.Add(MoneyConverter())

    let money = { Amount = 99.99m; CurrencyCode = "USD" }
    let json = JsonSerializer.Serialize(money, opts)
    Assert.Contains("\"amountMinor\":9999", json)
    Assert.Contains("\"currency\":\"USD\"", json)
    Assert.DoesNotContain("\"amount\":", json)

[<Fact>]
let ``MoneyConverter deserializes from amountMinor and currency`` () =
    let opts = JsonSerializerOptions()
    opts.PropertyNamingPolicy <- JsonNamingPolicy.CamelCase
    opts.Converters.Add(MoneyConverter())

    let json = "{\"amountMinor\":50000,\"currency\":\"USD\"}"
    let money = JsonSerializer.Deserialize<Money>(json, opts)
    Assert.Equal(500.00m, money.Amount)
    Assert.Equal("USD", money.CurrencyCode)

[<Fact>]
let ``MoneyConverter handles BTC satoshi precision`` () =
    let opts = JsonSerializerOptions()
    opts.PropertyNamingPolicy <- JsonNamingPolicy.CamelCase
    opts.Converters.Add(MoneyConverter())

    let json = "{\"amountMinor\":123456789,\"currency\":\"BTC\"}"
    let money = JsonSerializer.Deserialize<Money>(json, opts)
    Assert.Equal(1.23456789m, money.Amount)
    Assert.Equal("BTC", money.CurrencyCode)
