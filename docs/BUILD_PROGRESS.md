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
  --smoke` passed end to end. Two slices sit on top of that sweep, each — like its
  predecessors were — unbuilt until your next build/test run: the **first-administrator
  `/setup` bootstrap**, whose likely compiler-catch home is the multi-step wizard
  component (`Components/Account/Pages/Setup.razor`); and, newest, the **F-06a
  quick-tunnel passkey correction** (see the slice at the end of this file), whose
  likely home is the two new Identity classes (`Identity/WebAuthnOriginPolicy.cs`,
  `Identity/PublicOriginMiddleware.cs`) and their wiring.
- **Package versions in `Directory.Packages.props` are best-effort.** They target
  the .NET 10 GA era. Run `dotnet restore`; if a version does not exist, bump it
  there to the nearest available. Nothing else references versions. **The `/setup`
  slice adds no packages** and **no new front-end assets** — the wizard reuses the
  passkey ceremony from Slice 5, pointing the existing `wwwroot/js/passkey.js` at a new
  anonymous creation-options endpoint via an optional attribute on `PasskeySubmit`
  (both changes are backward-compatible; the account passkey pages are untouched). The
  **F-06a** slice likewise adds no packages and **no client change** — `passkey.js` is
  already server-driven; it is server wiring, a script rewrite, and docs.
- Shell scripts are syntax-checked with `bash -n`; they may need `chmod +x`.

## Staged plan

The work is split into six stages aligned to the spec's milestones (§19). Each
stage is meant to leave the tree buildable and testable.

- [x] **Stage 1 — M1: skeleton + pure Domain** *(built green: 139 passed, 28 skipped)*
- [ ] **Stage 2 — M2: identity & accounts** *(in progress — identity data layer + Argon2id hasher, sign-in/authorization wiring, the password sign-in flow + obligations middleware, TOTP enrollment, passkeys, the first-administrator `/setup` bootstrap, and now the F-06a quick-tunnel passkey correction have landed; last verified local sweep: 336 passed, 15 skipped, 0 warnings — the F-06a slice adds pure unit tests only)*
- [ ] **Stage 3 — M3: tables & joining**
- [ ] **Stage 4 — M4: ordering**
- [ ] **Stage 5 — M5: counter & administration**
- [ ] **Stage 6 — M6: hardening**

### A note on `run.sh` and quick-tunnel URLs (updated by F-06a)

There is still **no milestone for `run.sh` itself to print a `*.trycloudflare.com`
URL and exit**, and that remains correct: §14.4 keeps `run.sh` as the dev entry
(compose + watch, plus `--smoke` and `--containers-only`, which verify
`/healthz/ready` and exit), and "deliver a URL **and exit**" is impossible for a quick
tunnel — the URL lives exactly as long as the `cloudflared` process. Quick tunnels
stay in the **separate** helper, `scripts/quick_tunnel.sh` (M6, landed early).

What the F-06a change settled — and it *was* the spec ruling this note previously said
was a prerequisite — is the **shape and scope** of that helper:

- The one-command demo mode is now the shape. `scripts/quick_tunnel.sh` brings
  postgres up, opens the tunnel, discovers the assigned URL, exports it as
  `RESTAURANT_PUBLIC_ORIGIN`, recreates `web`, waits for `/healthz/ready`, and holds
  the tunnel in the foreground. You no longer run `./run.sh --containers-only` first
  (the helper does the bring-up itself); one command is enough.
- Quick tunnels are **not** password+TOTP-only. With per-request RP derivation
  (ADR-0005) and `https://*.trycloudflare.com` trusted by default, **passkeys work on
  a quick tunnel within a run** — including a passkey-only account. The one caveat the
  helper prints loudly: a new run gets a new random URL, so passkeys and bookmarks do
  not carry across runs and must be re-registered; the named tunnel is for durability.

The ADR-0005 / §14.3 edits that make the above true are part of the F-06a slice at the
end of this file.

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
5. Manual, on any origin the RP trusts — dev `https://localhost:8443`, or a quick
   tunnel via `scripts/quick_tunnel.sh` (passkeys work there per F-06a/ADR-0005; a new
   run just means re-registering): sign in → **Passkeys** in the header → **Add a
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
5. Manual, on the **stable named-tunnel domain** — bootstrap is the one flow you should
   *not* run through a quick tunnel: the passkey works there, but a quick tunnel's URL
   changes every run, so the first administrator's credential would not survive the
   next run (F-06a/ADR-0005). Visit `/setup` → username + display name + password →
   **register a passkey** (platform prompt) → **scan the TOTP QR** and confirm a code →
   review → **Create administrator**. The recovery codes show **once**, and you land
   signed in as the administrator. Revisit `/setup`: it now returns **404**.

### Slice 7 — F-06a: passkeys on quick tunnels (this change)

The **course correction** to F-06's original ruling. The spec, ADR-0005, and the wiring
had all assumed a passkey's relying-party ID must be **pinned at boot** to the host of
`RESTAURANT_PUBLIC_ORIGIN`, which made passkeys impossible on a Cloudflare quick tunnel
(its `*.trycloudflare.com` host is random per run and unknowable at startup) and led the
docs to declare quick tunnels "demo-only, password+TOTP." The owner's GoTunnels project
disproves that: it runs a full **passkey-only** flow over a quick tunnel by deriving the
relying party **per request** from the browser's origin when it matches a trusted
`https://*.trycloudflare.com` pattern. This slice brings the same approach to the .NET 10
Identity passkey API and corrects every document that claimed otherwise.

The mechanism rests on one decisive fact confirmed against the ASP.NET Core `release/10.0`
source: `PasskeyHandler` computes the RP ID as `options.ServerDomain ?? Request.Host.Host`,
re-derived on **both** options-generation and verification. So leaving `ServerDomain` null
makes the RP ID follow the request host, and a small middleware guarantees that host is the
browser's real public host.

- **New `Identity/WebAuthnOriginPolicy.cs`** — a pure, HTTP-free policy (unit-testable in
  isolation) that answers "may this origin act as the relying party?" and "what host should
  we present?" from configuration: the configured `RESTAURANT_PUBLIC_ORIGIN`, any
  `RESTAURANT_TRUSTED_ORIGIN_PATTERNS` entry (default `https://*.trycloudflare.com`), and
  loopback in dev. The wildcard matcher mirrors GoTunnels' `MatchOriginPattern` exactly — a
  leading `*.` matches exactly one non-empty DNS label, never a deeper label and never a
  ported host.
- **New `Identity/PublicOriginMiddleware.cs`** — runs immediately after
  `UseForwardedHeaders` and normalizes `Request.Host`: a trusted, unforgeable `Origin`
  header wins (this is what makes the RP ID self-healing across tunnel URL rotations); else
  an already-trusted host is kept; else it falls back to the configured public-origin host
  (covering form POSTs that omit `Origin` behind a proxy that rewrote the host to the
  internal service address). Every branch only ever sets a host the policy already trusts.
- **`Identity/IdentityServiceCollectionExtensions.cs`** — registers the policy as a
  singleton, sets `IdentityPasskeyOptions.ServerDomain = null`, keeps
  `userVerification`/`residentKey` at `preferred`, and sets `ValidateOrigin` to accept only
  a non-cross-origin request whose signed origin the policy trusts. This replaces the old
  `passkey.ServerDomain = options.ResolveWebAuthnRelyingPartyId()` pin — the exact line that
  broke quick tunnels.
- **`Configuration/RestaurantOptions.cs`** — adds a non-required `TrustedOriginPatterns`
  (env `RESTAURANT_TRUSTED_ORIGIN_PATTERNS`, default `https://*.trycloudflare.com`), reads
  and validates it, and updates the `ResolveWebAuthnRelyingPartyId` doc to say it is now the
  QR-URL/fallback host, not the pinned RP ID.
- **`Program.cs`** — inserts `app.UseMiddleware<PublicOriginMiddleware>()` right after
  `UseForwardedHeaders`, before static files / auth / endpoints.
- **`scripts/quick_tunnel.sh`** — rewritten into a one-command orchestrator in the
  GoTunnels "staged startup" spirit: detect the compose engine and a cloudflared runner,
  start postgres, open the tunnel, poll for the assigned `*.trycloudflare.com` URL, export
  it as `RESTAURANT_PUBLIC_ORIGIN`, force-recreate `web`, wait for `/healthz/ready`, print
  the URL in a banner with the corrected caveat (passkeys work this run; a new run = a new
  URL = re-register; never bootstrap a real instance here), and hold the tunnel in the
  foreground with a cleanup trap. It does not mutate the user's `.env`.
- **Docs / ADR.** `docs/adr/0005-...md` rewritten (F-06a revision + history entry);
  `TECHNICAL_SPECIFICATION.md` §3.3, §13 config table, §14.3, accepted-risks, and the
  traceability matrix corrected; `README.md` and `docs/OPERATIONS.md` §10 corrected;
  `.env.example` and `compose.yaml` gain the new variable. ADR-0010 (passkey-only admin
  already permitted) and `wwwroot/js/passkey.js` (already server-driven) needed no change.
- **Tests.** New `Identity/WebAuthnOriginPolicyTests.cs` (dev/prod/no-pattern policies:
  `PublicHost`, `IsTrustedOrigin`, `IsTrustedHost`, `TryResolveTrustedHost`, empty-pattern
  behaviour). `Identity/IdentityWiringTests.cs` swaps its pinned-`ServerDomain` fact for one
  asserting per-request derivation (`ServerDomain` null, `ValidateOrigin` set) plus a theory
  exercising `ValidateOrigin` (localhost + `*.trycloudflare.com` accepted, an evil host and
  any cross-origin request rejected) and a "policy is registered" fact.
  `RestaurantOptionsTests.cs` gains default-pattern binding, list-split binding, and a
  bad-pattern rejection theory. All pure — they run without a container engine.

### Build/test checklist for this slice

1. `dotnet restore` — **no new packages** this slice.
2. `dotnet build` — the two new Identity classes are the most likely home of anything a
   compiler catches (span `ContainsAny` overloads, `HostString` construction).
3. `dotnet test` — expect the previous green set plus `WebAuthnOriginPolicyTests`, the
   revised `IdentityWiringTests` facts, and the new `RestaurantOptionsTests` cases; all pure,
   so they always run.
4. `./run.sh --smoke` — unchanged; boots once, verifies `/healthz/ready`, exits. Confirm the
   dev RP ID is still `localhost` (Caddy forwards `X-Forwarded-Host: localhost:8443`).
5. Manual quick-tunnel proof (the point of this slice): `scripts/quick_tunnel.sh` → open the
   printed `*.trycloudflare.com` URL → register a passkey → sign out → sign in with the
   passkey. It should work. Re-run the script → new URL → the old passkey no longer matches
   (expected) → register again.

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

### M3 Slice 1 — table management: CRUD + join-secret rotation (landed)

The administration area can now manage tables (§4.1). New `ITableAdministration`/
`DapperTableAdministration` (DataAccess/Tables) creates a table with a CSPRNG 32-byte
join secret, renames it (no-change and label-collision detected), rotates the secret
(new 32 bytes + `join_secret_rotated_at` stamped — every outstanding token dies, §4.3),
and deactivates/reactivates — one connection+transaction per op, one IClock instant,
mirroring DapperAccountAdministration. The join secret is never read into a summary or
returned to any caller (§4.1). Read-only `ITableDirectory`/`DapperTableDirectory` lists
tables oldest-first and reads one by id, secret column never selected. Registered by a
new `AddRestaurantTables()` (WebApplication/Tables), wired in Program.cs after
`AddRestaurantIdentity`. Three static-SSR admin pages: `/administration/tables` (list),
`/administration/tables/new` (create), `/administration/tables/{id}` (rename, rotate,
deactivate/reactivate — post/redirect/get). AdministrationHome gained a Tables header
link. Table changes are not in the `security_event` vocabulary, so none are audited.
No packages, no migration (restaurant_table ships in 0001). Tests:
`TableAdministrationTests` (Testcontainers, 11 facts) and `TablesWiringTests`
(resolvability, no container). Next M3 slice: display pairing + device auth + `/display`.

### Fix — TOTP enrollment pages 500 (static-SSR intermediate render deref)

`/account/enroll-totp` (and its obligation sibling `/account/enroll-totp-required`) threw
`NullReferenceException` in `BuildRenderTree` → bare 500. `ComponentBase` issues an intermediate
render via `StateHasChanged` the moment the `await UserManager.GetUserAsync(...)` in
`OnInitializedAsync` suspends, before the async body sets `_enrolled`/`_recoveryCodes`/`_start`.
With all three at defaults the render fell into the setup `else` and dereferenced a null `_start!`,
aborting the static-SSR response. (`Passkeys.razor` escaped it — `_passkeys is { Count: > 0 }`
doesn't match null.) Fix: bare `else` → `else if (_start is not null)`, drop the `!` on the three
`_start` reads, both pages. Final render unchanged; transient render now emits only the panel eyebrow.
No schema/DI/behaviour change; no deletions.

### M3 Slice 2 — table join tokens: generation, validation, metric + admin QR fallback (landed)

The rotating join-token machinery (ADR-0009, §4.3–§4.5) is now wired end-to-end on the
server side. New server-only `ITableJoinSecretReader`/`DapperTableJoinSecretReader`
(DataAccess/Tables) reads a table's `join_secret` for token work — the deliberate narrow
counterpart to `ITableDirectory`, which never selects it — gated on `is_active = true`, so
§4.1's "deactivating stops validation and rendering" is one SQL predicate both paths share.
New `ITableJoinTokens`/`TableJoinTokens` (WebApplication/Tables) wraps the vector-tested
domain `JoinTokenService`: `DescribeCurrentAsync` builds the current QR (token, scan URL,
server-side inline SVG, next-rotation instant) for the counter/admin fallback and the future
display; `ValidateAsync` maps a presented token to the domain result and records
`table_join_tokens_validated_total{result}` (§12) on every attempt — a missing/inactive table
or malformed/old token is counted `invalid`, keeping the label set to §4.3's
{valid|expired|invalid}. One static-SSR admin page `/administration/tables/{id}/join-code`
renders the fallback QR on demand (§4.5/§11.4), reached from a new "Join code" section on
ManageTable; a deactivated/unknown table renders no code. Both services registered by
`AddRestaurantTables()` (scoped, alongside the management services). No migration
(restaurant_table ships in 0001), no packages (Dapper + Net.Codecrete.QrCodeGenerator + the
framework meter, all present), no spec edit (this realizes already-specified behaviour; the
metric and TABLE_JOIN_* config were scaffolded in M1). Tests: `TableJoinSecretReaderTests`
(Testcontainers, 4 facts — exact stored bytes, unknown→null, deactivated→null, rotation
tracked so the old token dies) and `TableJoinTokensTests` (in-memory, 6 facts — current QR
shape, no-secret→null, current/previous window valid, garbage/no-secret invalid), plus two
resolvability facts added to `TablesWiringTests`.

Deferred to the next M3 slice: the join **grant** cookie, `/table/{id}` GET routing
(member / anonymous / valid-token), sitting open + membership, and the `SittingMemberJoined`
broadcast (§4.4/§5.1) — `TableJoinTokens.ValidateAsync` is the hook they consume — followed by
display pairing + device auth + `/display` (§4.2/§11.5).
### M3 Slice 3 — join grant, `/table/{id}` routing, sitting open + membership (landed)

The join flow is now end-to-end (§4.4, §5.1, §9). New `ISittingDirectory`/`DapperSittingDirectory`
(DataAccess/Sittings) answers the two questions the table surface asks — "is this person already a
member of this table's open sitting?" (`GetOpenSittingForMemberAsync`, one round trip, the query
§4.4's "members bypass tokens entirely" rule turns on), "who else is here?" (`ListMembersAsync`,
join-order roster with a username fallback when no display name is set) — plus `GetOpenSittingAsync`
and `ListOpenSittingsForPersonAsync` for the confirmation copy and the `/table` index. Every column
is table-qualified: `table_sitting`, `restaurant_table`, and `table_sitting_member` all carry
same-named identifier columns, and an unqualified reference across the join is exactly how error
42702 bites (the `DapperUserStore` lesson). Rows are read into internal row types with `DateTime`
members and projected to `DateTimeOffset` records, the same Npgsql/Dapper constructor-binding fix
`TableDirectory` and `PersonDirectory` carry.

