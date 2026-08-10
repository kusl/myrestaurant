# myrestaurant — Operations

Runbooks for deploying, running, and maintaining one instance. The technical specification (`docs/TECHNICAL_SPECIFICATION.md`) is the contract for *what* the system does; this document is *how you operate it*. Section numbers here are referenced from the specification and the ADRs — renumber only with a matching edit there.

---

## 1. Deployment profiles at a glance

| | Development | Shared test instance | Production |
|---|---|---|---|
| Command | `./run.sh` (or `podman-compose up`) | `scripts/dev_instance.sh` | `podman-compose --profile production up -d` |
| Services | `web`, `postgres`, `caddy` | `web`, `postgres`, a detached `cloudflared` quick tunnel | `web`, `postgres`, `cloudflared` (+ `caddy` only if §7 enabled) |
| Origin | `https://localhost:8443` (Caddy internal CA) | `https://<random>.trycloudflare.com` (per tunnel) | `https://<your-domain>` (Cloudflare named tunnel; TLS at the edge) |
| Passkeys | work, bound to `localhost` | work, bound to that random host — survive a restart, **not** `--new-url` | work, bound to your domain — durable |
| Needs the .NET SDK on the host | yes | **no** | no |

The middle column is §10a: a spare machine on the LAN, reached over SSH, serving a build that testers use for days. It is the only profile here that runs entirely in containers *and* survives the terminal that started it.

Everything runs rootless. Host ports stay ≥ 1024; if you insist on 80/443 directly, that is a host decision (`sysctl net.ipv4.ip_unprivileged_port_start=80`), not a project default.

## 2. First production deployment

**Prerequisites:** a Linux host with rootless Podman + podman-compose; a domain you control on Cloudflare; `loginctl enable-linger <user>` so your user services (backups, the stack under systemd if you wrap it) survive logout.

1. **Create the named tunnel** (Cloudflare dashboard → Zero Trust → Networks → Tunnels → *Create*). Choose the *cloudflared* connector, copy the **tunnel token**. Add a **public hostname** for the tunnel: your domain (e.g. `orders.example.com`) → service `http://web:8080`. Cloudflare creates the DNS record for you; TLS terminates at Cloudflare's edge, and traffic reaches `web` over the compose network in plain HTTP — that is by design (ADR-0005).
2. **Clone and configure.** `git clone … && cd myrestaurant`, then `cp .env.example .env` — **by hand, because nothing in this tree does it for you** (**F-54**; this step previously claimed the scripts did). The scripts warn when `.env` is absent and otherwise leave it alone, and that is the ruling rather than an omission: `.env.example` carries `POSTGRES_PASSWORD=myrestaurant`, so a script that materialised it would create an untracked credentials file nobody knowingly wrote, and an absent `.env` is a *working* state — `compose.yaml` supplies development defaults for every key — so auto-creation would buy nothing while hiding the one moment an operator is supposed to make decisions. Set at minimum:
   - `RESTAURANT_PUBLIC_ORIGIN=https://orders.example.com` — this single value drives the WebAuthn RP ID and every QR URL. Get it right *before* anyone registers a passkey (§9).
   - `CLOUDFLARE_TUNNEL_TOKEN=…`
   - `POSTGRES_USER` / `POSTGRES_PASSWORD` / `POSTGRES_DB` — override the dev defaults.
   - `RESTAURANT_NAME`, `RESTAURANT_TIME_ZONE`, `RESTAURANT_CURRENCY_CODE` to taste.
3. **Start:** `podman-compose --profile production up -d`. The `web` container runs DbUp migrations at startup and exits non-zero on any failure (ADR-0012) — a crash-looping `web` at first boot means a configuration or migration problem; read `podman logs`.
4. **Verify:** `https://orders.example.com/healthz/ready` from a phone on cellular (proves the tunnel), then proceed to §3.

The application fails fast on invalid security-relevant configuration (bad origin, Argon2 parameters below the floor guard, missing connection string) — an instance that starts is an instance that is sanely configured.

## 3. First-run bootstrap — `/setup`

On a fresh database, only `/setup` is reachable. The wizard walks the first administrator through, in order and with no skippable steps: account details → **passkey registration** → **TOTP enrollment** (scan the QR with an authenticator app, confirm one code, store the recovery codes somewhere real) → the administrator grant. All of it commits as one transaction; if two browsers race, exactly one wins. From the moment an administrator exists, `/setup` is 404 forever.

Do the bootstrap **on the production origin**, never through a quick tunnel (§10) — the passkey you register binds to the origin you registered it on.

Immediately after: create the staff accounts (Administration → Users; each gets a temporary password and is forced to change it at first sign-in), create your tables, then pair the displays (§5).

**Guests need nothing from you.** A guest who scans a table's code without an account is offered registration on the spot at `/register` — a username, optionally a display name, and either a passkey or a password. There is no invitation, no approval queue, and no self-service password reset (§13 covers the lost-credential path, which runs through the counter). A guest account carries no role and therefore no capability beyond its own order, so the only cost of a junk registration is a row.

## 4. Kitchen display runbook

Use a dedicated, always-powered device (tablet or small PC) in browser kiosk mode pointed at `/kitchen`, signed in with a kitchen-role account (kitchen accounts hold a passkey — registered at grant time).

