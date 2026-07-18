# MyRestaurant

A self-hosted, single-restaurant ordering system: guests order from their table on their own phones,
the kitchen and counter work from live boards, and everything runs on one small box behind a
Cloudflare tunnel. Blazor Server over PostgreSQL, no external runtime dependencies beyond the
database and the tunnel.

This repository is at **Milestone 1 (the skeleton)**. The domain, data-access, and web host are in
place; the database schema applies at startup; health checks and the app shell work. The guest,
kitchen, counter, administrator, and table-display experiences — and all of authentication — arrive
in later milestones (see *Roadmap*).

## Layout

The solution (`MyRestaurant.slnx`) is a small set of projects with a strict dependency direction —
the web layer depends on data-access and the domain; the domain depends on nothing.

- `src/MyRestaurant.Domain` — pure domain logic: the order event model and its fold/validation, the
  join-token and Argon2 PHC primitives, identifiers, clock, and live-update contracts. No I/O.
- `src/MyRestaurant.DataAccess` — Dapper + Npgsql, the DbUp migration runner, and the embedded SQL
  schema (`Migrations/0001_initial_schema.sql`: 22 tables, 5 views, the `citext` extension). Entity
  Framework is deliberately not used anywhere.
- `src/MyRestaurant.WebApplication` — the composition root, configuration binding and fail-fast
  validation, OpenTelemetry wiring, the in-process live-update broadcaster, and the Blazor shell.
- `tests/` — pure domain tests, a Testcontainers migration test, web-layer configuration tests, and
  the version-controlled end-to-end scenario matrix (skipped until Milestone 6).

## Prerequisites

- The .NET SDK pinned in `global.json` (10.0.302 or a newer 10.0 feature band).
- A container engine — rootless **Podman** is the primary target; Docker works too.
- For integration tests, the container engine must be running. For end-to-end tests (later),
  Playwright browsers as well.

## Quick start

Host-dev with hot reload (database in a container, web app on the host):

```bash
./run.sh
```

This starts PostgreSQL in a container, exports sensible dev defaults, ensures the ASP.NET Core dev
certificate, and runs `dotnet watch`. The app comes up at `https://localhost:8443`.

Full containerized dev stack (adds Caddy for TLS):

```bash
./run.sh --containers-only
# equivalently: podman-compose --profile dev up --build
```

Then trust Caddy's local CA on first use and open `https://localhost:8443`.

## Configuration

All configuration is environment-only. Copy `.env.example` to `.env` and adjust; the file documents
every variable and its default. The application validates security-relevant settings at startup and
refuses to start on a bad value (non-https origin, Argon2 below the floor, an unresolvable time zone,
a missing connection string, and so on).

## Testing

```bash
dotnet test                                             # everything
dotnet test tests/MyRestaurant.Domain.Tests             # pure, fast, no services
dotnet test tests/MyRestaurant.WebApplication.Tests     # configuration binding + validation
dotnet test tests/MyRestaurant.DataAccess.Tests         # needs a running container engine
```

The domain and web-layer tests need no services. The data-access test spins up a real PostgreSQL 17
container via Testcontainers; if no container engine is available it skips rather than fails. The
end-to-end project lists the required scenarios as skipped placeholders and is implemented at
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

For a throwaway demo, `scripts/quick_tunnel.sh` opens a `*.trycloudflare.com` tunnel. Because that
domain is random per run and on the Public Suffix List, passkeys will not persist across runs; demos
sign in with password + TOTP.

## Backups

`scripts/backup.sh` writes a `pg_dump -Fc` archive to `BACKUP_DIRECTORY` and prunes to
`BACKUP_RETENTION_COUNT`; schedule it at `BACKUP_SCHEDULE_TIME` with a systemd timer or cron.
`scripts/restore.sh <dump>` stops the web app, restores, and starts it again (startup migrations then
verify the schema). Back up the Data Protection keys volume alongside the database — without it,
TOTP secrets and auth cookies are unrecoverable.

## First-build checklist

The code in this milestone was written carefully but has not been compiled in this environment
(no toolchain or package feed here). On a networked machine:

1. `dotnet restore` — resolve or adjust any package versions in `Directory.Packages.props`.
2. `dotnet build` — fix any analyzer/compiler findings.
3. `dotnet test` — domain and web-layer tests need no services; the data-access test needs a
   container engine.
4. `./run.sh` — confirm migrations apply and `/healthz/ready` returns 200.

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
- **Forwarded-headers trust.** `Program.cs` clears `KnownNetworks`/`KnownProxies` so `X-Forwarded-*`
  from the proxy is honoured. This is safe only because the app is reached exclusively through a
  trusted proxy (Cloudflare tunnel in production, Caddy in dev) and never exposed directly.
- **Rootless volume ownership.** The data-protection volume is mounted `:U` in compose so Podman
  chowns it to the container user. On Docker, drop the `:U` suffix if it objects.

## Roadmap

- **M2** — identity & accounts: custom Dapper Identity stores, Argon2id hashing with the floor guard
  and concurrency semaphore, passkeys, TOTP + recovery codes, lockout, the obligations middleware,
  `/setup` first-admin bootstrap, roles/policies, and security-event logging.
- **M3–M5** — guest ordering, kitchen and counter boards, table displays, administration.
- **M6** — the Playwright end-to-end matrix and CI publishing.