New `ISittingMembership`/`DapperSittingMembership` is the single write path a consumed grant flows
into. One connection, one transaction, one `IClock.UtcNow` instant, UUIDv7 keys from
`IIdentifierFactory`. §5.1's "atomically" is taken literally: the transaction first takes
`pg_advisory_xact_lock(hashtext('myrestaurant_table_sitting:{table}'))` — keyed per table, not
globally, so two tables never block each other — then re-checks `is_active` and re-reads the open
sitting **under the lock**, so two guests scanning the same display in the same second do not both
find "no open sitting" and race the `table_sitting_one_open_per_table` partial unique index; the
loser joins the winner's sitting. Four outcomes: `SittingOpened` (sitting row + first membership),
`JoinedOpenSitting`, `AlreadyMember` (nothing written — the `UNIQUE (table_sitting_identifier,
person_identifier)` constraint's promise, so a re-scan or a double submit is a no-op), and
`TableUnavailable` (unknown or deactivated table, §4.1). `JoinTableResult.MembershipInserted` is the
exact predicate §9 attaches the broadcast to ("fired on: membership insert"), so an idempotent
re-join announces nothing. Sittings are not in the person-scoped `security_event` vocabulary (§8.2),
so nothing is audited here.

New `JoinGrant`/`JoinGrantProtector`/`JoinGrantCookie` (WebApplication/Tables) is the §4.4 grant,
built exactly like the setup ticket: the payload is the specification's `{table_identifier,
issued_at}` and nothing more, serialized to JSON, Data-Protection-encrypted under its own purpose
(`MyRestaurant.Tables.JoinGrant.v1`, distinct from every other protector so a value from one context
can never be unprotected as another), and carried in a Secure/HttpOnly/SameSite=Lax cookie
(`myrestaurant.join`) whose `MaxAge` is `TABLE_JOIN_GRANT_MINUTES`. The server never trusts that
`MaxAge`: the authoritative expiry is the protected `issued_at`, which a guest cannot edit. The
protector is a singleton (unlike the setup ticket's, which one page constructs ad hoc) because the
table surface reads it on every request in the flow and the display surface will too.

New static-SSR page `/table/{TableId:guid}` (`Components/Pages/Table/TableJoin.razor`), deliberately
**anonymous** — §4.4 requires the grant to be issued *before* the detour through sign-in, and a guest
scanning an unfamiliar table has no account yet. It resolves to one of four states in §4.4's order:
a member sees the table surface with no query string consulted at all (and a table deactivated
mid-sitting does not evict them — §4.1 stops *new* tokens and display rendering, not a sitting in
progress); a signed-in non-member holding a live grant gets the join confirmation; an anonymous
holder of a live grant is redirected to `/sign-in` with this page as the return URL, grant cookie
already written; everything else renders the friendly "that code has expired — scan again" page at
HTTP 200 with one wording for every failure, so an unknown table, a deactivated table, a stale token
and a forged grant are indistinguishable to a prober. A presented token is validated on GET only —
the join POST re-posts to the same URL, query string and all, and re-validating there would
double-count one scan in `table_join_tokens_validated_total`. The join itself is post/redirect/get:
consume the grant (cleared whatever the outcome, so it cannot be replayed), open-or-join in one
transaction, publish `SittingMemberJoined` only when a row was inserted, redirect back.

`/table` (`TableArea.razor`) becomes the index it should be: the open sittings this person is a
member of, oldest first, each linking to its `/table/{id}`. §5.1 allows memberships in several open
sittings at once, so picking one is a real task. It stays interactive-server (it sets no cookie and
issues no redirect) and reads the principal through the cascading `Task<AuthenticationState>`, which
works identically in the prerender pass and on the circuit. New `PersonPrincipal` helper
(WebApplication/Identity) is the one place the person identifier is read off a principal —
`ClaimTypes.NameIdentifier`, the default `ClaimsIdentityOptions.UserIdClaimType` this application
never reconfigures — returning `null` for anonymous, missing, malformed, and all-zero values so every
caller reads a bad claim as "not signed in".

All four services plus the protector are registered by the existing `AddRestaurantTables()`; the
`Program.cs` change is its comment. No migration (`table_sitting` and `table_sitting_member` ship in
0001), no packages, no spec edit — this realizes already-specified behaviour. Tests:
`SittingMembershipTests` (Testcontainers, 9 facts — first join opens the sitting and inserts the
first membership with one instant, a second person joins the same sitting, a repeat join is
idempotent, deactivated and unknown tables write nothing, a new sitting opens after the previous one
closes, the member/non-member split, the closed-sitting predicates, oldest-first listing scoped to
one person, and the roster's display-name fallback), `JoinGrantTests` (8 facts — round trip, missing,
tampered, foreign key ring, foreign purpose, the expiry boundary, the wrong-table refusal, and
cookie/purpose distinctness from the setup flow), `PersonPrincipalTests` (6 facts), and three added
resolvability facts in `TablesWiringTests`.

**Known consequence — obligations outrank scanning.** A staff account with an outstanding
`must_change_password` / `must_enroll_totp` flag that scans a table code is redirected to the
obligation page before `/table/{id}` runs, so no grant is issued on that request (§3.5: nothing else
is reachable). The `ReturnUrl` carries the full URL including the token, so clearing the obligation
lands them back on the scan — and the token still validates if they were quick (worst case ~2× the
rotation, §4.3). This is the pipeline behaving as specified, not a defect; guests, who have no
obligations, never meet it.

Deferred to the next M3 slice: display pairing, device auth, and the `/display/{table}` surface with
its window-aligned QR refresh and party-size chip (§4.2/§11.5) — the chip consumes the
`SittingMemberJoined` broadcast this slice now publishes. Guest self-registration on the join path
(§11.1's "sign-in/registration, passkey-first") is still to come; today an anonymous scanner is sent
to `/sign-in` and needs an existing account.

### Build/test checklist for this slice

- `dotnet build` — green (Razor is the likely site of any compiler catch: `TableJoin.razor` and
  `TableArea.razor` are the two new/changed components).
- `dotnet test` — `SittingMembershipTests` needs a container engine; on rootless Podman,
  `systemctl --user enable --now podman.socket` once. The other three test files never touch one.
- Manual, end to end: `bash scripts/quick_tunnel.sh`, create a table in `/administration/tables`,
  open its `/administration/tables/{id}/join-code`, scan it with a phone that is **not** signed in →
  expect the sign-in page, sign in → expect the join confirmation → Join → expect the table surface
  with your name on the roster. Then hit `/table/{id}` with no query string at all → still the table
  surface (the members-bypass rule). Then sign in as a second person and scan the same code → expect
  the roster to show two people and the sitting count to stay at one.
- Manual, refusals: scan, wait past `TABLE_JOIN_GRANT_MINUTES`, then Join → the expired page, not an
  error. Deactivate the table and scan → the expired page. Edit one character of the
  `myrestaurant.join` cookie → the expired page.

### M3 Slice 4 — display devices: pairing, device authentication, and the `/display` surfaces (landed)

The table screen is real (§4.2, §11.5, §3.7). An administrator generates a one-time code for a table; a
tablet redeems it at the anonymous, rate-limited `/display/pair` and receives a long-lived credential;
`/display/{table}` then shows that table's rotating join QR full-screen, refreshing on the window
boundary, with a party-size chip and an offline state that makes a frozen code impossible to mistake for
a live one. This closes M3's "display pairing + device auth + `/display`" line.

**DataAccess — three services in a new `Displays/` folder.** `IDisplayDeviceDirectory`/
`DapperDisplayDeviceDirectory` lists a table's devices for the administration page, resolving both person
references (pairer, revoker) to usernames and never selecting `device_secret_hash`; live devices sort
before revoked ones. `IDisplayDevicePairing`/`DapperDisplayDevicePairing` is the write path — issue a code
(CSPRNG 8 characters from the §4.2 unambiguous alphabet, stored as SHA-256 with `expires_at`, plaintext
returned exactly once), redeem it, revoke a device. Redeem takes `FOR UPDATE` on the code row so two
tablets racing the same code cannot both pair, re-checks the table is active *under that lock* (§4.1 — a
table deactivated between issue and redeem takes no new screens), mints a 32-byte Base64Url secret,
stores only `sha256(secret)`, and burns the code. `IDisplayDeviceAuthenticator`/
`DapperDisplayDeviceAuthenticator` is the read side of the credential: `AuthenticateAsync` selects the
non-revoked row by identifier and compares the hash with `FixedTimeEquals`; `RevalidateAsync` does the
same by identifier alone, which is what §4.2's "or circuit revalidation" needs once the cookie is out of
reach. Both then run one conditional `UPDATE` that moves `last_seen_at` only when the last sighting is
older than a minute — the whole "at most once per minute" rule in a single race-free statement. All three
follow the established shape: one connection (and, for writes, one transaction) per operation, one
`IClock.UtcNow` instant, UUIDv7 keys from `IIdentifierFactory`, and `DateTime` row types projected to
`DateTimeOffset` records (the Npgsql/Dapper constructor-binding fix `TableDirectory`, `PersonDirectory`,
and `SittingDirectory` all carry). Display devices are not in the person-scoped `security_event`
vocabulary (§8.2), so nothing is audited here.

**Domain.** `PairingCode` gains `Normalize` — upper case, with spaces, hyphens, underscores, dots, and the
Unicode dashes a keyboard autocorrects into removed, bounded at 128 characters so the `stackalloc` is
fixed. It reshapes only; the result still has to pass `IsWellFormed`. The code is read off one screen and
typed into another by a person standing in a restaurant, so `abcd-efgh` has to be `ABCDEFGH`.

**The device principal, and why it is middleware.** `DisplayDeviceCookie` implements §4.2's value verbatim
(`device:{device_identifier}:{secret}`, Secure/HttpOnly/SameSite=Lax, 365 days) with a `TryParse` that
refuses anything else. It is deliberately **not** Data-Protection-encrypted, unlike the join grant: the
grant is a self-describing capability the server must trust on sight, whereas this is a bearer secret
checked against a row every request — sealing it would add nothing and would couple a year-long
credential to the key ring, so a rotation would silently unpair every screen in the building.
`DisplayDevicePrincipal` builds the claims (`NameIdentifier` = device, `Name` = label, the
`myrestaurant:principal_kind` = `table_display` marker, the table id and label) and reads them back,
refusing to answer for anything that is not a display device. It emits **no role claim** — §0/§3.7 say
`table_display` is never a `person_role`, so the four `RequireRole` area policies fail for a screen by
construction — and **no obligation claim**, so the §3.5 pipeline decides "none" and waves it through.

`DisplayDeviceAuthenticationMiddleware` installs that principal. The obvious .NET shape is a second
authentication scheme, and it does not work here: the display surface is interactive, and a circuit takes
its principal from the `/_blazor` request, which authenticates with the **default** scheme — a device
scheme would populate the initial GET and hand the circuit an anonymous principal. Plain middleware runs
on both. It is scoped to `/display*` and `/_blazor`, skips when a person is already signed in (staff on a
paired tablet are themselves), only touches the database when a cookie was actually sent, and clears a
credential it cannot honour so the pairing page can greet a revoked screen with §11.5's "this display was
disconnected".

**Rate limiting.** §4.2's "5 attempts/minute/IP" is a named fixed-window policy registered by
`AddRestaurantDisplays()` and opted into with `@attribute [EnableRateLimiting(...)]` on the pairing page —
component attributes become endpoint metadata, which is exactly what `RateLimitingMiddleware` reads. There
is no global limiter, so nothing else in the app acquires a budget. `RejectionStatusCode` is 429 rather
than the framework's 503 default, and `OnRejected` writes one plain sentence for whoever is holding the
tablet. `app.UseRateLimiter()` **must** sit after `UseForwardedHeaders()`: the partition key is the
connection's remote address, and before the forwarded headers are applied that is the proxy, so the whole
restaurant would share one bucket. Nothing new is installed — `Microsoft.AspNetCore.RateLimiting` and
`System.Threading.RateLimiting` are both in the shared framework.

**Surfaces.** `/display/pair` (anonymous, static SSR — it writes the cookie) resolves three entry states
on GET: already paired (offer the display, do not re-pair), disconnected, or fresh. Every failure —
unknown, reused, expired, malformed, table deactivated — produces one sentence, because anything finer
turns an anonymous endpoint into an oracle for which codes exist. `/display/{TableId:guid}` is
**interactive server** (§11.5 wants a live chip, a window-aligned refresh, and a connection state) and
**anonymous at the endpoint, gated in the component**: §11.5 says an unpaired device is *redirected* to
`/display/pair`, and `[Authorize]` cannot produce that — a failed policy challenges the default scheme and
lands a tablet on `/sign-in`. §3.7's "table claim matches `{table}`" is a route-value comparison and
belongs in the surface for the same reason `/table`'s per-sitting membership check already does. Both use
a new bare `DisplayLayout` rather than `MainLayout`, whose session header and `<AuthorizeView>` would
present a device as though it were a person. `/administration/tables/{id}/displays` issues codes (rendered
in place, never post/redirect/get — the plaintext exists once, exactly like the TOTP recovery codes),
lists devices with last-seen, and revokes via a select-driven form (the shape `ManagePerson` already uses
for roles). `ManageTable` gained the link.

**The refresh loop and the offline state.** The component schedules its next refresh from the QR it just
rendered — `NextRotationAt` plus 250 ms, floored and ceilinged against a clock jump — which keeps it
aligned to §4.3's `(window_index+1) × rotation` without keeping its own notion of the window. Each pass
re-validates the device (catching revocation, and doubling as the `last_seen_at` heartbeat), re-renders
the code, and re-reads the party size. `SittingMemberJoined`/`SittingClosed` re-read it too; the sitting
id is deliberately not compared, because the notification that matters most — the first person joining —
announces a sitting the surface has never seen. Staleness is detected client-side by `wwwroot/js/display.js`
against two attributes the surface publishes (`data-refresh-token`, `data-fresh-for-ms`), with the deadline
measured from when the **script** observed the token change rather than from a server timestamp: a kiosk's
clock is frequently wrong by minutes, and a skewed clock must not be able to declare a healthy display
dead or a dead one live. The same script holds the screen wake lock and re-acquires it on
`visibilitychange` (§11.5, §10.3). It is inert on every page without the surface, so loading it from
`App.razor` alongside `passkey.js` costs one element lookup a second.

No migration (`table_display_device` and `table_display_pairing_code` ship in `0001_initial_schema.sql`),
no packages, no spec edit — this realizes behaviour §4.2, §3.7, and §11.5 already specify. Tests:
`DisplayDevicePairingTests` (Testcontainers, 14 facts — hash-only storage, the whole redeem happy path,
single-use, expiry, malformed codes as a theory, human-typed codes, the derived label, the
deactivated-after-issue refusal, revoke-once-and-stay-revoked, and the directory's view of all of it),
`DisplayDeviceAuthenticatorTests` (Testcontainers, 8 facts — correct secret, wrong secret, another
device's secret, unknown device, revoked device, deactivated table still authenticates, the once-a-minute
heartbeat, and revalidation), `DisplayDeviceCookieTests` (12 facts, no container), `DisplayDevicePrincipalTests`
(7 facts — including the two that matter most: a signed-in person is not a device even though it also has
a `NameIdentifier`, and a device holds no role), and `DisplaysWiringTests` (6 facts).

**Known consequence — a display is a device everywhere it is allowed to be, and nowhere else.** Because
the middleware is path-scoped, a kiosk browser walked to `/` or `/table` is an ordinary anonymous visitor
there. That is intended: no other surface has to defend against a principal whose `NameIdentifier` is a
device rather than a person, and `PersonPrincipal` — which reads that same claim — is never handed one.

Deferred: nothing in §4.2 or §11.5 remains. Next is M4 — the living order, the §6.6 locking protocol,
staging and batch send, fulfillment, and the kitchen surface.

### Build/test checklist for this slice

1. `dotnet restore` — **no new packages** this slice.
2. `dotnet build` — the four new Razor components are the most likely home of anything a compiler catches;
   `Components/Pages/Display/TableDisplay.razor` most of all, being the first interactive component here
   with a background loop, a broadcaster subscription, and `IDisposable`.
3. `dotnet test` — expect the previous green set plus the two Testcontainers display suites (which skip
   without a container engine) and the three pure ones (which never do).
4. `./run.sh --smoke` — boots once, verifies `/healthz/ready`, exits. No migration runs; 0001 already has
   both display tables.
5. Manual, end to end, on a quick tunnel (`bash scripts/quick_tunnel.sh`) with a second device or a second
   browser profile standing in for the tablet:
   - Administration → Tables → a table → **Manage displays** → **Generate pairing code**. The code shows
     once, with its expiry.
   - On the "tablet", open `/display/pair`, type the code (try it lower-case and hyphenated — it should
     still work), name the display, submit. Expect a redirect to the full-screen table display with a QR.
   - Watch it past a rotation boundary: the QR must visibly change and the code must keep working.
   - Scan it with a phone and join. The party-size chip should go from **Table free** to **1 person**
     without touching the display.
   - Kill the app (`podman-compose --profile dev stop web`) and watch the display: within about
     `TABLE_JOIN_TOKEN_ROTATION_SECONDS + 10`, the red **Reconnecting…** curtain must cover the QR.
6. Manual, refusals:
   - Enter the same code twice → the second attempt is refused with the same wording as a typo.
   - Enter six wrong codes inside a minute → the sixth returns **429** with the plain-text message.
   - Administration → **Manage displays** → **Revoke display**, then wait one rotation on the tablet → it
     should navigate itself to `/display/pair` and say **This display was disconnected.**
   - Deactivate the table → the display shows "out of service" rather than a code, and stays paired.
   - Open `/display/{some-other-table}` on the tablet → **Wrong table**, with a link back to its own.

### M2 close-out — the person's own profile: display name, contact details, password (landed)

A gap found in use rather than in review, and closed before M4 opens (ledger F-35). §4.6 and §11.1 both
say every person has a profile page; §19's build order never named it, so no milestone claimed it. M2
shipped the individual credential pages — `/account/enroll-totp`, `/account/passkeys` — with no hub above
them and no surface at all for the three fields that belong to the person rather than to a role. Two
freshly created staff accounts could not set their own display names, and there was nowhere to change a
password voluntarily either (only the forced §3.5 page existed). §11.2 groups the kitchen queue by *person
display name*, so M4 would have inherited tickets reading `betty`.

**New `Identity/ProfileDetails.cs`** — a pure record carrying the three self-editable fields with
`Normalize`, `Validate`, and `SameAs`. Normalization is trim + internal-whitespace collapse + control-
character removal in one pass, with blank collapsing to `null` (the schema's "unset" for all three
columns); `Booth  1` and `Ad\0am` are the cases that motivated it. Validation is deliberately loose:
these contact fields exist for *manual* escalation only (§4.6), nothing ever sends to them, and no paid
sending service is permitted, so there is no deliverability to check and nothing is gained by rejecting an
unusual-but-plausible value. It refuses only what is certainly a mistake — no `@`, an undotted domain,
letters in a phone number, fewer than three digits. `SameAs` normalizes the *stored* side too, so a row
written before this page existed does not read as "changed" forever, and compares the e-mail
case-insensitively because the column is `citext`. It lives outside the component for the reason
`ObligationsEnforcement`, `WebAuthnOriginPolicy`, and `PairingCode.Normalize` do: a static-SSR Razor
component is not unit-testable here (no bUnit, §16.1), so the parts with decisions in them move out.

**New `/account` (`Components/Account/Pages/Profile.razor`)** — static SSR, `[Authorize]` only, no area
policy: every authenticated principal has one, guest to administrator. **Your details** is one form
writing one `person` row update through `UserManager.UpdateAsync`, which (verified against
`dotnet/aspnetcore` `release/10.0`) validates and normalizes but does **not** rotate the security stamp —
correct, since none of these three fields is a credential and live sessions have no reason to be cut. The
username renders read-only with the reason. A changed display name *does* call `RefreshSignInAsync`,
because the name travels as a claim (`RestaurantClaimsPrincipalFactory`) and would otherwise not appear
until the five-minute revalidation. No `security_event` is written: §8.2's vocabulary is closed and has no
profile-edit type, which is right. **Sign-in and security** is a status row per credential — password
set/unset, authenticator enrolled/not, passkey count from `UserManager.GetPasskeysAsync` — each linking to
the surface that owns it. Post/redirect/get on save, carrying a one-word outcome, so a refresh does not
re-post; a no-op save is detected and reported as "nothing changed" rather than writing a row.

**New `/account/change-password` (`Components/Account/Pages/ChangePassword.razor`)** — the voluntary
password surface, distinct from `ChangePasswordRequired.razor`, which is obligation (1) of §3.5, is exempt
from the pipeline, and clears the flag. This one is an ordinary authenticated destination, so a person with
an outstanding obligation is routed to the forced page and never lands here. Two branches and two named
forms, because a passkey-only account (§3.2) has no current password to confirm and `[Required]` cannot be
made conditional: `change-password` → `ChangePasswordAsync`, `add-password` → `AddPasswordAsync` (which
re-reads the stored hash and refuses if one appeared meanwhile, so a stale form cannot overwrite). Both
record `password_changed` — §8.2 has no separate "added" type, and from the audit's point of view that is
what happened — with a null actor, the §8.2 convention for "the subject did it themselves". Both then call
`RefreshSignInAsync`: the framework rotates the security stamp inside `UpdatePasswordHash` on *both* paths,
so without it the person signs themselves out of the session they are sitting in.

**Both pages guard every render branch on `_person`.** This is the `/account/enroll-totp` 500 lesson
applied prospectively: `ComponentBase` issues an intermediate render the instant the `await` in
`OnInitializedAsync` suspends, with every field still at its default, and a dereference there aborts the
whole static-SSR response with a bare 500.

### Fix — role checkboxes were stretched and unlabelled (staff creation)

Reported as "the checkboxes don't feel aligned to anything", and it was not a design choice. `app.css` has
`.form-field input { width: 100%; padding: 0.6rem 0.75rem; border-radius: 8px }`, and `CreateStaff.razor`
put `class="form-field roles-fieldset"` on the fieldset — so each `InputCheckbox` inside inherited a text
field's full width, padding, and corner radius. Worse, the page's inline `.role-option { display: flex }`
**loses on specificity** to `.form-field label` (0,1,1 against 0,1,0), so the label reverted to
`display: block` and its text dropped below the stretched box.

Two changes. `app.css` gains an `input[type="checkbox"] / input[type="radio"]` override — an attribute
selector outranks the bare `.form-field input` regardless of source order, so it holds wherever a checkbox
lands in a form field — and a shared `.choice-fieldset` / `.choice-list` / `.choice` vocabulary for option
rows, deliberately two classes deep so it outranks `.form-field label`. `CreateStaff.razor` adopts it and
drops its inline `<style>` block; the fieldset no longer carries `.form-field` at all. `:has()` marks the
checked row as progressive enhancement — everything works without it. While there, `.form-actions` became
a wrapping flex row with a gap, which fixes button/link pairs butting together on several existing pages,
and the `.chip` / `.chip-ok` / `.chip-warn` / `.chip-role` / `.muted` vocabulary moved into `app.css`: it
had been invented inline twice (namespaced in `AdministrationHome`, bare in `ManagePerson`) so a third page
wanting a status chip had nowhere to reach. The two inline copies are redundant but harmless and are left
alone; fold them in whenever those pages are next edited.

### Fix — one Account link in place of two

`MainLayout` carried a **Security** link and a **Passkeys** link side by side. With the person's name and
the administrator link, that wrapped onto three rows at 375px (iPhone SE), and neither link was where you
would look for a display name. Both are replaced by one **Account** link to the new hub, which is also
where the header stops growing every time a credential surface is added.

### Documentation

Atomic, per §18: `REQUIREMENTS.md` §4.6 now states display-name editing and username immutability outright,
and records that the three fields are the person's own (the §4.5 administrative powers stop at credentials,
roles, and activation); `TECHNICAL_SPECIFICATION.md` §11.1 points at a new **§11.6 `/account`** specifying
the surface, and §19's M2 line names the profile page and admits it landed after M3;
`DOCUMENTATION_REVIEW.md` gains **F-35**. Addresses stay unsurfaced with the reason recorded in §11.6 —
nothing in version 1 consumes an address, and a form for data no reader exists for is scaffolding
pretending to be a feature. No ADR is affected.

### Build/test checklist for this slice

1. `dotnet restore` — **no new packages**, no migration, no DI change (both pages resolve `UserManager`,
   `SignInManager`, and `ISecurityEventLog`, all already registered; `ProfileDetails` is a pure type with
   no lifetime).
2. `dotnet build` — the two new Razor components are the likely home of anything the compiler catches.
3. `dotnet test` — the previous green set plus `ProfileDetailsTests` (16 pure facts and theories; no
   container engine, so they always run).
4. Manual: sign in as **adam** → **Account** in the header → set a display name → save → the header and
   the `/table` roster both show it. Change the password → you stay signed in (that is the
   `RefreshSignInAsync`; if you get bounced to `/sign-in`, the refresh did not happen). Save with nothing
   altered → "Nothing changed", no write. Enter `adam@example` → refused; leave both contact fields blank
   → accepted.
5. Manual, the checkbox fix: Administration → **Create staff account**. The three role rows are bordered
   cards with a normal-sized checkbox aligned to the first line of its label, and the checked one is
   tinted. Check it at 375px.
6. Manual, refusals: as a person with an outstanding `must_change_password`, hit `/account` → the forced
   page, not the profile (§3.5 — the profile is deliberately *not* on the exempt list).

**Still deferred from M2**, unchanged by this slice: voluntary TOTP *removal* and the §4.2 "an
administrator cannot remove their own enrolment" rule, which need a store-level `TotpRemoved` path; the
grant-time passkey mandate for the kitchen and administrator roles in the create-staff-without-session
case; and guest self-registration on the join path (§11.1), where an anonymous scanner is still sent to
`/sign-in` and needs an account already.

**Next: M4 — ordering.** Living order plus the §6.6 locking protocol, staging UI, batch send with
all-or-nothing validation, staff edits, fulfillment and reversal, projections with fold-equivalence tests,
and the kitchen surface with its alerts and reminder service.
### M4 Slice 1 — the order engine: the §6.6 transaction, the projections, and the menu read side (landed)

M4 opens with the part that has no user interface at all. Every screen in §11.1–§11.3 — the guest staging
area, the kitchen queue, the counter's bill — is a different rendering of one transaction and two
projections, and building any of them on top of a shaky write path would mean debugging Razor and
row-level locking at the same time. So this slice is DataAccess plus a thin post-commit shell, no
components, and it leaves M4's remaining work as presentation over a tested engine.

**The write path — one method, one transaction, §6.6 verbatim.** New `IOrderMutations`/
`DapperOrderMutations` (DataAccess/Orders) is the single way an order ever changes. There is deliberately
no "add a line" or "fulfill a line" method: §6.5.9 is all-or-nothing at the granularity of the *event*,
and a per-operation API would make honouring that impossible. Two entry points funnel into one core.
`AppendToLivingOrderAsync(sitting, owner, proposed)` is the guest send path and creates the order lazily
(§6.1) with `INSERT … ON CONFLICT (table_sitting_identifier, person_identifier) DO NOTHING` followed by
the locking re-select, which is exactly §6.1's "a lost creation race is re-read and proceeds".
`AppendToOrderAsync(order, proposed)` is the kitchen and counter path, acting on an order they are
looking at. It reads the order's sitting *without* a lock first — the column is immutable once the row
exists — because §6.6 puts the sitting lock before the order lock, and taking them the other way round on
this one path would invert the lock order and invent a deadlock.

Then, in order: `SELECT … FOR SHARE` the `table_sitting` row (which conflicts with the close
transaction's `FOR UPDATE`, and that conflict is the whole guarantee that no event slips past a close);
`SELECT … FOR UPDATE` the `guest_order` row; `coalesce(max(sequence_number), 0) + 1` under that lock, so
`UNIQUE (guest_order_identifier, sequence_number)` can never fire; read the prior log, the menu, the
sitting's open flag, ownership and membership; run `OrderMutationValidator`; insert the event, its
operations, and — when §10.1 says so — the `kitchen_notification` row, all in the same transaction;
commit. Step (g), the broadcast, is not here on purpose.

**Two behaviours that are not in the validator.** First, **the server prices every added line**. §6.5.4
says the unit price is "set server-side from the current menu price (client-sent prices are ignored)", so
whatever `UnitPriceAmount` arrives with is replaced by the `menu_item.price_amount` read inside this
transaction — and the rule is applied to staff edits as well as guest submissions, because the menu is
the price authority for an *add* and a counter who means to charge something else has
`price_adjustment`, which demands a reason and shows old → new on the bill. Second, **free text is
trimmed, never rejected**: customization notes and removal reasons are collapsed to `NULL` when blank,
because §7 is explicit that notes are never validated against any rules engine. A blank
price-adjustment reason is the exception and is passed through untouched, so the validator reports it
rather than the `btrim(reason) <> ''` CHECK exploding.

**The read side — two of them, and they have to agree.** `IOrderReadModel`/`DapperOrderReadModel` reads
the §8.3 views: `order_current_line` (per order, per sitting), `order_current_state`, `sitting_bill` with
the person's names joined on, and `kitchen_pending_line` with the §11.2 grouping keys and a
`COALESCE(NULLIF(btrim(display_name), ''), username)` so a freshly created account does not produce a
blank ticket header. `IOrderEventLog`/`DapperOrderEventLog` reads the raw log into domain `OrderEvent`s in
two queries — the headers, then all five typed operation tables folded into one flat set by `UNION ALL`
with every branch cast to its target type, because in a union PostgreSQL resolves the column type from
the branches and a bare `NULL` would leave it `unknown`. That log is what the validator sees under the
lock, what §8.5's equivalence test folds, and what §11.4's "complete stored record, never projected or
truncated" will render.

**§8.5, the load-bearing assertion.** `OrderReadModelTests.Views_AndTheDomainFold_AgreeOnARandomisedEventSequence`
drives 60 real events — sends, removals, fulfillments, reversals, price adjustments, counter staff edits
— through the real transaction with a **seeded** generator (a projection bug that reproduces only on
Tuesdays is worse than no test), then compares the fold against the views order by order: every line's
item, quantity, current price, note, fulfillment flag, added-at and adding event; both counts; the total;
first-submitted and last-event instants; and that sequence numbers are dense from 1. Some generated
events are rejected by §6.5 and that is left in deliberately — a rejected event must leave the log and the
views equally untouched.

The comparison is by line **set**, not by row order, and that is a finding worth recording. Lines added
in one send share an `occurred_at` to the microsecond, so the tie-breaker decides the order — and the two
tie-breakers cannot agree: the fold's `ThenBy(Guid)` uses .NET's `Guid.CompareTo` (Data1 as an `int`, then
two `short`s, then bytes) while the view's `ORDER BY` uses PostgreSQL's bytewise `uuid` collation. Both
are stable, neither is wrong, and §8.5's wording is about "the line set, prices, and fulfillment flags"
precisely because ordering was never part of the contract. The SQL ordering is in fact the better of the
two for a reader: line identifiers are UUIDv7 minted in staging order, and PostgreSQL's bytewise
comparison puts a UUIDv7's timestamp first, so the view returns a send's lines roughly in the order the
guest staged them.

**Operations within one event are a set, too.** The same UUIDv7 reasoning cuts the other way here: the
operation surrogate keys give the read a deterministic order, but not necessarily the insertion order,
since two keys minted inside one millisecond differ only in their random bits. The schema records no
ordinal within an event and nothing needs one — §6.5.5 forbids the one intra-event ordering that could
change an outcome (removing a line the same event added), and the views break same-event ties arbitrarily
too, because every operation of one event shares its sequence number.

**Menu, read side only.** New `IMenuDirectory`/`DapperMenuDirectory` (DataAccess/Menu) lists every item
ordered by name and reads one by identifier. It returns **inactive items too** — §7 requires a
deactivated item to stay on the menu marked "currently unavailable" rather than vanish, "the guest sees
that the salmon exists and is out", and a directory that filtered them would break that quietly. Menu
*administration* — create, rename, reprice, activate/deactivate, each appending a `menu_item_event` — is
M5 (§19) and will bring its own write interface; the read side lands now because ordering is unbuildable
without it and is its only consumer today.

**The post-commit shell.** New `IOrderWorkflow`/`OrderWorkflow` (WebApplication/Orders) is what surfaces
call; they never touch `IOrderMutations` directly. It records the §12 counters
(`guest_submission_batches_total`, `order_lines_added_total`, `order_lines_removed_total`,
`order_lines_fulfilled_total`) and publishes the §9 notifications after commit: `OrderLinesChanged`
unconditionally ("any order event commit"), `LineFulfillmentChanged` additionally on fulfillment and
reversal, and `KitchenAlert(initial)` **only when the transaction actually wrote the row** — the workflow
does not re-derive §10.1, because a change to one copy of that rule and not the other would leave the
sound and the stored record disagreeing. Nothing is published or counted for a rejected event. All five
services are registered by a new `AddRestaurantOrders()`, wired in `Program.cs` after
`AddRestaurantDisplays()`.

No migration (every table ships in `0001_initial_schema.sql`), no packages, no spec edit — this realizes
behaviour §6, §7, §8.3, §8.5, §9, §10.1, and §12 already specify. Tests: `OrderMutationsTests`
(Testcontainers, 11 facts — lazy creation and server pricing, one notification per send and the next
sequence number, all-or-nothing rejection leaving not even the order row behind, the non-member refusal,
guest removal of their own pending line but not a fulfilled one, fulfillment and its reversal being
silent and alternating, price adjustment with and without a reason, counter-alerts-kitchen-doesn't, the
closed-sitting matrix, unknown sitting and unknown order, and the empty-event and cross-order refusals),
`OrderReadModelTests` (Testcontainers, 5 facts including the §8.5 equivalence), `MenuDirectoryTests`
(Testcontainers, 3 facts), `OrderWorkflowTests` (7 facts and theories, no container), and
`OrdersWiringTests` (5 facts, no container).

**Known consequence — a post-close administrative correction still alerts the kitchen.** §10.1 says a
`staff_edit` by counter or administrator that adds or removes lines writes a `kitchen_notification`, with
no exception for closed sittings, and the transaction implements it literally. In practice an
administrator comping a line an hour after the bill settled will make the kitchen's tablet chime. Leaving
it is the honest reading; if it becomes a nuisance in use, the fix is one clause in `ShouldNotifyKitchen`
and a sentence in §10.1, not a special case in a surface.

**Known consequence — a rejected first send returns an identifier for a row that no longer exists.** The
lazily-created `guest_order` is rolled back with everything else, but `AppendOrderEventResult` still
carries the identifier the event targeted, which is genuinely useful when the order already existed. A
caller must not persist it; the fresh projection beside it is what §6.5.9 intends them to use.

**Deferred to the next M4 slice:** the guest staging area and `/table/{id}`'s living-order view (§11.1),
which is the first consumer of all of this, followed by the kitchen surface with its alert sound, wake
lock, and "fulfill all for this order" (§11.2, §10.3), and the §8.4 reminder scan and its background
service (§10.2). `kitchen_reminders_sent_total` stays at zero until that lands. Nothing in this slice
depends on menu administration, but the next one does in practice — until M5 there is no way to put an
item on the menu except an `INSERT`.

### Build/test checklist for this slice

1. `dotnet restore` — **no new packages**, no migration, no schema change.
2. `dotnet build` — no Razor at all this slice, which is deliberate: the compiler-catch risk in this
   codebase lives in components, and the order engine is worth landing green on its own. The likeliest
   catch is in `OrderMutations.cs`, which is the largest new file.
3. `dotnet test` — the previous green set plus three Testcontainers suites (which skip without a
   container engine; on rootless Podman, `systemctl --user enable --now podman.socket` once) and two pure
   ones. Expect `OrderReadModelTests` to be the slowest test in the suite by some margin — it commits
   dozens of transactions.
4. `./run.sh --smoke` — boots once, verifies `/healthz/ready`, exits. Nothing new runs at startup; the DI
   additions are all scoped and lazily constructed.
5. Manual verification needs a menu, and there is no surface for one until M5. Two rows are enough:

   ```sql
   INSERT INTO menu_item (menu_item_identifier, name, price_amount, is_active, created_at)
   VALUES (gen_random_uuid(), 'Soup', 4.50, true, now()),
          (gen_random_uuid(), 'Salad', 6.00, true, now());
   ```

   There is still nothing to click — the staging area is the next slice — so the honest check this time
   is `dotnet test` plus reading the `order_event` and `order_operation_*` rows the integration tests
   leave behind if you point the suite at a persistent database.

---

## M5 Slice 2 — the menu: create, rename, reprice, and the history that explains a price

§19's M5 line reads "bills, price adjustment, close & settle, end-of-day, counter fallback QR, **menu
management + events**, event explorer, hide/unhide, post-close corrections". Slice 1 took the counter's
half; this takes the emphasised one. What is left after it is end-of-day batch close (§5.4) with the
administration sittings list, the event explorer, hide/unhide (§6.8) with the hidden-records view, and the
administrator's post-close corrective surface.

Until now the only menu **write** in the system was the kitchen's 86 toggle. An administrator could not
put a dish on the menu at all: the two items every demo has were inserted by hand, which is a fine way to
run a test and no way to run a restaurant.

### Three verbs, one log, two write services

`IMenuAdministration` is create, rename, and reprice. `IMenuAvailability` keeps activate/deactivate, where
it has been since M4, and the split is by **audience** rather than by table: §7 gives the 86 to kitchen and
counter as well as to administrators, because the kitchen is the surface that knows the salmon has run out,
and §11.2 puts the toggle on the kitchen board. Everything on the new interface is administrator-only
(§11.4). Two interfaces, two audiences, one `menu_item_event` log — which is the entire point of having a
log rather than four columns.

Rename and reprice are separate calls, and the manage page gives each its own form, because §7's event
vocabulary has `name_changed` and `price_changed` as distinct types whose payload columns are mutually
exclusive — §8.2 enforces that with paired CHECKs (`(new_name IS NOT NULL) = (event_type IN ('created',
'name_changed'))` and its price twin). A combined "Save" would have to write two events anyway, and would
then need a policy for what to do when one half is a no-op. Two forms make the log read the way somebody
settling a price argument needs it to.

Each write takes the row `FOR UPDATE` before comparing, for the reason `DapperMenuAvailability` does:
without the lock, two administrators repricing the same item at once could both read 4.50, both write 5.00,
and log two `price_changed` events for one change. The price would still be right and the history would be
a lie, which is the worse of the two failures in an append-only system (ADR-0002).

### Rounding is not a detail here

`menu_item.price_amount` and `menu_item_event.new_price_amount` are both `numeric(10,2)`, and they are
written by two separate INSERT/UPDATE statements. Hand PostgreSQL 4.567 and it rounds — quietly, and
independently for each statement. So the price is rounded **once**, in `NormalizePrice`, away from zero to
match `numeric`'s own rule, before either statement runs; the value returned to the caller is then the same
number as the row and as the event. The no-op comparison happens after rounding too, which is what stops a
form that helpfully posts a third decimal from writing a `price_changed` event recording that nothing
changed.

The same method refuses a negative price and anything that would not fit eight integer digits. Both would
otherwise surface as an opaque `PostgresException` (23514 and 22003 respectively) well after the form that
caused them, from inside a transaction that then has to be unwound.

### `IMenuEventLog`, uncapped on purpose

§11.4 is explicit: administration renders "the complete stored record everywhere — full event streams,
visibility logs, security events — never projected or truncated for the administrator; filters narrow only
on explicit request". So `ListForItemAsync` has no page size and no filter. It is the answer to "why does
this cost what it costs", and a truncated answer to that question is worse than none. It reads oldest
first, because that is the direction the answer is assembled.

`ListRecentAsync` is the one capped read, and its cap **is** the explicit request: it fills a twenty-row
activity panel on the menu index so an administrator opening the page can see that somebody 86'd two things
an hour ago without opening either.

`EventType` comes back as the stored string rather than an enum. An enum here is a projection with a
failure mode — a type this build did not know about would either throw or be silently mapped to something
wrong, and the one reader whose job is to show what is actually in the table is the last place that should
happen. Both surfaces render a friendly label for the five types §8.2 admits and fall back to the raw
string, so a future type shows up as itself.

The actor is rendered `COALESCE(NULLIF(btrim(display_name), ''), username)`, the same rule
`DapperCounterBoardReads` uses for whoever closed a sitting. An audit line that says who did it and then
leaves the name blank is not an audit line.

### `MenuAvailabilityWorkflow` is now `MenuWorkflow`

The class was named when availability was all it could do, and its own doc comment already said M5 would
grow methods on it rather than add a second workflow. It now has four. One workflow over two write services
because there is one notification: §9 fires `MenuChanged` on "a menu item or `menu_item_event` commit"
without distinguishing the verb, and every subscriber responds identically — re-read the menu. A second
workflow would only make it possible to wire an application that announces 86s and not repricings.

Create publishes unconditionally (a create commits or throws). The other three publish **only** when
something moved. Both halves of that matter and both fail silently: a reprice that committed without
announcing itself leaves every open guest picker quoting yesterday's price until that page happens to
reload, and the guest is then surprised at the till by a number nobody showed them; announcing a rename
that changed nothing tells every phone, kitchen board, and display in the building to re-query because
somebody pressed a button and nothing happened.

No metrics. §12's meter list has no menu counter, correctly — the menu changes a handful of times a
service, and `menu_item_event` is a better record of it than a counter would be. So unlike `OrderWorkflow`
and `SittingWorkflow` this takes no `RestaurantMetrics`.

### `Program.cs` is untouched again

The five registrations went into `AddRestaurantOrders()`, which has wired the menu since M4. The menu is not
an ordering concern, but it is not a table or an identity one either, and an order prices itself from the
menu (§6.5.4) — so nothing that can take an order can be wired without it. A fifth `AddRestaurantMenu()`
would mean a host could register ordering and get a system whose staging area cannot list anything: the same
class of half-wired failure as leaving the reminder loop out.

### Surfaces

Three static-SSR pages, on the `CreateTable`/`ManageTable` pattern exactly — post/redirect/get with a
one-word `?done=` outcome so a refresh cannot re-post, one named form per action so static-SSR form matching
stays unambiguous, `PersonPrincipal.IdentifierFor(HttpContext.User)` for the actor with a belt-and-braces
refusal if the principal carries no usable id claim (`actor_person_identifier` is NOT NULL and §7 requires
every change to name somebody).

`/administration/menu` lists every item — **including** the unavailable ones. §7 requires an 86'd item to
stay on the guest's menu marked "currently unavailable" rather than vanish, and a page that hid them from the
administrator would hide exactly the rows somebody came looking for. Ordered by name, which is also why
duplicate names end up adjacent: `menu_item.name` carries no UNIQUE constraint, unlike
`restaurant_table.label`, and nothing here invents one. A kitchen running a rotating special really does
want two rows called the same thing, and this layer does not get to overrule the schema of record.

`/administration/menu/{id}` carries the two editors, the availability toggle, and the complete history. Its
availability toggle handles `AlreadyInThatState` by simply re-rendering: somebody in the kitchen got there
first between the page rendering and the button being pressed, nothing changed, and nothing is wrong.

Both pages say, in prose, the thing that causes arguments if it is left implicit: a new price applies to
lines added from now on, and anything already on an order keeps the price it was added at (§6.5.4). A
rename, by contrast, shows through everywhere immediately including on closed bills, because the name is a
read-time join in §8.3's views and not captured.

A Menu link joins the header actions on the people and tables index pages, so the three administration
sections reach each other without going home first.

### Tests

- `MenuAdministrationTests` (Testcontainers, 15 facts) — the row and its event land together with both
  payload columns for `created`; the actor and instant are recorded; rounding reaches both rows and the
  returned value; the name is trimmed; two items may share a name; rename writes `name_changed` with a NULL
  price and leaves the price alone; renaming to the same name (or the same name with padding) writes
  nothing; renaming only the case *is* a change, because `name` is `text` and not `citext`; reprice writes
  `price_changed` with a NULL name and leaves the name alone; 4.500 against a stored 4.50 is a no-op; zero
  is a legal price; an unknown item is reported and untouched; a negative price, an eight-digit overflow,
  and a blank name are each refused before anything is written; and the log keeps all four changes when both
  write services have been at the same item.
- `MenuEventLogTests` (Testcontainers, 9 facts) — the stream is complete and oldest-first across both write
  services; each of the five types carries exactly the payload §8.2 allows it; the actor is named with the
  username fallback; instants read back as UTC; one item's stream excludes another's; an unknown item is
  empty rather than an error; the activity feed is newest-first, capped, and returns nothing for a
  non-positive cap; and a renamed item's history reads under its current name while each entry still says
  what it was set to then.
- `MenuWiringTests` (8 facts, no container) — the five registrations resolve, and the workflow announces
  exactly the commits that happened: always for a create, only-when-moved for rename, reprice, and the 86,
  never for an unknown item.

### Three failing tests from Slice 1, fixed here

The Slice 1 tree was not green. All three failures were in the new tests rather than in the code they
cover:

1. `CounterBoardReadsTests` and `SittingSettlementTests` both created a second guest as `"bo"`, which is two
   characters, against `person.username`'s `CHECK (char_length BETWEEN 3 AND 64)`. Now `"bode"`.
2. `CloseAndSettle_HonoursPriceAdjustmentsAndDropsRemovedLines` asserted `PendingLineCountAtClose == 0`. It
   is 1, and 1 is right: the removed steak leaves `order_current_line` entirely, so it is neither charged
   for nor counted, but the soup was only **repriced** and nothing fulfilled it, so it was still with the
   kitchen when the total was stamped. Adjusting a price is not the same act as passing the plate. The
   assertion moved, not the implementation.
### M4 Slice 2 — the guest ordering surface: menu, staging, batch send, and the living order (landed)

M4 Slice 1 landed an order engine with no user interface at all. This slice is the first consumer: the
member view of `/table/{id}` grows a menu picker, a staging area, a Send that reports per-operation
reasons, the committed living order with its removals and price adjustments intact, the rest of the
party's orders read-only, running personal and table totals, and the §11.1 flip to a settled bill when
the counter closes the sitting. No data-access code changed and no SQL was written — every query this
surface makes already existed and, until now, had no caller.

**The structural decision: an interactive island inside a static page.** `/table/{id}` is
`[ExcludeFromInteractiveRouting]` because the join flow writes the grant cookie and issues redirects,
and a Blazor circuit can do neither. But everything §11.1 asks for below the join is live — a basket
that survives a mis-tap, lines that re-badge when the kitchen fulfills them, a party total that moves
when someone else orders, the flip on `SittingClosed`. Rather than compromise one for the other,
`TableJoin.razor` keeps the cookie work and renders the new `TableOrderSurface` with
`@rendermode="InteractiveServer"`. The parameters that cross the boundary are two `Guid`s, which the
framework protects with the data-protection key ring like any other server component marker; the person
is **not** among them, and is read from the cascading authentication state inside the island so the
component is correct on its own terms rather than merely correct because its caller was careful. The
roster moved into the island for the same reason: §9 sends `SittingMemberJoined` to table members, and a
statically rendered list cannot hear it. `TableJoin.razor`'s inline `<style>` block is gone; the
`.table-*` vocabulary moved to `app.css`, because an inline style in one component silently styling
another component's markup is a dependency nobody can see.

**Scoping every read by the sitting, not the table.** The island is handed the sitting the parent
resolved and re-derives everything else, and every query is keyed by that identifier. The one query
keyed by the table — `GetOpenSittingForMemberAsync`, which answers "am I still a member of something
open here?" — has its answer **discarded unless it names this sitting**. That is not defensive
programming; it is the difference between two behaviours. A sitting that is closed and followed by a new
one on the same table would otherwise silently swap the order under the reader's feet mid-session,
showing a guest somebody else's table as though it were theirs. Comparing the identifier makes the same
event do what §11.1 asks instead: flip to the read-only settled bill.

**Two new pure types, because a Razor component is not testable here.** `OrderNarrative` (Domain) folds
an order's event log into a per-line story that **keeps removed lines** — the opposite of
`order_current_line`, and deliberately so: §11.1 wants "removed lines struck-through with actor +
reason, price adjustments shown old → new with reason", and an append-only log exists so history
survives state (ADR-0002). The "old" side of that arrow is stored nowhere, which is precisely why it
cannot come from a view: it is the price the fold was holding when the adjustment arrived.
`OrderNarrative` also states §6.5.3's guest-removal rule once, as `GuestMayRemove`, so the surface can
grey out what the transaction would refuse rather than offering a control that always fails.
`OrderStaging` (WebApplication) is the basket: stage, unstage, change a quantity, tick a committed line
for removal, and `Build` into the operations of one `guest_submission` with a parallel description per
operation so a rejection's `OperationIndex` becomes "2 × Soup — the menu item is currently unavailable"
rather than "operation 3 failed". Both live outside the component for the reason `ProfileDetails`,
`ObligationsEnforcement`, and `PairingCode.Normalize` do (§16.1 — no bUnit).

**There are now two folds over the same log, and that is a risk with a test against it.**
`OrderProjection` answers the bill's question and §8.5 pins it against the SQL views;
`OrderNarrative` answers the guest's. Nothing structural stops them drifting, and the first person to
notice would be a customer being charged something the app never showed them. So
`OrderNarrativeTests.NonRemovedLines_AgreeWithOrderProjectionOnARandomisedSequence` closes the triangle:
on a seeded 120-event sequence — sends, removals, fulfillments, reversals, price adjustments, and one
operation aimed at a line that does not exist — the narrative's non-removed lines equal the projection's
lines field for field, in the same order, and the totals match. The sequence opens with a fixed prologue
that guarantees a removed line, a fulfilled line, and an adjusted line, so the coverage assertions hold
by construction rather than by the seed happening to be kind.

**Three behaviours worth naming.**

*Staged lines carry no price.* §6.5.4 prices every added line server-side from the menu read inside the
send transaction, so a price captured at staging time would be a second, older authority — and the one
moment it disagreed with the charge is the moment a guest would notice. The surface renders the current
menu price beside each staged line instead, which is correct by construction and re-reads itself on
`MenuChanged`. For the same reason `Build` proposes every added line at **zero**: the transaction
overwrites it, and sending zero rather than a plausible-looking number means a regression in that
overwrite shows up as a free lunch on the first order instead of as a stale price nobody spots.

*Stale removal ticks are pruned on every re-read.* A guest ticks a pending line; the kitchen fulfills it
before they press Send. §6.5.9 refuses the **whole** event on one bad operation, so that one leftover
tick would make the Send button permanently useless with no explanation. `PruneRemovals` drops marks for
lines that are no longer the guest's to remove and the surface says so in a sentence. The transaction
re-decides all of it under the lock regardless; this only moves the answer to the moment of the tap.

*Notes are not validated, at any length.* §7 is explicit that customization notes are free text and are
"never validated against any rules engine", so `OrderStaging` trims and collapses a blank to `null` —
exactly what the transaction does — and refuses nothing. The input carries `maxlength="200"` as a
courtesy against a pasted novel, and the circuit's own message-size limit is the real backstop.

**Live updates.** The island subscribes on `OnAfterRender(firstRender)` — never during prerendering, so
no subscription is created for a render about to be thrown away — and handles the four §9 notifications
that can change what is on the screen, filtered on the sitting identifier: `OrderLinesChanged`,
`LineFulfillmentChanged`, `SittingMemberJoined`, `SittingClosed`, plus `MenuChanged`, which additionally
re-reads the menu so a staged item picks up its "currently unavailable" mark (§7). One re-read per
notification, unconditionally: the queries are small, scoped to one sitting, and a restaurant table is
not a hot path. Nothing publishes `MenuChanged` yet — menu administration is M5 — and the handler is
wired now because writing it later means remembering to.

**`MoneyText`.** New, and the reason it is not `amount.ToString("C")` is that the framework's currency
formatting reads the *server's* culture and ignores `RESTAURANT_CURRENCY_CODE` entirely, printing
dollars for a restaurant configured in euros and doing it silently. A short ISO 4217 → symbol table with
the code itself as the fallback (`ISK 1200.00`), and the digits always invariant with two decimals,
because prices are `numeric(10,2)` and a guest checking a bill against a menu board wants them to line
up.

No migration, no packages, no DI change (every service the island resolves was registered by
`AddRestaurantOrders()` in Slice 1), and no specification edit — this realizes behaviour §6, §7, §9,
and §11.1 already specify. Tests: `OrderNarrativeTests` (16 facts including the drift guard, no
container), `OrderStagingTests` (22 facts and theories, no container), `MoneyTextTests` (10 facts and
theories, no container).

**Known consequence — a basket dies with the circuit.** Staging is circuit state and is not persisted, so
a refresh, a phone locking for long enough to drop the WebSocket, or a tab restore empties it. That is
deliberate: §6 gives an order exactly one persistence mechanism, the append-only event log, and a
half-composed basket is not an order event. Persisting drafts would mean a second write path, a second
projection, and a new question — "whose draft is on this table?" — that §11.1 never asks. If it proves
annoying in real use, the cheap fix is `sessionStorage` on the client, not a table.

**Known consequence — prerendering reads the order twice.** The island prerenders inside the static page
and then loads again when the circuit starts, so the first paint costs two rounds of the same six
queries. `TableDisplay` has carried the same cost since M3. Disabling prerender would trade it for a
visibly empty panel on a phone over a tunnel, which is the worse deal.

**Known consequence — times render in the server's zone, not `RESTAURANT_TIME_ZONE`.** Found while
building this surface and recorded as **F-36**, not fixed here. Every surface in the tree uses
`ToLocalTime()`, `RestaurantOptions.ResolveTimeZone()` has no caller, and the runtime container sets no
`TZ`, so a deployed instance shows a guest UTC. This slice matches the existing convention rather than
introducing a second one inside a single screen — the static header and the interactive island are one
page — and the fix, together with the locale question §13 never settles (12- versus 24-hour), lands as
its own slice before M5.

**Deferred, and why.** §11.1's per-order **Hide** control and the guest's own **history** of past orders
need `order_visibility_event` writes and a closed-sitting query that reads across sittings; both are
§6.8 work and belong with the §11.4 hidden-records view that is their only unhide path (M5). The
committed order renders removals and price adjustments the guest can actually cause today; the surfaces
that *produce* staff removals and price adjustments — the counter (§11.3) — arrive in M5, so those
branches are written and tested but cannot yet be exercised through the interface. Guest
self-registration on the join path (§11.1) is still outstanding from M2: an anonymous scanner is sent to
`/sign-in` and needs an account already.

### Build/test checklist for this slice

1. `dotnet restore` — **no new packages**, no migration, no schema change, no DI change.
2. `dotnet build` — `TableOrderSurface.razor` is where a compiler catch would live: it is the first
   interactive island in the tree (as opposed to a whole interactive page), and the first component with
   a `<select>`, lambda-bound `@onchange` handlers inside a `@foreach`, and a nested `record` in its
   `@code` block. `TableJoin.razor` is the second-likeliest, having lost its `<style>` block and gained
   a child component with `@rendermode`.
3. `dotnet test` — the previous green set plus three pure suites; none needs a container engine, so
   none skips.
4. `./run.sh --smoke` — boots once, verifies `/healthz/ready`, exits.
5. Manual verification **needs a menu**, and there is still no surface for one until M5. Two rows is
   enough, and add an inactive one to see §7's rule work:

   ```sql
   INSERT INTO menu_item (menu_item_identifier, name, price_amount, is_active, created_at)
   VALUES (gen_random_uuid(), 'Soup',   4.50, true,  now()),
          (gen_random_uuid(), 'Salad',  6.00, true,  now()),
          (gen_random_uuid(), 'Salmon', 18.00, false, now());
   ```

6. Manual, the happy path, on a quick tunnel (`bash scripts/quick_tunnel.sh`) with a phone:
   - Scan a table's display, sign in, join. The table page now shows **Who is here**, a picker, and an
     empty basket.
   - **Salmon** appears in the picker greyed out and reading *(currently unavailable)*, and cannot be
     selected — §7's "the guest sees that the salmon exists and is out".
   - Add two Soups with a note and one Salad. The basket shows both with the running amount; **Send**
     is enabled and names the count.
   - Send. The status line says what went; the basket empties; the lines appear below badged **With the
     kitchen**, with the note under each.
   - Tick **Take this off my order on the next send** on one line, then Send again. It renders
     struck-through and badged **Removed**, with "Taken off by you".
7. Manual, live updates — open the same table as a **second** guest in another browser profile:
   - The first guest's roster gains the second person **without a refresh** (`SittingMemberJoined`).
   - The second guest orders; the first guest's **the rest of the table** panel and the **table total**
     move without a refresh (`OrderLinesChanged`).
8. Manual, refusals and edges:
   - Set a quantity to `0` or `101` in the basket → refused in place, the row keeps its old quantity.
   - Deactivate an item in SQL (`UPDATE menu_item SET is_active = false WHERE name = 'Soup';`) **after**
     staging it, refresh, and Send → the whole batch is refused, the panel names the offending line by
     description, and **the basket is untouched** (§6.5.9 all-or-nothing).
   - `UPDATE table_sitting SET closed_at = now() WHERE closed_at IS NULL;` then refresh the table page →
     the surface flips to **This sitting has been settled**, the picker and Send are gone, and the bill
     renders read-only.
### M4 close-out — restaurant time everywhere, and a clock that says so (landed)

Two things landed together because they are one decision. The first is a build fix: `TableOrderSurface.razor`
called `LinesFor(...)` in its render tree and the method was never written — `_partyLines` was loaded in
`LoadAsync` and then read by nothing, so the sitting-wide line query had no consumer and the web project did
not compile. The second is **F-36**, the open row in `DOCUMENTATION_REVIEW.md`: S§8.1 has always said instants
are "stored `timestamptz` UTC; rendered in `RESTAURANT_TIME_ZONE`", and no code did that. Eighteen call sites
across ten Razor files called `.ToLocalTime()`, which reads the **server process's** zone;
`RestaurantOptions.ResolveTimeZone()` existed with no caller; the runtime container sets no `TZ`. A deployed
instance rendered UTC and said nothing about it.

---

#### The ruling, and why it is stronger than the specification was

The owner's ruling settles a question S§8.1 had left implicit: **the reader's zone is irrelevant.** Not "prefer
the restaurant's zone" — *always* the restaurant's, for every viewer, wherever they are. A restaurant is a
physical place in one IANA zone. A guest in New York opening the history of a meal they ate in Tokyo wants the
times the meal actually happened at, not the times it would have been on their own wristwatch; and inside the
building, a kitchen ticket, the counter's bill, and the tablet on table four must agree to the minute or the
staff cannot talk to each other. Rendering in the viewer's zone would make all four screens disagree about the
same event, which is the one thing an append-only history is supposed to prevent (ADR-0002).

So S§8.1 is now normative about it rather than merely descriptive, R§8 states the rule and its reason, and one
type is the only thing allowed to perform the conversion.

#### `RestaurantTime` — one type, invariant formats, and no `ToLocalTime()` anywhere

`src/MyRestaurant.WebApplication/Time/RestaurantTime.cs` is a singleton over the configured
`TimeZoneInfo`, exposing `Time`, `TimeWithSeconds`, `Date`, `DateAndTime`, `DateAndTimeWithSeconds`,
`MachineReadable`, and `Snapshot`. Every call site moved onto it; the only surviving mention of `ToLocalTime`
in the tree is the doc comment forbidding it.

The formats are explicit and `InvariantCulture`, for exactly the reason `MoneyText` already documents for
`"C"`: `ToString("t")` takes the 12-versus-24 choice, the separator, and the month names from the *server's*
culture, which in this deployment is whatever locale the base image happens to carry. That is a decision made
by nobody, and it changes silently when the image is rebuilt. F-36's row named this entanglement and left it
open; it is now settled as configuration — `RESTAURANT_CLOCK_FORMAT`, `12-hour` (default) or `24-hour`,
validated at startup so a typo fails the process instead of quietly showing the wrong clock on every screen in
the building. It is deliberately **not** a `required` property, so every existing `RestaurantOptions`
construction (including the wiring tests') keeps compiling on the documented default.

`Snapshot` additionally computes **when the offset next changes** — a day-by-day walk to the bracketing day,
then a bisection to the second, memoized until the transition it found has passed. That is what lets a page
left open across the first Sunday in November stop rendering EDT without anybody reloading it.

#### §11.7 — a footer clock, because the convention is invisible until stated

"Sent 3:04 PM" tells a reader on another continent nothing at all unless the page says whose three o'clock that
is. A new `RestaurantClockFooter` therefore appears on **every** page in **both** layouts — including
`DisplayLayout`, whose whole existing rationale is that it carries no chrome, because a clock is the one piece
of chrome that means something on a screen a whole table looks at. `DisplayLayout`'s shell became a column with
the card centred in a `.display-main`, so the footer sits below it rather than beside it.

**There is no server-side timer, on purpose.** A Blazor timer would tick only on the interactive surfaces —
half the pages here are static SSR — and would cost one render plus one circuit message per second per open
tab, indefinitely, on phones. Instead the component renders one anchor and never renders again
(`ShouldRender() => false`, since the script owns that text node afterwards and a framework re-render would
overwrite a correct ticking reading with a stale one). `wwwroot/js/clock.js` advances it: a classic script
alongside `passkey.js` and `display.js`, so it works on the static-SSR account pages too, and a no-op on any
document without the footer.

`Intl` is forbidden in that script and the comment says why: it formats in the **reader's** locale and zone,
the exact thing the ruling rules out. The invariant abbreviated day and month names are hardcoded so the
ticking text and the server-painted text it takes over from are byte-identical — otherwise the handover at
page load is visible as a flicker.

**The handheld budget.** Most readers are on a phone, and the browser and OS will both try to save battery;
this must cooperate rather than fight:

1. **Nothing runs while hidden.** `visibilitychange` *clears* the timer rather than letting it fire and be
   ignored. A backgrounded tab costs zero.
2. **One wake per visible second.** `setTimeout` aimed at the coming second boundary — not `setInterval`
   (drift accumulates into double-fires) and never `requestAnimationFrame` (sixty wakes for one visible
   change).
3. **No DOM write unless the text changed**, guarded by one string comparison. `tabular-nums` stops the digits
   changing width as they tick, and `contain: content` on the footer keeps the repaint from inviting a layout
   pass over the page around it.

**Which clock is believed.** Elapsed time comes from `performance.now()` — monotonic, so an NTP step cannot
move it, which `Date.now()` alone cannot promise. But `performance.now()` stops advancing during device
suspend on several platforms, so a phone that spends an hour in a pocket would wake an hour behind. Both are
therefore read every tick and their divergence is treated as the signal it is: past two seconds, prefer the
wall clock (only it saw the suspend) and re-anchor from the server. A wall clock stepped *backwards* under us
prefers the monotonic reading and re-anchors anyway.

#### `GET /restaurant-clock`

The markup anchor is the whole story for a short-lived page. Two surfaces here are not short-lived: a
`/display/{table}` tablet holds one URL for days on a cheap oscillator, and a guest's circuit lasts a meal.
Rather than reload either, the script re-asks — every ten minutes while visible, on returning from a minute or
more hidden, and on detected divergence. Never while hidden, never more than once a minute, and a failed
request is *ignored* rather than allowed to blank the clock: a wall clock a second off beats a blank one. Half
the measured round trip is subtracted as the usual symmetric-latency estimate; the initial markup anchor uses
Navigation Timing's `responseStart` for the same reason, since anchoring at script-run time would leave the
clock permanently a fraction of a second slow.

The endpoint is anonymous (`credentials: 'omit'` from the script), `no-store`, and **added to
`ObligationsEnforcement.IsExemptPath`** alongside `/healthz`. That last one is not incidental: the §3.5
obligation pages render this footer too, and a redirect to HTML would leave the one page a locked-out user is
allowed to see with a dead clock.

#### `LinesFor` — the build fix, done as the read it should always have been

Rather than reinstate a `_partyLines.Where(...)` scan per bill entry, `LoadAsync` now groups the one
sitting-wide read into `_partyLinesByOrder` and `LinesFor` is a dictionary lookup. §11.1 renders a row per
person at the table, so the alternative — a query inside that loop — would turn a six-person table into six
extra round trips on every §9 notification. An order with nothing on it is absent from the grouping rather
than present and empty, so the empty list is the ordinary answer and not an error.

---

#### Files

**New**

- `src/MyRestaurant.WebApplication/Time/RestaurantTime.cs`
- `src/MyRestaurant.WebApplication/Time/RestaurantClockEndpoints.cs`
- `src/MyRestaurant.WebApplication/Components/Layout/RestaurantClockFooter.razor`
- `src/MyRestaurant.WebApplication/wwwroot/js/clock.js`
- `tests/MyRestaurant.WebApplication.Tests/Time/RestaurantTimeTests.cs`

**Changed**

- `Configuration/RestaurantOptions.cs` — `ClockFormat`, `UsesTwelveHourClock`, validation
- `Program.cs` — `RestaurantTime` singleton, `MapRestaurantClock()`
- `Identity/ObligationsEnforcement.cs` — exempt `/restaurant-clock`
- `Components/_Imports.razor`, `Components/App.razor`, both layouts, `wwwroot/app.css`
- Ten Razor pages, eighteen call sites: `Account/Pages/Passkeys`, `Administration/{AdministrationHome,
  AdministrationTables, ManagePerson, ManageTable, TableDisplays, TableJoinCode}`, `Setup`,
  `Table/{TableArea, TableOrderSurface}`
- `tests/.../RestaurantOptionsTests.cs`, `tests/.../Identity/ObligationsEnforcementTests.cs`
- `.env.example`, `compose.yaml`, `run.sh`
- `docs/REQUIREMENTS.md` (R§8), `docs/TECHNICAL_SPECIFICATION.md` (S§8.1, new S§11.7, S§13, S§19,
  Appendix A), `docs/DOCUMENTATION_REVIEW.md` (F-36 closed)

**Deleted** — none.

---

#### Verification

1. `dotnet build` — the `CS0103: LinesFor` error is gone; the web project compiles.
2. `dotnet test` — `RestaurantTimeTests` covers: the same instant rendering differently for a New York and a
   Tokyo restaurant; the date rolling forward when the restaurant zone has passed midnight and the reader's
   has not; a 45-minute offset (`Asia/Kathmandu`) not rounded to the hour; midnight as `12:00 AM` not
   `0:00 AM`; both clock formats; **format stability under `de-DE` and `ja-JP` ambient cultures**, which is
   the property F-36 was really about; the November 2026 transition found at the second, cross-checked
   against `TimeZoneInfo` on both sides of it; a no-DST zone reporting no transition; and the memo expiring
   after the transition it named.
3. `grep -rn "ToLocalTime" --include=*.razor --include=*.cs src/` → only the doc comment forbidding it.
4. Manual, on a quick tunnel with a phone:
   - Set `RESTAURANT_TIME_ZONE=Asia/Tokyo` and reload. Every timestamp on `/administration/tables`,
     `/account/passkeys`, and the table surface moves together; the footer reads **Tokyo**.
   - Set `RESTAURANT_CLOCK_FORMAT=24-hour`. The footer and every page timestamp switch together — no page
     is left on the other convention.
   - Set `RESTAURANT_CLOCK_FORMAT=military`. The process refuses to start and names the variable.
   - Watch the footer tick for a minute. It advances once a second and does not visibly reflow the page.
   - Background the tab for two minutes, then return: the reading is correct immediately, not two minutes
     behind and catching up.
   - Lock the phone for ten minutes, unlock: same.
   - `curl -s http://127.0.0.1:8080/restaurant-clock` returns the snapshot JSON with
     `cache-control: no-store`.
   - Reach `/account/change-password-required` with an outstanding obligation: the footer clock still ticks
     (the exemption), and the page is otherwise unreachable as before.
