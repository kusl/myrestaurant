# Build progress

This is the running record of how MyRestaurant is being built. It exists so any
future session (or person) can see what is done, what is deliberately stubbed,
and what to watch out for. The technical specification is the source of truth;
this file only tracks execution against it.

## How this was produced (read this first)

The scaffold and each subsequent milestone are written in an environment
**without a .NET SDK** and without NuGet/.NET download hosts. Consequences:

- **The C# for a milestone is written to match the spec and the .NET 10 APIs, then
  first compiled on your machine.** M1 built green, and so did the first five M2
  slices (identity persistence + Argon2id hasher; sign-in/cookie/authorization wiring;
  the password sign-in pages + obligations middleware + claims factory; TOTP
  enrollment; and passkeys). After enabling the Podman socket, the last full local
  sweep was **green with zero warnings: 351 tests, 0 failed, 336 passed, 15 skipped**
  (the 15 remaining skips are the M6 Playwright end-to-end matrix), and `run.sh
  --smoke` passed end to end. The newest slice — the **first-administrator `/setup`
  bootstrap** (this change) — is, like its predecessors were, unbuilt until your next
  build/test run; expect to fix the occasional thing a compiler would catch, most
  likely in the multi-step wizard component (`Components/Account/Pages/Setup.razor`).
- **Package versions in `Directory.Packages.props` are best-effort.** They target
  the .NET 10 GA era. Run `dotnet restore`; if a version does not exist, bump it
  there to the nearest available. Nothing else references versions. **This slice adds
  no packages** and **no new front-end assets** — the wizard reuses the passkey
  ceremony from Slice 5, pointing the existing `wwwroot/js/passkey.js` at a new
  anonymous creation-options endpoint via an optional attribute on `PasskeySubmit`
  (both changes are backward-compatible; the account passkey pages are untouched).
- Shell scripts are syntax-checked with `bash -n`; they may need `chmod +x`.

## Staged plan

The work is split into six stages aligned to the spec's milestones (§19). Each
stage is meant to leave the tree buildable and testable.

- [x] **Stage 1 — M1: skeleton + pure Domain** *(built green: 139 passed, 28 skipped)*
- [ ] **Stage 2 — M2: identity & accounts** *(in progress — identity data layer + Argon2id hasher, sign-in/authorization wiring, the password sign-in flow + obligations middleware, TOTP enrollment, passkeys, and now the first-administrator `/setup` bootstrap have landed; last verified local run before this slice: 336 passed, 15 skipped, 0 warnings)*
- [ ] **Stage 3 — M3: tables & joining**
- [ ] **Stage 4 — M4: ordering**
- [ ] **Stage 5 — M5: counter & administration**
- [ ] **Stage 6 — M6: hardening**

### A note on `run.sh` and quick-tunnel URLs (recurring question, settled)

There is **no milestone anywhere in the plan for `run.sh` to print a
`*.trycloudflare.com` URL and exit** — a previous session said so, and it was right.
The specification splits the concerns deliberately: §14.4 defines `run.sh` as the dev
entry (compose + watch, plus `--smoke` and `--containers-only`, both of which verify
`/healthz/ready` and exit), while §14.3 makes quick tunnels a **demo-only** concern
delivered by a separate helper — `scripts/quick_tunnel.sh` — whose milestone home is
**M6** ("quick-tunnel demo script with warning"), already landed early. Note also
that "deliver a URL **and exit**" is impossible for a quick tunnel: the URL lives
exactly as long as the `cloudflared` process, so the correct shape is what the helper
does — bring the URL up prominently and hold the tunnel open in the foreground. The
demo flow is two commands: `./run.sh --containers-only`, then
`scripts/quick_tunnel.sh`. If the owner ever wants a one-command demo mode, that is a
spec ruling (§14.3/§14.4 + ADR-0005 edits) before it is a script change.

## Stage 1 — done (compiles green)

The first real build succeeded: `dotnet build` clean, `dotnet test` = 139 passed,
28 skipped (the DataAccess integration tests skip without a container engine; the
EndToEnd matrix is an M6 placeholder). Delivered:

- **Solution & build config**: `MyRestaurant.sln` (3 src + 4 test projects),
  `global.json` (SDK 10), `Directory.Build.props` (net10.0, nullable, implicit
  usings, analyzers), central package management in `Directory.Packages.props`,
  `.editorconfig`, `.gitignore`.
- **Domain** (`src/MyRestaurant.Domain`, BCL-only):
  - Orders: `OrderModel` (events, typed operations, projected/ledger records),
    `OrderProjection` (the §8.5 fold), `OrderMutationValidator` (all §6.5
    invariants + §6.3/§6.4 rules).
  - Security: `JoinTokenService` (§4.3 rotating HMAC token), `Argon2PhcString`
    (§3.2 PHC encode/parse/rehash), `Sha256Hashing`, `SecretGenerator` +
    `PairingCode` + `RecoveryCode` (§4.1/§4.2/§3.4).
  - Auth: `ObligationsPipeline` (§3.5 state machine).
  - Time/Identity: `IClock`/`SystemClock`, `IIdentifierFactory`/UUIDv7 factory.
  - Live updates: `IDomainEventBroadcaster` + notification records (§9).
- **DataAccess** (`src/MyRestaurant.DataAccess`):
  - `Migrations/0001_initial_schema.sql` — verbatim §8.2 (22 tables) + §8.3
    (5 views), `citext` extension.
  - `SchemaMigrationRunner` (DbUp, bounded boot retry, fail-fast),
    `IDatabaseConnectionFactory`/`NpgsqlDatabaseConnectionFactory`.
- **WebApplication** (`src/MyRestaurant.WebApplication`):
  - `Program.cs` — options validation → OpenTelemetry → services → **migrate
    before binding HTTP** → forwarded headers → health endpoints → Blazor
    interactive server.
  - `RestaurantOptions` (§13 env binding + fail-fast validation),
    `RestaurantMetrics` (§12 instruments), `InProcessDomainEventBroadcaster`,
    minimal Blazor shell (`App`/`Routes`/`MainLayout`/`Home`), appsettings,
    launch settings.
- **Infrastructure**: `Containerfile`, `compose.yaml`, `Caddyfile`, `run.sh`,
  backup/restore/tunnel scripts, `.env.example`, `README.md`.
