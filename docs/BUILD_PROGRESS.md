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

- [x] **Stage 1 — M1: skeleton + pure Domain**
- [x] **Stage 2 — M2: identity & accounts** *(plus the close-out that added the profile page — F-35)*
- [x] **Stage 3 — M3: tables & joining**
- [x] **Stage 4 — M4: ordering** *(plus the close-out that made `RESTAURANT_TIME_ZONE` true on every surface — F-36)*
- [x] **Stage 5 — M5: counter & administration**
- [x] **Stage 6 — M6: hardening** *(CI, the full §16.3 end-to-end suite, guest registration — F-37, the backup/restore drill — F-38, and the close-out that stamped the build and shipped the source offer — F-39)*

**All six stages are complete.** These boxes went unticked for four milestones, which is worth one sentence rather than a silent correction: each stage was finished in its own slice and the summary at the top of the file was never the thing anybody read, so nobody noticed. It is the same failure mode as F-35 and F-37 in miniature — a claim nobody was checking — and the honest fix is to check it here, once, at the release.

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

### M6 Slice 14 — §16.3 scenario 11, and a red test that was reading the stylesheet

Two things, and they belong in one slice because the second one was hiding inside the first.

**The red test.** Slice 13 shipped scenario 10 with `Assert.Equal("Settled total", settled.TotalLabel)`.
It went red on the first character: `Expected "Settled total"`, `Actual "SETTLED TOTAL"`. Playwright's
`InnerTextAsync` returns the browser's own `innerText`, which is defined in terms of layout and therefore
has `text-transform` already applied — and `CounterSitting.razor` upcases
`.counter-detail-total-label` for the eyebrow treatment. The label the component wrote really is
`Settled total`; the harness was reading the presentation layer.

Forty lines further on, the same mistake was waiting. `TableOrderJourneys.ReadTotalsAsync` reads each
`<dt>` of §11.1's totals list with `InnerTextAsync` and looks the result up in a dictionary keyed on
`"Your total"` and `"Table total"`, and `app.css` upcases `.order-totals dt`. Both lookups would have
missed and the method would have thrown *"§11.1's totals list does not carry both 'Your total' and 'Table
total'"* about a totals list that was entirely correct. Fixing only the first failure would have moved the
red rather than cleared it.

The whole surface was swept before either fix: 24 `text-transform` declarations in `src/` against 66
`InnerTextAsync` calls in the harness. Those two are the only collisions. `.eyebrow`, `.chip-role`,
`.manage-label`, `.hidden-facts dt`, `.event-stream-badge`, `.restaurant-clock-label` and the rest are
never read by the harness, and `p.pairing-code`, `p.totp-secret` and `p.staff-temporary-password` carry no
transform at all.

`Harness/ScreenText.cs` is the fix: `DeclaredAsync` reads `TextContentAsync` and collapses whitespace runs,
which makes it exactly `InnerTextAsync` minus the transform. It is deliberately **not** a blanket
replacement. The distinction is what the comparison is about — a *label* read is a claim about which branch
a component took, and casing is noise in it; a read of *content* (a table's label, a person's name, an
amount through `MoneyText.Format`) is data no rule here transforms. Two sites changed; sixty-odd were left
alone.

**Scenario 11.** A guest hides a settled order, it leaves their own history, and an administrator finds it
by username and puts it back.

The second guest is what makes the central assertion mean anything. With one guest, *"their history is
empty afterwards"* is satisfied equally well by a page that stopped rendering, by a reader that started
returning nothing, and by a hide that hid the sitting — and all three are catastrophic. A bystander whose
own history is unchanged across the same write separates *this order was hidden* from *history broke*, and
costs one registration rather than the second sitting a per-order claim would otherwise need.

Four figures, all derived from the prices the scenario created and all different from one another: the
hider's share (`soup + 3 × pie`), the bystander's (one soup), the table's stamped total, and the pie's unit
price. A history page showing the table's total where a person's belongs cannot pass by coincidence.

Both identifiers come off links the surfaces rendered — `?hide=` on the guest's Hide link, `?record=` on
administration's expand link — the same recovery `AdministrationJourneys` already does from a
"Manage this…" link. *"A row appeared"* is satisfied by any hidden order in the restaurant; that the row
administration found **is** the order this guest hid is a claim about those two identifiers agreeing.

The filter is asserted in both directions, and the negative one is not decoration: a filter that had
quietly stopped filtering would return this row for every username there is and would satisfy the positive
case perfectly.

Three product-facing facts are re-read from the server rather than from a DOM already on screen, because a
stale document agrees with *"nothing changed"* without having been asked: the bystander's history, the
till's bill (through §11.3's closed-sitting lookup, since a settled table has left the open list), and
`table_sitting.settled_total_amount` past every surface.

**No counter account**, and the contrast with scenarios 9 and 10 is the reason to say so. There the role
was load-bearing — §6.2 records who adjusted a price and §11.1 renders it; §11.3 makes read-only
unconditional for a counter and conditional for an administrator. Here the close is arrangement rather than
subject: §6.8 refuses a hide on an open sitting, so this needs a settled one and does not care who settled
it.

**Four additive markup changes, no CSS behind any of the new names, nothing changes on screen.**
`TableHistory.razor` and `HiddenRecords.razor` each get an `id` on their `section.panel` — every other
surface in the harness has one, and the two status paragraphs are `p.status-success` and `p.status-error`,
which a document-wide match would confuse with whatever the layout was saying. Each also gets a class on
its empty-list sentence, which was otherwise a `p.lede` among the page's other `p.lede`s and reachable only
by position. On `HiddenRecords.razor` that one element carries two different sentences through a ternary,
and the difference is load-bearing: *"nothing matches that filter"* and *"nothing is hidden anywhere in the
restaurant"* are different facts, and only the second says the restaurant is back where it started.

Two facts were deliberately **not** given fields. §11.4 renders the hidden row's table label between a
username and a timestamp in one sentence, and its line count as the second of two
`span.hidden-record-note`s — so reaching either means splitting prose or indexing siblings. Both are
asserted where they have elements of their own, on the guest's history page. A harness field that could
only be filled by counting siblings starts lying the day a third note is added.

The two `ol.hidden-events` lists an expanded record draws are told apart by what their entries *contain*
rather than by where they sit: a stored event wraps its metadata in `div.hidden-event-head` because it has
a sequence number to put beside the type, and a visibility event has no such wrapper. The heading-sibling
route works today and stops working when a paragraph is added.

`CounterJourneys.OpenSettledSittingAsync` is new and waits on the read-only note as part of its barrier,
not as a bonus: the route renders the identical component for an open sitting, so waiting only on the
surface would return happily from a bill that had never been settled — and every caller is re-reading one
*because* it is settled.

```bash
dotnet build
#    expect: all seven projects succeed, 0 errors

dotnet test
#    expect: total 971, failed 0, succeeded 956, skipped 15 — unchanged from Slice 13.
#    Scenario 11 moves from [Fact(Skip)] to [Fact] + Assert.SkipUnless; xUnit counts both as skipped,
#    so with MYRESTAURANT_E2E unset every number is identical.

bash scripts/ci_local.sh --with-all

MYRESTAURANT_E2E=1 dotnet test tests/MyRestaurant.EndToEnd.Tests
#    expect: total 15, failed 0, 14 passed, 1 skipped
#    Scenario 10 goes green on the ScreenText fix; scenario 11 adds roughly 35-40s — a /setup wizard,
#    two menu items, a table, two guest registrations with real WebAuthn attestations, two sends, a
#    close, a hide, two filters, an expand and an unhide, and no waiting on any timer.
```

No .NET SDK in the sandbox, so none of this has been run here. What was run: SHA-256 comparison of all six
pre-edit files against the hashes `export.sh` recorded in `dump.txt` — all six matched, so every byte not
touched is known identical to the working tree; brace/paren/bracket balance and a depth walk (never
negative, ends at zero) with strings and comments stripped, across every file in the test project; a CS4007
scan (no `await` inside any interpolation hole); a CS1620 scan confirming every additive operand inside
every `string.Create(...)` is an interpolated string; a Razor tag-tree walk of both edited components; an
existence check of all **36** selectors the two new journey classes depend on against the markup they
target; and a check that none of the four new names has a CSS rule anywhere, so nothing changes on screen.

### M6 Slice 15 — §16.3 scenario 12, and the last placeholder in the matrix

Scenario 12 is the fifteenth and last of §16.3, so `PendingHarnessExtension` is deleted with it: there are
no skipped placeholders left in `EndToEndScenarios.cs`.

The spec's sentence is *"Admin resets a TOTP-enrolled user → user password sign-in → forced password
change → forced TOTP re-enrollment → lands home; passkey sign-in path also hits the pipeline."* Every
clause is walked in that order, through the real surfaces, and two of them needed something the harness
did not have.

**Two browsers for one person, and it is not tidiness.** A WebAuthn private key never leaves the
authenticator that minted it, so the passkey this scenario registers belongs to one browser context for
good. That context cannot also be where the password sign-ins happen: `passkey.js` fires a
conditional-mediation `navigator.credentials.get()` on *every* sign-in page load, and the CDP virtual
authenticator reports a resident key and simulates presence automatically — so once a discoverable
credential exists in that context, a "password sign-in" there may be answered by the authenticator before
a password is ever typed. It would still land on the forced-change page, and the scenario would pass for
the wrong reason. So the staff member gets a *device* (authenticator, holds the passkey, does the passkey
sign-ins) and a *terminal* (no authenticator, does the password walk). The first password sign-in is on the
device and is safe there for a stated reason rather than by luck: the authenticator is still empty at that
point, so the conditional request has nothing to answer with.

**`SignOutAsync` could not sign a trapped principal out.** `ObligationsEnforcement.IsExemptPath` exempts
sign-out and the two obligation pages and nothing else — `/sign-in` is not on that list — so a principal
holding an obligation cannot reach the sign-in page at all, and sign-out is the only way to a fresh cookie.
But both obligation pages render a sign-out form of their own beside the header's ("Not ready right now?",
"Done for now?"), because §3.5 promises leaving is always possible. `SignOutAsync` held a bare
`Locator("form.sign-out-form button[type='submit']")` — a strict locator — which resolves to two elements
there and throws a strict-mode violation on every method that acts on one. `.First` fixes it, and taking
the first is safe rather than merely convenient: the two forms are identical in effect (same endpoint, same
token, neither carries a `returnUrl`, so `SafeLocalReturnUrl(null)` sends both to `/`), and the header's
comes first in document order. Scenario 12 signs a trapped principal out twice.

**Why the sign-out ordering is load-bearing.** `ObligationsMiddleware` decides from the cookie's claims,
not from the row. So the device is signed out *before* the terminal clears the two flags. Left signed in,
its cookie would still carry `must_change_password` and it would still be redirected — and the closing
assertion that the pipeline *releases* a passkey session could not then tell a fresh cookie from
`ChangePasswordRequired.razor`'s own stale-claim guard firing. That closing assertion is the point: without
it, *"the passkey path hits the pipeline"* is satisfied equally well by a middleware that refuses passkey
sessions permanently, which would be a worse defect than the one it guards against.

**Four form posts of arrangement, and no shortcut available.** §3.7's create-staff form writes
`must_change_password` and nothing else — no secret, no passkey, and deliberately not `must_enroll_totp` —
so an enrolled account with a passkey cannot be arranged by an administrator. It could have been arranged
by `INSERT`, and that would have been the wrong move: the reset under test *probes*
`totp_secret_protected` to decide whether to clear an authenticator at all, so a fixture that got that one
column wrong would produce a password-only reset and the scenario's second obligation would never exist.
The account enrols itself through §3.4's voluntary page and adds its own passkey through §3.3's, which is
what a real staff member does.

**Chips rather than columns.** Every flag this scenario asserts on has a row in `person` a fixture could
read directly, and reading it directly would prove nothing about §3.7: that `must_change_password` is set
is one claim, and that an administrator can *see* it is another. So the flags are read as the chips
`ManagePerson.razor` renders, found by the `span.manage-label` beside each group rather than by position —
the same reasoning as `TickRoleAsync`, and for the same reason: indexing works today and silently starts
reading roles as credentials the day a fourth fact is added above an existing one. The Credentials group is
the interesting one, because it is *derived* rather than stored: "Authenticator" appears iff
`totp_secret_protected IS NOT NULL` (§3.4 has no enrolled column), so the chip's absence after the reset is
the surface agreeing the secret is gone, and its return at the end is the surface agreeing a new one
landed.

**Declared text, at two new sites.** `.manage-label` is upcased for the eyebrow treatment, so the label
this reader matches on comes back as `STATUS` through `InnerTextAsync` and every lookup would miss; and
`.chip-role` is capitalized, so a role chip whose markup says `kitchen` — the stored vocabulary, which is
what `person_role.role_name`'s CHECK constrains — reads back as `Kitchen`. Both go through Slice 14's
`ScreenText.DeclaredAsync`. That helper was written for a failure that had already happened; this is the
first slice where it prevented two.

**The state assertions are pairs, never singletons.** Every claim in the post-reset block is of the form
"this chip is there now", which a chip that had always been there satisfies perfectly. So the same three
groups are read before the reset as well, and the pair is the assertion. The same shape applies to the
recovery codes: two sets of ten, asserted disjoint. §3.7's reset deletes every `totp_recovery_code` row and
§3.4 replaces the set on confirmation, so an overlap of even one code would mean a code the reset was
supposed to have destroyed is still live — and nothing else in the suite would notice.

**One non-default `ReturnUrl`, deliberately placed.** The pipeline's destination in this scenario is `/`,
which is also `SafeLocalReturnUrl`'s fallback — so "lands home" on its own cannot separate *carried the
destination across two redirects and two cookie re-issues* from *dropped it*. The trapped device therefore
asks for `/kitchen`, the one board its role could otherwise walk straight into, and the redirect is
asserted to carry `ReturnUrl=%2Fkitchen`. That is both §3.5's "no authenticated endpoint is reachable" on a
real area page rather than on a sign-in navigation, and the one place in the scenario where step (3)'s
carry is distinguishable from the default.

**One product change, additive.** `ManagePerson.razor`'s reset panel wrote the temporary password into
`<p class="totp-secret">`, which is the same collision Slice 12 fixed on `CreateStaff.razor` — an element
holding a password, addressable only by a class named for an authenticator key. It mattered more here: the
account this panel just reset is on its way to `/account/enroll-totp-required`, whose own `p.totp-secret`
holds a *real* authenticator key, so one selector meant two different secrets on two consecutive screens.
The element gains `.staff-temporary-password` beside the class it already had. There is no CSS rule for
that name anywhere in `src/`, so nothing changes on screen, and the harness reuses the constant Slice 12
introduced.

**The kitchen role, chosen rather than defaulted.** §3.4's authenticator is a staff credential — §17
accepts a password-only counter and nothing asks a guest to carry TOTP — so a staff account is the faithful
subject of "a TOTP-enrolled user". And the role gives the closing claim something to point at: `MainLayout`
renders the kitchen link to the kitchen role and to nobody else, not even to administrators, so "lands
home" becomes "landed home as this person, with this role's door on screen". Scenarios 9 and 10 use the
counter role; a fixture of its own keeps a failure here unambiguous.

```bash
dotnet build
#    expect: all seven projects succeed, 0 errors

dotnet test
#    expect: total 971, failed 0, succeeded 957, skipped 14 — one fewer skip than Slice 14.
#    Scenario 12 moves from [Fact(Skip)] to [Fact] + Assert.SkipUnless, and xUnit counts an
#    Assert.Skip as skipped too — so the total is unchanged and one test moves from the
#    unconditionally-skipped column into the conditionally-skipped one. With MYRESTAURANT_E2E unset
#    every scenario still skips.

bash scripts/ci_local.sh --with-all

MYRESTAURANT_E2E=1 dotnet test tests/MyRestaurant.EndToEnd.Tests
#    expect: total 15, failed 0, 15 passed, 0 skipped — the first fully green E2E run.
#    Scenario 12 adds roughly 40-50s: a /setup wizard, a staff account, four Argon2id hashes across
#    three password sign-ins and two forced changes, two TOTP confirmations, two passkey ceremonies
#    (one attestation, two assertions), a reset, four reads of the management page, and no waiting on
#    any timer.
```

No .NET SDK in the sandbox, so none of this has been run here. What was run: SHA-256 comparison of all five
pre-edit files against the hashes `export.sh` recorded in `dump.txt` — all five matched, so every byte not
touched is known identical to the working tree; brace/paren/bracket balance and a depth walk (never
negative, ends at zero) with strings, chars, raw string literals and comments stripped, across all three
edited C# files; a CS4007 scan (no `await` inside any interpolation hole); a CS1620 scan over every
`string.Create(...)`; a Razor tag-tree walk of `ManagePerson.razor`'s markup region and a brace check of its
`@code` block; an existence check of all 30 selectors and rendered phrases the new journeys depend on
against the markup they target; a count confirming both obligation pages carry exactly one sign-out form of
their own beside the layout's, which is the strict-mode violation `.First` resolves; and a check that
`.staff-temporary-password` has no CSS rule anywhere, so nothing changes on screen.

---

### M6 Slice 16 — the restore drill, and the four defects it found before it could run

§19's M6 line has read "full E2E suite (§16.3), backups + restore drill, …" since v1.0. Slice 15 closed the
first clause. This slice closes the second, and the interesting part is not the drill — it is what happened the
moment something tried to rehearse a procedure four documents agreed was already in place.

**`scripts/restore.sh` could not have completed a restore.** It ran `pg_restore --clean --if-exists` under
`set -euo pipefail`, with `web` already stopped, one line *before* the `up -d web` that would bring it back.
`pg_restore` exits **1 whenever it ignored any error at all** — that is its documented contract, and it is
`exit_code = AH->n_errors ? 1 : 0` in `pg_restore.c`, checked against the PostgreSQL 17 source rather than
recalled — and `--clean --if-exists` ignores errors as a matter of course, because that is what `IF EXISTS` is
*for*. So `set -e` killed the script one line early. The single most likely outcome of the documented recovery
procedure was a database that came back and an application that stayed down, with nothing printed to say so.
That is worse than a crash: a crash is attributable.

**Nothing captured the Data Protection key ring.** §15 has required it alongside the database since v1.0,
`OPERATIONS.md` §6 said to do it as step 3, §8 explains exactly why, and **F-16's own row in
`DOCUMENTATION_REVIEW.md` lists it under *Embodied in*.** Both scripts printed a reminder. Four documents in
agreement about a thing no code did, which means every backup ever taken from this tree was a set that restores
every account and no enrolled authenticator (§3.4).

**A failed dump could evict a good one.** The dump went straight through a redirect, so a truncated file
survived as the newest `.dump`. `set -e` skipped *that* run's pruning — which is what F-16's "prunes only after
a successful new dump" promises — but the next successful run counted the poison file toward
`BACKUP_RETENTION_COUNT` and pruned a real backup to make room for it. The guarantee held within one run and
broke on the following one.

**Container discovery took the first match.** `ps --format '{{.Names}}' | grep -m1 postgres`. Harmless with one
postgres container; a drill needs a second one. A backup that dumps the scratch database succeeds, comes out
roughly the right size, and is worthless — which is precisely the failure a backup script must not be capable
of. It now refuses on ambiguity and names what it found.

All four are one ledger row, **F-38**.

#### What the drill actually asserts

`scripts/restore_drill.sh` starts its own PostgreSQL container — distinct name, no published port (so it cannot
collide with the live `127.0.0.1:5432`), no volume (so its data dies with it) — restores a real backup set into
it, and tears it down on the way out. It never writes to the live database, and the argument for that is
structural rather than a promise: there is exactly one connection target in the file, `scratch_query`, and it
names `$SCRATCH` and nothing else. The only thing that goes near the live instance is `--from-live`, which
delegates to `scripts/backup.sh`, which only reads.

Seven gates. Two of them are the ones worth defending.

**Gate C reads its expectations out of the migrations.** `^CREATE TABLE x` / `^CREATE VIEW x`, anchored, over
`src/MyRestaurant.DataAccess/Migrations/*.sql` — 22 tables and 5 views today. A hard-coded list would have been
easier and would rot on the first migration nobody remembered to add to it. The parse also has the failure mode
a hard-coded list does not: if the DDL is ever reformatted past those patterns, the gate reports *that* rather
than silently passing on an empty expectation. Anything present in the dump that no migration declares is
reported too, so a stray object cannot hide behind a green run.

**Gate D reads DbUp's journal, because structural completeness is not the question this code asks.** At startup
`SchemaMigrationRunner.IsUpToDate()` asks DbUp whether every embedded script has been applied, and
`/healthz/ready` answers from it. A restored schema whose `schemaversions` is short is a schema this code will
try to migrate. The table name and shape were verified against `dbup-postgresql`'s `PostgresqlTableJournal` —
`schemaversions`, columns `schemaversionsid` / `scriptname` / `applied`, unqualified so it lands in `public` —
and the journal stores the embedded *resource* name, so a migration's file name is a suffix of its row rather
than equal to it. The gate matches on the suffix for that reason.

Gate E queries every §8.3 projection view, which is the one place in the schema where an object's correctness
depends on nine others and therefore the thing `--clean` is most plausibly able to break. Gate F is a row
census, reported and never asserted — the only sensible count for a fresh instance is almost all zeros, and the
only way to notice you have been faithfully backing up an empty database for a month is to be shown the
numbers. Gate G is the key ring, and it is the reason a drill of a database-only set is not allowed to look
like a pass.

Gates A and B are the cheap ones: the archive lists, and it restores. `--strict` promotes reservations (ignored
errors, an empty ring) to failures.

#### The key ring, and why the write direction is safe

Reading it out is uncontroversial: `podman cp <web>:<dir>/. -` streams a tar through the engine's own archive
API, so it works regardless of what is installed in the runtime image — no `tar` in the container, no helper
image, no mount.

Writing it back crosses the volume-ownership question that `:U` exists to answer, and that is why this was
nearly deferred. It is safe, and for a checkable reason rather than a hopeful one: `mcr.microsoft.com/dotnet/aspnet:10.0`
resolves to the Ubuntu-based variant, whose `runtime-deps` Dockerfile creates the `app` user at UID 1654 and
**never issues `USER app`** — verified in `dotnet/dotnet-docker`'s image sources — and this repository's
`Containerfile` does not set `USER` either. The application runs as root. Root-owned key files in that
directory are exactly what it already writes there itself, so `podman cp` in either direction and on either
engine cannot get the ownership wrong. `compose.yaml`'s `:U` is belt-and-braces, not load-bearing.

The restore happens while `web` is stopped, which is not tidiness: Data Protection creates a fresh key ring the
first time it protects anything, so a ring dropped in after startup would be sitting beside one the application
had already minted and begun using.

#### Why the drill went into `boot-smoke` rather than its own job

Everything a drill needs is already standing up in that job: a built image, a database DbUp has migrated, and a
running application. A separate job would have to build the image a second time to answer one question. So
`boot-smoke` gained three steps after its readiness probe — render a page, back the instance up, drill the
backup — and its display name is unchanged so nothing keyed on the check name moves.

The middle step needs the first, and the reason is a detail that would otherwise have made Gate G meaningless:
Data Protection creates its first key the first time it protects *anything*, and `/healthz/ready` protects
nothing. On a freshly booted instance the key ring is an empty directory. `/setup` renders a form, which mints
an antiforgery token, which mints the key. Without that `curl`, every CI run would have reported an empty ring
and the gate would have become noise.

`POSTGRES_CONTAINER` and `WEB_CONTAINER` are set explicitly in the job rather than discovered, because the
runner generates the service container's name and because `backup.sh` now refuses to guess when more than one
container matches — which is exactly what the drill's own scratch container causes moments later. And the backup
set is deliberately **not** uploaded as an artifact: the `-dataprotection.tar` is key material in the clear.
Throwaway here, but publishing one is not a habit worth forming, and `.gitignore` now refuses it for the same
reason.

The CI drill runs **without** `--strict` on this first landing. Every FAIL gate still blocks; what `--strict`
adds is that ignored `pg_restore` errors and an empty ring also fail, and neither number has been observed on a
real run. Once a few runs report "pg_restore completed with no errors", tightening it is a one-word edit.

#### Three-valued exit codes, in both scripts

"The database was dumped and the key ring was not" is neither success nor failure, and a scheduled job needs to
be able to tell. `backup.sh`: **0** complete set, **2** database only, **1** nothing usable. `restore.sh`: **0**
restored and healthy, **2** restored with reservations (ignored errors, or the ring did not go back), **1**
nothing restored. `restore.sh` also gained `--yes` for scripted recovery, since a recovery you cannot automate
is one more thing to get wrong at the worst possible time.

#### One documentation decision, stated so it can be vetoed

`REQUIREMENTS.md` is **untouched**, and that is a judgement call rather than an oversight. The atomic-documentation
rule (R§10 · S§18) wants a behaviour change to land with its requirement edit — but §15's sentence *"the Data
Protection keys volume must be backed up alongside the database"* was already the contract. Nothing here is new
intent; the documents were right and the code did not do what they said. So this lands as a defect fix at the
mechanism level: S§15 rewritten, S§16.4 amended, O§6 rewritten with O§8 and O§14 following it, F-38 entered, and
no revision bump on the requirements.

O§14's first paragraph also said CI "runs three gates" and listed three of four — `end-to-end` had been missing
since Slice 2. Fixed in passing.

#### Build and test

```bash
bash -n scripts/backup.sh scripts/restore.sh scripts/restore_drill.sh
shellcheck --severity=warning scripts/*.sh   # blocking gate: clean
shellcheck --severity=style   scripts/*.sh   # advisory gate: also clean

git add scripts/restore_drill.sh
#    NOT optional. Both scripts/ci_local.sh and CI's shell-scripts job enumerate with
#    `git ls-files '*.sh'`, so an untracked new script is silently unchecked.

chmod +x scripts/restore_drill.sh

dotnet build
#    expect: all seven projects succeed, 0 errors — no C# changed in this slice.

dotnet test
#    expect: 971 total, 0 failed, 957 succeeded, 14 skipped — unchanged from Slice 15.
#    Nothing here is a test; the drill is a shell gate, deliberately, because it asserts
#    on pg_dump/pg_restore round-tripping and Testcontainers is the wrong tool for that.

bash scripts/ci_local.sh --with-all

# the drill itself, against a live dev stack:
bash run.sh --containers-only
bash scripts/backup.sh --no-keys            # dev has no web container to read a ring out of
BACKUP_DIRECTORY=/var/lib/myrestaurant/backups bash scripts/restore_drill.sh --no-keys
#    expect: gates A–F pass, G skipped with a WARN, exit 0. Roughly 20-30s, most of it the
#    scratch container's first-boot initdb.
```

No .NET SDK in the sandbox, and no container engine either, so none of the three scripts has been executed
here. What *was* run: `bash -n` and `shellcheck` at both `--severity=warning` and `--severity=style` on all
three (clean at both, which keeps `ci.yml`'s claim that every script in the tree is style-clean true); a YAML
parse of the edited `ci.yml` confirming four jobs and nine steps in `boot-smoke`; the drill's pure-bash logic
exercised against the real migration files — 22 tables and 5 views parsed, `contains` verified safe on empty
arrays under `set -u`, the generated census SQL inspected, the journal suffix match and the ignored-error
regex checked on both the matching and the non-matching input; `pg_restore`'s exit-code contract read out of
the PostgreSQL 17 source; DbUp's journal table name and column shape read out of `dbup-postgresql`; and the
container's default user established from the .NET image sources rather than assumed.

#### What is left in M6

Nothing. §19's M6 line: full E2E suite (Slices 2–15), backups + restore drill (this slice), cloudflared
production profile + tunnel docs (M1/F-06a), quick-tunnel demo script with warning (landed early), OPERATIONS
runbooks (M5), CI pipeline (Slice 1), guest registration (Slice 5).

The next move is not a slice, it is a **release**: `scripts/ci_local.sh --with-all`, a drill against the real
stack, then a tag. `Stage 6 — M6: hardening` can have its checkbox.

## M6 close-out — the release: what the program says about itself, and to whom

Slice 16 ended with "What is left in M6: **Nothing**. The next move is not a slice, it is a release."
That was true of the feature list and false of the tree, and the gap only became visible by reading the
repository against *what a tag would make true* rather than against §19. Publishing changes who the
audience is, and two questions that had obvious answers while one person ran one instance stop having
them the moment somebody else can `podman pull` the thing:

- **Which build is this box running?** Nothing stamped a version. `Directory.Build.props` set no
  `VersionPrefix`, so every assembly reported the SDK's default `1.0.0`; `Program.cs` called
  `AddService(serviceName: "myrestaurant")` with no `serviceVersion`, so every trace and metric left
  the process unversioned; and no surface reported a build at all. The only answer available was
  "whatever the person who deployed it typed", which is not an answer, it is a memory.

- **Where is the source?** R§1 says the project is published *"so anyone may run their own copy under
  the AGPL"*, and `CONTRIBUTING.md` has told forks since rev 1 that *"your fork owes its users the
  same"*. AGPL-3.0-only §13 asks a **modified** version to prominently offer its users the
  corresponding source. Nothing in the application made that dischargeable, so a fork operator
  complied by writing a page from scratch or — far more likely — not at all.

Both land here, together, because they are one thing: §13 offers the source *of the version being
interacted with*, so an offer that cannot name a revision is approximate. Both land **before** the tag
rather than after it, because the first tag is the version people cite.

This is **F-39**, and it is the same shape as F-35 and F-37 — a capability the surrounding documents
assumed and §19's build order never claimed. Unlike those two, it was not found by somebody trying to
use the thing. It was found by the pre-publication read, which is the first time that habit produced
anything, and is therefore worth writing down as a habit.

### The colophon and `/source`

`AppColophon.razor` renders one quiet line beneath the wall clock — product, version, and a link
reading *"Source code (AGPL-3.0-only)"* — on every page, in both layouts. `Source.razor` is the
destination: version, revision, licence, and the URL the operator publishes at.

Four decisions inside that, each with a reason rather than a preference:

**It is a sibling of the clock's `<footer>`, not a child.** `RestaurantClockFooter` owns that element
and pins `ShouldRender() => false` because a script owns its text node after first paint; putting
markup inside it would mean reasoning about that. The two are rendered as a pair by both layouts and by
nothing else, so they can be *styled* as one bar: the clock keeps the border and the background, the
colophon takes over the bottom padding and the `env(safe-area-inset-bottom)` that keeps it clear of a
notched handset's home indicator. `.app-footer`'s `padding-bottom` moved for that reason and no other.

**`/source` is on §3.5's exemption list.** The obligations pipeline exists to stop a flagged principal
*acting* until they have changed a password or enrolled an authenticator. It is not a reason to
withhold the licence under which they are being shown a page — and the footer they are looking at links
there, so the alternative is a visible dead link on the one page they are allowed to see.

**There is no off switch.** An offer with an off switch is not one. An operator who wants it gone has
the source and the freedom to remove it, which is precisely the arrangement the licence exists to
guarantee.

**The revision is text, not a link.** Composing `{url}/tree/{revision}` would be the page guessing at
the URL layout of a forge it has never been told the identity of. GitHub, GitLab, Gitea, cgit and
Sourcehut do not agree, and a link that 404s is worse than a hash somebody can paste into
`git checkout`.

### The stamp, and the SourceLink trap

The obvious implementation is `-p:SourceRevisionId=$SHA` and let the SDK append it. **That silently does
nothing.** `AddSourceRevisionToInformationalVersion` in `Microsoft.NET.GenerateAssemblyInfo.targets` is
conditioned on `SourceControlInformationFeatureSupported`, and a search of `dotnet/sdk` finds that
property in exactly two files — that target and its own test. SourceLink sets it. Nothing else does.
Read out of the SDK source rather than recalled, because the failure mode is a build that succeeds and
a page that quietly reports "Not recorded" forever.

So the `Containerfile` passes `InformationalVersion` explicitly:

```dockerfile
ARG VERSION=1.0.0
ARG SOURCE_REVISION=
RUN INFORMATIONAL_VERSION="${VERSION}${SOURCE_REVISION:++${SOURCE_REVISION}}" \
    && dotnet publish … /p:Version="${VERSION}" /p:InformationalVersion="${INFORMATIONAL_VERSION}"
```

`${SOURCE_REVISION:+…}` expands to `+<revision>` only when the argument is set and non-empty, so an
unstamped build produces a clean `1.0.0` rather than a trailing `+` that the parser would have to treat
as a revision it does not have. A package dependency to obtain one string was the worse trade.

`BuildInformation` parses it back out and is the only reader. Its rules, and why each is a rule:

- **Everything after the first `+` is the revision.** SemVer allows dot-separated metadata and the
  SDK's own target appends `.$(SourceRevisionId)` when a `+` is already present, so two segments means
  two facts were stamped; dropping either would be the parse forming an opinion about which mattered.
- **A prerelease label stays with the version.** Splitting on the wrong character turns `1.1.0-rc.1`
  into `1.1.0` and publishes a release candidate claiming to be the release.
- **Not recorded is a real answer.** No revision renders as text saying so — never a guess, never an
  empty `<code>` element. It is the one field somebody would act on, and a production instance
  reporting "Not recorded" is itself the useful signal that it did not come from the pipeline.
- **A non-hexadecimal revision is not abbreviated.** A fork may stamp a tag or a build number, and
  truncating `nightly-2026-08-04` to seven characters would be this code assuming everyone uses git.

The same string becomes OpenTelemetry's `service.version` — the full informational version, not the
semver, because the question a collector is asked after a deployment is *which build changed* and two
builds of one tag are indistinguishable without the revision.

### The gate, which is the part that will still be true in a year

F-38's lesson was *a row in the embodiment column should name something executable*. This is the first
opportunity to apply it unprompted, so:

```yaml
- name: the source offer names this commit
  run: |
    page=$(curl --fail --silent http://127.0.0.1:8080/source)
    grep --quiet --fixed-strings "${{ github.sha }}" <<<"$page"
```

The stamp travels from a build argument through an MSBuild property, an assembly attribute, a parse and
a component. **Every link in that chain fails silently** — the page still renders, and it renders "Not
recorded", which reads as a configuration choice rather than a defect. The commit appearing in the
response is the one assertion a broken chain cannot satisfy. It also doubles as a reachability check:
no cookie is sent, so a regression that put the licence offer behind authentication fails here.

It lives in `boot-smoke` for the same reason the restore drill does — a built image and a booted
instance are already standing up there.

### The release pipeline, now that a tag means something

`release.yml` gained three things. The version is **derived from the tag** in a step of its own rather
than read back out of `metadata-action`'s tag list, and both the ref name and ref type arrive through
`env:` rather than being interpolated into the script body — a ref name is attacker-influencable on a
fork, and `${{ }}` inside `run:` is textual substitution before the shell ever sees it. That version
and the commit are **passed into the image build**, so the container reports what the registry called
it. And a **GitHub release** is opened on the tag by a downstream job holding the only `contents:
write` in either workflow, idempotent so a re-run updates the note instead of failing on "already
exists".

All eight action majors in both workflows were checked against the GitHub API rather than assumed —
`checkout@v7`, `setup-dotnet@v6`, `cache@v6`, `upload-artifact@v7`, `setup-buildx-action@v4`,
`login-action@v4`, `metadata-action@v6`, `build-push-action@v7`. All current. `release.yml` has never
run; if one of them had been wrong, the first tag is where it would have surfaced.

### Two things fixed in passing

**The specification's header said v1.1** while its own changelog already carried a v1.2 entry — Slice 16
bumped one and not the other. The header now says what the changelog says.

**Every stage checkbox from 2 to 6 was unticked**, with Stage 2 still marked "in progress" through four
completed milestones. Each stage was finished in its own slice and the summary at the top of this file
was never the thing anybody read. Same failure mode as F-35 in miniature: a claim nobody was checking.

### One documentation decision, stated so it can be vetoed

`REQUIREMENTS.md` moves to **rev 3** with one new §8 principle. This is the opposite call from Slice 16,
which deliberately left the requirements untouched, and the difference is real: §15's key-ring sentence
was *already the contract* and the code had failed to honour it, so that was a defect fix at the
mechanism level. Here nothing previously said the running program must name itself or offer its source.
`CONTRIBUTING.md` said a fork *owes* it, R§1 said the project is published under the AGPL — but neither
is a requirement on the program's behaviour. This is new intent, so the requirements move.

### Build and test

```bash
dotnet build
#    expect: all seven projects succeed, 0 errors.

dotnet test
#    expect: 996 total, 0 failed, 982 succeeded, 14 skipped — 25 more facts than Slice 16.
#    16 in the new BuildInformationTests, 8 in RestaurantOptionsTests (the source URL, and the
#    five shapes that must be refused), 1 InlineData in ObligationsEnforcementTests.

bash scripts/ci_local.sh --with-all

# then, before tagging, confirm the stamp end to end on a real build:
podman build --file Containerfile --tag myrestaurant_web:stamped \
    --build-arg SOURCE_REVISION="$(git rev-parse HEAD)" .
#    boot it and open /source: it must name that commit, not "Not recorded".
```

No .NET SDK in the sandbox, so nothing here has been compiled. What *was* run: brace, paren and bracket
balance on every C#, Razor and CSS file; a tag-balance parse of all four touched components with Razor
comments, `<style>` bodies and `@code` blocks stripped; a YAML parse of both workflows (four jobs and
ten `boot-smoke` steps in `ci.yml`, three jobs in `release.yml`); the SDK's version targets read out of
`dotnet/sdk` to establish the SourceLink condition and that `Version` falls back to `VersionPrefix`;
AGPL §13 read out of this repository's own `LICENSE` rather than recalled; the eight action majors
checked against the GitHub API; and every documentation edit applied by exact-match replacement with an
assertion that the anchor appears exactly once, so nothing was edited by position.

### What is left

The tag. `Stage 6 — M6: hardening` has its checkbox; so does every stage above it.

## M6 Slice 18 — the tree must parse before anything tries to build it

The close-out slice ended with "the next move is the tag". The next thing that actually happened was
that the whole solution stopped building, and it is worth writing down carefully, because the defect
was not in the program and the interesting part is not the mistake.

### What the failure looked like

```
$ dotnet build
error MSB4024: The imported project file
"/home/kushal/src/dotnet/myrestaurant/Directory.Build.props" could not be loaded.
Data at the root level is invalid. Line 86, position 1.
```

Seven of those, one per project, and the same message from `dotnet clean`, from `restore`, from `test`
and from inside the container build — `NETSDK1013: The TargetFramework value '' was not recognized`
there, which is the same fault wearing a different hat, since the `TargetFramework` that file supplies
never arrived. MSBuild imports `Directory.Build.props` before it evaluates anything, so a malformed one
fails every verb in the repository.

Line 86 was a line of eighty `#` characters, appended after `</Project>`. That string is the section
separator `export.sh` writes *between* files in a context dump.

### The extent, established by arithmetic rather than by looking

`export.sh` publishes an exact byte count for every file it emits (`Size: 4.3 KiB (4447 bytes)`).
Reading each file's content as exactly that many bytes and comparing the tail against the separator
gives an answer that does not depend on judgement: **twenty-one tracked files carried the identical
82-byte suffix** — a newline, eighty `#`, a newline.

The twenty-one were exactly the *modified* files of the close-out slice. The five *new* files of that
slice were clean. That asymmetry names the cause with no room left for a theory: the modified files
were reconstructed by reading the previous dump back, and the reader took the decoration between files
for the end of a file. The authoritative terminator in that format is the byte count. It was always
there; nothing in the dump format needed fixing.

`docs/BUILD_PROGRESS.md` had **two** — the trailing one, and a second buried at line 5760 between the
Slice 16 section and the close-out section, left over from an earlier cycle. The close-out had appended
text after it, which is how a stray separator stops being at the end of a file and becomes something no
amount of inspecting the end of a file will ever find.

### Why this is F-40 and not an apology

Of the twenty-one files, **six broke anything**:

| File | What the line means there | Consequence |
| --- | --- | --- |
| `Directory.Build.props` | XML content after the root element closed | `MSB4024` on every MSBuild verb — the outage |
| 5 × `.cs` | a preprocessor directive with a garbage name | `CS1024`, on a compile that never got to run |

The other **fifteen absorbed it in silence**. In `ci.yml`, `release.yml`, the `Containerfile` and
`.env.example` the line is a comment, and all four parsed perfectly while carrying it. In the four
Markdown documents it renders as a heading rule. In the three Razor components it is literal text on
the page. In `app.css` it is a dangling selector, which discards itself and the rule that follows it.

A class of damage that is catastrophic in one file and invisible in fifteen is a class of damage that
belongs to something running on every push. The cost of finding it turned out to be one `grep`.

### The gate

`scripts/check_tree.sh` — new, and the first gate in both CI (its own `tree` job) and
`scripts/ci_local.sh`. Five properties of the checkout, asserted before any tool that would report
their absence as something else:

1. **No context-dump separator** in any tracked file. `export.sh` is exempt **by path**, because
   writing that string is its job; the exemption is a literal path comparison rather than a cleverer
   rule so that it is obvious and cannot widen by accident. The threshold is **twenty** `#` rather than
   eighty: Markdown's deepest heading is six and nothing in this tree has a use for twenty consecutive
   ones, so a separator that got re-wrapped or truncated on the way in is caught too, where an
   exact-length match would wave it through.
2. **No whitespace-only lines.** Deliberately narrower than `.editorconfig`'s
   `trim_trailing_whitespace`: it fails only on lines made *entirely* of spaces or tabs, never on
   trailing whitespace after real content, because two spaces at the end of a Markdown line are a hard
   break and a gate that forbade those would be wrong about Markdown rather than right about
   whitespace. A line with nothing but indentation has no such defence.
3. **LF endings and a final newline.** Both load-bearing rather than cosmetic. A CRLF in a shell script
   reports as `bad interpreter: /usr/bin/env bash^M`, which names the wrong problem; and a missing final
   newline is what a truncated transfer looks like, which makes this the cheapest available detector of
   the *other* way a delivered tree arrives damaged.
4. **Every `.props`, `.targets`, `.csproj`, `.slnx` is well-formed XML.** The gate that turns this
   incident from a morning into thirty seconds. `xml.etree` is standard library, so no package and no
   network. Well-formedness only — MSBuild remains the authority on whether a project *means* anything;
   this asserts MSBuild will get far enough to have an opinion.
5. **Every `.yml` / `.yaml` parses.** Blocking where a parser exists, a reported skip where none does —
   the shape the shellcheck gate already uses. Worth being clear that this gate could **not** have
   caught the incident: a trailing `#` line is valid YAML. Gate 1 is what finds that. This one is for a
   workflow that was truncated or re-indented, which nothing else reads early enough to blame correctly.

Gates 1–4 need only git, grep and the Python standard library, so they block everywhere including a
workstation with no SDK installed.

Two pre-existing `.editorconfig` violations were fixed so gate 2 lands at zero noise: `compose.yaml:109`
and `DapperUserStorePasskeyTests.cs:228`, both blank lines carrying leftover indentation. That follows
the reasoning this repository already applies to `NU19xx` — a gate that reports a finding on every run
is a gate people learn to ignore.

`REQUIREMENTS.md` is deliberately **untouched**. `.editorconfig` has asked for LF endings, a final
newline and trimmed whitespace since M1, and §16.4 is the section that says which of the project's own
rules are enforced instead of remembered. Nothing new is being asked of the program.

### Build and test

```bash
bash scripts/check_tree.sh
#    expect: 5 gates, "tree hygiene passed.", exit 0. Under two seconds, no SDK needed.

dotnet build
#    expect: all seven projects succeed, 0 errors. This is the assertion that matters in this slice —
#    the previous tree could not reach a compiler at all.

dotnet test
#    expect: 996 total, 0 failed, 982 succeeded, 14 skipped — unchanged from the close-out slice.
#    No test is added, moved or renamed here. If this number differs from 996, the cause is the
#    close-out slice's 25 new facts having never run rather than anything in this one.

bash scripts/ci_local.sh --with-all
#    expect: 5 numbered gates locally now; tree hygiene is the new first one.
```

### What was verified here

No .NET SDK and no container engine in the sandbox, so nothing was compiled. What *was* run:

- The twenty-one damaged files identified by byte arithmetic against each `METADATA` block's `Size:`
  field, not by inspection — and the corrupt suffix confirmed byte-identical across all of them.
- After repair: all 10 MSBuild/solution files parsed as XML, all 4 YAML files parsed, and the tree
  scanned for every other artifact the dump format could have leaked (`--- METADATA ---`,
  `--- CONTENT ---`, `# FILE: `, the `═` rule) — none present.
- The whole tree checked for the properties gates 2 and 3 assert, to be certain they land green rather
  than green-except-for-two: exactly two whitespace-only lines existed and are fixed; **zero** files had
  CRLF, **zero** lacked a final newline, **zero** were empty.
- `scripts/check_tree.sh` run against the repaired tree (5 gates pass, exit 0), then run against a
  scratch copy with all five damage patterns re-introduced — the separator in `Directory.Build.props`,
  in `Program.cs` and buried in `BUILD_PROGRESS.md`, a whitespace-only line in `compose.yaml`, and a
  truncated flow sequence in `ci.yml`. It reported six problems, named each by file and line, and
  exited 1.
- `bash -n` plus `shellcheck` at `--severity=warning` *and* `--severity=style` on the new script and on
  the edited `ci_local.sh`, both clean at both, which keeps `ci.yml`'s claim that every script in the
  tree is style-clean true.
- A YAML parse of the edited `ci.yml`: five jobs, `tree` first with two steps, `boot-smoke` still ten.
- Every documentation edit applied by exact-match replacement with an assertion that the anchor appears
  exactly once, so nothing was edited by position.

### What is left

The tag, still. Nothing in this slice changes the release procedure beyond adding a two-second check in
front of it.

## M6 Slice 19 — the gate that could not pass

Slice 18 added `scripts/check_tree.sh` and made it the first gate in CI and in `scripts/ci_local.sh`.
The next full run reported this:

```
tree hygiene FAILED: 1321 problem(s). Nothing was modified.
```

On a tree in which every file was correct. And because tree hygiene is gate 1, the four gates behind it —
shell lint, strict Release build, the full test suite, the end-to-end scenarios — **did not run at all**.
CI's `tree` job was red for the same reason, on the same files.

Everything else in that run was green, which is worth stating plainly because it locates the defect:
`dotnet build` succeeded on all seven projects, `dotnet test` returned **996 total, 0 failed, 981
succeeded, 15 skipped**, `MYRESTAURANT_E2E=1` returned **15 passed, 0 skipped** — the whole §16.3 matrix
live — `run.sh --smoke` passed, the container stack came up healthy, `dotnet list package --outdated`
found nothing, and the quick tunnel worked. The only thing wrong with the repository was the gate
inspecting it.

### The 1321, which resolve exactly

| Count | Files | What the gate said | What was true |
| --- | --- | --- | --- |
| 638 | `docs/llm/dump.txt` | gate 1: separator, not content | it is a context dump; the separator **is** its structure |
| 638 | `docs/llm/vendor/claude-output.txt` | gate 1: separator, not content | same |
| 45 | every `.tar.gz` and `.zip` under `docs/llm/vendor/` | gate 3: "no final newline (truncated…)" | a gzip stream ends where it ends; a trailing `0x0A` would corrupt it |

1276 + 45 = 1321. Two independent bugs, not one.

### Bug 1 — the exemption was half a rule

Gate 1 exempted `export.sh`, on the stated grounds that writing separators is that script's job. It did
not exempt `docs/llm/` — the directory `export.sh` writes them **into**. `export.sh` has always excluded
that directory from its own output (`EXCLUDED_DIRECTORY="docs/llm"`), because a dump containing itself is
nonsense. The gate knew about the producer and not about the product.

The second reason to exclude it is stronger than the first and generalises: a dump is a *copy* of the
authored files. Every property this gate asserts is therefore asserted twice against the same content —
so a real finding is reported twice, and a correct separator is reported as a defect. Nothing under
`docs/llm/` is authored, and the gate's five properties are properties of files somebody wrote.

### Bug 2 — three gates, two beliefs about what a file is

Gates 1 and 2 are `grep -I`, and `-I` makes grep report no match in a binary file. They were binary-safe
**by accident**. Gate 3's final-newline half is `tail -c 1 | wc -l`, which has no such notion, so it
failed every archive in the tree — and its message, *"truncated, or an editor that does not add one"*, is
precisely backwards about a file that is intact.

Three checks over one file set, holding two different beliefs about what a file was, with nothing making
them reconcile. The fix is not a third guard: it is one predicate, `is_authored_text`, that all three
consult. Binary-ness is asked of `grep -I` rather than read off an extension list, because the extension
list is a list somebody must remember to update — and would have been wrong about the `.zip` files on the
day they were added.

### Considered and rejected

**A `.gitattributes` marking the archives `binary`.** The idiomatic git answer, and it would work for
bug 2. Rejected because it also changes how git diffs, merges and archives those paths — a larger change
than this gate needs — and because it does nothing about bug 1, a context dump being text.

**Untracking `docs/llm/`.** Not mine to propose. The directory is the project's deliberate working record
and the gate has no business editing what the repository chooses to keep. The gate's scope is the thing
that was wrong.

**Making gate 1 advisory.** This is the argument Slice 18 itself made against noisy gates, pointed the
other way: a gate that reports 1276 findings on a correct tree teaches people to skip it, and the four
real gates behind it go too.

### The gate now reports what it declined to inspect

```
checking 310 authored text file(s) of 327 tracked
  skipped: 17 generated (docs/llm), 0 binary, 0 empty
```

"Checking 412 tracked file(s)" was a true sentence on the run that failed 1321 times, and the least
useful true sentence available. A gate whose silence is supposed to mean something has to say what it
looked at.

### Build and test

```bash
bash scripts/check_tree.sh
#    expect: 5 gates, "tree hygiene passed.", exit 0. The skip line reports 17 generated files.
#    This is the assertion that matters in this slice.

bash scripts/ci_local.sh --with-all
#    expect: all 5 numbered gates RUN, for the first time since the gate landed. Gates 2-5 have
#    never executed under ci_local.sh, so this is where a surprise would appear — not in gate 1.

dotnet test
#    expect: 996 total, 0 failed, 981 succeeded, 15 skipped. UNCHANGED and now an observation
#    rather than a prediction: the previous run reported exactly this.

MYRESTAURANT_E2E=1 dotnet test tests/MyRestaurant.EndToEnd.Tests
#    expect: 15 passed, 0 skipped. Unchanged; no test is touched in this slice.
```

### What was verified here

No .NET SDK in the sandbox, so nothing was compiled — but this slice's subject is a shell script, and it
was executed:

- The 1321 accounted for exactly, by file and by gate, from the run's own output: 638 + 638 separator
  findings and 45 final-newline findings.
- `docs/llm/` reconstructed in a scratch tree at its real size and shape — both committed dumps and
  seventeen archives — so the gate faced the input that produced the failure rather than a description
  of it. Against that tree: **5 gates, exit 0, 17 skipped as generated.**
- **Sensitivity re-proven, which is the assertion that matters more than the pass.** All five damage
  patterns re-introduced *outside* `docs/llm/`: the separator appended to `Directory.Build.props`, to
  `Program.cs`, and buried at line 3000 of `BUILD_PROGRESS.md`; a whitespace-only line in `compose.yaml`;
  a truncated flow sequence in `ci.yml`; a CRLF in `scripts/backup.sh`; a stripped final newline on
  `README.md`. The gate reported **8 problems**, named each by file and line — including
  `Directory.Build.props:85` from gate 1 **and** gate 4 — and exited 1.
- A binary file planted outside `docs/llm/` in the same run, to prove the binary rule is general rather
  than a `docs/llm/` carve-out: reported as `1 binary` in the skip line, and accused of nothing.
- `grep -I -q ''` confirmed as a binary detector against files with real NUL bytes and against a real
  gzip stream, and confirmed to agree with a NUL scan of the first 8000 bytes.
- `bash -n` plus `shellcheck` at `--severity=warning` **and** `--severity=style`, both clean, which keeps
  `ci.yml`'s claim that every script in this tree is style-clean true.
- Every documentation edit applied by exact-match replacement with an assertion that the anchor appears
  **exactly once**, so nothing was edited by position.

### What is left

The tag. Unchanged from Slice 18, and now genuinely unblocked: `ci_local.sh --with-all` can reach its
last four gates.

## M6 Slice 20 — the door nobody left open

M6's close-out line reads: *"Then `scripts/ci_local.sh --with-all`, a drill against the real stack, and
the tag."* The first of those had just passed. Everything had:

| | Result |
| --- | --- |
| `dotnet build` | all seven projects, 0 errors |
| `dotnet test` | 996 total, 0 failed, 981 succeeded, 15 skipped |
| `MYRESTAURANT_E2E=1` | 15 passed, 0 skipped |
| `scripts/ci_local.sh --with-all` | all 7 gates, green |
| `scripts/check_tree.sh` | 310 authored files, 0 findings |
| `dotnet list package --outdated` | nothing outdated |

So the tree was read against what a tag makes true, which is the habit F-39 established. The defect was
not in the tree. It was one layer further out, and it is **F-42**.

### What the repository said about itself

Asked through the API, read-only:

```
has_issues                       : true
SECURITY.md                      : 404 at the root, in .github/, and in docs/
private vulnerability reporting  : disabled
has_wiki                         : true
description                      : null
open issues / pull requests       : 0
```

`CONTRIBUTING.md` had said since rev 1, in the indicative mood: *"Issues are disabled. There is no bug
tracker to file into."*

They were not. They never had been. The setting was on for the entire life of the sentence.

### Four doors, none of them working

A person who reads this source — which is what the AGPL is *for*, and which is precisely the population
that finds security defects — had these options:

- an Issues tab that was **open**, and that the only document addressing the question said was shut;
- no Security tab entry, because no `SECURITY.md` existed anywhere GitHub looks;
- no private reporting form, because the setting was off;
- no address, anywhere in the tree, in any file;
- and a notice that pull requests are closed unreviewed whatever their merit.

The only channel that actually worked was the one the documentation denied, **and it was public.** So
the first thing anybody would have done with a forgeable join token is publish it — not out of malice
but because there was nowhere else to put it.

Nothing had been lost yet, and only for one reason: nobody had tried. Zero issues, zero pull requests,
on the day this was found. That is luck, and luck is not a channel.

### Why this is a finding and not a chore

It is a category this ledger had not recorded, and the distinction is worth keeping because the guard
against each shape is different:

| Shape | Example | What was wrong |
| --- | --- | --- |
| a capability a requirement stated and no milestone claimed | F-35, F-37, F-39 | the build order |
| a rule four documents agreed on and no code honoured | F-38 | the embodiment column recorded intent as fact |
| the transport between a correct spec and correct code | F-40 | twenty-one files damaged in delivery |
| **the repository disagreeing with its own documents** | **F-42** | **a layer nothing in the tree can see** |

`check_tree.sh` reads `git ls-files`. A test process cannot see a settings page. And the one document
that made a claim about that page made it in a mood that cannot be checked from inside the repository.
Every gate this project has built was green, correctly, about the wrong thing.

### The rule that came out of it

Narrower and more useful than *check your settings*:

> **A document in this tree states policy, never platform state.**

"Nothing filed here is triaged" is a commitment. It is true wherever it is read, it survives somebody
toggling a checkbox, and it is the project's to keep. "The issue tracker is off" is a claim about a
checkbox, verifiable only from outside — and it went wrong in the one direction that mattered: the door
it declared shut was the single door standing open, and it was a public one.

### What shipped

**`SECURITY.md`**, at the repository root beside `LICENSE` and `CONTRIBUTING.md`. The private channel;
**no bounty, in the second paragraph** rather than discovered afterwards; scope in both directions,
including the deliberate out-of-scope entry for a deployment this maintainer does not operate, which
under the AGPL is most of them; timelines as targets one person can meet rather than an SLA nobody
would; newest-tag-only support, because there are no maintenance branches and a promise nobody can keep
is worse than its absence; and a section sending a reporter to **§17 first**, so the ≤120 s replay
window and the ruled absence of a `/register` limit cost nobody an unpaid evening.

**The carve-out, with its reason.** `CONTRIBUTING.md` now says a vulnerability report is not a
contribution. Refusing a feature costs the person who wanted it, and the AGPL has already handed them
the source and the freedom to build it — a fair arrangement. Refusing a report costs an operator's
*guests*, who never chose this software, cannot read that file, and have no fork to run. Opposite
costs, opposite answers.

**`scripts/check_repository.sh`** — F-38's lesson applied for the third time, and again without being
asked: a row in the embodiment column should name something executable. Two deliberately unequal
halves.

The **tree half blocks** on git and grep alone. A policy exists, is non-empty, names a reporting channel
and points at §17. `README.md`, `CONTRIBUTING.md` and `SECURITY.md` each name the others — the *edges*
are asserted rather than the files, because the way this breaks is a rewrite that forgets one edge, not
a deletion. And **no tracked file asserts a repository setting**, which is this finding made
unrepeatable rather than merely corrected. The files whose job is to record what this tree used to say —
this document, `DOCUMENTATION_REVIEW.md`, and the gate itself — are exempt by literal path, the way
`export.sh` is exempt from the separator gate, and for the same reason: quoting a defect is what a
ledger does.

The **platform half is advisory**. It reads the repository object and the private-reporting endpoint and
reports the issue-tracker state, the wiki state, whether a description is set, whether private reporting
is on. Advisory is a *ruling*, not caution: a fork's settings are the fork's business, and a gate that
failed somebody's build over this maintainer's disclosure preferences would be wrong about the licence
this project ships under. A token without `administration:read` is reported as *unknown* rather than as
a finding, so a fork's pull request stays green.

### Considered and rejected

**Folding it into `check_tree.sh`.** That script's five gates are all offline, all blocking, and all
assertions that a file somebody wrote is machine-readable. Half of this one is none of those, and a gate
whose halves carry different authority should not answer to one exit code.

**Making the platform half blocking.** Tempting, because a WARN nobody clears is a WARN people learn to
ignore — this project has twice argued exactly that, in Slices 18 and 19. Rejected because the argument
does not transfer: those gates reported on files, so there was always a commit that could clear them.
This one reports on something outside the tree, where no commit can, and a fork cannot satisfy an
assertion about this maintainer's settings at all.

**Untracking or disabling the wiki from here.** Not mine to do, and not a file. It is reported, with the
reason, and left as an operator decision.

**Writing the disclosure policy into `CONTRIBUTING.md` instead of its own file.** GitHub reads
`SECURITY.md` specifically, from the root, `.github/` or `docs/`, and surfaces it in the Security tab
and the reporting flow. A policy nobody is shown is not a policy.

### The honest limit

Two things in this finding cannot be fixed by any file, and are recorded rather than papered over:

1. **Private vulnerability reporting has to be enabled** in Settings → Advanced Security.
2. **The repository description has to be set.**

The gate will WARN about both on every CI run until somebody clicks them, and will never fail. That is a
gate reporting a finding on every run, which is the thing Slices 18 and 19 argued against — accepted
here because the finding is *about* something outside the tree, so no commit could clear it, and a WARN
that persists until a checkbox moves is exactly as loud as that deserves.

The documents are true whichever way the Issues tab and the wiki are left. That is the point of the
policy-not-platform rule: this delivery cannot change a setting, so nothing in it claims one.

### Build and test

```bash
bash scripts/check_repository.sh
#    expect: 4 gates, "repository governance passed", exit 0. With a token in the environment the
#    fourth gate is advisory and WILL report warnings — private vulnerability reporting off, no
#    description, the wiki on — and must still exit 0. That is the assertion that matters here:
#    a finding about a settings page must not be able to fail a build.

bash scripts/check_repository.sh --offline
#    expect: 3 gates plus a SKIP, exit 0. This is the half that blocks, in isolation.

bash scripts/check_tree.sh
#    expect: 5 gates, "tree hygiene passed.", exit 0. Two new authored files land in its scope
#    (SECURITY.md, scripts/check_repository.sh) plus the edited documents, so the count rises.

bash scripts/ci_local.sh --with-all
#    expect: 8 numbered gates now; governance is the new second one. Gates 3-8 are unchanged.

dotnet test
#    expect: 996 total, 0 failed, 981 succeeded, 15 skipped. UNCHANGED — no C#, no .csproj, no
#    migration and no Program.cs is touched in this slice. If this number moves, the cause is not
#    here.

MYRESTAURANT_E2E=1 dotnet test tests/MyRestaurant.EndToEnd.Tests
#    expect: 15 passed, 0 skipped. Unchanged.
```

`git add scripts/check_repository.sh SECURITY.md` is **not optional**. `ci_local.sh`, CI's
`shell-scripts` job and `check_tree.sh` all enumerate with `git ls-files`, so an untracked new file is
silently unchecked by every one of them — and `check_repository.sh` asks `git ls-files --error-unmatch`
about `SECURITY.md` specifically, so an unstaged policy file fails its own gate.

### What was verified here

No .NET SDK in the sandbox, and nothing in this slice compiles — but everything in it is a shell script
or a document, and both were executed rather than reasoned about:

- **The finding was measured, not inferred.** Every number in the block above came from the live GitHub
  API against `kusl/myrestaurant`: the repository object, the private-vulnerability-reporting endpoint,
  the community profile, and a 404 probe for `SECURITY.md` at all three paths GitHub reads.
- **`scripts/check_repository.sh` was run against a real git tree**, both halves, in four states: the
  delivered tree (passes, 3 blocking gates clean); `--offline`; with `SECURITY.md` deleted (fails, and
  names why a repository that refuses pull requests still owes a channel); and with the forbidden
  sentence re-introduced into `CONTRIBUTING.md` (fails, by file and line).
- **Sensitivity of each blocking gate proven individually**, because a gate that passes by asserting
  nothing is worthless: each of the three cross-reference edges broken in turn, `§17` removed from the
  policy, the reporting-channel phrase removed, the policy emptied.
- **The exemption proven to be narrow**: the forbidden sentence planted in a non-exempt document is
  reported; the same sentence in this file and in `DOCUMENTATION_REVIEW.md` is not.
- `bash -n` and `shellcheck` at **both** `--severity=warning` and `--severity=style`, clean, which keeps
  `ci.yml`'s claim that every script in this tree is style-clean true. The existing eight scripts were
  baselined clean first, so a finding would have been attributable.
- `scripts/check_tree.sh` run over the delivered tree, including every edited document and both new
  files: 5 gates, 0 findings. The new prose was checked for separator lines and whitespace-only lines
  rather than assumed innocent.
- `.github/workflows/ci.yml` parsed with PyYAML, job list and the `governance` job's permissions read
  back out of the parsed document rather than eyeballed.
- Every documentation edit applied by exact-match replacement with an assertion that the anchor appears
  **exactly once**, so nothing was edited by position. `OPERATIONS.md`'s section numbering read back
  afterwards, because §16 had to land at the end: its section numbers are referenced from the
  specification and the ADRs, so a renumber would have been a silent break.

## M6 Slice 21 — two green gates that were never green

Slice 20 closed on a table of results in which everything passed. Two of those rows were reporting on a
workstation, and the two CI jobs that have no workstation equivalent — `boot-smoke`, which boots the
production image, and `end-to-end`, which drives Chromium — were red on the same push. They stayed red.
Neither failure was in the thing its message named.

| Job | What it said | Where the defect was |
| --- | --- | --- |
| `boot smoke (container image)` | the database did not answer `pg_isready` | which container engine the script chose (**F-43**) |
| `end to end (Playwright)` | `Assert.Single()`: the collection was empty | when the harness was allowed to read the screen (**F-44**) |

Both are the same shape at one remove: a check that passed for years on one machine and could not pass on
another, because what it actually asserted was narrower than what it appeared to assert.

### F-43 — the engine the container did not belong to

`scripts/backup.sh` and `scripts/restore_drill.sh` both opened with the same four lines:

```bash
if command -v podman >/dev/null 2>&1; then
    ENGINE="podman"
elif command -v docker >/dev/null 2>&1; then
    ENGINE="docker"
```

podman first because ADR-0004 makes rootless Podman canonical, which is right — and on a host with one
engine the block is also correct, which is why it survived. A GitHub Actions `ubuntu-24.04` runner is not
that host. Its image runs `scripts/build/install-container-tools.sh`, which installs a pinned static
podman bundle into `/usr/local/bin` alongside buildah and skopeo, next to a working Docker daemon. So
`command -v podman` succeeds there, and every container in the `boot-smoke` job — the service database,
the image under test — belongs to Docker.

What the job printed:

```
[backup] using POSTGRES_CONTAINER='f35d0ec9fc2d3f…'.
[backup] error: 'f35d0ec9fc2d3f…' did not answer pg_isready for user 'myrestaurant' database 'myrestaurant'.
```

`podman exec <a docker container id>` fails with "no such container". The script had one message for
every way that command could fail, so a fault entirely in engine selection was reported as a database
that would not answer for its own credentials — and reported it two steps after `/healthz/ready` had
returned 200 from an application talking to that very database. **A diagnostic that names the wrong
subsystem is worse than no diagnostic**, because it is followed.

Three changes, and the order matters:

1. **`CONTAINER_ENGINE`** is honoured by both scripts and set to `docker` on the two `boot-smoke` steps.
   Explicit, visible in the job, and the half a reader will find first.
2. **The container chooses the engine.** When `POSTGRES_CONTAINER` is set, `backup.sh` asks each
   available engine `container inspect` and uses the one that answers. This is not a guess dressed up as
   a heuristic: the only reason the script needs an engine is to reach one named container, and whether a
   given engine can see that container is a fact. `container inspect` rather than `ps --filter name=`
   because CI passes an id, and `ps` filters do not match ids. Discovery without a name works the same
   way, per engine, and still **refuses** on ambiguity rather than picking — F-38's rule, unchanged.
3. **The two conditions are said separately.** "Knows it but it is not running" and "did not answer
   `pg_isready` for these credentials" are fixed in different places, so they are now different lines,
   and both name the engine they were asked of.

`restore_drill.sh` gets the variable but not the inference, and the asymmetry is the point: the drill
creates its own scratch container, so there is nothing to infer from. On the runner that also costs a
second pull of `postgres:17-alpine` into podman's store while Docker already holds it — slow rather than
wrong, and one more reason the job pins the engine.

### F-44 — a barrier that was satisfied by the first paint

§16.3 scenario 10 closes a sitting, walks back to `/counter`, and asserts the table left the floor and
appeared under "Settled today". It failed on the second half:

```
Assert.Single() Failure: The collection did not contain any matching items
Collection: []
```

`ListRecentlyClosedSittingsAsync` filters `closed_at >= now − 12h` against a sitting closed seconds
earlier, so the query was never in question. The harness was reading the board before either list
existed.

`WaitForBoardAsync` waited on `section.counter-board` becoming visible. **That element is present in
every state of the component**, including:

```razor
@if (!_loaded)
{
    <p class="lede">Loading the floor…</p>
}
```

So the wait was satisfied by the first paint and asserted nothing at all. And the state it failed to
wait past is not the prerender — prerendering runs the whole lifecycle before it emits, so the first HTML
a browser receives is fully loaded, which is exactly why this passed locally for as long as it did. It is
the **hand-over**. `blazor.web.js` opens the circuit, the component is constructed again from nothing,
`ComponentBase` renders the moment `OnInitializedAsync` yields, and the DOM returns to "Loading the
floor…" for as long as two queries take. Milliseconds on a workstation. Long enough on a loaded runner.

The failure is quiet in a way worth naming. The assertion one line above the failing one is

```csharp
Assert.DoesNotContain(tableLabel, floor.OpenTableLabels);
```

and an empty list satisfies it. So the reading that produced the failure had already produced a pass, for
the wrong reason, out of the same empty screen.

`CounterBoard.razor` now publishes what the other four live surfaces publish, plus one more bit:

| Attribute | From | Says |
| --- | --- | --- |
| `data-live` | `RendererInfo.IsInteractive` | a circuit produced this markup |
| `data-loaded` | `_loaded` | §11.3's two queries have answered |

and `BoardSurfaceSelector` demands both. **Either alone is wrong, in opposite directions.** `data-live`
by itself steers a reader *to* the circuit's first render — the one instant when neither list is in the
document — so it would have made this worse rather than better. `data-loaded` by itself matches the
prerendered markup, which is loaded and inert: correct as of the request and never again, on the one
screen in the application whose entire purpose is a number that moves while somebody stands reading it.

Until now `/counter` was also the only one of the five live surfaces that published nothing, so nothing
anywhere asserted a circuit was behind it.

### What is deliberately not in this slice

**`data-loaded` on the other four surfaces.** `KitchenBoard`, `CounterSitting`, `TableOrderSurface` and
`TableDisplay` all publish `data-live` and none publishes a loaded bit, so all four carry the same latent
race. They pass today because their callers go on to wait for specific content — a bill line, a badge, a
menu item — and that wait incidentally waits out the reload. The board is where it bites because
`ReadFloorAsync` asks about membership of a list, and absence is indistinguishable from a list that has
not rendered.

That is a real finding, and it is recorded rather than fixed here: four surfaces is ~4,000 lines of Razor
edited against a race none of them is currently losing, in the same delivery as two failures that are
losing. Scenario 10 is the evidence that the class is real; a scenario that fails is the evidence needed
to justify the other four.

### Build and test

```bash
bash scripts/check_tree.sh
#    expect: 5 gates, "tree hygiene passed.", exit 0. No new authored file lands in its scope —
#    docs/_append/ is a delivery convenience, merged and removed — so the count is unchanged.

bash scripts/ci_local.sh
#    expect: 8 numbered gates, green. Gate 3 (shell scripts) is the one that matters here: both
#    edited scripts must pass bash -n and shellcheck --severity=warning.

dotnet build MyRestaurant.slnx -c Release -p:ContinuousIntegrationBuild=true
#    expect: all seven projects, 0 errors. CounterBoard.razor is the only compiled file that changed
#    on the src side, and Razor is where a delivery like this would break.

dotnet test
#    expect: 996 total, 0 failed, 981 succeeded, 15 skipped. UNCHANGED. No test is added, renamed or
#    moved; the harness edit changes a selector and a message. If this number moves, the cause is
#    not here.

MYRESTAURANT_E2E=1 dotnet test tests/MyRestaurant.EndToEnd.Tests
#    expect: 15 passed, 0 skipped. Counter_ClosesSitting_TableFlipsToSettledAndTotalsMatch is the
#    one that was failing in CI and has never failed locally, so a local pass proves the selector
#    still matches — not that the race is closed. The runner proves that.
```

The drill and the backup cannot be rehearsed against the CI topology from a workstation, so the two
things worth doing locally are the ones that would catch a typo:

```bash
BACKUP_DIRECTORY=/tmp/mr-backup-check bash scripts/backup.sh --no-keys
#    expect: on a dev stack, the postgres container found and named with the engine it was found
#    through — "[backup] using 'myrestaurant_postgres_1', discovered via podman." That line is new
#    and it is the whole of F-43's fix reporting itself.

CONTAINER_ENGINE=nosuchengine bash scripts/backup.sh --no-keys
#    expect: exit 1, "CONTAINER_ENGINE='nosuchengine' is not on PATH." A bad override must fail on
#    the override rather than fall back to guessing, which is the behaviour this slice removed.
```

### Ledger

| Shape | Finding | What was wrong |
| --- | --- | --- |
| a script correct on one host and wrong on another, reporting the wrong subsystem | **F-43** | engine chosen by `PATH` order rather than by the container it had to reach |
| a test barrier satisfied by markup present in every state | **F-44** | `section.counter-board` exists while loading, so the wait asserted nothing |

Both rows belong to a category this ledger has recorded twice before — a check whose *name* was true and
whose *content* was narrower than its name. F-40's separator gate and F-42's governance gate were both
about layers nothing then looked at. These two are about looking at the right layer and asking it the
wrong question, which is harder to notice because the gate is green.

## M6 Slice 22 — what leaves this machine

Slice 21 fixed two CI jobs and shipped no ledger. That is where this one starts, because the rule it
broke is the rule this project is built on.

| | Finding | Shape |
| --- | --- | --- |
| **F-45** | the image build's context was the entire working tree | a file that should have existed and never had |
| **F-46** | a document asserted a *package* setting, and the gate built to forbid exactly that reported "none" | a rule enforced as a list of examples |
| — | F-43 and F-44 shipped without their rows in this file, in `DOCUMENTATION_REVIEW.md` or in Appendix A | the atomic-documentation rule, broken by a delivery that invoked it |

Both findings came out of the same reading — F-39's habit, now three for three: **the moment before
publishing is a distinct review, because publishing changes who the audience is.** One layer further
out each time. F-39 asked what a tag makes true about the *program*. F-42 asked what it makes true
about the *repository*. This slice asked what it makes true about the *artefact* — what goes into the
image, and who can get the image back out — and found that neither question had ever been asked.

### First: the debt Slice 21 left

`docs/_append/BUILD_PROGRESS-M6-Slice-21.md` was committed and never merged. F-43 and F-44 existed as
working code, as an `OPERATIONS.md` §6 paragraph, and nowhere else — not in this file, not in Group E,
not in Appendix A. S§18 says a behaviour change lands in one commit with its ledger and specification
edits, and a delivery that quoted that rule in its own notes did not follow it.

The mechanism is the interesting part rather than the lapse. Eleven append files before this one went
the same way and eventually got merged; the twelfth did not, because merging is a step somebody has to
remember after the archive is already extracted and the tests are already green. The reason the
mechanism existed at all was that this file is 434 KiB and regenerating it from scratch risks losing
history that cannot be reconstructed — a real concern, but the wrong solution to it. **From this slice
the file ships whole**, assembled from the existing bytes rather than rewritten, so there is no second
artefact and nothing to forget. `docs/_append/` goes with it.

Slice 21's entry is immediately above this one, unedited.

### F-45 — the build context was the whole tree

`Containerfile` has said `COPY . .` since M1, with the repository root as the context. There has never
been a `.dockerignore` in this tree, or a `.containerignore`, or an `--ignorefile` anywhere in
`compose.yaml`, `run.sh` or either workflow.

Measured, against a fresh clone with nothing built:

```
458 files, 31,148,997 bytes handed to the builder
    docs/llm/   16 MB      committed context dumps and delivery archives
    .git/       11 MB      history the build cannot read and would not use
    src/         1.6 MB    the only part `dotnet publish` opens
```

Eighty-seven per cent of it is material the build cannot use, which is the harmless half of the
finding and the half that would have been noticed eventually, because it costs seconds on every push.

The other half had been sitting there the whole time. **A build context is not a commit**, and nothing
in this project had ever looked at the difference. `.gitignore` names `.env`, `.dataprotection/`, every
`*.dump` and every `*-dataprotection.tar`, and it is *correct* about all of them — its own comment on
that last pattern reads "never commit one", and it is right. It protected nothing here. Docker and
Podman do not read `.gitignore`; they read an ignore-file that did not exist, so every one of those
paths was copied into the build stage on every `podman-compose --profile production up -d --build`.

The ordering is not hypothetical. It is the documented upgrade procedure:

```
OPERATIONS §12
  1. scripts/backup.sh      writes myrestaurant-<stamp>-dataprotection.tar
  2. podman-compose --profile production up -d --build
```

§8 calls that tar "the key material, in the clear". Step 1 creates it; step 2 hands it to the image
builder, on any `BACKUP_DIRECTORY` inside the tree — and `.gitignore` anticipates exactly that
placement in the comment above its `backups/` entry. CI escaped by accident of step ordering alone:
`boot-smoke` runs `docker build` before `backup.sh` writes `ci-backups/`, and if those two steps had
ever been reordered for any reason, nothing would have said so.

And no gate could have caught it, which is the part worth keeping. `check_tree.sh` enumerates with
`git ls-files`. `check_repository.sh` enumerates with `git ls-files`. Every file at issue is
git-ignored **on purpose**, and being git-ignored is precisely what made it dangerous.

#### An allow-list, and why not the other kind

```
*
!.editorconfig
!Directory.Build.props
!Directory.Packages.props
!global.json
!src
src/**/bin
src/**/obj
```

Result: **169 files, 1,615,409 bytes.** Every path in the publish graph survives — the three project
files, `Migrations/*.sql`, `wwwroot/`, `appsettings*.json`, every `.razor` and `.cs` under `src` — and
nothing else does. `.editorconfig` is on the list deliberately, so `EnforceCodeStyleInBuild` applies
the same analyzer set inside the image build as it does in CI.

A deny-list was the obvious alternative and is the wrong answer, for a reason this finding demonstrates
rather than asserts: **a deny-list is what failed.** `.gitignore` is a well-maintained deny-list, it was
already correct, and being correct did not help, because the question is not "what is wrong today". A
deny-list has to be extended for tomorrow's untracked secret by somebody who remembers to. An
allow-list has to be extended for tomorrow's *source directory*, by a build that fails immediately when
it is not — and the failure is loud, local, and self-describing. Same argument as `RECORD_FILES` being
literal paths in Slice 20, and the same argument as `is_authored_text` being one decision in Slice 19:
scope decided in one place, auditable, and unable to widen by accident.

One file rather than two: Podman reads `.containerignore` or `.dockerignore` and prefers the former
when both exist; Docker/BuildKit reads `.dockerignore` at the context root unless a
`Containerfile.dockerignore` exists, which this tree does not ship. So the single root `.dockerignore`
is read by both engines, and ADR-0004's canonical Podman and CI's Docker cannot disagree about it.

#### The row names something executable, and it runs where the risk is

F-38's lesson, fourth application, again unprompted. An ignore-file is an *instruction to a tool*, and
instructions fail quietly: rename it, shadow it with a `.containerignore`, pass `--ignorefile`, build
from a parent directory — the build still succeeds, and the only symptom is that it took longer.

So the allow-list is stated twice, on purpose. Once as the instruction, and once as an assertion in
`Containerfile` immediately after `COPY . .`:

```
BUILD CONTEXT REJECTED — .dockerignore did not take effect (see F-45).
  not allowed here:  CONTRIBUTING.md Caddyfile LICENSE README.md docs scripts tests .env .git
```

It fails unless the context root is *exactly* the allowed set, unless every required path is present,
and unless no `bin` or `obj` survives under `src`. The first condition is the one that matters: it
catches the top-level entry nobody has thought about yet, which is the population this finding was
actually about.

The placement is the deliberate part. **This guard runs wherever a build runs**, which is the operator's
workstation and not only CI — and the machine most likely to have a key ring sitting in the tree is the
operator's, because CI has never taken a backup it then rebuilt over. A gate that only ran in CI would
have been a gate pointed away from the risk.

### F-46 — the rule was right and the enforcement was a list

Slice 20's gate 3 exists so that no tracked file can assert a GitHub setting. It landed green, it has
been green ever since, and it was wrong on the day it landed. `docs/OPERATIONS.md` §14 told a reader, in
the indicative, that the published images carried a particular visibility and that pulling one therefore
needed no credentials — in the paragraph that tells an operator how to deploy from the registry.

Three things were wrong with that sentence at once:

- It is a claim about a **package** settings page. Gate 3's list enumerated the *repository* page —
  issues, pull requests, discussions, the wiki — and a package's visibility is a genuinely separate
  switch from its repository's.
- The package **did not exist**. There are no tags on this repository and no releases; the sentence
  described the future state of an object nobody had created.
- GitHub's own documentation contradicts itself about which way that switch falls for a `GITHUB_TOKEN`
  publish — one page says a package inherits the repository's visibility, another says it inherits
  permissions *but not* visibility. So the sentence was not merely unverifiable from inside the tree;
  nobody could say whether it was true.

The correct repair is the one F-42 prescribed and this file has already argued for once: state the
intention, and name where the switch lives. §14 now says these images are *meant* to pull without a
login and points an operator who meets a 401 at Package settings → *Change visibility*. That sentence
is true whichever way the checkbox falls, and it is useful in the case where the checkbox is wrong,
which the previous one was not.

#### The second half: the report never reached the run that matters

The gate's advisory half is the only thing in this project that looks at the platform layer at all. It
did not run on a release. A called workflow sees only the secrets it is handed, and `release.yml` used
`uses: ./.github/workflows/ci.yml` with no `secrets:` block — so `ADMIN_READ_TOKEN` was empty there, the
half skipped silently, and **the one run that creates a package produced no report about packages.**

`release.yml` now passes that secret by name. Not `secrets: inherit`: one named secret is a smaller
statement than all of them, and `ci.yml` declaring it `required: false` under `workflow_call` documents
the dependency where a reader of either file will find it. A fork's pull request will not carry the
secret, the half degrades to a skip, and nothing goes red — which is the behaviour Slice 20 ruled for
and this change preserves.

#### What the gate learned

The patterns are widened, and that is the smaller half. The larger half is that the list now sits
beside the rule with the reasoning attached, in the script and in S§16.4, because **a rule stated as a
rule and enforced as a list of examples is enforced as a list of examples.** That is the third time in
two slices: F-43's engine selection was named for what it needed and asked `PATH` instead; F-44's
barrier was named for a loaded board and matched a loading one; this one was named for repository
settings and meant six phrasings. All three were green while being narrower than their names.

### Considered and rejected

**A `.dockerignore` gate in `check_tree.sh`.** Tempting — it is cheap, and `check_tree.sh` runs in two
seconds against `boot-smoke`'s several minutes. Rejected because it would be a *second* place deciding
what belongs in a build context, and F-41 is this project's own finding about exactly that: when two
checks share a file set, the set is defined once. The Containerfile guard is not a duplicate of the
ignore-file, it is the assertion that the ignore-file took effect, and those are different jobs. A third
opinion in a fifth gate would be a duplicate.

**Exempting `docs/TECHNICAL_SPECIFICATION.md` so Appendix A could quote the offending sentence.** The
gate caught the F-46 row quoting the very sentence F-46 is about, which is the gate working. Slice 20
kept the exemption list at three files and paraphrased in `_CHANGES.md` rather than widen it; the same
answer applies here, more strongly — the specification is the document that makes normative claims, so
it is the last file in this tree worth exempting. Appendix A paraphrases.

**Adding `LICENSE` to the build context.** An AGPL image arguably ought to carry its licence text. It
does not carry it today either — the runtime stage copies only `/app/publish` — so including it in the
allow-list would have changed nothing about the image while making the list look like it had a reason
nobody could check. §11.9's `/source` is how this project discharges AGPL §13, and that is unaffected.
Recorded as a separate question, not answered here.

**Making the platform half of the governance gate blocking now that it reaches releases.** No. Slice
20's ruling stands and the argument has not changed: a fork's settings are the fork's business.

### Build and test

```bash
bash scripts/check_tree.sh
#    expect: 5 gates, "tree hygiene passed.", exit 0. The authored-text count rises by one —
#    .dockerignore is a new tracked text file and lands in scope — and docs/_append/ leaves it.

bash scripts/check_repository.sh --offline
#    expect: 3 gates plus a SKIP, exit 0. Gate 3 is the one this slice changes; it must report
#    "none" AFTER the OPERATIONS §14 edit and would report docs/OPERATIONS.md before it.

bash scripts/ci_local.sh --with-all
#    expect: 8 numbered gates, unchanged in number and order.

dotnet test
#    expect: 996 total, 0 failed. UNCHANGED. No C#, no .csproj, no migration, no Program.cs, no
#    Razor is touched here. If this number moves, the cause is not this slice.

MYRESTAURANT_E2E=1 dotnet test tests/MyRestaurant.EndToEnd.Tests
#    expect: 15 passed, 0 skipped. Unchanged.

podman build --file Containerfile --tag myrestaurant_web:local .
#    expect: an early line reading "build context accepted: 169 file(s)", then the usual publish.
#    This is the assertion that did not exist before. Compare the "Sending build context" figure
#    against a build from before this slice if you want the size difference in front of you.
```

The container build is the one worth running by hand, because it is the only thing here that exercises
the new file. If it prints `BUILD CONTEXT REJECTED`, read the list it prints: that is the difference
between what `.dockerignore` says and what the engine actually handed over.

### What was verified here

No .NET SDK and no container engine in the sandbox, so nothing was reasoned about that could be
executed instead:

- **The 31 MB figure is measured, not estimated**, from a fresh `git clone` of `kusl/myrestaurant`:
  458 files, 31,148,997 bytes, `docs/llm/` 16 MB, `.git/` 11 MB.
- **`.dockerignore` was evaluated against the real tree** with a faithful implementation of the
  documented matching rules — last matching pattern wins, `**` spanning zero or more segments, a
  pattern matching a path or any ancestor. Result: 169 files, 1,615,409 bytes, and every path in the
  publish graph individually asserted present (`Migrations/0001_initial_schema.sql`, `wwwroot/app.css`,
  `appsettings.json`, `Components/App.razor`, all three `.csproj`).
- **Sensitivity proven by planting the real hazards**: `.env`, `.dataprotection/key-abc.xml`, a
  `.dump`, a `-dataprotection.tar`, a stale `obj/project.assets.json` carrying a host `.nuget` path,
  and a built `.dll` under `src/**/bin`. All six excluded with the file present; all six confirmed
  copied with it absent.
- **The `Containerfile` guard was executed**, in `dash` rather than bash — the SDK image's `/bin/sh` —
  after joining its line continuations the way the Dockerfile parser does. `dash -n` clean on all three
  `RUN` bodies. It accepts the real 169-file context and rejects eight separately constructed damage
  cases: a leaked `.env`, a leaked `.git`, `docs` + `tests` + `README.md` (the no-ignore-file case), a
  `backups/` holding a key-ring tar, an `obj/` under `src`, a missing `.csproj`, a missing `global.json`,
  and the current tree as it stands. Every one exits 1 and names what it found.
- **Ignore-file precedence checked against the sources**, not from memory: Docker's build documentation
  for the context root and the `<Dockerfile>.dockerignore` override, and `podman-build.1.md.in` for
  `.containerignore`/`.dockerignore` and which wins when both exist.
- **Each new gate-3 pattern fired individually**, planted one at a time into `docs/OPERATIONS.md` and
  reverted between runs — seven true positives. And four legitimate policy phrasings were planted to
  prove the list does not over-fire, including the replacement sentence §14 now carries and an
  instruction to *set* the visibility, which must remain sayable.
- **Both workflows parsed with PyYAML** and the `workflow_call` secret declaration, the `verify` job's
  `secrets:` block and the governance job's `env:` read back out of the parsed documents rather than
  eyeballed.
- **Every documentation edit applied by exact-match replacement with an assertion that the anchor
  occurs exactly once**, so nothing was edited by position, and `docs/BUILD_PROGRESS.md` was assembled
  from its existing bytes plus two appended sections rather than rewritten.


---

## M6 Slice 23 — the bit every live surface owed, and the enumeration that made "every" wrong

**F-47 · F-48 · S v1.8 · docs/TECHNICAL_SPECIFICATION.md §11.10 (new), §16.4**

Slice 21 fixed one surface and wrote down that four more had the same problem. This slice went to fix
those four and found that there were never four, that one of the ones nobody had counted had been
publishing nothing at all since M3, and that on one of the four the bit being copied would have been
true and useless. Then, while bumping the specification, it found the specification's header disagreeing
with the specification's changelog for the second time in seven versions.

### What was carried forward, and what it turned out to say

F-44's row is unusually explicit about its own scope, which is why this slice exists:

> the other four live surfaces publish `data-live` with no loaded bit and carry the same latent race,
> passing because their callers go on to wait for specific content, which waits out the reload
> incidentally. Four surfaces is ~4,000 lines of Razor edited against a race none of them is currently
> losing; scenario 10 is the evidence the class is real, and a scenario that fails is the evidence
> needed for the rest.

That was the right call at the time and it is still the right call. What was wrong was the noun. The
first thing this slice did was not to open those four files but to ask where the number four came from,
and the answer was that it came from a doc comment, which had it from another doc comment.

### F-47, first half — there is no list of five

`App.razor` is eleven lines of decision:

```razor
private IComponentRenderMode? RenderModeForPage
    => HttpContext.AcceptsInteractiveRouting() ? InteractiveServer : null;
```

Every routable page is interactive unless it carries `[ExcludeFromInteractiveRouting]`. Computed rather
than remembered, that is **six pages** — `/`, `/counter`, `/counter/sittings/{id}`, `/kitchen`,
`/display/{table}`, `/table` — plus `TableOrderSurface`, which carries no `@page` and no `@rendermode`
of its own and is interactive because `/table/{id}` hosts it that way. Seven components. The tree said
five, in five separate doc comments, and one of them was `CounterBoard`'s claim to have been *"the only
one of the five live surfaces that did not say"*.

**`/table` had been live since M3 and published nothing.** No id, no `data-live`, no `data-loaded`. It
has a `_loaded` field and a "Looking up your tables…" branch, and the state below that branch — the one
the hand-over renders before the query answers — is an empty list, which is character-for-character the
state that means *you are not seated at a table right now*. Nothing was failing, because no §16.3
scenario reads that page. It was invisible for the only reason that matters: nobody had asked.

`Home.razor` is the interesting non-case. It is interactive, it has an async `OnInitializedAsync`, and
it correctly publishes neither bit, because it has no loading state to publish — `_needsSetup` defaults
to the answer that renders nothing, so there is no render in which the page is incomplete. It is in the
contract test's expected set anyway. The set is what the rule produces; whether a member of it owes the
bits is a separate question with its own answer.

### F-47, second half — the display's bit is not a loading bit

The obvious way to close this slice would have been to copy `CounterBoard`'s property into four files.
On `/display/{table}` that would have produced an attribute that was `true` whenever its element
existed.

`TableDisplay` renders `id="table-display-surface"` from two branches: the QR, and a
"Preparing the join code…" card for when `DescribeCurrentAsync` came back empty. The second is transient
rather than fatal and is deliberately not an error page — so it is **fully resolved, fully interactive,
and has no QR on it**. A `_loaded`-shaped bit reads `true` there. The barrier
`#table-display-surface[data-live='true']` already matched it, which is why scenario 2's failure mode,
when the join secret is wrong or the table is inactive, is sixty seconds inside `ReadJoinQrPathAsync`
and a message about a missing `d` attribute.

So the rule had to be written at the level where the six surfaces actually agree, which is the question
rather than the expression:

> **`data-loaded`** answers *does this markup have what the surface renders itself for*.

Five surfaces answer that with `_loaded`. The display answers it with `_loaded && _qr is not null`, and
that is the rule applied rather than an exception to it. §11.10 says so, and the contract test pins
`data-live`'s expression exactly while pinning only `data-loaded`'s *shape*, for exactly this reason.

**Both halves are the same finding**, and it is F-46's, one more time: a rule stated as a rule and
enforced as a list of examples is enforced as a list of examples. F-43, F-44, F-46 and now F-47 are four
checks in three slices whose names were true and whose contents were narrower than their names, and all
four were green throughout.

### What landed

| File | Change |
| --- | --- |
| `Components/Pages/Table/TableArea.razor` | **the sixth surface.** `id="table-area-surface"`, both bits, and the two properties |
| `Components/Pages/Display/TableDisplay.razor` | `_loaded`, latched on all four paths out of `OnInitializedAsync`; `data-loaded` on both surface branches, predicate `_loaded && _qr is not null` |
| `Components/Pages/Kitchen/KitchenBoard.razor` | `data-loaded` beside the live bit and the two §10.3 counts |
| `Components/Pages/Counter/CounterSitting.razor` | `data-loaded` |
| `Components/Pages/Table/TableOrderSurface.razor` | `data-loaded` |
| `Components/Pages/Counter/CounterBoard.razor` | doc comment only: the "only one of the five" sentence is retired and its wrongness recorded where it stood |
| `Harness/DisplayJourneys.cs` · `KitchenJourneys.cs` · `CounterJourneys.cs` · `TableOrderJourneys.cs` | selectors demand both bits; `DescribeSurfaceAsync` reports both; every failure message distinguishes *no circuit* from *circuit, still reading* |
| `Components/LiveSurfaceContractTests.cs` | **NEW.** Seven assertions, subject derived from the routing rule |
| `Documentation/SpecificationVersionTests.cs` | **NEW.** Two assertions — F-48 |

`Home.razor` is deliberately untouched. `js/display.js` is deliberately untouched: the staleness curtain
keys on `data-refresh-token`, which covers a circuit that died *later*, and these bits cover one that
never lived. Two mechanisms, two failure modes, no overlap.

### The gate, and why it reads text

`LiveSurfaceContractTests` asserts §11.10 against the Razor sources. The interactive set is **derived**
from `[ExcludeFromInteractiveRouting]` and from `@rendermode="InteractiveServer"` in any file's markup —
the second half being what a per-file rule would have got wrong, since `TableOrderSurface` is interactive
only because something else says so.

Seven assertions: the scan read the tree and classified it (it cannot pass vacuously — F-41); every
interactive surface with a loading state publishes each bit; the two are published the same number of
times per file (which is how a surface rendering its element from more than one branch is caught, and is
the assertion that catches `TableDisplay` before the change); `data-live` is answered by
`RendererInfo.IsInteractive` exactly; `data-loaded` comes from a named property rather than an inline
expression; and nothing which is not interactive publishes either — because on a static page `data-live`
is `false` on every render that will ever happen, which is an attribute shaped like an assertion and
empty of one.

It reads source text rather than rendering anything, and that is a decision rather than laziness. The
property under test is a property of the markup; a test renderer would need a DI container and a
database per surface to assert the same string; and the §16.3 scenarios already exercise these
attributes in a real browser. What a scenario cannot do is notice a **seventh** surface that nobody
wrote a scenario for, which is precisely how this survived four slices.

One list remains, and it is deliberate: the expected interactive set. It is compared against the set the
rule produces, so the two can only agree by both being right, and a new interactive page fails the test
until somebody adds it and thereby decides whether it owes the bits.

### F-48 — the specification's header, for the second time

While bumping the specification to v1.8 the header read **v1.6** and the newest changelog entry read
**v1.7**. The v1.3 entry corrects the identical drift from Slice 16, in its own words. So: found,
corrected, explained in the correcting document, and repeated seven versions later.

Recorded at full weight because of the second half rather than the first. A stale version number is not
worth a paragraph; a defect whose correction left nothing behind that runs is. The fix is two assertions
in `SpecificationVersionTests` — header matches newest entry, entries descend — and a refusal to grow a
third. No dates, no section numbers, no "is there an entry for this commit": those are judgements about
content, and a gate that reaches past what it can decide reports findings on correct trees (F-41).

### Considered and rejected

**A `check_tree.sh` gate for the live-surface contract.** Cheaper to run than `dotnet test` and wrong on
two counts. `check_tree.sh` asserts properties of *authored text as text* — encodings, separators, XML
and YAML well-formedness — and knows nothing about what a Razor file means; teaching it would give the
tree gate a second job and a second vocabulary. And F-41's rule cuts the other way here: gates that share
a file set must share one definition of it, and this test's file set is "components the routing rule
makes interactive", which git cannot compute.

**Publishing the pair on `Home.razor` for uniformity.** No. It has no render in which it is incomplete,
so `data-loaded` would be `true` from the first paint and would assert nothing — the display's problem in
its other direction. The contract is about surfaces with a loading state, and saying so is what keeps it
from becoming decoration.

**Fixing the other four live surfaces' latent race the way F-44 described it.** That is what this slice
set out to do, and doing exactly that would have shipped five files, left `/table` alone, and given the
display a bit that was always true. The reason it did not is the whole finding.

**Widening `js/display.js` to read `data-loaded`.** Rejected: it already handles the case these bits do
not, by a mechanism that works when the circuit dies mid-service rather than only at page load. Two
mechanisms for two failure modes is correct; one for both would be worse at each.

**A `REQUIREMENTS.md` edit.** No, on the v1.2 reasoning. §11.5 has said since v1.0 that a frozen screen
must not masquerade as a live one; this is that contract stated once for every surface instead of five
times for four of them. Mechanism catching up with intent, not new intent.

### Build and test

```bash
bash scripts/check_tree.sh
#    expect: 5 gates, "tree hygiene passed.", exit 0, and the header reading
#    "checking 315 authored text file(s) of 425 tracked" — two new .cs files in scope.

bash scripts/check_repository.sh --offline
#    expect: 3 gates plus a SKIP, exit 0. Unchanged: no tracked file gained a platform-state claim.

bash scripts/ci_local.sh --with-all
#    expect: 8 numbered gates, same number and same order as Slice 22.

dotnet test
#    expect: 1005 total, 0 failed. Was 996; nine new tests, seven in LiveSurfaceContractTests and
#    two in SpecificationVersionTests. No existing test is touched.

MYRESTAURANT_E2E=1 dotnet test tests/MyRestaurant.EndToEnd.Tests
#    expect: 15 passed, 0 skipped. The four barriers are strictly stricter, so any change here is a
#    real finding rather than a flake — see "where to look" in _CHANGES.md.
```

`podman build` is not in that list: nothing in the build context changed.

### What was verified here

No .NET SDK in the sandbox, so nothing was reasoned about that could be executed instead:

- **The interactive set was computed, not read.** A faithful implementation of the test's own scan was
  run over the tree: 48 `.razor` files, 32 statically routed pages, one island (`TableOrderSurface`),
  and an interactive set of exactly the seven expected names.
- **The gate was proven sensitive against the Slice 22 tree.** Run unchanged against the pre-slice
  sources it fails on `TableArea` (no live bit), on `CounterSitting`, `KitchenBoard`, `TableOrderSurface`
  and `TableArea` (no loaded bit), and on `TableDisplay` by counts — `2` live against `0` loaded, which
  is the only assertion that catches a surface with no `_loaded` field at all. Run against the delivered
  tree, all seven pass.
- **`SpecificationVersionTests` was proven sensitive the same way**: against the delivered spec the
  header parses to 1.8, the entries parse to 1.8 … 1.0 descending, and both assertions hold; against the
  Slice 22 spec the first fails with 1.6 against 1.7.
- **Every documentation and source edit was applied by exact-match replacement with an assertion that
  the anchor occurs exactly once**, so nothing was edited by position. `docs/BUILD_PROGRESS.md` is its
  existing bytes plus this one appended section.
- **Markup balance was checked before and after on every edited Razor file** and is byte-for-byte
  unchanged in structure — the two files a crude tag-matcher reports on (`ILogger<TableDisplay>`,
  `IReadOnlyList<OrderLineView>`) report identically before the slice, which is what makes them
  generic-type false positives rather than findings.
- **`.editorconfig` hygiene checked on every delivered file**: LF endings, final newline, no
  whitespace-only lines, no trailing whitespace, no context-dump separator.
- No shell script changed, so `bash -n` and shellcheck have nothing new to say.


---

## M6 Slice 24 — the policy nobody wrote, on the responses it never covered

**F-49 · S v1.9 · docs/TECHNICAL_SPECIFICATION.md §11.11 (new), §16.4, §17 · ADR-0013 (new) · REQUIREMENTS rev 5**

Slice 23 closed the live-surface contract and the specification's own header. This one went looking at
a layer nothing in this tree had ever described: what the application says about how a browser may use
the pages it serves.

The search took two minutes and returned nothing. Not one occurrence of `Content-Security-Policy`,
`frame-ancestors`, `nosniff` or `Referrer-Policy` in any source file, any document, `Caddyfile`,
`compose.yaml`, `run.sh` or either workflow. That is the finding as it looked at 09:00. It is not the
finding.

### F-49, first half — there was a policy, and it was not ours

`Program.cs` has said this since M1:

```csharp
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();
```

That parameterless call, read against `dotnet/aspnetcore` at `release/10.0`, installs an endpoint
convention:

```csharp
if ((options.ConfigureWebSocketAcceptContext is not null || !options.DisableWebSocketCompression) &&
    options.ContentSecurityFrameAncestorsPolicy != null)
{
    …
    headers.ContentSecurityPolicy = StringValues.Concat(
        headers.ContentSecurityPolicy, $"frame-ancestors {options.ContentSecurityFrameAncestorsPolicy}");
```

`ContentSecurityFrameAncestorsPolicy` defaults to `'self'` and `DisableWebSocketCompression` defaults
to `false`, so the condition is true and has always been true here. **Every page this application has
ever served carried `Content-Security-Policy: frame-ancestors 'self'`.** The framework adds it because
WebSocket compression combined with cross-origin framing is an attack, and it declines to enable the
first without mitigating the second.

So the honest statement of the gap is not *there is no policy*. It is **there is a policy this project
cannot reason about**, and that is a shape this ledger had no row for. F-35, F-37 and F-39 were
capabilities a requirement assumed and no milestone claimed. F-45 was a file that should have existed
and never had. This is a control that existed, that worked, that nobody decided on, and whose reach
nobody in the project could have stated. Measured:

| | Covered by the framework's convention |
| --- | --- |
| rendered pages | yes — `frame-ancestors 'self'`, one directive |
| `wwwroot/app.css`, `js/*.js` | **no** |
| `/_framework/blazor.web.js` | **no** |
| `/healthz/live`, `/healthz/ready` | **no** |
| the §11.7 clock endpoint | **no** |
| `POST /account/sign-out` | **no** |
| a 404, a 429, the obligations redirect | **no** |

And it **appends**. `StringValues.Concat`, not assignment — so a policy written beside it would have
been *delivered* beside it, as two `Content-Security-Policy` values on one response, both enforced as
an intersection, and unreadable to anybody trying to work out where either came from. That detail is
what makes the correct move `ContentSecurityFrameAncestorsPolicy = null` rather than "add the rest".
The option's own remarks ask for exactly what replaces it — *"care must be taken to apply a policy in
this case whenever the first document is rendered"* — and what replaces it is stronger in both
directions: `frame-ancestors 'none'` instead of `'self'`, on every response instead of on some.
Compression is untouched; that is `DisableWebSocketCompression`, left at its default.

Everything else was genuinely absent, and two of the absences have names in this product rather than in
a checklist:

- **No `script-src`.** There are six `@((MarkupString)…)` sites in this tree — the §3.4 TOTP QR, the
  §4.3 display QR, the §4.5 counter QR, the §11.4 administration QR, and the two enrollment pages — and
  inline SVG can carry a `<script>` element. Razor escapes everything else, which is a reason for
  confidence and not a reason to skip the second line: a policy is what makes an injection that got
  past escaping *inert* rather than merely unlikely.
- **No `form-action`.** Antiforgery covers a forged request. It says nothing about where a real form
  posts to, which is the thing a `<base>` or markup injection changes.
- **No `Referrer-Policy`,** on an application whose §4.3 join token travels in a query string.
- **No `nosniff`,** on an application that serves static files.

### F-49, second half — the obvious policy would have killed every live surface

This is why the slice is a slice.

The natural `connect-src` for a single-origin application is `'self'`. It would have passed every unit
test anybody would write about the header. It would also have refused the Blazor circuit's WebSocket on
every plain-HTTP origin, because **`'self'` is an origin comparison** and `ws://host` is not the same
origin as `http://host`. CSP3 added a carve-out — *"`'self'` now matches `https:` and `wss:` variants of
the page's origin, even on pages whose scheme is `http`"* — which covers production, where the page is
https. It does not clearly cover `ws:` from an `http:` page, browsers have disagreed since 2015, and
MDN still carries the warning.

`http:` is exactly what a bare `dotnet run` serves and exactly what the §16.3 harness boots. Every §9
notification in this system arrives over that socket. So `connect-src` names both WebSocket origins
explicitly, derived from the request:

```
connect-src 'self' ws://{host} wss://{host}
```

The host is the one `PublicOriginMiddleware` has already normalized to a trusted public host (§3.3),
which is what makes writing a request value into a response header safe rather than clever — it is
never an attacker's string, it is the configured origin or a trusted pattern. A host CSP's `host-part`
grammar cannot express at all (an IPv6 address literal — brackets and colons are not in the grammar)
falls back to the bare `ws:` and `wss:` scheme sources, because a source expression the browser
discards fails as a blank screen with no cause named, and the configuration that reaches it is one
somebody typed on purpose on a machine they are sitting at.

**And the thing that would have caught the mistake already existed, built for something else.** A policy
that kills the circuit is a policy under which no surface ever reports `data-live='true'` — so Slice
23's six surfaces and four barriers would have failed all fifteen scenarios in a way that named the
cause. That is twice in two slices that the most useful property of a gate was a failure nobody built
it for.

### The policy, and the four lines that needed a reason

```
default-src 'self'; base-uri 'self'; object-src 'none'; frame-src 'none';
frame-ancestors 'none'; form-action 'self'; img-src 'self' data:;
style-src 'self' 'unsafe-inline'; script-src 'self';
connect-src 'self' ws://{host} wss://{host}
```

**`style-src 'unsafe-inline'` is a concession, and it is tied to the fact that earns it.** Twenty-one
components carry a scoped `<style>` block — 2,388 lines of CSS — and Blazor's own reconnection overlay
builds one at runtime with `innerHTML`, so a guest whose circuit drops would see an unstyled dialog
without it. Both halves matter and only one is visible in this tree, which is why the second is written
down: moving all twenty-one blocks into `app.css` would not be sufficient on its own.

**`img-src data:`** exists for one thing, `<link rel="icon" href="data:," />` in `App.razor`, which
stops browsers requesting `/favicon.ico` on every page of a restaurant's phone traffic. A
`<link rel=icon>` is an image fetch as far as CSP is concerned, so without the scheme every page load
would log a violation.

**`default-src 'self'` rather than `'none'`** is the one place F-45's allow-list ruling is deliberately
not applied, and the distinction is the interesting part. F-45 argued that where a domain permits an
allow-list, an allow-list is right, because the population that matters is always the thing nobody has
thought of yet — and it was arguing about a set *this project enumerates and controls*, the paths in a
build context, where the failure is loud, local and self-describing. A CSP fallback governs a set the
**browser** defines and extends with each new fetch destination. `'none'` there is an allow-list over
somebody else's vocabulary, and its failure mode is a screen in a working restaurant that quietly stops
showing something. `'self'` already denies every cross-origin origin, which is the threat; the
directives that should be narrower are taken to `'none'` by name.

**`Referrer-Policy: same-origin`** is here for a product reason rather than as hygiene: §4.3's token
rides in a query string, every current browser's default would not leak it, and a secret in a URL
protected by a browser default is protected by something no deployment here controls.

### Where the middleware sits, and why that is normative

After `PublicOriginMiddleware`, before everything else, writing on the way **in**.

- *After* the host normalizer, because the policy names the request's host, and before that middleware
  runs the host may still be the internal service address a tunnel left behind.
- *Before* anything that can produce a response, because the rate limiter's 429, `UseStaticFiles`
  answering by itself, the obligations redirect and the router's 404 all short-circuit.
- *On the way in*, because a header written after the inner pipeline returns is written after the body
  was flushed. The page that makes you look is always the one that went all the way through, which is
  what makes this the easy thing to get wrong and the hard thing to notice.

Nothing above that point can answer a request — `UseForwardedHeaders` and `PublicOriginMiddleware` both
rewrite and call on — so the two constraints pick exactly one position rather than a range.

Plain middleware rather than an endpoint convention or an endpoint filter, for the reason the convention
it replaces demonstrates: a convention reaches the endpoints it was attached to, and the responses most
in need of a `nosniff` are the static ones that never reach an endpoint at all.

### Three test classes, because they assert three different kinds of thing

`ResponseSecurityHeadersTests` (29) asserts what the header **says**: the directive set with no
repetition; that the value is one policy rather than two, because a stray comma splits it and the
result looks approximately right; that nothing admits a wildcard, `'unsafe-eval'`, `'wasm-unsafe-eval'`,
`'strict-dynamic'` or `'unsafe-hashes'`; and the two `connect-src` branches by name.

`SecurityHeadersMiddlewareTests` (8) asserts **when and to what**: that the headers are on the response
before the inner delegate runs — the property that makes them survive a short circuit — and that a 404,
a 429, a redirect and a 503 all carry them. If somebody ever rewrites this as `await next(); …set
headers…`, that first assertion fails while every page still looks right in a browser.

`ContentSecurityPolicyContractTests` (9) asserts that **the tree still fits inside the policy**, and it
is the one that will actually catch a regression. A Content Security Policy is the only configuration
in this project that becomes wrong by editing a file it does not mention: one `<script>` block added to
a Razor page and that page silently stops working in a browser while everything here stays green. So it
computes the category rather than reading a list (F-47's habit, sixth application) — scanning 48 Razor
files and the static assets for inline script, inline `on*` handlers, off-origin references, `url()` and
`@import` in the stylesheets, and `data:` URLs, with every count asserted non-zero first so it cannot
pass vacuously (F-41). It asserts the **concessions in both directions**, so that removing the
twenty-one `<style>` blocks or the favicon fails a test whose message says to *tighten* the policy —
nothing else would ever cause a concession to be dropped. And it reads `Program.cs` to assert the
wiring three ways, because none of the three is visible from the middleware's own file: that it is
installed, that it precedes everything that can answer, and that the framework's appending convention
is off.

### Considered and rejected

**Put the headers in `Caddyfile`.** Caddy is the dev profile's proxy and is optional in production
(§14.1), so the headers would be present exactly where they matter least — and absent from a bare
`dotnet run`, from the harness, and from any fork that fronts this differently.

**Put them in a Cloudflare Transform Rule.** This is the one this project has already ruled on twice
without knowing it. F-42 and F-46 established that a document must not assert platform state, because
nothing in the repository can verify it. A *control* that exists only as platform state is that mistake
with worse consequences: unverifiable, untestable, invisible in a diff, and absent from every fork.

**Leave the framework's `frame-ancestors 'self'` alone and add the rest beside it.** Two policies on one
response, both enforced, neither attributable — and `'none'` unreachable, and every static file still
unframed-by-nobody.

**A `<meta http-equiv="Content-Security-Policy">` in `App.razor`.** `frame-ancestors` is specified to
have no effect from a `meta` tag, which is the directive with the sharpest consequence here; and a meta
policy protects nothing that is not an HTML document, which excludes every static asset.

**`X-Frame-Options: DENY` beside `frame-ancestors`.** Every browser that can run a Blazor circuit
understands `frame-ancestors`, and a second spelling of one rule is a second thing to keep in step.

**`Strict-Transport-Security`.** Not the application's to send. It is the one header with a long
memory — a wrong `max-age` is not revocable from here — it is meaningless on the plain-HTTP hop between
the tunnel and this process, and its parameters are decisions about the operator's domain rather than
about this software. O§14 gains a note saying where it belongs and how to turn it on carefully.

**`Permissions-Policy`.** F-45's ruling applied and reaching the opposite conclusion, which is worth
recording as such. That ruling prefers an allow-list *where the domain permits one*, and
`Permissions-Policy` is a deny-list by construction: there is no forward-compatible way to say "and
nothing else". Meanwhile the two features this application does use are screen wake lock (§10.3, both
`js/display.js` and `js/kitchen.js`) and WebAuthn (§3.3), so a wrong entry is a kitchen board that
sleeps mid-service and is found by a cook rather than by a test. Recorded as a separate question, not
answered here.

**A `check_tree.sh` gate.** Same answer as Slice 23: that script asserts properties of authored text as
text and knows nothing about what a Razor file means, and F-41's rule is that gates sharing a file set
must share one definition of it.

### Build and test

```bash
bash scripts/check_tree.sh
#    expect: 5 gates, "tree hygiene passed.", exit 0. The authored-text count rises by SIX —
#    two source files, three test files, one ADR. If it does not move, the `git add` did not happen.

bash scripts/check_repository.sh --offline
#    expect: 3 gates plus a SKIP, exit 0. Gate 3 must still report "none": the OPERATIONS §14 note
#    added here names where a switch lives without asserting its value, which is the form F-46 ruled
#    for.

bash scripts/ci_local.sh --with-all
#    expect: 8 numbered gates, same number and same order as Slice 23.

dotnet build
#    worth running on its own first. Two new source files and one Program.cs edit; the render-mode
#    call gains a lambda, which is the only line in the tree whose overload resolution changed.

dotnet test
#    expect: 1051 total, 0 failed. Was 1005. Forty-six new: 29 + 8 + 9. No existing test is edited.

MYRESTAURANT_E2E=1 dotnet test tests/MyRestaurant.EndToEnd.Tests
#    expect: 15 passed, 0 skipped. THIS IS THE ONE THAT MATTERS. Every scenario now runs against a
#    real Chromium enforcing a real Content Security Policy over plain http. If connect-src were
#    wrong, no circuit would open and every barrier waiting on data-live='true' would time out.

curl -sSI http://localhost:8080/ | grep -i -e content-security -e x-content-type -e referrer
curl -sSI http://localhost:8080/app.css | grep -i -e content-security -e x-content-type -e referrer
#    the second is the interesting one: a static file is the response class an endpoint convention
#    misses, and it is the reason this is middleware.
```

### What was verified here

No .NET SDK and no browser in the sandbox, so nothing was reasoned about that could be executed
instead:

- **The framework's convention was read, not remembered.** `ServerComponentsEndpointOptions.cs` and
  `ServerRazorComponentsEndpointConventionBuilderExtensions.cs` fetched from `dotnet/aspnetcore` at
  `release/10.0`, and the enabling condition evaluated against this tree's actual call: both defaults
  hold, so the convention has always been active. `StringValues.Concat` rather than assignment
  confirmed from the same source.
- **The `ws:`/`'self'` question was settled against the specification** rather than from memory: CSP3's
  own "Changes from Level 2" extends `'self'` to the `https:` and `wss:` variants and says nothing about
  `ws:`, and MDN's `connect-src` page still carries the interop note. This is the fact the whole
  `connect-src` design turns on.
- **All 46 new assertions were executed** as faithful ports against the delivered tree, and all pass.
  The scan reports 48 `.razor` files, 5 `<script src>` (four helpers plus `blazor.web.js`), 1 stylesheet
  link, 21 inline `<style>` blocks, 7 resource references, exactly 1 `data:` URL, and zero inline
  scripts, zero `on*` handlers, zero off-origin references and zero `url(`/`@import` in the stylesheets.
- **Sensitivity proven by planting the damage**: an inline `<script>`, an inline `onclick`, a CDN
  `<script src="https://…">`, and a second `data:` URL. Each is caught by the assertion named for it,
  and each is a change that would have passed the whole suite before this slice.
- **Blazor's reconnection overlay was checked** rather than assumed — `DefaultReconnectDisplay.ts`
  creates a `<style>` element and assigns `innerHTML`, which is why `style-src 'unsafe-inline'` cannot be
  dropped by moving the components' blocks alone. `JSInitializers.ts` uses a dynamic `import()` of a
  same-origin path, which `script-src 'self'` admits; there is no `ImportMap`, no WebAssembly render
  mode and no inline event handler in this tree, so none of the keywords Microsoft's starter policy
  carries for a Blazor Web App is needed.
- **`SpecificationVersionTests` re-run against the edited specification**: header 1.9, entries
  1.9 … 1.0 descending, both assertions hold.
- **Governance gate 3 re-run over every delivered file**: two matches, both pre-existing lines in
  `docs/DOCUMENTATION_REVIEW.md`, which is on `RECORD_FILES` by literal path. Nothing new matches.
- **Every documentation edit applied by exact-match replacement with an assertion that the anchor
  occurs exactly once**, so nothing was edited by position. `docs/BUILD_PROGRESS.md` is its existing
  bytes plus this one appended section.
- **`.editorconfig` hygiene checked on every delivered file**: LF endings, final newline, no
  whitespace-only lines, no trailing whitespace, no context-dump separator. Brace, paren, bracket and
  string balance checked on all five C# files.
- No shell script, workflow, project file, migration or Razor component changed, so `bash -n`,
  shellcheck, the XML/YAML gates and the Razor compiler have nothing new to look at.

---

## M6 Slice 25 — the variable that never arrived (F-50)

`RESTAURANT_SOURCE_URL` is the one variable in §13 whose default is a claim about **who wrote the
program**. Every other default is a formatting preference or a dev convenience: get `USD` wrong and a
price renders in the wrong symbol; get `America/New_York` wrong and a timestamp is off by hours. Get
this one wrong and a modified program tells its users that its source lives in a repository that does
not contain the modifications.

`compose.yaml` did not pass it.

### What that actually did

The `web` service enumerates its environment key by key. There is no `env_file`, so the set of
variables a deployed container receives is exactly the set that block names, and nothing warns about
the difference between that set and the set the program reads. Measured against everything
`RestaurantOptions.FromConfiguration` binds — seventeen keys — exactly one was missing, and it was
this one.

So the documented procedure produced the opposite of its purpose. A fork operator modified the
program, published their source, set the variable in `.env` the way `OPERATIONS.md` §15 instructs —
that section is *titled* "your obligations, and the one variable that meets them" — ran
`podman-compose --profile production up -d`, and served every one of their users an AGPL §13 offer
naming **this** repository. The one place their modifications are not.

**Nothing failed.** The container started. `/healthz/ready` answered 200. `/source` rendered, named a
version and a revision, and offered a link. The link resolved. It resolved to a real repository
containing the wrong program. The whole failure is that a string was plausible.

### Why the suite did not catch it, and why that is not an oversight

All 1051 tests passed, legitimately. `RestaurantOptionsTests` covers the binding layer thoroughly —
defaults, the Argon2 floor, the §13 lower bounds, the clock-format spellings — and the binding layer
had no defect. Every test in the suite **constructs its own `RestaurantOptions`**, because that is what
a unit test does. Not one of them receives the object a container would hand over, because doing that
means starting a container.

That is the general shape, and it is worth stating in a form that outlives this instance: **a value
crosses a boundary this project does not test, and the far side of the boundary has a plausible
default.** Silence is then the *expected output* of the defect. It is F-38's shape one layer further
out — F-38 was four documents agreeing about something no code did; this is four documents agreeing
about something the code did correctly, where the transport between the operator and the code
discarded it.

No existing gate could have seen it either. `check_tree.sh` reads tracked files as text, and
`compose.yaml` is correct as text — well-formed YAML, LF endings, final newline, no separators.
`check_repository.sh` reads the platform. `boot-smoke` boots the image, but it boots it with the
environment CI hands it rather than through compose, and it asserts `/source` names the *commit*,
which was true.

### The fix, and the one interesting decision in it

One line in `compose.yaml`:

```yaml
      RESTAURANT_SOURCE_URL: ${RESTAURANT_SOURCE_URL:-}
```

**Empty default, not the upstream URL** — and the asymmetry with every line around it is the ruling
rather than an oversight. `RESTAURANT_NAME` restates `My Restaurant`; `RESTAURANT_CURRENCY_CODE`
restates `USD`; those are harmless because the compose file and the constant say the same thing about
a formatting preference and always will.

This one is different in a way that only shows up in a fork. `RestaurantOptions.DefaultSourceUrl`
is a fork's *natural first edit* — somebody who forks this program and changes the constant has done
something entirely reasonable and expects it to hold. A compose file that spelled this repository's
URL as its own default would silently override that edit, and the operator would be back to serving
the wrong offer having fixed the thing that looked like the problem. That is F-50 reintroduced one
layer up, in the file that had just been fixed. An empty value is read as unset by `ReadString`
(`IsNullOrWhiteSpace` → fallback), so the fallback stays decided in exactly one place.

The constant's doc comment now says so, because that comment is what a fork reads before editing it.

### The rule, stated where the next person will meet it

§13 previously listed variables and their meanings. It now also says what that section *is*: the table
describes what the **program** reads; `compose.yaml` and `.env.example` are restatements joined to it
only by somebody having written the key a third time; and every key must appear in all three. Plus the
empty-default rule for a variable whose fallback is a claim rather than a format, and the direction the
rule runs in — one way only, because `POSTGRES_*` belongs to the database image and `OTEL_*` to the
exporter under its own published contract, and asserting those would report findings on a correct tree
(F-41).

### The gate, and why it is derived

`ConfigurationSurfaceTests` — F-38's lesson, seventh application, again without being asked.

It **derives the key set from `RestaurantOptions.FromConfiguration`** rather than listing it (F-47's
habit, seventh application): a key is the first string literal after a `configuration,` argument, with
the span between the two required to be whitespace so a call shaped differently is skipped rather than
guessed at. Seventeen keys, no duplicates. That derivation is what makes adding a variable and
forgetting one of the three restatements fail by name rather than by nobody noticing.

Five assertions:

1. **The scan read the tree** — at least twelve keys, no key read twice. First and alone, because every
   assertion below it is satisfied by an empty set (F-41), and a marker string that stopped matching
   produces exactly that, silently.
2. **Every variable `Validate()` refuses to start over is a variable the binding method reads.** A
   second, independent observation of the same set from the same file. It catches the rename applied to
   one half and not the other, whose symptom is an error message naming a variable nobody can set.
3. **Every key reaches the container** — the `web` service's `environment` mapping. This is the one that
   found F-50.
4. **Every key is in `.env.example`** — the file an operator copies. A commented-out line counts;
   that is how this project documents an optional setting.
5. **Every key is in §13's table** — checked against the section, not the document, so a variable
   mentioned in passing elsewhere does not count as specified.

The compose scan is **bounded to the `web` service**: from its own two-space key to the next
two-space key, then the `environment:` mapping inside that, then the six-space children. Every service
in the file takes an `environment:` block, so a key set on `postgres` would satisfy an unbounded scan
while reaching nothing. Plain string operations and no YAML parser, deliberately — a package
dependency in the unit test project to check five lines of indentation is the worse trade, and the
question being asked is answerable without one. The same reasoning, and the same house style, as the
CSP contract test, which also reads source text rather than parsing it.

### Considered and rejected

**`env_file: .env` on the `web` service.** It would fix this instance and nothing else: it hands the
container the *whole* file, including `POSTGRES_PASSWORD`, `CLOUDFLARE_TUNNEL_TOKEN` and whatever an
operator has added — which is F-45's mistake in a different file, a deny-list where an allow-list was
already working. The enumerated block is right. It was one line short.

**A `check_tree.sh` gate.** Same answer as Slices 23 and 24: that script asserts properties of
authored text as text and knows nothing about what a compose service means, and F-41's rule is that
gates sharing a file set must share one definition of it. This gate's subject is a C# method.

**Assert the reverse direction too** — that every key in the `web` block is one the application reads.
It would report `OTEL_EXPORTER_OTLP_HEADERS`, `OTEL_SERVICE_NAME` and `POSTGRES_*` on a correct tree,
which is F-41 exactly. Recorded in §13 as a one-directional rule so that nobody adds it later thinking
it was forgotten.

**Make `SourceUrl` required.** Tempting — it would turn a silent wrong answer into a refusal to start.
But it makes an unmodified deployment set a variable to discharge an obligation it does not have (§13
of the licence binds *modified* versions), and the failure it prevents is not the one that happened:
the operator here *did* set the variable.

### Two smaller things fixed on the way past

`OPERATIONS.md` §14's release procedure said `git tag --annotate v1.0.0` and then listed
`ghcr.io/kusl/myrestaurant:0.6.0` among the images it would produce, and the `compose.override.yaml`
example pinned `0.6.0` too; `release.yml`'s header comment said `v0.6.0`. One release, three places,
two versions. Not a finding — nothing is unverifiable and no gate is missing — but it is in the
paragraph a person reads *while* cutting the first tag, and it is now one number.

### Build and test

```bash
bash scripts/check_tree.sh
#    expect: 5 gates, "tree hygiene passed.", exit 0. The authored-text count rises by ONE — the new
#    test file. If it does not move, the `git add` did not happen.

bash scripts/check_repository.sh --offline
#    expect: 3 gates plus a SKIP, exit 0. Gate 3 must still report "none": the OPERATIONS §15 text
#    added here describes a compose file and a curl, not a repository setting.

bash scripts/ci_local.sh --with-all
#    expect: 8 numbered gates, same number and same order as Slice 24.

dotnet build
#    one new test file and one doc-comment edit to RestaurantOptions.cs. No signature changed.

dotnet test
#    expect: 1056 total, 0 failed. Was 1051. Five new. No existing test is edited.

podman-compose --profile production config | grep RESTAURANT_SOURCE_URL
#    the interesting one. Before this slice it printed nothing.

RESTAURANT_SOURCE_URL=https://example.invalid/mine bash run.sh --smoke
curl --silent http://localhost:8080/source | grep example.invalid
#    end to end through the transport that was broken: set it, boot it, read it back.
```

### What was verified here

No .NET SDK and no container engine in the sandbox, so nothing was reasoned about that could be
executed instead:

- **The finding was measured, not spotted.** Every key `FromConfiguration` binds was extracted and
  set-differenced against the `web` service's environment mapping, `.env.example` and §13's table.
  One key missing from one place, and the census is reproduced in the test.
- **All five assertions were executed** as faithful ports against the delivered tree. They fail on the
  Slice 24 tree at assertion 3 naming `RESTAURANT_SOURCE_URL`, and pass on this one — which is the
  strongest available sensitivity proof, since the gate written today fails on today's tree.
- **Sensitivity proven for the other four by planting the damage**: a key dropped from `.env.example`,
  a key renamed in §13's table, a key renamed in a `Validate()` refusal message only, and — the one
  that matters — the missing key added to the **`postgres`** service instead of `web`. Each is caught
  by the assertion named for it and by no other. The last one proves the service-boundary parse is
  doing work rather than searching the file.
- **The parsing was written twice and compared.** The C# uses plain string operations because there is
  no `Regex` anywhere in this tree; the Python port used regular expressions first and plain string
  walks second, and both produce the same seventeen keys and the same fifteen validated names, which is
  what makes the hand-rolled scanner trustworthy without a compiler here to run it.
- **`SpecificationVersionTests` re-run against the edited specification**: header 1.10, entries
  1.10 … 1.0 descending, both assertions hold. Note that `Version.TryParse` reads `1.10` as minor
  **ten**, so it sorts above 1.9 correctly — checked rather than assumed, because a string comparison
  would have got it backwards.
- **Both edited YAML files parsed** with a real parser, and the `web` service's environment mapping
  re-read from the parsed document to confirm the new key lands where the text scan says it does.
- **Governance gate 3 re-run over every delivered file**: no new match. The OPERATIONS §15 addition
  names a compose file and a command, not a platform setting (F-46's form).
- **Every documentation edit applied by exact-match replacement with an assertion that the anchor
  occurs exactly once**, so nothing was edited by position. `docs/BUILD_PROGRESS.md` is its existing
  bytes plus this one appended section.
- **`.editorconfig` hygiene checked on every delivered file**: LF endings, final newline, no
  whitespace-only lines, no trailing whitespace, no context-dump separator. Brace, paren, bracket and
  string balance checked on both C# files; CS4007 and CS1620 scans clean — the one `${...}` sequence in
  a failure message was removed rather than left in a concatenation chain somebody might later make
  uniformly interpolated.
- No shell script, migration or Razor component changed, so `bash -n`, shellcheck and the Razor
  compiler have nothing new to look at.

### Still open, and deliberately not answered here

**`Permissions-Policy`**, recorded by Slice 24 as a separate question. Left open on purpose: it is a
judgement about a security header's cost-benefit on a deny-list surface, and folding it into a slice
about an AGPL-compliance defect would muddy the record of both. It blocks nothing.

**Two operator actions** that no archive can contain: enabling private vulnerability reporting on
GitHub, and setting the repository description (F-42).

## M6 Slice 26 — the stack that would not start anywhere else (F-51, F-52)

Everything in this repository was green. 1056 tests, six CI gates, a tree that parses, a governance
surface with a policy in it, a Content Security Policy with a contract test, and an AGPL §13 offer that
finally reaches the process. Then the documented command ran on a second machine and the canonical stack
did not start.

```
Error: short-name "postgres:17-alpine" did not resolve to an alias and no
       unqualified-search registries are defined in "/etc/containers/registries.conf"
Error: no container with name or ID "myrestaurant_postgres_1" found: no such container
Error: "myrestaurant_postgres_1" is not a valid container, cannot be used as a dependency
```

Three errors. The second and third are consequences of the first. The first names a file nobody would
think to open, and none of the three names `compose.yaml`.

### F-51 — what a short name actually is

`postgres:17-alpine` is not an image reference. It is a *query*, resolved through
`unqualified-search-registries` in `registries.conf`. Fedora's `containers-common` populates that list;
a stock Debian ships it commented out, on the reasonable grounds that silently searching a registry
somebody else chose is not a thing an image reference should do. So the same eleven characters mean
`docker.io/library/postgres:17-alpine` on the machine this project was written on and mean nothing at
all on Debian.

**Why nothing here could see it.** `check_tree.sh` reads tracked files as text, and `compose.yaml` is
correct as text — well-formed YAML, LF endings, a final newline, no separator. `ConfigurationSurfaceTests`
had, one slice earlier, audited this exact file key by key; it audits the `environment` mapping, and the
`image` line is four lines above where it starts looking. CI runs on Ubuntu with Docker, which resolves
short names. And **no test in this project starts `compose.yaml` at all**: the Testcontainers fixtures
build their own container specification in C#, and boot-smoke runs the built image with an environment
the workflow hands it. The one artifact that describes how this program is deployed is the one artifact
nothing executes.

**The part that is uncomfortable rather than interesting.** The rule pre-existed the finding by ten
slices. From `scripts/restore_drill.sh`, unchanged since Slice 16:

```
#   DRILL_POSTGRES_IMAGE         scratch image (default docker.io/library/postgres:17-alpine —
#                                fully qualified so a short-name registry prompt cannot hang a drill)
```

Somebody reasoned this out, wrote down why, and applied it to a scratch container inside a rehearsal —
while the stack being rehearsed, the one ADR-0004 calls canonical and OPERATIONS documents and a fork
will run, kept the defect. F-46's lesson was that a rule enforced as a list of examples is enforced as a
list of examples. This is the step before it: a rule discovered on one example and never stated as a rule.

**And it is deliberately not made executable.** F-38's habit — a row in the embodiment column names
something that runs — is honoured seven times in this ledger and is declined here, on F-41's reasoning.
The available check is "no `image:` value lacks a registry component", which is a text assertion about a
file whose contract is behavioural: it would pass on a tree where the images are qualified and the stack
still cannot start for the next reason, and it would report findings on a correct tree the day somebody
legitimately references a local image. What catches this class is running the canonical stack on the
canonical engine — a CI job on a Podman host — and that is recorded as an open item rather than closed
with a grep that resembles one.

### F-52 — a document that explained why something was impossible

From `README.md`:

> a quick tunnel cannot "print a URL and exit", because exiting kills the URL

From OPERATIONS §10, more emphatically:

> there is no detached mode and no "print the URL and exit," because the tunnel dies with the process
> that owns it

The second sentence refutes itself in its own final clause. The tunnel *does* die with the process that
owns it. `scripts/quick_tunnel.sh` **is** that process — it runs `cloudflared` as a foreground child and
blocks on `wait`. Ownership is a choice. Run the same `cloudflared` as a detached container and the engine
owns it; the shell exits, the hostname keeps serving.

**What the sentence cost was not accuracy, it was a closed door.** The case that needed this is concrete
and had never been served: a spare machine on the LAN, reached over SSH or Tailscale, running the build
that testers will use for the next few days, on a host with **no .NET SDK**. `run.sh` cannot help there —
its default and `--smoke` modes both require `dotnet` on the host — and `quick_tunnel.sh` publishes a URL
that dies when the session closes. Anybody looking for the missing script would have found two documents
telling them it could not exist.

**This is a category the ledger did not have.** F-38 was four documents agreeing about something no code
did. F-42 was documents disagreeing with the platform. F-50 was documents agreeing about something the
transport discarded. This is a document *correctly* describing one implementation and stating its
incidental property as a law about the technology — the hardest kind to catch, because it is true every
time you check it against the script it was written about.

### What shipped

`scripts/dev_instance.sh`, and three orderings that are the whole content of it.

**The tunnel is a container, not a child.** `podman run --detach --name myrestaurant_quicktunnel
--network host …`. Nothing the script starts is its own descendant, which is what lets it exit. Host
networking is how `cloudflared` reaches the loopback-published `web` port, matching what `quick_tunnel.sh`
already does and is already proven to do on both machines.

**The image is built before any URL is announced.** `quick_tunnel.sh` opens the tunnel and then builds.
On the cold Debian host that produced F-51 the consequence was measured: the public URL was printed at
minute zero and the application became reachable nineteen minutes later. Building first costs nothing and
reduces that window to the time it takes to start two containers.

**The origin is known before `web` is created.** Because the build already happened, the tunnel URL is in
hand before the stack comes up, so `RESTAURANT_PUBLIC_ORIGIN` is exported and `up -d` runs **once** —
instead of coming up with a placeholder and being force-recreated. That avoids a flag whose behaviour is
worth writing down, because it is not what its name suggests. From podman-compose 1.3.0, `compose_up`:

```python
if args.force_recreate or len(diff_hashes):
    down_args = argparse.Namespace(**dict(args.__dict__, volumes=False))
    await compose.commands["down"](compose, down_args)
```

`up --force-recreate web` is a `down` of the **whole project** followed by an `up` of the named service.
It restarts the database and deletes and recreates the network. That is visible in both terminal logs
that motivated this slice, as an unexplained network id appearing twice. The engine's own
recreate-on-change is relied on instead, and it is sound: `self.yaml_hash` is computed *after*
`rec_subs(content, self.environ)`, so a changed origin is a changed config hash and a later run with a
new URL recreates by itself.

**Two behaviours are requirements, not conveniences.** A second `up` **reuses** the open tunnel rather
than minting a hostname, because a tester who registered a passkey registered it against that hostname
and `*.trycloudflare.com` is on the Public Suffix List — a new random subdomain discards every one.
`--new-url` is how to ask for a fresh hostname deliberately, and it says what it is about to break. And
since nothing else will ever close the tunnel, `down` is not housekeeping; it is the only thing that stops
the instance.

**`up` exits on evidence.** After printing the URL it re-probes the **public** origin for
`DEV_INSTANCE_SETTLE_SECONDS` (default 20) and reports how many probes answered, so a tunnel that came up
and immediately fell over is reported to the operator rather than discovered by a tester. The probe assumes
nothing about the host: `curl`, then `wget`, then the `curl` the runtime image installs for its own compose
healthcheck, reached with `exec`. That third path is why this works on a minimal Debian — it is a client
guaranteed to exist whenever there is anything worth probing, and it reaches both the application (it *is*
the application) and the public URL (it has the same egress the tunnel does).

**Compose engine chosen to match the container engine, not independently.** The engine is selected first
and the compose command is selected for it. Two independent `PATH` searches can disagree on a host with
both engines, leaving the stack in one store while `logs`, `exec` and `rm` look in the other — F-43 with a
different pair of commands.

**Containers found by label, not by name.** `--filter label=com.docker.compose.service=web` rather than
guessing at `<project>_web_1`. Both engines label what they create; the naming scheme is theirs.

### One more thing in `compose.yaml`, and why it is here

`SOURCE_REVISION` is now a build argument, so `/source` reports the commit a tester actually reached
rather than "not recorded". It is passed with an **empty** default, which is F-50's ruling applied to a
second file: `Containerfile`'s own `ARG` is the one place the fallback lives, and it renders an unstamped
build as *not recorded* rather than guessing. Repeating a value in `compose.yaml` would override the one
place the default is written down — F-50 reintroduced one layer up.

Its placement is load-bearing and was checked rather than assumed. `ConfigurationSurfaceTests` reads the
`web` service's environment keys by indentation: the service block runs from `  web:` to the next
two-space key, the mapping from `    environment:` to the next four-space key, and the keys are its
six-space children. `      args:` is a six-space key. It sits **above** `environment:`, so it is outside
the scanned span — verified by porting the scan and running it, and proven sensitive by moving the same
block below `environment:`, where it is picked up as an eighteenth configured key and the F-50 assertion
would fail.

### What was verified here

No .NET SDK and no container engine in the sandbox, so nothing was reasoned about that could be executed
instead.

- **`bash -n` and `shellcheck` on `scripts/dev_instance.sh`**, at `--severity=warning` (blocking in CI)
  and `--severity=style` (advisory). Clean at both. Baselined first against the nine existing scripts to
  confirm the installed shellcheck agrees with CI's on this tree.
- **podman-compose 1.3.0 read rather than remembered**, for the four behaviours this script depends on.
  `.env` does not beat the process environment (`self.environ = dotenv_dict` then
  `.update(dict(os.environ))`, lines 1927–1928) — so exporting the origin works, which the whole design
  rests on. `build.args` is forwarded as `--build-arg` (line 2516). `build` accepts a service name
  (`compose_build`, `args.services`). And `up --force-recreate` downs the whole project.
- **`compose.yaml` parsed with a real YAML parser** and the parsed document inspected: both image
  references fully qualified, `build.args.SOURCE_REVISION` present, twenty environment keys.
- **`ConfigurationSurfaceTests` ported and run against the edited tree** — all three restatement
  assertions pass, and the compose scan reports the same twenty keys with the same block boundaries
  (web 38→98, environment 53→85). Sensitivity proven by planting the `args` block below `environment:`,
  which produces twenty-one keys including `SOURCE_REVISION`.
- **`SpecificationVersionTests` ported and run**: header 1.11, entries 1.11 … 1.0 descending, both
  assertions hold. `Version.TryParse` reads `1.11` as minor **eleven**, so it sorts above 1.10 — checked,
  because a string comparison would get it backwards.
- **Every documentation edit applied by exact-match replacement with an assertion that the anchor occurs
  exactly once**, so nothing was edited by position.
- **Byte hygiene on every delivered file**: LF endings, exactly one final newline, no CR, no
  whitespace-only lines, no trailing whitespace, no context-dump separator.
- **No C#, no Razor, no migration changed**, so the compiler, the Razor tag-tree walk and DbUp have
  nothing new to look at. The test count is unchanged at 1056 — this slice adds no test, and saying so
  is more honest than predicting a number.

### What was NOT verified, and cannot be from here

**`scripts/dev_instance.sh` has never been executed.** It is a new script with eleven sections, and the
only tools that have looked at it are a parser and a linter. Everything it does to a container engine is
reasoned from podman-compose's source and from the two terminal logs. The first run on virginia is the
first real test, and the honest expectation is that something in it needs a second pass.

**F-51's fix has not been observed to work.** The claim is that fully qualified names resolve on Debian
without `unqualified-search-registries`, which is how the registry code is documented to behave and how
`restore_drill.sh` has behaved since Slice 16 — but the machine that produced the error has not yet run
the corrected file.

### Still open, and deliberately not answered here

**A CI job that runs the canonical stack on the canonical engine.** This is F-51's real embodiment and it
is not in this slice. Everything CI does today either builds the image or boots it with an environment
the workflow supplies; nothing runs `compose.yaml`. Until something does, "the canonical stack starts" is
a claim resting on somebody having tried it lately.

**`OPERATIONS.md` §2 asserts behaviour no code has.** It tells an operator that `.env` is created for
them — *"run.sh and the scripts do this automatically when .env is absent — F-16"*. No script in this
tree touches `.env`; all nine were grepped. That is F-38's shape aimed inward and it deserves its own
slice rather than a line in this one, because the interesting question is which of the two is right: the
document, in which case the scripts are missing something, or the scripts, in which case F-16's row needs
revisiting. `scripts/dev_instance.sh` warns when `.env` is absent rather than creating it, which is the
conservative choice while the question is open.

**`Permissions-Policy`**, carried forward from Slice 24 and still not urgent.

**Two operator actions** no archive can contain: enabling private vulnerability reporting on GitHub, and
setting the repository description (F-42).

## M6 Slice 27 — the command that started everything and never came back (F-53, F-54)

Slice 26 shipped `scripts/dev_instance.sh` and said, in this file, what had not been verified:

> **`scripts/dev_instance.sh` has never been executed.** It is a new script with eleven sections, and
> the only tools that have looked at it are a parser and a linter. […] The first run on virginia is the
> first real test, and the honest expectation is that something in it needs a second pass.

It needed a second pass. This is that pass, and the thing that needed it was not in the script.

### What the operator saw

```
[dev-instance] public URL: https://state-dust-pty-cfr.trycloudflare.com
[dev-instance] starting postgres and web against that origin…
331b32fcdf021f875818bd44fd8b0b0824c39924e7fd37d1fb1aaaee00d92f8a
Trying to pull docker.io/library/postgres:17-alpine...
[…]
c7ba751db39bbfb6a330db2e3071ebcc6c5047329387335d1975476eb230354e
myrestaurant_postgres_1
6fe372906ea32924020f1b9b9023a2b7ec64d16e8ebf7197508ad3a7b872126b
```

And then nothing, for as long as anybody was willing to wait. No error, no warning, no further line.
F-51's fix had worked — the fully qualified `docker.io/library/postgres:17-alpine` pulled without a
registry list, which is the observation Slice 26 recorded as *not verified* — and the run got further
than any before it, to a place with no output at all.

### F-53 — reading the engine instead of guessing at it

Four lines of output, mapped against podman-compose 1.3.0's source rather than against anybody's
memory of it:

| Line | The call that produced it |
|---|---|
| `331b32fc…` | `podman pod create` — `create_pods()` |
| pull, then `c7ba751d…` | `podman run -d` for `postgres` |
| `myrestaurant_postgres_1` | `podman start` echoing the name back |
| `6fe37290…` | `podman run -d` for `web` |
| *(nothing)* | `run_container()` → `check_dep_conditions()` |

`compose_up` in that version reads:

```python
podman_command = "run" if args.detach and not args.no_start else "create"
...
subproc = await compose.podman.run([], podman_command, podman_args)
if podman_command == "run" and subproc is not None:
    await run_container(compose, cnt["name"], cnt["_deps"], ([], "start", [cnt["name"]]))
```

and `check_dep_conditions`, which `run_container` calls before its `start`:

```python
if deps_cd:
    # podman wait will return always with a rc -1.
    while True:
        try:
            await compose.podman.output([], "wait", [f"--condition={condition.value}"] + deps_cd)
            log.debug(...)
            break
        except subprocess.CalledProcessError as _exc:
            log.debug(...)
        await asyncio.sleep(1)
```

An unbounded loop, both exits logged at **debug** level. `compose.yaml` asked `web` to wait for
`postgres` to be `service_healthy`, the health status never advanced past `starting`, and the loop ran
once a second until the operator gave up. Upstream carries this as issues **#1178** ("Podman Compose
1.3.0 `up -d` command never returns/finishes/ends", reported from Debian, with a `Ctrl+C` traceback
landing on exactly `compose_up` → `run_container` → `check_dep_conditions` → `asyncio.sleep`) and
**#1183**, which names the design error: the dependent containers are started in the first pass and
the conditions are checked afterwards, so the wait protects nothing and can only delay a return.

**The instance was fine the entire time.** `podman_command` is `"run"` under `-d`, so both containers
were started before the wait began; the tunnel was open; the public URL served the application. Every
observable fact except one said the slice had worked.

### Three things that made this worth a slice rather than a one-line fix

**There is no flag.** `--no-deps` is accepted by `up`'s parser in 1.3.0 and consulted only by
`compose_run` (`deps = cnt["_deps"]; if deps and not args.no_deps:` — in `run`, not in `up`).
`compose_up` passes `cnt["_deps"]` to `run_container` unconditionally. Starting the services in two
invocations does not help either: `get_excluded` subtracts a named service's dependencies from the
exclusion set, so `up -d web` processes `postgres` as well and reaches the same wait.

**Whether the condition is ever satisfiable is a property of the host.** A health status advances only
if something runs the healthcheck, and under rootless Podman that something is a transient systemd
timer in the user's session. Upstream's own fix for this (PR #1184) says as much in its commit titles —
*"run the healthy state validation only when systemd is available"*. So no amount of correctness in
this repository makes `service_healthy` safe to depend on across the hosts this project supports.

**The condition was never needed.** `SchemaMigrationRunner` has this, since M1:

```
/// <item>Bounded boot retry: at compose start the web container can race PostgreSQL, so
/// connection failures are retried a fixed number of times before giving up.</item>
```

Thirty attempts, two seconds apart. `web` losing the race to `postgres` is a race the application was
written to lose safely, four milestones before a health gate was added on top of it.

So the ruling is a **prohibition**, stated in §14.1 beside F-51's: the only condition `compose.yaml`
may use is `service_started`. The health**check** on `postgres` stays exactly where it was — `podman ps`
reads it, `dev_instance.sh status` prints it, and an operator needs it — it simply stops standing
between `up -d` and returning.

### The second half, which outlives its cause

A script whose entire purpose is to hand a terminal back must not contain a call that can keep it,
whatever the reason. `compose.yaml` is fixed; podman-compose is not, and the next version of this
problem will arrive through some other blocking path. So §14.3a now requires the deadline, and
`scripts/dev_instance.sh` implements it:

- `compose_guarded <seconds> <args…>` wraps every compose invocation in `timeout --kill-after=15s`,
  mapping both 124 and 137 to one "the deadline passed" result. Where `timeout` is absent the call runs
  unguarded and the preflight says so, because silently dropping a safety net is worse than not having
  one.
- `DEV_INSTANCE_COMPOSE_WAIT` (240s) for ordinary commands; `DEV_INSTANCE_BUILD_WAIT` (5400s) for the
  image build, because a watchdog that cut off a legitimate nineteen-minute build would be a worse
  defect than the one it guards against.
- A tripped deadline is **not** failure. The helper names F-53, prints each service's state and health
  read straight from the engine, starts anything created but never started, and then verifies
  `/healthz/ready` itself — because a compose command that did not return is not a stack that did not
  start, and in the observed case the difference was the whole story.
- `status` prints those two engine-read lines **before** it asks compose anything, so they arrive on a
  host where compose is wedged. `down` falls back to removing the containers directly.
- Killing compose is safe in the one way that matters, and it is the same property the detached tunnel
  relies on: the containers it has already created belong to the engine, not to this shell.

Two smaller things found while in the file. `compose_container` filtered on
`com.docker.compose.service` alone — the right label, podman-compose does set it — so a `web` service
belonging to any other compose project on the same host could have been mistaken for this one; it now
tries a project-scoped filter first and falls back, because the project name is derived here and the
engine is the authority on it. And `container_health` is new: reported, never waited on, which is the
whole distinction this slice is about.

### F-54 — the runbook step that cited this ledger

`OPERATIONS.md` §2 step 2 said: *"Copy `.env.example` to `.env` (`run.sh` and the scripts do this
automatically when `.env` is absent — F-16)."* All nine scripts were grepped. None writes `.env`. And
the citation is **accurate** — F-16's row in `DOCUMENTATION_REVIEW.md` says `.env` is copied from
`.env.example` when absent — so this is not a document drifting from code. It is a decision ruled on
2026-07-17, recorded in the *Embodied in* column, never implemented, and then restated in the
indicative by the runbook that depends on it. That is F-38 exactly, one-tenth the size, pointed inward.

Ruled the other way: **the document is wrong and the scripts are right.** A script that materialised
`.env.example` would write an untracked file carrying `POSTGRES_PASSWORD=myrestaurant` that nobody
knowingly created, on a path `.gitignore` hides from every tool that reads this tree — F-45's class of
artefact arriving through a different door — and because the stack starts without it, auto-creation
buys nothing except the removal of the one moment an operator is supposed to decide about credentials.
§2 says to copy it by hand and says why; §10a says it in a clause; `dev_instance.sh` says it in the
warning it already printed when the file was missing.

### What was verified, and how

No .NET SDK and no container engine in the sandbox, so nothing was reasoned about that could be
executed instead.

- **podman-compose 1.3.0's source was fetched and read**, not remembered — `compose_up`,
  `run_container`, `check_dep_conditions`, `get_excluded`, `container_to_args`'s healthcheck
  translation, `ServiceDependencyCondition.from_value` (which maps `service_started` → `running` and
  the list form to the same), and `Podman.run`'s return contract (an exit code, never an exception,
  which is why a name conflict on `podman run` does not stop `up`). Versions 1.2.0, 1.3.0, 1.4.0 and
  `main` were compared; `main` has the fix and a `deps_from_container` that honours `--no-deps`, 1.3.0
  has neither.
- **The four output lines were mapped to the four calls that produce them** before anything was
  changed, and the upstream issue with the matching traceback was read.
- **`scripts/dev_instance.sh` executed end to end against a fake engine.** `podman`,
  `podman-compose` and `curl` stand-ins were written — enough to create, start, inspect, log and
  report health — and the `up` path was run to completion, exit 0, including the banner and the settle
  phase. Then, one at a time:
  - **compose starts the containers and hangs** (the observed failure, with `postgres` health pinned at
    `starting`): the deadline trips at 3s, F-53 is named, both containers are reported
    `running, health: starting`, readiness is verified, the terminal is released. **Elapsed: 3 seconds.**
  - **compose creates the containers, does not start them, and hangs**: same, plus
    `starting build_postgres_1 (it is created)` and `starting build_web_1 (it is created)` — the repair
    path, exercised rather than reasoned about.
  - **compose is wedged from the first call**: `status` prints engine-read state and health before it
    asks compose anything, then reports the deadline; `down` reports the deadline, removes both
    containers directly, and leaves nothing behind.
  - **`--help`, an unknown argument, and two commands at once**: all as specified.
- **The preflight `service_healthy` grep proven sensitive**: silent on the delivered `compose.yaml`,
  fires with all four warning lines when the old condition is planted back.
- **`bash -n` and `shellcheck` on the rewritten script**, clean at `--severity=warning` (blocking in
  CI) and at `--severity=style` (advisory). Baselined against all nine existing scripts first: every
  one of them is style-clean with the installed shellcheck, so the tool agrees with CI's on this tree.
- **`compose.yaml` parsed with a real YAML parser** and the parsed document inspected: four services,
  three `depends_on` edges, all three `service_started`, twenty environment keys on `web`.
- **`ComposeDependencyContractTests` ported to Python and run against the tree**, then proven sensitive
  one regression at a time: `service_healthy` on all three edges (fails fact 2), on one edge only
  (fails fact 2), `service_completed_successfully` (fails fact 2 — the other condition with the same
  hang shape, since it waits on `--condition=stopped`), a dependency naming a service that does not
  exist (fails fact 3), every `depends_on` removed (fails fact 1, the non-vacuity guard), and the
  `services:` marker broken (throws rather than passing vacuously). The **list form passes**, and that
  is deliberate: both engines normalize it to `service_started`, so failing it would report a finding
  on a correct file (F-41).
- **`ConfigurationSurfaceTests`'s compose scan re-run against the edited file**: still twenty keys, no
  duplicates, `RESTAURANT_SOURCE_URL` present, `SOURCE_REVISION` correctly *not* counted as an
  environment key. The block boundaries moved (web 38→98 became 56→121, environment 53→85 became
  76→108) because comments were added, which is exactly why that test computes them.
- **`SpecificationVersionTests` ported and run**: header 1.12, entries 1.12 … 1.0 descending, both
  assertions hold. `Version.TryParse` reads `1.12` as minor **twelve**, so it sorts above 1.11 —
  checked, because a string comparison would get it backwards.
- **Brace, parenthesis and bracket balance walked** over the new C# file with a string- and
  comment-aware scanner, and over `ConfigurationSurfaceTests` as a control. Both balanced.
- **Every documentation edit applied by exact-match replacement with an assertion that the anchor
  occurs exactly once**, so nothing was edited by position.
- **Byte hygiene on every delivered file**: LF endings, exactly one final newline, no CR, no
  whitespace-only lines, no trailing whitespace, no context-dump separator.

### The test count

1056 → **1059**, and that is arithmetic rather than an observation: three `[Fact]` methods added, none
removed, no `[Theory]`. Nothing in this slice touches a Razor file, a migration or any application
code, so no existing test's subject moved.

### What was NOT verified, and cannot be from here

**The fix has not been observed to work on the machine that produced the failure.** The claim is that
`condition: service_started` makes `check_dep_conditions` wait on `--condition=running` against a
container that is already running, which returns immediately — read out of podman-compose's source and
podman's documented `wait` semantics, not watched. The deadline is the reason a wrong answer here is
survivable rather than another silent hang.

**Nothing has compiled.** The new test file has been balanced, its logic ported and exercised, and its
idioms copied from a file in this tree that compiles — but `dotnet build` has not run on it.

### Still open, and deliberately not answered here

**A CI job that runs the canonical stack on the canonical engine.** Third slice in a row where this is
the real embodiment of a finding and is not in the archive. F-51 needed it, F-53 needed it, and both
were found by a human running a command on a second machine. Everything CI does either builds the image
or boots it with an environment the workflow supplies; nothing runs `compose.yaml`.

**Every pre-F-38 row in `DOCUMENTATION_REVIEW.md` names embodiments that were, when written,
intentions.** F-54 is one of them, found by accident. Nobody has audited the rest. The cheap habit is
recorded in that file's closing section; the audit itself is not done.

**`Permissions-Policy`**, carried forward from Slice 24 and still not urgent.

**Two operator actions** no archive can contain: enabling private vulnerability reporting on GitHub,
and setting the repository description (F-42).

## M6 Slice 28 — the command that came back and said nothing was wrong (F-55, F-56)

Slice 27 fixed a bring-up that never returned. This slice fixes what it returned *with*.

`time bash scripts/dev_instance.sh` on virginia, one slice later:

```
[dev-instance] waiting for http://localhost:8080/healthz/ready (up to 300s; first boot runs the migrations)…
[dev-instance] warning: the app did not answer /healthz/ready within 300s.
[…]
  DEV INSTANCE — DETACHED

  PUBLIC URL:  https://tablets-opponents-vip-each.trycloudflare.com
[…]
[dev-instance] holding 20s to confirm the public URL answers, then releasing this terminal…
[dev-instance] warning: the public URL did not answer in 4 attempt(s) over 20s.
[dev-instance] releasing the terminal. The instance keeps running.

real    6m55.837s
```

Exit status 0. Then, from `status`:

```
  postgres:  myrestaurant_postgres_1 (running, health: starting)
  web:       myrestaurant_web_1 (stopped, health: starting)
```

`postgres` had been created six minutes earlier and reported `Up 1 second` — the engine restarting it
in a loop. `web` had exited **1**. And `logs -f`, the command the banner recommends, printed forty
lines of cloudflared saying `connection refused`, which is the symptom of the failure repeated forty
times and never once its cause.

### The exit code was the tell

**Exit code 1 is reachable from exactly one place in this program.** `Program.cs` binds and validates
configuration before a host exists, and on a validation failure it prints each error to stderr and
`return 1`s. Nothing else in the file returns 1: a `SchemaMigrationRunner` failure throws, and an
unhandled exception on .NET aborts with 134. So the application had written the reason to its own
stderr within the first second, and `podman logs myrestaurant_web_1` had been holding it ever since.

Nothing in the script had ever printed a container log. That is the finding.

### F-55 — a wait with a deadline and no evidence

Slice 27's rule was *no call may own the terminal indefinitely*, and it was applied correctly to every
compose invocation. The readiness wait was not a compose invocation. It polled HTTP every three
seconds for 300 seconds without once asking whether the container it was polling still existed — and
then the script printed the success banner unconditionally, spent twenty more seconds probing a public
URL for an application that was not answering on loopback, and exited 0.

Six of the seven minutes were spent waiting for something that had already failed. The seventh was
spent announcing it as ready.

**F-53 and F-55 look like the same defect and need opposite fixes.** A deadline stops a wait that
cannot *end*. Only a liveness check stops a wait that cannot *succeed*. Both were reasoned about
carefully in Slice 27 and neither reasoning covered the other case, which is why §14.3a now states the
rule as its own paragraph rather than as a clause of the deadline rule.

What changed, all of it in `scripts/dev_instance.sh`:

| | Before | Now |
|---|---|---|
| readiness wait | HTTP poll only, 300s | polls the container's state too; starts a stopped `web` up to `DEV_INSTANCE_START_ATTEMPTS` times, then ends |
| database | not waited on at all | its own bounded wait via `pg_isready` inside the container, ending early on a crash loop |
| a failure | one warning line | `NOT SERVING` banner, state + **exit code** + restart count, both log tails, a reading key |
| `logs` | the tunnel's, only | takes `web` (default), `postgres`, `database` or `tunnel`; `--tail N` |
| `diagnose` | — | the whole failure report, on demand, at any time |
| `reset` | — | `down` plus this project's volumes, enumerated from the engine, after confirmation |
| a stopped container | `(stopped, health: starting)` | `(exited, exit code 1, restarted 3x)` |
| settle phase | always ran | skipped when readiness already failed |
| exit status | 0 | 0 only if the application answered; 1 otherwise, stack left running |

The two waits are **separated** because one message cannot diagnose both: "the app did not answer" is
equally true of a crash-looping database, a rejected configuration and an image that never started.

**Why `web` is started again rather than only reported.** `SchemaMigrationRunner` retries a connection
failure thirty times at two-second intervals and then throws (ADR-0012), so the application gives up
after sixty seconds. A first `postgres` boot slower than that — an `initdb` on a cold volume, on a
spare machine, while an image build's page cache is still being written back — outlives the retry and
leaves a correctly built image stopped with nothing wrong with it. The engine's restart policy usually
covers this. *Usually* is not a thing to spend 300 seconds on.

**The reading key is the part that turns a report into a diagnosis.** Six symptoms this program can
actually produce, each paired with what it means and what to do: `Configuration error:` (it refused its
environment; the line names the variable; nothing retries it), `Database not ready (attempt n/30)` (the
cause is in the *other* log), the four PostgreSQL data-directory failures, `address already in use`, and
the case where both containers are healthy and the probe still fails.

### The failure `down` cannot repair, and `reset`

A PostgreSQL data directory that will not start — an interrupted first `initdb`, a hard reboot
mid-write, a directory from another major version — survives `down`, because `down` keeps the named
volumes deliberately: the database and the Data Protection key ring are what make a test instance
worth returning to. It also survives `podman system prune -a`, which does not remove volumes at all.
So an operator can clear every container and every image on the host, twice, and start the same broken
directory each time. That is exactly what the virginia session did.

Nothing in this tree could clear it. `reset` can, and it is destructive by construction: this project's
volumes, enumerated from the engine rather than guessed at by name, after printing what that destroys —
every account, every passkey, every enrolled TOTP secret — and requiring confirmation, refusing rather
than assuming consent when stdin is not a terminal.

### F-56 — three helpers, one port, one correct address

`compose.yaml` publishes `web` as `127.0.0.1:8080:8080`. `run.sh` has probed
`http://127.0.0.1:8080/healthz/ready` since M1. Both tunnel helpers defaulted `TUNNEL_TARGET` to
`http://localhost:8080`, and that value is dialled by three clients: `cloudflared`, and then whichever
of `curl` or `wget` the host has. curl and GNU wget try the second address when the first refuses.
**BusyBox wget does not** — and it is the second entry in the probe chain of a script whose whole
premise is a host that may not have curl.

The visible cost is what made it findable: cloudflared reports the address it failed on, so the tunnel
log of the F-55 failure reads `dial tcp [::1]:8080: connect: connection refused` over and over, and an
operator debugging that spends the evening on an IPv6 problem that does not exist.

This is **F-51's shape for the third time** — a rule reasoned through once, applied to one file, never
stated. Both helpers now dial the literal, with the reasoning beside the assignment, and §14.3a states
it generally.

### What was verified, and how

No .NET SDK and no container engine were available, so the script was exercised against a **mock
engine**: a `podman` shim answering `ps --filter label=…`, `inspect --format` for `.State.Status`,
`.State.ExitCode`, `.RestartCount` and `.State.Health.Status`, `logs`, `start`, `run --detach`, `rm`,
`volume ls/rm` and `exec`, plus a `podman-compose` shim and a `curl` shim, all driven by state files.
Six scenarios, with the production-length deadlines in place (`DEV_INSTANCE_DATABASE_WAIT=180`,
`DEV_INSTANCE_READY_WAIT=300`) so the timings below are the timings an operator would see:

| Scenario | Observed |
|---|---|
| everything healthy | ready, `DETACHED` banner, **exit 0** |
| `web` exits 1 with a `Configuration error:` line | three restart attempts, `NOT SERVING`, both logs, **exit 1, 15s** |
| `postgres` crash-looping with `PANIC: could not locate a valid checkpoint record` | crash loop named at three restarts, readiness wait skipped, **exit 1, 7s** |
| `logs` with no argument | the **web** container's log |
| `logs postgres` / `logs database` / `logs tunnel` / `--tail 2` | the right container, bounded |
| `reset` | enumerated `myrestaurant_postgres-data` and `myrestaurant_dataprotection-keys`, ignored `other_project_data`, removed both under `--yes`; **refused with exit 1** and removed nothing without it |

Against the same failure the transcript records — a stack that never served — this is **7 to 15 seconds
and exit 1** where it was **415 seconds and exit 0**.

Also verified:

- **Argument handling**: two commands, two log targets, `--tail abc`, an unknown flag, and a log target
  given to `status` each fail with their own message and a non-zero status. `--help` still renders the
  header comment block.
- **`bash -n` and `shellcheck`** on both edited scripts, clean at `--severity=warning` (blocking in CI)
  and at `--severity=style` (advisory). Baselined first: all nine existing scripts are style-clean with
  shellcheck 0.11.0, so the tool agrees with CI's on this tree. One finding was acted on rather than
  suppressed — SC2329, `wait_for_http` became unreachable once readiness moved to
  `wait_for_application`, so it was deleted instead of left as a function nothing calls.
- **`DevInstanceLoopbackContractTests` ported to Python and run against the tree**, then proven
  sensitive one regression at a time: either helper dialling `localhost` again (fails facts 2 and 3),
  a helper on the wrong port (fact 2), the port published as `8080:8080` or on a LAN address (facts 2
  and 4), the `TUNNEL_TARGET` default removed (facts 1, 2, 3), the `ports:` block rewritten as a flow
  sequence (facts 1, 2, 4), and a broken `services:` marker (throws rather than passing vacuously). A
  comment-only edit changes nothing, as a control.
- **`SpecificationVersionTests` ported and run**: header 1.13, entries 1.13 … 1.0 descending. Both
  assertions hold. `Version.TryParse` reads `1.13` as minor **thirteen**, so it sorts above 1.12 —
  checked, because a string comparison would get it backwards.
- **`ConfigurationSurfaceTests` re-run against the edited tree**: seventeen keys derived from
  `FromConfiguration`, all seventeen still in `.env.example` and in the `web` service's twenty-key
  `environment` mapping. The fourteen lines added to `.env.example` are **all comments**, so no key was
  added, removed or duplicated.
- **`ComposeDependencyContractTests` re-run**: four services, three `depends_on` edges, all
  `service_started`. `compose.yaml` is not edited by this slice.
- **Brace, parenthesis and bracket balance walked** over the new C# file with a string- and
  comment-aware scanner, with `ComposeDependencyContractTests` and `ConfigurationSurfaceTests` as
  controls. All balanced.
- **Every documentation edit applied by exact-match replacement with an assertion that the anchor
  occurs exactly once**, so nothing was edited by position.
- **Byte hygiene on every delivered file**: LF endings, exactly one final newline, no CR, no
  whitespace-only lines, no trailing whitespace, no context-dump separator.

### The test count

1059 → **1063**, and that is arithmetic rather than an observation: four `[Fact]` methods added, none
removed, no `[Theory]`. Nothing in this slice touches application code, a Razor file or a migration, so
no existing test's subject moved.

### What was NOT verified, and cannot be from here

**The fix has not been observed on the machine that produced the failure.** Every timing above is from
a mock engine driven by state files. What the mock cannot tell anybody is which of the reading key's
symptoms virginia will actually print — the exit code says the application refused its configuration,
and the variable it named is in a log this slice makes visible for the first time.

**Nothing has compiled.** The new test file is balanced, its logic ported and exercised, and its idioms
copied from two files in this tree that compile — but `dotnet build` has not run on it.

**`podman inspect --format '{{.RestartCount}}'`** is read from documentation rather than from a running
engine. A host whose engine omits the field reads as 0, which loses the crash-loop early exit and keeps
every other behaviour, so a wrong answer here costs a fast failure and not a correct one.

### Still open, and deliberately not answered here

**A CI job that runs the canonical stack on the canonical engine.** Fourth slice in a row where this is
the real embodiment of a finding and is not in the archive. F-51, F-52, F-53 and now F-55 were all found
by a human running one command on a second machine, and the honest expectation after this slice is the
same as after the last one: the next run gets further and finds the next one.

**Every pre-F-38 row in `DOCUMENTATION_REVIEW.md` names embodiments that were, when written,
intentions.** Nobody has audited them.

**`Permissions-Policy`**, carried forward from Slice 24 and still not urgent.

**Two operator actions** no archive can contain: enabling private vulnerability reporting on GitHub,
and setting the repository description (F-42).

## M6 Slice 29 — the engine that does not read its own defaults (F-57)

Slice 28 shipped a diagnosis and said what it could not know:

> **Nothing ran on virginia.** Every timing is from the mock. What the mock cannot say is which
> variable the real `Configuration error:` line names.

It named five. And the answer was not a variable at all.

### What the diagnosis printed

```
  ── postgres (myrestaurant_postgres_1), last 40 line(s) ──
  | 2026-08-10 21:11:23.698 UTC [31] FATAL:  invalid character in extension owner: must not contain
  |     any of ""$'\
  | 2026-08-10 21:11:23.698 UTC [31] STATEMENT:  CREATE EXTENSION plpgsql;
  | child process exited with exit code 1
  | initdb: removing contents of data directory "/var/lib/postgresql/data"

  ── web (myrestaurant_web_1), last 40 line(s) ──
  | Configuration error: RESTAURANT_TIME_ZONE '${RESTAURANT_TIME_ZONE:-America/New_York}' is not a
  |     resolvable time zone on this host.
  | Configuration error: RESTAURANT_CLOCK_FORMAT must be '12-hour' or '24-hour' (was
  |     '${RESTAURANT_CLOCK_FORMAT:-12-hour}').
  | Configuration error: RESTAURANT_CURRENCY_CODE must be a 3-letter ISO 4217 code (was
  |     '${RESTAURANT_CURRENCY_CODE:-USD}').
  | Configuration error: RESTAURANT_SOURCE_URL must be an absolute http or https URL (was
  |     '${RESTAURANT_SOURCE_URL:-}').
  | Configuration error: RESTAURANT_TRUSTED_ORIGIN_PATTERNS entry
  |     '${RESTAURANT_TRUSTED_ORIGIN_PATTERNS:-https://*.trycloudflare.com}' must be an https origin…
```

`1m51s`, exit 1, and both halves of the cause on one screen. The previous run took `6m55s` to exit 0.

### One engine behaviour, both containers

`compose.yaml` sets twenty-three values as `${NAME:-default}`. This engine does not apply the branch
after `:-`, so every variable not already set in the environment arrived as the placeholder text.

- **`web`**: five of those are validated, so `Program.cs` printed five errors and returned 1 — which is
  exactly the inference Slice 28's `_CHANGES.md` drew from the exit code, before any log was available.
- **`postgres`**: `POSTGRES_USER` arrived as `${POSTGRES_USER:-myrestaurant}`, so initdb's bootstrap
  `CREATE EXTENSION plpgsql` failed on the punctuation in the owner name, initdb erased the data
  directory, the container exited, the engine restarted it, and it did that forever. **The crash loop
  Slice 28 attributed to a possibly-poisoned volume was never about the volume.**

The `reset` command shipped in Slice 28 would not have helped, and that is worth recording rather than
quietly correcting: it removes a data directory that cannot start, and this data directory was being
removed by initdb on every attempt already.

### What is known about the behaviour, and what is not

**Known, from the same run.** `RESTAURANT_PUBLIC_ORIGIN` was the one value that arrived correct, and it
is the one `dev_instance.sh` exports. So substitution works when the variable is **set**; it is the
default branch that is unapplied. `${RESTAURANT_CURRENCY_CODE:-USD}` failed too, which rules out an
escaping problem with `/`, `:` or `*` — `USD` is as plain as a default gets.

**Not known, and deliberately not claimed anywhere in this slice.** Which podman-compose releases behave
this way, and whether assigning a variable **empty** in `.env` counts as supplying it. Both are
properties of a host. So nothing here predicts them: the new script asks the engine, and
`dev_instance.sh` asks again from the containers' own environment.

### The eleven that arrive wrong in silence

The five errors are the *good* case. What else was wrong:

| Variable | What arriving as placeholder text does |
|---|---|
| `RESTAURANT_NAME` | renders `${RESTAURANT_NAME:-My Restaurant}` as the restaurant's name, on every page |
| `ARGON2_*` (four) | `ReadInt` cannot parse it, so it is indistinguishable from absent — compiled-in values are used and nothing says so |
| `KITCHEN_…`, `TABLE_JOIN_…`, `TABLE_DISPLAY_…` | the same, silently |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | **its emptiness is the off switch.** The literal is not empty, so the exporter is attached and pointed at a hostname made of braces |
| `RESTAURANT_DATABASE_CONNECTION_STRING` | three nested placeholders inside one folded scalar; non-empty, so it passes validation and fails at connect time |

`OTEL_EXPORTER_OTLP_ENDPOINT` is the one that decided the shape of the fix. A setting whose whole
purpose is to be absent is the one that fails loudest when it is merely *unwritten*.

### The fix: ask, refuse, and make `.env` sufficient

**New `scripts/check_compose_substitution.sh`.** It does not predict from a version number:

1. enumerate the `${NAME…}` placeholders in `compose.yaml`;
2. subtract the ones that do not need a default — set and non-empty in the environment, or assigned in
   `.env` — because the *set* branch is the half observed working. If nothing is left, exit 0 without
   asking anybody anything;
3. otherwise render the file with `compose config` under a deadline and look for a surviving `${`.

Three-valued exit, and the middle value is the point: **3** the engine does not apply defaults, **2**
could not be determined here (no usable `config`), **0** fine. A missing subcommand is not a broken
engine, and a check that conflated the two would either block correct hosts or pass broken ones.

**Three helpers run it before doing work.** `dev_instance.sh` refuses *before* the twenty-minute image
build — a cold build is a poor thing to hand somebody along with the news that their stack was never
going to start. `quick_tunnel.sh` refuses before publishing a hostname. `run.sh` refuses before
`compose up`.

**`dev_instance.sh` asks again after `up -d`, from the containers' own environment.** One `inspect` per
container, `{{range .Config.Env}}`, grep for `${`. This is ground truth: it needs no subcommand, it
cannot be fooled by a `config` that renders differently from what it runs, and it is the only thing that
can settle the empty-assignment question. It runs *before* any waiting, because a placeholder in
`POSTGRES_USER` means initdb is already failing and the database wait would spend its deadline on a
settled question.

**`.env.example` now assigns every variable the stack interpolates.** It was assigning nineteen of
twenty-two — `OTEL_EXPORTER_OTLP_ENDPOINT`, `OTEL_EXPORTER_OTLP_HEADERS` and `CLOUDFLARE_TUNNEL_TOKEN`
were commented out, and a commented-out line supplies nothing. So the documented remediation
(`cp .env.example .env`, OPERATIONS §2) was incomplete in exactly the place that matters most.

**And `RESTAURANT_SOURCE_URL` is assigned empty there**, which is F-50's ruling applied one layer over.
That file spelled `https://github.com/kusl/myrestaurant`, so a fork whose first edit is
`RestaurantOptions.DefaultSourceUrl` — the edit F-50 says is the natural one — would have had it
silently overridden by the `.env` they were told to copy, and served their users a §13 offer pointing at
this repository. F-50 fixed that in `compose.yaml` and left the same value in `.env.example`.

### The build break Slice 28 shipped, and why

`DevInstanceLoopbackContractTests` did not compile. The assertion message was wrapped in
`string.Create(CultureInfo.InvariantCulture, …)`, and the concatenation fed to it ended with a **plain**
string literal rather than an interpolated one. An additive expression converts to an interpolated
string handler only when *every* operand is itself an interpolated string, so the call bound to no
overload. Checked against the tree afterwards: **every** `string.Create` concatenation in it prefixes
every operand with `$`, including the operands that have no holes — the idiom is uniform and this file
broke it.

The repair is not to add the missing `$`. Every hole in that message is already a `string`, so there is
nothing culture-sensitive to format and `string.Create` was never earning anything. It is a plain
interpolated string now, `using System.Globalization;` is gone with it (an unused using is an error
under CI's `TreatWarningsAsErrors`), and the reasoning is written above the assertion so the next person
does not reach for the same habit. Two other idioms were hardened while the file was open:
`Assert.Equal(collection.Count, …)` avoided in favour of `Assert.True`, since xUnit's analyzer has
opinions about the former, and `Assert.False(string.IsNullOrEmpty(x))` replaced with a length comparison.

### What was verified, and how

- **`scripts/check_compose_substitution.sh` run against three simulated engines** — a `podman-compose`
  shim whose `config` substitutes correctly, one that leaves `${…}` literal the way Debian's does, and
  one with no `config` subcommand at all — plus a fourth case with a complete `.env`. Results: exit 0,
  exit 3 with all 23 variables listed, exit 2 with the *undetermined* message, and exit 0 without asking
  the engine anything. Each is the intended path.
- **`bash -n` and `shellcheck`** on all four touched scripts, clean at `--severity=warning` (blocking in
  CI) and `--severity=style` (advisory), with the nine pre-existing scripts baselined style-clean first.
  One suppression, with its reason on the line above it: `SC2016` inside the container-environment grep,
  where `'${'` is the literal text being searched for.
- **`ComposeSubstitutionContractTests` ported to Python and proven sensitive** one regression at a time:
  either OTEL variable or the tunnel token commented out again (fact 2), a new variable added to
  `compose.yaml` and not to `.env.example` (fact 2), a plausible value given to
  `OTEL_EXPORTER_OTLP_ENDPOINT` (fact 3), the upstream URL restored to `RESTAURANT_SOURCE_URL` (fact 3),
  the `web` service's `environment:` key renamed so the scan misses twenty of twenty-two variables
  (fact 1, the non-vacuity guard), and a broken `services:` marker (throws rather than passing
  vacuously). A comment-only edit changes nothing, as a control.
- **`DevInstanceLoopbackContractTests` re-run** against the edited tree: four facts, still passing, and
  still failing on the six mutations Slice 28 recorded.
- **`ConfigurationSurfaceTests` re-run**: seventeen keys derived from `FromConfiguration`, all still
  present in `.env.example` — including `RESTAURANT_SOURCE_URL`, whose assignment is now empty, which
  that test accepts because it asserts the key appears rather than what it says.
- **`SpecificationVersionTests` ported and run**: header 1.14, entries 1.14 … 1.0 descending.
- **`ComposeDependencyContractTests` re-run**: four services, three edges, all `service_started`.
  `compose.yaml` is **not edited by this slice** — the file is correct and the engine reading it is not.
- **Brace, parenthesis and bracket balance walked** over both C# files with a string- and comment-aware
  scanner, with two existing files as controls.
- **Byte hygiene on every delivered file**: LF, one final newline, no CR, no whitespace-only lines, no
  trailing whitespace, no context-dump separator.

### The test count

1063 → **1066**. Three `[Fact]` methods added, none removed, no `[Theory]`. This assumes the local fix
to Slice 28's build break kept all four of that file's facts — it removed a call inside one assertion
message, not a method — and the file shipped here is the authority either way.

### What was NOT verified, and cannot be from here

**No engine ran this.** The substitution check was exercised against shims that imitate the two
behaviours; the real `podman-compose config` output on virginia has never been seen. If `config` is one
of the subcommands that version does not implement, the preflight exits 2 and the post-`up` container
check is what fires — which is why that second check exists.

**Nothing has compiled.** Two test files, balanced and ported and exercised, with `dotnet build` not run
on either.

**Whether `cp .env.example .env` is sufficient on that host is unknown**, and the honest answer is in the
tool rather than in this document: the check will say. If an empty assignment does not satisfy the
engine, the second remediation — a compose that applies defaults — is the one to take.

### Still open

**A CI job that runs the canonical stack on the canonical engine.** Fifth consecutive slice where this
is the real embodiment of a finding and is not in the archive. F-51, F-52, F-53, F-55 and F-57 were all
found by one person running one command on one machine that is not the workstation.

**`Permissions-Policy`**, carried since Slice 24. **Two operator actions** no archive can contain
(F-42).

---

# M6 Slice 30 — the screen it is read on (F-59), the gate that named one file (F-58), and a plan for the menu

Two things arrived in one conversation with the first person outside this project to be shown the running
application: an enhancement request for the menu, and a report that the Manage button on the
administration tables page sat off the right-hand edge of a phone screen.

The defect is shipped fixed. The enhancement is shipped **decided and planned** rather than half-built,
and the ordering is deliberate — the reason is in the plan and repeated below, because it is the one
judgement in this slice somebody might reasonably disagree with.

## What was found, and why nothing here could have found it

`AdministrationHome`, `AdministrationTables`, `AdministrationMenu` and `AdministrationSittings` each
declared their own inline copy of the same eighty lines of table CSS. Each copy ended in:

```css
.admin-people .admin-row-actions {
    white-space: nowrap;
    text-align: right;
}
```

inside a wrapper carrying `overflow-x: auto`. A five-to-eight column table in a 375px viewport scrolls
sideways, the action column is the last thing in it, so the only way into a row was reachable only by
scrolling past every other column of that row. Nobody decided that. It was one paste, four times — and the
same four pastes had invented the chip vocabulary **five** times over (four inline plus `app.css`, which
carried a comment apologising for the duplication and inviting somebody to fold it in) and
`.visually-hidden` **seven** times.

**The uncomfortable part is that no gate could have caught it and no test could have been written.** R§1
has said since rev 1 that guests order from their own phones. S§11.7 budgets the footer clock for a
handset in real detail — one wake per visible second, `contain: content` so a ticking clock does not
re-lay-out the page sixty times a minute. But **no section said a staff surface has to be operable on a
phone**, so there was no rule to enforce. 1066 tests green, fifteen §16.3 scenarios green,
`ci_local.sh --with-all` clear, and four pages an operator uses while standing at a table were unusable on
the device they would be standing there holding.

That is F-49's shape without its mitigating half. F-49 was a control that existed, worked, and that nobody
had decided on. This is a property everything assumed and nothing stated.

## Why the layout landed before the menu

The menu work adds four surfaces: a section index, a section editor, a rewritten item editor, and a guest
menu that is a grouped list of described items instead of one `<select>`. All four are read from a phone.

Written before the responsive vocabulary exists, they are written against the shape F-59 was found in, and
then all four need touching again. That is the engineering argument and it is real, but it is not the one
that decided it. **F-59 blocks user testing and the menu does not.** A menu without sections is a menu
somebody can still order from; a Manage button off the edge of the screen is a page an operator cannot use.

## S§11.12 — the rule, not the four pages

New normative section. The parts worth reading twice:

**The direction is the rule.** `app.css` states the narrow layout unconditionally and contains exactly one
`@media (min-width: 48rem)` query — the only place a width appears in the file. A `max-width` query would
say the wide layout is the default and the handset is the exception, and it fails in the worst available
direction: whatever is forgotten, or unapplied, lands on the layout that does not work on the screen the
software is actually used from. That was the previous arrangement, and it is what produced the defect.

**Every record-list cell states its own label, and this is not decoration.** Overriding `display` on a
table's parts drops the element's table semantics in every engine, so below the breakpoint the `<thead>`
stops being what associates a cell with a column. An unlabelled card is a column of bare values — `Table
4`, `2`, `19:04`, `$18.50` — with nothing on screen or in the accessibility tree saying which is which.
`data-label` is the replacement for the header, a cell whose content already says what it is opts out with
`data-label=""` (a decision written down rather than an omission), and the `<thead>` is clipped rather than
`display: none` so it survives for a reader in table mode at any width.

**A row's action is never a right-hand column.** It is the full width of the foot of the card, and the
row's primary cell is *also* a link — so the way in is at x=0 whatever the viewport does. Both, not either:
the link alone would leave a card with no visible affordance, and the button alone would put the target at
the bottom of a card whose top is what somebody taps.

**A 16px floor on every text control.** iOS Safari zooms the whole viewport when a focused control's text
is smaller, and it does not zoom back out — so one undersized field breaks the layout of the page around
it, on the platform most guests are holding. `.form-field select` and `textarea` are styled beside `input`
for the first time; the `textarea` is for Stage 3's description field, added now because the file was open.

**What the section does not require:** that every surface be *optimised* for a handset. `/kitchen` forces
the distinction — §11.2 and §10.3 describe a wall-mounted kiosk with a wake lock and a loud alert, so its
primary reader is a large screen. It satisfies §11.12 by being legible and operable at 375px, not by being
designed for one.

## What changed, and one thing that did not

**`.page-head`, not `.admin-header`.** The obvious move was to hoist the existing name into `app.css`.
That would have been wrong for a mechanical reason rather than a stylistic one: three pages this slice does
not restructure still declare `.admin-header` inline, and an inline copy of a shared name **wins on source
order at equal specificity** — so those pages would silently keep the old behaviour while the stylesheet
claimed otherwise. A new name cannot lose that argument. The two coexist for exactly as long as Stage 1b
takes, and `HandheldLayoutContractTests` holds the list of pages still carrying the old one.

**`AdministrationAreaLinks` + `AdministrationArea`.** The six area links were copy-pasted into six pages
and each copy omitted a different one — its own — so the row was a different row on every page and no page
was reachable from every other. Rendered once now, self-link included and marked `aria-current="page"`,
because on a handset it is a horizontally scrolled strip and a strip whose contents shift between pages
cannot be navigated from memory: the third pill has to be the third pill everywhere.

**The four pages lost their `<style>` blocks entirely.** Not reduced — removed. Every rule they held is
either shared vocabulary in `app.css` now or was a duplicate of one.

**`AdministrationMenu` is restructured and NOT given sections.** It says so in its own header comment, and
the `Describe` method's existing fallback — return the raw event type for anything it does not recognise —
means Stage 2's two new event types will read as themselves on that page before anybody teaches it their
names. Faking the grouping from a naming convention in the meantime would have been a second model to
delete.

## F-58, found by accident on the way to a §8 principle

`REQUIREMENTS.md` said **"Revision 4 — 2026-08-05"** in its header. Its revision history's newest entry
said **"Rev 5 — 2026-08-06"**. Six slices, green on every `dotnet test`.

`SpecificationVersionTests` exists precisely to stop that, and F-48's row in the ledger describes what it
asserts — header-matches-newest, entries-descend — both of which are true. What the row does not say is
*which file*, because the answer is a `const string`, and **a `const string` naming one path reads as
configuration rather than as a scope decision.** That is why six slices of readers took it for the former.
F-46 already established that a rule enforced as a list of examples is enforced as a list of examples;
this is the sharper corner: a list of one does not look like a list.

The subject is computed now — every `docs/*.md` with both a header version and a history section — with
both vocabularies read by one pattern rather than tabled per filename, and a **half-versioned** document
reported as a finding rather than skipped, because a header version with no readable history is one of the
two shapes in which a document could quietly leave the subject.

The same header also cited *"the companion `docs/TECHNICAL_SPECIFICATION.md` v1.6"* while that document
was at v1.14. That citation **loses its version number** rather than gaining a correct one: a version of
another document is F-50's class at the smallest possible stakes — a restatement joined to its subject only
by somebody remembering to edit it.

## The menu: decided, not built

ADR-0014 and `docs/MENU_AND_HANDHELD_PLAN.md` Stage 2. Every ruling is a `CREATE TABLE` or an
`ALTER TABLE`, and all of them are cheaper to argue with on a page than in a migration. The ones most
likely to draw a veto:

**Every item is in exactly one section, and the column is `NOT NULL`.** The alternative is a nullable
column and an "Uncategorized" bucket, which is a second branch on every reading surface forever, for a
state that exists only because the schema permitted it. The cost is accepted and named: on a database with
no sections, no menu item can be created, so the first ever use of the menu screen has one extra step and
the create-item form's job is to say so and link to section creation.

**`menu_section.name` is `citext NOT NULL UNIQUE`; `menu_item.name` stays neither.** §7 already rules that
a duplicate item name is a real menu — a weekly special genuinely is two rows called "Soup". A duplicate
*section* is never a real menu; it is a mis-tap the guest sees as the same heading twice with the items
split arbitrarily between them.

**`description` is `text NOT NULL DEFAULT ''`, and this one was forced rather than chosen.** §7's log ties
each nullable payload column to exactly the event types that carry it with paired *equality* CHECKs. An
optional payload cannot be tied that way: clearing a description has to write something, and if that
something is NULL the CHECK is violated by the very event recording the clearing. With `''` as "none",
clearing is a value like any other. This tree carries both idioms — `person.display_name` is nullable and
read through `NULLIF(btrim(…), '')` — so there was no house rule, and the constraint is the tie-breaker.

**`created` keeps carrying name and price only.** An item created with a description and a section writes
three events in one transaction, and the log reads *"Created as "Soup" at $4.50 / Description set / Filed
under Starters"*. Widening `created` would break `0001`'s two paired CHECKs against every `created` row
already in the database, because they are equalities and a description is optional. Three lines where one
would do, paid in prose rather than in a constraint that cannot be stated.

**`0003` replaces `menu_item_event`'s CHECK constraints by querying `pg_constraint` in a `DO` block.**
`0001` declared them inline, so PostgreSQL generated `menu_item_event_event_type_check`,
`menu_item_event_check` and `menu_item_event_check1` — deterministic, undocumented, and not a thing to
depend on in a script that runs at startup on somebody else's box. Verified before committing to it:
DbUp's PostgreSQL splitter is `PostgresqlQueryParser.ParseRawQuery`, whose `DollarQuotedStart` /
`DollarQuoted` states consume a whole tagged block, so the `;` inside a `DO $$ … $$` body does not split
the statement. Read from `DbUp/dbup-postgresql` at `main`, not from memory.

**Images: `bytea`, and the argument is F-38's.** §15 *defines* a recovery set as exactly two files, and
`restore_drill.sh` gates both on every push. A volume of image files makes it three, which means editing
that definition, both scripts, the drill and the runbook — and an operator who keeps taking backups the old
way has a set that restores an application whose menu has no pictures. Sixty items at 200 KB is 12 MB
inside a `pg_dump -Fc` that already compresses. And the direction is the reversible one: `bytea` → volume
is a migration that reads rows and writes files, volume → `bytea` is a migration that cannot find the
files.

**Comments are recorded as not startable, with three reasons rather than a hand-wave.** §17 already
records that `/register` has no rate limit and *why* it is not a two-line addition — a second naive
`AddRateLimiter` policy hijacks §4.2's single-valued rejection handler, so a refused registration would
answer *"too many pairing attempts"*. Comments hit the identical wall. Beyond that: a comment signed with a
display name is this system disclosing one person's name to strangers for the first time, against §5.3's
absolute table-to-table privacy, which is a `REQUIREMENTS.md` revision and not a schema decision. **Likes
are recommended instead**, and they need no new idiom at all: §8.3's `order_visibility_event` +
`order_visibility_current` is already an append-only per-person boolean folded by `DISTINCT ON`.

## What was verified, and how

- **`HandheldLayoutContractTests` ported to Python and run against the edited tree**: four facts, all
  passing. Then proven sensitive one regression at a time — a second breakpoint added; the one breakpoint
  inverted to `max-width`; the breakpoint block emptied; a page re-declaring `.record-actions` inline; one
  `<td>` losing its `data-label`; the wrapper class renamed on one page (caught by the fewer-than-four
  guard); a page not on the expected list acquiring the retired vocabulary; and a page on that list
  converted *without* the list being updated — caught, as it happens, by the label-parity fact rather than
  by the list fact, because a half-converted page has cells and no labels. Both non-vacuity guards were
  then attacked directly, by deleting every `.page-head*` and then every `.record-*` **selector** from
  `app.css` while leaving the comments that mention them: the guard asserts a selector begins a line
  rather than that the string appears anywhere, so both fire. A comment-only edit changes nothing, as a
  control.
- **The generalised `SpecificationVersionTests` ported and run against the tree *before* the fix**: it
  fails on `REQUIREMENTS.md`, header 4 against newest entry 5, which is F-58 reproduced by the gate that
  should have had it. After the rev 6 edit: two documents versioned, four skipped, zero half-versioned,
  both facts passing.
- **Razor tag-tree and `@code` brace balance** walked over all five new and rewritten components with a
  string-aware scanner: clean. Three untouched components as controls, of which `TableOrderSurface.razor`
  fails — and the failure is the checker's, not the file's: `IReadOnlyList<OrderLineView>` inside an `@{ }`
  block is a generic argument that looks like a tag. Worth recording rather than suppressing, because a
  checker that passes on everything is not looking.
- **`<td>` / `data-label` parity** across the four restructured pages: 5/5, 5/5, 9/9, 14/14.
- **Byte hygiene on every delivered file**: LF, one final newline, no CR, no trailing whitespace, no
  whitespace-only lines, no context-dump separator.
- **DbUp's statement splitter read from source** before the `DO` block was committed to in the plan.

## Test count

1066 → **1072**. Four `[Fact]` methods in the new `HandheldLayoutContractTests`, and the two in
`SpecificationVersionTests` are rewritten and renamed rather than added to —
`TheHeaderVersionMatchesTheNewestChangelogEntry` and `TheChangelogEntriesDescend` become
`EveryVersionedDocumentsHeaderMatchesItsNewestHistoryEntry` and
`EveryVersionedDocumentsHistoryEntriesDescend`, so the count there is unchanged at two. This is an
arithmetic prediction, not an observation: nothing was compiled or run.

## What was NOT verified, and cannot be from here

**Nothing compiled.** Two test files, five Razor components and one C# enum, balance-checked and
port-tested, with `dotnet build` run on none of them.

**No browser rendered any of this.** The whole of §11.12 is a claim about what a stylesheet does at two
viewport widths, and the strongest thing asserted here is its *structure*. The four pages have not been
seen at 375px by anything. That is the honest limit of this slice, it is exactly the gap Stage 1c exists to
close, and it is why the plan names the Playwright barrier as the first open item rather than leaving it
implied.

**`aria-current="@(area == Current ? "page" : null)"`** relies on Blazor omitting an attribute whose value
is null. That is the framework's documented behaviour for object-valued attributes and it is used
elsewhere in this tree, but it has not been observed in a rendered page here.

## Still open

**A CI job that runs the canonical stack on the canonical engine.** Sixth consecutive slice where this is
the real embodiment of a finding and is not in the archive.

**Stage 1b** — four pages still carry the retired table vocabulary, and
`HandheldLayoutContractTests.StillExpectedToCarryRetiredTableVocabularyIsExactlyWhatTheTreeCarries` names
all four, so finishing is deleting entries from that list. Then `.chip` and `.visually-hidden` come out of
the remaining nine components and the forbidden-prefix list is extended **in the same commit** — that
extension is the stage, not a tidy-up afterwards (F-46).

**Stage 1c** — the 375px Playwright barrier.

**Permissions-Policy**, carried since Slice 24. **Two operator actions** no archive can contain (F-42).

---

# M6 Slice 31 — the rule that was true of two files (F-60), and the helper that said it twice (F-61)

Two findings, neither of them reported by anybody. Both came out of reading the three terminal logs from
Slice 30's verification run — a run in which every gate was green on two hosts, 1070 tests passed, all
fifteen §16.3 scenarios passed, and a hundred thousand requests went through a quick tunnel at 737 RPS.
That is the useful shape of this slice: the evidence was in the output of a successful run.

## F-60 — a green suite that ran nothing

`compose.yaml` names its images `docker.io/library/postgres:17-alpine`. `scripts/restore_drill.sh` has
defaulted `DRILL_POSTGRES_IMAGE` to the same form since Slice 16. §14.1 has stated the rule since v1.11,
and F-51's ledger row explains at length why it is a correctness requirement rather than a preference.

Four references in the tree did not obey it:

```
tests/MyRestaurant.DataAccess.Tests/PostgreSqlFixture.cs:36        postgres:17-alpine
tests/MyRestaurant.EndToEnd.Tests/Harness/RestaurantHarness.cs:30  postgres:17-alpine
.github/workflows/ci.yml:265                                       postgres:17-alpine
.github/workflows/ci.yml:396                                       postgres:17-alpine
```

### The one claim this rests on was verified, not assumed

Everything else follows from whether Testcontainers normalises the reference before the engine sees it. It
does not, and the source says so in a comment. `MatchImage.Match`, in
`testcontainers/testcontainers-dotnet`, splits the remote name and records a registry **only** when the
first slash-separated segment contains a `.` or a `:`:

```csharp
var (registry, repository) = slices.Length == 2 && slices[0].LastIndexOfAny(['.', ':']) > -1
    ? (slices[0], slices[1]) : (null, remoteName);
```

with the comment above it stating that the implementation "does not resolve or set the default domain and
repository prefix." `DockerImage.FullName` then emits the registry only when one was recorded. So
`postgres:17-alpine` reaches the engine as `postgres:17-alpine`, and resolution is the engine's job —
through `unqualified-search-registries`, which Fedora's `containers-common` populates and a stock Debian
ships commented out. That is F-51's mechanism exactly, one layer over.

Read from the GitHub API rather than from memory, on the same discipline that read
`dotnet/aspnetcore` before the passkey slices.

### Why this is worse than F-51 rather than merely wider

F-51 was loud. `podman-compose up` printed three errors in three vocabularies and nothing started.

This is silent, and the reason is a design decision that is correct and stays. Both fixtures catch every
startup failure and set `SkipReason`, so the tests skip rather than fail — because a missing container
engine is not a broken product, and a suite that cannot tell the difference is a suite people stop
reading. The consequence on a host where the *reference* is what fails:

- `dotnet test` exits 0.
- Every test in `MyRestaurant.DataAccess.Tests` that touches the database skips.
- All fifteen §16.3 scenarios skip.
- The summary reports success.

And the skip reason an operator reads begins **"A container engine (Podman/Docker) was not reachable"**
and tells them to run `systemctl --user enable --now podman.socket`. If the engine was reachable, that
advice fixes nothing, the re-run produces the identical sentence, and the engine's own message naming the
real cause is three clauses further along, contradicting the headline it sits under.

### Two references no reading of this tree could have found

```bash
TUNNEL_RUNNER=(podman run --rm --network host docker.io/cloudflare/cloudflared)
```

```csharp
_container = new PostgreSqlBuilder("postgres:17-alpine")
```

Both are correct-looking lines, and both are outside the reach of any audit that could be written, because
an audit has to know where to look. This is why the remediation includes moving them into
`CLOUDFLARED_IMAGE` and a `PostgreSqlImage` constant. **Naming them is what puts them in scope** — not
that it reads better, which it also does.

`scripts/dev_instance.sh` has read a `CLOUDFLARED_IMAGE` variable since Slice 27, so `quick_tunnel.sh`
also gains an override it should always have had, and its reference gains the explicit `:latest` that
`compose.yaml` already writes.

### F-46, for the third time, and the sharpest instance of it

- **F-46**: a rule stated generally, enforced as six phrasings about one settings page.
- **F-58**: a rule stated generally, enforced against one file in a `const string`.
- **F-60**: a rule stated generally **in the same commit that applied it to one file**, by the person who
  chose the scope.

Each is narrower than the last, and each was written down correctly at the time. F-58's row already
recorded the general form of this — *a list of one does not look like a list* — and the register keeps
dropping.

### The gate, and why it is not a reversal of F-51's ruling

F-51's row is explicit: **"Deliberately not made executable, and the reason is F-41's:** the check would
have to be 'no `image:` value lacks a registry component', which is a text assertion about a file whose
real contract is behavioural, and it would pass on a tree where the images are qualified and the stack
still cannot start for the next reason."

That reasoning is right and is not being overturned. The CI job that runs the canonical stack on the
canonical engine remains the open item it has been for seven slices. What the new test asserts is a
different proposition, and one that is entirely a property of the tree: **a rule stated for the repository
is applied at every place in the repository it applies to.** F-51's objection is about substituting a grep
for a behaviour; this is not a substitute for a behaviour, it is a consistency check on the text, which is
the level a gate can reach without reporting findings on correct trees.

`ContainerImageReferenceContractTests`, three facts:

1. **The scan found a reference in each of the three positions it reads, and at least ten in total.**
   First and on its own, because both facts below pass against an empty set (F-41) — and a renamed
   constant, a re-indented workflow, or a `Containerfile` that grows a stage would produce exactly that
   without anything turning red.
2. **Every reference names a registry.** This is F-60.
3. **Every image name resolves to exactly one reference.** This is the fact the first two cannot reach.
   A reference that is fully qualified and has drifted to a different *version* from the one the canonical
   stack runs breaks no gate, produces no message, and means the suite passed against a database this
   project does not deploy.

The three positions a reference may occupy are a closed set on purpose: a YAML `image:` key, a
`Containerfile` `FROM` operand, or a value assigned to a name ending in `_IMAGE` (shell, YAML) or `Image`
(C#). The set is closed because a reference outside it is a reference no gate has an opinion about, which
is the state two of them were in.

The scan skips `docs/` entirely. Those files quote both the correct and the incorrect spelling on
purpose — the whole of F-51's row is about the difference — and a gate that failed on prose describing a
defect would be the same mistake in a new place.

**The test reads its own file**, which is a property worth keeping and one that has to be written for: a
constant in it named for what it holds, ending in `Image` and containing the string `image`, is read back
as a short-named image reference and fails fact 2. That happened once during authoring and is recorded in
the file beside the constant that caused it.

## F-61 — two closing lines for one Ctrl+C

From the end of the Slice 30 verification run:

```
^C[quick-tunnel] closing the tunnel (the stack keeps running; stop it with 'podman-compose down').
[quick-tunnel] closing the tunnel (the stack keeps running; stop it with 'podman-compose down').
```

`scripts/quick_tunnel.sh` ran `trap cleanup INT TERM EXIT` at line 123, and at line 185 registered a
second handler on the same three signals. A signal trap and the `EXIT` trap are independent
registrations, not one, so the body ran for the signal and then again on the way out.

**Nothing it did was harmful, and that is the finding rather than a mitigation.** `kill` on a reaped
process returns immediately, `rm -f` is idempotent, and the second pass had no work left. What was wrong
was the sentence. Two identical lines read as two tunnels, or as one that would not close, from a helper
whose entire job at that moment is to tell an operator what state the machine is in.

Third consecutive slice in which a helper's **output** was the defect while its actions were correct:
F-53 printed nothing, F-55 printed success over a dead container, this printed the truth twice.

### The fix does not depend on knowing which trap fired first

Which of `INT`, `TERM` or `EXIT` runs first depends on the bash version and on whether the terminal
signalled the whole foreground process group. The double fire **could not be reproduced in the authoring
sandbox** — a different bash, no controlling terminal, and `kill -INT <pid>` is not a `^C` delivered to a
process group — so the observation in the log stands as the evidence and the mechanism stands as the
structural explanation, rather than a claim about ordering that was never measured here.

The handler therefore carries a first-entry guard and calls `trap - INT TERM EXIT` on entry, so it runs
once under every ordering. The guarded handler *was* exercised in the sandbox and fires exactly once. The
second registration is deleted and its work — killing the log tail — folded into the one handler, with the
reason left at the site so nobody re-adds it.

### The class was audited, not the instance

| Script | Registration | Ruling |
|---|---|---|
| `scripts/quick_tunnel.sh` | twice, on `INT TERM EXIT`, handler prints | **the defect** |
| `run.sh` (smoke) | once, on `EXIT INT TERM`, handler silent and idempotent | **unchanged** — a rule that called this a defect would report findings on a correct tree (F-41) |
| `scripts/backup.sh` | `EXIT` only | correct by construction |
| `scripts/restore.sh` | `EXIT` only | correct by construction |
| `scripts/restore_drill.sh` | `EXIT` only | correct by construction |

**Deliberately not made executable.** The assertion would have to be *no handler is registered on both a
signal and `EXIT`*, which is false of `run.sh` for good reasons, so the gate would fail on a correct tree.
The rule that is actually true is about idempotence, and that is not decidable from the text. §14.3 states
it as a rule about the announcement.

## Two things from the same logs that are NOT findings

**The single HTTP 429 in 100,000 requests is Cloudflare's edge, not this application.**
`/healthz/live` carries no `[EnableRateLimiting]`, there is no global limiter, and §4.2's policy applies
to `/display/pair` alone. Recorded here as a baseline rather than in the ledger: 100,000 requests at 2,000
concurrency through a free quick tunnel, **737 RPS**, P50 90 ms, P95 125 ms, P99 215 ms, max 5.07 s, one
429. The same run against `/healthz/ready` — which opens a connection, executes `SELECT 1`, and asks DbUp
whether the schema is current — returned **100,000 of 100,000** with P50 91 ms. A readiness probe that
does real work being indistinguishable from a liveness probe that does none, at that volume, is worth
knowing.

**`Error: no container with name or ID "myrestaurant_caddy_1" found`** during
`run.sh --containers-only` is podman-compose's own internal `rm` of a container that does not exist yet.
Noise from the engine, printed at error level, and not this repository's to fix.

## What was verified, and how

- **The scanner ported to Python line for line** and run against the tree: 12 references, all fully
  qualified, every image name resolving to exactly one reference, all four positions populated.
- **Each of the three facts proven sensitive by its own regression**, which is the part that matters:
  reintroducing F-60 in `ci.yml` fails facts 2 **and** 3; changing one fixture to `postgres:18-alpine` —
  fully qualified, no short name anywhere — fails **only** fact 3, which is why fact 3 exists; renaming
  the three `*_IMAGE` variables fails fact 1 on both the total and the missing position.
- **`bash -n` and `shellcheck --severity=style`** clean on the edited script. Note the version: 0.9.0 in
  the authoring sandbox against 0.11.0 on the workstation, so this is weaker evidence than usual and the
  workstation's run is the one that counts.
- **The guarded cleanup handler executed** in a simulation of the script's shape: one signal, one line.
  The *unguarded* shape could not be made to double-fire there, which is recorded above rather than
  presented as a reproduction.
- **Razor-free slice**: no component touched, so none of Slice 30's tag-tree walking was needed.
- **Brace balance** on all three C# files with an untouched sibling as a control: clean.
- **`SpecificationVersionTests` ported and run** over `docs/`: two documents qualified, header 1.16 against
  newest changelog entry 1.16, seventeen entries descending, no half-versioned document. The first port
  was written too strictly — it scanned whole files for entry patterns rather than the text after a
  history heading, and reported `BUILD_PROGRESS.md` as half-versioned. Corrected against the real test's
  regexes, which is the reason to port rather than to reason about it.
- **Byte hygiene** on every delivered file: LF, one final newline, no CR, no trailing whitespace, no
  whitespace-only lines, no context-dump separator. One exception, and it is pre-existing:
  `.github/workflows/ci.yml` ends with two newlines in the current tree and `check_tree.sh` passes it, so
  the gate's rule is looser than the scan and the file is left as the gate finds acceptable.
- **Both workflows and `compose.yaml` parse** as YAML.

## Test count

Observed **1070** (workstation and `ci_local.sh --with-all --with-e2e`, both). Predicted **1073** after
three new `[Fact]` methods in `ContainerImageReferenceContractTests`. Arithmetic, not an observation:
nothing here was compiled or run.

**A correction to Slice 30's entry.** It predicted 1072 from a baseline of 1066. The run reports 1070. The
rewritten `SpecificationVersionTests` has **two** `[Fact]` methods where it previously had four, not two —
so the two rewritten facts replaced four rather than two, and the count landed two lower than predicted.
The prediction was wrong in the direction predictions in this project tend to be wrong: it counted what
was added and under-counted what was replaced.

## What was NOT verified, and cannot be from here

**Nothing compiled.** One new test file and two rewritten fixtures, balance-checked and port-tested, with
`dotnet build` run on none of them.

**No engine resolved any reference.** The claim that a short name fails on Debian rootless Podman is
F-51's observation plus a verified reading of Testcontainers' source; it was not reproduced. What *is*
asserted here is only that the tree is now consistent, which is all the new test claims.

**CI's service container was changed without being run.** `image: docker.io/library/postgres:17-alpine`
under `services:` and `DRILL_POSTGRES_IMAGE` set to the same string. Docker normalises a short name to
exactly that reference, so both should be a store hit rather than a second pull, and the comment
explaining the cache-sharing intent was updated to say so — but the first push is what proves it. If the
drill's step starts pulling on every run, that comment is where to look.

## Still open

**A CI job that runs the canonical stack on the canonical engine.** Seventh consecutive slice where this
is the real embodiment of a finding and is not in the archive. F-60 makes the case slightly stronger:
there is now a second class of defect — a fixture that skips instead of failing — that only a run on that
host would surface.

**Stage 1b** — four pages still carry the retired table vocabulary.

**Stage 1c** — the 375px Playwright barrier.

**Stage 2's boundary is corrected in the plan, not yet built.** `menu_section_identifier NOT NULL` means
the schema and the data access cannot land without the section-create page, the item form's picker, and a
harness `CreateMenuSectionAsync` — five §16.3 scenarios drive the real create form. Three things pull
forward out of Stage 3; the rest of Stage 3 stays where it is.

**Permissions-Policy**, carried since Slice 24. **Two operator actions** no archive can contain (F-42).

# M6 Slice 32 — the barrier F-59 would have failed, and the reason it took two slices (F-62)

One finding, and it is about a sentence rather than about code. Slice 30 fixed F-59 — four administration
index pages whose only affordance sat off the right-hand edge of a 375px screen — wrote §11.12, and made
its *structure* executable in `HandheldLayoutContractTests`. It also recorded, carefully and in three
places, the one assertion it deliberately did not make: that a control is **reachable** inside a 375px
viewport. That is the assertion F-59 would have failed, and it is the only one that would have.

This slice makes it. §16.3 gains a sixteenth scenario.

## F-62 — a gap justified by a property the tree does not have

The reason recorded for the deferral was this, verbatim from `docs/MENU_AND_HANDHELD_PLAN.md`:

> the fifteen §16.3 scenarios all run in one default context, and giving one of them a second viewport is
> either a second browser context per run or a resize that every subsequent scenario inherits

`RestaurantHarness` holds one **browser**. Every scenario calls `StartInstanceAsync`, which calls
`RestaurantInstance.StartAsync`, which calls `browser.NewContextAsync(...)` and returns a context of its
own; `OpenIsolatedPageAsync` mints and tracks further contexts on request and closes them in reverse on
disposal. A viewport is a property of a context. There was never a shared default context to resize, and
nothing for a later scenario to inherit.

### The contradicting evidence was in the file the claim was about

`RestaurantInstance`'s class summary carries a paragraph headed *why more than one browser context*, and
`RestaurantHarness.StartInstanceAsync`'s own summary says it brings up "a browser context with a virtual
authenticator" per instance. Both were written in Slice 2 and neither has changed. So this was not a
subtle property that had to be derived — it is the second paragraph of the type's documentation.

### What makes it a finding rather than a wrong guess

The sentence was written once, while planning, and by the close of Slice 30 the same claim had been copied
into **S§16.4**, into **F-59's row in the ledger**, and into the plan. Three documents asserting a
property of the test harness; none of them written by reading it.

That is **F-50's shape** — a cross-document citation that outlives what it cited — applied in the worse
direction. F-50 was a claim that stopped being true, so there was a moment at which a reader comparing the
two could have caught it. This was never true, so there is no such moment: every copy agreed with every
other copy, forever, and the only thing that could have found it was somebody opening the file.

### And the cost was not the gap

Stage 1b is roughly 2,400 further lines of Razor across four large surfaces. Scheduling it before the
barrier meant converting those pages exactly the way the four pages in F-59 were written — by hand, with
nothing in the tree able to decide whether the result was reachable. **So the stage order is swapped**, and
that is the design decision in this slice most worth vetoing if it is unwelcome: the plan said 1b next,
this ships 1c instead, and the argument is that a barrier built after the conversions cannot check them.
Building it first also retro-proves Slice 30's four pages, which nothing until now could.

## The barrier

`Harness/HandheldReach.cs` (new) navigates a surface, waits on `.page-head`, and takes every measurement
in **one** `EvaluateAsync` round trip — not one `BoundingBoxAsync` per element, because a dozen
consecutive round trips interleaved with layout produce a dozen numbers that describe a dozen moments.

Three assertions, each against the viewport with one pixel of tolerance:

| What | Read from | Why it is the assertion and not a proxy for one |
|---|---|---|
| No surface is wider than its own viewport | `documentElement.scrollWidth` vs `clientWidth` | This is F-59's mechanism stated as a number: a page wider than the screen is a page whose far column is reached by dragging sideways |
| Every action lies inside it | `getBoundingClientRect()` per `.record-actions` and `.page-head-action` control | The finding itself, per element |
| Every control is ≥ 44px tall | the same rects, plus `.page-head-areas a` | §11.12's other control rule, equally undecidable from text |

### Three properties of it are rulings

**The viewport is asserted before anything else**, and read back from the document rather than from the
option that set it. At Playwright's default 1280 every other assertion in the scenario passes and the
whole thing means nothing (F-41). It is compared as a *ceiling* with twenty pixels of allowance under it,
because `clientWidth` excludes a classic scrollbar and headless Chromium draws one on every page here —
so the honest measurement at a 375px viewport is a dozen-odd pixels under 375, and an equality assertion
would have failed on a correct tree on the first run.

**The count of measured controls is asserted.** Seven are expected: two rows plus a create button on
people, one and one on tables, one and one on menu, nothing on sittings. The floor is six rather than
seven so that one surprise is not a red suite, and it is still under every way this goes quietly wrong —
a renamed `.record-actions` leaves three, a renamed `.page-head-action` leaves four.

**The widest element on the page is collected and may never fail a run.** A page may legitimately contain
an element wider than the viewport inside a scroll container of its own, and `.page-head-areas` — the
horizontally scrolled strip of area links §11.12 specifies — is exactly that: its pills extend past the
right edge by design. A walk that failed on those would report a finding on a correct tree, which is the
mistake this barrier was deferred a slice to avoid. So the walk skips anything inside a scroller, and even
then it only ever writes the sentence that explains a failure. The two numbers decide.

### The surface list is the migration

Four paths today — the four Slice 30 restructured — and Stage 1b adds a line per page it converts. Same
arrangement as `HandheldLayoutContractTests.StillExpectedToCarryRetiredTableVocabulary` and the same
reason (F-47): finishing is then something somebody decides rather than something nobody notices.

### What the scenario arranges, and the one fixture that is doing real work

An administrator through §3.6's wizard **at 375px** — not arranged around, because a layout barrier that
only applies after a wide sign-in has a hole in the one place a new operator starts. Then a staff account,
a table and a menu item, so each index has a row; two rows on people, because a one-row list cannot fail
an assertion only the widest row would fail, and it is a *row* that F-59 was about.

The counter account's display name is `Anastasia Featherstonehaughwolstenholmeworthington`, and the
unbroken run is the point. A single token longer than the card is wide is the one input that can push a
record card past the viewport, and §11.12 relies on `overflow-wrap: anywhere` to stop it. **The keyword is
load-bearing:** `break-word` breaks the line but leaves the element's *min-content* width at the length of
the token, so a table or flex context still sizes to it and the page still scrolls sideways; only
`anywhere` shrinks min-content. `app.css` says `anywhere`, on `.record-list td` and `.record-link`, and it
is an inherited property so it reaches `.record-secondary` where the display name renders. Without this
fixture the scenario asserts that the stylesheet contains the right word; with it, that the word does the
right thing.

Sittings is measured **empty**, and that is stated in the scenario rather than glossed. Opening a sitting
needs a guest, a token and a join, which is scenario 3's subject. The page still has to lay out and its
record list is the same §11.12 vocabulary — so what is untested there is a row on that page, not the page.

## What was verified, and how

- **The embedded JavaScript actually ran.** It was extracted from the raw string literal, `node --check`
  parsed it, and it was then executed against a hand-built fake DOM. Two results are the ones that matter:
  a pill 100px past the right edge **inside** an `overflow-x: auto` strip was correctly *ignored* by the
  overflow walk, and a rogue element outside any scroller was correctly *named*. The false-positive guard
  is the whole reason this barrier was safe to write, so it is demonstrated rather than argued.
- **`describe()` output checked against real markup shapes**: whitespace collapses, so a `Manage` anchor
  spanning two source lines reads back as one name rather than as a paragraph of indentation.
- **Brace, paren and bracket balance** on all four C# files, string-literal-aware and raw-string-aware,
  with an untouched sibling as a control: clean. **Proven sensitive**: deleting one closing brace is
  reported at the line the block opened on.
- **CS1620 scan** (every operand of a `string.Create(IFormatProvider, …)` addition chain must be `$"…"`):
  clean, and **proven sensitive** by breaking one operand into a bare literal — three findings on one
  call. **CS4007 scan** (no `await` in an interpolated string hole): clean, and proven sensitive.
- **Byte hygiene** on every delivered file: LF, exactly one final newline, no CR, no trailing whitespace,
  no whitespace-only line, no context-dump separator.
- **Every `StartInstanceAsync` call site audited** before the parameter was inserted. Fifteen call sites;
  every one passes either the first positional argument or named arguments, and `cancellationToken:` is
  named at all fifteen — so a `bool handheld` before it changes no existing call.
- **`SpecificationVersionTests` ported and run** over `docs/`: header 1.17 against newest changelog entry
  1.17, entries descending, no half-versioned document.
- **CA1861** was caught during authoring rather than in CI: the selector pair was an array literal at a
  call site, which is a constant array built per call and an *error* under `ContinuousIntegrationBuild`.
  It is a `static readonly` field now, the way `RestaurantHarness` already holds its install arguments.

## What was NOT verified, and cannot be from here

**Nothing compiled.** One new file, three edited, `dotnet build` run on none of them.

**No browser rendered anything, and this is the slice where that hurts most.** `npm install
playwright-core` succeeded in the authoring sandbox; the Chromium download is blocked by the egress
allow-list, and there is no system browser. So the claim that these four pages *pass* the barrier rests on
reading `app.css` line by line — `.record-actions .button-secondary { width: 100% }`, `overflow-wrap:
anywhere` on the cells, `min-width: 0` on the strip's parent, `clamp()` padding on the panel — and not on
a measurement. **The first run on the workstation is what proves it**, and it is the one run in this
project's history where a red result would be information rather than a defect: if a page does overflow,
the failure message names the widest element outside a scroll container, which is the diagnosis.

**The `EvaluateAsync` return is parsed out of a `JsonElement` by hand** rather than deserialised into a
type, deliberately: property naming, constructor selection and the accessibility of a nested record are
three things that would otherwise sit between a correct measurement and a green run, none of them visible
in the file. That choice is untested too, but it has a smaller surface than the alternative.

## Test count

Observed **1073** (Slice 31's run, workstation and `ci_local.sh --with-all --with-e2e`, both). Predicted
**1074** after one new `[Fact]`. Arithmetic, not an observation: nothing here was compiled or run. The
§16.3 subtotal moves from 15 to 16.

Slice 31's prediction was 1073 from a baseline of 1070, and 1073 is what the run reported — the first
prediction in several slices to land exactly, and it landed because it counted only additions with no
replacements to under-count.

## Still open

**Stage 1b** — four pages still carry the retired table vocabulary (`EventExplorer`, `HiddenRecords`,
`ManageSitting`, `TableDisplays`), and `.chip` / `.visually-hidden` are still declared inline by three
more (`ManageMenuItem`, `ManagePerson`, `ManageTable`). Extending `SharedSelectorPrefixes` to cover both
is part of emptying them, not a tidy-up afterwards (F-46). Each converted page adds a line to
`HandheldAdministrationPaths` in the same commit.

**A CI job that runs the canonical stack on the canonical engine.** Eighth consecutive slice.

**Stage 2's boundary**, corrected in the plan and not yet built — and one number in it moved here:
`CreateMenuItemAsync` now drives the real create-item form in **six** of sixteen scenarios, not five,
because the handheld barrier needs a row on `/administration/menu` to measure.

**Permissions-Policy**, carried since Slice 24. **Two operator actions** no archive can contain (F-42).

# M6 Slice 33 — Stage 1b's first half, and two rules that were true and unenforced (F-63, F-64)

Two findings, and neither was in the work this slice set out to do. That is worth stating first, because
the interesting part of the slice is not the conversion.

The work: `EventExplorer.razor` and `HiddenRecords.razor` join §11.12's shared vocabulary. They were the
last two pages carrying a hand-rolled copy of §11.4's row of area links, and the only two carrying a
filter form, so they went together rather than in size order.

The findings: §11.12's *exactly one breakpoint* was asserted about one file while being a rule about the
tree (**F-63**), and five CSS custom properties were read fifty-five times across eight components and
declared nowhere, so eight surfaces had been rendering a palette nobody chose (**F-64**).

## What landed, and why these two pages

Four pages carried the retired per-page table vocabulary when Stage 1b opened. These are the two smaller
ones, and the reason they are a pair is not their size.

**They were the last two copies of the row `AdministrationAreaLinks` exists to replace.** Five
`<a class="button-secondary">` elements in a `.admin-header-actions` div, each copy omitting its own page.
F-59's resolution named that as the defect — six sets of five links, no two the same, no page reachable
from every other — and Slice 30 ended it on four pages of six. On a handset §11.12 makes the row a
horizontally scrolled strip, and a strip whose contents shift between pages cannot be used from memory:
the third pill has to be the third pill everywhere. It was, on four screens out of six.

**They were the two copies of one filter form.** `.event-filter` and `.hidden-filter` were the same twelve
lines — `display: flex; flex-wrap: wrap; align-items: flex-end`, a `.form-field { margin: 0 }` override,
and an actions div — with no column fallback anywhere in them. On a 375px screen that wraps five fields
into five rows of unequal width, each as wide as its own content, with the submit button wherever the wrap
left it. One shared `.filter-form` / `.filter-fieldset` / `.filter-choice` / `.filter-actions` /
`.filter-count` vocabulary in `app.css` replaces both, handheld-first, widened inside the existing single
query. `.filter-` is the third entry in `SharedSelectorPrefixes`, so a third copy cannot be written.

**Both are in the §16.3 scenario 16 barrier now**, which takes it from four surfaces to six.

## F-63 — a rule about the tree, asserted about one file

§11.12 has said since v1.15 that the layout is written handheld-first and widened by exactly one
`min-width: 48rem` query, and Slice 30 made that executable in the same commit that wrote it.
`HandheldLayoutContractTests` counts the width media queries in `wwwroot/app.css`, asserts there is one,
asserts it is a `min-width`, and asserts no `max-width` appears. In `app.css`.

**The same section grants every component an inline `<style>` block**, deliberately and with a stated
reason: `App.razor` links the static stylesheet rather than the scoped bundle, so CSS isolation is not
active anywhere in this tree, and a rule exactly one page reads may stay with that page. Twenty-one
components carry such a block. A width query inside any of them is a second breakpoint — the same number
written in a second place, which is the F-48 / F-50 / F-56 mechanism — and nothing here could have seen it.

**No component had one.** That is the only reason this is a gap and not a defect, and it is the same kind
of true F-59 was: true until somebody writes the page that breaks it.

### How it was found

By needing to write one. `HiddenRecords` has a `.hidden-record-money { text-align: right }` block and a
`.hidden-facts` grid; `EventExplorer` had `.event-when { margin-left: auto }`. All three are a wide-layout
preference expressed unconditionally, and the obvious repair for each is a media query on the page.
Reaching for that and asking *what in this tree would stop me* is the whole of the discovery.

**F-46's shape for the fourth time**, and the fourth time the general sentence and the narrow scope were
written by the same person in the same commit.

### The repair, and the three parts of it that are choices

The fact is renamed `TheTreeIsWrittenHandheldFirstThroughExactlyOneBreakpoint` — F-38's habit applied to a
test name — and reads every component `<style>` block as well as `app.css`.

- **An inline query gets its own assertion, ahead of the count.** The count's message is *there are two of
  these*; the message a reader needs is *yours is in the wrong file*.
- **The surviving query is asserted to be in `app.css`.** "Exactly one breakpoint, declared in a
  component" satisfies a count of one and abandons the arrangement the count exists to protect.
- **The component walk carries its own non-vacuity guard** — at least eight blocks. A scan that matched no
  `<style>` blocks would report that no component declares a query while having read nothing, which is
  F-41 in the one place this repair could have introduced it.

§11.12 says *in the whole tree* where it said the stylesheet, and adds the note the conversion earned: a
page can usually avoid needing a query at all. Both cases here were avoidable. `margin-left: auto` inside
a wrapping flex row pushes the timestamp to the right edge of whichever line it lands on, so on a handset
it ended up alone on its own line, far from the event it timestamps — deleting it reads correctly at every
width. `.hidden-facts` is already `repeat(auto-fit, minmax(9rem, 1fr))`, which is intrinsically responsive.
And `.hidden-record-money`'s right alignment is money rather than an affordance, so §11.12's
right-hand-column rule does not reach it — that is recorded at the rule rather than left to be rediscovered.

## F-64 — fifty-five references to five properties nothing declares

`--muted-foreground` is named thirty times. `--rule` sixteen. `--surface-sunken`, `--chip-background` and
`--chip-foreground` three each. The eight components are `EventExplorer`, `HiddenRecords`,
`ManageMenuItem`, `ManagePerson`, `ManageSitting`, `ManageTable`, `TableJoinCode` and `CounterJoinCode`.

`app.css`'s `:root` declares nineteen custom properties. None of those five is among them.

**An undeclared custom property is not an error in CSS.** `var(--muted-foreground, #666)` resolves to
`#666` and renders. No browser warns, no linter runs here, MSBuild has no opinion about a stylesheet, and
no gate in this repository looked. So all fifty-five rules worked, and all fifty-five were using a colour
that had been typed once as a guess beside a name that never existed.

The measurable consequence:

| Those eight pages drew | Every other surface draws |
|---|---|
| `#666` and `#999` greys | `--ink-soft` `#55636f` |
| `#e5e5e5`, `#ccc`, `#eee` hairlines | `--hairline` `#e2e6ec` |
| `#f4f4f5` / `#f7f9fb` sunken panels | `--surface` `#f7f8fa` |
| a chip in `#f0f0f0` on `#333` | `.chip` in `#eef1f5` on `--ink` |

### Why no reader could have caught it

**The mechanism is a fallback, and a fallback is what a careful author writes.**
`var(--hairline, #e2e6ec)` and `var(--rule, #e5e5e5)` are indistinguishable at the site, on the page, in
review, and in a diff. The code that is wrong reads as *more* careful than the code that would have been
correct.

That is F-49's shape — something that existed, worked, and that nobody had decided — plus that property.
And it was found sideways, while checking one grey on one page during an unrelated conversion.

### The repair, and the ruling inside it

Every reference names the declared property the rule always wanted: `--rule` → `--hairline`,
`--muted-foreground` → `--ink-soft`, `--surface-sunken` → `--surface`, `--chip-foreground` → `--ink`.
`--chip-surface` is **declared** and `.chip`'s literal `#eef1f5` reads it, because two pages still carry a
chip rule of their own until Stage 1b empties them and three copies of one hex value is precisely how
`--chip-background` came to be referenced by pages and declared by nobody.

**Fixed across all eight files rather than deferred behind a second migration list**, and that is the
decision worth vetoing if it is unwelcome. The repair is a name substitution inside `<style>` blocks: no
markup moves, so it cannot break a Razor compile, and it was applied programmatically and then
diff-verified — 110 changed lines, every one a `var(--…)` line. Two of the eight (`ManageSitting`,
`ManagePerson`) are Stage 1b's next conversion targets and are therefore touched twice, which costs
nothing under full-file delivery. The alternative was an F-47-style expected-holders list, and a list whose
only purpose is to defer a name substitution is a list this project has ruled against writing.

**Deliberately not asserted:** that a reference to a *declared* property carries no fallback. Over a
hundred references across sixteen components still do. Where the name exists they are dead code rather
than a wrong colour, and a gate that failed on them today would report findings on a tree whose every
colour is correct (F-41). §11.12 states that half as a **should**, and Stage 1b removes them as it empties
each block — which is an arrangement rather than a promise, because the blocks are being emptied anyway.

## The area row finally has an assertion

`HandheldLayoutContractTests` gains a fact that has been owed since Slice 30. F-59's resolution named the
copy-paste and the omission-per-copy as the defect; §11.12 restated it; `AdministrationAreaLinks`'s own doc
comment restated it again. Nothing in the tree enforced it. Slice 30 converted four pages, this slice
converted the last two, and a seventh administration page written tomorrow could have pasted the row back
with every gate green.

**How a hand-rolled row is told from a legitimate link.** By counting the distinct area paths a component
names literally, *excluding its own route*. A hand-rolled row names five or six; a "Back to tables" link
names one. The self-route exclusion is what makes the threshold two rather than a fudged three:
`HiddenRecords` legitimately links to its own path — that is the "Show everything" filter reset — and a
threshold that failed on it would report a finding on a correct tree (F-41).

Two guards beneath it, failing in opposite directions: at least six components must render the shared
component, because a page that dropped the row entirely names no paths and would pass the count; and the
shared component must still name all six areas, because a row that had quietly lost two links is the F-59
defect with the paste removed and the omission kept.

## The barrier grew, and the reach selector grew with it

Six surfaces, and `.filter-actions a, .filter-actions button` joins the reach selector.

**That is a membership decision rather than a widening.** The selector's rule is *the thing an operator
opened the page in order to press*, not *a record row's action*. §11.4 makes both explorers read-only —
there is no record action and no page-head action on either — so a barrier that measured only the first two
selectors would have visited two new pages and measured nothing on either, which is exactly the empty-set
failure the count guard exists to catch (F-41).

Nine controls expected, floor eight: two rows plus a create button on people, one and one on tables, one
and one on menu, nothing on sittings, one filter submit on each explorer. A renamed `.record-actions`
leaves five, a renamed `.page-head-action` leaves six, a renamed `.filter-actions` leaves seven.

**The stream checkboxes are deliberately outside it.** A checkbox is 1.35rem by declaration —
`.form-field input[type="checkbox"]` sets `min-height: 0` on purpose — so the target is the
`.filter-choice` row around it, which carries `--touch-target` in `app.css`. Asserting a 44px box on the
input itself would report a finding on a correct tree. What is untested is that the row does its job, which
is the same honest gap `.record-tick` has.

**`/administration/hidden-records` is measured empty**, on the terms sittings already was and stated the
same way: putting a row on it needs a guest, a token, a join, an order and a close, which is scenario 11's
arrangement rather than this scenario's. The page still lays out, its filter is the shared vocabulary, and
its submit is measured. What is untested there is a record card, not the page.

## A red suite caught before it shipped

`HiddenRecordJourneys.cs` pins `form.hidden-filter #filter-username`, `form.hidden-filter
button[type='submit']`, `form.hidden-filter .hidden-filter-actions a` and `p.hidden-count`. Renaming those
classes without that file is scenario 11 failing on a correct page. The selectors are repointed at the
shared names; `p.hidden-none` is kept exactly as it was, because it is the harness's handle for "the list
is empty after an unhide" rather than a style, and that reason is now written where the class is.

Every selector any harness reads was checked against both converted pages, not just the ones that changed.

## What was verified, and how

No .NET SDK here, so everything below is text-level and says so.

- **All six facts were ported to Python and executed against the delivered tree: all six pass.** Run
  against the tree as it was before this slice, **five fail** — the `.filter-` prefix is undeclared, four
  pages carry retired vocabulary instead of two, only four components render the area row, both explorers
  name five area paths besides their own, and twenty-three distinct undeclared-property references are
  reported. That is a before/after demonstration rather than a claim.
- **Every fact proven sensitive by a planted regression** — eight plants, eight caught:
  a `max-width` query in a component (F-63's own regression); a second `min-width` in `app.css`; a
  `.filter-actions` rule re-declared inline; a `data-label` deleted from a record cell; a converted page
  left on the migration list; the area row pasted back into `EventExplorer`; one link deleted from
  `AdministrationAreaLinks`; and `var(--text-quiet, #666)` planted in a converted page.
- **Brace, paren and bracket balance** on all four C# files and all nine Razor files, string-, char-,
  verbatim-, raw-string- and comment-aware, with untouched siblings as controls: clean. **Proven
  sensitive** — deleting one closing brace is reported at the line the block opened on.
- **CS1620 scan** (every operand of a `string.Create(IFormatProvider, …)` chain must be `$"…"`): clean,
  and **proven sensitive** by breaking one operand into a bare literal. **CS4007 scan** (no `await` in an
  interpolation hole): clean, and **proven sensitive**.
- **Razor tag-tree balance** on every touched component: clean, and **proven sensitive** by deleting one
  `</nav>`. The scanner's first version reported twenty findings that were all C# generic type arguments
  inside `@code` read as HTML tags; it excludes `@code` and the directive lines now, which is recorded
  because a scanner that reports findings on a correct tree is the thing F-41 is about.
- **The `var()` substitution was diff-verified**: 110 changed lines across eight files, every one a
  `var(--…)` line, no markup touched.
- **Byte hygiene** on every delivered file: LF, exactly one final newline, no CR, no trailing whitespace,
  no whitespace-only line, no context-dump separator.
- **`SpecificationVersionTests` ported and run** over `docs/` — header 1.18 against newest changelog entry
  1.18, entries descending, two documents qualifying, no half-versioned document. The first port of it was
  wrong in a way worth recording: it read history entries from the whole file rather than from after the
  `## Changelog` heading, and flagged `BUILD_PROGRESS.md` for the string `**v1.7**.` in a sentence. The
  pristine tree failed it identically, which is what said the port was wrong rather than the tree.

## What was NOT verified, and cannot be from here

**Nothing compiled.** Thirteen files edited, `dotnet build` run on none of them. The Razor files are the
likely site of a compiler complaint, and the markup changes here are small and structural.

**No browser rendered anything.** The claim that these two pages pass the barrier rests on reading
`app.css` — `.filter-actions` is a stretch column below the breakpoint, `.button-primary` carries
`min-height: var(--touch-target)`, `.filter-form` is a flex column — and not on a measurement. The first
run on the workstation is what proves it, and a red result here would be information: the failure message
names the widest element outside a scroll container.

**The colour change is not verified as an improvement**, only as a correction. Eight pages will render
slightly different greys and hairlines than they did yesterday: `--ink-soft` `#55636f` is cooler and
darker than `#666`, and `--hairline` `#e2e6ec` is lighter and cooler than `#e5e5e5`. That is the palette
those pages were always supposed to be drawing. Whether it looks better on a phone is a judgement, and it
belongs to whoever is holding one.

## Test count

Observed **1074** (Slice 32's run, workstation and `ci_local.sh --with-all --with-e2e`, both). Predicted
**1076** after two new `[Fact]` methods in `HandheldLayoutContractTests`. Arithmetic, not an observation:
nothing here was compiled or run. The §16.3 subtotal stays at 16 — scenario 16 walks six surfaces instead
of four, which is a longer scenario rather than another one.

Slice 32's prediction was 1074 from 1073 and 1074 is what the run reported, which is two in a row.

## Still open

**Stage 1b's second half** — `TableDisplays.razor` and `ManageSitting.razor` still carry the retired table
vocabulary, and `.chip` / `.visually-hidden` are still declared inline by `ManageMenuItem`, `ManagePerson`
and `ManageTable`. Extending `SharedSelectorPrefixes` to cover both is part of emptying them, not a
tidy-up afterwards (F-46). Each converted page adds a line to `HandheldAdministrationPaths` and loses one
from `StillExpectedToCarryRetiredTableVocabulary`, in the same commit.

**The redundant `var(--declared, #literal)` fallbacks** — over a hundred, across sixteen components. A
*should* rather than a *must*, removed per block as Stage 1b empties them.

**A CI job that runs the canonical stack on the canonical engine.** Ninth consecutive slice.

**Stage 2's boundary**, corrected in the plan and not yet built. One number moves again:
`CreateMenuItemAsync` drives the real create-item form in **six** of sixteen scenarios, unchanged by this
slice — the handheld barrier still needs its menu row, and the two surfaces added here need no menu item.

**Permissions-Policy**, carried since Slice 24. **Two operator actions** no archive can contain (F-42).

# M6 Slice 34 — the number written twice (F-65), the fifth copy (F-66), and the gate that read prose (F-67)

Stage 1b's second half, which was supposed to be a conversion and an extension of one list. The conversion
happened. The extension turned out to be blocked by the gate it was an extension of, and two more findings
were sitting in the pages it was meant to cover.

## What was asked for

"Continue." Stage 1b of `docs/MENU_AND_HANDHELD_PLAN.md` had two pages left carrying the retired table
vocabulary and nine more that were never record lists, and the plan said `.chip` and `.visually-hidden`
come out of all of them with `SharedSelectorPrefixes` extended in the same commit — F-46's rule, that a rule
enforced against a list of examples is enforced against a list of examples.

## The three findings, in the order they were found

**F-67 came first, because it blocked the work.** Adding `.chip` and `.muted` to `SharedSelectorPrefixes`
reported findings on `KitchenBoard`, `CounterBoard` and `CounterSitting` — three pages that are correct.
Each carries a CSS comment naming the shared vocabulary it leans on: *"Anything shared — .panel, .chip,
.muted"*. The fact matched a prefix against the **text** of a `<style>` block. It stripped Razor comments
and not CSS comments, while `EveryCustomPropertyTheTreeReadsIsDeclaredInTheStylesheet` — thirty lines below
it, in the same file — stripped both.

So the set of prefixes that gate could hold was the set of shared names nobody had written a sentence
about. That is not a rule about the tree; it is a rule about the tree's comments, and it bounded the
migration Stage 1b existed to finish. The gate had also never applied its own standard evenly: two
assertions further down it checks that `app.css` *declares* rather than merely mentions each prefix, with a
comment saying "Declared, not merely mentioned".

**F-66 is the fifth copy of F-59, one register down.** Four pages — `ManageMenuItem`, `ManagePerson`,
`ManageTable`, `TableDisplays` — each declared their own inline copy of one `.manage-*` detail vocabulary.
Twenty duplicated rules, five of them drifted between copies. The duplication is the cheap half; three
overlaps with `app.css` are the finding, and each fails in a different way:

- **`.visually-hidden` was still `clip: rect(0, 0, 0, 0)` on all four.** Slice 30's own plan entry says
  `.visually-hidden` was centralised "with `clip-path` rather than the deprecated `clip`". The inline copy
  wins from later in the document at equal specificity, so those four pages never received it, and every
  document in this tree said they had.
- **`.chip-ok` and `.chip-warn` were pinned to literals absent from `:root`** — `#fdecea` / `#a3261c`
  against `--danger-surface` `#fbeaea` and `--danger-ink` `#7f1d1d`. Four screens drew a visibly different
  red from the palette. That is F-64 with the property *declared and overridden* rather than undeclared and
  defaulted, which is the harder direction to notice, because `:root` is correct.
- **`.manage-inline-form input` and `select` had no `min-height` and no font-size floor** — just
  `padding: 0.45rem 0.6rem`. So the one control each of those pages exists for (rename this table, revoke
  this role, change this price, revoke this display) was about 34px against §11.12's 44, and typing in it
  zoomed an iPhone's viewport and left it there. **Both halves of the control rule, broken on four
  surfaces, by a page-local rule.**

None of the four had ever been laid out at 375px by anything, because scenario 16 walked indexes only.

**F-65 was found by writing the assertion F-66 needed.** §11.12 requires `--touch-target` (2.75rem, 44px)
of "buttons, links that act as buttons, checkbox rows, and the session links in the header". `.session-link`
and `.link-button` both declared `min-height: 2.25rem`. That is 36px, and between them those two rules are
the **sign-out control in the header of every page in both layouts** and the destructive action on four
administration surfaces: "Revoke role", "Revoke display", "Deactivate account", "Deactivate table".

The comment above `.session-link` is the reason nobody caught it. It said the links *"carry the §11.12
target height"* and that the rule used *"vertical padding rather than a min-height, so the row does not grow
on a wide screen"*. The three lines beneath declared a `min-height` and no padding at all. Two claims, both
false about the declaration they introduce, in the precise place a reader checking compliance stops.

Nothing measured it either: the barrier's reach selector covers a record row's action, a page-head action
and a filter's submit, and a `.link-button` is none of those on any surface. This is F-48's mechanism inside
a stylesheet — the number written a second time in a second place, where a custom property exists so it is
written once — with the extra property that the second copy is the one that renders.

## What the gate reads now

Simple selectors, from one helper both halves of the fact consult. CSS comments stripped; each rule's
prelude taken as the text following the previous rule's closing brace; an at-rule's prelude discarded by its
leading `@` while the rules nested inside it are still read; every prelude split on commas and on each
combinator.

That last split is what catches the harder half. `.chip-ok` matches `.chip` by prefix, which a text scan
also managed — but `.sitting-record .muted` is a page overriding a shared name at *higher* specificity, and
under the old scan an ancestor selector was a hiding place. `ManageSitting` had exactly that, adding a
`font-size` to `.muted` within its own panel.

**The prefix list is seven now:** `.record-`, `.page-head`, `.filter-`, `.manage-`, `.chip`, `.muted`,
`.visually-hidden`.

## The name that was kept, and why that is the opposite of last time

`.page-head` exists because `.admin-header` could not be reused: a shared declaration under the old name
would have been overridden on every page still carrying the inline copy, so the four converted pages would
have silently kept the old behaviour while the stylesheet said otherwise. That argument is about a migration
that **spans slices**.

This one spans none. All four holders are emptied in the same commit that declares the shared version, so
there is no window in which a page could win the specificity argument, and the name is kept. The reasoning
is recorded beside the declaration, because the two decisions look contradictory and the difference between
them is the only thing that makes either correct.

## The list that became an emptiness assertion

`StillExpectedToCarryRetiredTableVocabulary` named the files still holding a retired name, compared for set
equality so that neither the list nor the tree could quietly be wrong about the other. Four pages in Slice
30, two in Slice 33, two here — and then the expected set is empty, at which point the list is a name for
zero things and F-47 says to delete it rather than keep it as a monument.

What replaces it is **stronger** than what it asserted: a new page reaching for the old shape now fails
without anybody deciding that it should. It gains a non-vacuity guard the list did not need, because an
emptiness assertion over a walk that found nothing passes, and a list compared for equality does not.

## The barrier grew from six surfaces to ten

Six indexes, then four detail surfaces built from identifiers the scenario already mints and reads back off
each surface's own success panel: an account, a table, that table's display roster, a menu item.
`.manage-inline-form button` joins the reach selector on the same membership rule — *the thing an operator
opened the page in order to press*. `.manage-back` stays outside it, because leaving is not that thing.

**Converting a page and not measuring it is how F-59 survived four milestones.** All four of those pages had
34px form controls at the moment they were added here.

**Fifteen controls expected, floor fourteen.** Three on people, two on tables, two on menu, none on
sittings, one filter submit on each explorer, two on the account, one on the table, two on the item, one on
the roster. A renamed `.record-actions` leaves eleven, a renamed `.page-head-action` eleven, a renamed
`.manage-inline-form` nine, and a renamed `.filter-actions` thirteen — the smallest loss, so it is what sets
the floor.

**`/administration/sittings/{sitting}` is converted and deliberately not measured.** Reaching a sitting needs
a guest, a table token and a join before there is an identifier for the route, which is scenario 3's
arrangement and three scenarios of setup for one measurement; and an invented identifier meets the not-found
panel, which has no page head, so the barrier would fail on arrival rather than measure anything. That
page's conversion rests on the contract test and on reading `app.css`. It is the trade hidden-records is
already measured with, one route deeper — and the page head being what distinguishes an arrived detail
surface from the not-found panel is now written where the barrier waits.

## What was verified, and how

No .NET SDK here, so everything below is text-level and says so.

- **All seven facts were ported to Python and executed against the delivered tree: all seven pass.** Run
  against the tree as it was before this slice, **five fail** — 54 shared-name re-declarations across five
  components, two controls under the touch target, four record-list pages where seven are now expected, and
  two retired-vocabulary holders. Before and after rather than a claim.
- **Every fact proven sensitive by a planted regression — ten plants, ten results as designed:** a
  re-declared `.manage-heading`; `.link-button` back at 2.25rem; `/kitchen`'s line button shortened to 2rem;
  a deleted `data-label`; `admin-header` returning to `ManageSitting`; a `max-width` query inside a
  component; the whole `.manage-` prefix renamed out of `app.css`; every `var(--touch-target)` replaced by a
  literal `44px`; and a `.sitting-record .muted` descendant override — all reported. And **the plant that
  must not fire does not**: a CSS comment naming `.chip`, `.muted`, `.manage-rule` and `.record-list`, which
  is what three counter and kitchen components actually contain, passes. That is F-67 demonstrated rather
  than asserted.
- **Brace, paren and bracket balance** on all three C# files and all five Razor files, string-, char-,
  verbatim-, raw-string- and comment-aware, with untouched siblings as controls: clean. **Proven sensitive**
  by deleting one `</div>`, which is reported at the line the block opened on.
- **CS1620 scan** (every operand of a `string.Create(IFormatProvider, …)` chain must be `$"…"`): clean, and
  **proven sensitive** by breaking one operand into a bare literal. **CS4007 scan** (no `await` in an
  interpolation hole): clean.
- **Razor tag-tree balance** on every touched component: clean. **The scanner had to be fixed first, and
  that is worth recording** — its first version ended each tag at the next `>` and reported twenty-seven
  findings on a correct tree, every one a self-closing component whose attribute held a lambda:
  `For="@(() => Input.Name)"` contains a `>` inside quotes, so the match ended early and the trailing slash
  was never seen. It reads quoted attribute values now. A scanner that reports findings on a correct tree is
  the thing F-41 is about, and it very nearly caused five correct files to be "fixed".
- **One report remains on an untouched file** and is the same class of limitation: `TableOrderSurface.razor`
  has `IReadOnlyList<OrderLineView>` inside an inline code region, read as an HTML tag. The scanner excludes
  `@code` blocks and not inline `@{ }` regions. That file is not touched by this slice; the report is noted
  rather than acted on.
- **`SpecificationVersionTests` ported and run** over `docs/`: header 1.19 against newest changelog entry
  1.19, entries descending, two documents qualifying, no half-versioned document. **The first port was wrong
  in the same way Slice 33's was**, and it said so the same way: it matched history entries as `**vN` /
  `**Rev N` at the start of a line and missed `REQUIREMENTS.md`'s `- **Rev 6 — …**` bullets, so only one
  document qualified. **The pristine tree failed it identically**, which is what said the port was wrong
  rather than the tree. Corrected against the gate's own two regexes.
- **Byte hygiene** on every delivered file: LF, exactly one final newline, no CR, no trailing whitespace, no
  whitespace-only line, no context-dump separator.

## What was NOT verified, and cannot be from here

**Nothing compiled.** Fourteen files edited, `dotnet build` run on none of them. The Razor files are the
likely site of a compiler complaint, and two of the five are substantially restructured.

**No browser rendered anything.** The claim that four detail surfaces now pass the barrier rests on reading
`app.css` — `.manage-inline-form` is a flex column below the breakpoint, its controls carry
`min-height: var(--touch-target)` and `font-size: max(1rem, 1em)`, its buttons are `width: 100%` — and not
on a measurement. **A red result on first run would be information rather than a surprise**, and it is worth
saying which way: these four pages have never been measured, so if one of them lays out wider than 375px the
failure message names the widest element outside a scroll container, and that is a finding this slice
created the means to see rather than a regression this slice introduced.

**Slice 33 has not been run either.** The terminal logs in this repository are Slice 32's — 1074 tests, 16
scenarios. Slice 33's tree was committed and not yet exercised, and this slice is built on top of it. If the
first run is red, the two slices' changes are both candidates, and the honest order to read them in is
Slice 33 first.

**The colour and layout changes are not verified as improvements**, only as corrections. Four detail pages
will render a different warning red than they did yesterday (`--danger-ink` `#7f1d1d` rather than `#a3261c`),
their small forms will be a stack rather than a row on a phone, and the header's sign-out control is 8px
taller on every page in the application. Whether that reads better is a judgement, and it belongs to
whoever is holding the phone.

## Test count

Observed **1074** (Slice 32's run). Predicted **1077**: Slice 33 added two `[Fact]` methods and this slice
adds one. Arithmetic, not an observation — nothing here was compiled or run, and the 1074 baseline predates
both slices. The §16.3 subtotal stays at **16**: scenario 16 walks ten surfaces instead of six, which is a
longer scenario rather than another one.

## Still open

**1b's last surfaces** — `CounterBoard`, `CounterSitting`, `KitchenBoard`, `TableHistory`, `TableJoinCode`,
`CounterJoinCode`. None re-declares a shared name any more, so there is nothing left to extend and no list
to empty; what remains is a judgement per surface about its own layout at 375px, which is a different kind
of work. `KitchenBoard` needs its own judgement rather than the same treatment, for the reason §11.2 gives.

**The redundant `var(--declared, #literal)` fallbacks** — around a hundred, across the components that still
have blocks. A *should* rather than a *must*.

> **Corrected in Slice 35 (F-69).** That figure was already wrong when this line was written: emptying four
> blocks in this slice took it to **fifty, across seven**, and the number was the entire argument for the
> rule being a *should*. It is left here rather than edited, because what it recorded is what was believed —
> and it is the fourth document to carry the figure, which is the finding.

**`.sitting-meta` is declared by two components** — `ManageSitting` and `TableArea` — which is a two-copy
duplicate of a name `app.css` does not own. Not in this slice's scope and recorded so that it is a decision
next time rather than a discovery.

**A CI job that runs the canonical stack on the canonical engine.** Tenth consecutive slice.

**Stage 2's boundary**, corrected in the plan and not yet built. `CreateMenuItemAsync` still drives the real
create-item form in six of sixteen scenarios, and as of this slice the handheld barrier also opens that
item's management page — so the menu row it needs is now load-bearing twice.

**Permissions-Policy**, carried since Slice 24. **Two operator actions** no archive can contain (F-42).

# M6 Slice 35 — the palette written twice (F-68), the count that was already wrong (F-69), and the gate nobody wrote down (F-70)

Stage 1b's tidying, which was recorded as a *should* and turned out to be three findings, one of which was
sitting inside this repository's own arithmetic.

## Read this first: Slice 34 is green

`docs/llm/vendor/claude-output.txt` and the terminal logs in this repository are **Slice 34's**, and Slice
34 passed: **1078 tests, 0 failed, 0 skipped**, 16/16 §16.3 scenarios, all eight `ci_local.sh` gates, boot
smoke clean, 334 authored text files in tree-hygiene scope. Slices 33 and 34 both landed and both work.

**Slice 34 predicted 1077.** The run returned 1078, and the difference is F-70. That is where this slice
started.

## What was asked for

"Continue." Slice 34's *Still open* named the seven components that still carry a `<style>` block and the
roughly hundred redundant `var(--declared, #literal)` fallbacks inside them, and called the second one a
*should* rather than a *must*. Both were correct about what remained. Both were wrong about it being
tidying.

## The three findings, in the order they were found

**F-69 came first, because it is what the tidying was for, and the number was wrong.** §11.12 left the
no-fallback rule as a *should* on one stated ground: *over a hundred references across sixteen components*
still carried one, so a gate that failed on them would be reporting findings on a tree whose colours are
all correct (F-41). That was true in v1.18. §16.4 repeated it, F-64's ledger row repeated it, and
`MENU_AND_HANDHELD_PLAN.md` repeated it — and by the time the last of them was written, **Slice 34 had
emptied nine of the sixteen blocks**. The true figure was **fifty, across seven**.

Both halves of the sentence were wrong and the component count was wrong by more than half. This is F-50
and F-62's shape applied to a claim about the tree — F-62's own row says *a reason for not doing something
is a claim about the tree, and it is checked against the tree before it is written down, the more so before
it is cited* — and this claim was cited three times by the work that falsified it. What makes it a finding
rather than a typo is that the number was **load-bearing**: it was the entire argument for the rule being a
*should*, and fifty across seven is an afternoon.

**F-68 came second, out of the first file opened.** `TableHistory.razor`'s `.history-confirm` — the guest's
irreversible-hide warning, the one panel in the guest area whose whole job is to look alarming before
something that cannot be undone — was:

```
border: 1px solid #f5c2c0;
background: #fdecea;
color: #7f1d1d;
```

`--danger-surface` is `#fbeaea`. `--danger-hairline` is `#f0c7c7`. `--danger-ink` is `#7f1d1d`. So one of
the three was the palette written a second time and **two were the palette copied and then drifted** — and
`#fdecea`/`#f5c2c0` is the same pair Slice 34 removed from four `.chip-warn` copies. **F-66, found in a
fifth place, one area over from where that sweep was looking.** It survived because F-66 was about
administration and this is a guest page.

The class is bigger than the instance. **Ninety-five colour values are written outside `:root`** — fifty
inside `var()` fallbacks and forty-five bare — and of the forty-five:

- **Twenty are byte-identical to a property declared in `:root`.** `#ffffff` six times against
  `--surface-raised`, **three of them inside `app.css` itself**; `#b45309` five times against
  `--caution-ink`; `#1e7a37` and `#7f1d1d` twice each; `#e6f4ea` once.
- **Three are `rgba(22, 32, 43, α)`, which is `--ink` in decimal**, inside two box-shadows. That is the one
  form of duplicate no reader scanning for `#hex` would ever have found, and it is why the pattern behind
  the new gate matches `rgb()` and `hsl()` as well as hex. A fourth, `rgba(127, 29, 29, 0.94)` on the table
  display's unavailable veil, is `--danger-ink` at 94%.
- **Five are near-copies**, all on `/administration/events`'s stream badges: a near-black that is not
  `--ink`, a neutral tint that is not `--chip-surface`, and three role tints a few bits off
  `--danger-surface`, the accent tint and the amber. **An exact copy is a duplicate; a near copy is a
  decision nobody made.**
- **Twenty-two have no property to reach for at all** — the kitchen's alert red, the amber notice pair, the
  accent tints in `app.css` — and that is the *cause* rather than a separate problem. Once a literal was
  normal in a block, the ones that had properties got written as literals too, by habit.

**Why F-64's gate is structurally unable to see any of it.** F-64 asks whether a custom property a rule
*reads* is declared. A rule that reads nothing and writes `#b45309` is invisible to it. Identical
wrong-palette failure, direction reversed — F-46's lesson arriving as a *direction* rather than as a list
of files.

**F-70 came third, from reconciling a number.** Slice 34 predicted 1077 tests; the run returned 1078.
`HandheldLayoutContractTests` holds **eight** `[Fact]` methods. §16.4 says *"Seven assertions"*. The class's
own summary says *"three of the seven facts"*. This file's Slice 34 write-up says *"all seven facts were
ported to Python… all seven pass"*. The defect ledger has no row for the eighth, the specification changelog
has no entry, and `_CHANGES.md` has no line — **every artefact §18's atomic-documentation rule requires of
a behaviour change, absent.**

The eighth assertion is `OverflowWrapIsDeclaredExactlyOnceOnTheBodyElement`, and the first thing to say is
that **it is a good rule**. `overflow-wrap` is inherited, so eight declarations are eight copies of what one
declaration states and the copies reach only the elements somebody thought of (F-48) — and this repository
already contains the measured consequence, in Slice 32's own write-up: the long unbroken display name
rendered correctly on `/administration`, which had a copy, and pushed the page sideways on
`/administration/people/{id}`, which did not. It arrived from outside this project's slice discipline.

**Two things here should have caught it, and one of them did.** §16.4 states an assertion count for nine
test classes and **nothing in the tree had ever compared any of them to a file** — a count in prose is one
fact written in two places, which is F-48, F-50, F-56 and F-65's mechanism met in a fifth. And the test-count
prediction exists precisely so a run can disagree with it. It disagreed, by one, in a green summary line at
the end of a passing run, and nobody read it.

It was also carrying two defects of exactly the kind the file it lives in is otherwise scrupulous about:

- **No non-vacuity guard on its component walk.** *"Declared exactly once in the tree"* was satisfied by a
  scan that read `app.css` and opened no component at all — the count would still be one, and the assertion
  would pass having looked at one file (F-41).
- **Its keyword check ran against the composed report line rather than the value.** `Assert.Contains("anywhere", only)`
  where `only` is `"src/…/app.css (anywhere)"`, so a repository path containing the word would have
  satisfied it.

## What the palette looks like now

Ten new properties in `:root`, each declared at the value the literal already had, so **twenty of the
forty-five substitutions change nothing on any screen**:

| Property | Value | What it is |
|---|---|---|
| `--accent-ink` | `#ffffff` | the ink that sits on the accent |
| `--accent-surface` | `#e7f4f1` | accent tint behind a result |
| `--accent-surface-soft` | `#f2f8f7` | the paler tint marking a chosen row |
| `--accent-hairline` | `#b9ddd6` | accent tint border |
| `--caution-surface` | `#fff7ed` | the amber notice background |
| `--caution-ink-strong` | `#7c2d12` | the darker amber legible *on* that surface |
| `--danger-signal` | `#b91c1c` | the saturated red of a state seen across a room |
| `--danger-veil` | `rgba(127, 29, 29, 0.94)` | the display's unavailable overlay |
| `--shadow-panel` | two `rgba(22, 32, 43, …)` stops | the panel shadow, written whole |
| `--shadow-footer` | one stop | the footer shadow |

**Two naming decisions are written beside the declarations, because both look wrong at a glance.**
`--accent-ink` is white and so is `--surface-raised` — two names for one value, on exactly the principle
`--focus-ring` and `--accent` already were: *the ink on the accent* and *a raised surface* are different
jobs that need not move together. And `--danger-signal` is deliberately **not** `--danger-ink-strong`,
because it carries *less* contrast on white than `--danger-ink` does; `-strong` in this palette means more
contrast (`--accent-strong`, `--caution-ink-strong`), and this one is louder rather than stronger.

## The seven rules whose colour changes, and they are the ones to veto

Everything else is byte-identical. These are near-copies collapsed to the property that already existed —
F-66's precedent, where four chip reds were moved onto the palette — and each carries a comment beside the
rule saying what moved:

| Surface | Rule | Was | Now |
|---|---|---|---|
| `/table/history` | `.history-confirm` background | `#fdecea` | `--danger-surface` `#fbeaea` |
| `/table/history` | `.history-confirm` border | `#f5c2c0` | `--danger-hairline` `#f0c7c7` |
| `/kitchen` | `.kitchen-menu-item.is-off` background | `#fef2f2` | `--danger-surface` `#fbeaea` |
| `/kitchen` | `.kitchen-menu-item.is-off` border | `#fca5a5` | `--danger-hairline` `#f0c7c7` |
| `/counter/sittings/{id}` | `.counter-settle-confirm` background | `#fef2f2` | `--danger-surface` `#fbeaea` |
| `/administration/events` | `.event-stream-badge` ink and background | `#1f2937`, `#e5e7eb` | `--ink`, `--chip-surface` |
| `/administration/events` | the three stream tints | `#fde8e8`, `#e0f2f1`, `#fef3c7` | `--danger-surface`, `--accent-surface`, `--caution-surface` |

**The rule applied, and it is the line between correction and redesign.** A literal identical to an existing
property reads that property. A literal that is a *near*-copy of an existing property reads that property
and the change is stated. A literal with **no** existing property gets one declared at its current value —
because choosing between two undeclared literals is a design judgement, and this slice is a correction.

`.counter-settle-confirm` keeps `--danger-signal` for its 2px border rather than dropping to
`--danger-hairline`: it is the last thing between somebody and a settled total that is never rewritten
(§5.3), and a 2px hairline is not what that should look like. That is recorded at the rule.

## What the two new gates read

**The ninth fact in `HandheldLayoutContractTests`** takes the palette in one assertion and the fallbacks in
another, fallbacks first — because a fallback is what made F-64's undeclared names indistinguishable from
declared ones, so it is the layer over the other, and it is the cheaper of the two to clear.

It reads **declaration blocks only**, and that is the whole of what keeps it off correct trees. `#ffffff` in
a value and `#blazor-error-ui` in a prelude open with the same character; a gate that confused them would
report a finding on a stylesheet that is right (F-41). The helper is the mirror of the one F-67 introduced —
`SimpleSelectorsDeclaredIn` takes preludes, `DeclarationBlocksIn` takes values, both resting on the fact
that a declaration block contains no `{`. An at-rule wrapper is skipped by having another `{` before its own
`}`, while the rules nested inside it are still reached, which is what makes a colour written inside the
breakpoint query visible.

**`TestingSectionContractTests` compares §16.4's counts to the files.** Subject computed, nothing named —
F-58's fix shape, taken on the first opportunity: the section is read, every backticked `*Tests.cs` in it is
resolved against the tree **by file name**, and where the same paragraph states a count, the two are
compared. Both citation forms are admitted, the full repo-relative path and the elided
`…/Documentation/SpecificationVersionTests.cs`, because §16.4 uses both and a gate that understood one would
be a gate about typography.

A paragraph naming a class and no count is out of scope and silent — prose may describe what a class asserts
without enumerating it — and `MinimumCountedClasses` is what stops *that* becoming the way to satisfy the
gate, since deleting a number makes a paragraph stop being a pair.

## What was verified, and how

No .NET SDK here, so everything below is text-level and says so.

- **All nine facts of `HandheldLayoutContractTests` were ported to Python and executed against the delivered
  tree: all nine pass.** Run against the tree as it stood before this slice, **the ninth fails twice** — 50
  fallbacks and 95 literals. Before and after rather than a claim.
- **The ninth fact proven sensitive seven ways, seven results as designed:** one fallback restored in
  `KitchenBoard`; a bare `#7f1d1d` written back into `CounterSitting`; a bare colour in `app.css` outside
  `:root`; **a colour written inside `app.css`'s one breakpoint query**, which is what demonstrates the
  nested at-rule is read; every colour deleted from `:root`, which trips the palette floor; `:root` renamed,
  which trips the palette-absent assertion; and every `var(--ink-soft)` in the tree replaced by an
  equivalent `rgb()`, which trips the reference floor.
- **And proven NOT to fire three ways, which matters more here than usual.** An id selector named
  `#abcdef-notice` — six hex characters after a `#`, in a prelude — passes. `background-color: transparent`
  passes. And a CSS comment naming both `#b45309` and `var(--hairline, #e2e6ec)` in prose passes. **That
  last one is left in the delivered tree on purpose**, inside `KitchenBoard`'s block comment: a future
  version of this fact that forgot to strip CSS comments would fail on arrival rather than quietly bounding
  its own reach, which is what F-67 did for four slices. Measured both ways — with comment-stripping, 0
  literals and 0 fallbacks; without it, 14 literals and 1 fallback, all of them prose.
- **`TestingSectionContractTests` ported and run, and it is proven sensitive by the tree itself with nothing
  planted.** Against §16.4 as delivered before this slice: **nine (class, count) pairs found, eight agree,
  one disagrees** — `HandheldLayoutContractTests`, said seven, held eight. Against the edited specification:
  nine pairs, nine agree. The pair count also caught something worth noting — the document-version test is
  cited with an elided path and *was* accurate, so restricting the parse to full `tests/…` paths would have
  found eight pairs instead of nine and silently skipped a correct claim.
- **Brace, paren and bracket balance** on both C# files, string-, char-, verbatim-, raw-string- and
  comment-aware, with untouched siblings as controls: clean. **Proven sensitive** by deleting one closing
  brace, reported at the line the block opened on.
- **CS1620 scan** (every operand of a `string.Create(IFormatProvider, …)` chain must be `$"…"`): clean.
  **CS4007 scan** (no `await` in an interpolation hole): clean. **A CS interpolation-brace scan was added to
  the pass and it caught a real defect during authoring**: the palette fact's first draft wrote
  `$"No ':root { ... }' block was found"`, where `{ ... }` is an interpolation hole and not a literal brace.
  It would not have compiled. Rephrased rather than escaped.
- **Razor tag-tree balance** on every touched component: clean — **and the scanner had to be fixed again,
  which is the third time in three slices and now worth calling a pattern.** Its first run reported
  `<TableDisplay> never closed` on `TableDisplay.razor`, pointing at `@inject ILogger<TableDisplay> Logger`:
  a C# generic argument on a directive line, read as markup. Slice 34's version had the same class of bug in
  a different place, and Slice 34 recorded one standing false report it chose not to chase —
  `TableOrderSurface.razor`'s `IReadOnlyList<OrderLineView>` inside an inline `@{ }` region. Both are the
  same thing: **the scanner was reading C# as HTML wherever C# appears outside a `@code` block.** It now
  blanks directive lines and inline `@{ … }` regions as well, and the result is worth stating precisely:
  **zero reports across every `.razor` file in `Components/`, including the one Slice 34 left open.** Still
  **proven sensitive** — deleting one `</div>` from `KitchenBoard` is reported twice, naming the `<div>` that
  is now closed by a `</section>` and the `<section>` left open, both at the lines they opened on. A scanner
  that reports findings on a correct tree is exactly what F-41 is about, and this one has now done it twice
  and been made to stop.
- **`SpecificationVersionTests` ported and run** over `docs/`: header 1.20 against newest changelog entry
  1.20, entries descending, two documents qualifying, no half-versioned document.
- **Byte hygiene** on every delivered file: LF, exactly one final newline, no CR, no trailing whitespace, no
  whitespace-only line, no context-dump separator.

## What was NOT verified, and cannot be from here

**Nothing compiled.** Fourteen files edited and one created; `dotnet build` run on none of them. The new
`TestingSectionContractTests` is the likeliest site of a complaint — it is the only new C# file, it uses
`Dictionary` deconstruction in a `foreach`, and `Assert.True` inside a helper that returns a string is a
shape nothing else in this tree does.

**No browser rendered anything, and this time no browser could.** §16.3 scenario 16 measures geometry at
375px; it is told nothing about which red a warning panel is, and a barrier that asserted a computed colour
would be asserting the palette against itself. So the palette rules are text assertions and *only* text
assertions, and §11.12 now says so where it previously said the contract is asserted at two levels. What
they guarantee is that a drift is impossible; whether the value in `:root` is the right value is a
judgement.

**The seven colour changes are corrections, not improvements.** Four surfaces render a different warning
red than they did yesterday, and `/administration/events`'s five badges all shift. Whether that reads better
belongs to whoever is holding the phone.

**F-70's eight-times figure is not independently verifiable from this repository.** The assertion's own
comment says `overflow-wrap: anywhere` was declared eight times, the stylesheet it describes is two commits
back, and there is no git history in this sandbox. It is preserved and **labelled as that assertion's own
account** rather than restated as fact — inventing corroboration would be the F-62 error in miniature.

**Nothing was done about how a gate arrives undocumented in general.** What is closed is the case that
happened: a class §16.4 *cites* gaining an assertion the citation does not know about. A brand-new test file
the document never mentions is still invisible, and the stronger version — *every test class is cited by
§16.4* — is deliberately declined, because the three response-header classes are described there as a group
by directory and that is better prose, so the assertion would report a finding on a correct tree.

## Test count

Observed **1078** (Slice 34's run, confirmed green). Predicted **1080**: one new `[Fact]` in
`HandheldLayoutContractTests` and one in the new `TestingSectionContractTests`. Arithmetic, not an
observation. The §16.3 subtotal stays at **16** — no scenario is added, and none could be: this slice's
subject is not measurable by a browser.

**And per §18's new habit, if the run returns anything other than 1080, the difference is the next thing to
chase rather than a rounding.** That habit exists because of F-70, which was one digit.

## Still open

**1b's last surfaces, now genuinely a judgement.** `CounterBoard`, `CounterSitting`, `KitchenBoard`,
`TableHistory`, `TableJoinCode`, `CounterJoinCode` keep their `<style>` blocks; none re-declares a shared
name, none writes a colour, none carries a fallback. What is left is whether each one's own layout works at
375px, which is not a migration and is not tracked as one. `KitchenBoard` needs its own judgement for the
reason §11.2 gives.

**`.sitting-meta` is declared by two components and the two have drifted** — `ManageSitting` has
`margin: 0.2rem 0 0` and `TableArea` does not. Slice 34 recorded this so it would be a decision rather than
a discovery; this slice edited `TableArea` and **deliberately did not resolve it**, because the fix is a
choice between a shared declaration (`app.css` plus a prefix-list entry) and a rename that touches markup,
and neither belongs in a slice whose subject is colour. It is now a decision that has been deferred twice,
which is the point at which deferring it again should require a reason.

**`check_tree.sh` gate 3 prints a claim one word stronger than the check it just ran.** It says
`all files end with exactly one LF`, and what it asserts is `tail -c 1 | wc -l` — *at least* one. **Eleven
tracked files end with two or more**, among them `.env.example`, `.github/workflows/ci.yml`,
`scripts/backup.sh`, `scripts/restore_drill.sh`, `Components/Pages/Counter/CounterBoard.razor` and
`Components/Account/Pages/SignIn.razor`. Nothing is broken by it: `.editorconfig` asks for
`insert_final_newline = true` and does not forbid a second, and the gate's stated purpose — detecting a
truncated transfer — is served by the check it actually performs. **The finding, if it is one, is the same
shape as F-65:** a message asserting a property the declaration beneath it does not provide, in the one place
a reader checking compliance would stop reading. Found by this slice's own byte-hygiene pass being stricter
than the gate, which is how the eleven files surfaced. Not fixed here, because the choice is between
weakening one word of a message and normalising eleven files in six directories this slice has no other
business in, and that is a decision rather than a repair. **The one thing that would be wrong is to
`sed` the eleventh byte off six files inside a slice about colour** — so the evidence is written down instead.

**A CI job that runs the canonical stack on the canonical engine.** Eleventh consecutive slice.

**Stage 2's boundary**, corrected in the plan and not yet built. `CreateMenuItemAsync` still drives the real
create-item form in six of sixteen scenarios and the handheld barrier opens that item's management page, so
the menu row it needs is load-bearing twice.

**Permissions-Policy**, carried since Slice 24. **Two operator actions** no archive can contain (F-42).

---

# M6 Slice 36 — the suite that did not build (F-71), the register that was not a table (F-72), and a stale count inside the gate against stale counts (F-73)

## Read this first: Slice 35 did not fail a test. It never ran one.

`dotnet test` on Slice 35's tree printed `Test summary: total: 497, failed: 0, succeeded: 497, skipped: 0`
and then `Build failed with 3 error(s)`. Those 497 are Domain, DataAccess and all sixteen §16.3 scenarios,
and every one of them passed. **`MyRestaurant.WebApplication.Tests` did not compile**, so its roughly five
hundred and eighty assertions did not run — including both facts Slice 35 had just added, which were the
entire point of the slice.

```
HandheldLayoutContractTests.cs(1113,48): error CS1503: cannot convert from 'System.StringComparison' to 'int'
HandheldLayoutContractTests.cs(1115,52): error CS1503: cannot convert from 'System.StringComparison' to 'int'
HandheldLayoutContractTests.cs(1121,53): error CS1503: cannot convert from 'System.StringComparison' to 'int'
```

That is F-71 and it is a one-line class of mistake. Everything else on that run was green: tree hygiene,
governance, shellcheck, `run.sh --smoke`, the compose-substitution preflight, `dotnet test
tests/MyRestaurant.EndToEnd.Tests` at 16 of 16, and the quick tunnel.

## F-71 — an overload that exists for `string` and not for `char`

`DeclarationBlocksIn` is the helper Slice 35 added so the colour scan would read declaration blocks rather
than whole files, which is what keeps `#blazor-error-ui` in a prelude from being read as a colour. It called
`css.IndexOf('{', open + 1, StringComparison.Ordinal)`.

The overload set was **read from `dotnet/runtime` at `release/10.0`** rather than recalled, because a claim
about a framework API is exactly the kind of claim F-62 says to check before writing down.
`System.Private.CoreLib`'s `String.Searching.cs` declares, for a `char`:

| Overload | Exists |
|---|---|
| `IndexOf(char)` | yes |
| `IndexOf(char, int startIndex)` | yes |
| `IndexOf(char, StringComparison)` | yes |
| `IndexOf(char, int startIndex, int count)` | yes |
| `IndexOf(char, int, StringComparison)` | **no** |
| `LastIndexOf(char, StringComparison)` | **no** |

For `string` the three-argument-with-comparison form *does* exist, and so does the four-argument one. So the
same shape is correct one keyword over, argument three binds to `count`, and the compiler reports a type
mismatch on an argument rather than a missing member — which reads like a wrong value and not like a wrong
overload. That asymmetry is the whole finding.

**The fix is to drop the third argument**, and it is behaviour-identical rather than merely equivalent:
`IndexOf(char, StringComparison.Ordinal)` in that same file is a `switch` whose `Ordinal` arm returns
`IndexOf(value)`, and `IndexOf(char, int startIndex)` returns `IndexOf(value, startIndex, Length - startIndex)`,
which is precisely the search intended. The redundant second argument on the loop's initialiser goes with
them, so one `for` statement does not spell one search two ways; the repository's own convention already
omits it for a `char` at eleven other sites.

**No gate is added, and that is a ruling.** The compiler is the gate. It ran, it blocked, and CI would have
blocked identically. A test asserting what CSC already rejects is a monument, which is what F-47 says to
delete rather than build. What failed is not the tree; it is the authoring-side verification, which walked
brace balance, scanned CS1620 and CS4007, and had no way to see an overload. §18 therefore gains the habit
rather than the tree gaining a test: **an archive that has not been compiled is a prediction, and the first
thing to do with one is build it.** Where a trap is mechanical it is scanned by name, and that scan now
exists in the authoring pass: three hits against Slice 35's tree, zero against this one.

## F-72 — the two registers this project runs on had both stopped being tables

Found by opening Appendix A to add F-71's row, and discovering there was no way to add one without first
deciding how many columns a row has.

Three defects, in both documents:

| Shape | In `TECHNICAL_SPECIFICATION.md` Appendix A | In `DOCUMENTATION_REVIEW.md` Group E |
|---|---|---|
| Header narrower than its rows | header declared **3** columns; every row from F-38 to F-70 carried **4**, and F-41 carried 4 with an escaped pipe | F-38's row held **5** against a 4-column header |
| Rows outside any table | F-63 to F-70 sat after a horizontal rule with **no header and no delimiter** — eight rows, the whole of Slices 33, 34 and 35 | thirty-one rows from F-40 to F-70 split into **fourteen fragments** by blank lines and one horizontal rule |
| A row swallowing its neighbour | **F-65 had no row**, fused onto the end of F-64's by a stray `\|\|` | F-38's fifth cell, because `` `ps \| grep -m1 postgres` `` spells a pipe a cell reads as a boundary |

A Markdown renderer truncates a row to its header's width and discards the rest without a word. So the
*Embodied in* column — the file paths and the BUILD_PROGRESS slice, which is the entire second half of what
*ruling → embodiment* means — **was being dropped on thirty rows of the register that heading names.** And
rows with no delimiter above them are not rows at all: they render as a paragraph of pipe characters.

**Nothing was ever wrong in the source.** Every character of every row was present and correct, and an
editor showed all of it. It was wrong only once *rendered*, which is how these two files are actually read.
That is F-49's shape a third time — a thing that existed, worked from one angle, and that nobody had
decided. And it accumulated in the one direction nothing could catch: each slice appended a row in the shape
of the previous slice's rows, so the drift was invisible **because** it was consistent.

### What was decided, and where to veto

**Appendix A goes to four columns rather than the rows going to three.** The newer rows were right: the
ledger has used `| ID | Finding | Ruling | Embodied in |` since Group A, and Appendix A's rows have been
following it since F-38 while its header stayed at the compressed shape. Collapsing thirty rows to three
columns would have meant merging each F-number into its narrative and losing the scannable left column.

**The seventeen older compressed rows gain an em-dash, not a story.** `| F-20 | Hand-written fakes… | §16.1 |`
becomes `| F-20 | — | Hand-written fakes… | §16.1 |`. Writing a narrative for a 2026-07 ruling now would be
inventing history in the register whose job is to hold it.

**F-63 to F-70 join the table after F-62**, in the numeric order the rows above them keep, ahead of the four
summary rows (the menu enhancement, F-21 – F-24, F-25 – F-33, the judgment calls) which have always been an
out-of-sequence tail. **The horizontal rule that had been standing inside the table moves above the next
heading**, which is where this document puts one before every other section — the likeliest explanation for
it being there at all is that it was aimed at that position and landed one paragraph early.

**The gate is named for the property, not for the register.** `MarkdownTableContractTests`, not
`DefectRegisterContractTests`. Naming it after the two files that prompted it would be enforcing a general
rule against its own examples, which is F-46's lesson and the reason F-63 needed writing at all; the subject
is computed over every Markdown file in the repository with nothing named, on F-58's shape.

### Two things the gate must know about Markdown, both demonstrated by the tree

Neither was planted, and the first draft reported both as findings on correct documents:

- **A pipe inside a cell may be escaped.** `docs/OPERATIONS.md` writes a shell pipeline inside a table cell
  as `\|`, correctly, and has done for slices. A scan splitting on every pipe reports that row as three cells
  under a two-column header. The cell-boundary pattern therefore carries a lookbehind, and that lookbehind is
  the whole of what keeps this fact off a correct tree (F-41).
- **A fenced code block is not a table.** This file quotes the diagnosis `dev_instance.sh` prints on a failed
  bring-up, and every line of that quoted output opens with the pipe the helper indents a container's log
  with — **eighteen such lines, in two fences, right here in BUILD_PROGRESS.** Read as Markdown they are a
  code block; read without fence tracking they are two runs of table lines with no delimiter, which is
  exactly what the first fact reports. Fence tracking and Markdown's own three-space indentation rule are
  both in the walk for that reason.

Build output is excluded by name at any depth — `.git`, `.vs`, `bin`, `obj`, `llm`, `node_modules` — spelled
the way `ContainerImageReferenceContractTests` already spells its own list. `llm` is excluded on the decision
the tree gate makes about generated text; the rest are there because a restored tree carries other people's
`README.md` under `obj/`, and a stranger's malformed table is not this repository's finding.

## F-73 — the gate against stale counts shipped with a stale count

`TestingSectionContractTests` exists because a count of assertions written in prose is one fact written in
two places. Its own class summary said §16.4 *"carries eight of them"*. §16.4 as delivered carried **nine** —
the ninth being the paragraph that same slice wrote about this same test. Meanwhile
`MinimumCountedClasses` two screens below said nine, so the two numbers inside one file disagreed with each
other.

Trivial in effect, and worth a row for its position: F-69's mechanism occurring inside the repair for F-70.
Both numbers move to **ten**, which this slice's own §16.4 paragraph makes true, and the summary records that
it said eight when the answer was nine.

**The number is kept rather than deleted, and the difference from F-69 is the point.** F-69's count was the
argument for a rule being a *should*, so deleting the count removed the argument and the *should* with it.
This one is the argument for a floor, and a floor is a deliberate refusal to accept whatever the tree
currently happens to say — there is nothing to derive it from. What is added is the habit of moving it, which
is what the test beneath it now mechanically requires of §16.4 and cannot require of its own comment.

## What is in this slice

| Path | Change |
|---|---|
| `tests/…/Components/HandheldLayoutContractTests.cs` | three call sites and one initialiser lose an argument that does not exist; the helper's summary records F-71 |
| `tests/…/Documentation/MarkdownTableContractTests.cs` | **NEW.** Two facts: a run of table lines opens with a header and its delimiter; a row carries its header's column count |
| `tests/…/Documentation/TestingSectionContractTests.cs` | F-73: the summary's count and `MinimumCountedClasses` both move to ten |
| `docs/TECHNICAL_SPECIFICATION.md` | **v1.21.** Appendix A restructured to four columns with F-63 – F-70 brought inside and F-65 restored; §16.4 gains two paragraphs; §18 gains the build-it-first habit; F-71, F-72, F-73; changelog |
| `docs/DOCUMENTATION_REVIEW.md` | Group E rebuilt as one table; F-38's pipe escaped; three rows; status line; *Going forward* |
| `docs/BUILD_PROGRESS.md` | this section |
| `_CHANGES.md` | the archive note |

**Nothing under `src/` is touched.** No application code, no stylesheet, no Razor. The only behaviour that
changes is which assertions run.

## What was verified

No .NET SDK in the authoring sandbox, so all of this is text-level and says so. The point of F-71 is that
this is not sufficient.

- **The tree was reconstructed from `dump.txt` and verified by SHA-256: 334 of 334 files match.** The one
  mismatch is `export.sh`, whose dump embeds a nested copy of the script that writes it. Incidentally, the
  eleven files that needed a second trailing newline to hash correctly are **exactly** the eleven this
  slice's predecessor recorded as ending in two or more LFs, which reproduces that open item from the dump
  alone.
- **All nine handheld facts and the testing-section fact ported to Python and run: ten facts, thirty-five
  assertions, zero failures.** They pass against Slice 35's tree too — the compile error was the only thing
  between that tree and a green suite, which is stated as a measurement rather than a hope.
- **The two new facts proven sensitive by the tree**: forty-one findings against Slice 35's tree (fourteen
  structural, twenty-seven width), zero against this one. **Proven not to fire** on the escaped pipe in
  `OPERATIONS.md` and the eighteen fenced log lines in this file — both of which the first draft reported,
  which is why they are recorded as demonstrations rather than as claims.
- **The new gate caught this slice's own rows.** The first draft of F-72's ledger row wrote `\|\|` and
  `` `ps \| grep` `` unescaped inside a table cell, and both rows came back seven cells wide against a
  four-column header. Fixed by escaping, which is the repair the failure message names.
- **§16.4's ten counted paragraphs each compared to their file: ten pairs, ten agree.**
- **`SpecificationVersionTests` ported:** header 1.21 against newest entry 1.21, twenty-two entries
  descending, two documents qualifying.
- **Bracket balance** on all three C# files, string- and comment-aware, with an untouched sibling as a
  control and proven sensitive by deleting one brace. **CS1620** and **CS4007** scans clean. **The
  overload-arity scan** that would have caught F-71: three hits against Slice 35's tree, zero against this
  one.
- **Byte hygiene** on every delivered file: no CR, one final LF, no whitespace-only lines.

## What was NOT verified

**Nothing compiled** — which is the finding this slice opens with, so it is worth being exact about what
that leaves open. `MarkdownTableContractTests` is the only new C# file and therefore the likeliest site of a
complaint. It uses two `sealed record` declarations for `Run` and `Census`, positional and nested inside the
class, which nothing else in this test project does; a `List<string>` inside a record used as a value; and
`UnreadDirectoryNames.Contains(segment, StringComparer.Ordinal)`, which is the LINQ overload rather than the
array one and needs `System.Linq` from implicit usings.

**No browser rendered anything, and none needed to.** No `src/` file changed.

**Nothing here confirms that either register now renders as intended in a browser.** The gate asserts the
structure a renderer requires; whether the four-column table is *readable* at that row length is a judgement
for whoever opens the file, and the rows are long.

## Test count

Slice 35's predicted 1080 was never observed, because the project holding those tests did not build. The
last observed figure is **1078**, from Slice 34.

Predicted here: **1078 + 2 (Slice 35's two new facts, now able to run) + 2 (this slice's two new facts) =
1082.** Arithmetic on the last observed count, not an observation. §16.3 stays at **16**.

Per §18: if the run returns anything other than 1082, that difference is the next thing to chase.

## Still open

**F-41 has no row in `DOCUMENTATION_REVIEW.md`.** It is cited fifteen times in that file and appears only in
Appendix A. Found by the same read that found F-72, and deliberately not repaired: whether the fix is to
write the row or to accept that Appendix A is the register of record for gate-scope rulings is a decision,
and it does not belong in a slice whose subject is table structure. The gate written here deliberately does
not assert that every cited finding has a row, because that assertion would have to be right about grouped
rows like `F-21 – F-24` and would report findings on a correct tree.

**`check_tree.sh` gate 3 still prints a claim one word stronger than its check.** Carried from Slice 35 with
the eleven files named, and now independently reproduced from the dump.

**`.sitting-meta` is declared by two components and the two have drifted.** Deferred three times now.

**A CI job that runs the canonical stack on the canonical engine.** Twelfth consecutive slice.

**Stage 2's boundary.** **Permissions-Policy**, carried since Slice 24. **Two operator actions** no archive
can contain (F-42).

# M6 Slice 37 — menu sections exist (Stage 2, section half), an advisory nobody read (F-74), the gate that could not see it (F-75), and a claim one word too strong (F-76)

## Read this first: Slice 36 was green, exactly as predicted, and that is the subject

```
Test summary: total: 1082, failed: 0, succeeded: 1082, skipped: 0
§16.3 end to end: 16 of 16
all local CI gates passed
```

The predicted 1082 was the observed 1082, so per §18 there was no difference to chase. Every gate passed:
tree hygiene, governance, shellcheck, restore, strict build, the full suite, the sixteen scenarios, the
boot smoke, the compose-substitution preflight, the container stack, and the quick tunnel.

**Two of this slice's three findings are things that green run printed.** They were in the scroll-back of
the same terminal that reported all gates passing:

```
warning NU1903: Package 'SSH.NET' 2025.1.0 has a known high severity vulnerability
```

twice, on every restore. And:

```
  3. LF endings and a final newline
     all files end with exactly one LF
```

which was not true of eleven files.

## Menu — Stage 2's section half

**What landed.** `menu_section` and `menu_section_event`, migration `0003_menu_sections.sql`,
`MenuSectionDirectory`, `MenuSectionAdministration`, both registered, and twenty integration facts against a
real PostgreSQL. **Nothing that already existed changed.** `menu_item` still has four columns, no surface
reads a section, and no §16.3 scenario behaves differently.

**The stage boundary moved, and it is the ruling of this slice.** `MENU_AND_HANDHELD_PLAN.md` already
carried a correction saying Stage 2 as first written could not ship green: `menu_item.menu_section_identifier`
is `NOT NULL`, so the moment `0003` applies, `CreateMenuItem.razor` cannot write a row without a section, and
`AdministrationJourneys.CreateMenuItemAsync` drives that real form in six of the sixteen scenarios. That
correction's answer was to pull three surfaces forward. **This slice cuts between the two tables instead**,
which is cheaper in every direction that matters:

- `0003` adds two tables and touches nothing. The suite stays green **by construction** rather than by
  inspection — there is no existing row, column, query, form or scenario for it to affect.
- The rejected nullable-then-tighten alternative stays rejected, for its original reason.
  `menu_section_identifier` goes from non-existent to `NOT NULL` in `0004`; it is never nullable, so no
  reading surface ever gains a code path for an item under no heading, and no "Uncategorized" state exists
  for even one slice.
- The cost is one extra migration script. DbUp journals by script name, so that is not a cost.

**The seed moved with it, and for a better reason than tidiness.** The plan put a conditional one-section
seed in the same script as the tables. It is in `0004` instead, beside the backfill that needs it: a script
that adds a `NOT NULL` reference to a populated table is the script that has to create something to point
the existing rows at. `0003` is therefore tables only, and a fresh installation gets **no** sections — which
is what §7 wants, since the administrator names their own.

**Three decisions inside the write service that are rulings rather than implementation.**

- **`display_order` is assigned, not supplied.** `CreateMenuSectionAsync` reads
  `COALESCE(MAX(display_order), -1) + 1` inside its own transaction, so the first heading is 0 and a new one
  lands at the bottom where somebody adding a heading expects it. `MAX + 1` rather than `COUNT(*)`: positions
  are neither unique nor required to be contiguous, so counting rows hands out a number an existing section
  already sits on as soon as one has been moved.
- **Two concurrent creates may tie, and that is not a defect.** The table is not locked, so two
  administrators can be appended at the same number. The column is deliberately not UNIQUE, so nothing
  fails; the reads break the tie by name, so both render stably; either can be moved. Locking a table to
  prevent a tie nobody can see is the more expensive answer to the smaller problem.
- **A rename is compared ordinally although the column is `citext`, and the distinction is the point.**
  `citext` governs *collisions between two sections* — a second "Drinks" spelled "drinks" is the mis-tap the
  type exists to refuse. It does not govern whether one section's own spelling moved: renaming "drinks" to
  "Drinks" changes what every guest reads. Comparing with the database's semantics there would silently
  refuse a visible change.

**One obligation is deferred on purpose.** The five writes are **not** behind `IMenuWorkflow`, so nothing
publishes `MenuChanged` when a section moves. Correct today — no surface reads a section, and a workflow verb
with no caller is a code path no test can reach through the interface meant to protect it. It becomes a
defect the moment Stage 3's guest menu groups by section. Recorded in the plan and in the registration's own
comment, in the file somebody editing that group will read.

## F-74 — a high-severity advisory, printed twice per restore, mentioned by nothing anybody runs

`NU1903` named `SSH.NET` 2025.1.0 on every restore and build, once per test project. The advisory was
**read rather than recalled**: GHSA-q939-rpr3-3284 / **CVE-2026-48798**, high, **CVSS 7.1**, published
**2026-08-12** — one day before the dump this slice was authored from. `ScpClient`'s recursive download
builds local paths out of names the remote SCP server supplies with no containment check, so a malicious or
man-in-the-middle server writes wherever the client process can.

**Nothing in this tree references the package.** `Testcontainers` pins it at exactly 2025.1.0 — confirmed in
their own `Directory.Packages.props` at the 4.13.0 tag — and `Testcontainers.PostgreSql` drags it into both
test projects, which is why an advisory appears against two projects that never name it, and why an audit
that finds it must pass `--include-transitive`.

The fix is one line, because `CentralPackageTransitivePinningEnabled` has been true since M1. **It is a pin
rather than a suppression, and the ledger row says why it is not urgent rather than implying that it is:**
`ScpClient` appears **zero times** in `testcontainers/testcontainers-dotnet`, and nothing here names `Renci`,
`SshNet`, `ScpClient` or `SftpClient`, so the vulnerable call site is unreachable. 2026.0.0's release notes
state *no known breaking changes* against 2025.1.0, and the data-access suite drives Testcontainers against
a real database on every run, so that claim is tested rather than trusted. And 2026.0.0 adds a `net10.0`
target framework that 2025.1.0 does not have — both test projects had been resolving its `net9.0` asset.

## F-75 — the script that names its one blind spot had two

`scripts/ci_local.sh` says *"The one gate this cannot reproduce is CI's boot-smoke job."* True about the
container image. Silent about CI's `vulnerable package audit (advisory)` step, which had no local counterpart
for fourteen slices, while `Directory.Build.props` asserted in the indicative that *"CI reports them in a
dedicated non-blocking step instead"* — true of the workflow, quietly untrue of the equivalent people
actually run.

This is **F-46's shape again**: a rule applied to one of its two homes. And it is F-74's *mechanism* rather
than a defect beside it — the audit is the only thing in this project whose job is to say an advisory out
loud, and the eight-gate run reported `all local CI gates passed` without asking.

**The header's claim is repaired by becoming true again rather than by being weakened**, which is the
direction that matters here: a claim softened to match a check is a check nobody strengthens afterwards.

## F-76 — `exactly one LF`, checked as `at least one`

Gate 3 of tree hygiene printed `all files end with exactly one LF` while testing only that the last byte
*was* one. Eleven tracked files ended with two. Nothing else could see them: gate 2 forbids whitespace-only
lines via `^[[:space:]]+$`, which requires at least one space or tab, and an empty line has neither — so gate
2 passed correctly.

**The eleven were found before the carried note was read.** Each surfaced as a one-byte SHA-256 delta while
reconstructing the tree from `dump.txt`, which is an independent derivation of the same set:

```
.env.example                                     scripts/backup.sh
.github/workflows/ci.yml                         scripts/restore_drill.sh
…/Account/Pages/SignIn.razor                     …/Identity/AccountEndpoints.cs
…/Counter/CounterBoard.razor                     …/Identity/IdentityServiceCollectionExtensions.cs
…/Identity/ObligationsEnforcement.cs             …/Harness/CounterJourneys.cs
…/Identity/IdentityWiringTests.cs
```

**The claim is earned rather than deleted.** Dropping `exactly` would have made the message honest and left
the property unchecked forever.

## What is in this slice

| Area | Change |
|---|---|
| Migration | `0003_menu_sections.sql` — **new**, two tables and one index, touching nothing existing |
| Data access | `MenuSectionDirectory.cs`, `MenuSectionAdministration.cs` — **new** |
| Wiring | two registrations in the menu group of `OrdersServiceCollectionExtensions` |
| Tests | `MenuSectionAdministrationTests.cs` — **new**, twenty facts; `VulnerabilityAuditParityContractTests.cs` — **new**, two facts; one new fact in `MenuWiringTests`; two relations in `SchemaMigrationRunnerTests` |
| Test harness | `OrderTestWorld` truncates `menu_section` as a named root |
| Packages | `SSH.NET` 2026.0.0 transitive pin (F-74) |
| Scripts | audit gate 7 and a corrected header claim in `ci_local.sh` (F-75); a third half in gate 3 of `check_tree.sh` (F-76) |
| Bytes | eleven files lose one trailing newline each (F-76) |
| Documents | S v1.22 (§7, §8.2, §16.4, Appendix A, changelog), ledger F-74/75/76, ADR-0014 amended, plan's Stage 2 |

## What was verified

**A real PostgreSQL 16 was installed in the authoring environment, and the schema half is measured rather
than predicted.** `0001` and `0002` were replayed, then `0003` applied, then every constraint exercised:

- The `citext` UNIQUE refuses `'drinks'` after `'Drinks'`. Negative `display_order` and an empty `name` are
  refused. A `created` event missing its description payload is refused; a `renamed` event carrying a display
  order it did not move is refused; an event type outside the vocabulary is refused.
- `renamed`, `described`, `reordered` and `deactivated` are each accepted carrying exactly their own payload
  and nothing else — including `described` carrying `''`, which is the case the `NOT NULL DEFAULT ''` column
  exists for.
- **Every statement the two services emit was executed**: the `FOR UPDATE` lock read, the list and get, all
  four UPDATEs, the `MAX + 1` probe, and the §11.4 history read joined to `person`.
- **The behaviour twenty facts assert was replayed**: appends land 0, 1, 2; stored order is
  `Breakfast, Entrees, Drinks` and not alphabetical; after a section is moved to 9 the next append is **10**
  and not the row count 3; two sections sharing position 0 render `Apple, Zebra`.
- **The one that would have been easy to get backwards was checked directly**: renaming the *same row* from
  `Apple` to `APPLE` does **not** trip the `citext` UNIQUE, so treating a capitalisation fix as a real rename
  works rather than reporting a name collision.
- `TRUNCATE … menu_section CASCADE` reaches `menu_section_event`, which is what `OrderTestWorld` now relies on.

**F-76 was demonstrated in both directions on the real tree.** The strengthened gate was run before the
repair and named **exactly the eleven files**, then run after it and passed. Each repaired file was then
verified **byte-identical to its recorded SHA-256 but for the one removed newline** — so nothing else moved
inside eleven files, two of which are over 24 KB.

**F-75's two facts were simulated against the real files** rather than reasoned about: the command string is
present in both, there is exactly one uncommented invocation locally, and it ends in `\| \|` `true`.

**§16.4's contract test was simulated over the edited specification, and it caught a real failure before
packaging.** The menu-sections paragraph stated *twenty* assertions and then used the phrase *"One assertion
in it…"*, which that gate reads as a second count in the same paragraph and reports as unattributable. The
sentence was reworded. Final state: twelve counted paragraphs against a floor of ten, no ambiguity, no
disagreement, and the count is written as `20` rather than as a word because the gate's vocabulary stops at
twelve and a spelled *twenty* would have been silently unchecked.

**The Markdown table gate was simulated over every Markdown file, and it caught a second real failure.**
F-75's Appendix A row contained `\| \|` `true` inside a code span — two unescaped pipes in a table cell,
which is precisely F-72's finding. Escaped. Final state: zero shape problems across the repository.

**`SpecificationVersionTests` ported:** header 1.22 against newest entry 1.22, twenty-three entries
descending.

**Structural verification on all five new or edited C# files**, string- and comment-aware, with untouched
siblings as controls and **proven sensitive** by three planted defects — a deleted brace, an unterminated
raw string, and an `await` inside an interpolated hole. CS4007 and CS1620 scans clean. Byte hygiene on every
delivered file: no CR, one final LF, no whitespace-only lines. `bash -n` on both edited scripts.

**Two package facts were read from source rather than recalled**: Testcontainers 4.13.0's pin of SSH.NET
2025.1.0, and SSH.NET's target frameworks at both tags (`net462;netstandard2.0;net8.0;net9.0` at 2025.1.0,
with `net10.0` added at 2026.0.0).

## What was NOT verified

**Nothing compiled.** There is no .NET SDK in the authoring environment. Five C# files are new or edited and
the two new ones are the likeliest site of a complaint: `MenuSectionAdministration.cs` uses
`ExecuteScalarAsync<int>` with a `CommandDefinition` built by named argument, skipping `parameters` —
a shape this file introduces to the menu directory even though `SchemaMigrationRunnerTests` already uses
`ExecuteScalarAsync` — and `ArgumentOutOfRangeException.ThrowIfGreaterThan` with an explicit third argument.
`Assert.Null` on a nullable value type read from `World().ScalarAsync<T>` has a precedent in
`MenuAdministrationTests` at line 210, which is why it is used rather than avoided.

**No browser rendered anything, and none needed to.** No Razor file changed and no CSS changed.

**The migration was verified against PostgreSQL 16, not 17.** The stack and CI both run 17. Nothing in
`0003` uses a feature that differs between them — `citext`, `char_length`, named CHECK constraints and a
composite index are all long-settled — but the version that ran it here was not the version that will.

**DbUp did not apply it.** The script was applied with `psql`; DbUp's journalling, its statement splitter and
its embedded-resource discovery are exercised for the first time by `SchemaMigrationRunnerTests` on the real
run. `0003` contains no dollar-quoted block, which is the splitter behaviour `0004` will need.

**Nothing here proves the advisory is gone.** The claim is that a pin at 2026.0.0 clears NU1903; the
evidence is the advisory's own `first_patched_version`. The next restore is what settles it.

## Test count

Last observed: **1082**, from Slice 36, matching its prediction exactly.

Predicted here: **1082 + 20 (`MenuSectionAdministrationTests`) + 2 (`VulnerabilityAuditParityContractTests`)
+ 1 (`MenuWiringTests`) + 2 (`SchemaMigrationRunnerTests` theory rows) = 1107.** Arithmetic on the last
observed count, not an observation. §16.3 stays at **16**.

Per §18: if the run returns anything other than 1107, that difference is the next thing to chase.

## Still open

**`0004` and Stage 2's remainder.** The three `menu_item` columns `NOT NULL` from birth, the conditional
seed and backfill beside them, three new `menu_item_event` types with their widened CHECKs, and the three
surfaces pulled forward — the section create page, the section picker and description field on the item
form, and a harness `CreateMenuSectionAsync` the five ordering scenarios call before their first
`CreateMenuItemAsync`. That last one is the file that decides whether the ordering integration tests compile.

**The section writes are not behind `IMenuWorkflow`.** Deliberate, recorded twice, and Stage 3's obligation.

**F-41 has no row in `DOCUMENTATION_REVIEW.md`.** Carried unchanged from Slice 36: cited fifteen times in
that file and present only in Appendix A. Still a decision rather than a repair — whether to write the row or
to accept that Appendix A is the register of record for gate-scope rulings.

**`.sitting-meta` is declared by two components and the two have drifted.** Deferred a fourth time.

**A CI job that runs the canonical stack on the canonical engine.** Thirteenth consecutive slice.

**`run.sh --containers-only` prints two `Error:` lines from `podman-compose` about a container that does not
exist yet, then starts it successfully.** New, from this slice's reading of the terminal log, and not
repaired: it is noise in the right place at the wrong volume, and deciding whether the fix belongs in
`run.sh` or is the engine's to make is a judgement rather than a slice.

**Permissions-Policy**, carried since Slice 24. **Two operator actions** no archive can contain (F-42).

---

# M6 Slice 38 — item descriptions and item positions (Stage 2, item half minus the heading), and a count written in three places (F-77)

## Read this first: Slice 37 was green, exactly as predicted

```
Test summary: total: 1107, failed: 0, succeeded: 1107, skipped: 0
§16.3 end to end: 16 of 16
all local CI gates passed
```

**1107 predicted, 1107 observed**, so per §18 there was no difference to chase. All three of Slice 37's
findings are settled by that run rather than by assertion:

- **F-74 is cleared.** Gate 7 prints `has no vulnerable packages` for all seven projects. The claim was that
  a pin at `SSH.NET` 2026.0.0 clears NU1903, the evidence at authoring time was the advisory's own
  `first_patched_version`, and the restore is what settled it.
- **F-75 is cleared.** The audit exists locally and ran, as gate 7 of eight.
- **F-76 is cleared.** Gate 3 prints `LF endings and exactly one final newline` — the strengthened wording —
  and passes over 341 authored files.

One thing that run printed is **not** repaired here and is carried deliberately: `run.sh --containers-only`
emits two `Error: no container with name or ID "myrestaurant_caddy_1" found` lines from `podman-compose` and
then starts the stack successfully. It is noise from the engine's own teardown-before-up, and the only local
lever is suppressing stderr on `up`, which would hide real errors. Still a judgement rather than a slice.

## The ruling of this slice: the boundary moved a second time

Slice 37's *Still open* scoped `0004` as the whole item half — three columns `NOT NULL` from birth, the
conditional seed, the backfill, three event types, **and** three surfaces pulled forward plus a harness
`CreateMenuSectionAsync`. Counted against the real tree that is twenty-five-odd files, none of them compiled
in the authoring environment, with `menu_item.menu_section_identifier NOT NULL` breaking the create-item form
that six of the sixteen §16.3 scenarios drive.

**This slice cuts between the item's own columns instead**, which is Slice 37's own test applied one register
lower. `0004` adds `description` and `display_order` — both `NOT NULL` with a `DEFAULT` — plus the two event
types that move them. It does not add the section reference. The properties that follow are the argument:

- **No backfill runs and no existing row is rewritten.** PostgreSQL 11 and later store a non-volatile
  `ADD COLUMN … DEFAULT` in the catalogue rather than the heap.
- **No form is required to supply anything**, so `AdministrationJourneys.CreateMenuItemAsync` fills `#name`
  and `#price` and clicks, exactly as it did. The new textarea is left empty, stores `''`, and writes no
  event.
- **The new ordering is the old ordering.** `display_order` defaults to 0 and nothing assigns anything else,
  so `ORDER BY (display_order, name, menu_item_identifier)` **is** the `ORDER BY (name, menu_item_identifier)`
  this table has always been read in. The tie-break is doing all of the work. That is why
  `MenuDirectoryTests.List_ReturnsDeactivatedItemsToo_OrderedByName` still asserts the alphabet and still
  passes unedited.

So the suite stays green **by construction** rather than by inspection, and the expensive coupling — a
`NOT NULL` reference against three surfaces and a harness — is paid on its own in `0005`. The cost is one
more migration script. DbUp journals by name, so that is not a cost.

## Four decisions inside the migration that are rulings rather than implementation

**An item is created at position 0, not appended.** `DapperMenuSectionAdministration` reads
`COALESCE(MAX(display_order), -1) + 1` and appends, because a section's position is menu-wide and always was.
An item's is not: §7 puts it *within its section*, and "the end of the menu" is not a defined place until
`0005` gives the item a heading. Appending a menu-wide number now would hand out positions `0005` would have
to undo — and it would break the invisibility property above, which is the whole reason this slice ships
green. The two services therefore disagree, on purpose, and both say why in the file.

**`created` keeps carrying the name and the price only, so an item created with a description writes two
events.** Widening it to carry a description is the obvious alternative. It is refused for a concrete reason:
a description is optional, so the biconditional binding the payload to the type would have to be relaxed to
an implication, and every `created` row already in a database was written without one. The log reads
*"Created as “Soup” at 4.50 / Description set"* — two lines where one would do, and honest about it. A
**blank** description at creation time writes no second event at all, on the no-op rule the other verbs
already honour: an append-only log of "somebody left a field empty" is noise.

**The vocabulary is spelled `description_changed` and `reordered`, and `menu_section_event` spells the same
two verbs `described` and `reordered`.** The asymmetry is a decision. Each table's vocabulary is internally
consistent — this one has said `name_changed` and `price_changed` since `0001` — and harmonising them would
mean rewriting a vocabulary already in applied history and in rows in somebody's database, to buy nothing a
reader of either table needs.

**Every CHECK on `menu_item_event` is dropped by querying for it, not by name.** `0001` declared four inline,
so PostgreSQL generated `menu_item_event_event_type_check`, `menu_item_event_new_price_amount_check`,
`menu_item_event_check` and `menu_item_event_check1`. Those are deterministic and undocumented, and depending
on them in a script that runs at startup on somebody else's box is depending on an implementation detail of a
PostgreSQL version nobody here chose. `0004` loops `pg_constraint` in a dollar-quoted `DO` block and adds five
**named** constraints back, so `0005` can widen the vocabulary by name on a tree that knows the name.
`contype = 'c'` cannot catch a NOT NULL in either 17 (an attribute) or 18 (`contype = 'n'`).

## What descriptions actually do, today

This is not schema-only. A description is authored, stored, logged, read back and displayed **this slice**:

- `/administration/menu/new` gains an optional textarea. `.form-field textarea` has been styled in `app.css`
  since Slice 30 for exactly this field, so the block declares nothing — and `.muted` is the shared soft-ink
  class rather than a new one. The success panel echoes the stored description when there was one, from the
  result rather than a second read, because the write service returns what it actually stored (trimmed).
- `/administration/menu/{id}` gains a **Description** form and a **Position** form, bringing that page to
  four verbs. The description form uses a `.form-field` block rather than `.manage-inline-form`, deliberately:
  that class styles `input` and `select` and has never styled a `textarea`, so a three-line box inside it
  would be an unstyled control on the one page that has one. The facts grid gains Position, and the item's
  description renders under it — or a `.muted` line saying guests see the name and the price only.
- `/administration/menu` renders the description as a `.record-secondary` **under the item's name** rather
  than in a column of its own, because a sentence in a table cell at 375px is the shape F-59 was about. The
  *Created* column is replaced by *Position*; created-at moves to the item's own page, where the history it
  belongs to already lives. The count line reports how many items are described.
- Both `Describe` methods learn the two new types. The item page quotes the description; the index feed says
  only that one changed, because the feed is a glance and the sentence belongs where the uncapped history is.
  Both distinguish **cleared** from **set**, because `''` is a stored value and *"Description set to
  nothing"* is not what a reader wants.

**What guests do not get yet, and why that is correct.** The picker is a `<select>`. A description inside an
`<option>` label is the problem Stage 3's card layout exists to solve, not a smaller version of the solution
— so the guest-facing half waits, and Stage 3's scenario 17 is where it gets an end-to-end assertion.
`MenuWorkflow` still publishes `MenuChanged` on a description that moved, and that is deliberate rather than
premature: the notification means *re-read the menu* and nothing else, and a workflow that decided which
columns were worth announcing would have to be edited again the moment a surface starts reading one.

## F-77 — a count of the event vocabulary, written in three files, checkable in none

`MenuEventLog`'s type summary, `AdministrationMenu.razor`'s `Describe` and `ManageMenuItem.razor`'s all said
*"a friendly label for the five event types §8.2's CHECK admits"*. Five was right when `0001` shipped. This
slice makes it seven; `0005` makes it eight.

**And one of the three was already wrong about a document in the same repository.**
`AdministrationMenu.razor` went on to name *"Stage 2's two new types (`description_changed`,
`section_changed`)"*, where `MENU_AND_HANDHELD_PLAN.md` Stage 2 specifies **three** — omitting `reordered`,
which is a type this very slice implements.

The effect is nil: the `switch` fallback renders an unlabelled type as itself, which is the design and is what
made the drift survivable. The kind is not nil. It is **F-47's habit** — where a rule can be executed, a list
must not exist — applied to a census no test can read, and it is the **sixth** time this ledger has recorded
one fact written in two places disagreeing with itself (F-48, F-50, F-56, F-65, F-73, this).

**The counts are deleted rather than corrected, and that is the ruling.** A corrected count goes stale again
in `0005`; three slices of evidence say the sentence cannot be maintained by hand. Each comment now states
the *property* — a friendly label per admitted type, falling back to the stored string — which is true at
every vocabulary size, leaving the `switch` arms as the only census.

**No gate is added, deliberately.** A test counting `case` arms against a CHECK constraint in a `.sql` file
would be a monument to a sentence, and the compiler already refuses a duplicate arm. **The apparent
contradiction with F-73 is recorded rather than left to be noticed**: F-73's count was *kept* because it is
the argument for a floor a test asserts and nothing derives it, so deleting it would have removed the
argument. This one decorates a `switch` that is its own answer. §16.4 states the residual — nothing
mechanically stops the next such sentence.

## The migration gate needed a new shape of fact, and that is the interesting half

`0004` is **the first migration in this tree that creates no relation.** It is entirely `ALTER`. So every
assertion `SchemaMigrationRunnerTests` already held passes unchanged on a tree where `0004` never ran: the
census of relations that catches `0003` is structurally blind to it.

Two facts close that. Four theory rows name the columns that arrived by `ALTER` — and only those, because
`0001`'s columns are proven by their relation existing and a census of all of them would be a second copy of
the DDL (F-47). One fact names the five CHECK constraints, and asserts the two generated names **absent**.

That second fact is load-bearing for a reason worth stating: a `dbup-postgresql` splitter that broke the
dollar-quoted `DO` block would leave a `DO` with a **truncated body**, which is still valid SQL that simply
does less. The failure would present as a green migration against a table with no constraints — a state no
relation check and no column check can see. This is the first statement in this repository to depend on that
splitter's `DollarQuoted` handling, which is why it gets an assertion rather than a comment.

## What is in this slice

| Area | Change |
|---|---|
| Migration | `0004_menu_item_descriptions.sql` — **new**, two columns, two payload columns, five named CHECKs replacing four generated ones |
| Data access | `MenuDirectory.cs` (`Description`, `DisplayOrder`, position-first ordering), `MenuAdministration.cs` (create takes a description; `DescribeMenuItemAsync`, `ReorderMenuItemAsync`), `MenuEventLog.cs` (two payload columns) |
| Web layer | `MenuWorkflow.cs` — two verbs, publishing only on a real change |
| Surfaces | `CreateMenuItem.razor` (textarea), `ManageMenuItem.razor` (Description and Position forms, seven flash outcomes), `AdministrationMenu.razor` (description as subtitle, Position replaces Created) |
| Tests | `MenuAdministrationTests` +8, `MenuDirectoryTests` +2, `MenuWiringTests` +2, `SchemaMigrationRunnerTests` +2 attributes (one a four-row theory); `OrderStagingTests` and `OrderTestWorld` updated for the new shape |
| Gate arithmetic | `TestingSectionContractTests`' floor and summary move from ten to sixteen with §16.4's census — F-73's habit, second application |
| Documents | S v1.23 (§7, §8.1, §8.2, §16.4, Appendix A, changelog), ledger F-77, ADR-0014 history, plan's Stage 2 boundary |

## What was verified

**Every `CreateMenuItemAsync` call site was found and updated by type, not by search.** The description is
positioned after `name` to mirror `CreateMenuSectionAsync`, so `string` lands where `decimal` was expected at
every un-updated site — a compile error, never a silent mis-bind. Nine sites: the interface, the
implementation, the workflow's interface and implementation, the Razor form, the wiring fake, and four in the
data-access tests.

**`MenuItemSummary` has exactly two construction sites in the whole tree**, verified by search:
`DapperMenuDirectory.ToSummary` and one factory in `OrderStagingTests`. Members were therefore *inserted* to
mirror `MenuSectionSummary`'s member order rather than appended, and the factory passes `""` and `0` with a
comment saying `OrderStaging` reads neither.

**Structural verification on all fifteen edited or new files, string- and comment-aware**, with untouched
siblings as controls. The walker was **proven sensitive by three planted defects** in
`MenuAdministration.cs` — a deleted closing brace, an unterminated raw string, and an extra parenthesis —
each reported. It was also **proven to have been wrong first**: an earlier version mis-parsed `$"""` as an
interpolated single-quote string and reported three false unbalanced files, which is why the controls are in
the run at all.

**The three Razor pages were walked as tag trees**, quote-aware, with the `@code` block excluded and `@*…*@`
comments stripped. Both of those exclusions are corrections rather than conveniences: the first draft read
`<ValidationMessage For="@(() => …)" />` as an unclosed element because `=>` contains `>`, and read
`IReadOnlyList<MenuItemSummary>` in the `@code` block as markup — which is precisely the standing false
report Slice 36 recorded. `AdministrationTables.razor` and `ManageTable.razor` are the controls; all five
files clean.

**Selector existence was checked against the stylesheet rather than assumed**, and it caught two real
defects before packaging: the first draft of `CreateMenuItem.razor` invented `.field-optional` and
`.field-hint`, neither of which exists in `app.css`. Both are now `.muted`, and every one of the thirteen
classes that page uses is declared. `.record-secondary` was confirmed present before being used on the index.

**The 375px barrier was reasoned about rather than hoped for.** `.form-field textarea` is `width: 100%` under
a universal `box-sizing: border-box`, so neither new control can exceed the viewport; and every `<td>` in
both tables on the index carries a `data-label`, with header and cell counts balanced at 5 and 5 —
`HandheldLayoutContractTests` asserts both.

**§16.4's contract test was simulated over the edited specification, and it caught a real failure.** The
first draft described all four test classes in one paragraph with one count, which that gate reads as
unattributable and reports — the same finding Slice 37 hit. Split into four paragraphs of one class and one
count each. Final state: **sixteen counted paragraphs, every claim matching its file, zero ambiguity, zero
uncited names.** The floor moved with it.

**`SpecificationVersionTests` simulated:** header 1.23 against newest entry 1.23, twenty-four entries
descending, both versioned documents found against a floor of two.

**The Markdown table gate was simulated over all twenty-four documents**, fence-aware and escape-aware:
fifty-seven table runs, zero shape problems. F-77's Appendix A row and ledger row were both checked to carry
exactly four cells.

**A CS1587 was introduced and caught.** Splitting a `<para>` in `IMenuAdministration`'s summary left a bare
blank line inside a `///` block, which under warnings-as-errors is a build failure. Repaired before
packaging. CS4007 and CS1620 scans clean across all fifteen files.

## What was NOT verified

**Nothing compiled.** There is no .NET SDK in the authoring environment. The likeliest sites of a complaint
are named rather than left to be discovered: `ScalarAsync<int?>` and `ScalarAsync<decimal?>` on a nullable
value type read through `OrderTestWorld.ScalarAsync<T>` (precedent at `MenuAdministrationTests` line 211,
which is why it is used rather than avoided); `HashSet<string> found = [.. names];` building a collection
expression from a Dapper `IEnumerable<string>`; and `InputTextArea` with a `rows` attribute, which is a
standard component parameter passthrough but is not used anywhere else in this tree.

**No database ran it.** Unlike Slice 37, no PostgreSQL was available in the authoring environment this time,
so `0004` is **reasoned about and not executed**. The three things that would settle it are stated so a red
run is read correctly rather than chased in the wrong place:

1. **Whether DbUp's splitter survives the `DO` block.** The claim rests on `PostgresqlQueryParser`'s
   `DollarQuoted` state consuming a tagged block, which Slice 37 read from source but did not exercise —
   `0003` contains no dollar-quoting. `Run_NamesTheMenuItemEventCheckConstraints` is the assertion that
   decides it, and a failure there means the block split, not that the names are wrong.
2. **Whether `pg_constraint` returned all four generated names.** If the loop ran against a partial list, a
   leftover `menu_item_event_check` survives — which the same fact asserts absent.
3. **Whether `ADD CONSTRAINT` on an existing table with existing rows validates.** Every constraint added
   here is satisfied by every row `0001`'s CHECKs already admitted, and the two new columns are NULL on every
   existing row, which both new biconditionals require for the five old types. That is an argument, not a run.

**No browser rendered anything.** Three Razor pages changed, and the reachability barrier at 375px is
scenario 16's job on the real run. The two new forms on `ManageMenuItem.razor` are the surfaces that have
never been laid out, and a red first run there is informational.

**The description is not exercised end to end.** No §16.3 scenario fills the new textarea, and the harness is
deliberately untouched so the sixteen scenarios stay byte-identical. Stage 3's scenario 17 is where that
lands.

## Test count

Last observed: **1107**, from Slice 37, matching its prediction exactly.

Predicted here: **1107 + 8 (`MenuAdministrationTests`) + 2 (`MenuDirectoryTests`) + 2 (`MenuWiringTests`)
+ 4 (`SchemaMigrationRunnerTests` theory rows) + 1 (`SchemaMigrationRunnerTests` fact) = 1124.** Arithmetic on
the last observed count, not an observation. §16.3 stays at **16**.

Per §18: if the run returns anything other than 1124, that difference is the next thing to chase before this
slice closes.

## Still open

**`0005` and the last of Stage 2.** `menu_item.menu_section_identifier uuid NOT NULL REFERENCES
menu_section`, the conditional one-section seed and the backfill beside it, `section_changed` with its payload
column and a vocabulary CHECK now droppable **by name**, and the three surfaces the `NOT NULL` forces: the
section create page, the section picker on the item form, and a harness `CreateMenuSectionAsync` the five
ordering scenarios call before their first `CreateMenuItemAsync`. That last file decides whether the ordering
integration tests compile.

**The section writes are still not behind `IMenuWorkflow`.** Deliberate, recorded three times now, and Stage
3's obligation — it becomes a defect the moment a guest surface groups by section.

**F-41 has no row in `DOCUMENTATION_REVIEW.md`.** Carried unchanged for a third slice: cited fifteen times in
that file and present only in Appendix A. Still a decision rather than a repair.

**`.sitting-meta` is declared by two components and the two have drifted.** Deferred a fifth time.

**A CI job that runs the canonical stack on the canonical engine.** Fourteenth consecutive slice.

**`run.sh --containers-only` prints two `Error:` lines about a container that does not exist yet, then starts
it successfully.** Carried from Slice 37, still a judgement about whose fix it is.

**No authoring-environment database this slice.** Slice 37 had PostgreSQL 16 and measured its schema half;
this one did not. That is a difference in evidence quality between two consecutive slices and it is recorded
so the next one restores the practice rather than quietly dropping it.

# M6 Slice 39 — the migration that could not run (F-78), and the dropdown that was the request

## Read this first: Slice 38 was red, and not where it predicted

```
System.InvalidOperationException : Variable migrate_menu_item_event_checks has no value defined
   at MyRestaurant.DataAccess.SchemaMigrationRunner.Run() … line 61
   at MyRestaurant.DataAccess.Tests.Menu.MenuSectionAdministrationTests.InitializeAsync() … line 58
MyRestaurant.DataAccess.SchemaMigrationException : Database migration failed on script
   'MyRestaurant.DataAccess.Migrations.0004_menu_item_descriptions.sql'.
```

Repeated for every fact in the data-access suite. **The predicted count of 1124 was never reached, because
the number of tests that ran is not the interesting number here** — a migration is the first thing every
data-access fixture applies and the first thing the end-to-end harness applies, so one unparseable script
took the integration suite and all sixteen §16.3 scenarios down together.

**Slice 38's own "what was NOT verified" named this exact claim**, first in its list of three: *"Whether
DbUp's splitter survives the `DO` block. The claim rests on `PostgresqlQueryParser`'s `DollarQuoted` state
consuming a tagged block, which Slice 37 read from source but did not exercise."* That entry was right that
the claim was unexercised and wrong about which component would decide it. The splitter was never reached.

## F-78 — two dollar-quote syntaxes, and dbup-core goes first

`0004` opens its constraint sweep with `DO $migrate_menu_item_event_checks$`. Read from source rather than
inferred from the message:

- **dbup-core**, `Support/ScriptExecutor.PreprocessScriptContents` → `VariableSubstitutionPreprocessor.Process`
  → `VariableSubstitutionSqlParser.ReplaceVariables`. `IsCustomStatement` returns true when the current
  character is `$` and `PeekChar()` is a letter, digit, `_` or `-`. `ReadCustomStatement` then reads to the
  closing `$` and raises `InvalidOperationException("Variable {0} has no value defined")` for a name absent
  from the dictionary.
- **dbup-postgresql**, `PostgresqlQueryParser.ParseRawQuery`, has the `DollarQuotedStart` / `DollarQuoted`
  states Slice 38 cited, including an explicit *empty tag* branch. It is correct, and it runs **after** the
  preprocessor, on text the preprocessor never handed it.

PostgreSQL spells a dollar-quoted body with the same four characters around the same kind of identifier that
DbUp spells a variable reference with. **The two syntaxes are one syntax, and dbup-core wins.**

**Why nothing here caught it.** The script is well-formed SQL; `check_tree.sh` reads it as text and it is
correct as text. `0003` contains no dollar-quoting, so no test in this tree had ever applied one — the
capability arrived and was exercised for the first time in the same slice. And Slice 38 had no database in
its authoring environment, which it recorded.

**The shape.** F-62 is a reason for doing something written without reading the tree it is about; this is the
same error at a dependency boundary — a safety claim about a NuGet package's behaviour, written by reading
one of the two components involved. The comment was not wrong. It was about the wrong file.

## The fix, and why it is one line and not two

`SchemaMigrationRunner.BuildUpgradeEngine` gains `.WithVariablesDisabled()`. Nothing in this tree has ever
used a DbUp variable: there is no `WithVariable` call anywhere, and the only `$` in any of the four migration
scripts is that tag.

**`0004` keeps its TAGGED body rather than being reduced to `$$`, and that is the ruling of this slice.**
`DO $$ … $$` would also survive substitution — an empty tag's next character is `$`, which is not a valid
variable-name character, so `IsCustomStatement` never fires. Writing both fixes would mean that deleting the
builder call leaves every gate in this repository green while the rule is gone, and that is precisely the
mechanism of **F-64** (an undeclared property rendering its fallback), **F-69** (a `should` resting on a
count nobody re-derived) and **F-75** (a local gate that existed nowhere while the claim said it did). A
second belt that hides the first is how this project's worst findings have all been shaped.

A tagged body makes the builder call load-bearing: remove it and every fact in `SchemaMigrationRunnerTests`
fails on the next run with the message at the top of this entry. **So the row names something executable by
adding nothing** (F-47) — the gate already exists, already blocks, and now says in its own summary that this
is its job.

**`0004` is edited in place, which F-34 forbids for an applied script.** Allowed here for the one reason that
makes the rule inapplicable: **it had never applied anywhere.** DbUp journals only on success, so no journal
row for it exists to be stale. Stated in the script itself, so that a future reader does not take the edit
for a precedent.

**If `WithVariablesDisabled()` does not resolve** against the pinned `dbup-postgresql` 7.0.1 — it was read in
dbup-core's public API at tag `6.1.1` and has been there for years, so this is unlikely — the one-line
alternative is to change both `$migrate_menu_item_event_checks$` to `$$` and drop the builder call. Recorded
here rather than shipped, for the reason above.

## F-79 — one paragraph of §7, written twice

The paragraph beginning *"Two existing rules survive that change…"* — a deactivated item stays visible under
its heading, and deactivating a section does not cascade to its items — appears **twice consecutively,
byte-identical**, from Slice 38. Deleted.

Worth a number for its position rather than its cost. Three gates read this document:
`MarkdownTableContractTests` for table shape, `SpecificationVersionTests` for the header against the
changelog, `TestingSectionContractTests` for §16.4's counts against the files they describe. **None of them
reads a sentence**, and no gate is added, because the assertion available — no paragraph appears twice
verbatim — would report findings on a tree that restates rulings across documents on purpose.

## The picker: what the request actually asked for

> the menu choice should no longer be a dropdown … a select or a dropdown does not give nearly enough
> context … you should be able to select each menu item and see a lot more information about that item if
> such information exists

**Three things were wrong with the `<select>`, and only the third is cosmetic.** A closed dropdown shows
exactly one option, so comparing two items means opening a modal list twice — at sixty items that is a
lookup, not a menu. An `<option>` renders text and nothing else, so `menu_item.description` — the column
`0004` exists to deliver — had nowhere to go, and every future fact about an item would have been
concatenated into the same label. And the label already was a concatenation: name, an em dash, a formatted
price, and sometimes the words "(currently unavailable)".

**It is now a card per item**, each card a `<button>`, with a **detail panel** that opens on the chosen one.

### Stage 3 ran before `0005`, and that is a decision to veto if you disagree

The plan put the guest menu after sections, on the reasoning that a grouped menu needs headings. The two
halves are separable: a card per item needs `menu_item.description` and nothing else, and grouping the cards
under headings is an outer loop added later around markup that does not change. Leaving the picker until
`0005` meant leaving the only part of the request its author could see behind two migrations, with the column
that exists to be read already in the schema and read by nothing.

**To revert:** the surface and its stylesheet block are self-contained. `TableOrderSurface.razor` and the
`.order-menu*` rules in `app.css` go back, and `StageAsync` returns to `SelectOptionAsync("#order-picker-item", …)`.
Nothing else in this slice depends on it — the F-78 fix is independent.

### Three rulings inside it

**The panel says when it has nothing to say.** *"If such information exists"* is answered with a sentence
rather than an empty box, because a blank panel is indistinguishable from a surface that failed to load —
the confusion §11.10's `data-loaded` bit exists to prevent one level up. The facts are a `<dl>` of terms
(Price, Available, On the menu since) so Stages 4 and 5 add rows instead of rewriting markup.

**A card is a `<button>` with `aria-pressed`, not a radio.** A radio group is the more precise ARIA for
"choose exactly one" and it was refused for a concrete reason: Blazor reconciles the `checked` *attribute*
while browsers track the checked *property*, so a radio whose state a component owns can drift out of step
with the DOM in ways only a browser can observe — and this slice had no browser. Every other control on that
island is an `@onclick`, and a button carries no DOM state to reconcile. A one-of-many toggle set is a slight
stretch of `aria-pressed` and it is the better trade against emulating a radio group's keyboard semantics.

**No breakpoint.** `.order-menu` and `.order-menu-facts` are `auto-fit` grids, so one column at 375px and as
many as fit on a counter's laptop is the same rule. §11.12 asks for exactly this in preference to a width
query, and a width written here would have been the tree's second breakpoint (F-63).

### §7's availability rule reads better in a list than it did in a label

A deactivated item keeps its card, keeps its description, gains a *currently unavailable* chip, and its
button is `disabled`. "The guest sees that the salmon exists and is out" was true of the old `<option>` and
was four words appended to a label; it is now a marked card a guest can still read about.

## What the harness gained, and why it could not have existed before

`TableOrderJourneys` adds `ChooseAsync`, `ReadMenuAsync`, `ReadChosenItemDetailAsync`, `WaitForMenuAsync`,
the `MenuCard` and `ChosenItemDetail` records, a `Describe` overload and a private `WaitForAttributeAsync`.

An `<option>` renders text only, so the sole thing a harness could read off the old picker was one
concatenated label and the sole assertion available was containment — **which is why `0004`'s description
column shipped with nothing behind it.** A card has an element per fact.

Two membership rules are recorded because both could reasonably have gone the other way. **Availability is
read from `disabled`, not from the chip:** §7 requires both, and `disabled` is the half that enforces it —
the same reasoning `ReadBadgeAsync` applies in reverse, that whichever of the two is the contract is the one
to read. **`Description` is `null` rather than `""` when absent:** the surface renders no element at all in
that case, so absence is what the DOM says, and a scenario asserting a description arrived must not be
satisfiable by an empty string that happened to render.

**One line of the sixteen existing scenarios changed** — the item choice inside `StageAsync`. Everything else
in `EndToEndScenarios.cs` is byte-identical, deliberately: the smallest edit that could carry the rewrite.

## What is in this slice

| Area | Change |
|---|---|
| Migration runner | `SchemaMigrationRunner.cs` — `.WithVariablesDisabled()`, with the collision documented at the call site |
| Migration | `0004_menu_item_descriptions.sql` — the safety claim corrected to name both components; tagged body kept deliberately; the in-place edit justified against F-34 |
| Surface | `TableOrderSurface.razor` — the `<select>` replaced by a card list and a detail panel; `ChooseItem`; `PickedMenuItem` re-resolving against the live menu |
| Stylesheet | `app.css` — the `.order-menu*` vocabulary, two `auto-fit` grids, no new breakpoint, no colour literal, no `var()` fallback |
| Harness | `TableOrderJourneys.cs` — four journeys, two records, two helpers; `AdministrationJourneys.cs` — an optional description on `CreateMenuItemAsync` |
| Tests | `SchemaMigrationRunnerTests.cs` — summary records that it is the gate on the builder call. **No assertion added or removed anywhere** |
| Documents | S v1.24 (§7, §11.1, §16.4, Appendix A, changelog), ledger F-78 + F-79, plan's Stage 3 partially struck |

## What was verified

**The DbUp behaviour was read from source at a pinned tag, not recalled.** `VariableSubstitutionSqlParser.cs`
and `Support/SqlParser.cs` from `DbUp/DbUp`, `PostgresqlQueryParser.cs` from `DbUp/dbup-postgresql`, and
`Builder/StandardExtensions.cs` at tag **`6.1.1`** to confirm `WithVariablesDisabled` is public API — line
751 of that file. This is the finding's own lesson applied inside its own repair.

**The working tree was reconstructed from `dump.txt` and checked against the SHA-256 recorded for every
file: 338 of 340 byte-identical.** Of the two that were not, one is `export.sh` (which the dump documents
rather than reproduces) and one is a trailing-summary artefact of the extractor, in a file this slice does
not touch. Every edit below was made against a verified tree.

**Byte hygiene on all seven edited files:** no CR, exactly one final newline, no whitespace-only line, no
context-dump separator. F-76's third half was simulated specifically — none of the seven ends in a blank
line.

**Structural verification, string- and comment-aware:** brace, paren and bracket balance on all seven;
CS4007 scan (no `await` in an interpolation hole) and CS1620 scan (no bare literal operand in a
`string.Create` addition chain) clean. Both apply to the new harness code, which composes six failure
messages that way.

**The Razor file was walked as a tag tree**, `@*…*@` stripped and the `@code` block excluded. It reports
four findings, **and all four are the standing false positives Slice 38 recorded** — `IReadOnlyList<OrderLineView>`
inside a `@{ }` block reads as a tag. Proven by running the identical checker against the *pristine*
extracted file and getting the same four at the same offsets. That control is the only reason the result is
usable.

**Every `HandheldLayoutContractTests` invariant was re-derived over the edited stylesheet**, not assumed:
one width media query and it is `min-width` in `app.css`; **zero** colour literals in any declaration block
outside `:root`; **zero** `var()` fallbacks; `overflow-wrap` declared once as `anywhere`; 19 `min-height`
declarations of which 14 read `--touch-target` and none is a literal under 44px; and every custom property
the tree reads is declared. The two new grids introduce no width.

**Selector existence was checked in both directions.** Every class the harness names exists in the markup;
every `order-*` class the markup uses has a rule in `app.css`, except the four that documented themselves as
having none (`order-surface`, `order-settled-heading`, `order-prune-notice`, `order-line-adjustment`).

**Every new harness method is defined exactly once and `SelectOptionAsync` appears nowhere**, verified by
search after the rewrite rather than assumed from having written it.

**`SpecificationVersionTests` simulated:** header 1.24 against a newest entry of 1.24, entries descending,
two versioned documents against a floor of two.

**`TestingSectionContractTests` reasoned about rather than simulated, and the reasoning is short: no
assertion count moves.** No `[Fact]` or theory row is added or removed in this slice, so every count §16.4
states is still the count in its file and the floor of sixteen is untouched. The new §16.4 material is folded
into the *existing* migration-gate paragraph rather than written as a new one, precisely so it cannot become
a seventeenth counted paragraph carrying a number nobody re-derives (F-73).

**Every new Markdown table row was checked to carry exactly four cells**, and no cell contains an unescaped
pipe (F-72).

## What was NOT verified

**Nothing compiled.** No .NET SDK in the authoring environment. The likeliest sites of a complaint are named
rather than left to be found: **`.WithVariablesDisabled()`** resolving against the pinned `dbup-postgresql`
(the fallback is in the F-78 section above); **`aria-pressed="@(isChosen ? "true" : "false")"`**, a ternary
inside an attribute value, which this tree does elsewhere but not with nested quotes; and
**`RestaurantClock.Date(chosenItem.CreatedAt)`**, whose signature was read but never called from a Razor
attribute context before.

**No database ran the migration.** The fix is argued from two sources read at a tag; the assertion that
settles it is the whole of `SchemaMigrationRunnerTests`, and a green run there is the evidence this slice
does not have. **A failure that still says `Variable … has no value defined` means the builder call did not
take**; a failure naming a missing constraint means the splitter broke after all, which would be Slice 38's
original claim being wrong as well.

**No browser rendered the picker.** This is the largest visual change in the project since Slice 30 and
nothing has laid it out. Named consequences a first run may show: whether a 16rem `minmax` is the right floor
on a 375px screen (one column is guaranteed; whether the card is comfortable is a judgement); whether the
detail panel opening below the list pushes the quantity box off-screen on a short viewport, which is a
scroll rather than a defect but is the kind of thing that reads as one; and whether `aria-pressed` on a
one-of-many set announces sensibly, which only a screen reader decides.

**The description is still not exercised end to end.** The harness can now read a card and a panel, and no
§16.3 scenario yet fills the textarea and reads the sentence back. Scenario 17 is where that lands, with
`0005`.

**A previous session's edits were lost to my own mistake and re-applied.** The extractor that reconstructs
the tree writes to a hard-coded path, and re-running it from a different directory overwrote the edited
tree. Every edit was re-applied and re-verified from scratch; the extractors were then deleted from the
authoring environment. Recorded because the *verification* above was re-run against the rebuilt tree rather
than inherited from the lost one, and a reader is entitled to know which.

## Test count

Last observed: **1107**, from Slice 37. Slice 38 predicted 1124 and **never reached a count** — the suite
failed at fixture initialisation, so 1124 remains unobserved arithmetic rather than a number that was wrong.

Predicted here: **1124**, unchanged, because this slice adds and removes no assertions. §16.3 stays at
**16**.

Per §18: if the run returns anything other than 1124, that difference is the next thing to chase — and it is
the first honest opportunity to chase it, since Slice 38's own arithmetic has never been tested against a
run.

## Still open

**`0005` and the last of Stage 2.** `menu_item.menu_section_identifier uuid NOT NULL REFERENCES
menu_section`, the conditional one-section seed and the backfill, `section_changed` with its payload column
and a vocabulary CHECK now droppable **by name**, and the three surfaces the `NOT NULL` forces: the section
create page, the section picker on the item form, and a harness `CreateMenuSectionAsync` the five ordering
scenarios call before their first `CreateMenuItemAsync`.

**The section headings on the guest menu.** The cards exist; grouping them is an outer loop and needs
`0005`.

**The section writes are still not behind `IMenuWorkflow`.** Recorded a fourth time. It becomes a defect the
moment the guest menu groups by section, which is the next slice.

**§16.3 scenario 17.** Create a section, create an item in it with a description, read both back off the
guest surface. Half of it is now expressible; the section half is not.

**The kitchen's "86" panel still groups by nothing.** Stage 3's remaining surface.

**F-41 has no row in `DOCUMENTATION_REVIEW.md`.** Fourth slice carried. Still a decision rather than a
repair.

**`.sitting-meta` is declared by two components and the two have drifted.** Deferred a sixth time.

**A CI job that runs the canonical stack on the canonical engine.** Fifteenth consecutive slice.

**`run.sh --containers-only` prints two `Error:` lines about a container that does not exist yet, then
starts it successfully.** Carried.

**No authoring-environment database for a second consecutive slice.** Slice 38 recorded this as a drop in
evidence quality and asked the next slice to restore the practice. It did not. That is now a two-slice
pattern rather than a one-off, and it is the reason F-78 was found by a test run on your machine instead of
by a migration in mine.

# M6 Slice 40 — the heading every item has, and a vocabulary nobody could check

## Read this first: Slice 39 was green, and the number it predicted was finally tested

```
Test summary: total: 1124, failed: 0, succeeded: 1124, skipped: 0, duration: 147.5s
```

That number matters more than a green line usually does. **Slice 38 predicted 1124 and never reached a
count** — its migration threw at fixture initialisation, so the arithmetic went untested. Slice 39 re-predicted
1124 unchanged and shipped the F-78 repair. This run is the first time in three slices that a prediction
was compared against a run, and it matched exactly. The full local CI ran green as well: sixteen §16.3
scenarios in 147.6s, the boot smoke check, the restore drill, and a quick tunnel that served the
application over a public origin.

## The authoring environment has a database again, and it earned its keep in the first hour

Slice 39's "still open" ended on this, and it was the sharpest line in the file: *"No authoring-environment
database for a second consecutive slice… That is now a two-slice pattern rather than a one-off, and it is
the reason F-78 was found by a test run on your machine instead of by a migration in mine."*

PostgreSQL 16 was installed in the authoring environment before a line of `0005` was written, and **every
statement in this slice was executed rather than reasoned about**. What that bought, concretely:

- `0005` applied on an **empty** database (no seed section) and on a **populated** one (seed written, two
  items backfilled under it, `SET NOT NULL` and the foreign key applied, positions preserved). Both
  branches of the conditional seed were walked, which is the only way to walk them — a fresh container
  takes the first branch and can never take the second.
- `SELECT … FOR UPDATE` with a correlated `MAX + 1` subquery was run before it was written into
  `DapperMenuAdministration`. PostgreSQL rejects `FOR UPDATE` alongside aggregation in the *outer* query,
  and the shape that works is the one now in the file.
- The **three-event create** was executed as a transaction — `created`, `section_changed`,
  `description_changed` — and accepted.
- The new paired CHECK was **proven to bite**: an INSERT of a `created` event carrying a section is refused
  with `violates check constraint "menu_item_event_section_payload"`. That is the constraint doing its job
  under observation rather than a claim that it would.
- All five new `SchemaMigrationRunnerTests` probes were run against the live schema: `attnotnull` true, the
  named foreign key present, `menu_item_section_index` present, eight CHECK constraints on
  `menu_item_event`, and zero sections on a fresh database.

## F-80 — a vocabulary copied out of its own constraint, wrong for two migrations, with no symptom

`EventTypeVocabulary.MenuEventTypes` in `EventExplorerReads.cs` feeds §11.4's explorer dropdown. Its doc
comment said *"the five `menu_item_event.event_type` values"* and it listed five. `0004` added
`description_changed` and `reordered`. Neither the list nor the sentence was touched.

**Nothing broke, and that is the finding.** §11.4's explorer deliberately never *refuses* an unrecognised
type — `IsKnown` exists only to warn an administrator that a hand-edited `?type=` is not a word this build
catalogues, and the filter runs regardless, because a schema this build has not caught up with is exactly
the case where somebody most needs to see the rows. So a missing word has **no run-time symptom at all**.
It is two of the menu's verbs that cannot be chosen from the dropdown, on the page whose entire purpose is
choosing. Two slices, every gate green throughout.

**This is F-77 one register worse.** That row deleted a *count* of this same vocabulary, written in three
files and checkable in none. This is the vocabulary **itself**, copied into a second file, silently
drifting — in a file neither slice that widened the vocabulary had any reason to open.

**The repair is F-47's habit, and the three choices inside it are each deliberate.**
`MenuEventVocabularyContractTests` walks `Migrations/*.sql` in name order (which is DbUp's apply order),
takes the `event_type IN (…)` list from the last script declaring `menu_item_event_type_vocabulary`, and
compares the set against the C# list.

- **Not a count.** A count would have passed every version of this bug — which is precisely what F-77
  established about this exact vocabulary.
- **Non-vacuity is asserted.** A regex that matched the constraint and extracted no quoted words would
  otherwise pass against an empty list, which is F-41's failure mode.
- **SQL text, not a database.** `SchemaMigrationRunnerTests` owns "this constraint exists on a real
  PostgreSQL"; "the constraint and the C# list agree" is a different question and belongs in the fast suite,
  where a wrong answer is available in seconds.

Simulated against the real files before shipping: it identifies `0005` as the last declaration and derives
the same eight types the C# list now holds.

## `0005` needed no dollar-quoted block, and that is what `0004` bought

`0004` needed a `DO` block only because it had to query `pg_constraint` for names PostgreSQL had generated.
Since it replaced every one with a chosen name, the single CHECK that had to widen here was dropped **by
name** — two ordinary statements, nothing to query, nothing for dbup-core's variable substitution to
collide with. **F-78 was a one-migration problem rather than a recurring one because the previous slice paid
for the names.** The script says so in its header, so that a future migration does not reintroduce a block
it does not need.

## The seed carries two guards, and the plan specified one

`docs/MENU_AND_HANDHELD_PLAN.md` specified the seed as *one* section, *only if `menu_item` has rows*. That
is necessary and not sufficient. **"No surface calls `IMenuSectionAdministration`" is not the same claim as
"no row exists"** — Slice 37 shipped that write service and registered it, and a database where somebody
exercised it holds sections. Without a second guard the INSERT would trip `menu_section.name`'s `citext`
UNIQUE on any database holding a section called "Menu", and a migration that fails at startup takes the
whole application down.

So the seed is guarded by `EXISTS (SELECT 1 FROM menu_item) AND NOT EXISTS (SELECT 1 FROM menu_section)`,
and the backfill correspondingly targets the **first section in display order** rather than the seed's
literal identifier. Both paths converge: if the seed ran it *is* the first section; if it did not, the
orphans go under the earliest heading that already exists.

## Two decisions kept a mandatory column from reaching sixteen files

This is the transferable part of the slice, and it is worth stating as a rule rather than as two
implementation notes. **When a mandatory argument arrives late, give the arrangement helper a default rather
than threading the argument through every caller that does not care about it.**

- **`OrderTestWorld.AddMenuItemAsync` takes an optional section** and lazily creates a house heading named
  "Menu" when none is given, cleared by `TruncateAsync`. The dozen integration test files that put something
  on the menu — about ordering, settlement, the kitchen, visibility, none about headings — compile unchanged
  and mean exactly what they meant. The house section is created lazily rather than in `TruncateAsync`
  because several classes here count rows and should not carry one they did not ask for.
- **`AdministrationJourneys.CreateMenuItemAsync` arranges its own heading** through
  `EnsureMenuSectionAsync` before opening the form. **The sixteen existing §16.3 scenarios needed no edit at
  all.** `EnsureMenuSectionAsync` is idempotent by *looking first* rather than by submitting and swallowing
  a "name taken" failure — the latter would also pass on a form that reported the wrong error, and "taken"
  is a real outcome this project asserts elsewhere.

## Rulings inside `0005`, each of which could have gone the other way

**An item is appended at `MAX(display_order) + 1` within its section, reversing `0004`'s "created at
position 0".** The reason the rule could change is the reason it existed: "the end of the menu" was not a
defined place while an item had no heading, and it is defined now. `MAX + 1` rather than `COUNT(*)`, on the
rule `menu_section` has followed since `0003` — a count collides with an existing position as soon as
anything has been moved, and `AppendingUsesTheHighestPositionRatherThanTheCount` is the assertion.

**The lock is on the section row, not the item.** Locking the section is what serialises two administrators
creating an item under the same heading at the same moment: without it both read the same `MAX`, both write
the same position, and the menu has two dishes claiming one place — which the schema *permits*, positions
being deliberately non-unique, and which is therefore a defect nothing would ever report. It doubles as the
existence check, which is why a missing heading is a reported `MenuSectionNotFound` rather than PostgreSQL
error 23503 naming a constraint.

**`created` still carries the name and the price alone**, so an item created under a heading writes two
events and one with a description writes three. Widening it would relax an equality to an implication and
break every `created` row already written. **The position writes no event ever**, because
`new_display_order` is bound to `reordered` and a `created` row carrying a position would be false of every
row written before `0005`.

**One index, and it is not the one `0004` declined.** PostgreSQL does not index the referencing side of a
foreign key, so without `menu_item_section_index` every statement touching a `menu_section` row scans
`menu_item`. Its trailing columns are the tail of §11.1's `ORDER BY`, so one index answers both.

## §7's asymmetry, implemented, and it points two ways one sentence apart

An inactive **item** stays on the guest's menu, marked, unorderable. An inactive **section** is not rendered
to the guest at all. That is not a contradiction: switching off a heading is a decision about a whole part
of the menu ("no breakfast this evening"), where 86ing a dish is a decision about one thing a guest is still
entitled to know exists. Neither flag cascades to the other.

**Both are carried unfiltered by `IMenuDirectory` and the filtering is on the surface.** §11.4's
administrator must see every heading including the ones no guest can reach — that is precisely the row
somebody is looking for when they wonder why an available dish is not on the menu — so
`/administration/menu` and `ManageMenuItem` both carry a *Section hidden* chip.

## Two scope rulings, flagged for veto

**`MoveMenuItemToSectionAsync` is not in this slice.** The plan schedules it with Stage 2's data access. The
item editor that would call it is Stage 3, and this project's own rule — a verb with no caller is a code path
no test can reach through the interface meant to protect it — applies to it exactly as it applied to the
section verbs for three slices. **To reverse:** the verb is a `section_changed` event and an `UPDATE`
alongside `ReorderMenuItemAsync`, plus a picker on `ManageMenuItem.razor`; nothing in this slice blocks it.

**Only `CreateMenuSectionAsync` moved behind `IMenuWorkflow`.** The obligation carried four times **narrows
to four verbs rather than closing**. The create page is a caller, so that verb arrives and publishes
`MenuChanged` on a committed row; rename, describe, reorder and set-active have no surface. What changed is
the *cost* of leaving them: §11.1's guest menu groups by heading now, so a renamed section that announced
nothing would leave a stale heading in every open picker. That defect is real and merely **unreachable**.
`MenuWiringTests`' fake throws from all four with a message naming the obligation, so the next person to
wire one is told rather than left to notice.

## One assertion was cut from scenario 17, and the cut is recorded rather than quietly made

The scenario was drafted to deactivate a heading and watch it vanish from the guest's menu — §7's asymmetry,
and the one thing about it no unit test can see. That needs `SetMenuSectionActiveAsync` to have a surface,
which is the section editor this slice deliberately did not ship. Asserting it would have meant either a
harness reaching past the UI, which §16.3 refuses, or a verb wired for a test, which is worse.

It was replaced with an assertion this slice can actually make: a third item created under an existing
heading joins it rather than starting a new grouping, and lands at the end of it — `MAX + 1`-within-section
proven through a browser. The inactive-section rule is covered at the data layer by
`MenuDirectoryTests.AnInactiveSection_IsCarriedRatherThanFiltered` and is **unverified end to end**.

## What is in this slice

| Area | Change |
|---|---|
| Migration | `0005_menu_item_sections.sql` — **new**. Conditional two-guard seed, nullable column, backfill, `SET NOT NULL`, named foreign key, vocabulary widened by name, `new_menu_section_identifier` with its paired CHECK, one index |
| Data access | `MenuDirectory.cs` — three section members, INNER join, six-key ordering; `MenuAdministration.cs` — section on create, `MAX + 1` under a section lock, `CreateMenuItemOutcome`, widened result; `MenuEventLog.cs` — section payload and an aliased LEFT join |
| Web | `MenuWorkflow.cs` — `CreateMenuSectionAsync`, section on the item create, that publish made conditional; `OrdersServiceCollectionExtensions.cs` — the registration note rewritten |
| Surfaces | `CreateMenuSection.razor` — **new**; `CreateMenuItem.razor` — picker and first-use panel; `AdministrationMenu.razor` — Section column, Create section, `section_changed` arm; `ManageMenuItem.razor` — the heading and its arm; `TableOrderSurface.razor` — §11.1 grouped under headings |
| Stylesheet | `app.css` — `.order-menu-section` and its heading. No new breakpoint, no colour literal, no `var()` fallback |
| Explorer | `EventExplorerReads.cs` — the menu vocabulary corrected from five to eight (F-80) |
| Harness | `AdministrationJourneys.cs` — `CreateMenuSectionAsync`, `EnsureMenuSectionAsync`, `FindMenuSectionAsync`, a section on item creation; `TableOrderJourneys.cs` — `MenuCard.SectionName`, section-walking read, `ReadMenuSectionNamesAsync` |
| Tests | `MenuEventVocabularyContractTests.cs` — **new**, two facts; `MenuDirectoryTests` +2; `MenuAdministrationTests` +3; `SchemaMigrationRunnerTests` +2; `MenuWiringTests` +2; `MenuEventLogTests` counts corrected; `EndToEndScenarios.cs` — scenario 17; `OrderTestWorld.cs` — `AddMenuSectionAsync` and the optional section |
| Documents | S v1.25 (§7, §8.1, §8.2, §16.3, §16.4, Appendix A, changelog), ledger F-80, plan Stage 2 closed and Stage 3 partly struck, `_CHANGES.md` |

## What was verified

**Everything SQL, against a live PostgreSQL 16.** Listed in full at the top of this entry rather than
summarised here, because it is the difference between this slice and the two before it.

**The working tree was reconstructed from `dump.txt` and checked against the SHA-256 recorded for every
file: 341 of 341 byte-identical.** The only file the dump does not reproduce is `export.sh`, which
documents itself inside its own output. Every edit below was made against a verified tree.

**Three gates were simulated rather than assumed.** `MarkdownTableContractTests` was run in substance over
every Markdown file in the repository — fence-aware, escaped-pipe-aware — and reports **zero** mismatches,
including the two new four-cell rows. `SpecificationVersionTests`: header 1.25, newest entry 1.25, entries
descending. `MenuEventVocabularyContractTests`: derives eight types from `0005` and matches the C# list.

**Brace, paren and bracket balance** on every C# file touched, checked after each edit rather than at the
end. **Every `InsertEventAsync` call site** was enumerated after the signature change and each of the seven
confirmed to carry the new argument — by search, not by having written them.

**The contiguity assertion in `MenuDirectoryTests` was proven sensitive.** A grouped list of four returns 2
runs; a scattered list with the same set of names returns 4. An assertion that could not tell those apart
would be the whole fact rendered vacuous.

**Selector existence in both directions** for the new markup: `.order-menu-section` and
`.order-menu-section-name` exist in `app.css` and are read by both the surface and the harness;
`--hairline` and `--ink-soft` are declared in `:root`.

## What was NOT verified

**Nothing compiled.** No .NET SDK in the authoring environment. The likeliest sites of a complaint are named
rather than left to be found: **`InputSelect` bound to a `Guid`** on the item form, which this tree does
elsewhere for enums but not for `Guid`; **`_created is { Created: true } created`**, a property pattern with
a designation, on two Razor pages; and **`Assert.Single(menu, card => card.Name == …)`**, whose predicate
overload returns the element in xUnit v3 and returned `void` in some v2 lines.

**No test ran.** The SQL is executed and the C# is not. Every count below is arithmetic.

**No browser rendered the grouped menu.** Named consequences a first run may show: whether an uppercase
letter-spaced heading at 0.78rem is legible enough on a 375px handset; whether two headings with one item
each read as grouping or as clutter; and whether `aria-labelledby` on a per-section `<ul>` announces
sensibly when the same page has several, which only a screen reader decides.

**The `0005` backfill branch was walked by hand and cannot be walked by a test here.** A fresh container
takes the no-seed branch, which `Run_SeedsNoSectionOnAFreshDatabase` asserts. The populated branch is
recorded in this entry as manually exercised, and it is the branch that runs on your actual database.

**§7's inactive-section rendering rule is unverified end to end**, for the reason above.

## Test count

Last observed: **1124**, from Slice 39 — and, for the first time in three slices, a prediction that matched
its run.

Predicted here: **1136**. The arithmetic: `MenuEventVocabularyContractTests` +2, `MenuDirectoryTests` +2,
`MenuAdministrationTests` +3, `SchemaMigrationRunnerTests` +2, `MenuWiringTests` +2, §16.3 scenario 17 +1.
No fact was removed; several had their assertions corrected in place. §16.3 goes from **16** to **17**.

Per §18: if the run returns anything other than 1136, that difference is the next thing to chase.

## Still open

**The section editor, and it is now the highest-value thing outstanding.** A heading created with a typo can
only be worked around by creating another. It carries four workflow verbs, `MoveMenuItemToSectionAsync`, the
sections-first index, and the end-to-end assertion cut from scenario 17.

**A section's own description under its heading on the guest menu.** The surface groups from
`MenuItemSummary`, which carries the heading's name and not its description; showing it needs either a
second read or a widened record, and guessing between those was not this slice's business.

**The kitchen's "86" panel still groups by nothing.** Stage 3's remaining surface, and the last one.

**§16.3 scenario 17 does not deactivate a section.** Named above; lands with the editor.

**F-41 has no row in `DOCUMENTATION_REVIEW.md`.** Fifth slice carried. Still a decision rather than a repair.

**`.sitting-meta` is declared by two components and the two have drifted.** Deferred a seventh time.

**A CI job that runs the canonical stack on the canonical engine.** Sixteenth consecutive slice.

**`run.sh --containers-only` prints two `Error:` lines about a container that does not exist yet, then
starts it successfully.** Carried.

**The handheld barrier does not visit `/administration/menu/sections/new`.** Scenario 16 walks ten surfaces
and this slice added an eleventh administration page. It is a create form rather than a record list, so
none of the barrier's three reach selectors matches it and the control count would not move — which is
exactly why it would be easy to leave out permanently. Recorded so it is not.

# M6 Slice 41 — the section editor, a reserved word two files were named after (F-81), and the gate that never ran (F-82)

**Closes the deferred obligation this project has been counting down since `0003`.** All five of
`IMenuSectionAdministration`'s verbs now have a surface and all five are behind `IMenuWorkflow`. A heading
can be renamed, described, moved and switched off, and each of those announces `MenuChanged` on a committed
row and nothing on a write that committed nothing.

**And it unblocks the four things that were waiting on it**: `IMenuSectionEventLog`, without which §11.4's
"complete stored record" had nothing to read for a section; the menu index's Section column, which was a
column linking nowhere; the harness journey that had to recover a new section's identifier from a
neighbouring form; and §16.3 scenario 17's two cut steps.

## The two findings, and the order they were found in

**F-81 — two loop variables named after a Razor directive.** Slice 40 wrote
`@section.MenuSectionIdentifier` on the create-item form and `@section.MenuSectionName` on the guest
ordering surface. `@section` is MVC's **section directive**, reserved in Razor's own grammar, so the parser
read a directive with a malformed name and produced four errors across two files — `RZ9979`, `RZ2005`,
`RZ1011` — none of which mentions an identifier and none of which is about the markup. `RZ1011`'s column
lands on the `.` immediately after the seven characters of `section`, which is the only thing in the four
messages that points at the actual cause.

Two properties of it are worth keeping.

**It is invisible in review.** `@key="section.MenuSectionIdentifier"` one line above and
`@SectionHeadingId(section)` one line below compile perfectly, because neither puts the word directly after
an `@`. So the errors read as complaints about the `<option>` and the `<h4>`, and the identifier — which is
the whole cause — appears in none of them.

**It is blocking everywhere.** `MyRestaurant.WebApplication` is the project every other one references. The
unit suite, the integration suite and all seventeen §16.3 scenarios were unreachable together.

**F-82 — the gate against stale counts had gone stale, and said nothing, because it never ran.**
`TestingSectionContractTests` compares every assertion count §16.4 states against the file it names. It was
written after F-70, where exactly that drift concealed an undocumented gate for four slices. Slice 40 added
assertions to four classes §16.4 cites and moved none of the four numbers:

```
MenuAdministrationTests      §16.4 said 23   file holds 26
MenuDirectoryTests           §16.4 said  5   file holds  7
MenuWiringTests              §16.4 said 11   file holds 13
SchemaMigrationRunnerTests   §16.4 said  5   file holds  7
```

Slice 40's own delivery note predicted every one of those increments **by name**. The arithmetic was done,
written down, and never carried into the document.

**This is F-71 read from the other side.** That finding was a test project failing to compile behind a
summary line reading `total: 497, failed: 0`. This is a gate that never started, behind a build error
everybody was already looking at. The shared lesson: **a gate that cannot run is indistinguishable from a
gate that passed**, and nothing in this repository distinguishes them — `dotnet build` reports what failed
to compile and no artefact reports what consequently failed to *execute*.

No gate is added for F-82, deliberately. The gate that would have caught it is the gate that did not run,
and the repair for *that* is F-81's. What is added is the pairing, stated in §16.4 and in the class's own
summary: the first question about a red build is *what stopped being checked*, not only *what stopped
compiling*.

## What this slice does

- **`ManageMenuSection.razor`** at `/administration/menu/sections/{id}` — static SSR, four forms,
  post/redirect/get with a one-word outcome, a facts grid, the heading's items, and its complete uncapped
  event history. Declares no CSS: every class it uses is app.css's §11.12 vocabulary, checked in both
  directions.
- **`IMenuSectionEventLog` / `DapperMenuSectionEventLog`** — the per-heading history read.
- **Four verbs behind `IMenuWorkflow`**, each publishing `MenuChanged` only on a committed row.
- **Links into the editor** from the create panel, the menu index's Section column, and each item's page.
- **F-81's rule made executable** — `RazorDirectiveContractTests`, two facts.
- **F-82's four counts corrected**, and `MinimumCountedClasses` moved from sixteen to eighteen.
- **Scenario 17 regains its two cut steps**, and comes back larger than it was cut.

## Three rulings

**`IMenuSectionEventLog` is a second reader, not a widened `IMenuEventLog`.** Two tables, two vocabularies,
and the two share three type words while meaning different things by all three — a `renamed` section is not
a `name_changed` item, and neither log's payload columns exist on the other. A `UNION ALL` over both is a
real read §11.4's explorer may want one day; it is not this, and building one here would make the
per-section history pay for a merge it never uses.

**No cross-section activity feed.** `IMenuEventLog.ListRecentAsync` exists to fill a panel on
`/administration/menu`, which is an index over items. Sections have no such panel, and a read with no caller
is the same defect as a workflow verb with no caller — which is the rule this slice spent four verbs
discharging, so inventing a fifth instance of it in the same archive would be absurd.

**The editor reads the whole menu and filters in memory** rather than adding a per-section query with one
caller. `IMenuDirectory.ListAsync` already orders by section first and makes each heading's items
contiguous, so the filter preserves the order guests see without re-deciding it in a second file (§7). It is
a read that grows with the menu, on a database whose whole reason for existing is one restaurant. **Flagged
for veto**: the reversal is one method on `IMenuDirectory` and one call site.

## The four verbs' broadcasts, and why two of them are not optional

**A rename** is the one that had stopped being latent. §11.1 renders a heading above every card under it, so
a rename that committed and announced nothing leaves the old word on every open picker until that page
happens to reload.

**A visibility flip** is worse. §7 hides an inactive section from the guest *entirely* — the opposite of the
rule one paragraph away for an inactive item — so switching a heading off without a broadcast leaves a whole
part of the menu tappable on every phone already looking at it, until the send is refused server-side for a
reason the guest never saw coming (§6.5.9).

**A description** publishes and reaches no guest surface today, and that is the same ruling the item
description already carries: `MenuChanged` means *re-read the menu* and nothing else, and a workflow that
decided which columns were worth announcing must be edited again the moment a surface starts reading one.

**A move** publishes because §11.1 orders headings by `(display_order, name, identifier)`, so the whole menu
is in a different order even though no item moved.

## Scenario 17 came back larger than it was cut

Slice 40 drafted a deactivation assertion, cut it, and recorded the cut. The restored version does not only
watch the heading vanish. It asserts that the *other* heading's items stay present, in order, and orderable;
then switches the heading back on and checks the menu returns exactly as it was.

That second half is the only end-to-end proof that deactivating a section **does not cascade** to its items.
A cascade would come back with the pie marked unavailable. It was not in the draft — it became obvious once
the assertion was being written against a surface that existed, and it would not have been written at all if
the cut had been made silently.

## What was verified

**The working tree was reconstructed from `dump.txt` and checked against the SHA-256 recorded for every
file: 344 of 344 byte-identical.** Two files needed repair in the *reader* rather than the tree —
`export.sh` contains its own `# FILE:` banner as literal text, and the last file in the dump abuts the
`DUMP SUMMARY` footer — and both were confirmed against their recorded hashes after repair.

**`RazorDirectiveContractTests` was run in substance** over all 51 components: zero uses, and the
non-vacuity guard's floor of twenty is met four times over. Its five sensitivity cases were run and all five
behave as the second fact asserts.

**`TestingSectionContractTests` was run in substance**, before and after. Before: 16 counted classes, **4
disagreements** — which is F-82, found by simulation rather than by reading. After: 18 counted classes, 0
disagreements, 0 ambiguous, 0 uncited.

**`MarkdownTableContractTests`** over every Markdown file in the repository, fence-aware and
escaped-pipe-aware: 60 table runs, zero findings, including the three new four-cell Appendix A rows and the
two new ledger rows.

**`SpecificationVersionTests`**: header 1.26, newest changelog entry 1.26, 27 entries descending.

**`MenuEventVocabularyContractTests`**: eight types derived from `0005`, set-equal to the C# list. Unchanged
by this slice and re-run because the slice touches the menu.

**`HandheldLayoutContractTests`' data-label parity** across all 8 record-list components — up from 7,
because `ManageMenuSection` adds two lists — every one with cells equal to labels. Its palette fact was run
against the new page: zero `var()` references to undeclared properties, zero inline `<style>`, and every one
of the classes it names exists as a selector in `app.css`.

**Brace, paren and bracket balance** on all nine changed C# files, string- and comment-aware.

**The balance checker was itself proven, and it failed the proof first.** Its first run reported an
imbalance in the new `MenuSectionEventLog.cs`. Running it against two *untouched* sibling files —
`MenuEventLog.cs` and `MenuSectionDirectory.cs`, both byte-verified against the dump — produced the
identical report, which is what identified the checker rather than the file: it read `$"""` as an empty
interpolated string and parsed the SQL body as code. Fixed, re-run, all nine balanced. **A verification tool
that has not been run against a known-good input is a verification tool with no established false-positive
rate**, and this is the second slice in which that mattered.

**Byte hygiene** on every changed and new file: no CR, exactly one final newline, no whitespace-only line,
no context-dump separator.

## What was NOT verified

**Nothing compiled.** No .NET SDK in the authoring environment. The likeliest sites of a complaint are named
rather than left to be found: **`Assert.Single(restored, card => card.Name == pie.Name)`**, whose predicate
overload returns the element in xUnit v3; **`Assert.Equal(string.Empty, described.NewDescription)`** on a
`string?`, which is an equality rather than a null-flow narrowing precisely so it does not depend on how
xUnit annotates `Assert.NotNull`; and **the `foreach` over a `bool[]` inside a `[Fact]`** in
`ASectionVisibilityFlip_…`, which asserts twice in one loop and will report the first failure only.

**No test ran.** Every count below is arithmetic.

**No browser rendered the section editor.** Named consequences a first run may show: whether four forms plus
two record lists on one page reads as an editor or as a wall at 375px; whether *Hide from guests* as a
`link-button danger` is the right weight for an action that is fully reversible; and whether the two record
lists on one page need distinguishing headings for a screen reader, which only a screen reader decides.

**The `.manage-facts .chip` selector the new harness journey waits on is unexercised.** It is the first
harness read in this project that keys on a chip inside the facts grid rather than on a flash or a heading.
If scenario 17 times out at step (g), that selector is the first thing to check and the failure will name
it.

**§16.3 scenario 17 is longer than any scenario in the suite** and now spans two §9 broadcasts on an
already-open circuit. If it becomes flaky, the deactivation wait is the more likely half — it waits for an
*absence*, which `WaitForMenuAsync` expresses as a predicate over the whole menu.

**Nothing verified that F-82's four counts were the only stale ones.** The simulation compares what §16.4
states to what the files hold, which is exactly the gate's own reach; a class §16.4 never cites is invisible
to both, and that residual is stated in §16.4 rather than closed.

## Test count

Last predicted: **1136**, from Slice 40 — and **not observed**, because the build failed. The last observed
count is **1124**, from Slice 39.

**That gap is itself the finding.** §18's habit is that a predicted count the run contradicts is chased
before the slice closes; a predicted count that never meets a run cannot be chased at all, and F-82 is what
was sitting in it.

Predicted here: **1149**. The arithmetic, from 1136: `RazorDirectiveContractTests` +2,
`MenuSectionEventLogTests` +6, `MenuWiringTests` +5. Scenario 17 gains assertions and no new `[Fact]`, so
§16.3 stays at **17**. No fact is removed.

Per §18: if the run returns anything other than 1149, that difference is the next thing to chase — and this
slice is the first opportunity since Slice 39 to perform that check at all.

## Still open

**The sections-first index.** `/administration/menu` is still an item list with a Section column. The column
links now, which was the blocker; what remains is the restructure.

**`MoveMenuItemToSectionAsync`.** The last verb in the whole enhancement with no surface — `ManageMenuItem`
shows the heading, links to it, and cannot change it. It is now the only instance left of the rule this
slice spent four verbs discharging.

**A section's own description under its heading on the guest menu.** Unchanged from Slice 40: the surface
groups from `MenuItemSummary`, which carries the heading's name and not its description.

**The kitchen's "86" panel still groups by nothing.** Stage 3's last surface.

**The handheld barrier visits neither section surface.** Scenario 16 walks ten and this slice adds a twelfth
administration page. `ManageMenuSection` is a detail surface with `.manage-inline-form` buttons, so unlike
the create page it *would* move the control count — which makes leaving it out a real gap rather than a
neutral one. Carried, and now larger than when Slice 40 recorded it.

**Nothing reports which gates a failed build prevented from running.** F-82's residual, stated rather than
resolved.

**F-41 has no row in `DOCUMENTATION_REVIEW.md`.** Sixth slice carried.

**`.sitting-meta` is declared by two components and the two have drifted.** Deferred an eighth time.

**A CI job that runs the canonical stack on the canonical engine.** Seventeenth consecutive slice.

**`run.sh --containers-only` prints two `Error:` lines about a container that does not exist yet, then
starts it successfully.** Carried.