5. Manual, the display: open `/display/{table}` on a tablet. The card is still centred; the clock sits under
   it at display type size; the rotating QR and the staleness curtain behave exactly as before.
### M4 Slice 4 — the kitchen board: the queue, fulfillment, the "86" panel, and the reminder that nobody triggers (landed)

M4's build-order line (§19) reads "living order + locking protocol, staging UI, batch send + validation,
staff edits, fulfillment/reversal, projections + fold + equivalence tests, **kitchen surface + alerts +
reminder service**". Slice 1 landed the engine, Slice 2 the guest half, the close-out the time
convention. This is the clause that was still outstanding, and with it M4 is complete: a send now
reaches a screen a cook is standing at, and a send that is ignored says so by itself.

No migration, no packages, no schema change. Every table, view, and constraint this slice needs shipped
in `0001_initial_schema.sql` — including, crucially, `kitchen_notification`'s
`UNIQUE (order_event_identifier, kind)`, which is what makes the whole reminder mechanism safe rather
than merely careful.

---

#### The reminder is the load-bearing part, and it is the only thing here whose bug is silence

Everything else on this screen fails loudly. A queue in the wrong order looks wrong; a fulfil button that
does not work is pressed twice. The reminder is different: if it never fires, the application starts
cleanly, serves every page, alerts correctly on every send, and simply never mentions the ticket that
has been sitting for four minutes. Nothing in the logs, nothing on a dashboard, nothing a test that
checks "does it work" would catch — because it does work, right up until the moment nobody did anything.

So the design puts as little of it as possible in C#. §8.4's scan is a single SELECT, run every five
seconds; the "exactly one reminder per send" guarantee is the unique constraint, not a flag in memory;
and "broadcast only if the insert took" is a `RETURNING` clause, not a rowcount the caller interprets. A
restart mid-scan, two overlapping ticks, or (if there is ever one) a second web replica cannot
double-alert, and none of that depends on this process remembering anything between ticks.

**One documented deviation from §8.4's literal SQL.** The specification writes
`submission.occurred_at < now() - make_interval(secs => :reminder_seconds)`.
`DapperKitchenNotifications` computes the same threshold from `IClock.UtcNow` and binds it as
`@DueBefore` instead. `occurred_at` was stamped by the *application's* clock, so comparing it against the
*database's* `now()` compares two clocks — invisible while both containers share a host clock, wrong the
first time they do not. It also makes the rule testable at all: against `now()` there is no way to place
a send precisely either side of the threshold, and every clause of §10.2 would go unasserted. The four
EXISTS/NOT EXISTS clauses, the open-sitting filter, and the ordering are §8.4 verbatim.

`KitchenReminderService` is a `BackgroundService` on a `PeriodicTimer`, one DI scope per tick, and a
deliberately broad `catch` around each tick: a transient database blip must not stop the loop for the
life of the process. It is registered by `AddRestaurantOrders()` rather than by `Program.cs` — see below.

#### Why a hosted service is registered from `AddRestaurantOrders()`

Because §10 is one rule with two halves and they must not be separable. §10.1's initial alert is written
*inside* the order transaction (it has to be — a committed alert must never point at an event that rolled
back), so it already lives in `DapperOrderMutations`. If §10.2's half were wired somewhere else, it would
be possible to compose ordering into a host and get a system that alerts but never reminds. Registering
both from the same call means you cannot have one without the other. The extension's doc comment says so
at length, because a hosted service appearing from an `AddX()` is otherwise spooky.

#### The queue: `KitchenQueue`, pure, and ordered oldest-first at both levels

§11.2: "grouped by (table label → person display name → order), ordered by the group's oldest
`added_at`". The grouping is a pure function over the `kitchen_pending_line` read, outside the component
for the same reason `OrderStaging` and `OrderNarrative` are (§16.1 — no bUnit): the ordering rule *is*
the behaviour of the screen, and a rule that can only be checked by rendering a Razor component is a rule
nobody checks.

The word doing the work is **oldest**. Sorting a board by its most recent send is the obvious mistake and
looks fine in a demo; what it does in service is push a forgotten order further down the screen every
time somebody at that table asks for another drink — which is precisely the failure §10.2's reminder
exists to catch, arriving by a second route.

Every comparison falls through to a label and then to an identifier, so a re-read of unchanged data
produces a byte-identical board. Lines added by one send share an `occurred_at` to the microsecond and
two tables can be sent to in the same instant; a queue whose rows shuffle under a cook's hand on every
live update is worse than one in a slightly wrong order.

#### Undo needs a question the §8.3 views cannot answer

§11.2 wants "an Undo affordance on recently-fulfilled lines". `order_current_line.is_fulfilled` is the
latest flip's *direction* with its instant thrown away, so "fulfilled in the last quarter of an hour" is
not a question the projection views can answer. Rather than add a timestamp column to a schema-of-record
view to serve one button, `IKitchenBoardReads` asks the operation tables directly — a lateral pick of the
highest-sequence `order_operation_line_fulfilled` for each currently-fulfilled line. Hence a separate
interface rather than a sixth method on `IOrderReadModel`: that type is "the four §8.3 views", and this
is honestly a different question.

A line fulfilled, undone, and fulfilled again reports the second fulfillment. A line whose latest flip is
a reversal is absent — it is pending again and belongs in the queue, and offering a second Undo for it
would produce a refusal (§6.5.6) with no way for the cook to know why. Window: fifteen minutes, a
constant rather than configuration, since §13 does not name it and a setting nobody would change is only
a new way to be wrong.

#### `IMenuAvailability` — the one piece of M5 that could not wait

§11.2 puts the "86" toggle on the kitchen board, and the kitchen is the surface that knows the salmon has
run out. So availability, and only availability, ships now: `SetActiveAsync` flips `menu_item.is_active`
and appends the matching `menu_item_event` in one transaction, under `FOR UPDATE`. Create, rename,
reprice, and §11.4's per-item history stay in M5 and will grow from this rather than replace it — keeping
the write this narrow is what stops the kitchen board becoming an accidental menu editor.

Two behaviours are deliberate. **A no-op flip writes no event**: an append-only log of "somebody pressed
a button that changed nothing" is noise, and §11.4's history is meant to be read by a person. **The row
is locked before it is compared**: without that, two staff toggling at once could both read "active",
both write "inactive", and log two `deactivated` events for one deactivation — the flag would be right
and the history would be a lie, which is the worse failure in an append-only system (ADR-0002).

Surfaces take `IMenuWorkflow`, never the raw write, so the §9 `MenuChanged` always goes out. An 86 that
skipped the broadcast would leave the item selectable in every open guest picker until that page happened
to reload, and the guest would then have a whole send refused for it (§6.5.9).

#### §10.3's alert, and why the sound is synthesised

"Browsers block autoplay: the kitchen surface shows a one-tap 'enable sound' arm control per session;
until armed (and whenever playback fails) a persistent, high-contrast visual badge with unseen-alert
count is the fallback." Three facts are load-bearing there — armed, playback-failed, and the count — and
`KitchenAlertState` states the sentence that combines them once, in a pure type, rather than
re-deriving it in markup.

Arming is circuit state, deliberately. It is a browser-audio permission that lives exactly as long as the
page does, so "per session" means per circuit; persisting it would be a lie, since a fresh tab has not
been armed no matter what a database says.

`wwwroot/js/kitchen.js` owns the noise, because an `AudioContext` will only start inside a real user
gesture and a Blazor circuit is not one. **It synthesises two square-wave beeps rather than shipping an
audio file.** An `.mp3` would be a binary asset to ship, cache, license, and get wrong at 3 kHz on a
cheap tablet speaker; Web Audio needs no file, cannot 404, and starts with zero network latency. The two
patterns differ on purpose — a rising two-note chime for a new send (§10.1), a flatter insistent triple
for a reminder (§10.2) — because "somebody just ordered" and "you have not touched this in a minute" are
different news. The gain ramps are not decoration: an oscillator started at full amplitude clicks, and on
a small speaker the click is most of what you hear.

`arm()` proves itself with a short quiet tone rather than trusting `state === 'running'`, and returns a
boolean. `alert()` returns false rather than throwing when it cannot play, and the component treats that
as §10.3's "whenever playback fails" and raises the badge. The wake lock follows `display.js`'s dance,
keyed on `#kitchen-board-surface`, on a two-second tick — half as busy as `display.js`, which has an
actual per-second job.

#### One threading detail worth naming

`KitchenAlertState` is a plain counter and `IDomainEventBroadcaster` fans out from whichever thread
committed the event, so two sends landing together could lose an increment. The board therefore records
the alert *inside* `InvokeAsync`, on the renderer's dispatcher, which serializes every mutation of it —
and records it **before** the re-read, so an alert still counts if the queries fail. A board that missed
a query is recoverable; a board that missed an alert is silent.

The alert token is a monotonic sequence, not a count, and acknowledging does not rewind it. Resetting it
would make the next alert's token equal to one already announced, and the board would go quiet with no
error anywhere. `KitchenAlertStateTests` pins both.

#### Two small things that came with it

`Pages/Home.razor`'s lede still said Milestone 2 was under way and that "the kitchen and counter boards
arrive in later milestones", which stopped being true two slices ago. It now describes what actually
works and carries role-gated area links — `<AuthorizeView Roles="…">` matching the §3.7 policies, because
showing somebody a door that answers "access denied" is worse than not showing it.

`MainLayout` gains a Kitchen link for the **kitchen role only**, not for administrators, even though
`area.kitchen` admits both. An administrator already carries an Administration link, and a fifth item
would put the header back into the three-row wrap at 375px that the single-Account-link change fixed.
Administrators reach the board from the landing page instead.

---

#### Files

**New (13)**

- `src/MyRestaurant.DataAccess/Orders/KitchenBoardReads.cs`
- `src/MyRestaurant.DataAccess/Orders/KitchenNotifications.cs`
- `src/MyRestaurant.DataAccess/Menu/MenuAvailability.cs`
- `src/MyRestaurant.WebApplication/Menu/MenuAvailabilityWorkflow.cs`
- `src/MyRestaurant.WebApplication/Orders/KitchenQueue.cs`
- `src/MyRestaurant.WebApplication/Orders/KitchenAlertState.cs`
- `src/MyRestaurant.WebApplication/Orders/KitchenReminderService.cs`
- `src/MyRestaurant.WebApplication/Components/Pages/Kitchen/KitchenBoard.razor`
- `src/MyRestaurant.WebApplication/wwwroot/js/kitchen.js`
- `tests/MyRestaurant.DataAccess.Tests/Orders/KitchenNotificationsTests.cs` (Testcontainers, 9 facts)
- `tests/MyRestaurant.DataAccess.Tests/Orders/KitchenBoardReadsTests.cs` (Testcontainers, 9 facts)
- `tests/MyRestaurant.DataAccess.Tests/Menu/MenuAvailabilityTests.cs` (Testcontainers, 7 facts)
- `tests/MyRestaurant.WebApplication.Tests/Orders/KitchenQueueTests.cs` (12 facts/theories, no container)
- `tests/MyRestaurant.WebApplication.Tests/Orders/KitchenAlertStateTests.cs` (13 facts, no container)
- `tests/MyRestaurant.WebApplication.Tests/Orders/KitchenWiringTests.cs` (6 facts, no container)

**Changed (4)**

- `src/MyRestaurant.WebApplication/Orders/OrdersServiceCollectionExtensions.cs` — four services and the hosted reminder loop
- `src/MyRestaurant.WebApplication/Components/App.razor` — loads `js/kitchen.js`
- `src/MyRestaurant.WebApplication/Components/Layout/MainLayout.razor` — Kitchen link for the kitchen role
- `src/MyRestaurant.WebApplication/Components/Pages/Home.razor` — accurate lede, role-gated area links

**Deleted** — none. `Program.cs` is untouched.

No `docs/TECHNICAL_SPECIFICATION.md`, `docs/REQUIREMENTS.md`, or ADR edit: this realizes behaviour §7,
§8.4, §9, §10, §11.2, and §12 already specify.

---

#### Build/test checklist for this slice

1. `dotnet restore` — no new packages, no migration, no schema change.
2. `dotnet build` — `KitchenBoard.razor` is where a compiler catch would live: it is the first component
   with lambda-bound `@onclick` handlers inside nested `@foreach` loops, the first to call
   `IJSRuntime.InvokeAsync<bool>` from `OnAfterRenderAsync`, and the first with a component-local
   `<style>` block since `TableDisplay.razor`.
3. `dotnet test` — the previous green set plus six suites. Three need a container engine and skip
   without one; three do not.
4. `./run.sh --smoke` — boots once, verifies `/healthz/ready`, exits. Watch the log for
   `Kitchen reminder service started`, which is the registration this slice most wants confirmed.
5. Manual verification **needs a menu** (M5 still owns menu creation) and two browser profiles:

   ```sql
   INSERT INTO menu_item (menu_item_identifier, name, price_amount, is_active, created_at)
   VALUES (gen_random_uuid(), 'Soup',   4.50, true, now()),
          (gen_random_uuid(), 'Salad',  6.00, true, now()),
          (gen_random_uuid(), 'Salmon', 18.00, true, now());
   ```

   Grant an account the `kitchen` role from `/administration`, then open `/kitchen` as that account.
6. Manual, the happy path:
   - As a guest on a phone, scan a table and send two Soups with a note and one Salad.
   - The kitchen board gains the ticket **without a refresh**, grouped under the table label, with the
     note in an orange block and the send time on the ticket header.
   - Tap **Enable sound**. A short quiet two-note confirmation plays and the chip reads **Sound on**.
   - Send again from the phone: a rising chime plays and the red badge counts one alert.
   - Tap a line. It leaves the queue, appears under **Just fulfilled**, and the guest's phone re-badges
     that line to **At your table** without a refresh.
   - Tap **Undo**. It returns to the queue and the guest's badge goes back to **With the kitchen**.
   - Tap **Fulfill all** on a ticket: one event, the whole ticket clears.
7. Manual, the reminder (§10.2) — the point of the slice:
   - `KITCHEN_SUBMISSION_REMINDER_SECONDS=20` in `.env` makes this bearable to watch.
   - Send from the phone and touch nothing. Within ~25 seconds the board plays the **three-note** pattern
     and the badge reads `2 new alerts (1 overdue)`.
   - Wait two more minutes: **nothing further happens**. One reminder per send, ever.
   - `SELECT kind, count(*) FROM kitchen_notification GROUP BY kind;` → one `initial`, one `reminder`.
   - Send again, fulfil one of its lines within the window, and wait: **no reminder** — §8.4's last
     NOT EXISTS, and the clause most likely to be got wrong.
8. Manual, the "86" panel:
   - Turn **Salmon** off on the board. The guest's picker greys it out and marks it *(currently
     unavailable)* **without a refresh** (`MenuChanged`).
   - `SELECT event_type FROM menu_item_event;` → one `deactivated`.
   - Press **Turn off** again on a reloaded board → the status line says it was already set that way and
     no second event is written.
9. Manual, the sound fallback: open `/kitchen` and do **not** arm it. Send from the phone. The
   high-contrast badge counts the alert and the orange notice explains why it is silent.
10. Manual, the wake lock: leave the board open on a tablet for longer than its screen timeout.

---

#### Known consequences and deliberate limits

**The board re-reads everything on every notification.** Three small indexed queries per event, restaurant
wide. A table surface can scope its re-read to one sitting; the kitchen board is the whole restaurant by
definition, so there is nothing to scope by. Fine at the size of restaurant this application is for
(ADR-0006); a hundred-cover service would want the queue diffed rather than re-fetched, and that is a
different program.

**Playback failure is detected per alert, not continuously.** The board learns that sound is broken the
next time it tries to play something. Continuous detection would mean a `DotNetObjectReference` callback
and a disposal path on the one surface that must never break, to gain nothing: the badge is already up by
the time anybody could act on the information.

**"Waiting 12m" is coarse and does not tick.** A per-second counter on twenty rows is a re-render per
second per row and tells a cook nothing "12m" does not. It refreshes on every live update, which in a
working service is often.

**A reminder that fires while no board is open is lost as a sound.** The row is written and the ticket is
still on the queue with its age showing, but nothing replays the alert when a board later connects.
Replaying stored alerts on connect would mean a board opened at the end of service screaming through the
whole evening's history; the queue itself is the durable record.

**Staff line-adds do not alert from this screen.** §10.1 gives an initial alert to "every `staff_edit` by
counter or administrator that adds or removes lines" — the transaction already writes that row and
`OrderWorkflow` already broadcasts it. There is simply no surface that produces such an edit yet; the
counter (§11.3) arrives in M5, at which point the behaviour is exercised without a line of new code here.

**Post-close corrections are invisible here, correctly.** §6.7 corrections belong to an administrator on
a settled sitting, and every query on this board filters `closed_at IS NULL`.
### M5 Slice 1 — the counter: the bill, the corrections, and the one number that is never rewritten (landed)

M5's build-order line (§19) reads "**bills, price adjustment, close & settle, end-of-day, counter
fallback QR**, menu management + events, event explorer, hide/unhide, post-close corrections". This slice
is the emphasised half — everything §11.3 puts on the counter's two screens — and it closes the last
place in the system where a guest could order something and nobody could take payment for it.

No migration, no packages, no schema change, and `Program.cs` is untouched. Every table, view, and CHECK
this needs shipped in `0001_initial_schema.sql`: `table_sitting`'s three paired columns, the
`sitting_bill` view, and the two `CHECK ((closed_at IS NULL) = (…))` biconditionals that make a partial
close impossible to write.

---

#### The lock is the feature

§5.3 is four sentences and one of them is load-bearing: "`SELECT … FOR UPDATE` the sitting row … compute
the settled total as the sum over `sitting_bill` for the sitting **under that lock**". §6.6 already has
every order-mutating transaction take `FOR SHARE` on the same row first. Those two modes conflict, and
that conflict is the entire guarantee — no event slips in after the total is computed, and no total is
computed over a half-written order.

`DapperSittingSettlement` is therefore the narrowest service in the data layer: one method, one
transaction, no identifier factory (closing completes a row rather than creating one). It locks, checks
`closed_at IS NULL`, counts what is still pending, sums `sitting_bill`, and stamps all three columns
together. Take a weaker lock and the failure mode is a bill that is quietly wrong, which is the worst
shape of bug this system can have — it does not throw, it does not log, and the person it happens to has
already left.

