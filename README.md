# MyRestaurant

[![ci](https://github.com/kusl/myrestaurant/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/kusl/myrestaurant/actions/workflows/ci.yml)
[![license: AGPL-3.0-only](https://img.shields.io/badge/license-AGPL--3.0--only-blue.svg)](LICENSE)

A self-hosted, single-restaurant ordering system: guests order from their table on their own phones,
the kitchen and counter work from live boards, and everything runs on one small box behind a
Cloudflare tunnel. Blazor Server over PostgreSQL, no external runtime dependencies beyond the
database and the tunnel.

**Milestones 1 through 5 are complete**, which means the product is feature-whole: identity and
accounts, table administration and rotating join QR codes, paired table displays, the living order
with its locking protocol, the kitchen and counter boards, billing and settlement, menu management,
and the administration surfaces including the cross-log event explorer. **Milestone 6 (hardening)**
is in progress — continuous integration has landed, and so has the Playwright end-to-end harness
with the first three scenarios of the §16.3 matrix; the remaining twelve scenarios and an executable
backup/restore drill are what is left. See *Roadmap* and `docs/BUILD_PROGRESS.md`.

## Layout

The solution (`MyRestaurant.slnx`) is a small set of projects with a strict dependency direction —
the web layer depends on data-access and the domain; the domain depends on nothing.

- `src/MyRestaurant.Domain` — pure domain logic: the order event model and its fold/validation, the
  join-token and Argon2 PHC primitives, the sign-in audit and obligations-pipeline decisions,
  identifiers, clock, and live-update contracts. No I/O.
- `src/MyRestaurant.DataAccess` — Dapper + Npgsql, the DbUp migration runner, the embedded SQL
  schema (`Migrations/0001_initial_schema.sql`: 22 tables, 5 views, the `citext` extension), and the
  Identity stores (person, roles read side, TOTP secret encrypted at rest, recovery codes, the
  append-only security-event log). Entity Framework is deliberately not used anywhere.
- `src/MyRestaurant.WebApplication` — the composition root, configuration binding and fail-fast
  validation, OpenTelemetry wiring, the in-process live-update broadcaster, cookie authentication
  with the auditing sign-in manager, the §3.5 obligations middleware, the account pages (static SSR),
  and the Blazor shell.
- `tests/` — pure domain tests, Testcontainers integration tests for migrations, the Identity stores
  and every reader/mutation over real PostgreSQL, web-layer configuration/wiring/enforcement tests,
  and the end-to-end scenario matrix with its Playwright harness (see *End-to-end scenarios*).
- `.github/workflows/` — the CI and release pipelines (see *Continuous integration*).

## Prerequisites

- The .NET SDK pinned in `global.json` — `10.0.100` with `rollForward: latestMinor`, so any 10.0
  feature band satisfies it.
- A container engine — rootless **Podman** is the primary target; Docker works too.
- For integration tests, the container engine's API socket must be reachable (see *Testing*). For
  the end-to-end scenarios, a Chromium build as well (see *End-to-end scenarios*).

## Quick start

Host-dev with hot reload (database in a container, web app on the host):

```bash
./run.sh
```

This starts PostgreSQL in a container, exports sensible dev defaults, ensures the ASP.NET Core dev
certificate, and runs `dotnet watch`. The app comes up at `https://localhost:8443`.

Boot once, verify health, and exit (the end-of-sweep / CI mode):

```bash
./run.sh --smoke
```

Full containerized dev stack (adds Caddy for TLS):

```bash
./run.sh --containers-only
# equivalently: podman-compose --profile dev up --build
```

Then trust Caddy's local CA on first use and open `https://localhost:8443`.

`run.sh` never opens tunnels or prints public URLs — that is a separate, demo-only step (see
*Deployment* below and `docs/OPERATIONS.md` §10).

## Configuration

All configuration is environment-only. Copy `.env.example` to `.env` and adjust; the file documents
every variable and its default. The application validates security-relevant settings at startup and
refuses to start on a bad value (non-https origin, Argon2 below the floor, an unresolvable time zone,
a missing connection string, and so on).

## Testing

```bash
dotnet test                                             # everything
dotnet test tests/MyRestaurant.Domain.Tests             # pure, fast, no services
dotnet test tests/MyRestaurant.WebApplication.Tests     # config binding + identity wiring/enforcement
dotnet test tests/MyRestaurant.DataAccess.Tests         # needs a reachable container engine
```

The domain and web-layer tests need no services. The data-access tests spin up a real PostgreSQL 17
container via Testcontainers; if no container engine is reachable they skip rather than fail.

**Rootless Podman (the canonical engine):** Testcontainers talks to the engine's API socket, not the
`podman` CLI, so on a fresh Fedora/Podman machine the integration tests skip with a Docker-flavoured
"endpoint unavailable" message even though `run.sh` works. Activate the user socket once:

```bash
systemctl --user enable --now podman.socket
```

The test suite then discovers `unix://$XDG_RUNTIME_DIR/podman/podman.sock` automatically (and
disables Ryuk, which is unreliable rootless — every fixture disposes its own container). Explicit
configuration still wins: `DOCKER_HOST` or `~/.testcontainers.properties`, if set, are respected.

## End-to-end scenarios

The §16.3 matrix lives in `tests/MyRestaurant.EndToEnd.Tests`. It is **opt-in** — a plain
`dotnet test` skips it — because the first run downloads a Chromium build of roughly 150 MB. Run it
with either of:

```bash
MYRESTAURANT_E2E=1 dotnet test tests/MyRestaurant.EndToEnd.Tests
scripts/ci_local.sh --with-e2e
```

Each scenario gets its own stack: a fresh database on a shared PostgreSQL 17 container, a fresh
Data Protection key directory, the **built** web application as a child process on a free loopback
port, and a browser context with a CDP WebAuthn virtual authenticator. Nothing is shared between
scenarios, so they can run in any order — which matters, because scenario 1 needs a database with no
administrator and scenario 13 needs one with an administrator who has both a passkey and TOTP.

The app is served at `http://localhost:{port}` while `RESTAURANT_PUBLIC_ORIGIN` says
`https://localhost:{port}`. The mismatch is deliberate: §13 refuses to start on a non-https origin,
and Chromium treats `localhost` as a secure context regardless of scheme, so WebAuthn ceremonies run
and the `Secure` authentication cookie is accepted. Only the host is ever compared.

On a minimal Linux host Chromium's shared libraries may be missing. Install them once — this needs
root, which is why the harness never attempts it:

```bash
pwsh tests/MyRestaurant.EndToEnd.Tests/bin/Debug/net10.0/playwright.ps1 install --with-deps chromium
```

Every unavailability is a skip with the fix in its message: not opted in, no container engine, no
browser, no build output. A missing tool is not a broken product.

## Continuous integration

Every push and pull request against `main` runs four gates (`.github/workflows/ci.yml`):

| Gate | What it proves |
| --- | --- |
| `shell-scripts` | every tracked `*.sh` parses under `bash -n` and passes shellcheck |
| `build-and-test` | a Release build with **warnings escalated to errors**, then all ~934 facts — including the data-access integration tests, which run here rather than skipping, because a runner always has a container socket |
| `boot-smoke` | the production `Containerfile` builds, and the resulting image boots against a real PostgreSQL until `/healthz/ready` answers 200 |
| `end-to-end` | the §16.3 scenarios in Chromium against the built application, with `MYRESTAURANT_E2E=1` |

That third gate is the one worth understanding. `/healthz/ready` returns 200 only once DbUp has
applied every migration and the composition root has resolved, so it catches the class of failure no
unit test sees: a missing DI registration, a migration that conflicts against a genuinely empty
database, a configuration default the validator rejects. Those do not break a build — they break a
deployment.

Run the same gates locally before pushing:

```bash
scripts/ci_local.sh                # shell lint, restore, strict build, full suite
scripts/ci_local.sh --with-e2e     # ...and the §16.3 scenarios in a real browser
scripts/ci_local.sh --with-smoke   # ...and boot once against the dev database
scripts/ci_local.sh --with-all     # both of the optional gates
```

Note that `dotnet build` on a workstation is deliberately *more* forgiving than CI:
`TreatWarningsAsErrors` is switched on only when `ContinuousIntegrationBuild=true`
(`Directory.Build.props`), so a fresh clone on a newer SDK still builds through analyzer drift.
`scripts/ci_local.sh` passes that property, which is the whole reason it exists.

Pushing a `v*` tag runs the same gates and then publishes `ghcr.io/kusl/myrestaurant` at
`:<version>`, `:<major>.<minor>` and `:sha-<commit>` (`.github/workflows/release.yml`). Deploying
from the registry instead of building on the box is `docs/OPERATIONS.md` §14.

## Deployment

The stack is defined in `compose.yaml` with two profiles:

- default (`up`) — `postgres` + `web`, with the web port published on loopback only (headless).
- `--profile dev` — adds Caddy terminating TLS at `https://localhost:8443` (internal CA).
- `--profile production` — adds cloudflared running a named tunnel; TLS terminates at Cloudflare's
  edge and forwards to `web:8080`. Set `CLOUDFLARE_TUNNEL_TOKEN` and change `POSTGRES_PASSWORD` and
  `RESTAURANT_PUBLIC_ORIGIN` first.

```bash
podman-compose --profile production up -d
```

`RESTAURANT_PUBLIC_ORIGIN` is the single origin from which the WebAuthn relying-party ID and all QR
and link URLs are derived. In-house guests hairpin through the tunnel, so LAN ordering depends on WAN
health — an accepted tradeoff for this design.

For a throwaway demo over the public internet, one command does it all:

```bash
scripts/quick_tunnel.sh
```

The script brings PostgreSQL up, opens a `*.trycloudflare.com` tunnel, discovers the assigned URL,
sets `RESTAURANT_PUBLIC_ORIGIN` to it (so QR join links resolve), (re)starts the web app against
that URL, waits for it to answer, prints the URL in a banner, and then holds the tunnel in the
foreground. The URL lives exactly as long as the script runs (Ctrl+C ends the demo) — a quick
tunnel cannot "print a URL and exit", because exiting kills the URL.

**Passkeys work on the quick tunnel**, including a passkey-only account: the WebAuthn relying-party
ID is derived per request from the host the browser is on (ADR-0005), and `https://*.trycloudflare.com`
is trusted by default (`RESTAURANT_TRUSTED_ORIGIN_PATTERNS`). The one caveat, which the script prints
loudly: that hostname is random per run and on the Public Suffix List, so passkeys (and bookmarks) do
not carry across runs — register again on the next run. Use the stable named tunnel for anything that
must persist, and never bootstrap a real instance through a quick tunnel.

## Backups

`scripts/backup.sh` writes a `pg_dump -Fc` archive to `BACKUP_DIRECTORY` and prunes to
`BACKUP_RETENTION_COUNT`; schedule it at `BACKUP_SCHEDULE_TIME` with a systemd timer or cron.
`scripts/restore.sh <dump>` stops the web app, restores, and starts it again (startup migrations then
verify the schema). Back up the Data Protection keys volume alongside the database — without it,
TOTP secrets and auth cookies are unrecoverable.

## First-build checklist

The code in each milestone slice is written carefully but has not been compiled in its authoring
environment (no toolchain or package feed there). On a networked machine:

1. `dotnet restore` — resolve or adjust any package versions in `Directory.Packages.props`.
2. `dotnet build` — fix any analyzer/compiler findings.
3. `dotnet test` — domain and web-layer tests need no services; the data-access tests need the
   container engine socket (see *Testing*).
4. `./run.sh --smoke` — confirm migrations apply and `/healthz/ready` returns 200.

Or, in one command that mirrors what CI will say about the same tree:

```bash
scripts/ci_local.sh --with-all
```

## Known caveats and deliberate decisions

- **Warnings are errors in CI, not on your workstation.** `TreatWarningsAsErrors` is switched on only
  under `ContinuousIntegrationBuild=true` (`Directory.Build.props`), so a fresh clone on a newer SDK
  builds through analyzer drift while a pull request does not. `scripts/ci_local.sh` asks the strict
  question locally.
- **The end-to-end scenarios are opt-in.** `MYRESTAURANT_E2E=1` is the switch. Without it they skip,
  and a green `dotnet test` says nothing about them — read the `end-to-end` CI job instead.
- **Not `InvariantGlobalization`.** The app resolves `RESTAURANT_TIME_ZONE` through `TimeZoneInfo`,
  so globalization stays on and the runtime image installs `tzdata`.
- **DbUp logging** uses `LogToConsole()` rather than a custom `IUpgradeLog`, whose interface shape
  varies across DbUp versions. If a DbUp version differs from what is pinned, `SchemaMigrationRunner`
  is the most likely place a build break appears — adjust the builder calls there.
- **Npgsql OpenTelemetry.** Tracing is enabled with `AddNpgsql()`; if the extension's namespace has
  moved in the pinned Npgsql.OpenTelemetry version, adjust the using directives in `Program.cs`.
- **Forwarded-headers trust.** `Program.cs` clears `KnownIPNetworks`/`KnownProxies` so `X-Forwarded-*`
  from the proxy is honoured. This is safe only because the app is reached exclusively through a
  trusted proxy (Cloudflare tunnel in production, Caddy in dev) and never exposed directly.
- **Rootless volume ownership.** The data-protection volume is mounted `:U` in compose so Podman
  chowns it to the container user. On Docker, drop the `:U` suffix if it objects.
- **Account pages are static SSR by design.** Sign-in and the forced-change pages write cookies on
  the response, which a Blazor circuit cannot do; do not convert them to interactive components.
- **The first account is created at `/setup`, once.** On a fresh database only that route is
  reachable; from the moment an administrator exists it is 404 forever. Do the bootstrap on the
  production origin, never through a quick tunnel — the passkey binds to the origin it was
  registered on (`docs/OPERATIONS.md` §3).
- **Container images are `linux/amd64` only.** The release pipeline does not emulate arm64: the
  `Containerfile` runs `dotnet publish` in its build stage, and doing that under QEMU is slow enough
  to risk a timeout. An arm64 image wants a cross-compiled publish rather than an emulated one
  (`docs/OPERATIONS.md` §14).

## Roadmap

- ✔ **M1** — skeleton: solution layout, `Containerfile`, compose dev profile, DbUp with the initial
  schema, health endpoints, OpenTelemetry wiring, `run.sh`.
- ✔ **M2** — identity & accounts: Dapper Identity stores, Argon2id with the floor guard and
  concurrency semaphore, WebAuthn passkeys, TOTP + recovery codes, lockout, the obligations pipeline,
  the `/setup` first-administrator bootstrap, roles → policies → gated areas, security events,
  account administration, and the person's own profile page.
- ✔ **M3** — tables & joining: table CRUD, per-table join secrets and rotation, display pairing and
  device auth, the `/display` surface, rotating token generation/validation with metrics, the join
  grant cookie, sittings and membership.
- ✔ **M4** — ordering: the living order and its row-level locking protocol, client staging, batch
  send with all-or-nothing validation, staff edits, fulfillment and reversal, the projection fold
  with equivalence tests, and the kitchen surface with alerts and the reminder loop. Plus the
  close-out that made `RESTAURANT_TIME_ZONE` actually true on every surface.
- ✔ **M5** — counter & administration: bills, price adjustment with reason, close & settle,
  end-of-day, the counter fallback QR, menu management with its event log, the cross-log event
  explorer, hide/unhide, and post-close corrective events.
- **M6** — hardening *(in progress)*: ✔ the CI pipeline and publish-on-tag; ✔ the Playwright
  harness and §16.3 scenarios 1, 13 and 14; still to come, the other twelve scenarios and an
  executable backup/restore drill.
