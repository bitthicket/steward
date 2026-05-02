# ADR-020: Customer Portal Stack — SvelteKit with Adapter Static

## Status

Accepted

## Context

[ADR-006](006-high-level-service-architecture.md) defines the customer portal as a "minimal customer portal — not a rich first-party client. SPA or lightweight SSR; built at deploy time and served by the API."

The portal needs:
- Auth pages (register, login, logout) with minimal bundle size
- A loading-user boundary that hits `/me`
- Multi-tenant login with a tenant picker
- SPA routing so deep links like `/portal/register` work
- Deployment as static files served by the existing Falco/ASP.NET Core API

## Decision

Use **SvelteKit** with `@sveltejs/adapter-static` and `paths.base = "/portal"`.

### Rationale

| Criterion | SvelteKit | Vite + React |
|-----------|-----------|--------------|
| Bundle size | Smaller runtime (~35 KB gzipped for auth pages) | Larger React runtime |
| Ceremony | Minimal — file-based routing, built-in store reactivity | More boilerplate (router, state lib) |
| SPA routing | Built-in, works with adapter-static fallback | Needs react-router-dom |
| Static build | Native via adapter-static | Needs manual SPA config |
| Team familiarity | Lower, but learnable quickly | Higher |
| CSS | TailwindCSS integrates in one line | Same |

SvelteKit wins on bundle size and low ceremony. The team has more React familiarity, but the auth pages are simple enough that the learning curve is negligible. The 300 KB compressed budget is easily met.

### Build & deployment

1. `portal/` is a standalone Node project at repo root.
2. `docker build` runs a `portal-build` stage (`node:22-alpine`) that does `npm ci && npm run build`.
3. The static output (`build/`) is copied into `wwwroot/portal` in the .NET runtime image.
4. The API uses `UseStaticFiles` for `/portal` and a Falco catch-all route `get "/portal/{*path}"` that falls back to `index.html` for SPA routing.

### Auth strategy

- The portal calls `/auth/register` and `/auth/login` (same-origin).
- On success it receives a JWT `accessToken`, then calls `POST /api/auth/cookie-set` to store it in an **HttpOnly, SameSite=Lax** cookie named `steward_auth`.
- Token is **never stored in localStorage**.
- `TenantContextMiddleware` reads the JWT from the `steward_auth` cookie as a fallback when no `Authorization: Bearer` header is present.

## Consequences

- **Positive**: Very small bundle, fast first paint, simple deployment, no extra reverse-proxy rules.
- **Positive**: Cookie-based auth is XSS-safe by design.
- **Negative**: SvelteKit adapter-static with `paths.base` requires careful testing of asset URLs and SPA fallthrough.
- **Negative**: Team may need a short ramp-up on Svelte 5 runes and file-based routing.

## Alternatives considered

- **Vite + React**: Rejected due to larger bundle and extra router/state boilerplate for a minimal portal.
- **Next.js**: Rejected — full SSR framework is overkill for a static portal served by an API that already has its own backend.
- **Blazor WASM**: Rejected — much larger download and the team prefers a JS frontend for ecosystem flexibility.
