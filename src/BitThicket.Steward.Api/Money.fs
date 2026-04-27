namespace BitThicket.Steward.Api

open System
open System.Text.Json
open System.Text.Json.Serialization
open BitThicket.Steward.Api.Domain

// ─────────────────────────────────────────────────────────────────────────────
// Money helpers: minor-units everywhere internal, formatting at the edge.
// ─────────────────────────────────────────────────────────────────────────────

module MoneyHelpers =

    let decimalPlaces (currencyCode: string) : int =
        match currencyCode.ToUpperInvariant() with
        | "BTC" -> 8
        | "JPY" -> 0
        | _ -> 2

    let toMinorUnits (money: Money) : int64 =
        let places = decimalPlaces money.CurrencyCode
        let factor = pown 10m places
        int64 (Decimal.Round(money.Amount * factor))

    let fromMinorUnits (minor: int64) (currencyCode: string) : Money =
        let places = decimalPlaces currencyCode
        let factor = pown 10m places
        { Amount = decimal minor / factor; CurrencyCode = currencyCode }

    /// Format a Money value for human display.
    /// USD: $1,234.56   BTC: 0.01234567 ₿
    let formatMoney (money: Money) : string =
        match money.CurrencyCode.ToUpperInvariant() with
        | "USD" -> $"${money.Amount:N2}"
        | "BTC" -> $"{money.Amount:F8} ₿"
        | c -> $"{money.Amount:N2} {c}"


// ─────────────────────────────────────────────────────────────────────────────
// System.Text.Json converter: serialises Money as { amountMinor, currency }.
// Never emits a float `amount` field.
// ─────────────────────────────────────────────────────────────────────────────

type MoneyConverter() =
    inherit JsonConverter<Money>()

    override _.Write(writer, money, _options) =
        writer.WriteStartObject()
        writer.WritePropertyName("amountMinor")
        writer.WriteNumberValue(MoneyHelpers.toMinorUnits money)
        writer.WritePropertyName("currency")
        writer.WriteStringValue(money.CurrencyCode)
        writer.WriteEndObject()

    override _.Read(reader, _type, _options) =
        let mutable minor = 0L
        let mutable currency = ""
        let mutable depth = 0
        let mutable inObject = true

        // Simple state-machine read to avoid recursion
        if reader.TokenType <> JsonTokenType.StartObject then
            raise (JsonException("Expected StartObject for Money"))

        while reader.Read() && inObject do
            match reader.TokenType with
            | JsonTokenType.EndObject ->
                inObject <- false
            | JsonTokenType.PropertyName ->
                let prop = reader.GetString()
                reader.Read() |> ignore
                match prop with
                | "amountMinor" -> minor <- reader.GetInt64()
                | "currency" -> currency <- reader.GetString()
                | _ -> reader.Skip()
            | _ -> reader.Skip()

        MoneyHelpers.fromMinorUnits minor currency