Start of every shift: tap **Arm audio** once. Browsers refuse to autoplay sound without a user gesture; the armed state is indicated on screen and its *absence* is a prominent warning. Arming also acquires the screen wake lock (re-acquired automatically when the tab regains visibility). Disable OS-level sleep in the device's kiosk settings anyway — belt and braces.

If the circuit drops, the display replaces itself with a full-screen, high-contrast offline banner and an audible chirp — a dead kitchen display must be unmissable, never silently stale. It reconnects automatically; if it doesn't within a minute, check the host and the network.

Alert behavior, for expectations-setting with staff: one loud alert per guest send and per counter/administrator line change; the kitchen's own edits and fulfillments are silent; one reminder fires if a send with added lines has had none of them fulfilled or removed after 60 seconds (`KITCHEN_SUBMISSION_REMINDER_SECONDS`), and never a second reminder.

## 5. Table display devices

Any cheap device with a browser works — an old tablet on a stand is the reference hardware. Per table:

1. Administration → Tables → the table → **Generate pairing code**. You get an 8-character one-time code, valid 10 minutes (`TABLE_DISPLAY_PAIRING_CODE_MINUTES`), single-use, rate-limited server-side (5 attempts/minute/IP).
2. On the device, open `https://<origin>/display/pair`, enter the code, give the device a label ("Table 4 — window tablet"). The device receives a long-lived credential cookie and lands on `/display/{table}`: full-screen table label + the rotating join QR, refreshing on the 60-second window boundary, with a party-size chip while a sitting is open.
3. Kiosk-mode the browser, disable sleep, done. The display acquires a wake lock like the kitchen screen and shows a prominent offline state if the circuit drops — a frozen QR must never masquerade as a live one.

**A display dies mid-service:** nothing stops. The counter opens the sitting (or the table) and taps **Show join code** — the same rotating QR renders on the counter screen and the guest scans that. Replace or re-pair the device when convenient.

**A display walks away:** Administration → Tables → the device → **Revoke**. The credential dies on its next request. The device itself holds nothing worth extracting — the table's join secret never leaves the server; the screen only ever showed tokens that expire within ≤ 120 seconds. If you want ceremony anyway, also **Rotate join secret** on the table: every in-flight token dies instantly and the (revoked or replacement) display picks up the new sequence on its next window.

## 6. Backups and restore

**A backup is two files.** `scripts/backup.sh` writes both, sharing one timestamp, into `BACKUP_DIRECTORY` (bind-mounted, git-ignored):

```
myrestaurant-20260804-033000.dump                  the database — pg_dump --format=custom
myrestaurant-20260804-033000-dataprotection.tar    the Data Protection key ring (§8)
```

`pg_dump` runs **inside the postgres container** via `podman exec`, so the dump client always matches the server version (F-16). The key ring comes out of the web container with `podman cp`, which streams a tar through the engine's own archive API and therefore needs nothing installed inside that image.

**Both files, or you do not have a backup.** A dump without the key ring restores every account and no enrolled authenticator — see §8. The script tells you which you got: exit **0** is a complete set, exit **2** means the database was dumped and the key ring was not, exit **1** means nothing usable was written. Wire the schedule so a 2 actually reaches you; it is recoverable, but it is not a backup.

Run it as-is in production. In **dev** the application runs on the host under `run.sh`, so there is no container to read a key ring out of — use `scripts/backup.sh --no-keys` there.

Schedule at `BACKUP_SCHEDULE_TIME` (default 03:30 host-local) with a systemd **user** timer or cron; with a user timer, `loginctl enable-linger` is what keeps it running with nobody logged in. Retention keeps the newest `BACKUP_RETENTION_COUNT` sets (default 14) and prunes **only after** a new set has landed, so a failing backup never eats old ones. The dump is written to a hidden `.partial` file and renamed into place only once it is complete, which is what stops a half-written dump from counting as the newest backup and evicting a good one on the *next* run.

Container discovery **refuses when more than one container matches** rather than picking the first, and names what it found. Set `POSTGRES_CONTAINER` / `WEB_CONTAINER` to settle it. This matters more than it sounds: dumping the wrong database succeeds, comes out roughly the right size, and is worthless.

**On a host with both podman and docker, the container chooses the engine.** `backup.sh` asks each available engine whether it can see the container it was told to dump, and uses the one that can — rather than taking the first engine on `PATH`, which is what it used to do. Set `CONTAINER_ENGINE` to skip the question entirely; `scripts/restore_drill.sh` honours the same variable and *needs* it more, because the drill creates its own scratch container and therefore has nothing to infer an engine from. Worth knowing because of how the old behaviour presented (F-43): `podman exec` against a container belonging to the Docker daemon fails with "no such container", the script reported that as the database not answering `pg_isready`, and the message sends you to PostgreSQL for a fault entirely in engine selection. The two conditions are now said separately — "knows it but it is not running" is a different line from "did not answer for these credentials".

### The drill

```bash
scripts/restore_drill.sh                 # rehearse the newest set
scripts/restore_drill.sh --from-live     # take a fresh set first, then rehearse it
scripts/restore_drill.sh --strict        # treat reservations as failures
scripts/restore_drill.sh --keep          # leave the scratch container for inspection
```

**Run it now, and again after anything that touches the schema.** It restores into a scratch PostgreSQL container it creates and destroys itself — no maintenance window, no scratch host, no published port, and it never writes to the live database — then checks that the archive lists; that it restores, and with how many ignored errors; that every table and view the migrations declare came back; that DbUp's journal is at a version this code will accept; that every projection view still resolves; what the row counts are; and that the key ring is present and not empty. Ninety seconds. CI runs the same script on every push (§14).

