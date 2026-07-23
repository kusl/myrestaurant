# myrestaurant — Operations

Runbooks for deploying, running, and maintaining one instance. The technical specification (`docs/TECHNICAL_SPECIFICATION.md`) is the contract for *what* the system does; this document is *how you operate it*. Section numbers here are referenced from the specification and the ADRs — renumber only with a matching edit there.

---

## 1. Deployment profiles at a glance

| | Development | Production |
|---|---|---|
| Command | `./run.sh` (or `podman-compose up`) | `podman-compose --profile production up -d` |
| Services | `web`, `postgres`, `caddy` | `web`, `postgres`, `cloudflared` (+ `caddy` only if §7 enabled) |
| Origin | `https://localhost:8443` (Caddy internal CA) | `https://<your-domain>` (Cloudflare named tunnel; TLS at the edge) |
| Passkeys | work, bound to `localhost` | work, bound to your domain — durable |

Everything runs rootless. Host ports stay ≥ 1024; if you insist on 80/443 directly, that is a host decision (`sysctl net.ipv4.ip_unprivileged_port_start=80`), not a project default.

## 2. First production deployment

**Prerequisites:** a Linux host with rootless Podman + podman-compose; a domain you control on Cloudflare; `loginctl enable-linger <user>` so your user services (backups, the stack under systemd if you wrap it) survive logout.

1. **Create the named tunnel** (Cloudflare dashboard → Zero Trust → Networks → Tunnels → *Create*). Choose the *cloudflared* connector, copy the **tunnel token**. Add a **public hostname** for the tunnel: your domain (e.g. `orders.example.com`) → service `http://web:8080`. Cloudflare creates the DNS record for you; TLS terminates at Cloudflare's edge, and traffic reaches `web` over the compose network in plain HTTP — that is by design (ADR-0005).
2. **Clone and configure.** `git clone … && cd myrestaurant`. Copy `.env.example` to `.env` (`run.sh` and the scripts do this automatically when `.env` is absent — F-16). Set at minimum:
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

`scripts/backup.sh` runs `pg_dump --format=custom` **inside the postgres container** via `podman exec` — the dump client always matches the server version (F-16) — writing `myrestaurant-YYYYMMDD-HHMMSS.dump` into `BACKUP_DIRECTORY` (bind-mounted, git-ignored), then pruning to the newest `BACKUP_RETENTION_COUNT` (default 14). Pruning happens **only after** a successful new dump: a failing backup never eats old ones.

Schedule it at `BACKUP_SCHEDULE_TIME` (default 03:30 host-local) with a systemd **user** timer or cron; with a user timer, `loginctl enable-linger` is what keeps it running without a login session.

**Back up the Data Protection keys volume in the same breath** — see §8. A database dump without the key ring is a backup that cannot decrypt any TOTP secret.

**Restore drill** — do this once *before* you need it, against a scratch host:

1. `podman-compose down` (or stop only `web`).
2. `scripts/restore.sh <dumpfile>` — runs `pg_restore --clean --if-exists` into the postgres container.
3. Restore the Data Protection keys volume from the same backup set.
4. Start the stack. `web` verifies migrations at startup: a dump from an older schema is rolled forward automatically; a dump from a *newer* schema than the code fails fast — deploy matching code first.
5. Sign in, open a sitting's event history, confirm the world is intact.

There are no down-migrations anywhere in the system (ADR-0012): recovery from a bad migration is exactly this procedure with the pre-upgrade dump.

## 7. Optional staff-LAN fallback (off by default)

Production normally runs **without** Caddy: guests and staff alike reach the instance through the tunnel, and in-house traffic hairpins through Cloudflare — an accepted risk (F-06 ruling, ADR-0005). If you want the kitchen and counter to survive a WAN outage:

1. Enable the Caddy service in the production profile with a self-signed certificate for a LAN name (e.g. `restaurant.lan`) resolving to the host, proxying to `web:8080`.
2. Install the certificate on **staff devices only** (kitchen kiosk, counter machine). Guests never touch this origin.
3. Staff bookmark `https://restaurant.lan/kitchen` and `/counter` as the emergency door.

Hard limits, by design: **passkeys do not work on this origin** — `restaurant.lan` is not the public origin and is not in `RESTAURANT_TRUSTED_ORIGIN_PATTERNS`, so the RP-ID derivation (ADR-0005) won't trust it, and a credential registered on the public domain won't match it anyway — so staff sign in with password + TOTP — which means every staff member you expect to use the fallback must actually have a password set and TOTP enrolled *before* the outage. Guest ordering from phones is still down (§11). This fallback keeps the kitchen queue and the ability to close bills alive; it is not a second front door.

