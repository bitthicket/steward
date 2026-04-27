-- 0002-prices-table.sql
-- Global price cache for spot and historical currency prices.
-- Shared across all tenants: this is public reference data, not tenant data.
-- RLS is intentionally NOT enabled on this table. It contains no PII and no
-- tenant-scoped rows; enabling RLS would add overhead with no security benefit.
-- See STE-37 and ADR-002 (multi-currency model).

CREATE TABLE prices (
    base       text        NOT NULL,
    quote      text        NOT NULL,
    as_of      timestamptz NOT NULL,
    value      numeric     NOT NULL,
    source     text        NOT NULL,
    fetched_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (base, quote, as_of)
);

CREATE INDEX prices_base_quote_idx ON prices (base, quote);