It is not a replacement for knowing the procedure below, because the day you need it the drill is not what you will be running.

### Real recovery

1. `scripts/restore.sh <dumpfile>` — stops `web`, restores the database, puts the key ring back from the sibling tar, starts `web` again, and waits for `/healthz/ready`. Add `--yes` to skip the confirmation prompt in a scripted recovery.
2. **Read the exit code.** **0** restored and healthy. **2** restored with reservations — `pg_restore` ignored some errors (usually benign under `--clean --if-exists`), or the key ring was not put back; the warnings say which. **1** nothing was restored.
3. Sign in, open a sitting's event history, and confirm the world is intact.

`web` is started again on **every** path out of that script, including the paths that got there by failing, because it happens in an `EXIT` trap. That is worth knowing because it used not to be true: `pg_restore` exits non-zero whenever it ignored any error, the script runs under `set -e`, and the line that restarted `web` came *after* the restore — so the single most likely outcome of the previously documented procedure was a database that came back and an application that stayed down, with nothing saying so (F-38).

A dump from an **older** schema is rolled forward automatically at startup. A dump from a **newer** schema than the code fails fast — deploy matching code first. There are no down-migrations anywhere in the system (ADR-0012): recovery from a bad migration is exactly this procedure with the pre-upgrade dump.

**If you ran `down` rather than `stop`**, the web container no longer exists and the key ring has nowhere to go; the script says so instead of pretending. Bring the stack up and re-run, or place it by hand:

```bash
podman cp - '<web-container>:/var/lib/myrestaurant/dataprotection' < myrestaurant-<stamp>-dataprotection.tar
```

## 7. Optional staff-LAN fallback (off by default)

Production normally runs **without** Caddy: guests and staff alike reach the instance through the tunnel, and in-house traffic hairpins through Cloudflare — an accepted risk (F-06 ruling, ADR-0005). If you want the kitchen and counter to survive a WAN outage:

1. Enable the Caddy service in the production profile with a self-signed certificate for a LAN name (e.g. `restaurant.lan`) resolving to the host, proxying to `web:8080`.
2. Install the certificate on **staff devices only** (kitchen kiosk, counter machine). Guests never touch this origin.
3. Staff bookmark `https://restaurant.lan/kitchen` and `/counter` as the emergency door.

Hard limits, by design: **passkeys do not work on this origin** — `restaurant.lan` is not the public origin and is not in `RESTAURANT_TRUSTED_ORIGIN_PATTERNS`, so the RP-ID derivation (ADR-0005) won't trust it, and a credential registered on the public domain won't match it anyway — so staff sign in with password + TOTP — which means every staff member you expect to use the fallback must actually have a password set and TOTP enrolled *before* the outage. Guest ordering from phones is still down (§11). This fallback keeps the kitchen queue and the ability to close bills alive; it is not a second front door.

## 8. Data Protection keys

The ASP.NET Data Protection key ring lives in the `DATA_PROTECTION_KEYS_DIRECTORY` volume. It encrypts every stored TOTP secret and signs every authentication cookie and join-grant cookie.

- **Losing it** means: all sessions invalid (harmless — everyone signs in again) and **every enrolled TOTP secret undecryptable** (not harmless). Recovery from key loss: administrators clear TOTP per affected account (`Reset credentials`), and users re-enroll through the obligations pipeline. Passkeys and passwords are unaffected.
- **Therefore:** `scripts/backup.sh` captures it into `myrestaurant-<timestamp>-dataprotection.tar` beside every dump and `scripts/restore.sh` puts it back (§6) — it is no longer something you have to remember, and `scripts/restore_drill.sh` fails a drill whose set does not include one. It survives `podman-compose down`/`up` because it is a named volume, and you never delete it casually. Treat the backup copies as secrets: that tar is the key material, in the clear, which is also why `.gitignore` refuses it and why CI does not upload one as an artifact.

## 9. Changing the public origin (domain move)

A passkey binds to the RP ID — the host it was registered on. In production every browser is on the named-tunnel domain, so that is the host of `RESTAURANT_PUBLIC_ORIGIN` (the RP ID is derived per request, ADR-0005, but in production there is only the one public host). Moving domains therefore **orphans every passkey on the instance**. This is WebAuthn, not a bug; plan accordingly.

1. **Before the move**, confirm every administrator can complete a **password + TOTP** sign-in on the current origin. An administrator who is passkey-only with no password set will be locked out of administration by the move — have them set a password first. Encourage staff to do the same.
2. Create/repoint the named tunnel's public hostname to the new domain; update `RESTAURANT_PUBLIC_ORIGIN`; restart the stack.
3. Everyone's passkeys are now dead weight. Users sign in with password (+ TOTP where enrolled), get the passkey nudge, register a fresh passkey on the new origin, and delete the stale credential from their profile at leisure.
4. Table QR URLs embed the origin, but the displays render them live from configuration — they are correct the moment the stack restarts. No physical reprinting exists to worry about, because nothing is printed.

## 10. Quick-tunnel demo runbook

Show-and-tell over the public internet is a **one-command** flow (spec §14.3/§14.4, ADR-0005 — the quick tunnel is a separate helper, not part of `run.sh`):

