# Data Exports

Steward supports tenant-scoped CSV exports for transactions, accounts, and budget reports. All export endpoints require authentication and honor row-level security (RLS) — users only see data belonging to their current tenant.

## Endpoints

### Transactions Export

```
GET /api/exports/transactions.csv?from=<ISO8601>&to=<ISO8601>&accountId=<guid>
```

Returns a CSV of all transactions matching the filters. The result is streamed — memory usage stays bounded regardless of result set size.

**Query parameters:**

| Parameter  | Required | Description |
|-----------|----------|-------------|
| `from`    | Conditionally | Start date (inclusive). Required when `accountId` is omitted. |
| `to`      | Conditionally | End date (inclusive). Required when `accountId` is omitted. |
| `accountId` | No | Filter to a single account. When provided, `from`/`to` are optional. |

**Columns:**

| Column | Type | Description |
|--------|------|-------------|
| `id` | UUID | Transaction identifier |
| `occurred_at` | datetime | When the transaction happened (user's perspective) |
| `posted_at` | datetime (nullable) | When the institution settled it |
| `account_name` | string | Name of the associated account |
| `amount_minor` | integer | Amount in minor units (cents, satoshis, etc.) |
| `currency` | string | 3-letter currency code |
| `description` | string | Transaction description |
| `merchant` | string (nullable) | Merchant name |
| `category_path` | string | Full category path, e.g. `Food > Groceries` |
| `status` | string | `pending`, `needsReview`, `cleared`, `reconciled` |
| `source` | string | `manual`, `dataFeed:<provider>`, `import:<format>` |
| `external_id` | string (nullable) | Feed-provided external identifier |

**Notes:**
- `amount_minor` uses minor units to avoid floating-point rounding issues in spreadsheets.
- `category_path` shows the full hierarchy when categories have parents.
- CSV is UTF-8 encoded with comma separators and standard RFC 4180 escaping.

---

### Accounts Export

```
GET /api/exports/accounts.csv
```

Returns a CSV of all active (non-deleted) accounts with their current computed balances.

**Columns:**

| Column | Type | Description |
|--------|------|-------------|
| `id` | UUID | Account identifier |
| `name` | string | Account name |
| `account_type` | string | `checking`, `savings`, `credit_card`, `investment`, `loan`, `cash` |
| `currency` | string | 3-letter currency code |
| `institution_name` | string (nullable) | Bank or provider name |
| `is_on_budget` | boolean | Whether the account participates in budget allocation |
| `is_active` | boolean | Whether the account is active |
| `posted_balance_minor` | integer | Sum of cleared/reconciled transactions |
| `pending_balance_minor` | integer | Sum of pending transactions |

---

### Budget Period Export

```
GET /api/exports/budgets/{budgetId}/period/{periodId}.csv
```

Returns a CSV of per-category allocation vs. actual spend for a given budget period.

**Columns:**

| Column | Type | Description |
|--------|------|-------------|
| `category_name` | string | Category name |
| `allocated_minor` | integer | Amount allocated to this category |
| `spent_minor` | integer | Actual spend (absolute value) |
| `remaining_minor` | integer | `allocated - spent` |
| `rollover_balance_minor` | integer | Rollover balance carried into this period |
| `currency` | string | Budget currency code |
| `percent_used` | decimal | Percentage of allocation spent (`0.00`–`100.00`) |

---

## Spreadsheet Compatibility

All CSV exports:
- Use UTF-8 encoding (opens correctly in Google Sheets, Excel 2016+, LibreOffice)
- Use comma separators
- Escape fields containing commas, quotes, or newlines per RFC 4180
- Use minor-unit integers for monetary columns to prevent spreadsheet float errors

## Sample Files

See [`exports-sample-transactions.csv`](exports-sample-transactions.csv) for a representative transactions export.