- **Tests**: Domain (pure), DataAccess (`SchemaMigrationRunnerTests`, Testcontainers),
  WebApplication (`RestaurantOptionsTests`), EndToEnd (skipped placeholder).

## Stage 2 — in progress (M2 — identity & accounts)

M2 is being landed in coherent, individually-buildable slices.

### Slice 1 — identity persistence layer + Argon2id hasher (landed)

The identity persistence layer and the Argon2id password hasher, wired as Identity
core services and covered by tests. It intentionally stopped short of sign-in flows,
passkeys, and the bootstrap.

- **`Person`** (`DataAccess/Identity/Person.cs`) — the Identity user entity mapping
  the `person` row. No normalized-username/email shadow columns (citext handles
  case-insensitive uniqueness/lookup). Security stamp is a `uuid`; two-factor state
  is derived from `totp_secret_protected` (there is no `totp_required` column).
- **`Argon2idPasswordHasher`** (`DataAccess/Identity/`) — `IPasswordHasher<Person>`
  (§3.2, ADR-0008) over Konscious: 16-byte salt, 32-byte tag, PHC encode/decode via
  the Domain helper, `FixedTimeEquals` verify, `SuccessRehashNeeded` when stored
  parameters drift from configured ones, and a **process-wide `SemaphoreSlim`**
  bounding concurrent hashes. Registered as a **singleton** (so the semaphore is
  real) with a duration hook feeding `password_hash_duration_milliseconds` (§12).
- **`DapperUserStore`** (`DataAccess/Identity/`) — one class implementing the store
  family `UserManager<Person>` needs short of passkeys: `IUserStore`,
  `IUserPasswordStore`, `IUserSecurityStampStore`, `IUserLockoutStore`,
  `IUserTwoFactorStore`, `IUserAuthenticatorKeyStore`,
  `IUserTwoFactorRecoveryCodeStore`, `IUserRoleStore` (read side), `IUserEmailStore`,
  `IUserPhoneNumberStore`. TOTP secret stored Data-Protection-encrypted; recovery
  codes in their own table, SHA-256-hashed and single-use; duplicate/short usernames
  mapped to `DuplicateUserName`/`InvalidUserName` results.
- **`AddRestaurantIdentity`** (`WebApplication/Identity/`) — `AddIdentityCore<Person>`
  with the §3.1/§3.2 options, `AddUserStore<DapperUserStore>()`, default token
  providers (incl. the authenticator/TOTP provider), and the Argon2id hasher
  replacing the PBKDF2 default.
- **Project wiring**: `MyRestaurant.DataAccess.csproj` gained
  `<FrameworkReference Include="Microsoft.AspNetCore.App" />` (Identity core
  abstractions + Data Protection in a non-web library) plus the Konscious package;
  the same FrameworkReference is on `MyRestaurant.DataAccess.Tests.csproj`.
- **Tests**: `Argon2idPasswordHasherTests`, `DapperUserStoreTests` (Testcontainers,
  skips without an engine).

### Slice 2 — sign-in + authorization wiring (landed)

The services the sign-in **flows** need, plus authorization, plus the audit
trail sign-ins write to. No sign-in *pages* yet — those are Slice 3 — so the
`SignInManager` was wired and unit-tested through a pure decision rather than
driven by a form.

- **Cookie authentication** (`WebApplication/Identity/IdentityServiceCollectionExtensions.cs`)
  — Identity's four cookie schemes via `AddAuthentication(...).AddIdentityCookies()`
  (application cookie is the default authenticate/challenge scheme, external is the
  default sign-in scheme — what `AddIdentity` composes internally, minus a
  `RoleManager` we do not want). The application cookie is hardened per §3.1:
  `HttpOnly`, `SecurePolicy=Always`, `SameSite=Lax`, 24-hour **sliding** expiration,
  name `myrestaurant.authentication`; login/logout/access-denied paths point at the
  sign-in surfaces Slice 3 builds.
- **`RestaurantSignInManager`** (`WebApplication/Identity/`) — a `SignInManager<Person>`
  that audits every terminal sign-in outcome once as a `security_event` and once on
  `sign_ins_total{method,result}` (§3.5, §12). It overrides only the password and
  second-factor paths; the passkey path records its own success in the passkey slice
  (overriding `SignInAsync` would double-count the password path, which calls it
  internally). The **decision** of what to record is the pure
  `MyRestaurant.Domain.Authentication.SignInAudit` (unit-tested exhaustively);
  the manager just maps the framework `SignInResult` onto it. Registered via
  `AddSignInManager<RestaurantSignInManager>()`.
- **Security-stamp revalidation** — `SecurityStampValidatorOptions.ValidationInterval
  = 5 minutes` (§3.1). `AddIdentityCookies` points the cookie's `OnValidatePrincipal`
  at the static `SecurityStampValidator`, which resolves `ISecurityStampValidator` /
  `ITwoFactorSecurityStampValidator` at runtime; `AddIdentityCore`/`AddSignInManager`
  do **not** register those (only the monolithic `AddIdentity` does), so they are
  registered explicitly here. This is what makes resets/revocations/deactivations
  bite live sessions within minutes.
- **Roles → claims** — no `RoleManager` and no role entity (roles are plain strings,
  §3.7). The store implements `IUserRoleStore<Person>`; the claims-principal factory
  turns granted roles into role claims at sign-in. *(Slice 2 believed the default
  factory would do this; Slice 3 discovered it does not and fixed it — see below.)*
- **Area authorization policies** (`WebApplication/Authorization/AuthorizationPolicies.cs`)
  — `area.table` (any authenticated person), `area.kitchen` (kitchen **or**
  administrator), `area.counter` (counter **or** administrator), `area.administration`
  (administrator), matching the §3.7 matrix. Role names are the lower-case tokens the
  `person_role` CHECK stores. The display-device policies are an M3 concern.
- **`ISecurityEventLog` + `DapperSecurityEventLog`** (`DataAccess/Identity/`) — the
  append-only writer for the `security_event` table (§8.2), used by sign-in now and
  by the administration service later. Event-type strings are the closed
  `SecurityEventType` vocabulary in the Domain, guarded client-side so a bad value
  fails fast instead of as a CHECK violation.
- **`Program.cs`** — `app.UseAuthentication()` / `app.UseAuthorization()` added after
  static files and before antiforgery/endpoints.