`COALESCE(sum(...), 0)::numeric(10,2)` rather than a bare `sum`: `sitting_bill` is built *from*
`guest_order`, so a table where everybody joined and nobody ordered has no rows at all, and `sum()` over
no rows is NULL. The column is NOT NULL whenever `closed_at` is set, so a party that ordered nothing would
otherwise fail its own close.

**A losing race reports the winner's close, not a failure.** Two counters pressing Close together produce
one `Closed` and one `AlreadyClosed`, and the second carries the stamped total, instant, and actor of the
first. The person at the till wants to know the table is settled and for how much; "that didn't work" is
both untrue and unhelpful.

#### `PendingLineCountAtClose` is a record, not a warning

§5.3 puts the warning *before* the button — "the counter UI must surface still-pending lines prominently
before offering Close (remove with reason, or knowingly charge)" — and the surface does exactly that,
with a bordered panel that names the count. By the time the transaction returns, the decision has been
made and committed. The count comes back anyway so the confirmation can say "settled at $41.50 — with 2
lines still with the kitchen" instead of implying a clean close. §8.3 is explicit that the bill *includes*
pending lines by design; this is the sentence that admits it out loud.

#### Both totals, forever

§5.3: `settled_total_amount` "is **never rewritten**; post-close corrections (§6.7) live beside it, and
the UI shows both the stamped settled total and, when corrective events exist, the current corrected
total". `CounterSittingSummary` carries both and computes `HasPostCloseCorrections` once, so no surface
has to remember to make that comparison. `AmountToShow` is the stamped total once closed and the live one
while open — what was charged, versus what is owed, and the two are different questions.

#### The counter reads are not a §8.3 view, and not `ISittingDirectory` either

All four projection views are scoped to an order or a sitting the caller already knows. The counter's
questions are "which tables are open right now" and "which have just been settled", which no view
answers. `ICounterBoardReads` rolls `order_current_state` across a whole sitting through a LATERAL —
money, order count, pending and fulfilled line counts, and §5.4's last-activity instant — so twenty open
tables are one query rather than twenty-one.

It is kept apart from `ISittingDirectory` on the pattern `IKitchenBoardReads` set: the directory answers
the join flow's membership question and is consumed by the *guest* surface, and widening its record would
put a billing projection into the type a phone renders a roster from.

Every aggregate is cast in SQL rather than converted in C#. `count(*)` is `bigint`, and `sum()` over a
`bigint` widens to `numeric`; Dapper's constructor binding will feed neither into an `int` parameter. The
casts sit where the intent is visible. The LATERAL is an aggregate with no `GROUP BY`, which returns one
row even over nothing — that is what makes a table where nobody has ordered appear with zeroes rather
than vanish, and a table missing from the counter's list is a table nobody bills.

#### `AppendStaffEventToLivingOrderAsync`, and why `IOrderWorkflow` grew a third method

§11.3 wants the counter to be able to add a line. Most guests order by talking to a person, so the common
case is adding for somebody who joined the table and never pressed Send — and that person has no
`guest_order` row yet (§6.1 creates it lazily inside the first write). `AppendStaffEventAsync` takes an
order identifier, so it cannot serve that case, and minting one on the surface would put §6.1's lazy
creation race outside the transaction that owns it.

The new method routes to `IOrderMutations.AppendToLivingOrderAsync` with the *actor* and the *order owner*
as different people, which is precisely the difference between a counter's staff edit and a guest's own
submission. It is on the bill either way; the log says who put it there, and §10.1 alerts the kitchen
because a counter's line-changing staff edit is one of the two things that does.

#### The surfaces

`/counter` lists open sittings oldest-first — the table that has been sitting longest is the one most
likely to be asking for its bill — with the money, the chips, and a per-table "Show join code". Below it,
tables settled in the last twelve hours, read-only, so a receipt can be checked against the record. It
subscribes to the broadcaster because §9 lists the counter as a consumer of `OrderLinesChanged`: a total
moving while somebody is standing at the till reading it is the point.

`/counter/sittings/{id}` is the bill. Per-person totals from `sitting_bill`, every current line with its
state badge and note, price adjustment (new price + required reason), staff remove (optional reason),
staff add, the pending warning, and Close & settle behind a confirm step.

**A closed sitting is the same page with the controls gone, and that is §11.3's "closed-sitting lookup
(read-only)" rather than a second page.** §6.5.8 admits nothing after a close but an administrator's
corrective events, so a counter's Adjust button there would be a door that only ever answers no.

**One picker at the bottom rather than a panel per person.** The obvious layout puts "Add an item for
Ada" under Ada's lines, which needs a `RenderFragment`-returning method to avoid duplicating the markup
five times. One picker with a "For" select is fewer moving parts, and it is the only control on the page
that can name somebody who has no lines to sit under.

Nothing on either surface enforces §6.5 or §5.3. Every button goes through `IOrderWorkflow` or
`ISittingWorkflow` to the one transaction, which re-decides under the lock — so a guest sending while the
counter presses Close produces one winner and one plainly-reported refusal. What the surfaces refuse on
their own they refuse only to make the answer immediate.

`/counter/tables/{id}/join-code` is §4.5's fallback, deliberately a sibling of the administration page
rather than a shared component: they differ in policy (§3.7 gives the counter its own), in where "back"
goes, and in how large the code is drawn — a counter holds this up across a pass. What they must not
differ in is the code itself, and they do not: both call `ITableJoinTokens.DescribeCurrentAsync` and
render what comes back. Static SSR, so it is a snapshot; §4.3's two-window acceptance means a scanned code
cannot die in a guest's hand, and "Show a fresh code" is the manual refresh. The window-aligned automatic
refresh belongs to the paired display, which has a circuit to run a timer on.

#### `sittings_closed_total` finally has a caller

The meter has existed since M1 and `RecordSittingClosed()` was, until now, dead code — there was nothing
in the system that could close a sitting. `SittingWorkflow` calls it, and only on the call that actually
closed: a losing race would otherwise double-count one close and tell every subscriber to re-query for a
change it did not make. Metrics before the broadcast, matching `OrderWorkflow`, so a subscriber that
re-queries synchronously cannot observe a state change that has not been counted.

The broadcast is not cosmetic. §11.1 flips the guest's table surface to a read-only settled-bill view
**on** `SittingClosed`, and the kitchen drops the table from its queue on the same notification. A close
that committed without announcing itself would leave a settled table still taking orders on every phone
that already had the page open — and those sends would then be refused one by one, with nobody able to
say why. `TableOrderSurface` and `KitchenBoard` already handle the notification; this is the first thing
that publishes it.

#### Header arithmetic

`MainLayout` now shows a Counter link to the `counter` role only, on the same reasoning as the Kitchen
link: an administrator already carries Administration, and six items in a header that wrapped onto three
rows at 375px with four is not a trade worth making. Administrators reach both boards from the landing
page's role-gated area links, which is the one place every door is listed.

#### Tests

- `SittingSettlementTests` (Testcontainers, 9 facts) — the stamp lands as three columns or none; pending
  lines are charged for and counted; adjustments and removals reach the total; every member's order is in
  it; a sitting nobody ordered in settles at zero; a second close writes nothing and reports the first;
  an unknown sitting is untouched; a guest send after close is rejected and the stamp does not move; an
  administrator's §6.7 correction leaves it alone.
- `CounterBoardReadsTests` (Testcontainers, 10 facts) — the roll-up, the all-zeroes case, pending versus
  fulfilled counted separately, closed sittings excluded and open ones oldest-first, the stamped total
  and closer's name (with the username fallback), the window and the cap, a non-positive cap, an unknown
  identifier, and both totals after a post-close correction.
- `CounterWiringTests` (5 facts, no container) — the three registrations resolve, and the workflow
  announces exactly the closes that happened: once for a close, never for a losing race, never for an
  unknown sitting, and yes for a sitting that settles at zero.

#### What is left in M5

End-of-day batch close (§5.4) and the administration sittings list; menu management with its event log
(§7, §11.4); the event explorer; hide/unhide (§6.8) and the hidden-records view; and the administrator's
post-close corrective surface. The engine for that last one already exists and is tested here — what is
missing is the screen.
### M5 Slice 3 — administration sittings: end-of-day, the complete stored record, and the corrections that live beside a settled total (landed)

M5's build-order line (§19) reads "bills, price adjustment, close & settle, **end-of-day**, counter
fallback QR, menu management + events, event explorer, hide/unhide, **post-close corrections**". This
slice is the emphasised part, plus the §11.4 Sittings section that houses both: `/administration/sittings`
and `/administration/sittings/{id}`.

No migration, no packages, no schema change, `Program.cs` untouched, and nothing deleted. Every table and
view this needs shipped in `0001_initial_schema.sql`.

---

#### What was actually missing

Slice 1 built the close transaction and the counter's two screens. It left three holes, all of them the
same shape: the *engine* existed and had no *screen*.

- §5.4's end-of-day pass. `ICounterBoardReads.ListOpenSittingsAsync` has carried `LastEventAt` — "§5.4's
  last-activity timestamps" — since Slice 1, and nothing read it.
- §6.7's post-close corrections. `OrderMutationValidator` has admitted an administrator's corrective
  event on a closed sitting since M4, and `SittingSettlementTests` asserted it there; there was no way to
  author one outside a test.
- §11.4's "complete stored record". `IOrderEventLog` reads one order's log, but for the fold and the
  validator — it maps to domain enums and carries no names, which is right for its two callers and unusable
  on a screen.

#### `ISittingRecordReads` — the third reader of the same tables, on purpose

`DapperSittingRecordReads` is the answer to "what has ever happened at this table", and it is deliberately
not `IOrderEventLog` with extra columns.

§11.4: "Administration renders the **complete stored record** everywhere — full event streams, visibility
logs, security events — never projected or truncated for the administrator; filters narrow only on explicit
request." Two consequences.

**`EventType`, `ActorRole`, and `OperationKind` are the stored strings, not enums.** `IOrderEventLog` maps
to `OrderEventType` and throws on a word it does not recognise, and that is correct there: its callers are
the §6.5 validator and the §8.5 fold, and both must refuse to proceed rather than guess. A screen must do
the opposite. An enum on this path is a projection with a failure mode — a value this build does not know
would either throw and blank the one page whose job is to show what is stored, or be silently mapped to
something wrong. Both surfaces label the values §8.2's CHECKs admit and fall back to the raw string. Same
decision `MenuItemEventEntry` made in Slice 2, for the same reason.

**Nothing is capped or paged.** A removed line, an undone fulfillment, and a superseded price are all in
the answer, because the history outliving the state is the entire point of ADR-0002. A sitting holds a
party's worth of orders and a service's worth of events; the honest read is the complete one.

#### The join that makes the record legible

`order_operation_line_removed`, `_price_adjusted`, `_fulfilled`, and `_fulfillment_reverted` store an
`order_line_identifier` and nothing else about the dish. Rendered as stored, a removal reads "removed
0192f0…" — technically complete and useless to the one person the page exists for.

So every branch of the reader's `UNION ALL` joins back to `order_operation_line_added` on
`order_line_identifier` and on to `menu_item`, and carries the name and quantity on all five operation
kinds. **That join is exact and total rather than a guess**: the column is `NOT NULL UNIQUE` on the adding
table ("the line's identity", §8.2) and is the declared FK target of all four other tables, so exactly one
origin row exists for every operation PostgreSQL will accept. `INNER JOIN`, not `LEFT` — a `LEFT` would only
invite a nullable member for a row that cannot exist.

What is **not** joined is the price. `UnitPriceAmount` stays null off `line_added` and
`NewUnitPriceAmount` stays null off `line_price_adjusted`, because a price is the thing arguments are about
and this record must not synthesise one. An adjustment says what it set; the capture it superseded is on the
`line_added` operation above it, which is where somebody settling the argument reads it from anyway.

Three queries — the orders, then every event across all of them, then every operation across all of them —
grouped in memory. A party of six with a long service is three round trips rather than thirteen, and the
query count does not move with the size of the party.

#### `CloseManyAsync`: one transaction per sitting, and that is normative

§5.4 says "close each via the same §5.3 transaction". That is not phrasing, it is the design.

§5.3's guarantee is that a sitting's total is summed under a `FOR UPDATE` that conflicts with the
`FOR SHARE` every order writer holds (§6.6). One long transaction spanning twelve tables would hold twelve
of those locks until the last one committed — so a guest still ordering at table 1 would block the closing
of table 12, and an error on table 12 would roll back eleven closures that were correct. Twelve short
transactions can do neither.

The loop therefore goes through `CloseAndSettleAsync`, not through `ISittingSettlement` directly, so **each
close is counted and announced at the moment it happens**. Batching the §9 broadcasts to the end of the pass
would leave a settled table taking orders on every phone that had it open for as long as the rest of the
pass took — and those sends would then be refused one by one with nobody able to say why.

**Repeated identifiers are collapsed before anything is attempted.** Not hypothetical: a form can post one,
and the first attempt would then close the sitting while the second found it closed, reporting one table as
both settled by this pass and previously settled by somebody else.

**`EndOfDayResult.SettledTotalAmount` deliberately excludes `AlreadyClosed`.** That total belongs to
somebody else's close; adding it here would report a day that took more than it did.

If the token trips part-way the exception propagates and the sittings already closed **stay** closed. They
were separate committed transactions and there is no undo for a close (§5.3). The surface re-reads and shows
what is still open, which is the truth.

#### `/administration/sittings` — no select-all, nothing pre-ticked

