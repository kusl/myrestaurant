# Build progress

This is the running record of how MyRestaurant is being built. It exists so any
future session (or person) can see what is done, what is deliberately stubbed,
and what to watch out for. The technical specification is the source of truth;
this file only tracks execution against it.

## How this was produced (read this first)

The scaffold and each subsequent milestone are written in an environment
**without a .NET SDK** and without NuGet/.NET download hosts. Consequences:

- **The C# for a milestone is written to match the spec and the .NET 10 APIs, then
  first compiled on your machine.** M1 has since been built and tested there
  (green — see below). M2 code is, like M1 was, unbuilt until your next
  build/test run; expect to fix the occasional thing a compiler would catch.
- **Package versions in `Directory.Packages.props` are best-effort.** They target
  the .NET 10 GA era. Run `dotnet restore`; if a version does not exist, bump it
  there to the nearest available. Nothing else references versions. New in M2:
  `Konscious.Security.Cryptography.Argon2` (its repo publishes no Git tags, so
  the pin is the latest known NuGet release — bump if restore cannot find it).
- Shell scripts are syntax-checked with `bash -n`; they may need `chmod +x`.

## Staged plan

The work is split into six stages aligned to the spec's milestones (§19). Each
stage is meant to leave the tree buildable and testable.

- [x] **Stage 1 — M1: skeleton + pure Domain** *(built green: 139 passed, 28 skipped)*
- [ ] **Stage 2 — M2: identity & accounts** *(in progress — identity data layer + Argon2id hasher landed)*
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

M2 is being landed in coherent, individually-buildable slices. **This slice: the
identity persistence layer and the Argon2id password hasher**, wired as Identity
core services and covered by tests. It intentionally stops short of sign-in flows,
passkeys, and the bootstrap — those are the next slices (see "Next").

Delivered:

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
  The startup floor guard stays in `RestaurantOptions.Validate` (already present).
- **`DapperUserStore`** (`DataAccess/Identity/`) — one class implementing the store
  family `UserManager<Person>` needs short of passkeys: `IUserStore`,
  `IUserPasswordStore`, `IUserSecurityStampStore`, `IUserLockoutStore`,
  `IUserTwoFactorStore`, `IUserAuthenticatorKeyStore`,
  `IUserTwoFactorRecoveryCodeStore`, `IUserRoleStore` (read side), `IUserEmailStore`,
  `IUserPhoneNumberStore`. TOTP secret stored Data-Protection-encrypted; recovery
  codes in their own table, SHA-256-hashed and single-use; duplicate/short usernames
  mapped to `DuplicateUserName`/`InvalidUserName` results.
- **`AddRestaurantIdentity`** (`WebApplication/Identity/`) — `AddIdentityCore<Person>`
  with the §3.1/§3.2 options (password length 12/no composition, lockout 5/5-min,
  no email/phone confirmation), `AddUserStore<DapperUserStore>()`, default token
  providers (incl. the authenticator/TOTP provider), and the Argon2id hasher
  replacing the PBKDF2 default. Called from `Program.cs` after Data Protection.
- **Project wiring**: `MyRestaurant.DataAccess.csproj` now has
  `<FrameworkReference Include="Microsoft.AspNetCore.App" />` (so the Identity core
  abstractions and Data Protection are available in this non-web library at the
  installed runtime version — no NuGet version to pin) plus the Konscious package;
  the same FrameworkReference is added to `MyRestaurant.DataAccess.Tests.csproj`.
- **Tests**: `Argon2idPasswordHasherTests` (round-trip, wrong/garbage input, PHC
  shape, salt randomness, rehash signal — no container needed); `DapperUserStoreTests`
  (create/lookup with citext casing, duplicate/short-username mapping, password,
  security-stamp regeneration, lockout counters/end-date, TOTP-secret encryption at
  rest, hashed single-use recovery codes, role read path, and the refused operations:
  delete, unattributed grant/revoke — Testcontainers, skips without an engine).

### Build/test checklist for this slice

1. `dotnet restore` — resolves the new Konscious package (bump the pin if needed).
2. `dotnet build` — the Identity code is unverified until this runs.
3. `dotnet test` — hasher tests run everywhere; store tests need Podman/Docker.

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
  bite live sessions once stamp revalidation is wired (next slice).
- **M2 — two-factor is derived, not flagged.** `GetTwoFactorEnabledAsync` reads
  `totp_secret_protected`; `SetTwoFactorEnabledAsync(false)` clears the secret.
- **M2 — email/phone confirmation not modeled.** The schema has no confirmation
  columns (optional contact fields, manual escalation only, §11.1); the confirmed-*
  accessors are inert and sign-in never gates on them.
- **M2 — role grant/revoke via the store is `NotSupported`.** `person_role` requires
  the granting administrator (self-referencing for the first admin, §3.6), which the
  parameterless `AddToRoleAsync`/`RemoveFromRoleAsync` contract cannot supply. Grants
  land in the transactional account-administration service / `/setup` (next slices);
  the store's role **read** path is complete so claims flow at sign-in.
- **M2 — deletion does not exist (F-10b).** `DeleteAsync` throws; accounts are
  deactivated (`is_active=false`) so history keeps its actors.
- **Dev-machine note (not a code defect).** `run.sh` uses `dotnet watch`; a busy
  workstation can hit the kernel's inotify instance limit
  (`The configured user limit (128) on the number of inotify instances has been
  reached`). Raise it on the host, e.g. `sysctl fs.inotify.max_user_instances=1024`
  (persist via `/etc/sysctl.d/`). It does not affect the container runtime or CI.

## Next: remaining M2 slices (in order)

1. **Sign-in + authorization wiring**: `SignInManager`, cookie auth (Secure,
   HttpOnly, SameSite=Lax, 24-h sliding), **security-stamp revalidation every 5 min**
   (§3.1), roles→claims, and the area authorization policies (§3.7). Sign-in/lockout
   security events + `sign_ins_total{method,result}` (§12).
2. **Password sign-in flow** (username+password → TOTP/recovery challenge when
   enrolled) and the **obligations pipeline middleware** enforcing the pure
   `ObligationsPipeline` decision (§3.5): forced password-change and forced
   TOTP-enrollment pages; nothing else reachable while a flag is set.
3. **TOTP enrollment**: provisioning URI → server-side SVG QR, confirm-code, fresh
   recovery codes. Verify Identity's Rfc6238 skew; if it is not ±1 step (§3.4), add a
   custom authenticator token provider.
4. **Passkeys** (`IUserPasskeyStore`, .NET 10 WebAuthn): registration/assertion,
   username-first and discoverable flows, the passkey sign-in path (never a TOTP
   challenge). Verify the new .NET 10 passkey API against the framework source first.
5. **`/setup` first-admin bootstrap** under `pg_advisory_xact_lock` with the
   zero-administrator re-check, self-granting the administrator role in one
   transaction (§3.6) — the first home for role grants.
6. **Account administration** (§3.7): create staff, grant/revoke roles (with grantor
   + `role_granted`/`role_revoked` events), **Reset credentials** (temp password +
   `must_change_password`; clear TOTP + set `must_enroll_totp` iff enrolled; new
   stamp; `password_reset_by_administrator` [+ `totp_cleared_by_administrator`]),
   deactivate/reactivate. Store-level integration tests + middleware tests throughout.
