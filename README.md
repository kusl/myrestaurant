# MyRestaurant

[![ci](https://github.com/kusl/myrestaurant/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/kusl/myrestaurant/actions/workflows/ci.yml)
[![license: AGPL-3.0-only](https://img.shields.io/badge/license-AGPL--3.0--only-blue.svg)](LICENSE)

A self-hosted, single-restaurant ordering system: guests order from their table on their own phones,
the kitchen and counter work from live boards, and everything runs on one small box behind a
Cloudflare tunnel. Blazor Server over PostgreSQL, no external runtime dependencies beyond the
database and the tunnel.

**All six milestones are complete.** Identity and accounts, table administration with rotating join
QR codes, paired table displays, the living order and its locking protocol, the kitchen and counter
boards, billing and settlement, menu management, the cross-log event explorer — and then the
hardening milestone: continuous integration, all sixteen §16.3 end-to-end scenarios running against a
real browser, and a backup/restore drill that CI rehearses on every push rather than a runbook nobody
has executed.

Two product gaps surfaced during that milestone and were closed rather than noted. Writing the
end-to-end scenarios found that guests had nowhere to self-register despite the requirements having
mandated it since rev 2, so `/register` exists. Executing the restore procedure for the first time
found that it could not have completed — and that nothing had ever backed up the Data Protection key
ring, so every backup ever taken would have restored the accounts and none of their enrolled
authenticators. Both are in `docs/DOCUMENTATION_REVIEW.md` as F-37 and F-38.

**M7 is open, and it is the first milestone driven by a user.** The application was shown to somebody,
and what came back was one enhancement request — the menu needs sections and every item needs a
description — and one defect: on a phone, the Manage button on the administration tables page sat off the
right-hand edge of the screen. The defect is fixed and its rule is written down (`§11.12`, F-59); the
enhancement is decided (ADR-0014) and staged. `docs/MENU_AND_HANDHELD_PLAN.md` is the plan.

See *Roadmap*, `docs/MENU_AND_HANDHELD_PLAN.md` and `docs/BUILD_PROGRESS.md`.

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
  with the auditing sign-in manager, the §3.5 obligations middleware, the account pages including
  anonymous guest registration (all static SSR, because they write cookies on the response), and the
  Blazor shell.
- `tests/` — pure domain tests, Testcontainers integration tests for migrations, the Identity stores
  and every reader/mutation over real PostgreSQL, web-layer configuration/wiring/enforcement tests,
  and the end-to-end scenario matrix with its Playwright harness (see *End-to-end scenarios*).
- `scripts/` — the operational scripts: `check_tree.sh` (repository hygiene, the first CI gate),
  `check_compose_substitution.sh` (does this host's compose engine apply the defaults in
  `compose.yaml`? — a preflight, not a CI gate, because its subject is the machine),
  `check_repository.sh` (the governance surface, and an advisory read of the published repository's
  settings), `ci_local.sh` (every CI gate, locally), `backup.sh` / `restore.sh` / `restore_drill.sh` (§15
  recovery sets and the rehearsal CI runs on every push), `quick_tunnel.sh` (a demo origin, held in
  the foreground), and `dev_instance.sh` (the same origin, detached — for a spare machine with no
  .NET SDK that serves testers for days).
- `docs/` — `REQUIREMENTS.md` (intent), `TECHNICAL_SPECIFICATION.md` (the normative mechanism),
  `DOCUMENTATION_REVIEW.md` (the defect ledger, F-01 to F-59), `OPERATIONS.md` (the runbooks),
  `BUILD_PROGRESS.md` (what was built, slice by slice, and what was not verified),
  `MENU_AND_HANDHELD_PLAN.md` (M7's staged plan), and `adr/` (fourteen decision records).
- `.github/workflows/` — the CI and release pipelines (see *Continuous integration*).

## Prerequisites

- The .NET SDK pinned in `global.json` — `10.0.100` with `rollForward: latestMinor`, so any 10.0
  feature band satisfies it. Needed to build, test, or run on the host; **not** needed to run the
  containers, since the Containerfile builds inside the SDK image (see `scripts/dev_instance.sh`).
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

All configuration is environment-only. Copy `.env.example` to `.env` yourself and adjust — **no script
in this tree writes that file** (**F-54**; a runbook step used to say otherwise). It documents every
variable and its default. The application validates security-relevant settings at startup and
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

All sixteen are implemented:

| # | What it proves |
| --- | --- |
| 1 | the `/setup` bootstrap, with a real WebAuthn attestation and a real TOTP code, then `/setup` is 404 |
| 2 | a display pairs and its QR advances across a rotation boundary |
| 3 | a guest scans, self-registers with a passkey, and joins *after* the code they scanned has expired |
| 4 | a guest stages two adds and a note, sends, and the kitchen gets exactly one alert with both lines pending |
| 5 | a second guest joins on a fresh token and sees the first guest's order live; the roster updates both ways |
| 6 | the kitchen marks one line away and the guest's own screen re-badges it |
| 7 | removing a fulfilled line rejects the whole batch with a per-operation reason; removing a pending one succeeds |
| 8 | a send left unfulfilled past the threshold produces exactly one reminder, not a stream |
| 9 | the counter adjusts a price with a reason and the guest sees old → new with that reason |
| 10 | the counter closes with a pending-line warning; the table flips to settled read-only and the totals match |
| 11 | a guest hides a closed order, staff and admin views are unchanged, and an administrator finds and unhides it |
| 12 | an admin reset forces password change then TOTP re-enrollment — on the passkey path too |
| 13 | a passkey sign-in of a TOTP-enrolled person is not challenged for a code |
| 14 | the join-token window arithmetic as a guest experiences it |
| 15 | rotating a join secret kills every outstanding QR while the paired display recovers by itself |
| 16 | the four administration indexes at 375×667: nothing scrolls sideways, every row's action is on the screen, every control is 44px tall |

Scenario 3 is the one that earns its runtime. §16.3 words it *"registers with passkey (slowly — grant
outlives token)"*, and that parenthetical is the entire reason the §4.4 join grant exists. The
instance runs at the §13 floor of a ten-second rotation, so the scanned token is provably dead about
twenty seconds later; the scenario waits it out, proves the death by re-scanning in a third browser
context with no grant cookie to carry it past a refusal, and only then joins on the grant.

Scenarios 4 and 6 are the first to watch a §9 broadcast cross between two circuits — a guest's phone
and the kitchen board, in two browser contexts, reacting to each other's commits. The kitchen board
is opened *before* anything is sent, because `KitchenBoard.razor` subscribes in
`OnAfterRender(firstRender)` and a board opened afterwards would render the queue perfectly well
while having heard nothing.

A scenario that needs more than one principal at once opens more than one browser context — an
administrator, the tablet on the table, a guest with a phone. For the display device that is not
hygiene but necessity: the §4.2 device credential is ignored on any request the Identity cookie has
already authenticated, so a screen paired inside the administrator's browser *is* the administrator
and never renders a join code at all.

The rotation window is per instance rather than global, because the scenarios want opposite things
from it: scenario 14 needs one long enough that "the previous window" cannot roll over mid-assertion,
while 2 and 15 need one short enough that a boundary is crossed inside a test's patience. §4.3 accepts
the current and previous window whatever their width, so nothing an assertion depends on moves with it.

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

Every push and pull request against `main` runs six gates (`.github/workflows/ci.yml`):

| Gate | What it proves |
| --- | --- |
| `tree` | the checkout is machine-readable at all: no context-dump separator lines, no whitespace-only lines, LF endings with a final newline, every MSBuild and solution file well-formed XML, every YAML file parsing (`scripts/check_tree.sh`). Asserted over authored text only — generated dumps under `docs/llm/` and binary files are skipped, and the counts are reported |
| `governance` | a security policy exists, `README`/`CONTRIBUTING`/`SECURITY` each point at the others, and **no tracked file asserts a repository or package setting** (`scripts/check_repository.sh`). That half is blocking; a second, advisory half reads the GitHub API and reports the settings themselves, because a fork's settings are the fork's business |
| `shell-scripts` | every tracked `*.sh` parses under `bash -n` and passes shellcheck |
| `build-and-test` | a Release build with **warnings escalated to errors**, then all ~970 facts — including the data-access integration tests, which run here rather than skipping, because a runner always has a container socket |
| `boot-smoke` | the production `Containerfile` builds — which now also means its build context is exactly the allow-list `.dockerignore` describes, asserted by the build itself — the image boots against a real PostgreSQL until `/healthz/ready` answers 200, `/source` names the commit it was built from, and then that instance is backed up and the backup is put through a full restore drill |
| `end-to-end` | all sixteen §16.3 scenarios in Chromium against the built application, with `MYRESTAURANT_E2E=1` |

That third gate is the one worth understanding. `/healthz/ready` returns 200 only once DbUp has
applied every migration and the composition root has resolved, so it catches the class of failure no
unit test sees: a missing DI registration, a migration that conflicts against a genuinely empty
database, a configuration default the validator rejects. Those do not break a build — they break a
deployment. The same job then proves the instance's data comes back: `scripts/backup.sh` takes a real
recovery set off it, and `scripts/restore_drill.sh` restores that set into a scratch container it
creates and destroys, gating the archive, the restore, every relation the migrations declare, DbUp's
journal, all five projection views, and the Data Protection key ring.

The first gate is the cheapest and the newest. It exists because a stray line appended to
`Directory.Build.props` once failed every MSBuild verb in the repository at once — `clean`, `restore`,
`build`, `test` and the container build — with a message (`Data at the root level is invalid`) that
pointed at MSBuild rather than at the file. Twenty other tracked files had the same line and said
nothing, because in YAML, in a Containerfile, in `.env` and in Markdown it is a comment. It runs in
about two seconds and needs no SDK, so it is worth running by hand after applying anything to this
tree:

```bash
bash scripts/check_tree.sh
```

Run the same gates locally before pushing:

```bash
scripts/ci_local.sh                # tree, governance, shell lint, restore, strict build, full suite
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

Two rules are correctness requirements on rootless Podman rather than style, and both are stated in
`compose.yaml`'s own header: **every image reference is fully qualified** (`docker.io/library/postgres:17-alpine`,
not `postgres:17-alpine`, because a stock Debian resolves no short names — F-51), and **no `depends_on`
is gated on a health condition** (`service_started` only; Debian's podman-compose waits on a health
condition forever and silently — F-53). Waiting for the database is the application's job, and it has
retried its first connection thirty times since M1.

The first rule is not about `compose.yaml`. Reading it that way was a finding of its own (**F-60**): it
holds at every image reference in the repository, and the four places it was missing were the two
Testcontainers fixtures and CI's two — where a short name does not fail the suite, because both fixtures
turn a container that will not start into a *skip*, so the canonical host answers with a green run in
which no integration test and no end-to-end scenario executed. A reference must also sit somewhere that
can be read: a YAML `image:` key, a `Containerfile` `FROM`, or a value assigned to a name ending in
`_IMAGE` or `Image`. Two of them did not, and moving those into named constants is what put them back
inside the audit rather than a tidy-up.

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
foreground. The URL lives exactly as long as *that script* runs, because it owns `cloudflared` as a
foreground child — Ctrl+C ends the demo, once (**F-61**: it used to say so twice, because one handler
was registered on both the signal and on exit).

For a test instance that has to outlive the terminal — a spare machine on the LAN, reached over SSH,
serving a build that testers will use for days, **with no .NET SDK installed on it** — there is a
second script that exits and leaves the instance running:

```bash
scripts/dev_instance.sh            # builds, opens the tunnel, prints the URL, and exits
scripts/dev_instance.sh url        # that URL again, on stdout
scripts/dev_instance.sh status     # what is running, with exit codes if it is not
scripts/dev_instance.sh logs       # the application's log (postgres and tunnel are arguments)
scripts/dev_instance.sh diagnose   # both logs at once, with a key for reading them
scripts/dev_instance.sh down       # the only thing that closes the tunnel; keeps the volumes
scripts/dev_instance.sh reset      # down, and destroy the volumes — the database and the key ring
```

It runs `cloudflared` as a detached container rather than a child process, which is what lets it
return to the prompt; builds the image *before* announcing a URL, so the hostname is not published
minutes ahead of anything that answers it; and **reuses the hostname on a re-run**, because passkeys
are bound to it and a fresh random subdomain would discard every one. `--new-url` is how you ask for
a new hostname on purpose. See OPERATIONS §10a.

Every compose command it runs is under a deadline, and that is not belt-and-braces — the first
version of this script hung forever on its first real run (**F-53**). Not in the script: inside
`podman-compose up -d`, which on Debian's version starts every container and *then* waits on each
`depends_on` health condition in a loop that prints nothing. The stack was up and serving the whole
time. `compose.yaml` no longer asks for a health condition, which removes the cause, and the
deadline is there because a script whose job is to hand the terminal back must not contain a call
that can keep it.

**The third run found the cause of both failures, and it was not in this repository (F-57).** Every
value in `compose.yaml` is written `${NAME:-default}`, and Debian trixie's podman-compose does not
apply the part after `:-` — so the placeholder text itself reached the containers. The application
validated five of them and exited 1; `POSTGRES_USER` reached `initdb` as punctuation, which erased the
data directory and crash-looped. Eleven more were wrong in silence. `scripts/check_compose_substitution.sh`
asks the engine that question directly, all three helpers that start the stack run it first and refuse
rather than work, and `cp .env.example .env` — which now assigns every variable the stack interpolates,
empty where empty is the value — is the remediation.

**The second run of that script found the opposite defect (F-55), and it is why `up` now has an exit
code worth reading.** It did not hang; it waited out a five-minute readiness deadline against a
container that had already exited 1, printed the public URL banner over a dead application, and
returned 0 — and never printed either container's log, which is where the reason had been from the
first ten seconds. So every wait is now bounded *and* watched: it ends as soon as the container it
is waiting on is crash-looping or will not stay started, which turns a seven-minute non-answer into
a seven-second diagnosis. On failure you get a `NOT SERVING` banner, each container's state, exit
code and restart count, both log tails, and a key mapping the symptoms this program actually
produces to their causes; `up` exits non-zero and leaves everything running so you can read it. And
because a PostgreSQL data directory that will not start survives both `down` and
`podman system prune -a` — neither removes volumes — there is now `reset`, which does, after telling
you that it destroys the database and the Data Protection key ring. See OPERATIONS §10a.

**Passkeys work on the quick tunnel**, including a passkey-only account: the WebAuthn relying-party
ID is derived per request from the host the browser is on (ADR-0005), and `https://*.trycloudflare.com`
is trusted by default (`RESTAURANT_TRUSTED_ORIGIN_PATTERNS`). The one caveat, which the script prints
loudly: that hostname is random per run and on the Public Suffix List, so passkeys (and bookmarks) do
not carry across runs — register again on the next run. Use the stable named tunnel for anything that
must persist, and never bootstrap a real instance through a quick tunnel.

## Backups

**A recovery set is two files, not one.** `scripts/backup.sh` writes a `pg_dump -Fc` archive *and* a
tar of the Data Protection key ring, sharing one timestamp, to `BACKUP_DIRECTORY`, then prunes whole
sets to `BACKUP_RETENTION_COUNT`. Schedule it at `BACKUP_SCHEDULE_TIME` with a systemd timer or cron.
Without the key ring a restore brings back every account and **no enrolled authenticator** — the TOTP
secrets are encrypted with it.

`scripts/restore.sh <dump>` verifies the archive, stops the web app, restores the database, puts the
key ring back, and restarts the app from an `EXIT` trap so it comes back on every path out of the
script including the failing ones. `scripts/restore_drill.sh` rehearses the whole thing
non-destructively against a scratch container it creates and destroys — it never touches the live
database. CI runs the drill on every push.

All of this is `docs/OPERATIONS.md` §6. It reads the way it does because the procedure was executed
for the first time in Slice 16 and four defects fell out of the attempt (F-38); the shape of those
defects is the argument for why a recovery procedure nobody runs is a hypothesis.

## Knowing what you are running, and where the source is

Every page's footer carries one quiet line: the product name, the running version, and a link to
`/source`. That page is anonymous and reports the version, the exact source revision the binary was
built from, and the licence — so the answer to *"which build is on that box?"* comes from inside the
process rather than from whoever deployed it, and survives a mislabelled image or a tag that has since
moved. The same string is OpenTelemetry's `service.version`, so a latency change after a deploy is
attributable to a build. A build produced without a revision stamp says **"Not recorded"** rather than
guessing, which is itself a signal: a production instance saying that did not come from the release
pipeline.

CI proves it: `boot-smoke` fetches `/source` with no cookie and fails unless the response names the
commit the image was built from. The stamp travels through a build argument, an MSBuild property, an
assembly attribute, a parse and a component, and every one of those links fails *silently* — the page
still renders, it just renders "Not recorded".

**If you fork this and run it as a network service,** AGPL-3.0-only §13 asks you to offer your users
the corresponding source of *your* version. The mechanism is already here and takes one variable:

```bash
RESTAURANT_SOURCE_URL=https://git.example.com/you/myrestaurant
```

Publish your modified source there and the footer link already offers it. Stamp your builds with
`--build-arg SOURCE_REVISION=$(git rev-parse HEAD)` so the offer can name a revision. `http` is
accepted for this one setting — a Gitea on your LAN discharges the obligation perfectly well. There is
deliberately no setting that removes the offer; if you want it gone, you have the source and the
freedom to remove it, which is the arrangement. Details in `docs/OPERATIONS.md` §15, and none of it is
legal advice — `LICENSE` is the text that governs.

## Reporting a security problem

**Use the private channel, not the issue tracker:** the Security tab → **Report a vulnerability**.
`SECURITY.md` is the policy — what is in scope, what is out, what happens next, and the honest
timelines of a project one person maintains. There is no bounty, which it says in its second paragraph
rather than leaving you to find out.

Read `docs/TECHNICAL_SPECIFICATION.md` **§17** first. It is the accepted-risks register: the ≤120 s join
token replay window, the ruled absence of a `/register` rate limit, guests seeing their table-mates'
orders, and half a dozen others are decisions that were argued and written down, each with what bounds
it. An argument that one of them should be re-ruled is welcome. Presenting one as news is an evening
nobody gets back.

`CONTRIBUTING.md` refuses outside contributions; a vulnerability report is the one exception, and the
reason is that refusing a feature costs the person who wanted it — who has the source and the freedom to
build it — while refusing a report costs an operator's guests, who never chose this software and have no
fork to run.

## First-build checklist

The code in each milestone slice is written carefully but has not been compiled in its authoring
environment (no toolchain or package feed there). On a networked machine:

1. `bash scripts/check_tree.sh` — seconds, no SDK; confirms the tree arrived intact.
2. `bash scripts/check_repository.sh` — seconds; the governance surface, and an advisory read of the
   repository's own settings.
3. `dotnet restore` — resolve or adjust any package versions in `Directory.Packages.props`.
4. `dotnet build` — fix any analyzer/compiler findings.
5. `dotnet test` — domain and web-layer tests need no services; the data-access tests need the
   container engine socket (see *Testing*).
6. `./run.sh --smoke` — confirm migrations apply and `/healthz/ready` returns 200.

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
- **`/register` is anonymous and not rate-limited.** It is the second anonymous surface that writes
  a row, and the more consequential one, since a `person` outlives the request. What bounds it today
  is shape rather than policy: two requests behind an antiforgery token and a Data-Protection-
  protected ticket cookie (so not a scriptable single POST), a 256-character cap on the password so
  an anonymous caller cannot ask for unbounded Argon2id work, and §3.2's process-wide hashing
  semaphore. `/display/pair` is the only limited endpoint (§4.2, 5/min/IP), and adding a second
  policy naively would hijack its rejection message — see `docs/TECHNICAL_SPECIFICATION.md` §17.
- **The source offer has no off switch, and the version is not hidden.** A version number on every
  page is sometimes objected to as telling an attacker which advisories to try. That objection does
  not survive contact with this project: the source is public, the tags are public, the image digests
  are public, and concealing the number here would protect nothing while breaking an offer that is
  supposed to name the version it offers the source of.
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
- ✔ **M6** — hardening: the CI pipeline and publish-on-tag; the Playwright harness and all sixteen
  §16.3 scenarios against a real browser, the last of which measures the administration surfaces on a
  375px handset (F-59, F-62); guest self-registration at `/register` (F-37); the
  backup/restore drill, rehearsed by CI on every push rather than written down as a procedure
  (F-38); and the close-out that stamped the build and shipped the source offer (F-39).

The last thing M6 found was not in the code. Asked what it looked like from outside, the repository
had no security policy, private vulnerability reporting switched off, and a `CONTRIBUTING.md` that told
every reader the issue tracker was closed — while it was open, and had always been. The AGPL exists to
produce readers, readers are who find security defects, and the only channel that worked was a public
one the documentation denied. `SECURITY.md` and `scripts/check_repository.sh` are the answer (F-42).

- ⧗ **M7** — the menu, and the screen it is read on. The first work in this project that came from
  somebody using it rather than from a document. **Stage 1** is the handheld layout contract (§11.12):
  every surface laid out for a phone first and widened by exactly one breakpoint, 44-pixel touch
  targets, a 16-pixel floor under every text field so iOS Safari does not zoom the page and leave it
  zoomed, and administration indexes that are lists of cards on a narrow screen instead of wide tables
  whose only affordance is off the right-hand edge (F-59). **Stage 2** is the schema — menu sections,
  item descriptions, and explicit ordering on both (ADR-0014). **Stage 3** is the surfaces that read
  it, including a guest menu that is a grouped list of described items rather than one `<select>` with
  sixty things in it. **Stages 4–6** are images, likes, and comments — with comments recorded as *not
  startable* until §17's rate-limit ruling is revisited and a decision is made about showing one
  guest's name to another.

The order is not the order it was asked in, and the reason is worth stating: the menu work adds four
surfaces that are all read from a phone, so building them before the responsive vocabulary exists means
building them against the shape the defect was found in and then touching all four again. The defect
also blocked user testing and the menu did not.