## 8. Data Protection keys

The ASP.NET Data Protection key ring lives in the `DATA_PROTECTION_KEYS_DIRECTORY` volume. It encrypts every stored TOTP secret and signs every authentication cookie and join-grant cookie.

- **Losing it** means: all sessions invalid (harmless — everyone signs in again) and **every enrolled TOTP secret undecryptable** (not harmless). Recovery from key loss: administrators clear TOTP per affected account (`Reset credentials`), and users re-enroll through the obligations pipeline. Passkeys and passwords are unaffected.
- **Therefore:** the volume is part of every backup set (§6), it survives `podman-compose down`/`up` because it is a named volume, and you never delete it casually. Treat the backup copies as secrets.

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

The script stages the bring-up in the GoTunnels spirit: it detects your compose engine and a `cloudflared` runner (host binary or a container on the host network), starts PostgreSQL, opens the quick tunnel, and **polls the tunnel log for the assigned `https://<something>.trycloudflare.com` hostname**. Once it has the URL it exports it as `RESTAURANT_PUBLIC_ORIGIN` (so QR join links and the form-post host fallback resolve to the tunnel, not an internal address), force-recreates the `web` service against that origin, and waits for `/healthz/ready`. It then prints the URL in an unmissable banner and **stays in the foreground streaming tunnel logs** — the URL lives exactly as long as the process. `Ctrl+C` ends the demo; there is no detached mode and no "print the URL and exit," because the tunnel dies with the process that owns it. The script does not touch your `.env`; it passes the origin through the shell environment for this run only.

**Passkeys work on the quick tunnel**, including a passkey-only account — the RP ID is derived per request and `https://*.trycloudflare.com` is trusted by default (ADR-0005, §3.3), so you can register a passkey, sign out, and sign in with it, all within the demo. The one caveat, which the script prints loudly: every run gets a fresh random subdomain (`trycloudflare.com` is on the Public Suffix List), so **a passkey registered on one run will not match the next run's URL** and must be re-registered — quick-tunnel passkeys are not durable. Password + TOTP is the durable baseline. **Never bootstrap a real instance (§3) through a quick tunnel** — the first administrator's passkey would not survive the next run — and never point a real instance's `RESTAURANT_PUBLIC_ORIGIN` at one; use the stable named tunnel for anything that must persist.

## 11. WAN outage behavior

Default production topology has no LAN path: when the WAN drops, guests' phones and staff screens all lose the instance together, because everything hairpins through Cloudflare. Nothing corrupts — Blazor circuits drop visibly (offline banners, §4/§5), the database is untouched, and everyone reconnects when the WAN returns. Sittings stay open; nothing times out server-side. Take orders on paper, enter them as staff edits after recovery if you care about the records, and consider §7 if outages are a pattern.

## 12. Upgrades

1. `scripts/backup.sh` (and confirm it succeeded — §6).
2. `git pull` (or pull the new image), then `podman-compose --profile production up -d --build`.
3. `web` applies any new migrations at startup, fail-fast. Success → done. Failure → `web` exits non-zero and the old data is untouched; read the log, and if you must retreat, restore per §6 with the step-1 dump and the previous code.

Migrations are append-only and roll forward only — the same philosophy as the order event log. There is no schema downgrade path, ever.

## 13. Routine security operations — quick reference

| Situation | Action |
|---|---|
| Staff member leaves | Administration → Users → **Deactivate** (sessions die within the 5-minute security-stamp window; the account and its history remain, append-only) |
| Guest lost their password / authenticator | Counter identifies them in person → administrator **Reset credentials** → temporary password (shown once) → user is forced through password change and, if TOTP was enrolled, TOTP re-enrollment on next sign-in — any sign-in path, passkey included |
| Table display stolen | Revoke the device; optionally rotate the table's join secret (§5) |
| Suspected join-token abuse | Rotate the affected table's join secret — in-flight tokens die instantly; watch the `table_join_tokens_validated_total{result}` metric |
| Administrator's authenticator lost | Another administrator resets them (same flow as any user; TOTP re-enrollment is forced — administrators cannot exist unenrolled). Single-admin instances: this is why the bootstrap made you save **recovery codes** |
| Lockout complaints | 5 failed attempts locks 5 minutes, automatically clears; no admin action needed |
