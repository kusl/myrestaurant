# Build progress

This is the running record of how MyRestaurant is being built. It exists so any
future session (or person) can see what is done, what is deliberately stubbed,
and what to watch out for. The technical specification is the source of truth;
this file only tracks execution against it.

## How this was produced (read this first)

The scaffold and each subsequent milestone are written in an environment
**without a .NET SDK** and without NuGet/.NET download hosts. Consequences:

- **The C# for a milestone is written to match the spec and the .NET 10 APIs, then
  first compiled on your machine.** M1 built green, and so did the first M2 slice
  (identity persistence + Argon2id hasher). The newest M2 slice — sign-in, cookie
  auth, and authorization wiring — is, like its predecessors were, unbuilt until
  your next build/test run; expect to fix the occasional thing a compiler would catch.
- **Package versions in `Directory.Packages.props` are best-effort.** They target
  the .NET 10 GA era. Run `dotnet restore`; if a version does not exist, bump it
  there to the nearest available. Nothing else references versions. The sign-in
  slice adds **no** packages — cookie auth, `SignInManager`, the security-stamp
  validator, and authorization all come from the ASP.NET Core shared framework.
- Shell scripts are syntax-checked with `bash -n`; they may need `chmod +x`.

## Staged plan

The work is split into six stages aligned to the spec's milestones (§19). Each
stage is meant to leave the tree buildable and testable.

- [x] **Stage 1 — M1: skeleton + pure Domain** *(built green: 139 passed, 28 skipped)*
- [ ] **Stage 2 — M2: identity & accounts** *(in progress — identity data layer + Argon2id hasher, then sign-in + authorization wiring, landed; latest local run: 147 passed, 41 skipped)*
- [ ] **Stage 3 — M3: tables & joining**
- [ ] **Stage 4 — M4: ordering**
- [ ] **Stage 5 — M5: counter & administration**
- [ ] **Stage 6 — M6: hardening**

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

### Slice 2 — sign-in + authorization wiring (this change)

The services the sign-in **flows** will need, plus authorization, plus the audit
trail sign-ins write to. No sign-in *pages* yet — those are the next slice — so the
new `SignInManager` is wired and unit-tested through a pure decision rather than
driven by a form.

- **Cookie authentication** (`WebApplication/Identity/IdentityServiceCollectionExtensions.cs`)
  — Identity's four cookie schemes via `AddAuthentication(...).AddIdentityCookies()`
  (application cookie is the default authenticate/challenge scheme, external is the
  default sign-in scheme — what `AddIdentity` composes internally, minus a
  `RoleManager` we do not want). The application cookie is hardened per §3.1:
  `HttpOnly`, `SecurePolicy=Always`, `SameSite=Lax`, 24-hour **sliding** expiration,
  name `myrestaurant.authentication`; login/logout/access-denied paths point at the
  sign-in surfaces the next slice builds.
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
  §3.7). The store implements `IUserRoleStore<Person>`, so the default
  claims-principal factory adds one role claim per granted role at sign-in.
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
  static files and before antiforgery/endpoints. They populate `HttpContext.User`
  and enforce policies; nothing is authorized yet, so Home and the health endpoints
  stay anonymous and there is no behaviour change until the area pages carry
  `[Authorize]`.