Static SSR, like every administration surface. Open sittings oldest-first with §5.4's last-activity column
(and a coarse "4 hours ago" beside the instant, because the question being asked is "has this table gone
home"), a checkbox per row, and one button.

**There is no select-all and nothing starts ticked.** Closing twelve tables costs twelve deliberate ticks,
and that *is* the confirmation step rather than a second page. The counter's single close has a confirm
because it happens mid-service with a guest waiting; an end-of-day pass is deliberate by nature, and a
select-all next to an irreversible action at the end of a long shift is the wrong affordance to build.

§5.3's pending-lines warning is repeated above the button, summed across every open table. An end-of-day
pass is the most likely moment in the system for somebody to charge for a plate nobody carried.

**This is the one form in the tree that reads its own POST values** —
`await HttpContext.Request.ReadFormAsync(...)` — rather than binding a `[SupplyParameterFromForm]` model. A
checkbox set whose length is however many tables happen to be open has no static model to bind to, and the
alternative already used elsewhere (a select-one picker, as on `ManageTable` and `TableDisplays`) is not what
§5.4 describes. It is not a second read of the body: the Blazor endpoint has already parsed the form to find
which handler to invoke, and `HttpRequest` caches the parsed collection, so this returns the same instance
the framework used. Awaiting the cached path rather than touching `Request.Form` synchronously keeps the
cancellation token honoured.

Post/redirect/get afterwards, with three counts in the query string — closed, already settled, gone. The
money does **not** travel in the URL: the settled list below re-renders from the database and shows every
stamped total in the restaurant's own currency rather than one reconstructed from a query parameter.

#### `/administration/sittings/{id}` — corrections appear only once it is settled

The record renders for an open sitting too; reading history is not a write. The corrective forms do not.

§6.7 is titled "post-close corrections" and §3.7's matrix gives "post-close corrective events" to the
administrator alone. While a table is still eating, the screen built for it is the counter's — and an
administrator already holds the counter role's capabilities (§3.7), so this page links to
`/counter/sittings/{id}` instead of growing a second copy of the counter's controls. `TryBegin` refuses on an
open sitting anyway, for the replayed post the markup does not offer.

**One picker rather than a panel per line.** A per-line correction panel in static SSR needs a distinct form
name per row and a model to bind a dynamic set of them to, which is exactly the shape §5.4's checkboxes had
to escape. `CounterSitting` and `TableDisplays` already settled this: one picker at the bottom that can name
any row above it. Two forms, because §6.2's `staff_edit` and `price_adjustment` are two event types with two
operation subtypes and mutually exclusive payload columns.

The price is rounded once, in C#, away from zero — matching `numeric(10,2)`'s own rule — before it reaches
the transaction, so the operation row, the projection, and the number reported back are the same value
rather than three independent roundings of one input. The same decision `MenuAdministration` made for
reprice in Slice 2.

A refusal renders **in place** rather than redirecting: §6.5.9's per-operation reasons are the whole value of
the message and do not survive a query string. A success redirects, so a refresh cannot re-append.

Adding a line goes through `AppendStaffEventToLivingOrderAsync`, so it works for somebody who joined and
never sent anything — their order row is created inside the same transaction (§6.1), which the surface cannot
do for itself. An item currently marked unavailable is refused, by the surface immediately and by §6.5.4
under the lock regardless. That is deliberate: the alternative is a bill naming a dish the kitchen has said it
cannot make.

#### Both totals, again

`CounterSittingSummary.HasPostCloseCorrections` was built in Slice 1 and had one consumer. Now the settled
list and the record header both use it, because §5.3 requires the stamped total and the corrected total to be
shown together the moment they differ — "what was charged" and "what the record now says is owed" are two
questions, and after this slice an administrator can actually cause them to diverge.

#### Tests

- `SittingRecordReadsTests` (Testcontainers, 13 facts) — one record per order oldest-first with the owner's
  name; every event in sequence with its stored type and role; the actor-name username fallback; a
  `line_added`'s item, quantity, captured price and note; a removal's reason and the item it took off; a
  removed line **gone from the projection and still in the record**; an adjustment's new price and required
  reason with the capture untouched beside it; a fulfillment and its reversal both surviving; one event's
  several operations all staying on that event; orders in other sittings excluded; an unknown sitting empty;
  a sitting nobody ordered in empty; and an administrator's post-close correction in the record with the
  stamped total unmoved.
- `EndOfDayTests` (7 facts, no container) — the registration resolves; three closes are announced
  separately and in order; only the ones actually closed are counted and totalled while all three are still
  attempted; repeated identifiers collapse before anything is attempted; an empty selection touches nothing;
  a sitting settling at zero is still counted and announced; every individual result is carried in the order
  asked; and a null selection throws.

#### What is left in M5

Hide/unhide (§6.8) with the hidden-records view, and the cross-cutting event explorer (§11.4: security,
order, and menu events filtered by subject, actor, type, and time). `ISittingRecordReads` is the order half
of that explorer's engine; `IMenuEventLog` is the menu half; `ISecurityEventLog` reads the third. What is
missing is `order_visibility_event`'s write path and one screen that queries all three.

### M5 Slice 4 — hide and unhide: the guest's own history, and the only way back (landed)

M5's build-order line (§19) reads "bills, price adjustment, close & settle, end-of-day, counter fallback
QR, menu management + events, event explorer, **hide/unhide**, post-close corrections". This slice is the
emphasised word, both halves of it: §11.1's guest history with its per-order Hide, and §11.4's
hidden-records view with the per-record Unhide that is the system's only undo for it.

No migration, no packages, no schema change, `Program.cs` untouched, and nothing deleted. Every table and
view this needs shipped in `0001_initial_schema.sql` — `order_visibility_event` and the
`order_visibility_current` view have been sitting there since M1 with no writer and no reader.

---

#### What was actually missing

M4 Slice 2 recorded the deferral in as many words: "§11.1's per-order **Hide** control and the guest's own
**history** of past orders need `order_visibility_event` writes and a closed-sitting query that reads
across sittings; both are §6.8 work and belong with the §11.4 hidden-records view that is their only
unhide path (M5)." That is exactly what this is.

Until now a guest could see the meal they were eating and nothing else. Every reader in the tree is scoped
to an order or a sitting the caller already names — `IOrderReadModel` for the projection views,
`IOrderEventLog` for the fold, `ISittingRecordReads` for one sitting's complete record — and none of them
can answer "which of *this person's* orders, across every sitting they have ever been in, may they still
see". That question is the whole of §11.1's history section, and its second clause ("may they still see")
is §6.8.

---

#### Hiding is enforced in SQL, once

§6.8's guarantee is that a hidden order is gone from "the owner's own views". Both person-scoped queries in
`IOrderHistoryReads` therefore exclude hidden orders in the statement itself:

```sql
LEFT JOIN order_visibility_current AS visibility
        ON visibility.guest_order_identifier = guest_order.guest_order_identifier
WHERE guest_order.person_identifier = @PersonIdentifier
  AND sitting.closed_at IS NOT NULL
  AND NOT COALESCE(visibility.is_hidden, false)
```

A guarantee that depends on every future page remembering a `Where` clause is not one. The `COALESCE` is
load-bearing in its own small way: "never had a visibility event" and "explicitly unhidden" must read the
same, because §6.8 defines the current flag as the latest event and no events means not hidden.

`ListHiddenOrdersAsync` is the deliberate inverse, and it is the only reader in the tree that goes looking
for hidden rows. It is reached from one page, behind `area.administration` (§3.7).

---

#### Six decisions worth knowing before you read the diff

**The lock is on `guest_order` and nothing else.** Both writes take `SELECT … FOR UPDATE OF guest_order`
before reading the current flag, so two taps on Hide cannot both see "not hidden" and both append. They
deliberately do *not* lock `table_sitting`: §6.6's order-mutating transaction locks the sitting first and
the order second, and a transaction that only ever waits on the order can never be the other half of a
deadlock with it. The sitting's `closed_at` is read in the same statement without a lock, which is sound
because a close is one-way — §5.3 stamps it and nothing in the system clears it, so a value read here
cannot go stale in the direction that would matter.

**A visibility event is not an order event, so it does not go through `IOrderMutations`.** It has no
`sequence_number`, no operations, changes no line and no total, and appears nowhere in §8.5's fold.
Routing it through the §6.6 transaction would take a `FOR SHARE` on a sitting that is closed by definition
and imply, wrongly, that a bill could move because somebody tidied their history.

**Append-only, three rows for a round trip.** Hide writes `hidden`, unhide writes `unhidden`, and the
current flag is the latest of them. A boolean on `guest_order` would have been shorter and would have
thrown away the two questions the log answers: who hid it, and had it been hidden before. §6.8's prose
calls the administrator's row `unhidden_by_administrator`; the stored word is `unhidden` and who did it is
`actor_person_identifier`, because there is no guest unhide to distinguish it from.

**The confirmation is a step, not a `confirm()`.** §6.8 requires it to state "plainly that this cannot be
undone from the guest's account". A browser dialog cannot be read, cannot be styled, and does not exist
without JavaScript — while this page works with none. Tapping Hide navigates to `?hide={order}`, which
renders the warning inline above the row with the only two things to do next.

**Expansion in the hidden-records view is one row at a time, by URL.** §11.4 requires the complete
record — "never projected or truncated" — and a complete record is three queries. Rendering a hundred of
them to draw a list would be three hundred round trips for a page somebody is skimming. `?record={order}`
*is* the "expandable" in §6.8: the list is always complete, and the record of the row an administrator
actually opened is fetched in full.

**The hidden list is not filtered to closed sittings.** `HideAsync` refuses an open one, so a row for a
live sitting cannot arise from the application. If one ever does, this is the one screen that must show it
rather than hide the anomaly (§11.4) — hence a nullable `ClosedAt` on the summary and markup that says
"this sitting is still open" in bold.

---

#### The date filter, and why `RestaurantTime` grew two methods

§6.8's filter is "username, date range, and table". A date range needs a boundary, and §8.1 is
unambiguous about whose calendar day it is: an administrator typing 26 July means the restaurant's 26
July, not UTC's and not their own. §8.1 is equally unambiguous that exactly one type performs that
conversion, so `StartOfDay(DateOnly)` and `StartOfNextDay(DateOnly)` are on `RestaurantTime` rather than
in a `… AT TIME ZONE …` clause — a second place the configured zone is honoured is the place the two
drift.

Both return UTC-normalised instants, and that is not cosmetic: Npgsql refuses to write a `DateTimeOffset`
whose offset is not zero to a `timestamptz` parameter, so a boundary handed straight to a query must
already be UTC.

The range is half-open — the start of the lower day to the start of the day after the upper — so no caller
has to decide whether 23:59:59.999999 is inside a day. In a zone whose clocks move at midnight (Cuba's do)
the local midnight of a spring-forward day does not exist, and `TimeZoneInfo.GetUtcOffset` answers with
the standard offset; the boundary is then out by the size of the shift for one day a year. That is
deliberately preferred over `ConvertTimeToUtc`, which throws on an invalid local time: a filter an hour
wide at one edge is a filter, and a filter that throws is a blank page.

The username filter matches a substring case-insensitively, with `%`, `_` and `\` escaped so a pasted
wildcard cannot silently widen the search to everything. It is written `owner.username::text ILIKE …`
rather than as a bare `LIKE` on the column: `person.username` is `citext` (§8.2) and compares
case-insensitively under equality, but which of the extension's pattern operators a mixed
`citext`/`text` comparison resolves to is not something a query should quietly depend on.

---

#### `ISittingRecordReads` grew a second question

`GetOrderRecordAsync(guestOrderIdentifier)` returns one order's complete record, or `null`. The
hidden-records view lists orders from many different sittings and expands one of them; reading the whole
sitting to render one order would show an administrator the rest of that party's orders as a side effect
of opening a row about one person's hidden meal, and §11.4 is explicit that filters narrow "only on
explicit request" in *both* directions.

The three statements are now built from templates parameterised by one `const` WHERE fragment
(`table_sitting_identifier = @SittingIdentifier` or `guest_order_identifier = @GuestOrderIdentifier`),
composed once into six `static readonly` strings. Nothing is derived from input and every placeholder
stays a parameter; the point is not dynamic SQL but stopping the same 180-line union from existing twice,
which is how a reader ends up fixed in one copy and wrong in the other. `ListOrderRecordsForSittingAsync`
answers exactly as it did — its SQL is character-for-character the same once the fragment is
substituted — and both public methods now sit over one private `ReadRecordsAsync`.

---

#### No new metric

§12's meter list is closed and contains no visibility counter, correctly. The instruments there are the
ones an operator watches a service by — sends, lines, reminders, closes, token validations, sign-ins — and
how often guests tidy their own history is not one of them. The same judgement §11.6 recorded about
profile edits and `security_event`: the vocabulary is closed on purpose, and the honest answer to "should
this be in it" was no.

The §9 broadcast is not optional, though. `VisibilityChanged(orderId)` goes out after each committed
write, and §9 routes it to "table members (history views)" — a guest with their history open on one phone
and the order surface on another would otherwise keep seeing the row they had just asked to have gone.
`OrderVisibilityWorkflow` is the post-commit shell, in the same relationship to `IOrderVisibility` that
`OrderWorkflow` has to `IOrderMutations`; surfaces take the workflow and never the write service.

---

#### Tests

- `OrderVisibilityTests` (Testcontainers, 11 facts) — a hide on a settled sitting by the owner appends one
  row and flips the view; a hide by somebody else, on an open sitting, on an already-hidden order, and on
  an order that does not exist each refuse and write nothing, and the "not the owner" refusal still reports
  whose order it was; an unhide appends the second row and flips back, reporting the owner rather than the
  actor; an unhide of a visible order and of an unknown one write nothing; hide → unhide → hide leaves
  three rows and the order hidden, which is only true if this writer and `order_visibility_current` agree
  on what "latest" means; hiding one order on a two-person sitting leaves the other alone; and an
  administrator can unhide an order whose sitting is somehow still open — the deliberate asymmetry with the
  hide path, arranged directly because the writer will not produce that state.
- `OrderHistoryReadsTests` (Testcontainers, 16 facts) — settled sittings only, newest settled first, with
  the table and the person's own share, and the still-open one absent; a hidden order excluded and an
  unhidden one back; only that person's own orders even when two orders share a sitting; the lines as the
  projection (removal gone, adjustment applied, note carried) rather than the record; empty for a member
  who never ordered; the cap, and a non-positive cap asking for nothing; the hidden list system-wide with
  the username fallback on both the owner's name and the hider's, newest-hidden first; both totals and the
  sitting context on the row; an unhidden order gone from the list; the hide currently in force reported
  after a round trip; the username filter as a case-insensitive substring with wildcards taken literally;
  the table filter; the date range on the sitting's `opened_at`, half-open at both ends; the three filters
  composing; a row whose sitting is somehow open still reported with a null `ClosedAt`; and the visibility
  log oldest-first with the stored word, excluding other orders' events.
- `OrderVisibilityWorkflowTests` (10 facts, no container) — a committed hide announces once and passes its
  arguments through unchanged; the announcement carries the order the *service* reported; each of the four
  hide refusals and both unhide refusals announce nothing; a committed unhide announces once and is handed
  the administrator's identifier while reporting the owner's; hide-then-unhide announces twice; and the
  constructor rejects nulls.
- `OrdersWiringTests` gains two facts: `IOrderHistoryReads` resolves, and `IOrderVisibilityWorkflow`
  resolves *and* drags in a real `IOrderVisibility` — the same shape as the existing `IOrderWorkflow` fact
  and for the same reason.

`OrderTestWorld` gains `AddVisibilityEventAsync`, which writes a visibility row in plain SQL. That is the
established pattern in that class and the right one here twice over: it keeps a bug in the writer from
looking like a bug in the reader, and it reaches the state the writer refuses to create.

---

#### Where to look if the build breaks

**`HiddenRecords.razor`**, and specifically its two `<text>` blocks and the `class="hidden-record @(…)"`
ternary. Both idioms already exist in the tree — `<text>` in nineteen places, the nested-quote class
ternary in `CounterSitting.razor` and `KitchenBoard.razor` — so neither is new ground, but this file uses
them in the same markup. It deliberately declares no locals inside a markup `@foreach`: nothing else in
the tree does, so `IsExpanded(summary)` and `CountSentence(count)` are methods instead.

Then `SittingRecordReads.cs`'s template refactor. `OrdersTemplate`, `EventsTemplate` and
`OperationsTemplate` are `private static string` methods returning raw interpolated strings, consumed by
six `static readonly` fields declared after them; both scope fragments are `const`, so there is no
initialisation-order hazard. `ListOrderRecordsForSittingAsync` is now an expression-bodied `async` method
(`=> await ReadRecordsAsync(…).ConfigureAwait(false)`), which is legal but is the one shape in this slice
that has no precedent in the tree.

Three things I could not check without a compiler, each deliberate:

1. `form[OrderField]` is read out to a `string?` local before `Guid.TryParse` in both new surfaces.
   `StringValues` has an implicit conversion to `string?`, and `Guid.TryParse` has both a `string?` and a
   `ReadOnlySpan<char>` overload; being explicit keeps overload resolution off that question entirely.
2. `= ANY(@GuestOrderIdentifiers)` binds a `Guid[]` as one `uuid[]` parameter. `DapperMenuDirectory`
   already does this, so the shape is proven — but this is the first use of it in `Orders`.
3. `ESCAPE '\'` in a C# raw string literal is a single literal backslash on both sides: raw strings do not
   process escapes, and `standard_conforming_strings` (on by default since PostgreSQL 9.1) means the SQL
   string is one backslash rather than the start of an escape sequence.

Everything else is ordinary C# and SQL in the shapes the surrounding files already use — the readers are
`DapperCounterBoardReads`'s aliased-column, internal-row-type pattern with a `CROSS JOIN LATERAL … LIMIT
1` for the latest visibility event, and both surfaces are the static-SSR post/redirect/get shape
`ManageTable` established and `AdministrationSittings` repeated, including its `ReadFormAsync` note.

---

#### What is left in M5

The cross-cutting **event explorer** (§11.4: security, order, and menu events filtered by subject, actor,
type, and time). All three engines exist — `ISittingRecordReads` reads the order half, `IMenuEventLog` the
menu half, `ISecurityEventLog` the third — and what is missing is one screen that queries all three and a
shared filter over them. After that, M5 is closed and M6 is hardening: the Playwright matrix (fifteen
skips today, two of which — `Guest_HidesClosedOrder_AdminCanUnhide` and the join/token pair — this slice
finally makes writable), the restore drill, and CI.

### M5 Slice 5 — the event explorer: three logs, one question (landed)

M5's build-order line (§19) reads "bills, price adjustment, close & settle, end-of-day, counter fallback
QR, menu management + events, **event explorer**, hide/unhide, post-close corrections". This slice is the
emphasised phrase, and it is the last thing M5 owed. §11.4 spells it out in one clause: "Event explorer
(filter security/order/menu events by subject, actor, type, and time)."

No migration, no packages, no schema change, and nothing deleted. Every table this reads shipped in
`0001_initial_schema.sql`. `Program.cs` gains one `using` and one call — the first startup edit since M4
Slice 1, and the reasoning for it is below.

---

#### What was actually missing

Three append-only logs, three readers, and no way to ask a question of all three at once.

`ISecurityEventLog` writes `security_event` and has never had a read side at all. `IMenuEventLog` reads
`menu_item_event`, scoped to one item or capped to a recent-activity panel. `ISittingRecordReads` reads
`order_event`, scoped to one sitting or one order. Every one of them takes a subject the caller already
names, which is correct for the screens they serve — a person's management page, an item's history, a
sitting's record — and useless for the question §11.4 poses, which is the opposite shape: *what happened,
anywhere, in this window*.

Answering that by calling the three readers and merging in memory would mean fetching every event in the
restaurant in order to render the fifty most recent. The interleaving, the ordering and the cap have to
happen in one statement, so the statement had to exist.

---

#### One statement, sixteen columns, three branches

`DapperEventExplorerReads` is a `UNION ALL` over the three tables inside a subquery, with the filter, the
ordering and the `LIMIT` applied once to the union's output.

```sql
FROM (
    SELECT 'security'::text AS event_stream, … FROM security_event …  WHERE @IncludeSecurityEvents::boolean
    UNION ALL
    SELECT 'order'::text    AS event_stream, … FROM order_event …     WHERE @IncludeOrderEvents::boolean
    UNION ALL
    SELECT 'menu'::text     AS event_stream, … FROM menu_item_event … WHERE @IncludeMenuEvents::boolean
) AS event_row
WHERE (@SubjectPattern IS NULL OR event_row.subject_search ILIKE @SubjectPattern ESCAPE '\')
  AND …
ORDER BY event_row.occurred_at DESC, event_row.event_identifier DESC
LIMIT @MaximumCount;
```

The stream flags sit *inside* the branches so a stream that is switched off is not scanned at all; the
other four bounds sit outside, written once rather than three times, because PostgreSQL pushes qualifiers
down through a `UNION ALL` on its own and three copies of a WHERE clause is how one copy ends up fixed and
the other two wrong. That is the same failure `DapperSittingRecordReads`' shared `const` fragments exist to
prevent, reached from the other side.

**Every column is aliased in every branch**, which `DapperSittingRecordReads`' five-way union deliberately
does not do. PostgreSQL takes the names from the first branch and ignores the rest, so the aliases below
the first are documentation — and with sixteen columns drawn from three unrelated tables, documentation is
exactly what stops a future edit inserting a column into one branch and silently shifting five others.

**Every missing column is cast** (`NULL::uuid`, `NULL::bigint`, `NULL::text`, `NULL::numeric(10,2)`) and
every `citext` is cast to `text` before it meets a `text` from another branch, so no column's type depends
on how the planner resolves `citext` against `text` in a union.

---

#### The security branch's LEFT JOIN is the load-bearing line

`security_event.actor_person_identifier` is the only nullable actor column in the three tables (§8.2:
NULL means the subject acted on themselves, or the system did). Its join to `person` is therefore the only
`LEFT` one in the statement.

An `INNER` there would compile, run, return rows, and silently hide every lockout and every failed sign-in
from the one screen an administrator opens to look for them. It does not throw and it does not look
wrong. `Security_WithNoActor_KeepsTheRowAndReportsNoActor` is the test that notices.

The two search expressions are `concat_ws` rather than `||` for the neighbouring reason: `||` annihilates
on NULL, so a person with no display name would become unfindable by username. For an actorless security
event `concat_ws` yields the empty string, which matches no actor filter — correct, and quieter than a
NULL needing a `COALESCE` at every use.

---

#### Six decisions worth knowing before you read the diff

**The type filter is exact, and that is only sound because the three vocabularies do not overlap.**
`created` is the menu's word; `account_created` is security's, and contains it. A substring match would
answer "when was this item created" with a list of accounts. One flat `event_type = @EventType` across the
union works because no word appears in two streams — a property of the schema rather than a coincidence,
so `Catalogue_TheThreeVocabularies_DoNotOverlap` asserts it and `Catalogue_SecurityEventTypes_AreExactly…`
pins the biggest list to `SecurityEventType.All`.

**Three streams, not four.** §6.8's `order_visibility_event` is deliberately absent. §11.4 names security,
order and menu, and gives visibility its own screen — the hidden-records view, where its log sits beside
the order it is about and next to the Unhide button that is its only counterpart. Folding it in here would
put "somebody tidied their history" in the same list as the meal, without the one control that answers it.

**Nothing is projected, including on the way out.** `event_type`, `actor_role` and the stream word all
travel as strings, and the reader cannot throw on a value it does not recognise. The surface renders a
friendly label for the words §8.2's CHECKs admit and falls back to the raw string — the same rule
`ManageSitting` and `HiddenRecords` already follow, for §11.4's reason: an enum is a projection with a
failure mode, and the one screen whose job is to show what is stored is the last place a word may be
mapped to something it is not.

**The subject is not the same kind of thing in each stream, and the filter reaches all three.** A security
event's subject is a person, searchable by username or display name; an order event's is the order,
searchable by its owner *or by its table label* — which is what an administrator actually remembers; a
menu event's is the item, searchable by name. `SubjectFilter_ReachesPeopleTablesAndItems` covers all four
routes.

**Wildcard escaping is copied from `DapperOrderHistoryReads`, character for character.** `%`, `_` and `\`
are escaped so searching for the username `a_b` does not also find `axb`. Two search boxes on two
administration screens that escaped differently would be a bug nobody could see.

**The page is read-only, and that is a design constraint rather than a stage.** There is no control on
`/administration/events` that changes anything. Every row links to the screen that owns the thing it is
about, and that screen has the buttons. An audit log with edit affordances on it is a different and much
worse object.

---

#### Why `Program.cs` was edited this time

The four existing extensions each resisted being split for a real reason: a host that wired ordering
without the reminder loop would alert and never remind, and one that wired ordering without the menu would
have a staging area that could list nothing. Both are silent half-failures, so both stay welded together —
and that is why M5 Slices 2 and 4 both went out of their way to avoid a startup edit.

The explorer is not like that. It reads identity's audit log, ordering's event log and the menu's, and
belongs to none of them; putting it in `AddRestaurantOrders()` would make the ordering extension the
registrar of a reader of `security_event`. And its failure mode if omitted is not silent — one
administration route throws on resolve, loudly, in front of the person who asked for it. So it gets
`AddRestaurantEventExplorer()`, one scoped registration whose only dependency is the connection factory.
`EventsWiringTests` asserts that last part directly: a registration that grew a clock, an identifier
factory, a broadcaster or a metric would mean the explorer had acquired a write path or a notification,
and it must never have either.

---

#### `EventExplorerQuery`, and why the URL logic left the page

`HiddenRecords.razor` holds the same logic inline — parse two dates, notice a reversed range, rebuild the
path with every bound preserved — and none of it is reachable by a test, because reaching it means
rendering a static-SSR component with an `HttpContext`. It is also the part most likely to be quietly
wrong: every narrowing affordance on the page rebuilds the URL from the current selection, so a bound that
survives parsing but not the rebuild silently changes the administrator's question the moment they click
anything, and nothing looks broken while it happens.

Lifting it into one immutable class in the web layer costs a file and buys twenty-five container-free
facts, including the round trip (`ReparsingItsOwnPath_YieldsTheSameFilter`) and the restaurant-zone date
conversion asserted against two different zones. The same treatment is available to `HiddenRecords` later;
this slice does not touch it, because a refactor of a green page is not this slice's business.

Three parsing rules are worth stating out loud:

- **No stream named means all three.** An unchecked checkbox submits nothing, so "cleared every box and
  pressed the button" and "opened the page fresh" arrive as byte-identical requests. They cannot be told
  apart, so they must mean the same thing, and the only defensible meaning is §11.4's default: everything.
  The page then re-checks all three boxes, which is how it says so.
- **Nothing is ever refused.** An unreadable date, a reversed range, a misspelled stream, a type this
  build does not know — each is ignored or passed through, and each adds a sentence to `Problems` that the
  page prints. A filter returning a wider answer is still a filter; a filter that throws is a blank page in
  front of somebody trying to find out what happened.
- **Dates are the restaurant's** (§8.1), converted through `RestaurantTime.StartOfDay`/`StartOfNextDay`
  into a half-open UTC range. The mirror image of the rendering rule, and the reason the conversion is not
  an `AT TIME ZONE` in the query — §8.1 wants exactly one type performing it.

---

#### Files

**New (7)**

| Path | What |
| --- | --- |
| `src/MyRestaurant.DataAccess/Events/EventExplorerReads.cs` | `EventStream`, `EventTypeCatalogue`, `ExplorerEvent`, `EventExplorerFilter`, `IEventExplorerReads`/`DapperEventExplorerReads` |
| `src/MyRestaurant.WebApplication/Events/EventExplorerQuery.cs` | query string ↔ filter ↔ canonical URL |
| `src/MyRestaurant.WebApplication/Events/EventsServiceCollectionExtensions.cs` | `AddRestaurantEventExplorer()` |
| `src/MyRestaurant.WebApplication/Components/Pages/Administration/EventExplorer.razor` | `/administration/events` |
| `tests/MyRestaurant.DataAccess.Tests/Events/EventExplorerReadsTests.cs` | 21 facts (19 Testcontainers, 2 pure) |
| `tests/MyRestaurant.WebApplication.Tests/Events/EventExplorerQueryTests.cs` | 25 facts, no container |
| `tests/MyRestaurant.WebApplication.Tests/Events/EventsWiringTests.cs` | 3 facts, no container |

**Edited (7)**

| Path | What |
| --- | --- |
| `src/MyRestaurant.WebApplication/Program.cs` | one `using`, one `AddRestaurantEventExplorer()` |
| `…/Administration/AdministrationHome.razor` | one `<a>` — Events in the header actions |
| `…/Administration/AdministrationTables.razor` | one `<a>` |
| `…/Administration/AdministrationMenu.razor` | one `<a>` |
| `…/Administration/AdministrationSittings.razor` | one `<a>` |
| `…/Administration/HiddenRecords.razor` | one `<a>` |
| `tests/MyRestaurant.DataAccess.Tests/Orders/OrderTestWorld.cs` | `AddSecurityEventAsync`, `AddMenuItemEventAsync`, two statements |

No `docs/TECHNICAL_SPECIFICATION.md`, `docs/REQUIREMENTS.md`, or ADR edit: this realizes behaviour §11.4
already specifies, in the words it already uses.

---

#### Where to look if the build breaks

**`EventExplorer.razor`**, as always. Three things in it have no exact precedent in the tree:

1. `[SupplyParameterFromQuery(Name = …)] private string[]? Streams { get; set; }` — the first array-valued
   query parameter in the project. Repeated `?stream=a&stream=b` binds to it; the attribute's `Name` is a
   `const` on `EventExplorerQuery`, so the form field and the parser cannot drift.
2. `<fieldset>`/`<legend>` and `<optgroup>` — ordinary HTML, but the first of each here.
3. `class="event-stream-badge event-stream-@entry.Stream"` — an implicit expression finishing an attribute
   value. Common Razor, first use in this tree.

The one thing deliberately avoided: no locals are declared directly inside a markup `@foreach`, matching
`HiddenRecords`. Pattern variables inside an `@if` within a `@foreach` *are* used, which that file already
does.

Then `EventExplorerReads.cs`, and specifically the union's column list. Two things I could not check
without a compiler, both deliberate:

1. `WHERE @IncludeSecurityEvents::boolean` as an entire WHERE clause — a bare boolean parameter, cast for
   the same belt-and-braces reason `DapperSecurityEventLog` casts `@ActorPersonIdentifier::uuid`.
2. `concat_ws(' ', subject.username::text, subject.display_name)` — first use of `concat_ws` in the tree.
   It returns `text`, skips NULLs, and yields `''` (not NULL) when every argument is NULL, which is the
   behaviour the actorless-security-event case depends on.

`ESCAPE '\'` inside a C# raw interpolated string is one literal backslash on both sides — raw strings do
not process escapes, and `standard_conforming_strings` makes the SQL string a single backslash. Same as
`DapperOrderHistoryReads`.

---

#### Build/test checklist for this slice

1. `dotnet restore` — no new packages, no migration, no schema change.
2. `dotnet build` — the Razor page is the likely compiler-catch home, as always.
3. `dotnet test` — expect **+49 facts** (21 + 25 + 3). Taking M5 Slice 4's projected 877 as the baseline:
   926 total, 911 passing, 15 still skipped with a container engine present. Without one, the 19
   container-dependent facts here skip as well.
4. `bash run.sh --smoke` — should be unaffected, but `Program.cs` changed, so this one is not a formality
   this time.
5. `bash run.sh --containers-only`, then as an administrator:
   - `/administration/events` — three streams interleaved, newest first.
   - Untick Orders and Menu — only security events; the URL says `?stream=security`.
   - Type a username into Subject; then the same word into Actor — different answers.
   - Pick a type from each of the three optgroups.
   - Set a date range, then reverse it — the range is dropped and the page says why.
   - Click a stream badge, then "only this subject" — the other bounds survive.
   - Follow a security row to its person, an order row to its sitting, a menu row to its item.

---

#### What is left in M5

**Nothing.** §19's M5 line is closed: bills, price adjustment, close & settle, end-of-day, counter fallback
QR, menu management + events, event explorer, hide/unhide, post-close corrections.

M6 is hardening: the Playwright matrix (fifteen skips today), the backup/restore drill, and CI.

### M6 Slice 1 — continuous integration: the machine that compiles what I cannot (landed)

M6's build-order line (§19) reads "full E2E suite (§16.3), backups + restore drill, cloudflared production
profile + tunnel docs, quick-tunnel demo script with warning, OPERATIONS runbooks, **CI pipeline**". This
slice is the emphasised phrase, taken out of order on purpose — see below. §16.4 states it in one clause:
"GitHub Actions — build, unit, integration (service container PostgreSQL), E2E (compose), publish image on
tag."

No migration, no packages, no schema change, no new C# file, and nothing deleted. One analyzer warning is
fixed and `Directory.Build.props` finally answers a question it has carried since M1.

---

#### Why this is M6 Slice 1 rather than M6 Slice 4

§19 lists the E2E suite first, and every previous milestone has walked its line roughly in order. This one
does not, for a reason that is specific to how this repository gets built.

Every slice from M1 to M5 was written without a compiler. The authoring environment has no .NET SDK and no
package feed, so each slice's Razor pages and SQL went out on a "where to look if the build breaks" note
and were compiled for the first time on the owner's workstation. That loop worked — 934 facts and a green
sweep say so — but it has exactly one participant, it runs only when he runs it, and its results live in a
terminal scrollback that has to be copied into a conversation before anyone else can see them.

The E2E matrix does not fix that. It adds fifteen more facts to the same loop. CI moves the loop off one
workstation: a push compiles the tree, runs all 934 facts against real PostgreSQL, and boots the actual
production image — and says so in public, on every commit, without anybody remembering to ask. Sequencing
the pipeline ahead of the suite it will eventually run is the difference between hardening the project and
hardening one person's habits.

The second reason is narrower and concrete. `boot-smoke` (below) is the gate that catches "the app starts
but one route throws" — which is precisely the failure mode that cost this project a slice's worth of
back-and-forth at the end of M2, diagnosed by reading container logs by hand. That gate is worth more,
sooner, than any single E2E scenario.

---

#### Three gates, and what each one can prove that the others cannot

**`shell-scripts`.** Every tracked `*.sh` must parse under `bash -n` and pass shellcheck. This is not a new
standard — it is the project's existing pre-delivery discipline, which until now lived in an instruction
rather than in the tree. Five scripts (`export.sh`, `run.sh`, `scripts/backup.sh`, `scripts/quick_tunnel.sh`,
`scripts/restore.sh`) plus this slice's `scripts/ci_local.sh` are clean at shellcheck's *strictest* practical
level, `--severity=style`.

The gate nonetheless blocks at `--severity=warning`, one notch lower, with `style` running immediately after
as `continue-on-error: true`. The reason is drift rather than doubt: `ubuntu-latest`'s shellcheck moves on
its own schedule, and a new style check would turn the build red on a day nobody touched the repository. A
red build with no commit behind it is how people learn to stop reading the build.

**`build-and-test`.** Restore, a **Release** build with warnings escalated to errors, then the whole suite.
Two details matter.

The SDK is requested as `10.0.x`, not as `global.json`'s exact `10.0.100`. `global.json` pins that version
with `rollForward: latestMinor`, so any 10.0 SDK satisfies it; asking a runner for an exact patch that has
since been delisted is a way to fail that has nothing to do with the code.

And the data-access tests *run* here. On the owner's Fedora box, Testcontainers needs the rootless Podman
user socket activated by hand (`systemctl --user enable --now podman.socket`) or roughly 190 integration
facts skip — quietly, with a green summary. A GitHub runner has `/var/run/docker.sock`, so
`ContainerEngineDiscovery` returns early, Testcontainers uses the default endpoint, and those facts execute
on every push whether or not anybody remembered the socket. That is a real change in what "the tests pass"
means.

**`boot-smoke`.** The `Containerfile` is built and the resulting image is started against a real
PostgreSQL, and `/healthz/ready` must answer 200 within 180 seconds.

Nothing else in the suite covers this. `/healthz/ready` returns 200 only after DbUp has applied every
migration and reported the schema current, and it can only be *reached* if the composition root resolved —
which means every `Add…()` extension in `Program.cs` produced a satisfiable graph. A missing registration,
a migration that parses in a test but conflicts against a fresh database, a `RestaurantOptions.Validate()`
rejection from a bad default: each is invisible to unit tests, each kills a production deployment, and each
now fails a pull request instead of a service.

---

#### Why `compose.yaml` is not used in CI

The obvious implementation of `boot-smoke` is `docker compose --profile dev up --build`, and it does not
work. `compose.yaml` mounts the data-protection volume as
`dataprotection-keys:/var/lib/myrestaurant/dataprotection:U` — the `:U` asks Podman to chown the volume to
the container user, which is correct for the canonical rootless engine (ADR-0004) and which Docker Compose
rejects as an invalid mount mode. The file's own comment already says so: "if you are on Docker and it
complains, drop the ':U'".

Editing `compose.yaml` to suit CI would mean changing the canonical dev stack to accommodate the one
environment that is not the target, and shipping a second `compose.ci.yaml` would mean a second file that
can drift from the first. A job-level `services:` PostgreSQL plus one `docker run --network host` is the
same topology — same image, same environment variables, same readiness probe — with none of that argument,
and it leaves the canonical stack the only compose file in the tree.

`--network host` is what makes it one command: the container reaches the service database on the runner's
published `127.0.0.1:5432` and publishes its own `8080` back for the probe, so no user-defined network,
no container-to-container DNS, and no port-mapping arithmetic.

---

#### `TreatWarningsAsErrors`, and a note finally discharged

`Directory.Build.props` has carried this since M1:

> Warnings are NOT errors yet: a fresh clone must build through analyzer drift on a newer SDK. Flip to
> true once the first green build is established (BUILD_PROGRESS).

The first green build was established four milestones ago; the note stayed anyway, because both of its
halves are true at once. Warnings *should* be errors — the alternative is a build log nobody reads. And a
fresh clone on tomorrow's SDK *should not* refuse at the door over an analyzer that did not exist when the
code was written, because a clone that will not build is worse than a warning that was not read.

Conditioning on `ContinuousIntegrationBuild` resolves it without choosing a side. CI passes
`-p:ContinuousIntegrationBuild=true`, which is where strictness belongs — a machine, with a commit to point
at, on a tree somebody just changed. A `dotnet build` on a workstation stays lenient. The property is not
invented for this: it is MSBuild's own, and setting it also makes the build deterministic, which is what it
is for.

`NU1901`–`NU1904` are exempted through `WarningsNotAsErrors` even under CI. Those are NuGet audit findings,
raised when an advisory is published against a package this tree already depends on. That is real news and
worth surfacing, but it arrives without a commit — so it surfaces in a `continue-on-error` step running
`dotnet list package --vulnerable --include-transitive`, and it does not turn a pull request red for
something the pull request did not do.

The one warning in the tree had to go first: `SittingRecordReadsTests.cs:354` used
`Assert.Single(record.Events.Where(…))`, which xUnit's own analyzer flags as **xUnit2031** ("do not use a
Where clause to filter before calling Assert.Single"). It is now
`Assert.Single(record.Events, stored => stored.EventType == "fulfillment")` — the overload the analyzer
recommends, `Assert.Single<T>(IEnumerable<T>, Predicate<T>)`, verified against `xunit/assert.xunit`. Same
assertion, better failure message (the predicate overload reports how many matched and where), and the only
line in the repository standing between the tree and a strict build.

---

#### `release.yml` calls `ci.yml` rather than repeating it

§16.4 asks for "publish image on tag". A publish pipeline that does not first verify is a way to ship an
image nobody tested, and a publish pipeline that verifies with *its own copy* of the gates is two sets of
gates to keep identical. So `ci.yml` declares `workflow_call:` alongside its push and pull-request
triggers, and `release.yml`'s first job is `uses: ./.github/workflows/ci.yml`. A tag runs exactly the gates
a push runs, because they are the same file.

Tags land as `ghcr.io/<owner>/<repository>:<version>`, `:<major>.<minor>`, and `:sha-<commit>`, from
`docker/metadata-action`'s semver patterns. `concurrency.cancel-in-progress` is `false` here — the opposite
of `ci.yml`, where a superseded push should be abandoned. A half-pushed manifest is worse than a slow one.

**`linux/amd64` only, deliberately.** The `Containerfile` runs a full `dotnet publish` inside its build
stage. Doing that for arm64 through QEMU emulation is slow enough to risk the job timeout, and the right
fix is not a longer timeout — it is a cross-compiled publish (`-r linux-arm64` from an amd64 SDK), which is
a `Containerfile` change rather than a workflow line. Noted in `docs/OPERATIONS.md` §14 for whoever wants
to run this on an SBC.

---

#### `scripts/ci_local.sh`

CI's build is strict; a workstation's is not. That makes "it builds here" and "it builds in CI" two
different questions, and nothing in the tree asked the second one. This script asks it: the same shell
lint, the same `dotnet restore`, the same `--configuration Release -p:ContinuousIntegrationBuild=true`
build, the same test invocation, in the same order, with each gate announcing itself so a failure is
attributable at a glance.

`--with-smoke` appends `./run.sh --smoke`. That is the closest local equivalent of the `boot-smoke` job and
not the same thing: same migrations and same readiness probe, but the app runs on the host rather than
inside the image, so it does not prove the `Containerfile` builds. `./run.sh --containers-only` is the real
local analogue; the script says so rather than implying otherwise.

`--help` prints the header comment block by walking it with awk until the first line of code, rather than
by a hard-coded `sed` range — a range is a second place to maintain, and the kind that goes stale silently.

---

#### `dependabot.yml`

Weekly, two ecosystems. NuGet reads `Directory.Packages.props`, so every bump lands in the one file that
pins versions (REQUIREMENTS §2) rather than scattered across csproj files. OpenTelemetry, xunit and Npgsql
are grouped, because those families move in lockstep and six pull requests that only build together are six
red pull requests.

Worth stating because it is a project rule that needs no configuration: Dependabot does not propose a
prerelease for a dependency currently on a stable version. "No prerelease packages" is satisfied by
default.

---

#### Files

**New (5)**

| Path | What |
| --- | --- |
| `.github/workflows/ci.yml` | `shell-scripts`, `build-and-test`, `boot-smoke`; also `workflow_call`-able |
| `.github/workflows/release.yml` | calls `ci.yml`, then pushes to GHCR on `v*` |
| `.github/dependabot.yml` | weekly NuGet + github-actions updates, grouped |
| `scripts/ci_local.sh` | the same gates, locally |
| `docs/_append/BUILD_PROGRESS-m6-slice-1.md` | this section |

**Edited (5)**

| Path | What |
| --- | --- |
| `Directory.Build.props` | `TreatWarningsAsErrors` under `ContinuousIntegrationBuild`; `WarningsNotAsErrors` for NU1901-1904 |
| `tests/…/Sittings/SittingRecordReadsTests.cs` | one line — the xUnit2031 fix at 354 |
| `README.md` | status (M1-M5 closed), a CI section and badge, SDK pin corrected, four stale caveats retired |
| `docs/OPERATIONS.md` | new §14 — CI, cutting a release, deploying from the registry |
| `_CHANGES.md` | this slice's delivery note |

No `docs/TECHNICAL_SPECIFICATION.md`, `docs/REQUIREMENTS.md`, `docs/DOCUMENTATION_REVIEW.md`, or ADR edit:
this realizes behaviour §16.4 already specifies, in the words it already uses. The one substantive
deviation from the spec's phrasing is recorded here rather than there — §16.4 says "integration (service
container PostgreSQL)", written before Testcontainers landed in M1; the integration tests bring up their
own PostgreSQL 17 container and need no service container, and `boot-smoke` is where a service container
actually earns its place.

---

#### Where to look if this breaks

Unusually for this project: **not the code**. Nothing in `src/` changed, and the only test-project change is
one line inside one assertion.

1. **`build-and-test` fails on a warning.** That is the gate working. The build log names the file, the
   line and the code. Fix it, or — if it is genuinely not actionable — add the code to
   `WarningsNotAsErrors` in `Directory.Build.props` with a comment saying why. Note this can happen on a
   commit that touched nothing relevant, because the runner's SDK is newer than 10.0.110; that is the
   tradeoff the CI-only condition accepts.
2. **`boot-smoke` fails at "the web container exited before it became ready".** Read the `container logs`
   step that runs on failure. Three candidates, in order of likelihood: a migration that conflicts against
   a genuinely empty database, a `RestaurantOptions.Validate()` rejection (the job sets `RESTAURANT_*`
   explicitly, so a new required setting with no default would land here), and a DI registration added
   without its dependency.
3. **`shell-scripts` fails.** Reproduce exactly with `scripts/ci_local.sh`, which runs the same two
   shellcheck passes at the same two severities.
4. **`release.yml` cannot push.** `packages: write` is declared on the `image` job. The first push to a new
   GHCR package also needs the repository to be allowed to create it — a one-time settings step, not a
   workflow bug.
5. **An action version is rejected** ("Unable to resolve action"). The pinned majors were resolved at
   authoring time: `checkout@v7`, `setup-dotnet@v6`, `upload-artifact@v7`, `cache@v6`,
   `docker/metadata-action@v6`, `docker/setup-buildx-action@v4`, `docker/login-action@v4`,
   `docker/build-push-action@v7`. Dependabot's `github-actions` ecosystem now keeps them current.

---

#### Build/test checklist for this slice

1. `dotnet restore` — no new packages, no migration, no schema change.
2. `dotnet build` — unchanged from the last green sweep; no Razor page was touched.
3. `dotnet test` — **still 934 total, 919 passing, 15 skipped.** The xUnit2031 fix changes an assertion's
   form, not its meaning or its count. If the total moved, something else moved with it.
4. `bash -n scripts/ci_local.sh && shellcheck --severity=style scripts/ci_local.sh` — both clean as
   delivered.
5. `bash scripts/ci_local.sh` — the real check for this slice. It must pass, including the strict build.
   Then `bash scripts/ci_local.sh --with-smoke` once.
6. `git add .github scripts/ci_local.sh && git commit && git push` — and then watch the Actions tab, which
   is the actual deliverable. Expect roughly: `shell-scripts` under a minute, `build-and-test` five to
   eight (first run has no NuGet cache), `boot-smoke` four to seven.
7. Optional, and worth doing once while the pipeline is fresh: `git tag --annotate v0.6.0 --message 'M6
   slice 1'` and push it, to prove the release path end to end before it matters.

---

#### What is left in M6

The E2E matrix (§16.3 — fifteen skipped facts and no harness), and the backup/restore drill as something
executable rather than a runbook §6 describes in prose. The cloudflared production profile, the
quick-tunnel script and the OPERATIONS runbooks were all delivered inside earlier milestones, so §19's M6
line is down to two items.

Next slice is the E2E harness: a fixture that brings up a stack, a Playwright browser context, and the
first scenarios from §16.3 — starting with 1, 13 and 14, because the virtual authenticator and the
controllable-clock plumbing they need are what every other scenario is waiting on.
### M6 Slice 2 — the end-to-end harness, and the first three scenarios (landed)

§16.3 has listed fifteen required end-to-end scenarios since M1, and since M1 all fifteen have been
`[Fact(Skip = ...)]` — a version-controlled promise with nothing behind it. The skip reason said what was
missing: "needs Playwright browsers and a live instance". This slice supplies both, plus the two pieces
the reason did not mention (a WebAuthn virtual authenticator and a way to reason about token windows),
and then spends them on scenarios **1**, **13** and **14**.

No migration, no schema change, no `Directory.Packages.props` edit, nothing deleted, and nothing in
`src/` touched at all.

---

#### What a "live instance" turned out to mean

Six things, none of which existed:

1. **A database per scenario.** A shared PostgreSQL 17 container (Testcontainers, as the data-access
   tests already use) with a `CREATE DATABASE` per scenario.
2. **The application, actually running.** Booted from its own build output as a child process, on a
   loopback port reserved by binding port 0.
3. **A browser.** Chromium, launched once for the class, with a fresh context per scenario so cookies
   never leak between them.
4. **A software authenticator.** A CDP virtual authenticator, which is what §16.3 means by "passkey via
   virtual authenticator".
5. **An origin the app will accept and the browser will trust.** See below — this was the subtle one.
6. **A way to see why a scenario failed.** The browser only ever sees a 500; the server has the stack.
   The harness captures the child process's stdout and stderr and puts the tail in the exception message.

---

#### Why a child process rather than `WebApplicationFactory`

`WebApplicationFactory` hosts the app on an in-memory `TestServer`, and a real browser cannot connect to
an in-memory anything. The documented workaround — subclass it and start a second, Kestrel-backed host
alongside — additionally needs the entry point as a type argument, and `Program.cs` here is top-level
statements that `return 1` on invalid configuration, so its generated `Program` is internal.

Booting the built binary avoids both problems and is the more honest test besides. It runs the same
composition root, the same DbUp pass, and the same fail-fast `RestaurantOptions.Validate()` a deployment
runs. A scenario that reaches its first assertion has already proved what `boot-smoke` proves.

Two details make it work. `ASPNETCORE_CONTENTROOT` points at the web application's **source** directory,
because `Program.cs` serves assets with `UseStaticFiles()` and `wwwroot` is not copied into `bin` —
without it, `js/passkey.js` 404s and every passkey ceremony fails with nothing in the browser to say
why. And the configuration and target framework are read from the *test assembly's own* output path, so
a Debug run boots a Debug application and a Release run boots a Release one; a hard-coded either would
have silently tested a stale binary on one of the two.

---

#### The origin, which is where this could have quietly not worked

The app is served at `http://localhost:{port}` while `RESTAURANT_PUBLIC_ORIGIN` is set to
`https://localhost:{port}`. That mismatch is deliberate and it is doing three jobs at once:

- §13 **refuses to start** on a non-https public origin, so the configured value has to say https.
- Chromium treats `localhost` as a **secure context regardless of scheme**, so `navigator.credentials`
  is available and the §3.1 authentication cookie — `CookieSecurePolicy.Always`, i.e. `Secure` — is
  accepted over plain HTTP. Neither is true of `http://192.168.x.x`, which is why the harness insists on
  the hostname rather than an address.
- Only the **host** is ever compared. `WebAuthnOriginPolicy` matches `localhost:{port}` exactly and
  separately treats loopback as trusted for either scheme; the §3.3 relying-party ID is
  `ServerDomain ?? Request.Host.Host` and `ServerDomain` is null by design (ADR-0005), so it comes out
  `localhost` at registration and `localhost` again at sign-in. A passkey registered in scenario 13's
  setup pass is therefore usable by its sign-in pass, on the same instance, same port.

`PublicOriginMiddleware` leaves the host alone here, because it already matches the configured origin —
which is the branch that says "keep it".

---

#### Scenario 14 needs no clock control, and that is the point

The obvious reading of "expired token URL; token from the previous window" is that it wants a
controllable server clock. It does not. §4.3's token is a pure function of `(join_secret, table_uuid,
window_index)`, and `JoinTokenService` is in the Domain, so the harness can compute a token for *any*
window from the secret it chose. Scenario 14 inserts a table with a known 32-byte secret, asks for
`window − 4` (inside the bounded lookback, so `Expired` rather than `Invalid`) and then `window − 1`.

The one real hazard is the boundary rolling over between computing a token and the server validating it,
which at the app's 60-second default would be a one-in-sixty flake. The harness therefore takes
`TABLE_JOIN_TOKEN_ROTATION_SECONDS` per instance and defaults it to **3600**. §4.3 accepts the current
and previous window whatever their width, so nothing the assertion depends on changes — the race simply
stops existing. Scenario 2, when it lands, will ask the same harness for a *short* window, because
crossing a boundary is the thing it is testing.

The table is inserted by direct SQL rather than through `/administration/tables/new`, also deliberately:
a token-window assertion that first has to sign in, pass a role check and submit a form can fail for six
reasons that have nothing to do with token windows. Scenario 2 *is* about that flow and will use the UI.

And the acceptance assertion is the redirect. An anonymous scanner presenting a valid token gets a grant
cookie and is sent to `/sign-in?ReturnUrl=/table/{id}` — §4.4 step (3). That redirect is what "accepted"
looks like from the guest's side, and it needs no account, no password, and no second page object.

---

#### Two things about driving the passkey UI that are not obvious

**Conditional mediation races the button.** `passkey.js` starts a `mediation: 'conditional'` request the
moment the sign-in page loads, and a virtual authenticator with `automaticPresenceSimulation` can satisfy
it with no gesture at all — at which point the form has already been submitted and there is no button
left to click. `SignInWithPasskeyAsync` therefore waits a few seconds to see whether the page leaves on
its own and only drives the button when it has not. Clicking during an in-flight conditional request is
safe: the element aborts its own pending request before starting a new one.

**The "left the sign-in page" predicate compares the path exactly**, not by prefix. `/sign-in/two-factor`
has to count as *left*, so that scenario 13 fails with "landed on /sign-in/two-factor" rather than with
an unexplained thirty-second timeout. The failure message is the whole reason that scenario exists.

Two smaller notes. `defaultBackupEligibility` and `defaultBackupState` arrived in Chromium's 13x line and
became **required** in the same change, so a single fixed argument list to
`WebAuthn.addVirtualAuthenticator` is wrong on one side of that boundary; the call is attempted with them
and retried without. They are not merely tolerated either — the §3.3 store persists `is_backup_eligible`
and `is_backed_up` and the assertion handler reads them back, so exercising credentials that carry the
bits set is the more faithful test. And the wizard's every step posts to `/setup` and redirects to
`/setup`, so the URL never changes and cannot be waited on; each step is identified by the element that
only exists on it.

---

#### Opt-in, and why that is not a cop-out

The scenarios skip unless `MYRESTAURANT_E2E` is set. The first run downloads roughly 150 MB of Chromium
into `~/.cache/ms-playwright`, and the rest of this suite is entirely offline once packages are restored;
a `dotnet test` that silently starts a 150 MB download is a `dotnet test` people stop trusting.

The switch is not where the coverage lives, though. CI's new **`end-to-end`** job sets it on every push,
and `scripts/ci_local.sh --with-e2e` sets it locally. It is its own job rather than part of
`build-and-test` for two reasons: `build-and-test` would otherwise pay for a browser on every run to
answer a different question, and a browser-driven flake should be attributable at a glance instead of
hiding inside a nine-hundred-fact summary. The job also runs
`playwright install --with-deps chromium` through the generated `playwright.ps1`, because `--with-deps`
needs apt and therefore root, which a test process has no business asking for.

Everything else that can be absent is also a skip with the fix in its message: no container engine, no
browser, no build output, no `MyRestaurant.slnx` above the test assembly. A missing tool is not a broken
product, and a suite that cannot tell the two apart is a suite nobody reads.

`ContainerEngineDiscovery` is duplicated from `MyRestaurant.DataAccess.Tests` rather than shared. A
`[ModuleInitializer]` runs once per *assembly* load and Testcontainers snapshots its environment into
static singletons on first touch, so a helper living in the other test project cannot run in this one's
process. The alternative was a fourth project in the solution for thirty lines that must not have a
public API.

---

#### Where to look if this breaks

1. **Everything skips with "opt-in".** Working as intended. `scripts/ci_local.sh --with-e2e`.
2. **"The built web application could not be found."** The message names the exact path it wanted. Build
   the solution in the same configuration you are testing in.
3. **"Chromium could not be started."** Almost always missing shared libraries on a minimal host. Run
   `pwsh tests/MyRestaurant.EndToEnd.Tests/bin/Debug/net10.0/playwright.ps1 install --with-deps chromium`
   once.
4. **"exited with code 1 before it became ready."** The captured output is in the message and it will be
   a `Configuration error:` line — `RestaurantOptions.Validate()` refusing something the harness set.
5. **A passkey step times out waiting for `p.totp-secret`.** The attestation failed. Check the captured
   output for the ceremony, and check that `js/passkey.js` was served — a wrong content root turns this
   into a silent no-op.
6. **Scenario 14's second navigation shows the expired page.** The token was computed for a window the
   server no longer accepts. Confirm the instance really got `TABLE_JOIN_TOKEN_ROTATION_SECONDS=3600`.

---

#### Build/test checklist for this slice

1. `dotnet restore` — two package references are added to the end-to-end project (`Npgsql`,
   `Testcontainers.PostgreSql`), both already pinned centrally, so no version resolution is new.
2. `dotnet build` — the new code is seven C# files in one test project. Nothing in `src/` changed, so a
   break here is in the harness, not the product.
3. `dotnet test` — **still 934 total, 919 passing, 15 skipped.** Three facts changed from
   `[Fact(Skip)]` to `[Fact]` and now skip at runtime instead of at discovery, which the summary counts
   the same way. If the total moved, something else moved with it.
4. `MYRESTAURANT_E2E=1 dotnet test tests/MyRestaurant.EndToEnd.Tests` — the real check. Expect
   **12 skipped, 3 passed**, and roughly a minute per scenario the first time (Chromium download aside).
5. `bash scripts/ci_local.sh` unchanged, then `bash scripts/ci_local.sh --with-e2e` once.
6. Push, and watch the new `end-to-end` job.

---

#### What is left in M6

The other twelve §16.3 scenarios, and the backup/restore drill as something executable rather than a
runbook prose section. The scenarios are now incremental work rather than a plumbing project: 2 wants a
short rotation window and a second context for the display device's principal, 3–11 want the guest
registration journey and two live circuits at once, 12 wants the obligations pipeline walked end to end.
### M6 Slice 3 — the display's rotating code, watched (landed)

Two §16.3 scenarios, chosen together because they are the same scenario asked twice:

- **2** — Admin creates table → pairing code → device pairs at `/display/pair` → `/display/{table}`
  shows a rotating QR that **changes across a window boundary**.
- **15** — Admin rotates a table's join secret → the in-flight token dies; the **display's next window
  works**.

Both are about the rotating code as a *screen* rather than as a URL, both need an administrator driving
real administration forms, and both need a second browser that is a tablet rather than a person. Scenario
14 already proved the token arithmetic from the guest's side; these two prove that the thing on the table
is showing it.

Nothing in `src/` is touched. No migration, no schema change, no ADR, no specification edit — §4.1, §4.2,
§4.3 and §11.5 already say all of this. One `Directory.Packages.props` *reference* is added (the version
was already pinned), and nothing is deleted.

---

#### The problem with "the QR changed"

§16.3 scenario 2 says the code must change across a window boundary, and the obvious test is to
screenshot-compare, or diff the SVG, and assert inequality. That assertion is close to worthless. A
display frozen on a stale code satisfies "changed" the moment anything else on the page moves; a display
signed by the wrong table's secret satisfies it perfectly; a display that has drifted three windows behind
satisfies it every time it drifts one more. All three are exactly the failures §11.5 exists to prevent —
its own comment says it out loud: *"a frozen QR looks exactly like a live one"*.

So what needed proving is not that the artefact changed but that **the artefact on screen is the code the
server would accept right now**. The display renders nothing else: no token text, no URL, just an inline
SVG. There are precisely two ways to get from that SVG back to a token — decode it, or recompute it — and
decoding means a rasteriser and a computer-vision dependency to answer a question about HMAC arithmetic.

`Harness/JoinQrCodes.cs` recomputes it. The secret comes out of the row, the token from the domain's own
`JoinTokenService`, the URL from its own `BuildJoinUrl`, and the module geometry from the same
`Net.Codecrete.QrCodeGenerator` call the renderer makes. Then `Classify` answers a *sentence*: the current
window's code, the previous window's code, a code N windows out of date, or one this table's join secret
does not produce. A failure therefore reads

```
Assert.Contains() Failure: Item not found in collection
Collection: ["the current window's code", "the previous window's code"]
Not found:  "a code 3 windows out of date"
```

rather than two thousand characters of SVG path against two thousand characters of SVG path.

**The duplication, named.** Three private facts about `TableJoinTokens.RenderJoinQrSvg` are restated in the
harness: error-correction level Medium, a four-module quiet zone, `ToGraphicsPath` as the source of the `d`
attribute. They should stay private — nothing in the product needs them, and widening their visibility to
satisfy a test is the worse trade. If any of the three moves, both scenarios fail immediately and say so,
which is the behaviour a duplicated constant is supposed to have.

---

#### Reading the join secret, which is the one rule bent on purpose

§4.1 is emphatic that the join secret never leaves the server: no page renders it, `ITableDirectory`
refuses to select it, `ITableJoinSecretReader` exists as a deliberately narrow keyhole for the token
service alone, and rotation replaces it without showing anyone either value.
`RestaurantInstance.ReadJoinSecretAsync` reads it straight out of the row.

That is not a hole in the rule; it is the reason the rule is testable. The harness is not the application
— it owns the database it created — and the only reason it needs the secret is to check the application's
arithmetic from outside. Unlike `ITableJoinSecretReader` it is **not** gated on `is_active`, because a
scenario about a deactivated table still needs to know what it would have signed with.

---

#### Why the tablet needs its own browser, and why that is not tidiness

`DisplayDeviceAuthenticationMiddleware` resolves the §4.2 device credential only when nothing has
already authenticated the request — *"a signed-in person always wins"*, so that a member of staff who
opens the display URL on a paired tablet is themselves rather than the screen. Pair a display inside the
administrator's browser and the surface resolves to `DisplayStage.NotPaired` and bounces to
`/display/pair`, for a reason that looks nothing whatsoever like the cause.

So `RestaurantInstance.OpenIsolatedPageAsync()` hands out additional contexts — own cookie jar, same
origin — and both scenarios use one for the tablet. Scenario 15 opens a third for the guest, because the
join flow writes a grant cookie and a browser that had been refused must not be carrying one when it is
later accepted. Contexts are closed in reverse on disposal.

No virtual authenticator is attached to them. The journeys that need one run on the instance's own page;
a guest who registers a passkey will want one on a context of their own, which is scenario 3's business.

---

#### Scenario 2, step by step

1. `/setup` — the only way an administrator exists, and it signs them in on the same response.
2. `/administration/tables/new` — the label is typed, and the table identifier is read back out of the
   success panel's *Manage this table* link. Deliberately: the identifier is minted server-side, so
   recovering it this way tests the surface instead of reimplementing it.
3. `/administration/tables/{table}/displays` — Generate, then the plaintext code is read off the panel.
   That surface renders it in place rather than through a redirect, because that response is the only
   moment the plaintext exists; only its SHA-256 hash is stored.
4. The tablet asks for `/display/{table}` while still unpaired, and §11.5's first rule sends it to the
   pairing surface — not to a sign-in page a tablet could never satisfy.
5. It pairs, and the table it lands on is read out of the redirect rather than assumed, so *"the code
   paired this device to that table"* is an assertion rather than a premise.
6. The QR on screen is the current or previous window's code for this table's secret.
7. Within one rotation it is a **different** code — and that one is live too.

Steps 6 and 7 sample the clock *after* reading the browser, never before. The server rendered at or
before the read, so the window sampled afterwards is the newest one the screen could possibly be showing;
accepting the previous window as well is §4.3's own tolerance, and it is what makes a boundary landing
mid-assertion a non-event instead of a flake.

---

#### Scenario 15, and what "works" has to mean

Same setup, then: read the secret, read the screen, compute the token a guest holding a freshly scanned
code would have, rotate, read the new secret.

- **The in-flight token dies** — a guest presenting it gets §4.4's friendly page, at HTTP 200, with no
  detail about which thing failed. This half runs *first*, before anything is accepted, so no grant cookie
  exists that could carry the browser past a refusal and quietly turn a failure into a pass.
- **The display's next window works** — the wait predicate is not "a different code" but "a code the
  **new** secret signs, live at this instant". The weaker predicate would also be satisfied by a display
  that had merely drifted onto some other window of the old secret, which is the opposite of the claim.
  Nobody touches the tablet and nothing re-pairs it; §4.1's "paired displays pick up the new one
  automatically" is the whole assertion.
- ...and then a guest presenting the new window's token is accepted, because a code no guest can use is
  not a code that works.

Rotation is a post/redirect/get, so `RotateJoinSecretAsync` waits for the confirmation flash before
returning — and matches its *text*, since a rename and an activation change flash through the same
element. Without that wait, a scenario could read the old secret back and then spend its remaining minute
failing to explain why.

---

#### The rotation window is a parameter, not a constant

Twenty seconds for these two; the harness's default of an hour stays for scenario 14. The scenarios want
opposite things from the same knob — 14 needs "the previous window" not to roll over mid-assertion, 2 and
15 need a boundary to actually arrive inside a test's patience — and §4.3 accepts the current and previous
window whatever their width, so nothing an assertion depends on moves with it. Waits are two rotations
plus twenty seconds: one window because the refresh fires at the *next* boundary and the wait may have
begun just after the last one, a second because a loaded container can lose one, and the slack because a
timeout that fires while the thing was about to happen is the worst kind of flake.

---

#### The `--with-all` gate that never ran

The last full local run ended at step 6 with

```
scripts/ci_local.sh: line 153: ./run.sh: Permission denied
```

`run.sh` has no execute bit in the working tree, and under `set -euo pipefail` that ends the run rather
than reporting a fixable detail — so the boot-smoke gate has been silently unreachable through
`ci_local.sh` since `--with-all` was added. Every `run.sh` invocation in the script now goes through
`bash`, which works either way, and the header says why. Worth doing on your side as well, so the
README's own `./run.sh` is true:

```bash
chmod +x run.sh && git update-index --chmod=+x run.sh
```

---

#### What I verified rather than guessed

- **Playwright 1.61.0** (`microsoft/playwright-dotnet` at `v1.61.0`): `ILocator.GetAttributeAsync(string,
  LocatorGetAttributeOptions?) → Task<string?>`, `ILocator.CountAsync() → Task<int>`,
  `ILocator.InnerTextAsync(LocatorInnerTextOptions?)`, and `LocatorWaitForOptions` carrying
  `WaitForSelectorState? State` alongside `float? Timeout` — the QR path is waited for as **attached**
  rather than visible, because the offline curtain §11.5 raises over a stale code sits on top of that very
  element and a scenario diagnosing a frozen display must still be able to read what it froze on.
- **`Net.Codecrete.QrCodeGenerator` 3.0.0**: `QrCode.EncodeText(string, QrCode.Ecc)` and
  `ToGraphicsPath(int)` — the same two calls `TableJoinTokens` already makes, so they are verified by
  code that compiles today rather than by a document.
- **The surfaces themselves**, selector by selector, against the Razor in the tree: `#label` and
  *Create table*; `p.pairing-code` and *Generate pairing code*; *Rotate join secret* and the
  `secret-rotated` flash text; `#pairing-code`, `#device-label` and *Pair this display*;
  `#table-display-surface svg.join-qr-svg path`. Every one of them is a selector a Razor edit could
  break, which is why they live in three journey files rather than scattered through the scenarios.
- **`scripts/ci_local.sh`** under `bash -n` and `shellcheck --severity=warning` *and* `--severity=style`:
  clean at both, as delivered. The `--help` path prints its header by scanning contiguous `#` lines, so
  the new paragraph is `#`-prefixed throughout — a bare blank line there would have truncated the help.

---

#### Build/test checklist for this slice

1. `dotnet restore` — one new package *reference* (`Net.Codecrete.QrCodeGenerator`), already pinned
   centrally, and already arriving transitively; no version resolution is new.
2. `dotnet build` — three new files and three edited ones, all in the end-to-end test project. Nothing in
   `src/` changed, so a break is in the harness rather than the product.
3. `dotnet test` — **still 934 total, 919 passing, 15 skipped.** Two facts moved from a discovery-time
   skip to a runtime one, which the summary counts identically. If the total moved, something else moved
   with it.
4. `MYRESTAURANT_E2E=1 dotnet test tests/MyRestaurant.EndToEnd.Tests` — the real check. Expect
   **5 passed, 10 skipped**. Scenarios 2 and 15 each spend up to a minute waiting for rotation
   boundaries on purpose, so this run is meaningfully longer than the last.
5. `bash scripts/ci_local.sh --with-all` — and this time watch step 6 actually run.
6. Push, and watch the `end-to-end` job.

---

#### If something fails

1. **`Pairing the display did not reach a table display surface.`** The refusal text is quoted into the
   message. §4.2 gives one deliberately vague sentence for every failure — expired, used, unknown, typo —
   so check the app's captured output for which it was, and check that the code was not consumed twice.
2. **`a code this table's join secret does not produce`** on the *first* read. Either the public origin
   the harness computed is not the one the app embedded (they are now derived from one variable, so this
   should be impossible), or `RenderJoinQrSvg` changed one of its three geometry facts.
3. **`a code N windows out of date`.** The refresh loop is behind rather than wrong. Look for a paused
   container or a stopped circuit; §11.5's curtain will also be up on screen.
4. **The rotation wait times out.** The display never picked up the new secret. Check that
   `RotateJoinSecretAsync`'s confirmation really appeared, and that the tablet's circuit is still alive —
   `LoadAsync` revalidates the device on every pass, so a revoked or dead device stops the loop rather
   than showing a stale code.
5. **The unpaired-display assertion fails with a sign-in heading.** The pairing context inherited an
   Identity cookie, which means it is not actually isolated.

---

#### What is left in M6

Ten §16.3 scenarios, and the backup/restore drill as something executable rather than a runbook prose
section. Scenario 3 is the next natural one and the last with any plumbing left in it: the guest
registration journey, and a virtual authenticator on a context that is not the administrator's. From
there, 4 through 11 are two live circuits and a shopping list, and 12 walks the obligations pipeline
end to end.
### M6 Slice 4 — the display refreshes (landed)

M6 Slice 3 delivered §16.3 scenarios **2** and **15**, and both failed on their first real run:

```
Display_PairsAndShowsRotatingQrAcrossWindowBoundary
  The table display did not show a join code different from the one it started on within 60s.
Admin_RotatesJoinSecret_InFlightTokenDiesNextWindowWorks
  The table display did not show a join code signed by the rotated secret within 60s.
```

Every assertion before the wait passed in both, including the one that the *first* code on screen is one
the server would accept. So the display rendered a correct, live code exactly once and then never again —
which is precisely the failure §11.5 exists to prevent, arriving as a test failure rather than as a
restaurant full of dead QR codes.

Two distinct causes, one in the harness and one in the product, and a third latent hazard found on the way
to them. All three are fixed here. `dotnet test` goes from 934 to **950** (sixteen new cases, still zero
failures); `MYRESTAURANT_E2E=1` goes from 3 passed / 2 failed / 10 skipped to **5 passed / 10 skipped**.

No migration, no schema change, no package change, no ADR edit, nothing deleted.

---

#### Cause one: the harness never had a Blazor circuit

`RestaurantInstance` boots the **build** output — `src/MyRestaurant.WebApplication/bin/<Config>/net10.0` —
with `ASPNETCORE_ENVIRONMENT=Production`. That pairing has a consequence nobody had reason to expect.

The framework's own JavaScript, `_framework/blazor.web.js` among it, is a **static web asset**.
`dotnet publish` copies those into `wwwroot/`; a plain `dotnet build` leaves them in the NuGet cache and
describes them in a build-time manifest, `MyRestaurant.WebApplication.staticwebassets.runtime.json`. That
manifest is loaded by `WebHost.ConfigureWebDefaults` (`dotnet/aspnetcore`, `release/10.0`):

```csharp
builder.ConfigureAppConfiguration((ctx, cb) =>
{
    if (ctx.HostingEnvironment.IsDevelopment())
    {
        StaticWebAssetsLoader.UseStaticWebAssets(ctx.HostingEnvironment, ctx.Configuration);
    }
});
```

**Only in Development.** `Program.cs` serves assets with `UseStaticFiles()`, so a build output run as
Production has neither the published copies nor the manifest: `GET /_framework/blazor.web.js` returns 404,
no circuit is ever established, and every interactive page in the application silently degrades to the
prerendered HTML it was born with.

Silently is the whole difficulty. Prerendering renders the *entire* surface server-side — the table label,
the party-size chip, and a genuinely current, genuinely valid join code. Nothing errors. Nothing looks
wrong. The page simply never changes again. The container is unaffected (it publishes) and so is `run.sh`
(it is Development), which is why five earlier scenarios passed: **every one of them is a static-SSR page.**
No end-to-end scenario had ever exercised an interactive surface, so this had no way to be noticed until a
scenario watched one for sixty seconds.

**Fixed in `Program.cs`**, not in the harness:

```csharp
if (!app.Environment.IsDevelopment())
{
    StaticWebAssetsLoader.UseStaticWebAssets(app.Environment, app.Configuration);
}
```

Chosen over flipping the harness to Development because "build output + `ASPNETCORE_ENVIRONMENT=Production`"
is a configuration a real operator will reach for when reproducing a production setting locally, and losing
all interactivity with no diagnostic is a trap worth closing for them too. It costs a deployment nothing:
`StaticWebAssetsLoader.ResolveManifest` returns `null` when the file is absent, and publish emits no runtime
manifest — so in the container this call finds nothing and does nothing, while `UseStaticFiles` serves the
published copies exactly as before.

---

#### Cause two: the refresh loop lost a race with itself

Fixing the harness is not enough, because the surface had a live bug of its own:

```csharp
protected override void OnAfterRender(bool firstRender)
{
    if (!firstRender || _stage is DisplayStage.NotPaired or DisplayStage.WrongTable)
    {
        return;
    }

    _subscription = Broadcaster.Subscribe(OnDomainNotification);
    _ = RunRefreshLoopAsync();
}
```

`ComponentBase.RunInitAndSetParametersAsync` calls `StateHasChanged()` the moment `OnInitializedAsync`
yields — and it yields on the first of **four** database round trips inside `LoadAsync`. That first render
therefore goes out while `_stage` is still its default `NotPaired`. The client's acknowledgement of it is a
single loopback WebSocket message, and it routinely beats four queries. When it does,
`OnAfterRender(firstRender: true)` runs, the guard rejects the stage, and `firstRender` is never true
again: **the refresh loop is never started at all.**

Intermittent by construction — it turns on which of a round trip and four queries finishes first — and
invisible when it happens, because the frozen code is a valid code. On a busy database it would be the
common case.

The fix separates the two questions the old guard was conflating. "Am I interactive?" is
`RendererInfo.IsInteractive`, which is the honest form of what `firstRender` was standing in for. "Have I
already started?" is a latch, `_liveWorkStarted`, so the starter is idempotent and can be called from
anywhere. It is now called at the end of `OnInitializedAsync` — which needs no render at all — with
`OnAfterRender` kept only as a net for future edits.

---

#### The latent hazard: a loop that could die once, permanently

`RunRefreshLoopAsync` caught `OperationCanceledException` and `ObjectDisposedException`. Anything else — a
dropped connection, a moment of database unavailability, neither of them the display's fault — escaped a
fire-and-forget task: unobserved, unlogged, and terminal. One bad second and a screen the restaurant is
trusting freezes for the rest of the day, identically to a healthy one.

It now absorbs unexpected exceptions, logs them at warning, and waits for the next boundary. That is not a
swallow: `_refreshSequence` stops advancing while the trouble lasts, so `js/display.js` raises the §11.5
offline curtain if it outlasts `data-fresh-for-ms`. Cancellation and disposal still end the loop, because
those mean the circuit is genuinely gone.

---

#### `DisplayRefreshSchedule` — the arithmetic, out of the Razor and under test

The two expressions that decide whether a display ever refreshes now live in
`src/MyRestaurant.WebApplication/Displays/DisplayRefreshSchedule.cs`, pure and clock-free, covered by
sixteen cases that run in milliseconds. Both were previously private members of a `.razor` file, reachable
only by a Playwright scenario willing to watch a real boundary go past.

Moving them turned up a third, smaller mistake. The old ceiling was one rotation flat:

```csharp
return delay > rotation ? rotation : delay;   // was
```

A code minted at the very start of its window reports a `NextRotationAt` one full rotation away and
legitimately wants `rotation + 250 ms`. Clamping that to `rotation` woke the loop 250 ms *before* the
boundary, re-rendered the window already on screen, and only reached the new code on the pass after — a
visibly late QR on every healthy display, every window. The ceiling is now `rotation + overshoot`; it
exists to stop a clock that jumped backwards from parking the loop for hours, not to second-guess an
ordinary full-window wait.

The tests pin the property rather than the arithmetic: *the wake-up lands in the window after the one on
screen*, expressed through the domain's own `JoinTokenService.CurrentWindowIndex`, and *`data-fresh-for-ms`
always outlasts the longest possible delay* — the invariant that keeps a working display from raising the
offline curtain once per window.

---

#### `data-live`, so this can never be a mystery again

The surface now publishes `data-live`, set from `RendererInfo.IsInteractive`: `"true"` only when a circuit
produced the markup, `"false"` from prerendering. `js/display.js` does not need it — the staleness curtain
already covers a circuit that dies *later*, because `data-refresh-token` stops changing. This covers the
case where it never lived, which is otherwise indistinguishable from health in every pixel on the glass.

Two things now consume it, and between them they replace a sixty-second timeout two steps from its cause
with a sentence at the moment of failure:

- `RestaurantInstance` probes `/_framework/blazor.web.js` during startup and refuses to hand back an
  instance that answered anything but 200, naming the static-web-assets cause in the message. One request
  per instance, for a failure class that is invisible by nature.
- `DisplayJourneys.WaitForLiveSurfaceAsync` waits for `[data-live='true']` before either scenario starts
  watching the QR, and reports the surface's actual attribute values when it does not arrive.

---

#### What this slice does not claim

The end-to-end suite still boots a build output rather than a published one, so it still does not prove the
container's exact asset layout. What it now proves is that the application is interactive in an environment
other than Development, which is the property the two failing scenarios were unknowingly asking for. Making
the harness publish is a bigger change and a slower loop; it is worth doing the day a scenario needs
something only publishing produces.
### M6 Slice 5 — guests can register (landed)

M6 Slice 4 closed with "scenario **3** is next and the last with plumbing left in it". That was wrong in an
instructive way. Scenario 3 needed no plumbing at all; it needed a **page that did not exist**.

`/register` was never built. The route list had thirty-six entries and none of them was a registration
surface, `SignIn.razor` said so in its own footnote — *"Guests register at the table by scanning its
display — there is no stand-alone registration page"* — and `TableJoin.razor` sent an anonymous
grant-holder to `/sign-in`, which for a first-time guest is a door with nothing behind it. The gap was
recorded twice in this document (M2's known limitations, and the M3 join slice: *"Guest self-registration
on the join path is still to come"*) and mandated in three places in the specification:

- **R§4.3** — "Guests self-register at the moment of joining a table: username, optional display name, and
  at least one credential — passkey offered first, password accepted."
- **S§4.4** — "…and continue to sign-in/**registration** if anonymous… Registration mid-flow: the grant
  cookie survives the passkey ceremony; **that is its purpose**."
- **S§11.1** — "Anonymous with valid token → grant → sign-in/registration (passkey-first, password
  offered) → join."

So this slice is a product slice that happens to unblock a test. Nothing in the specification changes; three
sentences of it become true. It also unblocks §16.3 scenarios **4** through **11**, every one of which needs
a guest with an account, and until now the only way to obtain a non-administrator account was for an
administrator to create one from `/administration/people/new` — which is a *staff* account with
`must_change_password` set, and not the thing those scenarios are about.

`dotnet test` goes from 950 to **969**; `MYRESTAURANT_E2E=1` from 5 passed / 10 skipped to **6 passed / 9
skipped**. No migration, no schema change, no package change, no ADR edit, nothing deleted.

---

#### The surface: `/register`, two steps over a protected cookie

Anonymous and static SSR, like every account surface, because it writes cookies on the response — the
accumulating registration ticket, and the authentication cookie once the account exists.

1. **Details** — username, optional display name, **optional** password. The password is hashed
   immediately, so the ticket carries an Argon2id PHC string and never a plaintext.
2. **Sign-in method** — the real WebAuthn attestation ceremony. Registering commits the account; *"Not
   now — use my password"* commits it on the password alone, **and is rendered only when a password was
   set**.

That asymmetry is §3.3's rule made structural rather than advisory. A passkey is "always offered, never
required, never a gate for guests" — so declining is offered exactly when there is something to fall back
on, and `RegistrationTicket.CanDeclineThePasskey` is the single predicate both the markup and the POST
handler consult. An account with neither credential is refused twice: the button is absent, and
`DapperGuestRegistration` throws before any SQL runs.

**Why a ticket at all, for a two-step form.** A WebAuthn attestation needs a user handle *before* the
account exists — the browser is told who the credential is for, and the authenticator stores it. So the
person's UUIDv7 is minted at step one and must equal the `person` row's eventual id, or a later
discoverable-credential sign-in would present a handle matching nobody. This is exactly the problem `/setup`
solves with `SetupTicket` (§3.6), and it is solved the same way: a Data-Protection-protected, 30-minute,
Secure/HttpOnly/SameSite=Lax cookie, with `/register/passkey/creation-options` anonymous and gated on it.

Unlike `SetupTicket` there is **no step enum**. The wizard has three ordered steps that must each be
unskippable; registration has one, and the ticket's existence *is* the state. A single-valued enum would be
ceremony pretending to be a state machine.

**No TOTP step and no role.** §3.4 pairs the authenticator with the password path for staff and
administrators; a guest is never required to hold one, and may enroll voluntarily later from
`/account/enroll-totp`. And a guest is the *absence* of a role grant (§3.7) — not a role named `guest` — so
nothing is written to `person_role` and no `role_granted` event is recorded.

---

#### The commit: `IGuestRegistration`, and why it is not on `IAccountAdministration`

One transaction: the `person` row, the `passkey_credential` row when there is one, and the
`security_event` rows — `account_created`, plus `passkey_registered` when a passkey was registered — with
a **NULL actor**, matching the shape `/setup` writes for its own self-actions (§3.6).

Every method on `IAccountAdministration` takes a `grantedBy` / `changedBy` / `resetBy` identifier and
records that administrator as the actor, because §3.7 is about one person acting on another's account. A
guest registering has no actor: the subject is doing it to themselves. Sharing the interface would mean a
required parameter this path has nothing to put in.

**No advisory lock**, unlike `IFirstAdministratorBootstrap`. That one serializes on
`pg_advisory_xact_lock` because its precondition is global ("zero administrators exist") and cannot be
expressed as a constraint. Registration has no such invariant. The only race is two people claiming one
username at once, and `person.username`'s UNIQUE constraint over `citext` decides it correctly,
case-insensitively, and for free; the loser gets `UsernameTaken` and nothing is written.

The integration tests pin the passkey-only shape hardest, because it is the one with somewhere to go wrong:
`person.password_hash` must accept NULL (§3.2 makes it nullable precisely so a passkey can stand alone), and
the account must still be findable and usable through `DapperUserStore` afterwards — which is the seam where
a column written differently from the store's expectation would surface. For a passkey-only guest that store
is the only way in, so `RegisterAsync_ProducesAnAccountTheIdentityStoreCanSignIn` asserts the round trip
rather than trusting it.

---

#### §16.3 scenario 3, and the parenthetical nobody had tested

The scenario's full text is *"Guest scans (simulated URL from current token) → registers with passkey
**(slowly — grant outlives token)** → joins; sitting created."* The parenthetical is the interesting half,
and it is the entire reason §4.4's join grant exists. It had never been tested, because until this slice
there was no registration to be slow at.

The instance runs at `TABLE_JOIN_TOKEN_ROTATION_SECONDS = 10` — §13's floor, and the only scenario that
wants it. §4.3 accepts the current *and* previous window, so a scanned token is provably dead about twenty
seconds later; at the harness default of an hour there would be nothing to outlive and the assertion would
be vacuous. The scenario then:

- scans a live token in the guest's **own** browser context, with its **own** virtual authenticator (a
  WebAuthn credential belongs to the authenticator that minted it, so a passkey created on the shared
  administrator context would be one this guest could never use), and asserts §4.4 sent them to sign in;
