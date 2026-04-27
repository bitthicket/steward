module BitThicket.Steward.Api.Routes

open System
open System.Security.Cryptography
open System.Text.Json
open System.Text.Json.Serialization
open Microsoft.AspNetCore.Authentication
open Microsoft.AspNetCore.Http
open Falco
open Falco.Routing
open BitThicket.Steward.Api.Domain
open BitThicket.Steward.Api.Data

// ─────────────────────────────────────────────────────────────────────────────
// JSON helpers
// ─────────────────────────────────────────────────────────────────────────────

let jsonOptions =
    let o = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)
    o.DefaultIgnoreCondition <- JsonIgnoreCondition.WhenWritingNull
    o

let toJson (value: 'a) = JsonSerializer.Serialize(value, jsonOptions)
let fromJson<'a> (json: string) = JsonSerializer.Deserialize<'a>(json, jsonOptions)

let jsonResponse (statusCode: int) (value: 'a) : HttpHandler =
    fun ctx ->
        let json = toJson value
        ctx.Response.StatusCode <- statusCode
        ctx.Response.ContentType <- "application/json; charset=utf-8"
        ctx.Response.WriteAsync(json)

let plainResponse (statusCode: int) (text: string) : HttpHandler =
    fun ctx ->
        ctx.Response.StatusCode <- statusCode
        ctx.Response.ContentType <- "text/plain; charset=utf-8"
        ctx.Response.WriteAsync(text)

// ─────────────────────────────────────────────────────────────────────────────
// Password hashing (PBKDF2)
// ─────────────────────────────────────────────────────────────────────────────

module PasswordHash =
    let private saltSize = 16
    let private keySize = 32
    let private iterations = 100_000

    let hash (password: string) =
        let salt = RandomNumberGenerator.GetBytes(saltSize)
        let key = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, keySize)
        let saltB64 = Convert.ToBase64String(salt)
        let keyB64 = Convert.ToBase64String(key)
        $"V1${iterations}${saltB64}${keyB64}"

    let verify (password: string) (hashStr: string) =
        let parts = hashStr.Split('$')
        if parts.Length <> 4 || parts.[0] <> "V1" then false
        else
            let iter = int parts.[1]
            let salt = Convert.FromBase64String(parts.[2])
            let expectedKey = Convert.FromBase64String(parts.[3])
            let actualKey = Rfc2898DeriveBytes.Pbkdf2(password, salt, iter, HashAlgorithmName.SHA256, expectedKey.Length)
            CryptographicOperations.FixedTimeEquals(expectedKey, actualKey)

// ─────────────────────────────────────────────────────────────────────────────
// Auth helpers
// ─────────────────────────────────────────────────────────────────────────────

let getUserId (ctx: HttpContext) : Guid option =
    if ctx.User.Identity.IsAuthenticated then
        match Guid.TryParse(ctx.User.FindFirst("sub").Value) with
        | true, g -> Some g
        | false, _ -> None
    else
        None

// ─────────────────────────────────────────────────────────────────────────────
// Route handlers
// ─────────────────────────────────────────────────────────────────────────────

let readJson<'a> (ctx: HttpContext) =
    use reader = new System.IO.StreamReader(ctx.Request.Body)
    let json = reader.ReadToEndAsync().Result
    fromJson<'a> json

type RegisterRequest = { Email: string; Password: string; DisplayName: string }
type CreateTenantRequest = { DisplayName: string; DefaultCurrencyCode: string }

type PatchOnboardingRequest = {
    CurrentStep: int
    CompletedSteps: int list
    Skipped: bool
}

let registerHandler : HttpHandler =
    fun ctx ->
        let req = readJson<RegisterRequest> ctx

        match User.getByEmail req.Email with
        | Some _ ->
            jsonResponse 409 {| error = "Email already registered" |} ctx
        | None ->
            let user = {
                Id = Guid.NewGuid()
                DisplayName = req.DisplayName
                Email = req.Email
                PasswordHash = PasswordHash.hash req.Password
                CreatedAt = DateTimeOffset.UtcNow
                UpdatedAt = DateTimeOffset.UtcNow
            }
            User.create user

            let claims = [
                System.Security.Claims.Claim("sub", user.Id.ToString())
                System.Security.Claims.Claim("email", user.Email)
            ]
            let identity =
                System.Security.Claims.ClaimsIdentity(
                    claims,
                    Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme)
            let principal = System.Security.Claims.ClaimsPrincipal(identity)
            ctx.SignInAsync(principal).Wait()

            jsonResponse 201 {| id = user.Id; email = user.Email; displayName = user.DisplayName |} ctx

let createTenantHandler : HttpHandler =
    fun ctx ->
        match getUserId ctx with
        | None -> plainResponse 401 "Unauthorized" ctx
        | Some userId ->
            let req = readJson<CreateTenantRequest> ctx

            match Tenant.getByOwner userId with
            | Some _ ->
                jsonResponse 409 {| error = "User already has a tenant" |} ctx
            | None ->
                let tenant = {
                    Id = Guid.NewGuid()
                    OwnerUserId = userId
                    DisplayName = req.DisplayName
                    DefaultCurrencyCode = req.DefaultCurrencyCode
                    CreatedAt = DateTimeOffset.UtcNow
                    UpdatedAt = DateTimeOffset.UtcNow
                }
                Tenant.create tenant

                let onboarding = {
                    TenantId = tenant.Id
                    CurrentStep = 1
                    StartedAt = DateTimeOffset.UtcNow
                    CompletedAt = None
                    CompletedSteps = []
                    Skipped = false
                }
                Onboarding.upsertState onboarding

                jsonResponse 201 {| id = tenant.Id; ownerUserId = tenant.OwnerUserId; displayName = tenant.DisplayName; defaultCurrencyCode = tenant.DefaultCurrencyCode |} ctx

let getOnboardingHandler : HttpHandler =
    fun ctx ->
        match getUserId ctx with
        | None -> plainResponse 401 "Unauthorized" ctx
        | Some userId ->
            match Tenant.getByOwner userId with
            | None -> jsonResponse 404 {| error = "Tenant not found" |} ctx
            | Some tenant ->
                match Onboarding.getState tenant.Id with
                | None -> jsonResponse 404 {| error = "Onboarding state not found" |} ctx
                | Some state ->
                    jsonResponse 200 {|
                        tenantId = state.TenantId
                        currentStep = state.CurrentStep
                        startedAt = state.StartedAt
                        completedAt = state.CompletedAt
                        completedSteps = state.CompletedSteps
                        skipped = state.Skipped
                    |} ctx

let patchOnboardingHandler : HttpHandler =
    fun ctx ->
        match getUserId ctx with
        | None -> plainResponse 401 "Unauthorized" ctx
        | Some userId ->
            let req = readJson<PatchOnboardingRequest> ctx

            match Tenant.getByOwner userId with
            | None -> jsonResponse 404 {| error = "Tenant not found" |} ctx
            | Some tenant ->
                let state: OnboardingState = {
                    TenantId = tenant.Id
                    CurrentStep = req.CurrentStep
                    StartedAt = DateTimeOffset.UtcNow
                    CompletedAt = if req.CurrentStep >= 5 then Some DateTimeOffset.UtcNow else None
                    CompletedSteps = req.CompletedSteps
                    Skipped = req.Skipped
                }
                Onboarding.upsertState state
                jsonResponse 200 {| status = "updated" |} ctx

// ─────────────────────────────────────────────────────────────────────────────
// Portal welcome page (static HTML served inline for MVP)
// ─────────────────────────────────────────────────────────────────────────────

let welcomePage : HttpHandler =
    let html =
        """<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<meta name="viewport" content="width=device-width, initial-scale=1.0">
<title>Steward — Welcome</title>
<style>
  * { box-sizing: border-box; margin: 0; padding: 0; }
  body { font-family: system-ui, -apple-system, sans-serif; background: #0f172a; color: #e2e8f0; display: flex; align-items: center; justify-content: center; min-height: 100vh; }
  .wizard { width: 100%; max-width: 420px; padding: 2rem; }
  .step { display: none; }
  .step.active { display: block; }
  h1 { font-size: 1.5rem; margin-bottom: 1rem; color: #38bdf8; }
  label { display: block; margin-bottom: 0.25rem; font-size: 0.875rem; color: #94a3b8; }
  input, select { width: 100%; padding: 0.5rem; margin-bottom: 1rem; border: 1px solid #334155; border-radius: 0.375rem; background: #1e293b; color: #e2e8f0; }
  button { padding: 0.5rem 1rem; border: none; border-radius: 0.375rem; background: #38bdf8; color: #0f172a; font-weight: 600; cursor: pointer; }
  button.secondary { background: transparent; color: #94a3b8; border: 1px solid #334155; }
  .actions { display: flex; gap: 0.5rem; justify-content: flex-end; margin-top: 1rem; }
  .progress { display: flex; gap: 0.25rem; margin-bottom: 1.5rem; }
  .progress div { flex: 1; height: 4px; background: #334155; border-radius: 2px; }
  .progress div.active { background: #38bdf8; }
  .skip { text-align: center; margin-top: 0.75rem; font-size: 0.875rem; color: #64748b; cursor: pointer; }
</style>
</head>
<body>
<div class="wizard">
  <div class="progress" id="progress"></div>
  <div class="step active" data-step="1">
    <h1>Create your account</h1>
    <label>Display name</label>
    <input id="displayName" placeholder="e.g. Alex" />
    <label>Email</label>
    <input id="email" type="email" placeholder="alex@example.com" />
    <label>Password</label>
    <input id="password" type="password" />
    <div class="actions"><button onclick="next()">Next</button></div>
  </div>
  <div class="step" data-step="2">
    <h1>Create your tenant</h1>
    <label>Tenant name</label>
    <input id="tenantName" placeholder="e.g. Personal" />
    <label>Default currency</label>
    <select id="currency"><option value="USD">USD</option><option value="EUR">EUR</option><option value="GBP">GBP</option><option value="BTC">BTC</option></select>
    <div class="actions">
      <button class="secondary" onclick="back()">Back</button>
      <button onclick="next()">Next</button>
    </div>
  </div>
  <div class="step" data-step="3">
    <h1>Connect your first feed</h1>
    <p style="margin-bottom:1rem;color:#94a3b8;font-size:0.875rem;">Link a bank account via Plaid to automatically import transactions. You can skip this and add accounts manually.</p>
    <div class="actions">
      <button class="secondary" onclick="back()">Back</button>
      <button onclick="skipStep()">Skip for now</button>
    </div>
    <div class="skip" onclick="skipStep()">I'll do this later</div>
  </div>
  <div class="step" data-step="4">
    <h1>Set your initial budget</h1>
    <p style="margin-bottom:1rem;color:#94a3b8;font-size:0.875rem;">Choose a budgeting style. A monthly budget with default categories will be created at zero allocation.</p>
    <label>Budgeting style</label>
    <select id="budgetStyle"><option value="ZeroBased">Zero-based (envelope)</option><option value="TraditionalLimits">Traditional limits</option></select>
    <div class="actions">
      <button class="secondary" onclick="back()">Back</button>
      <button onclick="next()">Finish</button>
    </div>
  </div>
  <div class="step" data-step="5">
    <h1>You're all set!</h1>
    <p style="margin-bottom:1rem;color:#94a3b8;font-size:0.875rem;">Your dashboard is ready. You can connect accounts and refine your budget any time.</p>
    <div class="actions"><button onclick="goToPortal()">Open dashboard</button></div>
  </div>
</div>
<script>
  const totalSteps = 5;
  let currentStep = parseInt(new URLSearchParams(location.search).get('step')) || 1;
  let state = { displayName:'', email:'', password:'', tenantName:'', currency:'USD', budgetStyle:'ZeroBased', skipped:[] };

  function render() {
    document.querySelectorAll('.step').forEach(s => s.classList.toggle('active', parseInt(s.dataset.step) === currentStep));
    const p = document.getElementById('progress');
    p.innerHTML = '';
    for (let i=1;i<=totalSteps;i++) { const d=document.createElement('div'); d.classList.toggle('active', i<=currentStep); p.appendChild(d); }
    history.replaceState(null, '', '?step=' + currentStep);
  }

  async function next() {
    if (currentStep === 1) {
      state.displayName = document.getElementById('displayName').value;
      state.email = document.getElementById('email').value;
      state.password = document.getElementById('password').value;
      const res = await fetch('/api/register', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify({ email: state.email, password: state.password, displayName: state.displayName }) });
      if (!res.ok) { alert('Registration failed'); return; }
    }
    if (currentStep === 2) {
      state.tenantName = document.getElementById('tenantName').value;
      state.currency = document.getElementById('currency').value;
      const res = await fetch('/api/tenants', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify({ displayName: state.tenantName, defaultCurrencyCode: state.currency }) });
      if (!res.ok) { alert('Tenant creation failed'); return; }
    }
    if (currentStep === 4) {
      state.budgetStyle = document.getElementById('budgetStyle').value;
    }
    await patchOnboarding(currentStep + 1);
    currentStep++;
    render();
  }

  async function back() { if (currentStep > 1) { currentStep--; render(); } }

  async function skipStep() {
    state.skipped.push(currentStep);
    await patchOnboarding(currentStep + 1, true);
    currentStep++;
    render();
  }

  async function patchOnboarding(step, skipped=false) {
    await fetch('/api/onboarding', { method:'PATCH', headers:{'Content-Type':'application/json'}, body: JSON.stringify({ currentStep: step, completedSteps: Array.from({length:step-1},(_,i)=>i+1), skipped }) });
  }

  function goToPortal() { location.href = '/portal'; }

  render();
</script>
</body>
</html>"""
    fun ctx ->
        ctx.Response.ContentType <- "text/html; charset=utf-8"
        ctx.Response.WriteAsync(html)

let apiRoutes = [
    post "/api/register" registerHandler
    post "/api/tenants" createTenantHandler
    get "/api/onboarding" getOnboardingHandler
    patch "/api/onboarding" patchOnboardingHandler
]

let portalRoutes = [
    get "/portal/welcome" welcomePage
]