```bash
scripts/quick_tunnel.sh        # brings the stack up, exposes it, stays in the foreground
```

The script stages the bring-up in the GoTunnels spirit: it detects your compose engine and a `cloudflared` runner (host binary or a container on the host network), starts PostgreSQL, opens the quick tunnel, and **polls the tunnel log for the assigned `https://<something>.trycloudflare.com` hostname**. Once it has the URL it exports it as `RESTAURANT_PUBLIC_ORIGIN` (so QR join links and the form-post host fallback resolve to the tunnel, not an internal address), force-recreates the `web` service against that origin, and waits for `/healthz/ready`. It then prints the URL in an unmissable banner and **stays in the foreground streaming tunnel logs** — the URL lives exactly as long as the process. `Ctrl+C` ends the demo. That script has no detached mode, and the reason is worth being precise about (**F-52**): the tunnel dies with the process that owns it, and this script *is* that process, because it runs `cloudflared` as a foreground child and blocks on it. Ownership is a choice, not a property of quick tunnels — run `cloudflared` as a detached container and the engine owns it, and the shell can walk away. That is §10a, and it is a different script. The script does not touch your `.env`; it passes the origin through the shell environment for this run only.

**Passkeys work on the quick tunnel**, including a passkey-only account — the RP ID is derived per request and `https://*.trycloudflare.com` is trusted by default (ADR-0005, §3.3), so you can register a passkey, sign out, and sign in with it, all within the demo. The one caveat, which the script prints loudly: every run gets a fresh random subdomain (`trycloudflare.com` is on the Public Suffix List), so **a passkey registered on one run will not match the next run's URL** and must be re-registered — quick-tunnel passkeys are not durable. Password + TOTP is the durable baseline. **Never bootstrap a real instance (§3) through a quick tunnel** — the first administrator's passkey would not survive the next run — and never point a real instance's `RESTAURANT_PUBLIC_ORIGIN` at one; use the stable named tunnel for anything that must persist.

## 10a. Shared test instance on a spare machine (no .NET SDK)

The case: a box on the LAN — reachable over Tailscale or plain SSH — running the build that testers will use for the next few days. Rootless Podman and podman-compose are installed. **`dotnet` is not, and does not need to be:** `run.sh` cannot help you here (its default and `--smoke` modes both require the SDK on the host), while the image is built by the SDK container `Containerfile` names.

```bash
ssh spare-box
cd ~/src/myrestaurant
git pull
scripts/dev_instance.sh                # builds, opens the tunnel, prints the URL, EXITS
```

It returns you to the prompt and the instance keeps serving. You can close the SSH session.

| | |
|---|---|
| `scripts/dev_instance.sh` | bring it up; prints the public URL and exits |
| `scripts/dev_instance.sh url` | that URL again, on stdout and nothing else |
| `scripts/dev_instance.sh status` | tunnel state, current URL, `compose ps` |
| `scripts/dev_instance.sh logs -f` | the tunnel's log; `Ctrl+C` stops watching, not the instance |
| `scripts/dev_instance.sh down` | the only thing that closes the tunnel |
| `scripts/dev_instance.sh --new-url` | discard the hostname and mint a new one — **breaks every passkey** |
| `scripts/dev_instance.sh --no-build` | skip the image build and use whatever is already tagged |

**Re-running it reuses the URL.** That is the point, not an optimisation: a tester who has registered a passkey has registered it against that hostname, and a new random subdomain throws it away. Restart the app, `git pull` and rebuild, reboot the host and bring it back — the hostname holds as long as the tunnel container does. Only `down` and `--new-url` end it.

**Before you disconnect, check the linger line the script prints.** Rootless containers belong to your user session. Debian's logind default (`KillUserProcesses=no`) leaves them running when you log out, so in practice this works untouched — but the guarantee is one command, the same one §2 asks for on a production host:

```bash
loginctl enable-linger "$USER"
```

**The first build is slow and the script says so before it starts.** Measured cold on the machine this was written for: about nineteen minutes, almost all of it the SDK image pull and the `dotnet publish`. Nothing is public during that time — the tunnel is opened *after* the build, which is the ordering fix in §14.3a. Subsequent runs reuse the layer cache and take seconds.

**Copy `.env.example` to `.env` first if the box is reachable by anything you do not control.** Copy it yourself — no script here writes it (**F-54**, and §2 says why). Without it the stack runs on `compose.yaml`'s development defaults, including `POSTGRES_PASSWORD=myrestaurant`. Do not put `RESTAURANT_PUBLIC_ORIGIN` in that `.env`: the script sets it from the tunnel URL through the process environment, which takes precedence, so a pinned value would be ignored and the file would disagree with the running instance. The script warns when it finds one.

