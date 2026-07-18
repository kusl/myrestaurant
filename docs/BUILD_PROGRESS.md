# Build progress

This is the running record of how MyRestaurant is being built. It exists so any
future session (or person) can see what is done, what is deliberately stubbed,
and what to watch out for. The technical specification is the source of truth;
this file only tracks execution against it.

## How this was produced (read this first)

The scaffold was generated in an environment **without a .NET SDK**, and the
NuGet and .NET download hosts were not reachable from it. Consequences:

- **None of the C# has been compiled or run.** It is written to match the spec
  and the .NET 10 APIs, but the first real build happens on your machine. Expect
  to fix a few things a compiler would catch.
- **Package versions in `Directory.Packages.props` are unverified.** They were
  chosen for the .NET 10 GA era but not checked against nuget.org. Run
  `dotnet restore`; if a version does not exist, bump it there to the nearest
  available. Nothing else references versions.
- Shell scripts were syntax-checked with `bash -n`. They may arrive without the
  executable bit (`chmod +x run.sh scripts/*.sh`).

## Staged plan

The work is split into six stages aligned to the spec's milestones (§19). Each
stage is meant to leave the tree buildable and testable.

- [x] **Stage 1 — M1: skeleton + pure Domain** *(this scaffold; unbuilt)*
- [ ] **Stage 2 — M2: identity & accounts**
- [ ] **Stage 3 — M3: tables & joining**
- [ ] **Stage 4 — M4: ordering**
- [ ] **Stage 5 — M5: counter & administration**
- [ ] **Stage 6 — M6: hardening**

## Stage 1 — done (pending first compile)

Delivered:

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
- **Infrastructure**: `Containerfile` (SDK build → aspnet runtime, tzdata +
  curl), `compose.yaml` (postgres + web always; caddy in `dev`; cloudflared in
  `production`), `Caddyfile`, `run.sh`, `scripts/backup.sh`,
  `scripts/restore.sh`, `scripts/quick_tunnel.sh`, `.env.example`, `README.md`.
- **Tests**:
  - Domain (pure, real vectors): `JoinTokenServiceTests`,
    `Argon2PhcStringTests`, `ObligationsPipelineTests`, `OrderProjectionTests`,
    `OrderMutationValidatorTests` (every rule, both outcomes),
    `SecretGeneratorTests`.
  - DataAccess: `SchemaMigrationRunnerTests` (Testcontainers PostgreSQL 17 —
    applies migrations, asserts idempotency and that key relations exist).
  - WebApplication: `RestaurantOptionsTests` (binding, defaults, validation).
  - EndToEnd: skipped placeholder for the §16.3 matrix (filled in M6).

### First-build checklist

1. `dotnet restore` — resolve/adjust any package versions.
2. `dotnet build` — fix any compiler findings (the code is unverified).
3. `dotnet test` — the Domain and WebApplication tests need no services; the
   DataAccess test needs Podman/Docker running.
4. `./run.sh` — confirm migrations apply and `/healthz/ready` returns 200.

## Known caveats and deliberate decisions

- **Warnings are not errors.** `TreatWarningsAsErrors=false` keeps a fresh clone
  building through analyzer drift. Tighten to `true` once the build is green.
- **Not InvariantGlobalization.** The app relies on `TimeZoneInfo` for
  `RESTAURANT_TIME_ZONE`, so globalization stays on and the container installs
  `tzdata`. Do not set `InvariantGlobalization=true`.
- **DbUp logging.** `SchemaMigrationRunner` uses `LogToConsole()` rather than a
  custom `IUpgradeLog`, whose interface shape varies across DbUp versions. If
  the DbUp API differs from what is pinned, this file is the most likely place a
  build break appears — adjust the builder calls there.
- **Forwarded headers trust.** `Program.cs` clears `KnownIPNetworks`/`KnownProxies`
  so `X-Forwarded-*` from the tunnel/Caddy is honoured. This is safe only because
  the app is reached exclusively through a trusted proxy (Cloudflare tunnel in
  production, Caddy in dev) and is never exposed directly. Keep it that way.
- **Rootless volume ownership.** The data-protection volume is mounted `:U` in
  compose so Podman chowns it to the container user. On Docker this suffix is
  ignored (not needed there).
- **Compose profiles.** `web` + `postgres` always start. `--profile dev` adds
  Caddy (local TLS); `--profile production` adds cloudflared and **no** Caddy
  (the tunnel terminates TLS). A bare `up` is headless web on `:8080`.
- **Container-dependent tests.** DataAccess integration tests require a running
  container engine; Playwright E2E additionally needs the browsers installed
  (`playwright.ps1 install`) and a live instance.

## Next: Stage 2 (M2 — identity & accounts)

Planned scope: custom Dapper `IUserStore`/role/passkey stores over the `person`
tables; Argon2id `IPasswordHasher` with the floor guard and the concurrency
semaphore; passkey registration/assertion; TOTP enrollment + recovery codes;
lockout (5 fails / 5 min); the obligations middleware enforcing the §3.5
pipeline; `/setup` first-admin bootstrap under an advisory lock; roles/policies;
security-event logging; and administrator-driven password reset. Land it with
store-level integration tests (Testcontainers) and middleware tests.
