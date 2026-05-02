# ADR-021: Money formatting contract — minor-units internal, formatting at the edge

## Status

Accepted

## Context

The Steward domain model uses `Money = { Amount: decimal; CurrencyCode: string }` internally (ADR-002). We need a consistent contract for how monetary values are serialized over the wire, displayed in the UI, and returned from MCP resources. Key requirements:

- Never leak `amount` as a float/decimal in JSON — always use integer minor units (`amountMinor`) to avoid floating-point parsing issues in clients.
- Support USD (2 decimals) and BTC (8 decimals) from day one.
- Allow on-the-fly currency conversion for reporting and display via the price feed.
- Keep the formatting rule in one place so the portal, API, and MCP all behave identically.

## Decision

1. **Internal representation**: `Money.Amount` stays `decimal`. F#/.NET `decimal` gives 28-29 significant digits, sufficient for satoshi-level BTC and standard fiat.
2. **JSON serialization**: A custom `System.Text.Json.JsonConverter<Money>` serializes every `Money` value as:
   ```json
   { "amountMinor": 123456, "currency": "USD" }
   ```
   The converter is registered globally in `Program.fs` so any endpoint that returns `Money` directly gets the contract automatically.
3. **Explicit DTOs**: For report endpoints that flatten money into fields like `allocatedMinor`, we continue to use `int64` minor-unit fields and include a `currency` string for context. No `amount: 1234.56` fields remain in any public response.
4. **Display formatting**: `MoneyHelpers.formatMoney` produces human-readable strings:
   - USD: `$1,234.56`
   - BTC: `0.01234567 ₿`
   - Fallback: `{amount:N2} {currency}`
5. **Currency conversion**: `PriceConversion.convertMoneyAsync` uses `IPriceProvider.GetSpotAsync` to convert a `Money` value to a target currency. Conversion happens at the edge (report generation, balance endpoint, MCP resources) — never in the domain model.
6. **User preference**: `defaultDisplayCurrency` is stored in `user_preferences` and drives the default conversion target for dashboards and net-worth displays.

## Consequences

- **No float leaks**: Clients never need to parse `decimal` amounts.
- **Deterministic rounding**: `toMinorUnits` uses `Decimal.Round` at the correct number of decimal places per currency, so round-trip (`fromMinorUnits` → `toMinorUnits`) is stable.
- **Easy to extend**: Adding JPY (0 decimals) or ETH (18 decimals) is a one-line change to `MoneyHelpers.decimalPlaces`.
- **Conversion is explicit**: The domain model does not implicitly convert currencies; the caller must request a display currency.

## References

- ADR-002: Multi-currency model
- STE-48: Multi-currency display end-to-end