- **Tests**: `SecurityEventTypeTests` + `SignInAuditTests` (pure, always run);
  `DapperSecurityEventLogTests` (Testcontainers — null vs administrator actor,
  round-trip — plus a container-free guard test for the unknown-type rejection);
  `IdentityWiringTests` (builds the container and asserts the `SignInManager` type,
  cookie hardening, the 5-minute stamp interval, and the four policies' role rules).

### Slice 3 — password sign-in flow + obligations middleware (this change)

The §3.5 password path is now drivable by a browser end to end: sign in (with the
TOTP or recovery-code challenge when enrolled), get locked out after five failures,
be forced through a password change after an administrative reset, sign out — and the
first `[Authorize]`-gated area proves the whole chain.

- **Per-page render modes** (`Components/App.razor`) — the interactive-server render
  mode is no longer hard-coded on `<Routes>`/`<HeadOutlet>`; it is chosen per page via
  `HttpContext.AcceptsInteractiveRouting()`. Account pages opt out with
  `[ExcludeFromInteractiveRouting]` and render **static SSR**, because issuing or
  refreshing the authentication cookie requires a real HTTP response — a Blazor
  circuit cannot write cookies. Everything else stays interactive server (ADR-0004).
- **Account routes in one place** (`Identity/ObligationsEnforcement.cs` →
  `AccountRoutes`) — `/sign-in`, `/sign-in/two-factor`, `/sign-in/recovery-code`,
  `/sign-out`, `/access-denied`, `/account/change-password-required`,
  `/account/enroll-totp-required`. The cookie options, the pages, the middleware, and
  the tests all reference these constants so they cannot drift.
- **Sign-in pages** (`Components/Account/Pages/`, all static SSR, plain full-page
  form posts, antiforgery via `EditForm`):
  - `SignIn.razor` — username + password; `isPersistent: true` (the §3.1 session is a
    24-hour sliding cookie either way; persistence lets a guest's phone survive a
    browser restart mid-meal), `lockoutOnFailure: true`. Routes `RequiresTwoFactor`
    to the TOTP page, explains lockout and deactivation, and never reveals whether
    the username or the password was wrong. Return URLs are collapsed through a
    shared open-redirect guard (`SafeLocalReturnUrl`).
  - `SignInTwoFactor.razor` — the TOTP challenge (§3.4/§4.2); tolerant of the
    spaces/dashes authenticator apps display; deliberately **no** "remember this
    device" — TOTP is challenged on every password sign-in of an enrolled account.
    Bounces to `/sign-in` when no pending two-factor user exists.
  - `SignInRecoveryCode.razor` — single-use recovery codes standing in for TOTP;
    the `recovery_code_used` event is recorded centrally by the sign-in manager.
  - `AccessDenied.razor` — where the cookie's `AccessDeniedPath` lands (§3.7).
- **Sign-out** (`Identity/AccountEndpoints.cs`) — a minimal-API **POST** `/sign-out`
  (never GET-triggerable); binding the optional form field turns on the framework's
  automatic antiforgery validation. Clears all four Identity cookies and redirects
  to a safe local URL. No `security_event` is written: sign-out is not in the §8.2
  vocabulary, and sessions also end silently by expiry/stamp rotation, so recording
  only explicit sign-outs would tell a misleading story.
- **Claims factory** (`Identity/RestaurantClaimsPrincipalFactory.cs`) — **fixes a
  latent Slice-2 bug**: the single-generic `UserClaimsPrincipalFactory<TUser>` that
  `AddIdentityCore` registers emits **no role claims** (only the `TUser, TRole`
  variant does, and this app deliberately has no role entity), so the §3.7 area
  policies could never have passed. The restaurant factory adds one role claim per
  granted role (via the store's role read path), the optional display name
  (`myrestaurant:display_name`), and the §3.5 obligation flags
  (`myrestaurant:must_change_password` / `myrestaurant:must_enroll_totp`) as claims.
  Obligations-as-claims means the middleware needs **no database read per request**;
  the claims refresh on the explicit `RefreshSignInAsync` after an obligation clears
  and, at the latest, on the 5-minute security-stamp revalidation.
- **Obligations middleware** (`Identity/ObligationsMiddleware.cs` +
  `ObligationsEnforcement`) — enforces §3.5: an authenticated principal with an
  outstanding flag is redirected (with the original destination as `ReturnUrl`) to
  the page that clears the next obligation, and **nothing else is reachable** except
  sign-out, the pipeline pages, `/access-denied`, health probes, and framework static
  assets. The *decision* is the pure Domain `ObligationsPipeline`; the claim mapping,
  exemption list, and redirect building are testable statics. The Blazor circuit
  endpoint (`/_blazor`) is deliberately **not** exempt: a tab left open when a flag
  lands cannot reconnect its circuit until the pipeline clears.
- **Forced password change** (`Account/Pages/ChangePasswordRequired.razor`) —
  obligation (1): verifies the temporary password, applies the new one, clears
  `must_change_password` **in the same store update** (the flag is flipped on the
  entity before `ChangePasswordAsync`, whose success path is the only one that
  persists), records `forced_password_change_completed`, and `RefreshSignInAsync`
  re-issues the cookie so the cleared claim takes effect on the very next request.
- **Forced TOTP re-enrollment** (`Account/Pages/EnrollTotpRequired.razor`) — a
  **deliberate stub**: the addressable, middleware-exempt home for obligation (2),
  explaining that the enrollment mechanics arrive with the TOTP slice. Nothing in
  the application can set `must_enroll_totp` yet (administrative reset is a later
  slice), so the page is only reachable by hand-editing the database.
- **Deactivation gate** (`RestaurantSignInManager.CanSignInAsync`) — an inactive
  `Person` may not sign in on any path (§3.7, F-10b); the framework surfaces it as
  `NotAllowed`, audited as a failed sign-in. Previously the stamp killed live
  sessions but the front door was open.
- **Router + layout** — `Routes.razor` now uses `AuthorizeRouteView` (anonymous →
  `RedirectToSignIn`, which force-loads because the sign-in page is static SSR;
  authenticated-but-unauthorized → inline denial); `AddCascadingAuthenticationState`
  supplies the auth state in both render modes; `MainLayout` grew a session header
  (username + antiforgery-protected sign-out form, or a sign-in link) that behaves
  identically in static and interactive rendering.
- **First gated area** (`Components/Pages/Table/TableArea.razor`) — `/table` under
  `[Authorize(Policy = area.table)]`: an interactive placeholder proving cookie →
  claims → policy → router → circuit before M3/M4 replace its body.
- **Styling** (`wwwroot/app.css`) — session header, account panels, form fields,
  buttons, and error styling in the same quiet M1 palette; no external assets.
- **Tests**: `ObligationsEnforcementTests` (claims mapping, exemption list, redirect
  targets, the open-redirect guard — pure, always run);
  `RestaurantClaimsPrincipalFactoryTests` (hand-written fake store per §16.1;
  the regression guard for the role-claims fix); `IdentityWiringTests` extended
  (claims-factory registration, cookie paths = `AccountRoutes`).

### Course corrections from the first full local run (2026-07-19 terminal)

The first complete sweep on the owner's Fedora 44 machine (`dotnet clean/restore/
build/test` + `run.sh --smoke`) surfaced no failures — 220 tests, 0 failed, 177
passed, 43 skipped, and the smoke run booted, hit `/healthz/ready` = 200, and exited
cleanly. What *looked* like a wall of errors was two things, both now addressed:

- **Testcontainers could not find the container engine (40 of the 43 skips).** The
  machine runs **rootless Podman** — `run.sh` proved the engine works via
  `podman-compose` in the same sweep — but Testcontainers only probed the Docker
  socket (`unix:///var/run/docker.sock`) and skipped every DataAccess integration
  test with a Docker-flavoured error. Fixes:
  - `tests/MyRestaurant.DataAccess.Tests/ContainerEngineDiscovery.cs` — a
    `[ModuleInitializer]` that, when nothing is explicitly configured
    (`DOCKER_HOST`, `TESTCONTAINERS_HOST_OVERRIDE`, `~/.testcontainers.properties`)
    and the Docker socket is absent, points Testcontainers at the rootless Podman
    socket (`$XDG_RUNTIME_DIR/podman/podman.sock`) and disables Ryuk (unreliable
    under rootless Podman; every fixture disposes its own container anyway). A
    module initializer runs before Testcontainers snapshots its environment, so the
    setting is guaranteed to be seen.
  - `PostgreSqlFixture` — the skip message now states the one-time enable command
    (`systemctl --user enable --now podman.socket`) instead of only echoing the
    Docker error. **Action on the dev machine:** run that command once; the 40
    skips become real passing integration tests on the next `dotnet test`.
  - The remaining 3 skips are by design until their milestones (the EndToEnd matrix
    is M6; two DataAccess tests are also engine-gated).
- **One `xUnit1051` warning** — the container-free guard test in
  `DapperSecurityEventLogTests` now passes `TestContext.Current.CancellationToken`,
  returning the build to zero warnings.
- **`scripts/quick_tunnel.sh` hardening** (M6 deliverable, landed early; §14.3) —
  now pre-checks that the target is actually answering before opening a tunnel
  (failing fast with the `./run.sh --containers-only` hint instead of 502-ing in
  front of an audience) and surfaces the assigned `*.trycloudflare.com` URL in a
  prominent banner the moment cloudflared reports it, while keeping the tunnel in
  the foreground — see the `run.sh`/quick-tunnel note near the top of this file.

### Slice 4 — TOTP enrollment (this change)

Authenticator enrollment is now real, on both the voluntary page and the forced
re-enrollment obligation. A signed-in user can scan a QR, confirm a code, and receive
recovery codes; an enrolled user can regenerate those codes; and the §3.5 obligation
(2) page finally clears its flag instead of parking the user.

- **±1-step TOTP engine** (`Domain/Security/Rfc6238Totp.cs`) — HMAC-SHA-1, 6 digits,
  30-second step, **±1** acceptance window, constant-time compare, RFC 4226 dynamic
  truncation. Pure and BCL-only; verified against the RFC 6238 Appendix B vectors.
  Companions: `Base32Text` (RFC 4648 §6, the secret's on-the-wire/at-rest encoding,
  tolerant of case and grouping on decode) and `TotpProvisioningUri` (the
  `otpauth://totp/…` Key Uri, every component percent-encoded).
- **Why a custom token provider** (`WebApplication/Identity/RestaurantAuthenticatorTokenProvider.cs`)
  — the framework's built-in `AuthenticatorTokenProvider<TUser>` accepts a **±2-step**
  window (confirmed in the .NET 10 source, `Rfc6238AuthenticationService`); §3.4 says
  **±1**. So a custom `IUserTwoFactorTokenProvider<Person>` delegates to the Domain
  engine with the spec's skew and takes "now" from `IClock`. It is registered under
  the same `TokenOptions.DefaultAuthenticatorProvider` name **after**
  `AddDefaultTokenProviders()`; Identity's provider map keeps the last registration
  under a given name, so ours wins (asserted in `IdentityWiringTests`). This changes
  nothing in `RestaurantSignInManager` — `TwoFactorAuthenticatorSignInAsync` dispatches
  by that provider name.
- **Why a stateless protected ticket** (`WebApplication/Identity/TotpEnrollment.cs`)
  — enrollment state is **derived** (`totp_secret_protected IS NOT NULL`; there is no
  pending-secret column), so persisting an unconfirmed secret would switch two-factor
  on before the user proved possession. Instead the GET generates the secret and hands
  the page a Data-Protection-**protected** ticket
  (`v1|{personId}|{issuedAtUnix}|{base32}`, purpose
  `MyRestaurant.Identity.TotpEnrollmentTicket.v1`, distinct from the at-rest secret
  purpose) carried in a hidden field. Confirm unprotects it (catching
  `CryptographicException`), checks it belongs to the signed-in person and is within a
  15-minute lifetime (via `IClock`), verifies the code, and only then writes the
  secret. A **failed code re-posts the same ticket** so the scanned QR stays valid
  (`ResumeEnrollment`); an **expired** ticket yields a fresh QR. Ephemeral-provider
  round-trip, tamper, foreign-key-ring, wrong-person, expiry, and malformed cases are
  unit-tested (`TotpEnrollmentTicketTests`).
- **Commit shape** — confirm sets the key and clears `must_enroll_totp` on the tracked
  entity, then persists both in the single update `UpdateSecurityStampAsync` performs;
  the **stamp bump is the §3.1 credential-changed signal**, and the current session
  survives it via `RefreshSignInAsync` on the page. Recovery codes are written
  separately with the Domain `RecoveryCode.GenerateSet()` (10 × `XXXXX-XXXXX`), because
  the framework's `GenerateNewTwoFactorRecoveryCodesAsync` uses a different format and
  does **not** bump the stamp. **Regeneration alone does not bump the stamp** (it
  changes no sign-in credential) — matching the framework's own behaviour. Exactly one
  security event is recorded per action: `totp_enrolled` (voluntary),
  `forced_totp_enrollment_completed` (forced), or `recovery_codes_regenerated`.
- **QR is server-side and inline** (`TotpQrCode` in the same file) — modules from
  `Net.Codecrete.QrCodeGenerator` (its `Ecc` became an **enum** in 3.x — `QrCode.Ecc.Medium`),
  but the SVG is composed by hand: the library's `ToSvgString` emits an XML prolog and
  DOCTYPE unfit for inlining, so we take `ToGraphicsPath(border: 4)` (the four-module
  quiet zone baked into the path and the `viewBox`) and wrap it in a minimal
  `<svg role="img" aria-label="…">` with a white backing rect. The label is
  HTML-escaped. `TotpQrCodeTests` asserts no prolog/DOCTYPE, a viewBox, a non-empty
  path, no external references, and the accessible label.
- **Pages** (`Components/Account/Pages/`, both static SSR) —
  - `EnrollTotp.razor` (`/account/enroll-totp`, voluntary): two named EditForms on one
    page (`enroll-totp-confirm` / `enroll-totp-regenerate`), each with a matching
    `[SupplyParameterFromForm(FormName = …)]` model so a post binds only its own form;
    the GET picks the setup vs already-enrolled UI from the derived state. **Not** in
    the obligations-exempt list — a user with an outstanding obligation is routed to the
    pipeline, never here.
  - `EnrollTotpRequired.razor` (`/account/enroll-totp-required`, forced): the former
    stub, now the real obligation-(2) flow — same mechanics, `forced: true`, a
    sign-out escape hatch, and GET-time deference to the earlier password-change step
    if that flag is also set. Reads the flag from the database, not the claim, so a
    just-cleared obligation never re-traps.
- **Wiring & chrome** — `AccountRoutes.TotpEnrollment` constant; `TotpEnrollment`
  registered scoped via a factory closing over `RESTAURANT_NAME` (the provisioning
  issuer, §13) so it shares the request's `UserManager`/`DapperUserStore` instance;
  a **Security** link in the authenticated `MainLayout` header; QR/manual-key/recovery
  chip styles plus a `.status-success` in the same quiet palette.
- **Tests**: `Base32TextTests`, `Rfc6238TotpTests` (RFC vectors + ±1/±2 boundaries),
  `TotpProvisioningUriTests` (escaping), `RestaurantAuthenticatorTokenProviderTests`
  (fixed clock at the RFC anchor + fake key store; accept ±1, reject ±2, tolerate
  grouping, fail closed on no key/malformed), `TotpEnrollmentTicketTests` +
  `TotpQrCodeTests`; `IdentityWiringTests` extended (provider-map override +
  `TotpEnrollment` resolves); `ObligationsEnforcementTests` extended
  (`/account/enroll-totp` is blocked).

### Build/test checklist for this slice

1. `dotnet restore` — pulls **one** new package (`Net.Codecrete.QrCodeGenerator`
   3.0.0). If that exact version cannot be found, bump it in
   `Directory.Packages.props` to the nearest available (the API is stable across 3.x).
2. `dotnet build` — the two enrollment Razor components are the most likely home of
   anything a compiler catches.
3. *(one time, dev machine — already done in the last sweep)* `systemctl --user
   enable --now podman.socket` — lets the DataAccess integration tests run.
4. `dotnet test` — expect the previous green set plus the new TOTP suites (roughly a
   dozen-plus new pure/web tests); the EndToEnd matrix still skips (M6).
5. `./run.sh --smoke` — boots once, verifies `/healthz/ready`, exits.
6. Manual: sign in, click **Security** in the header → scan the QR in any
   authenticator app → enter the code → you should see ten recovery codes once.
   Re-visiting **Security** should now offer recovery-code regeneration. (Until
   `/setup` lands, creating a person to sign in **as** still means inserting a row by
   hand or waiting for the bootstrap slice.)

### Slice 5 — passkeys (this change)

WebAuthn passkeys via ASP.NET Core Identity's new .NET 10 passkey API (§3.3). The API
was verified against the framework source before a line was written (BUILD_PROGRESS's
standing instruction), and that reading drove two decisions worth recording (below and
in the caveats): the store persists more than §8.2's original columns, and the passkey
handler is registered by hand.

- **Store — `IUserPasskeyStore<Person>`** (`DapperUserStore.cs`, new region). The
  sixth and last capability interface `UserManager<Person>` needs; the class now
  advertises passkey support (`SupportsUserPasskey`). Own table `passkey_credential`
  (§8.2 + migration 0002), one row per credential: `AddOrUpdatePasskeyAsync` does a
  find-then-insert-or-update, and — mirroring the reference EF store exactly — an
  update writes only the mutable fields (sign count, display name, backed-up,
  user-verified), so the public key and backup-eligible bit captured at registration
  are never clobbered by a later assertion. `FindByPasskeyIdAsync` joins to the owning
  person; transports round-trip as a comma-joined list; attestation object / client
  data are reconstructed as empty on read (see caveats).
- **Migration `0002_passkey_credential_webauthn_state.sql`** — additive, three boolean
  columns (`is_user_verified`, `is_backup_eligible`, `is_backed_up`), all `NOT NULL
  DEFAULT false`. Required because the .NET 10 `UserPasskeyInfo` carries them and
  assertion *reads* the stored backup-eligible bit (see caveats). 0001 is untouched
  (DbUp journals per script, ADR-0012).
- **Wiring** (`IdentityServiceCollectionExtensions.cs`). Registers
  `IPasskeyHandler<Person>` explicitly (see caveats) and configures
  `IdentityPasskeyOptions`: `ServerDomain` = the host of `RESTAURANT_PUBLIC_ORIGIN`
  (the §14.2 origin truth, set so it never drifts to the request host behind the
  tunnel), `UserVerificationRequirement` / `ResidentKeyRequirement` = `preferred`,
  attestation left at the browser default (`none`). `IdentityWiringTests` extended:
  handler resolves, `SupportsUserPasskey` is true, options carry the RP ID + preferred
  settings.
- **Sign-in path** (`RestaurantSignInManager.PasskeySignInAsync` override). Reproduces
  the framework's assertion → `PreSignInCheck` → `AddOrUpdatePasskeyAsync` →
  `SignInOrTwoFactorAsync(bypassTwoFactor: true)` core (assertion is single-use, so it
  is performed exactly once) and adds the central auditing the rest of the manager
  does: `sign_ins_total{method=passkey}` plus a `security_event` once there is a
  subject. `bypassTwoFactor` is the framework's own default here — a passkey is
  already a second factor, so §3.5's "passkey path never gets a TOTP challenge" holds
  by construction; `PreSignInCheck` means the §3.7 deactivation gate still applies.
  The `AuditAsync`/`RecordMetric` helpers grew a `method` parameter (the password and
  two-factor paths pass `password`).
- **Options endpoints** (`AccountEndpoints.cs`): `POST /account/passkey/creation-options`
  (authenticated, attestation) and `POST /account/passkey/request-options` (anonymous,
  assertion — sign-in has no session yet; a username scopes `allowCredentials`, its
  absence enables discoverable/username-less). Both validate the antiforgery token
  from the request header (they are `fetch`ed, not form-posted), matching the template.
  Three route constants added to `AccountRoutes`; none are obligations-exempt (the
  management page is a normal authenticated destination, request-options is anonymous).
- **Client** (`wwwroot/js/passkey.js`) — a classic-script adaptation of the template's
  `passkey-submit` form-associated custom element, pointed at the routes above and
  loaded once from `App.razor`. It runs the browser ceremony, writes the credential
  JSON (or an error) into the surrounding form, and submits natively — which bypasses
  EditForm validation so the passkey button never trips the password rules.
- **Pages.** `SignIn.razor` switched from `OnValidSubmit` to `OnSubmit` (so the passkey
  button can skip the password DataAnnotations; the handler validates by hand only on
  the password path) and gained a "Sign in with a passkey" button + `autocomplete=
  "username webauthn"` for conditional-mediation autofill. New `Passkeys.razor`
  (`/account/passkeys`, static SSR, `[Authorize]`) lists, adds, renames, and removes
  passkeys, recording `passkey_registered` / `passkey_removed`; a **Passkeys** link
  sits beside **Security** in the header. `PasskeySubmit.razor` wraps the custom
  element and supplies the antiforgery header token via `IAntiforgery`.
- **Contracts** (`Identity/PasskeyContracts.cs`): `PasskeyOperation { Create, Request }`
  (names matched to the JS) and the shared `PasskeyInputModel { CredentialJson, Error }`.
- **Tests.** New `DapperUserStorePasskeyTests` (Testcontainers): add/get round-trips
  every stored field, find-by-credential returns the owner, find-passkey is per-user,
  add-or-update rewrites mutable fields only (public key + backup-eligible preserved,
  no duplicate row), remove deletes. `IdentityWiringTests` extended as above.

### Build/test checklist for this slice

1. `dotnet restore` — **no new packages** this slice.
2. `dotnet build` — the passkey Razor components (`SignIn.razor`, `Passkeys.razor`,
   `PasskeySubmit.razor`) are the most likely home of anything a compiler catches.
3. `dotnet test` — expect the previous green set plus the new passkey store suite and
   the three new wiring facts; the EndToEnd matrix still skips (M6). The new store
   tests skip too if no container engine is available.
4. `./run.sh --smoke` — boots once (which applies migration 0002), verifies
   `/healthz/ready`, exits.
5. Manual, on the **stable named-tunnel domain** (passkeys bind to the RP ID, so quick
   tunnels won't keep them — ADR-0005): sign in → **Passkeys** in the header → **Add a
   passkey** → complete the platform prompt → the credential appears in the list. Sign
   out, then **Sign in with a passkey** (or let the username field autofill one). An
   enrolled passkey sign-in should land you in **without** a TOTP challenge.

### Slice 6 — `/setup` first-administrator bootstrap (this change)

The one-time bootstrap that turns a freshly migrated, empty database into a running
system with an administrator (§3.6). `/setup` is reachable **only while zero
administrators exist**; it collects a username, display name, and password, then makes
the operator register a passkey and enroll TOTP (neither is skippable), and finally
grants the `administrator` role — the person recorded as their own grantor — in one
transaction. Once any administrator exists, `/setup` is gone (404).

The tension §3.6 sets up is that a passkey ceremony and a TOTP enrollment each span
several requests, yet the account must be written "in one transaction". This slice
reconciles them by treating everything before the final submit as *verification*, not
persistence: a Data-Protection-protected cookie carries the assembled state across the
wizard's steps, and only the last post writes anything.

- **Bootstrap — `FirstAdministratorBootstrap.cs`** (DataAccess). `IFirstAdministratorBootstrap`
  has the cheap, unlocked `AdministratorExistsAsync` gate and the authoritative
  `CreateFirstAdministratorAsync`. The latter opens its own transaction, takes
  `pg_advisory_xact_lock(hashtext('myrestaurant_setup'))`, **re-checks the
  zero-administrator condition under the lock**, then inserts the person (obligation
  flags cleared — it enrolled its own TOTP; `is_active=true`; a fresh security stamp),
  the verified passkey (including the migration-0002 WebAuthn flags and comma-joined
  transports), ten fresh recovery codes, the self-granted `administrator` row
  (`granted_by_person_identifier` = the new person), and the four `security_event`
  rows (`account_created` / `passkey_registered` / `totp_enrolled` with a NULL actor,
  `role_granted` with the person as their own actor) — all stamped with one clock
  instant. Recovery codes are generated **inside** the commit and returned once (stored
  only as SHA-256 hashes, §3.4). The TOTP secret is protected under the *same*
  Data-Protection purpose the store unprotects with, so the new administrator's
  authenticator works on their first sign-in. If the under-lock re-check finds an
  administrator, nothing is written and the result is `AdministratorAlreadyExists`.
- **Wizard page — `Setup.razor`** (`/setup`, static SSR, `[ExcludeFromInteractiveRouting]`).
  One page, four steps — account details → register a passkey → enroll TOTP → review &
  create — with state accumulating in a Data-Protection-protected, 30-minute cookie
  (`myrestaurant.setup`), never in a circuit (account pages are static SSR by design,
  and a circuit cannot set cookies). The person's UUIDv7 is minted at step one so it
  can double as the WebAuthn **user handle** and equal the eventual `person` id. The
  passkey attestation (`PerformPasskeyAttestationAsync`) and the TOTP code are verified
  as they arrive but **not** persisted; only **Create administrator** calls the
  bootstrap. On success the recovery codes render once (no redirect that would lose
  them) and the operator is signed in as administrator.
- **Reachability.** The page and the endpoint both check `AdministratorExistsAsync` on
  every request and return 404 once an administrator exists — which also covers the
  losing side of a two-browser race (the bootstrap's under-lock result maps to the same
  404 on the final submit). The obligations middleware already ignores anonymous
  requests, so `/setup` needs no exemption.
- **Setup passkey endpoint** (`AccountEndpoints.cs`): a new **anonymous**
  `POST /setup/passkey/creation-options` that reads the pending person id from the setup
  cookie and returns creation options for that handle (404 once an administrator exists;
  400 without a valid cookie). The account creation-options endpoint stays
  authenticated; this one exists because the wizard has no session yet. Two route
  constants added to `AccountRoutes` (`/setup`, `/setup/passkey/creation-options`).
- **Client reuse — no new assets.** `passkey.js` and `PasskeySubmit.razor` gained an
  *optional* creation-/request-options URL (an attribute on the custom element); when
  absent the script keeps its Slice-5 defaults, so the account **Passkeys** page is
  byte-for-byte unaffected. `Setup.razor` simply points `PasskeySubmit` at the new
  setup endpoint.
- **Landing page** (`Home.razor`): a one-time **Set up the first administrator** callout
  linking to `/setup` shows only while no administrator exists, and disappears once one
  does.
- **Store visibility** (`DapperUserStore.cs`): the TOTP-secret Data-Protection purpose
  constant went `private` → `internal` so the bootstrap can protect the secret under the
  exact same purpose without duplicating the string. Store behaviour is otherwise
  unchanged.
- **Wiring** (`IdentityServiceCollectionExtensions.cs`): `IFirstAdministratorBootstrap`
  registered scoped as `DapperFirstAdministratorBootstrap`; `IdentityWiringTests` gains a
  fact that it resolves.
- **Tests.** New `FirstAdministratorBootstrapTests` (Testcontainers): the exists-gate
  flips; a create on an empty database writes every row (person fields, the passkey with
  its flags and transports, ten recovery codes, the self-granted role, all four events
  with the right subject/actor) and the TOTP secret + recovery codes round-trip through
  the store; a second create once an administrator exists writes nothing. New
  `SetupTicketTests` (pure): the protected cookie round-trips every field including the
  verified passkey, and tampered / foreign-key / expired tickets are rejected.

### Build/test checklist for this slice

1. `dotnet restore` — **no new packages** this slice.
2. `dotnet build` — the multi-step wizard component
   (`Components/Account/Pages/Setup.razor`) is the most likely home of anything a
   compiler catches.
3. `dotnet test` — expect the previous green set plus the new `SetupTicketTests` and the
   new wiring fact (both pure, so they always run) and the `FirstAdministratorBootstrapTests`
   suite; that suite skips if no container engine is available, exactly like the other
   Testcontainers tests.
4. `./run.sh --smoke` — boots once, verifies `/healthz/ready`, exits.
5. Manual, on the **stable named-tunnel domain** (the passkey binds to the RP ID, so a
   quick tunnel won't keep it — ADR-0005): visit `/setup` → username + display name +
   password → **register a passkey** (platform prompt) → **scan the TOTP QR** and confirm
   a code → review → **Create administrator**. The recovery codes show **once**, and you
   land signed in as the administrator. Revisit `/setup`: it now returns **404**.

## Known caveats and deliberate decisions

- **Warnings are not errors.** `TreatWarningsAsErrors=false` keeps a fresh clone
  building through analyzer drift. Tighten to `true` once the build is green.
- **Not InvariantGlobalization.** The app relies on `TimeZoneInfo`, so globalization
  stays on and the container installs `tzdata`. Do not set `InvariantGlobalization=true`.
- **DbUp logging.** `SchemaMigrationRunner` uses `LogToConsole()`; if the DbUp API
  differs from what is pinned, that is the likely place a build break appears.
- **Forwarded headers trust.** `Program.cs` clears `KnownIPNetworks`/`KnownProxies`;
  safe only because the app is reached exclusively through a trusted proxy.
- **Rootless volume ownership / compose profiles / container-dependent tests** — as
  before (M1); plus the Podman-socket discovery above for the test suite.
- **M2 — security stamp is a `uuid`.** Identity's opaque Base32 stamp does not fit a
  `uuid` column, so `SetSecurityStampAsync` mints a fresh `Guid` and discards the
  passed string. The value is compared only for equality and regenerated on
  credential/role change, so this is faithful — and it is exactly what makes resets
  bite live sessions once stamp revalidation is wired (wired in Slice 2).
- **M2 — two-factor is derived, not flagged.** `GetTwoFactorEnabledAsync` reads
  `totp_secret_protected`; `SetTwoFactorEnabledAsync(false)` clears the secret.
- **M2 — email/phone confirmation not modeled.** The schema has no confirmation
  columns (optional contact fields, manual escalation only, §11.1); the confirmed-*
  accessors are inert and sign-in never gates on them.
- **M2 — role grant/revoke via the store is `NotSupported`.** `person_role` requires
  the granting administrator (self-referencing for the first admin, §3.6), which the
  parameterless `AddToRoleAsync`/`RemoveFromRoleAsync` contract cannot supply. The
  first-admin self-grant now lands in the transactional `/setup` bootstrap (Slice 6);
  grant/revoke for other people arrives with account administration (next slice). The
  store's role **read** path is complete so claims flow at sign-in.
- **M2 — deletion does not exist (F-10b).** `DeleteAsync` throws; accounts are
  deactivated (`is_active=false`) so history keeps its actors — and, as of Slice 3,
  a deactivated account is also refused at the front door (`CanSignInAsync`).
- **M2 — account pages are static SSR by design.** They post full pages and write
  cookies on the response; do not convert them to interactive components — a Blazor
  circuit cannot set cookies. The per-page render mode in `App.razor` is what makes
  the two worlds coexist.
- **M2 — no *self*-registration page exists.** Guests register at the moment of joining
  a table (§4.3, M3) and staff are created by an administrator (next M2 slice). The
  first account is now created by the `/setup` bootstrap (Slice 6); apart from `/setup`
  (reachable only until an administrator exists) and that future admin surface,
  `/sign-in` is the only public account surface.
- **M2 — obligations block Blazor circuits too.** `/_blazor` is not exempt in the
  middleware: an interactive tab open when a reset lands loses its circuit until the
  pipeline clears. Intended ("nothing else reachable", §3.5); expect the reconnect
  banner in that scenario, not an error.
- **M2 — obligation freshness.** Obligation state travels as claims; a flag an
  administrator sets mid-session bites on the next principal rebuild — immediately
  after the reset in practice, because the reset rotates the security stamp and the
  5-minute revalidation then rebuilds the principal (§3.1). Clearing is immediate
  via `RefreshSignInAsync`.
- **M2 — TOTP skew is ±1 by a custom provider** (see Slice 4). The built-in
  authenticator provider is ±2; §3.4 is ±1, so `RestaurantAuthenticatorTokenProvider`
  overrides it under the default provider name. If a future framework bump changes the
  built-in window or the provider-map ordering, `IdentityWiringTests` will catch it.
- **M2 — the forced-TOTP-enrollment page is real** (see Slice 4): it clears
  `must_enroll_totp` and records `forced_totp_enrollment_completed`. Nothing in the app
  **sets** that flag yet — administrative reset arrives in the account-administration
  slice — so in practice the page is still reached only by hand-setting the flag, but
  the flow behind it is complete and tested. Voluntary enrollment (the Security page)
  is reachable today by anyone signed in.
- **M2 — no voluntary TOTP *removal* surface yet.** The Security page enrolls and
  regenerates recovery codes; it does not remove an enrollment. Removal (and the §4.2
  rule that an admin cannot remove their **own** enrollment) belongs with the
  account-administration / profile slice, alongside the store-level `TotpRemoved` path.
- **M2 — passkeys required a schema addition (0002), a documented deviation from
  §8.2's "verbatim" table** (see Slice 5). The .NET 10 `UserPasskeyInfo` carries
  WebAuthn state the original `passkey_credential` columns did not model, and assertion
  *reads* the stored **backup-eligible** bit and fails the ceremony on a mismatch — so
  it must persist. `0002_passkey_credential_webauthn_state.sql` adds
  `is_user_verified` / `is_backup_eligible` / `is_backed_up` (additive, all `DEFAULT
  false`). Recorded in the spec (§8.2 note) and the review ledger (F-34). This is the
  framework gap §3.3 anticipated ("fallback if a framework gap is found"); no fallback
  library was needed — only these columns.
- **M2 — attestation object and client-data JSON are deliberately not stored.**
  `UserPasskeyInfo` exposes both, but attestation is `none` (§3.3) and nothing in v1
  re-reads either blob (assertion never consults them), so the store reconstructs them
  as empty on read rather than persisting the largest fields for no consumer. If a
  future need appears (e.g. attestation-statement verification), add two `bytea`
  columns in a later migration.
- **M2 — the passkey handler is registered by hand.** `AddIdentityCore` (what this app
  uses) does **not** register `IPasskeyHandler<TUser>` — only the monolithic
  `AddIdentity` does — so `AddRestaurantIdentity` registers `PasskeyHandler<Person>`
  itself, exactly as it already does for the two security-stamp validators. Without it,
  `MakePasskey*OptionsAsync` throws "requires an IPasskeyHandler service" at runtime;
  `IdentityWiringTests` guards the registration.
- **M2 — no post-registration passkey nudge yet.** §3.3 offers passkey enrollment as a
  dismissible nudge *after registration and after sign-in*. There is no registration
  page yet (guests join at a table in M3; staff via admin in a later M2 slice), so the
  durable home — the voluntary **Passkeys** management page — is what ships now; the
  post-registration/sign-in nudge lands with those registration surfaces.
- **M2 — `/setup` verifies across requests but persists in one transaction** (see
  Slice 6). §3.6 requires the first administrator to be written atomically, yet a passkey
  ceremony and TOTP enrollment each span several requests. The wizard resolves this by
  carrying the in-progress state — including the already-verified passkey and the
  confirmed TOTP secret — in a Data-Protection-protected, 30-minute cookie
  (`myrestaurant.setup`) and writing **nothing** until the final **Create administrator**
  post, which commits the whole account in one locked transaction. The person's UUIDv7 is
  minted at step one so it is stable as the WebAuthn user handle and becomes the `person`
  id. Because the state is tamper-evident and short-lived and the endpoint re-checks
  reachability, a stale or forged cookie cannot create an account; `SetupTicketTests`
  pins the round-trip and the rejections. One consequence: `/setup/passkey/creation-options`
  is **anonymous** (unlike the account creation-options endpoint), since there is no
  session yet — it is gated instead by the setup cookie and the zero-administrator
  condition.
- **Dev-machine note — inotify watch limit (handled in `run.sh`).** `dotnet watch`
  can exhaust the kernel's inotify **instance** limit on a busy workstation
  (`The configured user limit (128) on the number of inotify instances has been
  reached`), which killed the watcher. `run.sh` reads
  `fs.inotify.max_user_instances` and, when it is low (< 256) and the caller has not
  set `DOTNET_USE_POLLING_FILE_WATCHER`, falls back to the **polling** file watcher
  for that run so hot reload works without root. For the snappier native watcher,
  raise the cap once — `sudo sysctl fs.inotify.max_user_instances=1024` (persist via
  `/etc/sysctl.d/`) — or force it with `DOTNET_USE_POLLING_FILE_WATCHER=0`. Neither
  the container runtime nor CI is affected.

## Next: remaining M2 slice

**Account administration** (§3.7), the last M2 slice: create staff, grant/revoke roles
(with grantor + `role_granted`/`role_revoked` events), **Reset credentials** (temp
password + `must_change_password`; clear TOTP + set `must_enroll_totp` iff enrolled; new
stamp; `password_reset_by_administrator` [+ `totp_cleared_by_administrator`]),
deactivate/reactivate — the first thing that actually **sets** the obligation flags the
pipeline enforces (Slice 3) and the enrollment pages clear (Slice 4). It reuses the
transactional, self-referencing grant machinery `/setup` introduced (Slice 6), now with a
real grantor. This is also the home for **voluntary TOTP removal**, the §4.2 "an admin
cannot remove their own enrollment" rule, and the grant-time passkey mandate for the
kitchen and administrator roles (§3.7). Store-level integration tests + middleware tests
throughout.