- follows the **"Create an account"** link rather than navigating to `/register` — that link carrying the
  return URL is the whole mechanism by which registering lands the guest back at the table, and a scenario
  that typed the URL would be asserting on a path no guest can take;
- registers with a passkey and **no password**;
- asserts the URL it returns to carries **no token at all**, and shows the join confirmation;
- waits until the scanned token is past §4.3's acceptance window, measured from the instant it was minted
  rather than from now, and proves it is dead by re-scanning it in a **third** context — one with no grant
  cookie that could carry the navigation past a refusal and quietly turn a failure into a pass;
- joins anyway, and checks the database for exactly one open sitting on that table with exactly this guest
  on its roster.

That last check is not redundant with the page. §5.1's "sitting created" is a claim about rows, and a second
sitting that the `table_sitting_one_open_per_table` unique index should have prevented would look identical
from a seat.

---

#### Harness changes

- `RestaurantInstance.OpenIsolatedPageAsync(withVirtualAuthenticator: true)` — an isolated context that can
  hold credentials. Default remains off: a display device has no credentials beyond the §4.2 cookie, and
  CDP setup for every tablet would be waste.
- `RestaurantInstance.ReadOpenSittingAsync` — the open sitting and its roster, by username.
- `Harness/TableJourneys.cs` (new) — `ScanAsync`, `JoinAsync`, and `JoinStageOnScreen`, which names which of
  §4.4's four outcomes is on screen so a failure reads "it offered the join button" rather than quoting a
  heading.
- `AccountJourneys.RegisterGuestWithPasskeyAsync` and the `GuestAccount` record.

`ScanAsync` navigates to a **relative** path, and the reason is worth recording: the absolute URL a display
encodes comes from `RESTAURANT_PUBLIC_ORIGIN`, which the harness deliberately sets to
`https://localhost:{port}` while Kestrel serves plain HTTP there — the mismatch is what lets §13's https
requirement and Chromium's localhost-as-secure-context rule both hold. Navigating to that absolute URL
would reach nothing. Scenario 2 separately asserts that a real screen encodes exactly the code the table's
secret produces, so between the two nothing is assumed.

---

#### Known gap carried forward: `/register` is not rate-limited

`/display/pair` is the only rate-limited endpoint in the application (§4.2: five attempts per minute per IP),
and `/register` joins it as a second anonymous surface that writes a row — the more consequential of the two,
since a `person` row outlives the request.

It is not limited here, deliberately and not for free. The limiter is configured by
`AddRestaurantDisplays`, and `RateLimiterOptions.OnRejected` plus `RejectionStatusCode` are single-valued:
a second `AddRateLimiter` call registering a registration policy would silently take over the rejection
handler, and a refused registration would answer with the display's *"Too many pairing attempts from this
device"* — which is worse than no limit, because it is wrong and looks deliberate. Doing it properly means
`OnRejected` learning to dispatch on the endpoint, which is an edit to a file this slice otherwise does not
touch and a policy §13 does not specify.

What stands in the meantime: registration is a **two-request** flow behind an antiforgery token and a
Data-Protection ticket cookie, so it is not a scriptable single POST; the password is capped at 256
characters so an anonymous caller cannot ask for unbounded Argon2id work; and §3.2's hashing semaphore
bounds concurrent hashes process-wide regardless. This is a named follow-up, not an oversight.

---

#### What this slice does not do

- **It does not change `TableJoin.razor`.** An anonymous grant-holder still goes to `/sign-in`, which now
  offers registration alongside it. §4.4 says "sign-in/registration" and a returning guest — the common case
  after the first meal — wants sign-in first. Sending everyone to `/register` and offering sign-in from
  there would be the same page count with the wrong default.
- **It adds no header link.** Registration belongs to the join flow and to the sign-in page, per §4.3's
  "at the moment of joining a table". `MainLayout`'s session nav already wrapped onto three rows at 375 px
  the last time it held four items.
- **It reuses `.setup-steps` / `.setup-step` for the two-step indicator** rather than adding CSS. The rule
  is a generic numbered-step list with no setup-specific styling; the class name is now slightly wider than
  its origin, which is cheaper than a duplicate rule.
- **It offers no post-registration passkey nudge for the password path.** §3.3 wants a dismissible nudge
  "after registration and after sign-in". A guest who takes the password branch has just declined a passkey
  on the previous screen, so nudging them on the next one would be asking twice; the durable home is the
  `/account/passkeys` page, which already exists and is linked from the profile. The *sign-in* nudge remains
  outstanding, as recorded in M2's limitations.
### M6 Slice 6 — the kitchen hears the guest (landed)

