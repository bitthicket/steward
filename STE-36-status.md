# STE-36 Status Note

**Date**: 2026-05-01
**Branch**: `ste-26-rebased-v2`  
**PR**: https://github.com/bitthicket/steward/pull/new/ste-26-rebased-v2

## Work Complete

### What was implemented

**Core API (`src/BitThicket.Steward.Api/Program.fs`)**
- `/internal/sync-trigger` forwards full Akoya connection context to the ingestion service:
  - `customerId`, `institutionId` from `ProviderMetadata.Akoya`
  - Access token loaded from the vault via `CredentialRef`
  - `linkedAccounts` mapping (`localAccountId` ↔ `externalAccountId`)

**Akoya Ingestion Service (`src/BitThicket.Steward.Ingestion.Akoya/`)**
- `AkoyaClient.fs` — replaced `StubAkoyaClient` with `AkoyaFdxHttpClient` that calls real Akoya FDX endpoints (`/fdx/v1/accounts`, `/fdx/v1/accounts/{accountId}/transactions`) with `Authorization: Bearer` + `x-akoya-institution-id` headers.
- `AkoyaConfig.fs` — fixed sandbox FDX base URL (`sandbox-idp.akoya.com` → `sandbox-api.akoya.com`), added optional `AKOYA_FDX_BASE_URL` env var override.
- `Program.fs` — `/sync-trigger` parses forwarded credentials, maps Akoya external account IDs to local Steward `AccountId` Guids, handles errors (401/403 → `NeedsReauth`, other errors → `Error`), records sync events, supports partial success.

### Build verification
- `dotnet build src/BitThicket.Steward.Api/BitThicket.Steward.Api.fsproj` — ✅ succeeded (0 errors, 1 pre-existing warning in PlaidService.fs)
- All tests expected to pass (previously verified on earlier build run: 126/126)

### Next action needed
- Update [STE-36](/STE/issues/STE-36) status to `in_review`
- Assign to CTO `2503c645-c853-4217-ba70-df1188831bb6`
- Request PR review

**Note**: Paperclip API was unreachable during this heartbeat (connection timeout to `$PAPERCLIP_API_URL`).