- **Tests**: `SecurityEventTypeTests` + `SignInAuditTests` (pure, always run);
  `DapperSecurityEventLogTests` (Testcontainers — null vs administrator actor,
  round-trip — plus a container-free guard test for the unknown-type rejection);
  `IdentityWiringTests` (builds the container and asserts the `SignInManager` type,
  cookie hardening, the 5-minute stamp interval, and the four policies' role rules).

### Build/test checklist for this slice

1. `dotnet restore` — no new packages; should be a no-op beyond Slice 1.
2. `dotnet build` — the sign-in/authorization C# is unverified until this runs.
3. `dotnet test` — the pure Domain tests and the wiring test run everywhere; the
   security-event store test needs Podman/Docker.

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
  before (M1).
- **M2 — security stamp is a `uuid`.** Identity's opaque Base32 stamp does not fit a
  `uuid` column, so `SetSecurityStampAsync` mints a fresh `Guid` and discards the
  passed string. The value is compared only for equality and regenerated on
  credential/role change, so this is faithful — and it is exactly what makes resets
  bite live sessions once stamp revalidation is wired (now wired, this slice).
- **M2 — two-factor is derived, not flagged.** `GetTwoFactorEnabledAsync` reads
  `totp_secret_protected`; `SetTwoFactorEnabledAsync(false)` clears the secret.
- **M2 — email/phone confirmation not modeled.** The schema has no confirmation
  columns (optional contact fields, manual escalation only, §11.1); the confirmed-*
  accessors are inert and sign-in never gates on them.
- **M2 — role grant/revoke via the store is `NotSupported`.** `person_role` requires
  the granting administrator (self-referencing for the first admin, §3.6), which the
  parameterless `AddToRoleAsync`/`RemoveFromRoleAsync` contract cannot supply. Grants
  land in the transactional account-administration service / `/setup` (later slices);
  the store's role **read** path is complete so claims flow at sign-in.
- **M2 — deletion does not exist (F-10b).** `DeleteAsync` throws; accounts are
  deactivated (`is_active=false`) so history keeps its actors.
- **M2 — the sign-in manager is wired but not yet reached by a page.** Its audit
  logic is proven by the pure `SignInAudit` tests and the wiring test. The actual
  sign-in **pages** are the next slice, and they must post to a static/SSR endpoint
  (not an interactive-circuit event) so the cookie can be written to the response —
  a Blazor Server constraint, not an Identity one.
- **M2 — Blazor auth-state cascade + per-area `[Authorize]` deferred.**
  `UseAuthentication`/`UseAuthorization` are in the pipeline and the policies are
  registered, but they enforce nothing until the area pages carry `[Authorize]` and
  the router wraps `CascadingAuthenticationState` — added with those pages.
- **Dev-machine note — inotify watch limit (now handled in `run.sh`).** `dotnet watch`
  can exhaust the kernel's inotify **instance** limit on a busy workstation
  (`The configured user limit (128) on the number of inotify instances has been
  reached`), which killed the watcher. `run.sh` now reads
  `fs.inotify.max_user_instances` and, when it is low (< 256) and the caller has not
  set `DOTNET_USE_POLLING_FILE_WATCHER`, falls back to the **polling** file watcher
  for that run so hot reload works without root. For the snappier native watcher,
  raise the cap once — `sudo sysctl fs.inotify.max_user_instances=1024` (persist via
  `/etc/sysctl.d/`) — or force it with `DOTNET_USE_POLLING_FILE_WATCHER=0`. Neither
  the container runtime nor CI is affected.

## Next: remaining M2 slices (in order)

1. **Password sign-in flow** (username+password → TOTP/recovery challenge when
   enrolled), built on the `SignInManager`/cookies wired this slice, plus the
   **obligations pipeline middleware** enforcing the pure `ObligationsPipeline`
   decision (§3.5): forced password-change and forced TOTP-enrollment pages; nothing
   else reachable while a flag is set. Sign-in pages post to static/SSR endpoints so
   the auth cookie can be written. This is also where the Blazor
   `CascadingAuthenticationState` and the first `[Authorize]`-gated area land.
2. **TOTP enrollment**: provisioning URI → server-side SVG QR, confirm-code, fresh
   recovery codes. Verify Identity's Rfc6238 skew; if it is not ±1 step (§3.4), add a
   custom authenticator token provider. Records `totp_enrolled` /
   `recovery_codes_regenerated` via the security-event log (`recovery_code_used`
   is already recorded by the sign-in manager wired in this slice).
3. **Passkeys** (`IUserPasskeyStore`, .NET 10 WebAuthn): registration/assertion,
   username-first and discoverable flows, the passkey sign-in path (never a TOTP
   challenge — records `sign_in_succeeded` with method `passkey`). Verify the new
   .NET 10 passkey API against the framework source first.
4. **`/setup` first-admin bootstrap** under `pg_advisory_xact_lock` with the
   zero-administrator re-check, self-granting the administrator role in one
   transaction (§3.6) — the first home for role grants.
5. **Account administration** (§3.7): create staff, grant/revoke roles (with grantor
   + `role_granted`/`role_revoked` events), **Reset credentials** (temp password +
   `must_change_password`; clear TOTP + set `must_enroll_totp` iff enrolled; new
   stamp; `password_reset_by_administrator` [+ `totp_cleared_by_administrator`]),
   deactivate/reactivate. Store-level integration tests + middleware tests throughout.
