# MyRestaurant

A self-hosted ordering system for a single small restaurant. Guests order from
their table by scanning a QR code; kitchen and counter staff work the order from
their own surfaces; an administrator manages the menu, staff, and settlement.
Everything runs on one host under rootless Podman, fronted in production by a
Cloudflare named tunnel.

Built on .NET 10 (ASP.NET Core Blazor Server) with PostgreSQL and Dapper. Orders
are an append-only event stream folded into current state, so every change is
auditable. Authentication uses ASP.NET Core Identity over custom Dapper stores,
Argon2id password hashing, WebAuthn passkeys, and TOTP with recovery codes.

> **Status:** under active, staged construction. See
> [`BUILD_PROGRESS.md`](BUILD_PROGRESS.md) for exactly what is implemented, what
> is stubbed, and the known caveats (including that package versions in
> `Directory.Packages.props` still need a `dotnet restore` on a networked
> machine to confirm).

## Requirements

- .NET SDK 10 (see `global.json`)
- Rootless Podman with `podman-compose` (or `podman compose`); Docker Compose
  also works
- A container runtime is required to run the DataAccess integration tests

## Development

The scripts may arrive without their executable bit set; set it once:

```bash
chmod +x run.sh scripts/*.sh
```

Then start the stack. The default runs PostgreSQL in a container and the web app
on the host with hot reload:

```bash
./run.sh
```

This creates `.env` from `.env.example` on first run and serves the app at
`https://localhost:8443` (via a Kestrel dev certificate). Migrations run
automatically at startup.

To run everything in containers instead, including Caddy for local TLS:

```bash
./run.sh --containers-only
```

## Production

Production uses a **named** Cloudflare tunnel on your own domain — not a quick
tunnel — so the origin is stable (passkeys are bound to it).

1. Create a named tunnel in the Cloudflare Zero Trust dashboard and add a public
   hostname (e.g. `order.yourrestaurant.com`) routing to `http://web:8080`.
2. Copy `.env.example` to `.env` and set at least: `POSTGRES_PASSWORD`,
   `RESTAURANT_PUBLIC_ORIGIN` (your public https URL), `RESTAURANT_NAME`,
   `RESTAURANT_TIME_ZONE`, and `CLOUDFLARE_TUNNEL_TOKEN`.
3. Bring up the production profile (web + postgres + cloudflared; no Caddy):

```bash
podman-compose --profile production up -d
```

### First-run bootstrap

On the real production origin, visit `/setup` once to create the first
administrator. Bootstrapping is only available while no administrator exists and
must be done on the stable origin — never over a quick tunnel.

### Quick demo tunnel (not for real use)

To show the app to someone briefly, `scripts/quick_tunnel.sh` exposes it on a
random `*.trycloudflare.com` URL. This is demo-only: the hostname changes every
run and passkeys will not survive that, so never bootstrap a real instance this
way.

```bash
./scripts/quick_tunnel.sh start
./scripts/quick_tunnel.sh status
./scripts/quick_tunnel.sh stop
```

## Health checks

- `GET /healthz/live` — process is up
- `GET /healthz/ready` — database reachable and schema fully migrated (returns
  503 otherwise)

## Configuration

All configuration is via environment variables, documented in `.env.example`
(names, defaults, and the startup validation rules). An instance that starts is
an instance that passed validation.

## Tests

```bash
dotnet test
```

- **Domain tests** are pure and fast (no database): the order-event fold, the
  §6.5 mutation validator, the join-token vectors, the Argon2 PHC string, the
  obligations pipeline, and the secret generators.
- **DataAccess tests** spin up PostgreSQL via Testcontainers and run the real
  migrations; they need a container runtime.
- **WebApplication tests** cover options binding and fail-fast validation.
- **End-to-end tests** (Playwright) are scaffolded; the §16.3 scenarios are
  filled in during hardening.

## Backups

`scripts/backup.sh` writes a `pg_dump` custom-format dump and a tarball of the
Data Protection keys to `BACKUP_DIRECTORY`, pruning to the newest
`BACKUP_RETENTION_COUNT`. Restore with `scripts/restore.sh <dump> [keys.tar.gz]`.
Run the restore drill periodically so the recovery path stays known-good.

## Project layout

```
src/
  MyRestaurant.Domain           pure domain: orders, security, time (BCL only)
  MyRestaurant.DataAccess       Dapper, Npgsql, DbUp migrations + schema
  MyRestaurant.WebApplication   Blazor Server host, options, observability
tests/
  MyRestaurant.Domain.Tests
  MyRestaurant.DataAccess.Tests
  MyRestaurant.WebApplication.Tests
  MyRestaurant.EndToEnd.Tests
scripts/                        backup.sh, restore.sh, quick_tunnel.sh
compose.yaml, Containerfile, Caddyfile, run.sh
```

Dependency direction is enforced by project references only:
`WebApplication → DataAccess → Domain`.

## License

AGPL-3.0-only. See [`LICENSE`](LICENSE).
