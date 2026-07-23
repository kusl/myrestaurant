# MyRestaurant

A self-hosted, single-restaurant ordering system: guests order from their table on their own phones,
the kitchen and counter work from live boards, and everything runs on one small box behind a
Cloudflare tunnel. Blazor Server over PostgreSQL, no external runtime dependencies beyond the
database and the tunnel.

This repository has completed **Milestone 1 (the skeleton)** and is partway through **Milestone 2
(identity & accounts)**: password sign-in with the TOTP/recovery-code challenge, lockout, sign-out,
the forced-password-change pipeline, and the first authorization-gated area all work. Passkeys, TOTP
enrollment, the `/setup` bootstrap, and account administration are the remaining M2 slices; the
guest, kitchen, counter, administrator, and table-display experiences arrive in later milestones
(see *Roadmap* and `docs/BUILD_PROGRESS.md`).

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
- `tests/` — pure domain tests, Testcontainers integration tests for migrations and the Identity
  stores, web-layer configuration/wiring/enforcement tests, and the version-controlled end-to-end
  scenario matrix (skipped until Milestone 6).

## Prerequisites

- The .NET SDK pinned in `global.json` (10.0.302 or a newer 10.0 feature band).
- A container engine — rootless **Podman** is the primary target; Docker works too.
- For integration tests, the container engine's API socket must be reachable (see *Testing*). For
  end-to-end tests (later), Playwright browsers as well.

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

The end-to-end project lists the required scenarios as skipped placeholders and is implemented at
Milestone 6.

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

## Known caveats and deliberate decisions

- **Warnings are not errors yet.** `TreatWarningsAsErrors=false` lets a fresh clone build through
  analyzer drift on a newer SDK; tighten to `true` once the first build is green.
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
- **No registration or `/setup` page yet.** Guests register at the moment of joining a table (M3)
  and staff are created by an administrator (later M2 slice), so until the bootstrap slice lands
  there is no in-app way to create the first account.

## Roadmap

- **M2** — identity & accounts *(in progress)*: ✔ custom Dapper Identity stores, ✔ Argon2id hashing
  with the floor guard and concurrency semaphore, ✔ lockout, ✔ password sign-in with the
  TOTP/recovery challenge, ✔ the obligations middleware with forced password change, ✔ roles →
  policies → the first gated area, ✔ security-event logging; still to come: TOTP enrollment,
  passkeys, `/setup` first-admin bootstrap, and account administration (which is what starts
  setting the obligation flags).
- **M3–M5** — guest ordering, kitchen and counter boards, table displays, administration.
- **M6** — the Playwright end-to-end matrix and CI publishing.
