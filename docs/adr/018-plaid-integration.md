# ADR-018: Plaid Link integration and connection lifecycle

## Status

Accepted

## Context

Steward needs to support bank account connections via Plaid Link so that tenants can aggregate transactions from US financial institutions. The Plaid integration spans two services: the Core API owns the Link token flow and connection lifecycle, while the ingestion service (see STE-31 / D1) handles periodic sync. This split keeps user-facing auth handoffs in the API and long-running batch work in a separate Northflank service.

Plaid provides three environments — sandbox, development, and production — selectable via `STEWARD_PLAID_BASE_URL`. The sandbox environment supports test institutions (e.g. `ins_109508`) that simulate all Link flows including re-auth and error webhooks.

## Decision

### Core API owns the Link flow

The Core API exposes four authenticated endpoints for Plaid connections:

1. **`POST /api/connections/plaid/link-token`** — Creates a Plaid `link_token` via `/link/token/create`. The `client_user_id` is derived from the authenticated tenant and user (`{tenantId}-{userId}`). The token is returned to the client; it is never logged or persisted server-side.

2. **`POST /api/connections/plaid/exchange`** — Accepts `{ publicToken, institutionId, institutionName, accounts[] }` from the client (collected during the Link `onSuccess` callback). The server exchanges the public token for an `access_token` and `item_id` via `/item/public_token/exchange`, stores the access token in the vault (see ADR-016), creates a `DataFeedConnection` with `ProviderMetadata.Plaid(itemId, institutionId, cursor=None)`, and persists each selected Plaid account as a Steward `Account` with `external_id` set to the Plaid account ID.

3. **`DELETE /api/connections/{id}``** — Revokes the Plaid item via `/item/remove`, deletes the vault entry, soft-deletes the connection (status → `Disabled`), and soft-deletes all linked accounts. Transactions remain for historical reporting.

4. **`POST /api/connections/{id}/reauth`** — For Plaid connections in `NeedsReauth` status, creates a new Link token in update mode by calling `/link/token/create` with the stored `access_token`. The client launches Link with this token to re-establish consent.

### Environment configuration

Plaid credentials are resolved from environment variables at startup:

- `STEWARD_PLAID_CLIENT_ID` — required
- `STEWARD_PLAID_SECRET` — required
- `STEWARD_PLAID_BASE_URL` — defaults to `https://sandbox.plaid.com`

### Webhook handling

Plaid webhooks are received at `POST /webhooks/plaid`. The handler verifies the JWT signature on the `Plaid-Verification` header (see PlaidWebhookVerifier in `PlaidService.fs`), then dispatches by `webhook_type` and `webhook_code`:

- `TRANSACTIONS` / `SYNC_UPDATES_AVAILABLE` → triggers `IPlaidService.SyncConnectionAsync`
- `ITEM` / `ERROR` with `ITEM_LOGIN_REQUIRED` → sets connection status to `NeedsReauth`
- `ITEM` / `ERROR` with other codes → sets connection status to `Error(...)`

### Re-auth handoff

When a Plaid item returns `ITEM_LOGIN_REQUIRED` (via webhook or sync), the connection status is set to `NeedsReauth` and a `connection.status_changed` event is emitted implicitly through the status update. The frontend detects this status and offers a re-auth button that calls `POST /api/connections/{id}/reauth` to obtain an update-mode Link token.

### Account type mapping

Plaid account types and subtypes are mapped to Steward's closed `AccountType` DU:

| Plaid type | Plaid subtype | Steward `AccountType` |
|---|---|---|
| `depository` | `checking` | `Checking` |
| `depository` | `savings` | `Savings` |
| `depository` | any other | `Checking` |
| `credit` | any | `CreditCard` |
| `loan` | any | `Loan` |
| `investment` / `brokerage` | any | `Investment` |
| any other | any | `Cash` |

Currency is defaulted to `USD` for Plaid-sourced accounts; multi-currency support is out of scope for this integration.

## Consequences

- **User data never touches logs**: The `link_token` and `access_token` are handled in-memory only. The access token is encrypted at rest in the vault (ADR-016). The logging pipeline redacts `accessToken` and `refreshToken` fields via Serilog destructuring policies.
- **Soft-delete preserves history**: Deleting a connection removes the Plaid item and vault secret but keeps transactions intact, which is the expected behavior for financial record-keeping.
- **Sandbox-first testing**: The default `sandbox.plaid.com` base URL means local and CI tests can exercise the full Link flow without real credentials.
- **Split responsibility with ingestion**: The Core API does not perform periodic sync; it only triggers sync on-demand (via `POST /internal/sync-trigger`) or in response to webhooks. The ingestion service (D1) is a separate concern.
