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