**If it appears to hang, it is not hanging any more — but read this anyway, because the first version did (F-53).** Every compose command this script runs has a deadline (`DEV_INSTANCE_COMPOSE_WAIT`, 240s; the image build gets `DEV_INSTANCE_BUILD_WAIT`, 5400s, since a cold build legitimately takes twenty minutes). When one trips you get a paragraph naming F-53, then a two-line report of what the containers are actually doing, then readiness verified independently of compose — so the terminal comes back either way. The failure it guards against is worth recognising, because nothing in the output points at it: podman-compose 1.3.0 (Debian trixie's version) runs `podman run -d` for every container and *then* waits on each `depends_on` condition in an unbounded loop that prints nothing, so `up -d` prints the container ids and stops, forever, with the stack already up and serving. If you ever see that from `podman-compose` directly, `Ctrl+C` is safe: the containers belong to the engine, not to the shell, and `scripts/dev_instance.sh status` will show them.

**`status` answers from the engine before it asks compose**, for the same reason — the two lines that matter (`postgres: … running, health: healthy` and `web: … running`) arrive even on a host where compose itself is wedged. A `health: starting` on `postgres` that never advances is the specific symptom behind F-53: it means nothing is running the container's healthcheck, which under rootless Podman is a systemd timer in your user session.

**What this is still not.** Everything §10 says applies unchanged, and applies harder because the instance is long-lived: the hostname is random and on the Public Suffix List, passkeys do not survive `--new-url`, and **you must not bootstrap a real instance (§3) here** — the first administrator's credentials would be tied to a hostname you will eventually discard. Take a backup (§6) before you tear a test instance down if the data in it mattered; `down` leaves the named volumes alone, so the database and the key ring survive a normal stop.

## 11. WAN outage behavior

Default production topology has no LAN path: when the WAN drops, guests' phones and staff screens all lose the instance together, because everything hairpins through Cloudflare. Nothing corrupts — Blazor circuits drop visibly (offline banners, §4/§5), the database is untouched, and everyone reconnects when the WAN returns. Sittings stay open; nothing times out server-side. Take orders on paper, enter them as staff edits after recovery if you care about the records, and consider §7 if outages are a pattern.

## 12. Upgrades

1. `scripts/backup.sh` (and confirm it succeeded — §6).
2. `git pull` (or pull the new image), then `podman-compose --profile production up -d --build`.
3. `web` applies any new migrations at startup, fail-fast. Success → done. Failure → `web` exits non-zero and the old data is untouched; read the log, and if you must retreat, restore per §6 with the step-1 dump and the previous code.

Migrations are append-only and roll forward only — the same philosophy as the order event log. There is no schema downgrade path, ever.

**Steps 1 and 2 are in that order for a reason, and until Slice 22 that order was a hazard (F-45).** Step 1 writes a `-dataprotection.tar`, which §8 calls the key material in the clear. Step 2 builds an image whose context was the entire working tree — so if `BACKUP_DIRECTORY` pointed anywhere inside the repository, the backup you had just taken was copied into the image builder. `.gitignore` names all of it and protected none of it: a build context is not a commit, and nothing in this project had ever looked at the difference. `.dockerignore` now reduces the context to the three source projects plus four files, and `Containerfile` refuses to build if that did not take effect, so the ordering is safe on any `BACKUP_DIRECTORY` you choose. You do not have to move it, and you no longer have to remember not to.

## 13. Routine security operations — quick reference

| Situation | Action |
|---|---|
| Staff member leaves | Administration → Users → **Deactivate** (sessions die within the 5-minute security-stamp window; the account and its history remain, append-only) |
| Guest lost their password / authenticator | Counter identifies them in person → administrator **Reset credentials** → temporary password (shown once) → user is forced through password change and, if TOTP was enrolled, TOTP re-enrollment on next sign-in — any sign-in path, passkey included |
| Table display stolen | Revoke the device; optionally rotate the table's join secret (§5) |
| Suspected join-token abuse | Rotate the affected table's join secret — in-flight tokens die instantly; watch the `table_join_tokens_validated_total{result}` metric |
| Administrator's authenticator lost | Another administrator resets them (same flow as any user; TOTP re-enrollment is forced — administrators cannot exist unenrolled). Single-admin instances: this is why the bootstrap made you save **recovery codes** |
| Lockout complaints | 5 failed attempts locks 5 minutes, automatically clears; no admin action needed |
| A scanner reports missing security headers | It is probably reporting `Strict-Transport-Security`, and it is right that this application does not send one — see the note at the end of §14. Everything else on a typical scanner's list is sent on **every** response by the application itself (specification §11.11): a Content Security Policy, `X-Content-Type-Options` and `Referrer-Policy`. Check with `curl -sSI https://your-host/ \| grep -i -e content-security -e x-content-type -e referrer`, and check a **static file** too — `curl -sSI https://your-host/app.css` — because that is the response class a proxy-level rule most often misses |
| A page or a screen stopped working after a deployment and the browser console says "Refused to …" | That is the Content Security Policy (§11.11) doing its job on something new. It is not tuned per deployment and it is not configurable: it is source, and `ContentSecurityPolicyContractTests` fails on the change that would have needed it widened. Report it rather than working around it at the proxy — a header added in front of this application does not replace the one it sent, it arrives beside it, and two policies are enforced as an intersection |
| Somebody reports a vulnerability | §16 — and note that the private channel `SECURITY.md` names is a **repository setting**, so §16's first table is worth checking before you need it |

## 14. Continuous integration and releases

CI is not an operations concern until the day you need to know *which build* is on the box and whether anyone verified it. This section is that day.

**What every push is checked against.** `.github/workflows/ci.yml` runs six gates on every push and pull request against `main`: `tree` (the checkout is machine-readable at all — see below), `governance` (a security policy exists and no document asserts a repository setting — the blocking half; the advisory half reports the settings themselves), `shell-scripts` (every tracked `*.sh` parses and passes shellcheck), `build-and-test` (a Release build with warnings escalated to errors, then the whole suite — which since Slice 24 includes the response-header contract tests, so a change to the markup that the Content Security Policy would refuse fails here rather than in a browser — the data-access integration tests execute against real PostgreSQL here rather than skipping the way they do on a machine with no container socket), `end-to-end` (the §16.3 Playwright scenarios in Chromium, all fifteen of them), and `boot-smoke` (the production `Containerfile` is built, the resulting image is booted against a real PostgreSQL until `/healthz/ready` answers 200, and then that instance is backed up and the backup is put through `scripts/restore_drill.sh`).

`boot-smoke` is the gate that matters operationally, because it is the only one that exercises what a deployment exercises: DbUp applying every migration to an empty database, `RestaurantOptions.Validate()` accepting the configuration, and the composition root resolving. A green `boot-smoke` now says "this commit starts, **and its data comes back**". Nothing else in the suite says either half.

The restore drill lives in that job rather than one of its own because everything it needs is already standing up there — a built image, a migrated database, a live key ring. Giving it a separate job would mean building the image twice for one answer. And putting it in CI at all is the point: §6's drill was five manual steps for four milestones and nobody had performed them, so when something finally did, it found that `scripts/restore.sh` could not have completed a restore at all (F-38). A procedure nobody executes is a hypothesis.

CI deliberately does **not** use `compose.yaml`. The data-protection volume carries Podman's `:U` suffix — correct for the canonical rootless engine (ADR-0004) and rejected outright by Docker Compose — so the job uses a service-container PostgreSQL plus one `docker run --network host` instead. Same image, same environment variables, same readiness probe; the canonical stack stays the only compose file in the tree.

**The `tree` gate exists because of an outage in the toolchain, not in the product** (F-40). On 2026-08-05 every MSBuild verb in the repository — `clean`, `restore`, `build`, `test`, and the container build — failed with `MSB4024: Data at the root level is invalid`, because `Directory.Build.props` had acquired a stray line after `</Project>` and MSBuild imports that file before it evaluates anything. Twenty other tracked files had acquired the same line and said nothing about it, since in YAML, in a Containerfile, in `.env` and in Markdown it is inert. `scripts/check_tree.sh` now asserts, in about two seconds and with no SDK, that no tracked file carries a context-dump separator, that no line is made only of whitespace, that every file uses LF endings and ends with a newline, that every MSBuild and solution file is well-formed XML, and that every YAML file parses. **Run it before you deliver anything into this tree**, not only in CI:

```bash
bash scripts/check_tree.sh
```

It is also the first gate of `scripts/ci_local.sh`, so `--with-all` already covers it.

`boot-smoke` also fetches `/source` with no cookie and fails unless the response names the commit the image was built from. That is the gate behind "Verifying what is actually running" below: the version an instance reports is checked on every push, so it is worth trusting.

**Cutting a release.**

```bash
bash scripts/check_tree.sh                # seconds, no SDK; run this first (F-40)
bash scripts/check_repository.sh          # seconds; the governance surface, and the settings (F-42)
scripts/ci_local.sh --with-all            # optional, but far cheaper than a failed tag
```

1. **Bump `VersionPrefix` in `Directory.Build.props`** to the version you are about to tag, and commit it. This is the number an *untagged* build reports; the pipeline overrides it from the tag, so skipping this step does not produce a wrong image — it produces a `main` that misreports itself between releases.
2. Tag and push:

```bash
git tag --annotate v1.0.0 --message 'M6 complete'
git push origin v1.0.0
```

`.github/workflows/release.yml` re-runs the full CI workflow (it calls `ci.yml` rather than repeating it, so a tag is verified by exactly the gates a push is), derives the version from the tag, passes it and the commit into the image build so the running container reports what the registry called it, publishes to GitHub Container Registry, and finally opens a release on the tag. The release step is downstream of the push and idempotent — re-running updates the note rather than failing — so a half-published release cannot advertise an image that is not there:

- `ghcr.io/kusl/myrestaurant:1.0.0` — the exact version. **Use this in production.**
- `ghcr.io/kusl/myrestaurant:1.0` — moves with each patch on that minor.
- `ghcr.io/kusl/myrestaurant:sha-<commit>` — for when you need to name a build rather than a version.

There is no `latest`, on purpose. A tag that silently changes what it points at is the reason people cannot answer "what is running".

**What goes into the image, and what the build refuses.** `Containerfile`'s build stage is `COPY . .`, so the build context is whatever `.dockerignore` leaves of the repository root. That file is an **allow-list**: `.editorconfig`, `Directory.Build.props`, `Directory.Packages.props`, `global.json`, and `src` — and, from inside `src`, never `bin` or `obj`. Everything else is excluded by default, including tomorrow's file that nobody has thought about yet. On a fresh clone that is 169 files and 1.6 MB rather than 458 files and 31 MB; on a workstation that has run the test suite it is a great deal more than that.

The list is stated twice on purpose. `.dockerignore` is the instruction, and a `RUN` guard immediately after the `COPY` is the assertion that the instruction took effect — because an ignore-file can be renamed, shadowed by a `.containerignore` (Podman prefers that name when both exist), or overridden with `--ignorefile`, and none of those failures announce themselves. If the two disagree the build stops and names what it found, which is the only outcome that distinguishes "excluded" from "excluded on the machine where somebody last checked". Adding a source directory to the tree therefore means adding it to both, and a build that fails with `BUILD CONTEXT REJECTED` immediately after `COPY . .` is telling you exactly that.

**Images are `linux/amd64` only.** The `Containerfile` runs a full `dotnet publish` inside its build stage, and doing that for arm64 through QEMU emulation is slow enough to risk the job timeout. If you want to run this on an ARM single-board machine, build on the box (`podman-compose --profile production up -d --build`, which is the default deployment anyway) or teach the `Containerfile` a cross-compiled publish (`-r linux-arm64` from an amd64 SDK). Emulation is the wrong fix.

**Deploying from the registry instead of building on the box.** The default production flow builds locally — `git pull` then `--build` (§12) — which needs the SDK image and a few minutes of CPU on the host. To deploy a published image instead, add a `compose.override.yaml` beside `compose.yaml`; Podman and Docker both merge it automatically, and it stays untracked so it cannot follow a `git pull`:

```yaml
services:
  web:
    build: !reset null
    image: ghcr.io/kusl/myrestaurant:1.0.0
```

Then the upgrade in §12 becomes: back up, edit the pinned version in that one file, `podman-compose --profile production up -d`. Everything else about §12 still holds — `web` applies new migrations at startup and exits non-zero on failure, and there is no schema downgrade path, ever.

If your compose implementation does not support `!reset`, delete the `build:` block by writing the whole `web` service out in the override instead.

**The intent is that these images pull without a login**, and if yours does not, the switch is not in this repository — package visibility is a setting on the package, separate from the repository's own visibility, and a package first published by a workflow may land private. `ghcr.io/<owner>/<package>` → Package settings → *Change visibility*. This paragraph states an intention rather than a fact on purpose: a sentence here saying the images *were* already so configured was false for as long as it existed, because it described a checkbox nothing in this tree can see, about a package that did not yet exist (F-46, and F-42 before it — §16 has the same warning about the setting `SECURITY.md` depends on). If you hit a 401 on a `podman pull`, the answer is one page in the package settings and not a bug in the override above.

**Verifying what is actually running.** Ask the application, not the host:

```bash
curl --silent https://your-domain.example/source | grep --after-context=3 'source-revision'
```

Every page's footer links to `/source`, which reports the version and the exact source revision the running binary was built from. That answer comes from inside the process — it survives a mislabelled image, a hand-edited `compose.override.yaml`, and a container somebody restarted from a tag that has since moved. It is also anonymous, so you can check it from a phone without signing in, and it is the answer CI verifies on every push.

A build produced without a revision stamp — a local `podman build` with no arguments, for instance — says **"Not recorded"** rather than guessing. That is itself the useful signal: a production instance saying "Not recorded" did not come from the release pipeline.

The container labels are a second opinion, from the outside:

```bash
podman inspect --format '{{index .Config.Labels "org.opencontainers.image.version"}}' myrestaurant_web_1
podman inspect --format '{{index .Config.Labels "org.opencontainers.image.revision"}}' myrestaurant_web_1
```

Those are stamped by `docker/metadata-action` at publish time, so they are present on a registry image and absent on one you built locally. If the labels and `/source` disagree, believe `/source` — the labels describe the image somebody pushed, the page describes the code that is executing.

`service.version` on every trace and metric carries the same string, so a latency change after a deployment is attributable to a build rather than to the weather.

**One security header this application deliberately does not send, and where it belongs.**
`Strict-Transport-Security` is not emitted by the application, and that is a decision rather than an
omission (specification §11.11, ADR-0013). Three reasons, in order of how much they would cost you.
It is the one header with a **long memory**: a browser that has seen `max-age=31536000` will refuse
plain HTTP to that host for a year, and there is no way to reach back and tell it otherwise except by
serving `max-age=0` over working HTTPS for as long as it takes every visitor to come back. It is
**meaningless where this process sits**: TLS terminates at Cloudflare's edge (or at Caddy), and the hop
from there to `web:8080` is plain HTTP, so the application is not the thing making the promise. And its
parameters — the `max-age`, whether to include subdomains, whether to submit to the preload list — are
decisions about **your domain**, not about this software, and a fork that inherited this repository's
answer would be inheriting a promise about a name it does not own.

So: **turn it on at the edge**, once you are confident HTTPS works on every name that resolves to you,
starting with a short `max-age` and lengthening it. Cloudflare has a switch for this under SSL/TLS →
Edge Certificates; Caddy sends nothing by default and takes a `header` directive. Everything else a
scanner will ask for is already sent by the application on every response, including on static files —
which is the class a proxy-level rule most often misses, because it is usually written against the
paths somebody remembered to test.

## 15. If you fork this — your obligations, and the one variable that meets them

This program is AGPL-3.0-only. **§13 of that licence asks anyone who runs a *modified* version as a network service to offer its users the corresponding source.** Running this tree unmodified places you under no such obligation, and everything below is then a courtesy you get for free.

If you *have* modified it — and forking is explicitly encouraged; `CONTRIBUTING.md` says so — the mechanism is already in the application and takes one variable:

```bash
RESTAURANT_SOURCE_URL=https://git.example.com/you/myrestaurant
```

Publish your modified source at that URL, and the footer of every page already links to a `/source` page that offers it, names the version, and names the revision. That is what §13 asks for.

**Then check that it took, because for four milestones it would not have.** `compose.yaml`'s `web` service names its environment variable by variable and takes no `env_file`, and until Slice 25 it did not name this one — so a value set in `.env` never reached the process, the application used its compiled-in default, and `/source` offered *this* repository to the users of a modified program. Nothing failed: the container started, the page rendered, the link resolved (F-50). The check is one command and it is the only thing that distinguishes the two outcomes:

```bash
curl --silent http://localhost:8080/source | grep 'git.example.com'
```

Your own URL, not this one. `ConfigurationSurfaceTests` now asserts on every `dotnet test` that every variable the program reads is passed by that service — so the class of defect is closed — but the value itself is yours, and only you can see whether it is the right one.

Three things worth knowing:

- **`http` is accepted here**, unlike `RESTAURANT_PUBLIC_ORIGIN`. A Gitea on your LAN discharges the obligation perfectly well and the application will not refuse to boot over the scheme.
- **Stamp your builds** or the offer cannot name a revision. Pass `--build-arg SOURCE_REVISION=$(git rev-parse HEAD)` to `podman build`, and `--build-arg VERSION=...` if you version your fork separately. Without them the page reports the version and "Not recorded", which is honest but less useful to the person asking.
- **There is no setting that removes the offer.** That is deliberate. If you want it gone you have the source and the freedom to remove it — which is, precisely, the arrangement this licence exists to guarantee. Just be aware of what you are removing and from whom.

None of this is legal advice; it is the mechanism. `LICENSE` is the text that governs.

## 16. Somebody reported a vulnerability

This is the receiving end of `SECURITY.md`. It exists because the alternative to writing it down is
improvising the first time, and the first time is the worst possible moment to be deciding what the
process is.

### The settings this depends on, which are not in the tree

`SECURITY.md` sends a reporter to GitHub's private advisory form. That form is a **repository setting**,
not a file, so no gate in this project can turn it on and `scripts/check_repository.sh` can only tell
you it is off. Check these once, and re-check them after any repository migration or transfer:

| Setting | Where | Wanted |
|---|---|---|
| Private vulnerability reporting | Settings → Advanced Security | **enabled** — this is the channel the policy names |
| Security policy detected | Security tab | `SECURITY.md` shows up (it is read from the repository root) |
| Repository description | the About box | set — it is the first line anybody reads |
| Issues | Settings → Features | either way; `SECURITY.md`'s fallback wants it on, and nothing in the tree claims a state for it |
| Wiki | Settings → Features | **off** is the intent — every document here is in the tree and under the atomic-documentation rule, so a wiki is a second place for documentation to be wrong with no gate over it |

```bash
bash scripts/check_repository.sh
```

The tree half of that script is blocking and offline. The platform half needs a token
(`GITHUB_TOKEN`, `GH_TOKEN`, or an authenticated `gh`) and is advisory — it reports these settings and
never fails on them, because a fork's settings are the fork's business.

### Triage

1. **Acknowledge within seven days.** Even "received, looking at it, expect an assessment in a
   fortnight" — the target that gets missed most is this one, and missing it is what turns a
   coordinated report into an uncoordinated one.
2. **Decide whether it is a defect or §17.** The accepted-risks register is the first question, not the
   last. If the report restates a ruled risk, answer with the paragraph that ruled it and say the
   argument is welcome on the merits. If it argues the ruling was wrong, that is a specification
   question and it goes through the atomic-documentation rule like any other.
3. **Reproduce it before believing it, and before disbelieving it.** The end-to-end harness is the
   right tool: a scenario that demonstrates the problem is worth more than a paragraph describing it,
   and it becomes the regression test for free.
4. **Write the draft advisory as you go**, in the GitHub Security Advisory on the report. It is private
   until published, so it is the natural place for the working notes, and it means the note is not
   being written from memory on the day of the fix.

### Fixing it

The fix is an ordinary commit under the ordinary rule — behaviour, `REQUIREMENTS.md`, the
specification, a `DOCUMENTATION_REVIEW.md` row, and any affected ADR, in one commit. Two additions:

- **A regression test lands with it**, and preferably a §16.3 scenario rather than a unit test, because
  a security defect that a unit test can see is usually one a unit test would already have seen.
- **The ledger row names the reporter**, unless they asked otherwise. The register is the project's
  memory and an outside finding is worth being visibly outside.

Then tag it. §14's release procedure is unchanged — bump `VersionPrefix`, tag, push — and the pipeline
publishes the image and opens the release. **Publish the advisory when the tag exists**, not before:
the advisory is what tells operators to upgrade, and it should never point at a version that is not in
the registry yet.

### Telling operators

You cannot. There is no callback from a running instance to this repository, by design — no telemetry
home, no update check, nothing that phones anywhere. The advisory and the release note are the whole
notification mechanism, and a fork operator has to be the one looking. `SECURITY.md` says so in the
fork section rather than leaving it as an unpleasant surprise.

This is a real limitation and it is worth being clear-eyed about rather than defensive: the same
property that makes this software safe to self-host — it talks to nobody — is what makes it impossible
to warn its operators. If that trade ever looks wrong, the thing to change is §17 and this paragraph,
not to quietly add a version check.

### If a report arrives on the issue tracker anyway

Somebody will eventually post a security problem publicly, having read nothing. Delete or hide the
comment, respond privately, and treat the clock as having started at the public post — because it did.
Whether the tab is open or closed is not the interesting variable; a determined reporter with no
private channel will use whatever is available, which is the argument for the private channel existing
at all.
