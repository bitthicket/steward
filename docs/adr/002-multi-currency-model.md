# ADR-002: Multi-currency model with fiat/crypto distinction

## Status

Accepted

## Context

The product must support USD and BTC from day one, with the ability to add more currencies later. BTC differs from fiat currencies in important ways:

- 8 decimal places (satoshis) vs 2 (cents)
- No single authoritative exchange rate — rates vary by exchange
- Transactions can be sub-cent in dollar terms

We need a currency model that handles both cleanly without over-engineering for currencies we don't yet support.

## Decision

- Define a `Currency` record with `Code`, `Name`, `CurrencyType` (Fiat | Crypto), and `DecimalPlaces`.
- All monetary values use `Money = { Amount: decimal; CurrencyCode: string }`.
- Accounts are denominated in a single currency. Cross-currency transfers are modeled as two transactions in their respective currencies (exchange rate captured at transfer time, not in the domain model itself — that's a service concern).
- Use `decimal` for all amounts. F#/.NET `decimal` gives 28-29 significant digits, sufficient for both satoshi-precision BTC and standard fiat.

## Consequences

- **Extensible**: Adding EUR, GBP, ETH, etc. is just a new `Currency` record — no schema changes.
- **No mixed-currency accounts**: Simplifies balance calculation and avoids implicit exchange rate assumptions.
- **Exchange rates are external**: The domain model doesn't track rates; a separate service handles conversion for reporting/display.
- **Decimal precision**: 28-digit precision handles BTC to satoshi level (8 decimals) and fiat with room to spare. No floating point issues.