Two more of §16.3's fifteen: **4** (a guest stages two adds and a note, presses Send, and the kitchen
gets exactly one alert with both lines pending) and **6** (the kitchen marks one line away and the
guest's own screen re-badges it). They are the first scenarios in which a commit made by one browser has
to be observed by a *second live circuit in another browser context* — everything before this watched a
timer, a redirect, or a row.

`dotnet test` stays at **971 total / 0 failed** (956 succeeded, 15 skipped) because no unit test was
added; `MYRESTAURANT_E2E=1` goes from **6 passed / 9 skipped** to **8 passed / 7 skipped**. No migration,
no schema change, no package change, no ADR edit, no `Program.cs` edit, nothing deleted.

---

#### The build was red, and the reason is worth writing down

Slice 5 shipped `Harness/TableJourneys.cs` with this in it:

```csharp
string.Create(
    CultureInfo.InvariantCulture,
    $"Joining did not confirm; the table page is now showing {stage}. A grant is"
    + " single-use and is cleared whatever the outcome (§4.4), so if this was a"
    + " refusal the grant is already spent and a retry will not help."),
```

`error CS1620: Argument 2 must be passed with the 'ref' keyword`. The overload being bound is
`string.Create(IFormatProvider?, ref DefaultInterpolatedStringHandler)`, and C# converts an addition to a
handler only when the additive expression is composed **entirely** of interpolated strings. Roslyn's
`Binder_Operators.cs` says so literally:

```csharp
&& left  is BoundUnconvertedInterpolatedString or BoundBinaryOperator { IsUnconvertedInterpolatedStringAddition: true }
&& right is BoundUnconvertedInterpolatedString or BoundBinaryOperator { IsUnconvertedInterpolatedStringAddition: true }
```

A bare `" single-use…"` binds as a `BoundLiteral`, the whole expression collapses to a plain `string`, and
the call no longer matches an overload it can bind by value. Prefixing every continuation with `$` fixes
it, and a hole-less `$"…"` still qualifies — `BindInterpolatedString` returns a
`BoundUnconvertedInterpolatedString` carrying a constant, not a literal node. The rule is now written into
the file as a comment beside the call, because this is the second time it has been reintroduced.

The same review found a quieter one three files over. `DisplayJourneys.WaitForLiveSurfaceAsync` wrapped its
diagnostic in a **raw** string literal with trailing backslashes for line continuation — and raw string
literals process no escape sequences at all, so every one of those backslashes and every newline it was
meant to hide was being printed into the message. It compiled and no test failed on it; it just produced a
mangled sentence at the exact moment somebody most needed to read one. Both are now concatenated
interpolated strings, which is the shape the rest of the harness already uses.

---

#### One new attribute on two surfaces: `data-live`

`TableDisplay.razor` has published `data-live` since Slice 4, for a reason that turned out not to be
specific to displays: **a surface that never became interactive is indistinguishable from a live one**.
Prerendering produces the whole document server-side, so the page looks completely correct and then never
changes again.

That hazard is worse, not better, on the other two live surfaces:

- **The ordering island.** Every control on it is a click handler. On a dead island "Add to basket" lands
  on nothing, and the first thing anybody learns is that the basket stayed empty — thirty seconds later,
  with no mention of circuits. `TableOrderSurface.razor` now renders its whole tree inside
  `<div class="order-surface" id="table-order-surface" data-live="…">`. That wrapper is also what
  disambiguates the island's own `p.status-success` from the parent page's *"You have joined Table Four"*,
  which is the same element on the same document and is on screen from the moment a join redirects back.
- **The kitchen board.** A prerendered board lists what was outstanding when the page was requested, in the
  right order, with the right waiting times, and then never alerts. A kitchen that has genuinely had no
  orders for ten minutes looks exactly the same. This is §10's worst failure and the one nobody notices
  while it is happening. `KitchenBoard.razor` already had `id="kitchen-board-surface"` for `js/kitchen.js`;
  it gains `data-live` on the same element.

The board also gains **`data-unseen-alerts`**, the §10.3 count as a number. The badge says it in English
already, but that string carries pluralisation and an optional *"(n overdue)"* parenthetical, and the count
is the one piece of state on that screen that exists **only in circuit memory** — everything else can be
re-derived from the database. Publishing it means an operator can read it in dev tools and a scenario can
assert on it without parsing prose to learn a fact the component already knows.

No CSS changed. Nothing in `app.css` reaches into the ordering surface with a child combinator, so the
wrapper is inert; `.order-totals > div` is internal to the tree it wraps.

---

#### Scenario 4 — "one loud alert" is a claim about the number one

The scenario stages 1 × *Soup of the day* with the note *"No onions, extra hot"*, then 2 × *Steak pie*, and
sends. Three things are asserted in an order that matters.

**Before the send, the board is live and showing nothing.** That is what turns §11.1's *"nothing reaches
the kitchen until you press Send"* from documentation into a test: a surface that wrote as it staged would
have alerted already, and the board was subscribed and watching.

**After the send, one predicate over both facts.** §9 publishes `OrderLinesChanged` **before**
`KitchenAlert`, and `KitchenBoard.OnDomainNotification` handles each as it arrives — so there is a real
window in which the queue has re-read and the alert has not yet been counted. A scenario that waited for
two lines and *then* read the count would sample that window and report a silent kitchen. So the wait is
`PendingLines.Count == 2 && UnseenAlertCount >= 1`, and the equality (`== 1`) is asserted on the snapshot
that satisfied it — then re-read once at the end, after the guest surface has finished reacting, which
turns "one alert so far" into "one alert, full stop".

**One alert, not two.** Two adds in one send is one `order_event`, therefore one `kitchen_notification`
row, therefore one `KitchenAlert` (§10.1). A count of two would mean the alert had gone per-line, which is
how a busy service becomes a siren nobody can hear over. The whole of `AfterCommit`'s
`if (result.KitchenNotificationWritten)` is what this asserts.

`Sound is not armed and not asserted.` §10.3's arm control has to run inside a real user gesture to unlock
browser audio, and what it unlocks is an `AudioContext` on a headless browser with no output device — "did
it beep" is a question about Chromium's audio stack, not about this application. §10.3 itself names the
visual badge as the fallback whenever sound is not working and makes the unseen count the record of what
arrived; that count is the number the sound is played *from*, so asserting on it asserts on the alert.

---

#### Scenario 6 — the badge crosses a browser boundary

One tap on one line (§11.2's *"tap a line → one fulfillment event"*, not *fulfill all*, because the half
worth getting wrong is the **other** line staying where it was), and then nobody touches the guest's phone.
§9 sends `LineFulfillmentChanged` to the sitting's members, the ordering island re-reads, and the chip goes
from *With the kitchen* to *At your table*.

The badge is read from the chip's **class** rather than its words: `chip-ok` is the state, "At your table"
is copy, and a scenario that matched the copy would fail on a wording change and pass on a styling bug. The
pass losing the line is asserted too — `kitchen_pending_line` excludes a fulfilled line (§8.3), so that is
the same fact from the writing side rather than a second opinion.

---

#### The arrangement both scenarios share

`ArrangeServiceAsync` stands up: an administrator (the §3.6 wizard), two menu items, a table, a guest who
scans, self-registers with a passkey and joins, a live ordering island, and a live kitchen board. Two
decisions in it are worth recording.

**The kitchen board is the administrator's own browser.** §3.7 admits both `kitchen` and `administrator` to
`/kitchen`, and an administrator covering the pass is a case the application deliberately supports —
`KitchenBoard.razor` reads the actor role from the principal precisely so that one is not recorded as the
other. Creating a separate kitchen account would mean `/administration/people/new`, a forced password
change (§3.2), and a second sign-in, none of which either scenario asserts on. It is also not free to
avoid: the administrator has TOTP from the wizard, so a *password* sign-in in a fresh context would hit the
§3.5 challenge, and a *passkey* sign-in would need the credential, which belongs to the authenticator on the
administrator's own context. Scenarios that are about a staff account's own journey will create one.

**The board is opened before anything is sent.** An alert is a §9 broadcast to subscribers, and
`KitchenBoard.razor` subscribes in `OnAfterRender(firstRender)` — which only runs on a circuit. A board
opened after the send would show the queue perfectly well and would have heard nothing.

The join secret is read out of the row rather than decoded off a paired display. These scenarios are about
what happens after the guest sits down, and pairing a tablet to obtain a QR would put the whole of scenario
2's apparatus in front of them; scenario 2 already proves that a real screen encodes exactly the code the
table's secret produces, so nothing is assumed by computing one here.

---

#### Harness changes

- `Harness/TableOrderJourneys.cs` (new) — `WaitForLiveSurfaceAsync`, `StageAsync`, `SendAsync`,
  `BasketLineCountAsync`, `ReadCommittedLinesAsync`, `WaitForCommittedLinesAsync`, and the
  `GuestOrderLine` / `GuestLineBadge` vocabulary. Every selector is scoped to `#table-order-surface`.
- `Harness/KitchenJourneys.cs` (new) — `OpenAsync`, `WaitForLiveBoardAsync`, `ReadBoardAsync`,
  `WaitForBoardAsync`, `FulfillLineAsync`, and the `KitchenBoardSnapshot` / `KitchenBoardLine` vocabulary.
- `AdministrationJourneys.CreateMenuItemAsync` and the `MenuItemOnTheMenu` record.

Two small habits worth keeping. **Items are selected by identifier, not by label**: the picker's label is
the name, the formatted price, and possibly §7's *"(currently unavailable)"*, so matching it would make a
scenario fail for a currency setting. **A line is found by reading the queue and comparing names in C#**,
not by interpolating a name into a `:text-is('…')` selector — menu items are free text, and an apostrophe
in *"Chef's soup"* is not something a scenario should have to know about.

---

#### What this slice does not do

- **It does not arm the alert sound.** Recorded above; the badge count is the assertion §10.3 itself names.
- **It does not assert the §10.2 reminder.** That is scenario 8, and it wants a short
  `KITCHEN_SUBMISSION_REMINDER_SECONDS` rather than sixty seconds of waiting — a harness change, not a
  product one.
- **It does not touch `TableJoin.razor`.** The island's wrapper lives inside the component, so the static
  parent is unchanged.
- **It leaves scenario 5 for its own slice.** Two guests with two live circuits watching each other's
  roster and totals is a different axis from this one, and folding it in here would have made a single
  arrangement serve three scenarios badly.
### M6 Slice 7 — documentation catches up, and one dependency moves (landed)

Not a feature slice. Two slices of product landed while the documentation stood still, one package went a
minor version behind, and an analyzer started failing the CI build. This closes all three, and touches no
behaviour whatsoever — no `.cs`, no `.razor`, no SQL, no migration, no wiring.

`dotnet test` counts are unchanged at **971 total / 956 succeeded / 15 skipped**, and
`MYRESTAURANT_E2E=1` unchanged at **8 passed / 7 skipped**, because nothing here is executable.

---

#### Why the documentation had drifted two slices

`README.md` still said *"five of the fifteen §16.3 scenarios"* and *"all ~934 facts"*. Both were true on
the day they were written and neither had been true since. That is the ordinary failure mode of a status
sentence living in a document nobody edits when the status changes, and the fix is not a process — it is
noticing, which is what this slice is.

More consequentially, **M6 Slice 5 shipped a product surface without a ledger row.** It built `/register`,
argued in its own change notes that no specification edit was required, and was right about the narrow
question it asked — R§4.3, S§4.4 and S§11.1 all already mandated guest registration, so nothing they said
became false. But the atomic-documentation rule (R§10 · S§18) is not only about contradictions. A behaviour
change lands with its ledger row, and a new anonymous route that writes a `person` row is a behaviour
change by any reading.

So **F-37** is entered now, late, and says so. Its content is the interesting part rather than its
existence: it is the *same shape* as F-35, the profile page. A requirement stated plainly in
`REQUIREMENTS.md`, a mechanism described in the specification, and no line in §19's build order claiming
it — so every milestone review passed, because every milestone had done what its own list said. Twice is a
pattern. The ledger's closing note now names it, along with the cheap guard: when a requirement section
names a capability, §19 should name the milestone that owns it. Both gaps were found the same way in the
end — by somebody trying to *use* the thing, not by anybody re-reading the documents.

The specification gains **§11.8 `/register`**, appended rather than inserted. §11.7 (the wall clock) keeps
its number because a dozen source files cite it in comments, and renumbering to put registration next to
§11.6 `/account` — where it belongs thematically — would have silently falsified every one of them. Section
numbers are an interface once code quotes them.

Also new: **S§17** now carries the missing rate limit as a named accepted risk rather than a note in a
change file nobody will find again. The reason it is not a two-line addition is written down — the limiter
lives inside `AddRestaurantDisplays`, `RateLimiterOptions.OnRejected` and `RejectionStatusCode` are
single-valued, and a second `AddRateLimiter` policy would silently take over the rejection handler so that
a refused registration answers with §4.2's *"Too many pairing attempts from this device"*. Wrong, and
deliberate-looking, which is worse than absent. Four things bound it meanwhile, and none of them is a
policy; all four are recorded.

Specification version bumps **1.0 → 1.1** with a dated changelog, per its own §18.

---

#### The package

`Net.Codecrete.QrCodeGenerator` **3.0.0 → 3.1.0**, the only outdated reference in the tree.

The release is additive: balanced sizing for *structured append*, which is the feature that splits one long
text across several linked QR codes. Nothing here does that — every code this application renders encodes
one short URL. The three members it actually uses (`QrCode.EncodeText(string, Ecc)`, `QrCode.Size`,
`QrCode.ToGraphicsPath(int)`) carry identical signatures at the v3.1.0 tag, checked against the source
rather than inferred from semver.

One consequence worth stating because central package management hides it: this package is referenced by
**both** the web application and the end-to-end test project, so one bump moves both. That is not
incidental. `JoinQrCodes` asserts that a paired display is showing the code the table's secret produces by
recomputing the expected path with this same library, and that assertion is only real while both sides
encode identically. A version skew between them would turn scenario 2 into a test of the library.

No golden module path is pinned anywhere — the QR assertions are structural (`starts with <svg`, `has a
viewBox`, `path begins with a moveto`, `makes no external references`) — so the modules may legitimately
change shape without anything going red.

`dotnet list package --outdated` cannot see `NSubstitute`, because no project references it; it is a
version pin standing ready for §16.1's *"NSubstitute acceptable"*, not a dependency. Checked by hand: 6.0.0
is current. The comment in `Directory.Packages.props` now says so, so the next refresh does not have to
rediscover why one entry is invisible to the tool.

---

#### The analyzer

Four `xUnit2031` findings in `EndToEndScenarios.cs` — `Assert.Single(xs.Where(p))` where
`Assert.Single(xs, p)` is meant. Warnings on a workstation, **errors in CI**, since
`TreatWarningsAsErrors` is conditioned on `ContinuousIntegrationBuild`; the local build was green and the
strict build was not, which is exactly the split `Directory.Build.props` documents and exactly why
`scripts/ci_local.sh` exists.

Worth fixing rather than suppressing, and the reason is diagnostic rather than stylistic. `Assert.Single`'s
failure message prints the collection it was given. With `.Where(…)` in front, the collection it was given
is the *filtered* one — so a scenario that expected one soup line and found none reports an empty
collection, which is a restatement of the failure rather than information about it. The predicate overload
(`public static T Single<T>(IEnumerable<T>, Predicate<T>)`) prints every line that *was* there, which for
an end-to-end failure is most of the diagnosis.

The rewrite was applied mechanically rather than by hand, by a parser that only touches a call whose entire
argument is `<receiver>.Where(<lambda>)` — leaving `Assert.Single(xs)`, `Assert.Single(xs, p)`,
`Assert.Single(xs.Where(p).ToList())` and any occurrence inside a comment or a string literal alone. Run
across all 300-odd `.cs` files in the tree it changes nothing outside the four intended sites, and running
it twice changes nothing at all.

---

#### Carried forward, unchanged

The `docs/_append/` backlog is the one piece of housekeeping this slice does *not* fix, because merging it
is a `cat` the owner runs, not a file this archive can safely contain: `docs/BUILD_PROGRESS.md` is far too
large to regenerate, and shipping a partial one would overwrite the whole document.
### M6 Slice 8 — three scenarios that had never once passed (landed)

Slice 5 recorded `MYRESTAURANT_E2E=1` going to **6 passed / 9 skipped**. Slice 6 recorded **8 passed / 7
skipped**. Neither was ever observed. The first honest run of the suite reports **5 passed / 7 skipped / 3
failed**, and the three failures are §16.3 scenarios **3**, **4** and **6** — every scenario that registers
a guest, all stopping on the same line of the same harness method with the same message:

```
System.TimeoutException : Timeout 30000ms exceeded.
Call log:
  - waiting for Locator("button[name='__passkeySubmit']") to be visible
  at AccountJourneys.RegisterGuestWithPasskeyAsync … AccountJourneys.cs:line 149
```

Nothing in the application is wrong. `/register` works exactly as §4.3 specifies, and a real guest has
never been able to hit this. The harness was reading the address bar and believing it.

---

#### The URL changes before the page does

`RegisterGuestWithPasskeyAsync` is the only journey in the harness that navigates by **clicking a link in
the application** rather than by `page.GotoAsync`. That is deliberate and stays: §4.4's whole mechanism for
returning a guest to the table they scanned is the return URL riding on the sign-in page's *"Create an
account"* link, and a scenario that typed `/register` itself would be asserting on a path no guest can
take. But it is also what exposed the journey to a Blazor behaviour nothing else in the suite touches.

With `blazor.web.js` loaded and no interactive `Router` on the page — which is every static-SSR surface
here, so every account page, the join page and the display — an in-app link click is intercepted by
*enhanced navigation*. `NavigationEnhancement.ts`, `onDocumentClick`:

```ts
history.pushState(null, /* ignored title */ '', absoluteInternalHref);
...
performEnhancedPageLoad(absoluteInternalHref, /* interceptedLink */ true);
```

The URL is pushed **first**; the `fetch` and the `synchronizeDomContent` that patches the new markup in
happen after. And Playwright resolves `WaitForURLAsync` on a same-document navigation the moment the URL
matches — there is no `load` event coming, so there is nothing else it could wait for.

So the journey ran like this:

1. Click *Create an account*. `pushState` fires. `WaitForURLAsync(IsRegistrationUrl)` returns **while the
   sign-in document is still on screen**.
2. `FillAsync("#username", "e2e.guest")` succeeds instantly — because **`/sign-in` has a `#username` too**.
3. `FillAsync("#display-name", …)` has to wait; that field exists only on `/register`. While it waits, the
   fetch lands and `synchronizeDomContent` runs. `DomSync.ts`'s `ensureEditableValueSynchronized` assigns
   every input the value the server rendered, and the fresh registration markup carries `value=""`. **The
   username is erased.**
4. Continue posts with an empty username. `[Required]` fails, `OnValidSubmit` never fires, and the details
   step re-renders with *"Choose a username."*
5. There is no credential step, so there is no `__passkeySubmit`, so thirty seconds later the scenario
   times out on an element three states away from the problem.

It failed on every run rather than intermittently because the fill takes about two milliseconds and the
fetch about twenty; step 2 always won.

Form posts are unaffected, and that is worth stating rather than assuming: `enhancedNavigationIsEnabledForForm`
requires `data-enhance` on the form element itself and nothing in this application sets it, so the passkey
step, the join POST and every administration form are ordinary browser navigations where Playwright's waits
mean what they say. Only link clicks come through here — which is why `CompleteSetupAsync`, which does the
same four-step cookie dance over the same kind of surface, has worked since Slice 2.

---

#### `Harness/EnhancedNavigation.cs`

One method: click the link, then wait for **an element the destination has and the current page does
not**. `synchronizeDomContent` is a single synchronous call on the main thread and a Playwright query
cannot interleave with it, so the instant any part of the new markup is observable, all of it is —
including the reset of every field the two surfaces share. That makes the wait an exact barrier rather
than a hopeful delay.

It is a file of its own rather than four lines inlined into the registration journey because the hazard is
general and the next scenario to meet it is already named: §16.3 scenario **11** has an administrator
filtering the hidden-records view, which is a link click on a static-SSR page with a form on the other
side of it.

The registration journey uses `#display-name` as its barrier — a field, not the heading, because copy
changes and a barrier that breaks on a reworded sentence is a barrier somebody deletes. The URL is still
checked, but *after* arrival, where it is a fact rather than an intention.

---

#### The guard, and why a second one was worth two round trips

`AssertFieldHoldsAsync` reads both fields back with `InputValueAsync` immediately before the form goes,
and refuses to submit if either has lost what was typed into it.

That is not belt-and-braces around the fix above. It is the answer to the shape of this bug: a value reset
by a DOM patch produces a completely ordinary validation refusal on the next screen, and the scenario then
times out waiting for the screen after *that*. The distance between the cause and the symptom is what cost
the time here, and it is a distance any future self-patching surface can reintroduce. Two round trips buy a
message naming the field, what it holds and what it should have held.

The wait for `__passkeySubmit` also gained the diagnostic it never had. `DescribeRefusalAsync` became
`DescribeSurfaceAsync` and now reports the heading and **every** `p.status-error` and `.validation-message`
on the page rather than the first — a details step can refuse in more than one field at once, and
*"Choose a username."* on its own would have explained all three scenarios the day they were written.

---

#### What this does not change

- **No product file.** Not one `.cs` or `.razor` under `src/`. `/register` is correct: a guest clicks the
  link, watches the page arrive, and then types. Disabling enhanced navigation on the account surfaces to
  make a test pass would have been fixing the wrong thing.
- **No new scenario.** Scenario 5 was next on the list and is deliberately held back one slice. Two
  consecutive slices shipped scenarios reported as passing that had never run, and stacking a third on top
  of an unverified harness fix is how that happens a third time.
- `dotnet test` stays at **971 total / 0 failed** (956 succeeded, 15 skipped) — nothing outside the
  end-to-end project moved.

Expected after this: `MYRESTAURANT_E2E=1` reports **8 passed / 7 skipped**, which is what Slice 6 claimed
and this is the first slice entitled to.

### M6 Slice 9 — two guests at one table (landed)

§16.3 scenario **5**: *"Second guest joins via fresh token → sees first guest's order live; first guest sees
roster update."* It is the first scenario with two guests in the restaurant at once, and the only one so far
where **every** interesting event is raised by a browser other than the one being asserted on. Scenario 6
already watched a fulfillment cross from the kitchen's circuit to a guest's; this watches a join and a send
cross from one guest's circuit to another's, in the opposite direction, twice.

`MYRESTAURANT_E2E=1` goes from **8 passed / 7 skipped** to **9 passed / 6 skipped**. `dotnet test` stays at
**971 total / 0 failed** — no unit or integration test moved, and the scenario is opt-in like every other.

---

#### What the scenario does

An administrator bootstraps, puts soup and a steak pie on the menu, and creates a table. Then:

1. **The first guest** scans, self-registers with a passkey, joins, and sends one soup. Their roster is
   asserted to hold exactly one person wearing the *"you"* chip, and *"the rest of the table"* to be empty.
2. **The second guest** scans the code the table is showing now — their own browser context, their own
   virtual authenticator, their own account — registers, and joins.
3. **The first guest's roster grows to two, with nobody touching their phone.** `TableJoin.razor` publishes
   `SittingMemberJoined` after the membership row commits (§9: *"fired on: membership insert"*), the first
   guest's circuit re-reads, and the second guest appears without the *"you"* chip.
4. **The first guest's party list stays empty**, which is the assertion that makes §5.2's *"who is here"*
   and §11.1's *"the rest of the table"* two different lists rather than two renderings of one. §6.1 creates
   the `guest_order` row lazily inside a first send and `sitting_bill` is grouped from those rows, so a guest
   who has joined and ordered nothing is on the roster and nowhere near the bill.
5. **The second guest sees the first guest's soup on arrival.** That half comes from the read model, not
   from a notification — their circuit started after the send — and would still hold with §9 switched off.
   It is asserted separately for exactly that reason.
6. **The first guest sends a steak pie ×2, and the second guest's screen grows a line.** No reload, no
   click, no navigation on the second browser: `OrderLinesChanged` to the sitting's members, and the surface
   re-reads. The quantity is asserted as well as the name, because a party list that showed the line and
   lost the number would be a bill nobody could read.
7. **One open sitting, two members, in join order** — read from the rows. From a seat, *"both joined the
   sitting"* and *"a second sitting was opened on the same table and the unique index did not stop it"* look
   identical, and only `table_sitting` plus `table_sitting_member` say which happened.

---

#### "Fresh token" at an hour-long window

§16.3's word is *fresh*, and this scenario deliberately runs at the harness default of 3600 s rather than at
§13's ten-second floor. At an hour the second guest's token is very often the same string as the first
guest's, and that is the right reading: *fresh* means **the code the table is showing at the moment they
scan**, not a code the first guest's has aged out of. Scenario 3 already owns the expiry half — it is built
on the ten-second floor precisely so that the token it scanned is provably dead before the guest joins — and
duplicating that here would only add a clock to race against two registrations and four form posts.

---

#### The one product change: naming a span

`TableOrderSurface.razor`, §11.1's *"the rest of the table"*, rendered another guest's line as:

```razor
<span>@theirLine.Quantity × @theirLine.MenuItemName</span>
```

It is now `<span class="order-party-line-name">`. That was the only text on the ordering surface a reader
had no way to address — the guest's own lines carry `.order-line-name`, the kitchen's carry
`.kitchen-line-name`, and this one was a gap rather than a decision. A **distinct** class rather than
reusing `.order-line-name`, because that one is `font-weight: 600` and the rest of the table is deliberately
quieter than your own order; the new name has no rule behind it and changes nothing on screen.

Nothing else under `src/` moved. No migration, no schema change, no package, no ADR, no `Program.cs`, no
`.slnx`.

---

#### Harness: three new reads, and why each has a wait

`Harness/TableOrderJourneys.cs` gains `ReadRosterAsync` / `WaitForRosterAsync`, `ReadPartyAsync` /
`WaitForPartyAsync`, two records (`TableRosterMember`, `PartyOrder`), and one extracted helper
(`ReadBadgeAsync`) now shared by the guest's own lines and everybody else's.

Both new lists change because of a §9 broadcast started by **another** browser, and there is no click on
this page to await and no navigation to settle. A scenario that read them once would be sampling a race it
cannot see. So both re-read until the predicate holds and then report, in one sentence, what was on screen
when it never did — the same discipline `WaitForBoardAsync` and `WaitForCommittedLinesAsync` already follow,
and for the same reason: *"the roster did not grow"* and *"the broadcast never left the other circuit"* are
indistinguishable from a bare timeout.

`TableRosterMember.IsYou` is read from the presence of the roster row's chip rather than from its word. That
chip is the only thing on the surface that makes the list this reader's view of the table rather than a list
of strings, and step 3 above turns on exactly that distinction.

`PartyOrder.TotalText` is kept as **text**. It is rendered through `MoneyText.Format(amount, CurrencyCode)`,
so parsing it back into a decimal would mean reimplementing a currency formatter inside a test in order to
compare against a number the test already knew. A scenario that cares can assert containment; this one
asserts on the lines, which is what §16.3 scenario 5 is actually about.

`GuestOrderLine` is reused for a party line rather than duplicated. The two lists come from different
sources — your own from the event fold, everybody else's from `order_current_line` — and word the badge
differently (*"At your table"* against *"At the table"*), but both publish the same `chip-ok`, which is the
state rather than the copy. The one asymmetry is that `GuestLineBadge.Removed` cannot occur on a party line
at all: `order_current_line` filters removals out in SQL (`WHERE removed.order_line_identifier IS NULL`), so
a line that was taken off simply is not there.

---

#### `SeatGuestAsync`, extracted

Scan → register with a passkey → join → wait for the circuit, in the guest's own browser context with its
own virtual authenticator. `ArrangeServiceAsync` (scenarios 4 and 6) now calls it instead of inlining the
same five steps; the call sequence is byte-for-byte what it was, so those two scenarios are unchanged in
behaviour. Scenario 5 needs two of these alive at once, which is what turned four lines inside one
arrangement into a method.

Cookies are per-context and a WebAuthn credential belongs to the authenticator that minted it, so a second
guest sharing the first's context would be the first guest with a second passkey — which is a different
scenario, and not one §16.3 asks for.

---

#### What this does not prove

The two guests are seated **sequentially**, not concurrently. §5.1's advisory lock over sitting creation and
§6.6's `FOR SHARE`/`FOR UPDATE` ordering are what make a genuine race safe, and neither is exercised here —
those belong to `MyRestaurant.DataAccess.Tests`, which already drives concurrent sends and a concurrent
close against a real PostgreSQL. A browser scenario that tried to race two joins would be asserting on
Playwright's scheduling rather than on the lock.

---

#### Where scenario 5 sits in the matrix

Live after this slice: **1, 2, 3, 4, 5, 6, 13, 14, 15**. Remaining: **7** (a removal of a fulfilled line
sinks the whole batch with a per-operation reason), **8** (one reminder and exactly one, which wants a short
`KITCHEN_SUBMISSION_REMINDER_SECONDS` rather than sixty seconds of waiting), **9** (a counter price
adjustment read old → new on the guest's screen), **10** (a close, the pending-line warning, and the flip to
a settled read-only view), **11** (hide, the hidden-records filter, unhide — the one that will meet
`EnhancedNavigation` again, on an administrator following a filter link), and **12** (a TOTP reset driving
the obligations pipeline through a forced password change and a forced re-enrollment).

Then the backup/restore drill, and M6 is done.

---

### M6 Slice 10 — the refusal a guest cannot reach (landed)

§16.3 scenario **7**: *"Guest tries to remove the fulfilled line → whole batch rejected with per-op reason;
removing their pending line succeeds."*

Writing it found that the first clause **cannot happen through the surface**, and that finding is the
slice. It also found two defects in `TableOrderSurface.razor` that no test had ever been in a position to
notice, and one in the end-to-end harness that had been quietly true since M6 Slice 6.

---

#### The scenario, and the thing it could not do

`NarratedOrderLine.GuestMayRemove` is false for a fulfilled line (§6.5.3), and §11.1 renders the removal
tick box only where it holds. So a guest looking at a fulfilled line is offered no control at all. The only
remaining route to the server rule — hold a tick on a pending line and press Send in the instant between
the kitchen fulfilling it and §9's broadcast arriving — is a race measured in milliseconds against an
in-process broadcaster, and a scenario built on it would be a coin toss dressed as an assertion.

`OrderStaging.PruneRemovals` closes even that: every re-read drops marks that have stopped being valid,
precisely so one stale tick cannot sink an otherwise-good batch under §6.5.9.

So the scenario asserts what actually happens, in three acts:

| Act | §16.3's clause | What the browser does |
| --- | --- | --- |
| (a)–(d) | *"tries to remove the fulfilled line"* | The guest ticks their pending soup; the kitchen passes that plate in the other browser; the tick is dropped, the box disappears, and §11.1 says why. **The surface refuses on the guest's behalf.** |
| (e) | *"whole batch rejected with per-op reason"* | Reached the one way a guest still can — §7's documented path. Stage the soup, let the kitchen 86 it, tick the pie for removal, Send. One reason, nothing written, and the *good* removal did not slip through either. |
| (f) | *"removing their pending line succeeds"* | Take the 86'd item out; the same tick, on the same line, now commits. |

Act (e) is not a substitute for the fulfilled-line rule — that rule is covered where it can be covered
honestly, in `OrderMutationValidatorTests` (`"A fulfilled line cannot be removed by the guest."`) and in
`OrderMutationsTests` against a real PostgreSQL under the §6.6 lock. What (e) covers is the thing only a
browser can: that §6.5.9's all-or-nothing is all-or-nothing *as a guest experiences it*, with the refused
operation named and the innocent one visibly not applied.

---

#### Defect 1 — the unticking notice nobody ever saw

`LoadAsync` opened with `_pruneNotice = null;` and set it again if anything had been dropped. That is
correct for one re-read and wrong for the way re-reads actually arrive: **one commit raises more than one
notification.** `IOrderWorkflow.AppendAsync` publishes `OrderLinesChanged` unconditionally and then
`LineFulfillmentChanged` on top of it for a fulfillment (§9), and this surface subscribes to both.

So the first pass pruned the mark and wrote the sentence, and the second pass — microseconds later, having
nothing left to prune — erased it. The tick vanished from the basket with no explanation, which is the one
outcome the sentence exists to prevent.

The notice is no longer cleared on re-read. It is cleared where the guest touches the basket themselves —
`StageItem`, `Unstage`, `ToggleRemoval`, `SendAsync` — because that is the moment it stops being news.

#### Defect 2 — `Unstage` left the previous refusal on screen

`StageItem` and `ToggleRemoval` both clear `_sendStatus` and `_rejection`: the guest changed the basket, so
what the last send said about it is stale. `Unstage` did not. §6.5.9's panel ends *"Fix these and send
again"*, and taking the offending item out **is** fixing it — so the one edit that most often resolves a
refusal was the one edit that left the refusal on screen. An inconsistency rather than a decision, and now
it matches its two siblings.

`ChangeStagedQuantity` still does not clear them. It is the fourth basket edit and it has the same argument
against it; it is left alone here because no scenario drives it and every `.razor` edit is a compiler
diagnostic I cannot see. Recorded so it is not rediscovered.

#### Defect 3 — waits that a previous action could satisfy

`TableOrderJourneys.SendAsync` waited for `#table-order-surface p.status-success`, and
`KitchenJourneys.FulfillLineAsync` waited for the board's equivalent. Both sentences **survive on screen
until something clears them**, so in any scenario that acts twice the second wait is satisfied by the first
action's message — before the second has committed, or even if it was refused. Scenario 5 escapes this only
by accident: `StageItem` happens to null `_sendStatus`, and `StageAsync` happens to wait for the render
that does it.

Both now wait on **state** rather than on copy, which is the rule the rest of this harness already follows
(`data-live`, `data-unseen-alerts`, the `chip-ok` class rather than the words "At your table"):

- a send is accepted when the **basket empties** — §11.1 clears the staging area only on an accepted event
  — and refused when `ul.order-reject-list` appears. One poll watches for both, so a refusal is now named
  at the moment it happens instead of surfacing thirty seconds later as a timeout;
- a fulfillment is done when the line **leaves the pass** — `kitchen_pending_line` excludes a fulfilled
  line (§8.3), so its disappearance is the write itself rather than a report of it.

`PressSendAsync` additionally refuses to start while a refusal panel from an earlier send is still on
screen, because that is a state in which no answer it could return would be trustworthy.

---

#### Surfaces

One class, `order-prune-notice`, on the unticking notice. Three other `p.status-error` elements live inside
this island — the picker's staging refusal, §6.5.9's panel, and the expired-session line — so there was no
way to name this one. Purely additive: `.status-error` still does all the styling and no CSS rule stands
behind the new name. The same reasoning that gave `.order-party-line-name` a class in Slice 9.

#### Harness

`TableOrderJourneys` gains the basket as a value (`BasketContents`: staged adds, ticked removals, and §7's
unavailable marks, read in one pass because a wait that read them one at a time would sample a surface that
re-renders all three together), `MarkForRemovalAsync`, `UnstageAsync`, `LineOffersRemovalAsync`,
`ReadPruneNoticeAsync` and `SendExpectingRefusalAsync`. A missing tick box is reported as §6.5.3 having
already decided, with the line's badge, rather than as a click that failed.

`KitchenJourneys` gains `EightySixAsync` and `IsEightySixedAsync`, driving §11.2's "86" panel rather than
`/administration/menu/{id}`. Both go through `IMenuWorkflow` to the same write and the same `MenuChanged`
broadcast (§9), so nothing is given up by using the one that does not navigate a board the scenario is
still watching. Completion is read from the row's `is-off` class — matched as a whole word, so a future
`is-off-peak` could not be mistaken for it — rather than from the flash sentence beside it.

#### Where scenario 7 sits in the matrix

Live after this slice: **1, 2, 3, 4, 5, 6, 7, 13, 14, 15**. Remaining: **8** (one reminder and exactly one,
which wants a short `KITCHEN_SUBMISSION_REMINDER_SECONDS` rather than sixty seconds of waiting), **9** (a
counter price adjustment read old → new on the guest's screen), **10** (a close, the pending-line warning,
and the flip to a settled read-only view), **11** (hide, the hidden-records filter, unhide), and **12** (a
TOTP reset driving the obligations pipeline through a forced password change and a forced re-enrollment).

Then the backup/restore drill, and M6 is done.

#### Housekeeping

`docs/_append/` is retired. Every one of its fifteen `BUILD_PROGRESS-*.md` files was already merged into
this document, and this slice ships the whole of `docs/BUILD_PROGRESS.md` instead of a block to append —
which also removes the duplicated **M5 Slice 3** section that had been sitting at line 1550, between M4
Slice 1's tail and M4 Slice 2's head, byte-identical to the copy at its proper place after M5 Slice 1.


---

### M6 Slice 11 — the alert nobody raised (landed)

§16.3 scenario **8**: *"A send sits unfulfilled 60 s → exactly one reminder alert."*

It is the first scenario in the matrix whose subject is something **no browser did**, and the first whose
closing assertion is that *nothing further happened* — a different shape of claim from every other one
here, and one no wait can conclude on its own.

`MYRESTAURANT_E2E=1` goes from **10 passed / 5 skipped** to **11 passed / 4 skipped**. `dotnet test` stays
at **971 total / 0 failed**.

---

#### First: the build error Slice 10 shipped

`Harness/TableOrderJourneys.cs`, `LineOffersRemovalAsync`, reported at line 412 and caused at line 433:

```csharp
+ $" It holds: {Describe(await ReadCommittedLinesAsync(page))}."
```

**CS4007.** An `await` inside an interpolation hole of a string that binds to
`DefaultInterpolatedStringHandler` — the handler is a `ref struct` and cannot be held across the
suspension point. The rest of that file already hoists the read into a local in four places and says why
in a comment each time; this one was written inline and did not compile. Fixed the same way.

The whole tree was re-scanned for the pattern: **this was the only occurrence in 317 files.** With the
end-to-end project failing to build, `dotnet test` had been reporting 956 rather than 971 — the missing
fifteen were that project's scenarios, never discovered rather than failing.

---

#### What the scenario does

An administrator bootstraps, puts soup and a steak pie on the menu, creates a table; a guest scans,
registers with a passkey, joins; the kitchen board is opened and becomes live. (`ArrangeServiceAsync`, as
scenarios 4, 6 and 7 use it.) Then:

1. **The guest sends one soup, and the kitchen ignores it.** One line rather than two, because §8.4
   reminds per *send* — the reminder is about a ticket having been ignored, not about how much was on it.
2. **One alert, and it is not a reminder.** Asserted before anything waits, so that the reminder below is
   a second thing arriving rather than the first thing being reinterpreted. Without it, a board that had
   somehow alerted twice at the send would satisfy every remaining assertion.
3. **The `kitchen_notification` rows say `initial: 1, reminder: 0`.** §10.1's row is written inside the
   send's own transaction; the reminder's absence here is the half of §8.4 that says a reminder is not
   merely a second copy of the initial alert.
4. **The threshold passes and the badge goes to two, one of them overdue.** Nobody has touched anything
   in any browser. §10.2's background service scans, finds a submission older than the threshold with
   nothing fulfilled or removed off it, writes one row, and broadcasts.
5. **The line is still on the pass.** A reminder is a nudge, not a mutation: it must not touch
   `kitchen_pending_line`, and a board that quietly dropped the ticket it was reminding about would be
   the worst possible reading of §10.2.
6. **The badge is cleared, and three more scans go by in silence** — with the send still sitting there,
   still overdue, still matching every clause of §8.4's query except the `NOT EXISTS` on a prior reminder
   row.
7. **The rows still say `initial: 1, reminder: 1`.** Which is the assertion that actually means
   "exactly one" — see below.

---

#### Why "exactly one" needed three separate things

§16.3's word is *exactly*, and none of the obvious readings carries it alone.

**The badge cannot.** `KitchenAlertState.UnseenCount` only ever rises, so two is two whether the second
alert landed a second ago or a minute ago. Clearing it first (§10.3's *"tap to clear"*) turns any further
alert into a rise from zero, which is something that can be watched for rather than inferred.

**A sleep-then-read cannot.** Waiting fifteen seconds and reading once would miss an alert that arrived
and was cleared again inside the window — which is not an exotic failure but precisely the bug being
watched for: a second reminder that a re-render swallowed. `KitchenJourneys.WatchBoardAsync` polls for the
duration and returns the **high-water mark**, so a scenario asserting the counts are zero is asserting
about the whole stretch rather than about its last instant.

**The board cannot, at all.** Its count is circuit state that a cook clears with one tap, so a quiet board
is consistent with a second row having been written and broadcast to nobody. The `UNIQUE
(order_event_identifier, kind)` constraint is what actually makes a reminder singular and §8.4's
`RETURNING` is how the scan learns its insert was swallowed. Hence
`RestaurantInstance.ReadKitchenNotificationsAsync` — the second place this harness deliberately reaches
past every surface, for a reason of the same shape as `ReadJoinSecretAsync`'s: the fact being asserted is
one no screen renders.

---

#### Five seconds instead of sixty

`KITCHEN_SUBMISSION_REMINDER_SECONDS` now threads through the harness the way
`TABLE_JOIN_TOKEN_ROTATION_SECONDS` already did — `RestaurantHarness.StartInstanceAsync` →
`RestaurantInstance.StartAsync` → the child process's environment, where it had been the literal `"60"`.

Scenario 8 asks for **5**. §8.4's scan compares `occurred_at` against a threshold it is handed, so the
rule is identical at five seconds and at sixty and the number is a duration to wait rather than a
parameter under test. Shorter would be pointless: `KitchenReminderService.ScanInterval` is a fixed five
seconds, and below that the scan's own resolution dominates and the configured threshold stops being the
thing that fires it.

**Every other scenario keeps sixty, and that default is load-bearing rather than lazy.** §8.4 is the only
thing in the system that writes because *nobody* acted. At a short setting, any scenario that sends and
then spends thirty seconds asserting on something else would acquire a reminder alert it never asked for
and never mentions — scenario 4's *"still one"* re-read being the obvious casualty. At sixty it cannot: no
scenario holds a send untouched for a minute except the one that means to.

Patience is computed from what the application was actually given
(`instance.KitchenSubmissionReminderSeconds`) rather than from what it was asked for, the same discipline
every window computation already follows: threshold + two scan intervals + twenty seconds. Two intervals
because the `PeriodicTimer` starts with the process rather than with the send, so a send landing an
instant after a tick waits a whole extra interval before it is even looked at — and a second because the
tick that finally sees it may be the one a busy container skipped.

---

#### The one product change: naming a number

`KitchenBoard.razor` published `data-unseen-alerts` and not its §10.2 half. It now also publishes
`data-unseen-reminders="@_alerts.UnseenReminderCount"`.

The count was already on screen, inside the badge, as `" (1 overdue)"` — and that parenthetical appears
only when the number is non-zero, so its absence is ambiguous between *"no reminders"* and *"no badge at
all"*. Reading it back out of a sentence that also carries pluralisation would be parsing prose to learn a
fact the component already knows. Purely additive; the value is `KitchenAlertState.UnseenReminderCount`,
which the badge already renders, and nothing on screen changes. Same reasoning as Slice 9's
`.order-party-line-name` and Slice 10's `.order-prune-notice`.

Nothing else under `src/` moved. No migration, no schema change, no package, no ADR, no `Program.cs`, no
`.slnx`.

---

#### Harness

`KitchenBoardSnapshot` gains `UnseenReminderCount` — a **subset** of `UnseenAlertCount`, not a second
tally beside it, because a reminder increments both. "One alert" after a send and "two alerts, one of them
overdue" a threshold later are the same board to anything that can only count alerts.

`KitchenJourneys` gains `AcknowledgeAlertsAsync` (§10.3's badge; a call on an already-clear board is a
named mistake rather than a tolerated no-op, because it means the alert the scenario meant to acknowledge
never arrived) and `WatchBoardAsync`. Attribute reads went through a shared
`ReadCountAttributeAsync` that names the component property it came from, because *absent* has a
different cause from *garbage* and the first is what a renamed attribute looks like.

One stale `<see cref>` corrected while in the file: `TableOrderJourneys.BasketWarningCountAsync` has not
existed since Slice 10 folded it into `BasketContents.UnavailableMarks`. Silent today only because
`GenerateDocumentationFile` is `false`; it would be CS1574 — and therefore an error under CI's
`ContinuousIntegrationBuild` — the day that changes.

---

#### What this does not prove

The scan's idempotence under **overlapping ticks** is not exercised here, and cannot be from a browser:
one process, one `PeriodicTimer`, one tick at a time. `ON CONFLICT DO NOTHING` against two concurrent
scans belongs to `KitchenNotificationsTests` against a real PostgreSQL, which already drives it. What this
scenario covers is the thing only a browser can: that the reminder **reaches a cook**, distinguishably,
once.

Nor does it prove the *sound*. §10.3's arm control needs a real user gesture and unlocks an `AudioContext`
on a headless browser with no output device, so "did it beep" is a question about Chromium. §10.3 names
the visual badge as the fallback whenever sound is not working and makes the unseen count the record of
what arrived; that count is what is asserted, and it is the same number the sound is played from.

---

#### Where scenario 8 sits in the matrix

Live after this slice: **1, 2, 3, 4, 5, 6, 7, 8, 13, 14, 15**. Remaining: **9** (a counter price
adjustment read old → new on the guest's screen), **10** (a close, the pending-line warning, and the flip
to a settled read-only view), **11** (hide, the hidden-records filter, unhide — the one that will meet
`EnhancedNavigation` again, on an administrator following a filter link), and **12** (a TOTP reset driving
the obligations pipeline through a forced password change and a forced re-enrollment).

Then the backup/restore drill, and M6 is done.

---

### M6 Slice 12 — §16.3 scenario 9, and the first staff account

`Counter_AdjustsPriceWithReason_GuestSeesOldToNew`. §16.3 words it *"counter adjusts a price with reason
→ guest sees old → new with reason"*, and the sentence has a subject that no earlier scenario in the
matrix could supply.

**Twelve of fifteen are now live.** Remaining: **10**, **11**, **12**.

---

#### Why this one needed a real staff account

Scenarios 4, 6, 7 and 8 all put an administrator at the pass on purpose, and the reasoning was recorded
each time: §3.7 admits both `kitchen` and `administrator` to `/kitchen`, an administrator covering a
station is a thing the application really supports, and standing up a staff account would have added
`/administration/people/new`, a forced password change and a second sign-in that none of those scenarios
asserts on. That reasoning does not survive contact with scenario 9.

The thing under test here is a *sentence on a guest's screen*, and §6.2 binds a `price_adjustment` to
counter **or** administrator and records which. `CounterSitting.razor` reads the actor role off the
principal rather than assuming it — `counter` wins when somebody holds both, because that is the capacity
they are standing at the till in — so an administrator adjusting the price renders *"by an
administrator"*. The assertion would then be about the wrong role, and it would pass. **Who acted is part
of the claim**, which is what makes this the first scenario that has to create a staff account and sign
in as one.

So it walks the whole of §3.7 → §3.5 on the way to the interesting part: the create-staff form, the
generated temporary password, the sign-in that lands on `/account/change-password-required` rather than
anywhere it asked for, and the change that clears the obligation. The landing page is asserted
explicitly rather than absorbed into a journey helper — a §3.7 account carries `must_change_password`,
and a counter who could reach the till on a password an administrator can still read off a screen is a
hole rather than an inconvenience.

---

#### Quantity two, and why the number is load-bearing

The pie is ordered **two** at 14.00 and adjusted to 11.00. §6.5.7 adjusts a *unit* price and §11.1
renders the *extension*, so at any quantity above one those are separable observations: the unit price
must read 11.00, the line must read 22.00, and a surface that wrote the sentence without recomputing the
money fails the second while passing the first. At quantity one they are the same number twice and the
weaker claim passes for both.

The soup is the control. One line adjusted, not the ticket — the same discipline scenario 6 applies to
fulfillment, and the half worth getting wrong.

Both totals are **derived in the scenario** from the prices it actually put on the menu, rather than
restated as constants. A restated total is a second place the fixture lives, and the day somebody changes
the soup's price to make another scenario read better, a restated total goes quietly wrong while every
assertion still passes for the wrong reason.

---

#### Two independent opinions about the same money

The guest's own lines come from `OrderNarrative.FromEvents` — the event fold, in C#. The till's figures
come from `sitting_bill`, which sums `order_current_line`'s extended prices — in SQL. §16.2 has a
view ≡ fold equivalence test over randomized sequences precisely because those two must never disagree;
this scenario asserts both from a browser, on the same adjustment, which is that property seen from a
screen rather than from a property test.

Money is asserted as **formatted text**, through `MoneyText.Format` and the currency code the instance
was configured with (`RestaurantInstance.CurrencyCode`, named this slice rather than left a literal
inside `CreateProcess`). Hard-coding `"$11.00"` would be a claim about `RESTAURANT_CURRENCY_CODE` that
silently becomes a claim about nothing the day it moves; formatting it the way the surface did makes the
assertion be about the adjustment. Comparing formatted strings is also stricter than comparing decimals,
because it catches a formatter that has started dropping its symbol — which §13 makes display-only and
therefore has no other test above it.

---

#### Three product changes, all additive

| File | Change |
| --- | --- |
| `CounterSitting.razor` | `id="counter-sitting-surface"`, `data-live`, and an `id` on each of the two price-editor inputs |
| `TableOrderSurface.razor` | `.order-line-adjustment` beside `.order-line-detail` |
| `CreateStaff.razor` | `.staff-temporary-password` beside `.totp-secret` |

**`data-live` on the till is the one that is worth having regardless of any test.** A prerendered till is
the dangerous kind of broken, because it is the kind that looks right: the bill is correct as of the
request, every total adds up, and Adjust price, Remove, Add to the bill and Close & settle are all
`@onclick` handlers with no circuit behind them. Pressing any of them does nothing at all — no refusal,
no flash, no error — and the screen never hears §9 either, so a guest sending while somebody stands at
the till changes nothing on it. Same attribute, same reasoning, as `KitchenBoard`'s and
`TableOrderSurface`'s.

**The two editor `id`s** replace the only way the fields were previously distinguishable: `inputmode` on
one and `maxlength` on the other. That is not a thing anything outside the markup should have to know.
Only one editor is ever open — `StartAdjust` calls `CancelEditors` first and `_adjustingLine` holds a
single line — so an id is unique in the document, and the wrapping `<label>` still associates each input
implicitly, so no accessible name changes.

**`.order-line-adjustment`** exists because the removal sentence directly above it carries the identical
`.order-line-detail`, so *"the detail paragraph under this line"* was never a way to name this one — and
on a line that had been both adjusted and removed, it would have named both. **`.staff-temporary-password`**
exists because that element holds a password and borrowed the TOTP class for its monospaced treatment;
reading a password out of something named for a TOTP secret breaks silently the day that page grows a
real authenticator panel. No CSS stands behind either new class, and nothing changes on screen. Same
reasoning as Slice 9's `.order-party-line-name`, Slice 10's `.order-prune-notice` and Slice 11's
`data-unseen-reminders`.

No migration, no schema change, no package, no ADR, no `Program.cs`, no `.slnx`, and no normative
specification change — §16.3's wording of scenario 9 is what was implemented, not amended.

---

#### Harness

A new `Harness/CounterJourneys.cs`: the board, opening a bill, adjusting a price, and reading the whole
bill back. Three things in it are worth naming.

**An adjustment is judged by the unit price, not by the confirmation.** §11.3 writes a flash sentence
naming the new price and that sentence survives until something clears it, so a second adjustment of the
same shape is satisfied by the first one's words. The unit price on the line is the state the transaction
wrote. The same reasoning `SendAsync` uses for the basket and `FulfillLineAsync` uses for the pass.

**The refusal is polled for first, and that ordering is deliberate.** Every button at the till goes
through `IOrderWorkflow` and can be refused under the §6.6 lock — a guest sending, the kitchen
fulfilling, somebody closing a second earlier — and a refusal leaves the unit price exactly as it was. A
poll that only watched the price would spend the whole patience failing to notice that the answer had
already arrived.

**The two money figures on a bill line share one element.** §11.3 nests `span.counter-line-unit` *inside*
`span.counter-line-price`, so the parent's inner text carries both. The child's text is removed from the
parent's rather than the pair being split on a line break, because how a flex column becomes line breaks
is a browser detail and this is exact.

`AdministrationJourneys` gains `CreateStaffAccountAsync` and a `StaffRoles` flags enum. Roles are ticked
**by the name rendered beside the checkbox** rather than by position: indexing would work today and would
silently grant the wrong role the day a fourth role is added above an existing one — which is a failure a
scenario would blame on authorization. `CheckAsync` rather than `ClickAsync`, so asking for the same role
twice is a no-op rather than an untick.

`AccountJourneys` gains `SignInWithPasswordAsync` and `CompleteForcedPasswordChangeAsync`, kept as two
methods rather than one because the page a staff member lands on in between is itself the assertion. The
submit button is named by **exclusion** — `button[type='submit']:not([name='__passkeySubmit'])` — because
this form carries two and "Sign in" as text matches both, the second being "Sign in with a passkey".

`TableOrderJourneys` gains `GuestPriceAdjustment` and `GuestOrderLineDetail`. The two amounts are read
from the elements that carry them — the old one struck through in an `<s>`, the new one in a `<strong>` —
rather than pulled back out of prose, because a single string could satisfy *"the new price appears"*
while the old one had quietly vanished, and that is the half of "old → new" whose loss costs a guest the
ability to see what changed. A missing half is reported as a failure naming which one went.
`ReadCommittedLinesAsync` now **projects** from the fuller read rather than walking the DOM a second
time; two walks that drifted out of step would be a worse price than a few extra locator round trips
against a local browser.

---

#### One widened selector, corrected in passing

`AccountJourneys.DescribeSurfaceAsync` looked for `p.status-error`. `ChangePasswordRequired.razor`
renders its refusals as a `ul.status-error` of `<li>` elements, because Identity hands back a list — so
the one page in this application whose entire job is to refuse would have described itself as reporting
no error. Widened to `.status-error`; the old match set is a subset, so no existing caller changes, and
any element carrying the class now reads out whole.

---

#### What this does not prove

The **post-close** half of §6.7 is untouched. §6.5.8 admits nothing but an administrator's corrective
events after a close, and `CounterSitting.razor` renders no Adjust button on a settled sitting at all —
so a counter's price adjustment and an administrator's correction are two different mechanisms and this
scenario exercises the first. The second belongs with scenario 10, which is where a close exists to
correct after.

Nor does it read the `order_event` row. The guest's own lines *are* the event fold, so the assertion in
(f) is already an assertion about what was written; a third opinion from `RestaurantInstance` would have
been a query that could only agree.

---

#### Where scenario 9 sits in the matrix

Live: **1, 2, 3, 4, 5, 6, 7, 8, 9, 13, 14, 15**. Remaining: **10** (a close, the pending-line warning,
and the flip to a settled read-only view — which inherits this slice's till harness and `data-live`),
**11** (hide, the hidden-records filter, unhide — the one that will meet `EnhancedNavigation` again, on
an administrator following a filter link), and **12** (a TOTP reset driving the obligations pipeline
through a forced password change *and* a forced re-enrollment — which inherits this slice's
`CompleteForcedPasswordChangeAsync` and needs only the second obligation added beside it).

Then the backup/restore drill, and M6 is done.

---

### M6 Slice 13 — §16.3 scenario 10, and the write that cannot be undone

Scenario **10** of the §16.3 matrix: *"Counter closes (pending-line warning shown) → table flips to
settled read-only; totals match."* It inherits Slice 12's till harness, `data-live`, staff account and
forced-password-change journey wholesale, and adds the close.

Live after this slice: **1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 13, 14, 15**. Remaining: **11** and **12**.

---

#### What makes this one different

Every scenario before it asserts on something that could be done again. A send can be resent, a
fulfillment re-marked, a price re-adjusted, a join secret rotated twice. §5.3 has no undo: `closed_at`,
`closed_by_person_identifier` and `settled_total_amount` are stamped together under a `FOR UPDATE`, and
the total *is never rewritten*. So this is the first scenario whose subject is a one-way door, and that
shapes three decisions below.

---

#### Seven readings of one number

"Totals match" is the part of §16.3's sentence that could most easily be satisfied by nothing at all. Six
of the seven figures this scenario compares are computed at render time from the same `sitting_bill` view,
so all six could agree perfectly on a close that stamped no total whatsoever. They are still worth
comparing — they are computed by different code in two languages on three circuits — but only the seventh
makes the claim §5.3 actually promises.

| # | Where | How it is computed |
| --- | --- | --- |
| 1 | The till header, before the close | SQL sum over `sitting_bill` (`CurrentTotalAmount`) |
| 2 | §11.3's confirmation prompt | `CurrentTotalAmount`, quoted directly |
| 3 | The till header, after the close | the stamped `settled_total_amount` (`AmountToShow`) |
| 4 | The till's settle panel | C# sum over the per-person entries |
| 5 | The guest's "Table total" | C# sum over `sitting_bill`, on another circuit |
| 6 | The guest's "Your total" | C# sum filtered to one person, on another circuit |
| 7 | `table_sitting.settled_total_amount` | the column, read past every surface |

Reading 2 earns its place on its own: it is the last number a person reads before an irreversible write,
and it comes from a third expression rather than from either sum beside it. A prompt asking somebody to
confirm a figure other than the one about to be stamped is the worst available place for a disagreement.

Reading 6 is asserted rather than assumed. One guest is at the table, so their own total *is* the table's
— which means a filter that had stopped filtering would produce the right answer for the wrong reason,
and only in a party of one. That is exactly this scenario, so it says so.

Every figure is derived in the scenario from the prices it created. `soup + 2 × pie`, not `34.50`.

---

#### One soup delivered, two pies that never arrive

The bill is deliberately mixed, and the quantities are chosen so that no wrong arithmetic produces the
right answer.

- **The pending-line warning needs exactly one line to name.** §11.3 renders
  `CounterSittingSummary.PendingLineCount`, which counts rows in `order_current_line` that are not
  fulfilled. The pie line is *one* row at quantity two — so a warning that had started counting portions
  would say "2", and the scenario asserts **1**. Getting this wrong in production means a counter being
  told the wrong thing at the moment they decide whether to charge.
- **The settled total has to include food nobody ate.** §5.3's "knowingly charge" is the whole point of
  the warning: the counter is told, settles anyway, and the table is charged. A close that quietly dropped
  the undelivered line would produce a smaller total on all seven readings and would look entirely
  self-consistent.
- **At one of each item, several wrong sums are the right number.** A total that added the *unit* prices
  instead of the extensions, or one that counted the delivered line twice, is indistinguishable from the
  correct total at quantity one. At one soup and two pies each of those is a different number.

The pie is also asserted to still be undelivered *after* the close, on both the till and the guest's
phone. A surface that re-badged it at settlement would be telling the guest their food arrived — which is
the one fact on that bill they might want to argue about.

---

#### Why a counter account, when the screen does not branch on role

Scenario 9 stood up a staff account because §6.2 records the actor and §11.1 renders it, so the assertion
would otherwise have been about the wrong role and *would have passed*. Scenario 10's reason is the mirror
image, and it is worth writing down because the assertion passes either way **today**.

`CounterSitting.razor` gates every control on `_sitting.IsOpen` and never consults the principal. An
administrator at a settled till sees the identical read-only screen. So the argument is not about today's
markup — it is about the direction of the next failure. §6.5.8 admits nothing but an administrator's
corrective events after a close, and §5.3 says corrections "are an administrator's". The day this surface
grows the correction panel those sections describe, an administrator standing at a settled till will
*correctly* see controls a counter must not. Asserting "read-only" as an administrator is asserting it for
the one role permitted to act after a close; the counter is the role for which read-only is unconditional,
and that is the claim §11.3 makes.

The administrator still covers the pass for one tap, on scenarios 4, 6, 7 and 8's reasoning. A second
staff account for a single fulfillment would be a sign-in and a forced password change nothing here
asserts on.

---

#### The close is two harness calls

`BeginCloseAsync` returns §11.3's confirmation prompt; `ConfirmCloseAsync` accepts it. Not a style
preference: a composite would settle the table before a scenario could read the prompt, and there is no
going back for it — a settled sitting renders no prompt at all. The same reasoning kept
`SignInWithPasswordAsync` and `CompleteForcedPasswordChangeAsync` apart in Slice 12, and the same
reasoning discarded a composite `SignInAsStaffForTheFirstTimeAsync` there.

The barrier for the close is `p.counter-readonly`, rendered from `!_sitting.IsOpen` — that is, from
`closed_at` being set on the row this page re-read after the transaction committed. Waiting on the
confirmation sentence would be wrong in the way this harness keeps rediscovering: `_notice` survives until
something clears it, so a second close of the same shape is satisfied by the first one's words.

`AlreadyClosed` returns normally rather than throwing. The sitting really is settled, the view really does
flip, and a scenario that cares which close it observed has the notice text; only `SittingNotFound` — a
problem with no flip — is a failure. So the poll looks for the read-only note **before** the refusal, or a
losing race would be reported as a fault.

---

#### Three surfaces, one flip

§16.3 says "the table flips to settled read-only", and the table is on three screens at once.

**The till** goes read-only in place — §11.3's "closed-sitting lookup (read-only)" is the same page, not a
second one. The assertion is mostly an absence, counted rather than enumerated: zero `.counter-line-actions`
blocks, no staff-add panel, no close button. A settled sitting still offering Adjust would be a door that
only ever answers no. The total's *label* is read beside the total, because `AmountToShow` feeds one
element in both states — so the amount alone cannot say which it is, and a close that stamped nothing
would leave a screen that looks entirely correct. `.counter-detail-corrected` is asserted **absent**: §5.3
shows both figures only when §6.7 corrections exist, and a corrected total seconds after a close would
mean the stamped value and the live one had diverged on their own.

**The guest's phone**, which nobody touches. §9 publishes `SittingClosed` after the commit, the circuit
re-reads, `GetOpenSittingForMemberAsync` now answers `null` because `closed_at` is set, and §11.1's
picker, Send row and removal ticks stop being rendered. The lines and both totals stay — the settled view
is a bill, not an empty page.

**The counter board**, where the phrase is most literally true: the table leaves the floor and appears
under "Settled today" at the stamped amount. Both lists are read together, because a table on neither has
vanished and a table on both is two rows for one sitting.

---

#### The flip is an absence, so the heading got a name

Everything that identifies §11.1's settled view is something that stopped being rendered — and a surface
whose circuit died mid-scenario has none of those either. A wait keyed on the picker disappearing would be
satisfied by a dead page while proving nothing.

`TableOrderSurface.razor`'s settled `<h2>` gains **`.order-settled-heading`**, the one positive marker
that the flip happened. "The second `h2` on the surface" was never a way to name it: that is also the
heading of the live ordering view. Same reasoning, and same fourth time now, as `.order-line-adjustment`,
`.order-prune-notice` and `.order-party-line-name` — no CSS rule stands behind the class, `.table-heading`
still does all the styling, and the tag tree is unchanged.

`CounterSitting.razor`'s two close buttons gain **`#counter-close`** and **`#counter-close-confirm`**,
because they are otherwise the same selector: "the primary button in the settle section" is *Close &
settle* before the prompt and *Yes — close & settle* after it. Nothing outside that markup could tell "I
opened the confirmation" from "I settled the table", and settling cannot be undone — which makes this the
one place in the application where the distinction is most worth having in the markup rather than inferred
from which panel happens to be on screen. The two live in exclusive branches, so each is unique in the
document, and neither is styled by its id.

---

#### `ReadSettledSittingAsync`, and why it is scoped by sitting

The third place this harness reaches past every surface, after `ReadJoinSecretAsync` and
`ReadKitchenNotificationsAsync`, and for the same shape of reason: the fact being asserted is one no
screen renders. Reading 7 is the column.

Scoped by the **sitting**, unlike `ReadOpenSittingAsync`'s table. A table has at most one *open* sitting —
the partial unique index says so — but any number of closed ones, and the next guest to scan opens
another. "The settled sitting on this table" is not a question with one answer. The scenario has the
identifier because `OpenSittingAsync` returns it off the URL it followed.

The three stamped columns are read together because the schema will not have them any other way:
`table_sitting` carries `CHECK ((closed_at IS NULL) = (closed_by_person_identifier IS NULL))` and the same
paired check on the total, so a reader returning one of them would be describing a state the database
cannot hold. The join to `person` is `INNER` for the same reason — a closed row without an actor is
impossible, and silently reporting a dropped constraint as "still open" would be the worst available
answer.

The scenario also asserts `ReadOpenSittingAsync` now returns **null**. That is the row-level form of "the
table left the floor", and it is what makes the next guest to scan open a new sitting rather than rejoin a
settled one.

---

#### Also new in the harness

`CounterJourneys` gains `ReadPendingWarningAsync` (returning `null` for "no warning", which is a real
answer a scenario should assert on either way), `BeginCloseAsync`, `ConfirmCloseAsync`,
`ReadSettledTillAsync`, `ReadFloorAsync`, and prose for both. The pending count is parsed off the leading
`<strong>` the same way a bill line's quantity is parsed off its "2×": the digits are the data and the
words around them are markup. The board's settled amount nests §5.3's "now …" corrected figure exactly as
a bill line nests its unit price, so the existing `WithoutUnitPrice` does that separation too.

`TableOrderJourneys` gains `ReadTotalsAsync`, `ReadSettledViewAsync`, `WaitForSettledViewAsync` and
`DescribeSettledView`. The totals are found by the `<dt>` that names each figure rather than by position —
which is how anything reading the document finds them, and is what keeps a third total added between the
two from silently shifting the answer. A missing term is a failure naming which terms the list does hold,
because a surface that had stopped rendering "Table total" would otherwise compare equal to one showing
the wrong one: both produce an empty string.

`ReadSettledTillAsync` is safe to call on an *open* sitting and worth doing, since every field is then the
other value. That is how a scenario establishes that a flip happened rather than that the page always
looked settled.

---

#### What this does not prove

**The contended close.** §5.3's `FOR UPDATE` conflicting with §6.6's `FOR SHARE` is the guarantee that no
event slips in after the total is computed, and nothing here contends for it — the guest sends, then the
kitchen fulfills, then the counter closes, in sequence. That property is §16.2's, where a concurrent send
and close are driven against a real PostgreSQL without a browser in the way; two Playwright contexts
racing a lock would be a slower test of the same thing with a worse failure message.

**The post-close correction.** §6.7's administrator-only corrective events, and the two-figure display
§5.3 requires once they exist, are asserted here only in the negative: no correction has been made, so no
corrected total is shown. The positive case has a surface of its own in administration (§11.4) and no
§16.3 scenario claims it.

**`AlreadyClosed`.** The harness handles it correctly and no scenario reaches it. An end-of-day pass
(§5.4) closing a table a counter is standing at is the real shape of that race, and it belongs with a
scenario about §5.4 rather than one about §5.3.

---

#### Build/test checklist for this slice

```bash
cd /home/kushal/src/dotnet/myrestaurant

dotnet build
#    expect: all seven projects succeed, 0 errors

dotnet test
#    expect: total 971, failed 0, succeeded 956, skipped 15 — unchanged from Slice 12.
#    Scenario 10 moves from [Fact(Skip)] to [Fact] + Assert.SkipUnless; xUnit counts both as
#    skipped, so with MYRESTAURANT_E2E unset every number is identical.

bash scripts/ci_local.sh --with-all

MYRESTAURANT_E2E=1 dotnet test tests/MyRestaurant.EndToEnd.Tests
#    expect: total 15, failed 0, 13 passed, 2 skipped
#    Scenario 10 adds roughly 25-30s: a /setup wizard, two menu items, a table, a staff account, a
#    guest registration, a send, one fulfillment, two Argon2id verifies and two hashes, a close, and
#    no waiting on any timer.
```

No .NET SDK in the sandbox, so none of this has been run here. What was run, on every edited file:
brace/paren/bracket balance and a depth walk with strings and comments stripped; a CS4007 scan (no `await`
inside any interpolation hole); a CS1620 scan confirming every additive operand inside every
`string.Create(...)` is an interpolated string; a Razor tag-structure comparison against the pristine file
from `dump.txt`, confirming both `.razor` edits leave the tag tree **identical**; an existence check of all
twenty-seven selectors the new harness code depends on against the markup it targets; and a SHA-256
comparison of all seven pre-edit files against the hashes `export.sh` recorded, so every unchanged byte is
known to match the working tree.
