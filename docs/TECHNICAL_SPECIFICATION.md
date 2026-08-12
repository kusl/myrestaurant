# myrestaurant — Technical Specification

**Version 1.17 — 2026-08-11 — Status: accepted, implementation-ready.** (Changelog at the bottom; v1.0 was 2026-07-17.)

This document is the normative implementation contract for the system described in `docs/REQUIREMENTS.md` (rev 5). It is written so that a person or an LLM who has never seen the project can implement it without asking questions. The words **must**, **must not**, **should**, and **may** are used in their RFC 2119 sense. Where this specification and an ADR describe the same decision, they agree by construction; the ADRs in `docs/adr/` carry the rationale, this document carries the mechanism. The decisions register in Appendix A maps every ruling to its embodiment.

---

## 0. Glossary

- **person** — any account: guest, staff, or administrator. One row in `person`.
- **role** — `administrator`, `kitchen`, or `counter`, held by a person (`person_role`). `guest` is not a stored role; it is the implicit capacity of any person acting as a sitting member on their own order.
- **table** — a physical table (`restaurant_table`), holding the server-side `join_secret`.
- **display device** — a cheap per-table device (`table_display_device`) paired once, showing the rotating join QR at `/display/{table}`. A device principal, kind `table_display`; never a person.
- **sitting** — one party's occupation of one table from first join to close (`table_sitting`). **member** — a person joined to a sitting (`table_sitting_member`).
- **living order** — the single `guest_order` a member has within a sitting.
- **line** — one ordered item instance, identified by `order_line_identifier`, created by a line-added operation. **pending** — added, not fulfilled, not removed. **fulfilled** — kitchen marked it prepared and dispatched. **removed** — terminal.
- **event** — one append-only `order_event` row. **operation** — one row in a typed operation table owned by an event. **send / batch** — a guest pressing Send, producing exactly one `guest_submission` event carrying all staged operations.
- **join token** — the rotating HMAC value in the QR URL. **join grant** — the 10-minute encrypted cookie a valid token is exchanged for. **pairing code** — the one-time code that binds a display device to a table.
- **origin** — `RESTAURANT_PUBLIC_ORIGIN`, the single public base URL (scheme + host [+ port]); drives WebAuthn RP ID and all QR URLs.

## 1. Architecture overview

One ASP.NET Core **Blazor Server** application (.NET 10, interactive server render mode) serves five routed areas — `/table`, `/kitchen`, `/counter`, `/administration`, `/display` — against one PostgreSQL database via **Dapper** (Entity Framework is forbidden). Schema evolution is DbUp SQL scripts executed at startup (ADR-0012). Live UI updates ride each user's Blazor circuit, fed by a single in-process broadcaster (§9); there is no Redis and no external bus in v1 (ADR-0006). Identity uses ASP.NET Core Identity core services over custom Dapper stores (ADR-0003) with Argon2id password hashing (ADR-0008) and .NET 10 built-in WebAuthn passkeys. The canonical runtime is rootless Podman Compose (ADR-0004); the production public origin is a Cloudflare **named tunnel** on the owner's stable domain (ADR-0005). Everything is instrumented with OpenTelemetry (§12). License: AGPL-3.0-only; all dependencies free/libre.

Order state is an append-only event log with fully relational typed operation tables and projection views (ADR-0002, ADR-0007); §8 is the schema of record.

## 2. Repository layout

```
myrestaurant/
├── src/
│   ├── MyRestaurant.Domain/              # pure domain: projections, validation, token algorithm, id factory
│   ├── MyRestaurant.DataAccess/          # Dapper repositories, Identity stores, Migrations/ (embedded .sql)
│   └── MyRestaurant.WebApplication/      # Blazor Server app: areas, auth, broadcaster, background services
├── tests/
│   ├── MyRestaurant.Domain.Tests/
│   ├── MyRestaurant.DataAccess.Tests/    # integration tests against real PostgreSQL (Testcontainers or compose)
│   ├── MyRestaurant.WebApplication.Tests/
│   └── MyRestaurant.EndToEnd.Tests/      # Playwright, scenarios in §16.3
├── docs/                                  # this bundle
├── scripts/                               # backup.sh, restore.sh, tunnel setup helpers
├── compose.yaml                           # canonical; profiles: (default/dev), production
├── Containerfile
├── .dockerignore                          # the build context, as an allow-list (§14.1a)
├── Caddyfile                              # dev TLS; optional staff-LAN fallback
├── run.sh                                 # dev entry: compose up + dotnet watch, see §14.4
├── export.sh                              # repo → dump.txt exporter (review tooling)
├── CONTRIBUTING.md · LICENSE · README.md
```

Dependency direction: `WebApplication → DataAccess → Domain`. `Domain` references nothing but the BCL.

## 3. Identity, authentication, authorization

### 3.1 Identity core over Dapper stores

Register ASP.NET Core Identity **core services** (not the EF default UI/stores) with custom stores in `MyRestaurant.DataAccess` implementing at minimum: `IUserStore`, `IUserPasswordStore`, `IUserSecurityStampStore`, `IUserLockoutStore`, `IUserTwoFactorStore`, `IUserAuthenticatorKeyStore`, `IUserTwoFactorRecoveryCodeStore`, `IUserPasskeyStore` (the .NET 10 passkey store abstraction), over the `person*` tables in §8. Usernames are `citext`, unique, 3–64 characters (enforced by CHECK and by validator). `security_stamp` is a `uuid` regenerated on every credential or role change. Cookie auth: Secure, HttpOnly, SameSite=Lax, 24-hour sliding expiration, security-stamp revalidation interval **5 minutes** — so resets, role revocations, and deactivations bite live sessions within minutes. Lockout: **5** consecutive failures (password, TOTP, or recovery code all count) locks for **5 minutes**; sign-in pages surface remaining-lockout messaging without revealing whether the username exists.

### 3.2 Password hashing — Argon2id (ADR-0008)

Custom `IPasswordHasher<Person>`; Identity's PBKDF2 hasher is not registered. Algorithm **Argon2id** via Konscious.Security.Cryptography.Argon2 (MIT). Parameters from environment with defaults `ARGON2_MEMORY_KIBIBYTES=65536`, `ARGON2_ITERATIONS=3`, `ARGON2_PARALLELISM=1`; salt 16 bytes CSPRNG per hash; tag 32 bytes. Stored as a PHC string in `person.password_hash`:

```
$argon2id$v=19$m=65536,t=3,p=1$<base64-no-pad(salt)>$<base64-no-pad(tag)>
```

Verification parses the **stored** parameters, recomputes, compares with `CryptographicOperations.FixedTimeEquals`, and returns `SuccessRehashNeeded` when stored parameters differ from configured ones (Identity then rehashes transparently at sign-in). A process-wide `SemaphoreSlim(ARGON2_MAX_CONCURRENT_HASHES=4)` bounds concurrent computations (~64 MiB each); excess queue. **Startup floor guard:** the application must fail fast (log + non-zero exit) if `ARGON2_MEMORY_KIBIBYTES < 19456`, `ARGON2_ITERATIONS < 2`, or `ARGON2_PARALLELISM < 1`. Password policy: minimum length 12, no composition rules, no expiry.

### 3.3 Passkeys

.NET 10 Identity WebAuthn. RP ID = the host the browser is actually on, derived **per request** from `Request.Host` (`IdentityPasskeyOptions.ServerDomain` is left **null**; ADR-0005), full host and not the registrable domain. `PublicOriginMiddleware` normalizes `Request.Host` to the real public host (the unforgeable `Origin` header when it is trusted, else the configured `RESTAURANT_PUBLIC_ORIGIN` host), and `IdentityPasskeyOptions.ValidateOrigin` gates the browser's signed origin against the trust set — `RESTAURANT_PUBLIC_ORIGIN`, any `RESTAURANT_TRUSTED_ORIGIN_PATTERNS` entry (default `https://*.trycloudflare.com`), and loopback in dev. Registration options: `residentKey=preferred`, `userVerification=preferred`, `attestation=none`. Sign-in supports username-first and username-less (discoverable credential) flows. Store per §8 `passkey_credential` (credential id unique, public key, sign counter, optional transports and label). Fallback library if a framework gap is found: fido2-net-lib (record the fallback by editing ADR-0003). Registration and change-password forms follow platform conventions — `autocomplete="username"` / `autocomplete="new-password"` — so operating-system password managers generate and offer strong passwords automatically. Passkey enrollment is offered to guests after registration and after sign-in as a **dismissible nudge**: always offered, never required, never a gate for guests (the grant-time passkey mandate applies only to the kitchen and administrator roles, §3.7). **Consequence of per-request RP derivation (ADR-0005):** passkeys work on whatever origin the browser is on, including a Cloudflare quick tunnel (`*.trycloudflare.com`). The only quick-tunnel caveat is *persistence*: `*.trycloudflare.com` is a Public Suffix List entry and each run gets a fresh random subdomain, so a passkey registered on one run's URL binds to that URL and must be re-registered on the next run. The named tunnel is the durable production origin (a stable domain so passkeys persist across runs), not a prerequisite for passkeys to function; password + TOTP is the durable baseline for accounts that must survive a URL change.

### 3.4 TOTP and recovery codes

RFC 6238: SHA-1, 6 digits, 30-second step, ±1 step skew. Secret 20 random bytes; provisioning URI (`otpauth://totp/{RESTAURANT_NAME}:{username}?secret={base32}&issuer={RESTAURANT_NAME}`) rendered as a server-side SVG QR at enrollment; enrollment confirmed by one valid code. Secret stored **encrypted with ASP.NET Data Protection** in `person.totp_secret_protected`; the Data Protection key ring persists to the `DATA_PROTECTION_KEYS_DIRECTORY` volume (losing it invalidates TOTP secrets and cookies — see OPERATIONS §8). Enrollment state == `totp_secret_protected IS NOT NULL`; there is **no** `totp_required` column. Ten single-use recovery codes generated at enrollment (and on regeneration), stored hashed (SHA-256), usable **only** on the password path in place of a TOTP code; `recovery_code_used` / `recovery_codes_regenerated` security events recorded.

### 3.5 Sign-in flows and the post-authentication obligations pipeline (ADR-0010)

**Password path:** username + password → if account has TOTP enrolled, challenge for TOTP or recovery code → success. **Passkey path:** WebAuthn assertion → success, **never** a TOTP challenge. Both paths then run the **obligations pipeline** before any destination: (1) `must_change_password` → forced password-change page (sets new password, clears flag, `forced_password_change_completed` event); (2) `must_enroll_totp` → forced TOTP enrollment (QR, confirm code, fresh recovery codes, clears flag, `forced_totp_enrollment_completed` event); (3) continue to the originally requested URL or role-appropriate home. The pipeline must be enforced by an authorization filter/middleware so no authenticated endpoint (except sign-out and the pipeline pages themselves) is reachable while a flag is set. Every sign-in attempt records `sign_in_succeeded` / `sign_in_failed` (with method tag in the metric, §12) and lockouts record `account_locked_out`.

### 3.6 First-administrator bootstrap

`/setup` is reachable only while **zero administrators exist**. The wizard collects username/display name, registers a **passkey**, enrolls **TOTP** (with recovery codes), then grants `administrator` (recording the new administrator as their own grantor — `granted_by_person_identifier` self-references, satisfying its NOT NULL constraint) — all committed in **one transaction** that first takes `pg_advisory_xact_lock(hashtext('myrestaurant_setup'))` and re-checks the zero-administrator condition under the lock (two racing browsers: one wins, the other sees 404 on retry). After any administrator exists, `/setup` returns 404. The wizard must not allow skipping the passkey or TOTP steps.

### 3.7 Roles, policies, capability matrix

Stored roles: `administrator`, `kitchen`, `counter` (CHECK-constrained in `person_role`; `table_display` is a device principal, never a row here). Administrative reset (per §4.5 of requirements): set temporary password; set `must_change_password`; **iff** TOTP was enrolled, delete secret + recovery codes and set `must_enroll_totp`; regenerate security stamp; write `password_reset_by_administrator` (+ `totp_cleared_by_administrator` when applicable). Deactivation (`is_active=false`) blocks sign-in and invalidates sessions via stamp; deletion does not exist (F-10b) — history must keep its actors.

Area policies: `/table` any authenticated person (membership checked per sitting); `/kitchen` role kitchen or administrator; `/counter` role counter or administrator; `/administration` administrator; `/display/{table}` a non-revoked device principal whose table claim matches `{table}`; `/display/pair` anonymous, rate-limited (§4.2).

**Capability matrix** (server-enforced in the order transaction, §6.5; UI merely mirrors it):

| Capability | guest (owner) | kitchen | counter | administrator |
|---|---|---|---|---|
| Send batch (`guest_submission`): add lines; remove **own pending** lines | ✔ (open sitting, member, own order) | — | — | — (admins dining act as guests on their own order, `actor_role='guest'`) |
| `staff_edit`: add/remove any line | — | ✔ | ✔ | ✔ |
| `price_adjustment` (reason required) | — | — | ✔ | ✔ |
| `fulfillment` / `fulfillment_reversal` | — | ✔ | — | ✔ |
| Activate/deactivate menu item | — | ✔ | ✔ | ✔ |
| Create/edit menu items | — | — | — | ✔ |
| Close & settle sitting; end-of-day batch close | — | — | ✔ | ✔ |
| Show rotating join QR for a table (fallback) | — | — | ✔ | ✔ |
| Hide own order / unhide any order | ✔ / — | — | — | — / ✔ |
| Pair & revoke display devices; rotate `join_secret` | — | — | — | ✔ |
| Users, roles, resets; post-close corrective events | — | — | — | ✔ |

## 4. Tables, display devices, and join tokens (ADR-0009)

### 4.1 `restaurant_table` and the join secret

Each table row holds `join_secret bytea CHECK (octet_length(join_secret) = 32)`, generated with a CSPRNG at table creation, **never sent to any client** (displays receive a rendered SVG QR over their circuit). Administrators may **rotate** the secret at any time (new 32 bytes, `join_secret_rotated_at` stamped): every outstanding token for that table dies instantly. Deactivating a table (`is_active=false`) stops token validation and display rendering for it.

### 4.2 Display devices and pairing

`table_display_device`: bound to one table; authenticated by a device cookie whose value is `device:{device_identifier}:{secret}` where `secret` is 32 random bytes Base64Url; the server stores only `sha256(secret)` (`device_secret_hash`). Cookie: Secure, HttpOnly, SameSite=Lax, expiry ~365 days. Each request re-validates the hash and `revoked_at IS NULL`; `last_seen_at` is updated at most once per minute. Revocation (`revoked_at`, `revoked_by_person_identifier`) kills the device on its next request or circuit revalidation.

Pairing: administrator, from the table's admin page, generates a one-time code — 8 characters from the unambiguous alphabet `ABCDEFGHJKMNPQRSTUVWXYZ23456789`, stored **hashed** (SHA-256) in `table_display_pairing_code` with `expires_at = now() + TABLE_DISPLAY_PAIRING_CODE_MINUTES` (default 10), single-use (`used_at`). The device opens `/display/pair` (anonymous; rate-limited **5 attempts/minute/IP**), enters the code; on match the server creates the device row, sets the cookie, marks the code used, and redirects to `/display/{table}`. Failed attempts burn nothing but the rate budget.

### 4.3 Token algorithm (normative)

```
rotation      = TABLE_JOIN_TOKEN_ROTATION_SECONDS            -- default 60
window_index  = floor(unix_time_seconds / rotation)
message       = UTF8( lowercase-hyphenated-table-uuid + ":" + decimal(window_index) )
token         = Base64Url( HMAC_SHA256( join_secret, message ) )   -- full 32 bytes, no padding
url           = {RESTAURANT_PUBLIC_ORIGIN}/table/{table_identifier}?token={token}
```

Validation: recompute for `window_index` and `window_index − 1`; accept iff either matches by `CryptographicOperations.FixedTimeEquals`. Worst-case token life = 2 × rotation (default 120 s). Rotation is continuous and **independent of sitting state**. Every validation increments `table_join_tokens_validated_total{result=valid|expired|invalid}` — `expired` when the token matches some window older than the previous (recompute a bounded lookback of, say, 10 windows purely for metric labeling; anything else is `invalid`). QR is rendered **server-side as SVG**; the display re-renders on a server timer aligned to the window boundary (fire at `(window_index+1) × rotation` UTC).

### 4.4 Join flow and grants

`GET /table/{id}?token=…` for a non-member: validate token → on success, issue the **join grant** — a Data-Protection-encrypted cookie `{table_identifier, issued_at}`, TTL `TABLE_JOIN_GRANT_MINUTES` (default 10) — and continue to sign-in/registration if anonymous, else to the join confirmation. The join action (post-auth) requires a valid, matching grant; it opens a sitting if none is open on that table, inserts membership, **consumes the grant** (cookie cleared), and broadcasts `SittingMemberJoined`. Invalid/absent token for a non-member → friendly "this code has expired — please scan the table display again" page (HTTP 200, no oracle detail). **Members bypass tokens entirely:** `/table/{id}` with an authenticated member of that table's open sitting renders the order surface regardless of query string. Registration mid-flow: the grant cookie survives the passkey ceremony; that is its purpose.

### 4.5 Counter fallback

Counter and administration surfaces can render, on demand per table, the **same** rotating QR (same server-side generation; secret never leaves the server). This is the operational fallback when a table's display is dead. There is no printed QR and no human-readable short-code path in v1.

## 5. Sittings

### 5.1 Open and membership

First consumed grant on a table with no open sitting creates `table_sitting` (opened_at) and the first membership atomically. Later grants add members (`UNIQUE (table_sitting_identifier, person_identifier)` makes double-join idempotent). A person may hold memberships in multiple open sittings; the UI scopes to the sitting behind the current `/table/{id}` route.

### 5.2 Visibility while open

Members see the party roster (display names), every member's living order with per-line states, and the running per-person and table totals (from `sitting_bill`, §8.3). Kitchen sees pending lines for all open sittings; counter sees bills for all open sittings.

### 5.3 Close and settle

Counter or administrator. In one transaction: `SELECT … FOR UPDATE` the sitting row; verify `closed_at IS NULL`; compute the settled total as the sum over `sitting_bill` for the sitting **under that lock** (concurrent order writers hold `FOR SHARE` on the sitting and are excluded — §6.6); stamp `closed_at`, `closed_by_person_identifier`, `settled_total_amount`; commit; broadcast `SittingClosed`. The counter UI must surface still-pending lines prominently before offering Close (remove with reason, or knowingly charge). `settled_total_amount` is **never rewritten**; post-close corrections (§6.7) live beside it, and the UI shows both the stamped settled total and, when corrective events exist, the current corrected total.

### 5.4 End of day

Administration provides batch close: list open sittings with last-activity timestamps, select, close each via the same §5.3 transaction.

## 6. Orders — the living-order event model (ADR-0002, ADR-0007)

### 6.1 Living order

Exactly one `guest_order` per (sitting, member): `UNIQUE (table_sitting_identifier, person_identifier)`. Created lazily inside the member's first send transaction; a lost creation race (unique violation) is re-read and proceeds.

### 6.2 Events

`order_event`: per-order monotonic `sequence_number` (1, 2, 3… assigned under the order lock), `event_type` ∈ `guest_submission | staff_edit | price_adjustment | fulfillment | fulfillment_reversal`, `actor_person_identifier`, `actor_role` ∈ `guest | kitchen | counter | administrator`, `occurred_at`. Same-row CHECKs bind type→role (schema §8.2): guest_submission→guest; staff_edit→kitchen/counter/administrator; price_adjustment→counter/administrator; fulfillment and fulfillment_reversal→kitchen/administrator. `UNIQUE (guest_order_identifier, sequence_number)` and `UNIQUE (order_event_identifier, event_type)` (the composite-FK target for subtype enforcement).

### 6.3 Operations

Typed operation tables, each with a uniform surrogate `uuid` PK, a redundant CHECK-constrained `event_type`, and a composite FK `(order_event_identifier, event_type) → order_event`:

| Table | Allowed event types | Payload |
|---|---|---|
| `order_operation_line_added` | guest_submission, staff_edit | `order_line_identifier` (UNIQUE — the line's identity), `menu_item_identifier`, `quantity` 1–100, `unit_price_amount` (captured at add), `customization_note` NULL |
| `order_operation_line_removed` | guest_submission, staff_edit | `order_line_identifier` (UNIQUE — removal terminal), `reason` NULL |
| `order_operation_line_price_adjusted` | price_adjustment | `order_line_identifier`, `new_unit_price_amount`, `reason` NOT NULL |
| `order_operation_line_fulfilled` | fulfillment | `order_line_identifier` |
| `order_operation_line_fulfillment_reverted` | fulfillment_reversal | `order_line_identifier` |

A guest send is one `guest_submission` event owning N added + M removed rows. Staff UIs send one operation per event typically, but the model permits multi-operation staff events (e.g. kitchen "fulfill all pending for this order" = one `fulfillment` event, N fulfilled rows).

### 6.4 Line lifecycle

pending → fulfilled (revertible, roll-forward) → …; removed is terminal from either state (guests only from pending, staff from any). Fulfillment state of a line = the **latest by parent sequence_number** of its fulfilled/reverted operations (fulfilled if that latest is a fulfilled row). Removed = a removal row exists. Re-adding after removal = a new line, new identifier.

### 6.5 Validation invariants (application-enforced inside the serialized transaction; integration-tested)

1. Every event owns ≥ 1 operation row.
2. Every referenced `order_line_identifier` belongs to **this** order (its adding event's `guest_order_identifier` matches).
3. A removal may not target a line already removed (DB also enforces via UNIQUE) **or** — for guest actors — a line that is currently fulfilled or not their own… (guest sends may only remove lines whose adding event was their own `guest_submission` and which are currently pending).
4. A guest_submission requires: actor is the order owner, is a member of the sitting, sitting open; each added `menu_item_identifier` exists and `is_active` **re-checked in this transaction**; quantity 1–100; `unit_price_amount` set server-side from the current menu price (client-sent prices are ignored).
5. A removal operation may not reference a line added in the same event.
6. `fulfillment` targets currently-pending, non-removed lines; `fulfillment_reversal` targets currently-fulfilled lines (fulfilled/reverted must alternate per line).
7. `price_adjustment` targets non-removed lines; reason non-empty.
8. Post-close (sitting closed): only administrators, only event types staff_edit / price_adjustment / fulfillment / fulfillment_reversal — never guest_submission.
9. **All-or-nothing:** any failed operation rejects the entire event; the response carries per-operation error reasons plus a fresh projection so the client restages.

### 6.6 Locking protocol (normative)

Every order-mutating transaction: (a) `SELECT … FOR SHARE` the `table_sitting` row and verify it is open (post-close administrative corrections skip the open check but still take `FOR SHARE`); (b) `SELECT … FOR UPDATE` the `guest_order` row (creating it first if absent — `INSERT … ON CONFLICT DO NOTHING` then re-select FOR UPDATE); (c) read `max(sequence_number)` for the order and assign +1; (d) validate §6.5 against the projection under the lock; (e) insert the event + operations (+ `kitchen_notification` `initial` when §10.1 says so) in the same transaction; (f) commit; (g) broadcast after commit. The close transaction takes `FOR UPDATE` on the sitting (§5.3); FOR SHARE vs FOR UPDATE conflict is what guarantees no event slips past a close and no close computes a total while a write is in flight.

### 6.7 Post-close corrections

Administrator-only appended events per §6.5(8), fully visible in history views next to the stamped settled total.

### 6.8 Hiding

`order_visibility_event` (owner hides; only an administrator unhides). Hiding applies to an order in a **closed** sitting and removes it from the **owner's own views** — their personal history — and changes **no other party's view**: cross-member history is never shown in the first place (§11.1), and kitchen/counter operational lookups and administration always see everything. There is **no user-facing unhide**; the confirmation dialog states plainly that this cannot be undone from the guest's account. Administrators locate hidden orders in the **hidden-records view** (§11.4): every currently-hidden order system-wide, filterable by username, date range, and table, each row expandable to the complete stored record — full event log, visibility log, sitting context, unprojected — with a per-record Unhide (appends `unhidden_by_administrator`). Current flag = latest event (view `order_visibility_current`).

## 7. Menu

`menu_item` (name, `price_amount numeric(10,2) ≥ 0`, `is_active`) with append-only `menu_item_event` mirroring every change (`created | name_changed | price_changed | activated | deactivated`, typed nullable payload columns CHECK-bound to type, actor, timestamp). Create/edit is administrator; activate/deactivate is kitchen/counter/administrator and takes effect instantly (broadcast `MenuChanged`; guest staging areas mark newly-inactive staged items and the send re-validates server-side regardless). Prices on existing lines never move when the menu price changes (§6.5.4 capture rule). Deactivated items are **not hidden** from the guest menu: they remain visible, marked "currently unavailable", and cannot be added to a send — the guest sees that the salmon exists and is out, rather than watching it silently vanish. Customization notes are free text and are never validated against any rules engine; an impossible request ("eggless omelette") is handled by a human walking to the table.

**Sections and item descriptions are decided and not yet implemented.** A menu has headings and each item under a heading has a sentence explaining it; `menu_item` has four columns and expresses neither, which is the first enhancement request this project received from somebody shown the running application. **ADR-0014** carries the rulings — `menu_section` as a table, `menu_item.menu_section_identifier` `NOT NULL` so every item is in exactly one, `citext NOT NULL UNIQUE` on a section name where an item name is deliberately neither, explicit non-unique `display_order` on both, `description text NOT NULL DEFAULT ''` because a paired CHECK cannot bind an optional payload, and three new `menu_item_event` types beside a new `menu_section_event` — and Stage 2 of `docs/MENU_AND_HANDHELD_PLAN.md` carries the migration and the file list. **This section and §8.2 are edited in the commit that implements it**, per §18; until then the schema of record below is the schema, and nothing reads a section.

Two existing rules survive that change and are restated here because both are easy to lose while rewriting a menu surface. A deactivated item stays visible **under its section heading** — which reads better than it does in a flat list, not worse. And **deactivating a section does not deactivate its items:** an inactive section is not rendered to the guest, its items keep their own `is_active`, and reactivating it brings the menu back exactly as it was. Cascading the flag downward would silently rewrite every item's availability and lose which of them the kitchen had 86'd, which is F-10b's mistake in a new place.

## 8. Database schema (schema of record)

### 8.1 Conventions

PostgreSQL, current major. Extension: `citext`. All identifiers snake_case, unabbreviated (carve-out per requirements §8: TOTP/HMAC/QR/URL/SQL/TLS). Primary keys `uuid` named `{table}_identifier`, application-generated UUIDv7 (ADR-0011) — **no database defaults for identifiers**. Timestamps `timestamptz`, UTC, named `…_at`; **rendered in `RESTAURANT_TIME_ZONE` — never in the reader's zone, and never in the server process's.** A restaurant is a physical place in one IANA zone: a guest abroad reading last week's bill wants the times the meal happened at, and a kitchen ticket must agree with the counter's bill to the minute on every screen in the building. One type performs that conversion — `WebApplication/Time/RestaurantTime` — and nothing else may call `ToLocalTime()`, whose answer is the container's `TZ` (unset, therefore UTC). Its formats are explicit and invariant-cultured for the same reason `MoneyText` refuses `"C"`; the one real choice, 12- versus 24-hour, is `RESTAURANT_CLOCK_FORMAT` (§13) rather than an accident of the base image. Money `numeric(10,2)`. The DDL below ships verbatim as `src/MyRestaurant.DataAccess/Migrations/0001_initial_schema.sql` (plus `CREATE EXTENSION IF NOT EXISTS citext;` at top).

### 8.2 Tables (DDL)

```sql
CREATE EXTENSION IF NOT EXISTS citext;

CREATE TABLE person (
    person_identifier        uuid PRIMARY KEY,
    username                 citext NOT NULL UNIQUE
                             CHECK (char_length(username) BETWEEN 3 AND 64),
    display_name             text NULL,
    email_address            citext NULL,        -- optional; manual escalation only (§11.1)
    phone_number             text NULL,          -- optional; manual escalation only (§11.1)
    password_hash            text NULL,          -- PHC argon2id string (§3.2)
    totp_secret_protected    text NULL,          -- Data-Protection-encrypted; NULL = not enrolled
    must_change_password     boolean NOT NULL DEFAULT false,
    must_enroll_totp         boolean NOT NULL DEFAULT false,
    security_stamp           uuid NOT NULL,
    failed_access_count      integer NOT NULL DEFAULT 0,
    lockout_end_at           timestamptz NULL,
    is_active                boolean NOT NULL DEFAULT true,
    created_at               timestamptz NOT NULL
);

CREATE TABLE person_role (
    person_role_identifier       uuid PRIMARY KEY,
    person_identifier            uuid NOT NULL REFERENCES person (person_identifier),
    role_name                    text NOT NULL
                                 CHECK (role_name IN ('administrator', 'kitchen', 'counter')),
    granted_by_person_identifier uuid NOT NULL REFERENCES person (person_identifier),
    granted_at                   timestamptz NOT NULL,
    UNIQUE (person_identifier, role_name)
);

CREATE TABLE passkey_credential (
    passkey_credential_identifier uuid PRIMARY KEY,
    person_identifier             uuid NOT NULL REFERENCES person (person_identifier),
    credential_id                 bytea NOT NULL UNIQUE,
    public_key                    bytea NOT NULL,
    signature_counter             bigint NOT NULL DEFAULT 0,
    transports                    text NULL,
    credential_display_name       text NULL,
    created_at                    timestamptz NOT NULL
);

CREATE TABLE totp_recovery_code (
    totp_recovery_code_identifier uuid PRIMARY KEY,
    person_identifier             uuid NOT NULL REFERENCES person (person_identifier),
    code_hash                     bytea NOT NULL,       -- sha256
    used_at                       timestamptz NULL,
    created_at                    timestamptz NOT NULL
);
CREATE INDEX totp_recovery_code_person_index ON totp_recovery_code (person_identifier);

CREATE TABLE person_address (
    person_address_identifier uuid PRIMARY KEY,
    person_identifier         uuid NOT NULL REFERENCES person (person_identifier),
    label                     text NOT NULL,      -- always free text, chosen by the user ("Home", "Work")
    street_line_one           text NULL,
    street_line_two           text NULL,
    city                      text NULL,
    region                    text NULL,
    postal_code               text NULL,
    country                   text NULL,
    created_at                timestamptz NOT NULL
);
CREATE INDEX person_address_person_index ON person_address (person_identifier);
-- Deliberate scaffolding for a possible future delivery/takeout feature (REQUIREMENTS §4.6):
-- consumed by nothing in version 1, and not to be removed as dead weight.

CREATE TABLE security_event (
    security_event_identifier uuid PRIMARY KEY,
    subject_person_identifier uuid NOT NULL REFERENCES person (person_identifier),
    actor_person_identifier   uuid NULL REFERENCES person (person_identifier), -- NULL = the subject themselves / system
    event_type                text NOT NULL CHECK (event_type IN (
        'account_created', 'account_deactivated', 'account_reactivated',
        'password_changed', 'password_reset_by_administrator',
        'forced_password_change_completed',
        'totp_enrolled', 'totp_removed', 'totp_cleared_by_administrator',
        'forced_totp_enrollment_completed',
        'recovery_code_used', 'recovery_codes_regenerated',
        'passkey_registered', 'passkey_removed',
        'role_granted', 'role_revoked',
        'sign_in_succeeded', 'sign_in_failed', 'account_locked_out')),
    occurred_at               timestamptz NOT NULL
);
CREATE INDEX security_event_subject_index ON security_event (subject_person_identifier, occurred_at);

CREATE TABLE restaurant_table (
    restaurant_table_identifier uuid PRIMARY KEY,
    label                       text NOT NULL UNIQUE,
    join_secret                 bytea NOT NULL CHECK (octet_length(join_secret) = 32),
    join_secret_rotated_at      timestamptz NULL,
    is_active                   boolean NOT NULL DEFAULT true,
    created_at                  timestamptz NOT NULL
);

CREATE TABLE table_display_device (
    table_display_device_identifier uuid PRIMARY KEY,
    restaurant_table_identifier     uuid NOT NULL REFERENCES restaurant_table (restaurant_table_identifier),
    device_label                    text NOT NULL,
    device_secret_hash              bytea NOT NULL CHECK (octet_length(device_secret_hash) = 32), -- sha256
    paired_by_person_identifier     uuid NOT NULL REFERENCES person (person_identifier),
    paired_at                       timestamptz NOT NULL,
    revoked_at                      timestamptz NULL,
    revoked_by_person_identifier    uuid NULL REFERENCES person (person_identifier),
    last_seen_at                    timestamptz NULL,
    CHECK ((revoked_at IS NULL) = (revoked_by_person_identifier IS NULL))
);
CREATE INDEX table_display_device_table_index ON table_display_device (restaurant_table_identifier);

CREATE TABLE table_display_pairing_code (
    table_display_pairing_code_identifier uuid PRIMARY KEY,
    restaurant_table_identifier           uuid NOT NULL REFERENCES restaurant_table (restaurant_table_identifier),
    code_hash                             bytea NOT NULL CHECK (octet_length(code_hash) = 32), -- sha256
    created_by_person_identifier          uuid NOT NULL REFERENCES person (person_identifier),
    created_at                            timestamptz NOT NULL,
    expires_at                            timestamptz NOT NULL,
    used_at                               timestamptz NULL
);

CREATE TABLE table_sitting (
    table_sitting_identifier    uuid PRIMARY KEY,
    restaurant_table_identifier uuid NOT NULL REFERENCES restaurant_table (restaurant_table_identifier),
    opened_at                   timestamptz NOT NULL,
    closed_at                   timestamptz NULL,
    closed_by_person_identifier uuid NULL REFERENCES person (person_identifier),
    settled_total_amount        numeric(10,2) NULL,
    CHECK ((closed_at IS NULL) = (closed_by_person_identifier IS NULL)),
    CHECK ((closed_at IS NULL) = (settled_total_amount IS NULL))
);
-- at most one open sitting per table:
CREATE UNIQUE INDEX table_sitting_one_open_per_table
    ON table_sitting (restaurant_table_identifier) WHERE closed_at IS NULL;
CREATE INDEX table_sitting_table_index ON table_sitting (restaurant_table_identifier, opened_at);

CREATE TABLE table_sitting_member (
    table_sitting_member_identifier uuid PRIMARY KEY,
    table_sitting_identifier        uuid NOT NULL REFERENCES table_sitting (table_sitting_identifier),
    person_identifier               uuid NOT NULL REFERENCES person (person_identifier),
    joined_at                       timestamptz NOT NULL,
    UNIQUE (table_sitting_identifier, person_identifier)
);

CREATE TABLE menu_item (
    menu_item_identifier uuid PRIMARY KEY,
    name                 text NOT NULL,
    price_amount         numeric(10,2) NOT NULL CHECK (price_amount >= 0),
    is_active            boolean NOT NULL DEFAULT true,
    created_at           timestamptz NOT NULL
);

CREATE TABLE menu_item_event (
    menu_item_event_identifier uuid PRIMARY KEY,
    menu_item_identifier       uuid NOT NULL REFERENCES menu_item (menu_item_identifier),
    actor_person_identifier    uuid NOT NULL REFERENCES person (person_identifier),
    event_type                 text NOT NULL CHECK (event_type IN
                               ('created', 'name_changed', 'price_changed', 'activated', 'deactivated')),
    new_name                   text NULL,
    new_price_amount           numeric(10,2) NULL CHECK (new_price_amount IS NULL OR new_price_amount >= 0),
    occurred_at                timestamptz NOT NULL,
    CHECK ((new_name IS NOT NULL)         = (event_type IN ('created', 'name_changed'))),
    CHECK ((new_price_amount IS NOT NULL) = (event_type IN ('created', 'price_changed')))
);
CREATE INDEX menu_item_event_item_index ON menu_item_event (menu_item_identifier, occurred_at);

CREATE TABLE guest_order (
    guest_order_identifier   uuid PRIMARY KEY,
    table_sitting_identifier uuid NOT NULL REFERENCES table_sitting (table_sitting_identifier),
    person_identifier        uuid NOT NULL REFERENCES person (person_identifier),
    created_at               timestamptz NOT NULL,
    UNIQUE (table_sitting_identifier, person_identifier)
);

CREATE TABLE order_event (
    order_event_identifier  uuid PRIMARY KEY,
    guest_order_identifier  uuid NOT NULL REFERENCES guest_order (guest_order_identifier),
    sequence_number         bigint NOT NULL CHECK (sequence_number >= 1),
    event_type              text NOT NULL CHECK (event_type IN
        ('guest_submission', 'staff_edit', 'price_adjustment', 'fulfillment', 'fulfillment_reversal')),
    actor_person_identifier uuid NOT NULL REFERENCES person (person_identifier),
    actor_role              text NOT NULL CHECK (actor_role IN
        ('guest', 'kitchen', 'counter', 'administrator')),
    occurred_at             timestamptz NOT NULL,
    UNIQUE (guest_order_identifier, sequence_number),
    UNIQUE (order_event_identifier, event_type),   -- composite-FK target for subtype enforcement
    CHECK (event_type <> 'guest_submission'    OR actor_role = 'guest'),
    CHECK (event_type <> 'staff_edit'          OR actor_role IN ('kitchen', 'counter', 'administrator')),
    CHECK (event_type <> 'price_adjustment'    OR actor_role IN ('counter', 'administrator')),
    CHECK (event_type <> 'fulfillment'         OR actor_role IN ('kitchen', 'administrator')),
    CHECK (event_type <> 'fulfillment_reversal' OR actor_role IN ('kitchen', 'administrator'))
);
CREATE INDEX order_event_order_index ON order_event (guest_order_identifier, sequence_number);

CREATE TABLE order_operation_line_added (
    order_operation_line_added_identifier uuid PRIMARY KEY,
    order_event_identifier                uuid NOT NULL,
    event_type                            text NOT NULL
        CHECK (event_type IN ('guest_submission', 'staff_edit')),
    order_line_identifier                 uuid NOT NULL UNIQUE,   -- the line's identity
    menu_item_identifier                  uuid NOT NULL REFERENCES menu_item (menu_item_identifier),
    quantity                              integer NOT NULL CHECK (quantity BETWEEN 1 AND 100),
    unit_price_amount                     numeric(10,2) NOT NULL CHECK (unit_price_amount >= 0),
    customization_note                    text NULL,
    FOREIGN KEY (order_event_identifier, event_type)
        REFERENCES order_event (order_event_identifier, event_type)
);
CREATE INDEX order_operation_line_added_event_index
    ON order_operation_line_added (order_event_identifier);

CREATE TABLE order_operation_line_removed (
    order_operation_line_removed_identifier uuid PRIMARY KEY,
    order_event_identifier                  uuid NOT NULL,
    event_type                              text NOT NULL
        CHECK (event_type IN ('guest_submission', 'staff_edit')),
    order_line_identifier                   uuid NOT NULL UNIQUE   -- removal is terminal
        REFERENCES order_operation_line_added (order_line_identifier),
    reason                                  text NULL,
    FOREIGN KEY (order_event_identifier, event_type)
        REFERENCES order_event (order_event_identifier, event_type)
);
CREATE INDEX order_operation_line_removed_event_index
    ON order_operation_line_removed (order_event_identifier);

CREATE TABLE order_operation_line_price_adjusted (
    order_operation_line_price_adjusted_identifier uuid PRIMARY KEY,
    order_event_identifier                         uuid NOT NULL,
    event_type                                     text NOT NULL
        CHECK (event_type = 'price_adjustment'),
    order_line_identifier                          uuid NOT NULL
        REFERENCES order_operation_line_added (order_line_identifier),
    new_unit_price_amount                          numeric(10,2) NOT NULL CHECK (new_unit_price_amount >= 0),
    reason                                         text NOT NULL CHECK (btrim(reason) <> ''),
    FOREIGN KEY (order_event_identifier, event_type)
        REFERENCES order_event (order_event_identifier, event_type)
);
CREATE INDEX order_operation_line_price_adjusted_line_index
    ON order_operation_line_price_adjusted (order_line_identifier);

CREATE TABLE order_operation_line_fulfilled (
    order_operation_line_fulfilled_identifier uuid PRIMARY KEY,
    order_event_identifier                    uuid NOT NULL,
    event_type                                text NOT NULL CHECK (event_type = 'fulfillment'),
    order_line_identifier                     uuid NOT NULL
        REFERENCES order_operation_line_added (order_line_identifier),
    FOREIGN KEY (order_event_identifier, event_type)
        REFERENCES order_event (order_event_identifier, event_type)
);
CREATE INDEX order_operation_line_fulfilled_line_index
    ON order_operation_line_fulfilled (order_line_identifier);

CREATE TABLE order_operation_line_fulfillment_reverted (
    order_operation_line_fulfillment_reverted_identifier uuid PRIMARY KEY,
    order_event_identifier                                uuid NOT NULL,
    event_type                                            text NOT NULL
        CHECK (event_type = 'fulfillment_reversal'),
    order_line_identifier                                 uuid NOT NULL
        REFERENCES order_operation_line_added (order_line_identifier),
    FOREIGN KEY (order_event_identifier, event_type)
        REFERENCES order_event (order_event_identifier, event_type)
);
CREATE INDEX order_operation_line_fulfillment_reverted_line_index
    ON order_operation_line_fulfillment_reverted (order_line_identifier);

CREATE TABLE kitchen_notification (
    kitchen_notification_identifier uuid PRIMARY KEY,
    order_event_identifier          uuid NOT NULL,
    event_type                      text NOT NULL
        CHECK (event_type IN ('guest_submission', 'staff_edit')),
    kind                            text NOT NULL CHECK (kind IN ('initial', 'reminder')),
    created_at                      timestamptz NOT NULL,
    UNIQUE (order_event_identifier, kind),
    FOREIGN KEY (order_event_identifier, event_type)
        REFERENCES order_event (order_event_identifier, event_type)
);

CREATE TABLE order_visibility_event (
    order_visibility_event_identifier uuid PRIMARY KEY,
    guest_order_identifier            uuid NOT NULL REFERENCES guest_order (guest_order_identifier),
    actor_person_identifier           uuid NOT NULL REFERENCES person (person_identifier),
    event_type                        text NOT NULL CHECK (event_type IN ('hidden', 'unhidden')),
    occurred_at                       timestamptz NOT NULL
);
CREATE INDEX order_visibility_event_order_index
    ON order_visibility_event (guest_order_identifier, occurred_at);
```

Note on the `menu_item_event` CHECKs: they are biconditionals — `new_name` is present exactly when the type is `created` or `name_changed`, and `new_price_amount` exactly when the type is `created` or `price_changed`; `activated`/`deactivated` therefore carry neither. Integration tests must assert all ten combinations (five types × payload present/absent).

Schema evolution — `passkey_credential` (M2 passkey slice). The DDL above is what ships as `0001_initial_schema.sql` and is left unchanged. Implementing passkeys against ASP.NET Core Identity's .NET 10 API revealed that its `UserPasskeyInfo` credential record carries WebAuthn state the original table did not model, and that assertion (§7.2, verifying an authentication assertion, step 19) *reads* the stored backup-eligible bit and fails the ceremony on a mismatch — so it must persist. An additive migration, `0002_passkey_credential_webauthn_state.sql`, therefore adds three columns to this table: `is_user_verified boolean NOT NULL DEFAULT false`, `is_backup_eligible boolean NOT NULL DEFAULT false`, and `is_backed_up boolean NOT NULL DEFAULT false`. The raw attestation object and client-data JSON that `UserPasskeyInfo` also exposes are intentionally **not** stored (attestation is `none` per §3.3 and nothing re-reads them), and are reconstructed as empty on read. This is the framework gap §3.3 anticipated; no fallback library was required, only these columns. Recorded in the review ledger as F-34.

### 8.3 Projection views

```sql
CREATE VIEW order_current_line AS
SELECT
    added_event.guest_order_identifier,
    added.order_line_identifier,
    added.menu_item_identifier,
    menu_item.name AS menu_item_name,
    added.quantity,
    COALESCE(latest_price.new_unit_price_amount, added.unit_price_amount)
        AS current_unit_price_amount,
    added.customization_note,
    COALESCE(latest_flip.is_fulfilled, false) AS is_fulfilled,
    added_event.occurred_at AS added_at,
    added.order_event_identifier AS added_by_order_event_identifier
FROM order_operation_line_added AS added
JOIN order_event AS added_event
    ON added_event.order_event_identifier = added.order_event_identifier
JOIN menu_item
    ON menu_item.menu_item_identifier = added.menu_item_identifier
LEFT JOIN order_operation_line_removed AS removed
    ON removed.order_line_identifier = added.order_line_identifier
LEFT JOIN LATERAL (
    SELECT adjustment.new_unit_price_amount
    FROM order_operation_line_price_adjusted AS adjustment
    JOIN order_event AS adjustment_event
        ON adjustment_event.order_event_identifier = adjustment.order_event_identifier
    WHERE adjustment.order_line_identifier = added.order_line_identifier
    ORDER BY adjustment_event.sequence_number DESC
    LIMIT 1
) AS latest_price ON true
LEFT JOIN LATERAL (
    SELECT flip.was_fulfillment AS is_fulfilled
    FROM (
        SELECT true AS was_fulfillment, fulfilled_event.sequence_number
        FROM order_operation_line_fulfilled AS fulfilled
        JOIN order_event AS fulfilled_event
            ON fulfilled_event.order_event_identifier = fulfilled.order_event_identifier
        WHERE fulfilled.order_line_identifier = added.order_line_identifier
        UNION ALL
        SELECT false, reverted_event.sequence_number
        FROM order_operation_line_fulfillment_reverted AS reverted
        JOIN order_event AS reverted_event
            ON reverted_event.order_event_identifier = reverted.order_event_identifier
        WHERE reverted.order_line_identifier = added.order_line_identifier
    ) AS flip
    ORDER BY flip.sequence_number DESC
    LIMIT 1
) AS latest_flip ON true
WHERE removed.order_line_identifier IS NULL;

CREATE VIEW kitchen_pending_line AS
SELECT
    line.*,
    guest_order.table_sitting_identifier,
    guest_order.person_identifier,
    person.display_name AS person_display_name,
    table_sitting.restaurant_table_identifier,
    restaurant_table.label AS restaurant_table_label
FROM order_current_line AS line
JOIN guest_order       ON guest_order.guest_order_identifier = line.guest_order_identifier
JOIN person            ON person.person_identifier = guest_order.person_identifier
JOIN table_sitting     ON table_sitting.table_sitting_identifier = guest_order.table_sitting_identifier
JOIN restaurant_table  ON restaurant_table.restaurant_table_identifier = table_sitting.restaurant_table_identifier
WHERE table_sitting.closed_at IS NULL
  AND NOT line.is_fulfilled;

CREATE VIEW order_current_state AS
SELECT
    guest_order.guest_order_identifier,
    guest_order.table_sitting_identifier,
    guest_order.person_identifier,
    first_event.first_submitted_at,
    last_event.last_event_at,
    COALESCE(line_summary.pending_line_count, 0)  AS pending_line_count,
    COALESCE(line_summary.fulfilled_line_count, 0) AS fulfilled_line_count,
    COALESCE(line_summary.current_total_amount, 0::numeric(10,2)) AS current_total_amount
FROM guest_order
LEFT JOIN LATERAL (
    SELECT min(occurred_at) AS first_submitted_at
    FROM order_event
    WHERE order_event.guest_order_identifier = guest_order.guest_order_identifier
      AND order_event.event_type = 'guest_submission'
) AS first_event ON true
LEFT JOIN LATERAL (
    SELECT max(occurred_at) AS last_event_at
    FROM order_event
    WHERE order_event.guest_order_identifier = guest_order.guest_order_identifier
) AS last_event ON true
LEFT JOIN LATERAL (
    SELECT
        count(*) FILTER (WHERE NOT line.is_fulfilled) AS pending_line_count,
        count(*) FILTER (WHERE line.is_fulfilled)     AS fulfilled_line_count,
        sum(line.quantity * line.current_unit_price_amount) AS current_total_amount
    FROM order_current_line AS line
    WHERE line.guest_order_identifier = guest_order.guest_order_identifier
) AS line_summary ON true;

CREATE VIEW sitting_bill AS
SELECT
    guest_order.table_sitting_identifier,
    guest_order.person_identifier,
    guest_order.guest_order_identifier,
    COALESCE(sum(line.quantity * line.current_unit_price_amount), 0::numeric(10,2))
        AS person_total_amount
FROM guest_order
LEFT JOIN order_current_line AS line
    ON line.guest_order_identifier = guest_order.guest_order_identifier
GROUP BY guest_order.table_sitting_identifier,
         guest_order.person_identifier,
         guest_order.guest_order_identifier;

CREATE VIEW order_visibility_current AS
SELECT DISTINCT ON (guest_order_identifier)
    guest_order_identifier,
    (event_type = 'hidden') AS is_hidden
FROM order_visibility_event
ORDER BY guest_order_identifier, occurred_at DESC, order_visibility_event_identifier DESC;
```

The bill (sum over `sitting_bill` for a sitting) **includes still-pending lines** by design; the counter reviews them before close (§5.3).

### 8.4 Reminder scan (normative SQL)

The reminder background service (§10.2) runs every ~5 seconds:

```sql
-- :reminder_seconds = KITCHEN_SUBMISSION_REMINDER_SECONDS
SELECT submission.order_event_identifier
FROM order_event AS submission
JOIN guest_order   ON guest_order.guest_order_identifier = submission.guest_order_identifier
JOIN table_sitting ON table_sitting.table_sitting_identifier = guest_order.table_sitting_identifier
WHERE submission.event_type = 'guest_submission'
  AND table_sitting.closed_at IS NULL
  AND submission.occurred_at < now() - make_interval(secs => :reminder_seconds)
  AND EXISTS (SELECT 1 FROM order_operation_line_added AS added
              WHERE added.order_event_identifier = submission.order_event_identifier)
  AND NOT EXISTS (SELECT 1 FROM kitchen_notification AS prior
                  WHERE prior.order_event_identifier = submission.order_event_identifier
                    AND prior.kind = 'reminder')
  AND NOT EXISTS (
      SELECT 1
      FROM order_operation_line_added AS added
      WHERE added.order_event_identifier = submission.order_event_identifier
        AND (EXISTS (SELECT 1 FROM order_operation_line_fulfilled AS fulfilled
                     WHERE fulfilled.order_line_identifier = added.order_line_identifier)
          OR EXISTS (SELECT 1 FROM order_operation_line_removed AS removed
                     WHERE removed.order_line_identifier = added.order_line_identifier)));
```

For each hit: `INSERT INTO kitchen_notification (…, kind => 'reminder') ON CONFLICT (order_event_identifier, kind) DO NOTHING`; broadcast `KitchenAlert(reminder)` **only if the insert took** (rowcount 1). The `UNIQUE (order_event_identifier, kind)` constraint makes the whole thing race-safe.

### 8.5 Domain fold equivalence

`MyRestaurant.Domain` provides `OrderProjection.FromEvents(IReadOnlyList<OrderEvent>)` — a pure fold producing the same line set, prices, and fulfillment flags as `order_current_line`/`order_current_state`. Integration tests generate randomized event sequences (respecting §6.5), then assert view output ≡ fold output. The fold is what mutation validation (§6.5) evaluates under the lock; the views serve reads. Neither is the source of truth — the event tables are.

## 9. Live updates

`IDomainEventBroadcaster` (in `Domain`, implemented in-process in `WebApplication`) fans out to subscribed Blazor circuits **after commit**. Notification types (records with the identifiers a subscriber needs to re-query — payloads are ids, not state):

| Notification | Fired on | Consumed by |
|---|---|---|
| `OrderLinesChanged(sittingId, orderId)` | any order event commit | table members of the sitting; counter |
| `KitchenAlert(orderEventId, kind)` | kitchen_notification insert (initial/reminder) | kitchen (sound + highlight) |
| `LineFulfillmentChanged(sittingId, orderId)` | fulfillment / reversal commit | table members; kitchen |
| `MenuChanged()` | menu_item / menu_item_event commit | all surfaces showing the menu |
| `SittingMemberJoined(sittingId)` | membership insert | table members; displays (party size) |
| `SittingClosed(sittingId)` | close commit | table members; kitchen; counter |
| `VisibilityChanged(orderId)` | visibility event commit | table members (history views) |

Subscribers re-query views on notification (ids let them scope the re-query). Components unsubscribe on disposal. If Redis ever becomes necessary (second web replica), only the broadcaster implementation changes (ADR-0006). Display QR rotation is **not** broadcast — displays re-render on their own window-aligned timer (§4.3).

## 10. Kitchen alerting

### 10.1 Alert rule

A `kitchen_notification (kind='initial')` row is written **in the same transaction** as: every `guest_submission`, and every `staff_edit` **by counter or administrator** that adds or removes lines. The kitchen's own `staff_edit`s, all `price_adjustment`s, and fulfillment/reversal events are silent (no notification row). After commit, `KitchenAlert(initial)` broadcasts; the kitchen surface plays the loud sound and highlights the affected order group.

### 10.2 Reminder rule

Exactly the SQL of §8.4: one reminder maximum per guest send, fired at `KITCHEN_SUBMISSION_REMINDER_SECONDS` (default 60) iff the send had ≥1 added line and none of its added lines has since been fulfilled or removed. Pure-removal sends alert once (10.1) and never remind. Reminders exist only for guest submissions — staff coordinate verbally.

### 10.3 Audio arm and wake lock

Browsers block autoplay: the kitchen surface shows a one-tap "enable sound" arm control per session; until armed (and whenever playback fails) a persistent, high-contrast visual badge with unseen-alert count is the fallback. The surface requests `navigator.wakeLock('screen')`, re-acquiring on `visibilitychange`. The display surface (§11.5) does the same wake-lock dance, no audio.

## 11. Surface behavior

### 11.1 `/table`

Anonymous with valid token → grant → sign-in/registration (passkey-first, password offered; the registration surface is §11.8) → join. Member view: the party roster; **my order** — staging area (add item pickers from the menu — deactivated items greyed out and unselectable (§7) — with quantity 1–100 and note; mark-my-pending-line-for-removal) with a Send button that is disabled while empty and shows an all-or-nothing error panel (per-operation reasons) on rejection; below it the committed living order, each line badged pending/fulfilled, removed lines struck-through with actor + reason, price adjustments shown old → new with reason; **party orders** — read-only equivalents for other members; running personal and table totals; history (the guest's **own** past orders at this restaurant — cross-member history is never shown); a per-order **Hide** control on closed orders, confirmed as irreversible from the guest's account (§6.8); and a link to the **profile page** (§11.6) — set the display name; manage passkeys, password, TOTP and recovery codes; optional phone number and email address (used for manual staff escalation only — nothing in the system sends to them automatically); postal addresses with **free-text labels** ("Home", "Work", "Grandparents' house") — deliberate scaffolding for a possible future delivery/takeout feature, consumed by nothing in version 1 and not to be removed as dead weight. On `SittingClosed`, the surface flips to a read-only settled-bill view.

### 11.2 `/kitchen`

Queue of `kitchen_pending_line` grouped by (table label → person display name → order), ordered by the group's oldest `added_at`; each group shows the send timestamp(s); customization notes prominent. Tap a line → one `fulfillment` event; "fulfill all for this order" → one event, N operations; an Undo affordance on recently-fulfilled lines → `fulfillment_reversal`. An "86" panel lists menu items with active toggles. Loud alert + badge per §10.3.

### 11.3 `/counter`

Open sittings with per-person and table totals (`sitting_bill`); drill-in shows lines with states; price adjustment dialog (new price + required reason); add/remove line (staff edit, optional reason on removal); pending-lines warning then **Close & settle**; a per-table "Show join code" button rendering the rotating QR full-screen (§4.5); closed-sitting lookup (read-only).

### 11.4 `/administration`

Users (create staff, roles grant/revoke, activate/deactivate, **Reset credentials** per §3.7); Tables (create/edit/deactivate, rotate join secret with confirmation, display devices list with pair-code generation and revoke, show rotating QR); Menu (CRUD + activity, event history per item); Sittings (open + recent, end-of-day batch close, post-close corrective actions per §6.7); **Hidden records** (every hidden order system-wide, filterable by username / date range / table, full unprojected record per row, per-record Unhide — §6.8); Event explorer (filter security/order/menu events by subject, actor, type, time); no printed-QR page exists. Administration renders the **complete stored record** everywhere — full event streams, visibility logs, security events — never projected or truncated for the administrator; filters narrow only on explicit request.

### 11.5 `/display/{table}`

Unpaired device → redirect `/display/pair` (code entry). Paired: full-screen table label + rotating QR (server SVG, window-aligned refresh), party-size chip when a sitting is open (via `SittingMemberJoined`/`SittingClosed`), connection-state indicator (circuit down → prominent "offline — see the counter" state; the QR must not silently freeze stale), wake lock. Revoked → pairing screen with "this display was disconnected".

### 11.6 `/account`

The person's own profile (§4.6) — reachable by **every** authenticated principal, guest to administrator, since the fields it edits belong to the person rather than to a role. It is not an area: no policy guards it, only `[Authorize]`, and it is **not** exempt from the §3.5 obligations pipeline (an outstanding flag routes elsewhere first).

Two things live here. **Your details** — display name, email address, phone number — is one form writing one `person` row update; the username is rendered read-only with the reason. None of the three is a credential, so the update rotates no security stamp and writes no `security_event` (§8.2's vocabulary is closed and contains no profile-edit type, correctly). A changed display name does re-issue the authentication cookie, because the name travels as a claim. **Sign-in and security** is a status row per credential — password set/unset, authenticator enrolled/not, passkey count — each linking to the surface that owns it: `/account/change-password`, `/account/enroll-totp` (§3.4), `/account/passkeys` (§3.3). Those three record their own events.

`/account/change-password` is the voluntary password surface, distinct from the forced `/account/change-password-required` of §3.5: it takes current + new (or, for a passkey-only account, new alone via an add path), records `password_changed`, and re-issues the cookie — the framework rotates the security stamp inside the password update, so without that the person would sign themselves out.

Address management (§4.6's free-text-labelled postal addresses) is **not surfaced**: nothing in version 1 consumes an address, and a form for data no reader exists for would be scaffolding pretending to be a feature. The table and columns stay as specified.

### 11.7 The wall clock (every page, both layouts)

§8.1's rule — every instant in `RESTAURANT_TIME_ZONE`, for every reader — is correct and invisible. "Sent 3:04 PM" tells someone on another continent nothing unless the page says whose three o'clock that is. A footer therefore appears on **every** page in both layouts (`MainLayout` and, deliberately, `DisplayLayout`), reading `Restaurant time · Sun 26 Jul 2026, 3:04:05 PM · New York`, ticking to the second. It is the restaurant's own clock, and it is as useful to the counter and the kitchen as it is to a remote guest.

**Server-anchored, client-driven.** There is **no server-side timer**: a Blazor timer would tick only on the interactive surfaces (half of these pages are static SSR), and would cost one render plus one circuit message per second per open tab — on phones, indefinitely. Instead `RestaurantClockFooter` renders one anchor and never re-renders (`ShouldRender() => false`; the script owns that text node thereafter):

| Attribute | Meaning |
|---|---|
| `data-epoch-milliseconds` | the server's instant, UTC |
| `data-utc-offset-minutes` | the zone's offset at that instant |
| `data-next-transition-epoch-milliseconds` | when that offset next changes, if within ~800 days |
| `data-next-utc-offset-minutes` | the offset that takes over then |
| `data-twelve-hour-clock` | the §13 format decision |
| `data-snapshot-url` | where to re-anchor |

`wwwroot/js/clock.js` — a classic script alongside `passkey.js` and `display.js`, so it works on static SSR too — advances that anchor locally and formats it by reproducing `RestaurantTime`'s invariant patterns character for character. `Intl` is **forbidden here**: it formats in the *reader's* locale and zone, the exact thing §8.1 rules out. Precomputing the next transition is what lets a page left open across the first Sunday in November stop rendering EDT without a reload.

**Handheld budget** (most readers are on a phone; the browser and the OS will both try to save battery, and this must not fight them):

1. **Nothing runs while hidden.** `visibilitychange` clears the timer rather than letting it fire and be ignored. A backgrounded tab costs zero.
2. **One wake per visible second.** `setTimeout` aimed at the coming second boundary, not `setInterval` (drift accumulates into double-fires) and never `requestAnimationFrame` (sixty wakes for one visible change).
3. **No DOM write unless the text changed**, guarded by one string comparison; `tabular-nums` and `contain: content` keep the tick from reflowing the page around it.

**Which clock is believed.** Elapsed time comes from `performance.now()` — monotonic, so an NTP step cannot move it. But it stops advancing during device suspend on several platforms, so a phone that spends an hour in a pocket would wake an hour behind; `Date.now()` is therefore read alongside it every tick, and a divergence past two seconds is treated as the signal it is (prefer the wall clock, which alone saw the suspend, and re-anchor).

**`GET /restaurant-clock`** returns the same anchor as JSON: anonymous, `no-store`, and exempt from the §3.5 obligations pipeline (it carries no user action, and the obligation pages render this footer too). The page markup is the anchor for a short-lived page; the endpoint exists for the ones that are not — a `/display/{table}` tablet holds one URL for days on a cheap oscillator, and a guest's circuit lasts a meal. It is asked: every ten minutes while visible, on returning from a minute or more hidden, and on detected clock divergence. Never while hidden, never more than once a minute, and a failed request is ignored rather than allowed to blank the clock — half the round trip is subtracted as the usual symmetric-latency estimate.

### 11.8 `/register`

The surface R§4.3 has always required — "guests self-register at the moment of joining a table: username, optional display name, and at least one credential — passkey offered first, password accepted" — and §11.1 and §4.4 both assume. Anonymous, static SSR (it writes cookies on the response), reachable from `/sign-in` by a **"Create an account"** link that carries the return URL forward; that link is the whole mechanism by which registering mid-join lands the guest back at their table.

Two steps over one Data-Protection-protected **registration ticket** cookie:

1. **Details** — username (§3.1's 3–64 `citext` rules), optional display name, **optional** password. A supplied password is hashed at once and the ticket carries the PHC string; a plaintext never persists across the round trip.
2. **Sign-in method** — the WebAuthn attestation ceremony against an anonymous `POST /register/passkey/creation-options` gated on that cookie. Registering the passkey commits the account. **"Not now — use my password"** commits on the password alone, and **renders only when a password was set**.

That asymmetry is §3.3's rule made structural: a passkey is always offered, never required, never a gate for a guest — so declining is offered exactly when there is something to decline *to*. An account with neither credential is refused twice, in the markup and again in the data layer before any SQL runs.

**Why a ticket rather than a single form.** A WebAuthn attestation needs a user handle *before* the account row exists, and that handle must equal the eventual `person.person_identifier` or a later discoverable-credential sign-in presents a handle matching nobody. This is §3.6's problem and §3.6's solution. Unlike the bootstrap wizard's ticket there is **no step enum**: `/setup` has three ordered steps that must each be unskippable, registration has one, and the ticket's existence *is* the state.

No TOTP step (§3.4 pairs the authenticator with the password path for staff) and no role — a guest is the *absence* of a grant (§3.7), so nothing touches `person_role`. The account commits in one transaction — `person`, the optional `passkey_credential`, and a `security_event` with a **NULL actor**, this being a self-action, exactly as the bootstrap records itself.

### 11.9 The colophon and `/source` (every page, both layouts)

**The colophon** is one quiet line beneath the §11.7 wall clock, rendered by both layouts on every page: the product name, the running version, and a link to `/source` reading *"Source code (AGPL-3.0-only)"*. It is a sibling of the clock's `<footer>` rather than a child of it, because that element belongs to a component whose re-rendering is pinned off; the pair is styled as one bar and the colophon carries the bottom padding and the safe-area inset.

**`/source`** is anonymous, static SSR, and on §3.5's exemption list. It states the version, the source revision, the licence, and the URL at which the operator publishes the corresponding source (`RESTAURANT_SOURCE_URL`, §13).

**Why the program offers its own source.** AGPL-3.0-only §13 requires a **modified** version to *"prominently offer all users interacting with it remotely through a computer network … an opportunity to receive the Corresponding Source of your version by providing access to the Corresponding Source from a network server at no charge"*. An unmodified deployment of this tree therefore owes nothing, and this page is a courtesy for it. But this project is published so that other people will run it, and `CONTRIBUTING.md` has told them since rev 1 that a fork *"owes its users the same"* — so the mechanism belongs **in the program**, discharged by setting one environment variable, rather than left as a page each operator must remember to write. A footer link on every page is the customary reading of *prominently*; a link only an administrator can reach is not an offer to all users, which is also why the destination is exempt from the obligations pipeline (§3.5): the pipeline exists to stop a flagged principal **acting**, not to withhold the licence under which they are being shown a page.

There is deliberately **no setting that turns the offer off.** An offer with an off switch is not one, and an operator who genuinely wants it gone has the source and the freedom to remove it — which is the entire arrangement.

**The build stamp.** `Directory.Build.props` sets `VersionPrefix`; the `Containerfile` takes `VERSION` and `SOURCE_REVISION` build arguments and passes `InformationalVersion` (`version` or `version+revision`) to `dotnet publish`; `WebApplication/Configuration/BuildInformation` parses that attribute back out at runtime and is the only reader of it. The value also becomes OpenTelemetry's `service.version` (§12).

Three normative details:

- **Everything after the first `+` is the revision.** SemVer allows dot-separated build metadata, and the SDK's own `AddSourceRevisionToInformationalVersion` appends `.$(SourceRevisionId)` when a `+` is already present — so a second segment means two facts were stamped and discarding either would be the parse forming an opinion.
- **An unstamped build must say so.** No revision renders as *"Not recorded"*, never as a guess and never as an empty element. This is the one field somebody would act on.
- **`SourceRevisionId` alone does nothing.** The SDK's `AddSourceRevisionToInformationalVersion` target is conditioned on `SourceControlInformationFeatureSupported`, which **SourceLink** sets and nothing else in the SDK does. Passing `InformationalVersion` explicitly is deliberate — a package dependency to obtain one string is a worse trade than four lines of `Containerfile`.

`RESTAURANT_SOURCE_URL` validates as an **absolute http or https URL**. http is accepted here and nowhere else in `RestaurantOptions`: `RESTAURANT_PUBLIC_ORIGIN` is https-only because WebAuthn needs a secure context and the authentication cookie is `Secure`, and neither property applies to an outbound link at which somebody else serves a repository. A fork operator running a forge on a LAN over plain http is discharging §13 perfectly well.

The revision is rendered as text, never composed into `{url}/tree/{revision}`: GitHub, GitLab, Gitea, cgit and Sourcehut do not agree on that path, and a link that 404s is worse than a hash somebody can paste into `git checkout`.

### 11.10 The live-surface contract (every interactive surface)

**Every interactive surface publishes two boolean attributes on the element that wraps it: `data-live` and `data-loaded`.** Both are normative, and neither is useful without the other.

**Which surfaces.** *Interactive* means what `App.razor` means by it and nothing else: `RenderModeForPage` returns `InteractiveServer` for every routable page except those carrying `[ExcludeFromInteractiveRouting]`, and a component hosted with an explicit `@rendermode="InteractiveServer"` is interactive wherever it lives. That is the rule. It is stated here rather than as a list of surfaces because it was carried as a list of surfaces for four milestones, the list said *five*, and `/table` had been interactive since M3 without appearing on it (**F-47**).

- **`data-live`** answers *did a circuit produce this markup*, and it is answered by `RendererInfo.IsInteractive` — from nothing else. A prerendered copy of a live surface is correct as of the request and correct forever after, which is the failure mode that looks most like success: a kitchen that has heard nothing for ten minutes and a kitchen board with no circuit behind it are the same screen.
- **`data-loaded`** answers *does this markup have what the surface renders itself for*. The predicate is each surface's own. On five of the six it is the `_loaded` field the loading branch already reads; on `/display/{table}` it is that **and** a join code, because both branches carrying that surface's id are reachable fully resolved and one of them has no QR on it — a bit that read true there would be true and useless on the one screen whose entire content is a code.

**Why both.** `ComponentBase` renders the moment `OnInitializedAsync` yields, so the circuit's first render is `data-live="true"` with nothing loaded. A reader waiting on `data-live` alone is therefore steered **towards** the one instant at which the surface has finished nothing, rather than past it. A reader waiting on `data-loaded` alone matches the prerendered markup, which is fully loaded and inert. The window is not the prerender — prerendering runs the whole lifecycle before it emits — it is the **circuit hand-over**, where the component is built again from nothing and the DOM returns to its loading branch for as long as the reads take. Milliseconds on a workstation; long enough on a loaded runner to fail a scenario (**F-44**).

**Who they are for, in this order.** An operator with dev tools open on a screen that is not moving; then the §16.3 harness, whose barriers demand both bits; then nobody. They carry no styling and no behaviour, and `js/display.js` deliberately does not read them — the staleness curtain keys on `data-refresh-token`, which covers a circuit that died *later*, where these cover one that never lived.

**A surface that is not interactive must publish neither.** On a statically rendered page `data-live` would be `"false"` on every render that will ever happen: an attribute in the shape of an assertion with no assertion in it, and one a harness could wait on until it timed out.

The contract is asserted by a test rather than by this paragraph — see §16.4.

### 11.11 Response security headers (every response, ADR-0013)

**Every HTTP response this application produces carries `Content-Security-Policy`,
`X-Content-Type-Options: nosniff` and `Referrer-Policy: same-origin`.** Every response means every
response: rendered pages, static files under `wwwroot`, `/_framework/*`, the health endpoints, the
clock endpoint, the sign-out POST, a 404 from the router, the rate limiter's 429, and the obligations
pipeline's redirect. Not a subset chosen by which of them happens to be an endpoint.

**The application emits them and no proxy is trusted to.** This tree is deployed behind Caddy in the
dev profile, behind a Cloudflare tunnel in production, behind an optional Caddy on a staff LAN, and
behind nothing at all when somebody runs `dotnet run` to reproduce a defect or when the §16.3 harness
boots. A header configured in `Caddyfile` is absent from two of those; a header configured at
Cloudflare is absent from every fork that does not use Cloudflare, and is platform state of exactly
the kind §18 forbids a document from asserting (ADR-0013).

**There was already a policy, and nobody wrote it (F-49).** `AddInteractiveServerRenderMode` installs
an endpoint convention that appends `frame-ancestors 'self'` to `Content-Security-Policy` on component
endpoints, because WebSocket compression combined with cross-origin framing is an attack and the
framework declines to ship the first without the second. It has been in every build since M1, it
covers one directive, it covers only endpoints that render components, and it **appends** rather than
assigns — so a second policy written here would be delivered beside it. It is therefore switched off
(`ContentSecurityFrameAncestorsPolicy = null`) and this section replaces it with something strictly
stronger in both directions: `'none'` rather than `'self'`, on every response rather than on some.
Compression is unaffected; that is a different option.

**The policy.** In this order, and asserted in full by a test rather than described here:

```
default-src 'self'; base-uri 'self'; object-src 'none'; frame-src 'none';
frame-ancestors 'none'; form-action 'self'; img-src 'self' data:;
style-src 'self' 'unsafe-inline'; script-src 'self';
connect-src 'self' ws://{host} wss://{host}
```

Four of those need their reason recorded beside them.

- **`connect-src` names the request's own host, and that is not belt-and-braces.** CSP's `'self'` is an
  *origin* comparison and `wss://host` is not the same origin as `https://host`. CSP3 added a carve-out
  so `'self'` also matches the `https:` and `wss:` variants of the page's origin, which covers
  production; it does not clearly cover `ws:` from an `http:` page, browsers have historically
  disagreed, and `http:` is exactly what a bare `dotnet run` and the §16.3 harness serve. Every §9
  notification in this system arrives over that WebSocket, so the two origins are named rather than
  inferred. The host is the one `PublicOriginMiddleware` has already normalized to a trusted public
  host (§3.3), which is why a value taken from the request can be written into a header without
  widening anything. A host that cannot be written as a CSP `host-source` — an IPv6 address literal,
  which the grammar cannot express at all — falls back to the bare `ws:` and `wss:` scheme sources,
  because a source expression the browser discards fails as a blank screen with no cause named.
- **`style-src 'unsafe-inline'` is a concession and is tied to the fact that earns it.** Twenty-one
  components carry a scoped `<style>` block, and Blazor's own reconnection overlay builds one at
  runtime with `innerHTML` — so a guest whose circuit drops would see an unstyled dialog without it.
  The contract test asserts the blocks still exist, so that moving them into `app.css` fails a test
  that says to tighten the policy. Nothing else would ever cause a concession to be dropped.
- **`img-src data:`** exists for one thing: the empty `data:` favicon in `App.razor`, which stops
  browsers requesting `/favicon.ico` on every page of a restaurant's phone traffic. A
  `<link rel="icon">` is an image fetch as far as CSP is concerned. The contract test asserts it is
  still the only `data:` URL in the tree.
- **`default-src 'self'` rather than `'none'`** is the one place F-45's allow-list ruling is
  deliberately not applied, and the distinction is worth stating because the two cases look alike.
  F-45 was about a set this project enumerates and controls — the paths in a build context — where an
  allow-list fails loudly and locally. A CSP fallback governs a set the *browser* defines and extends
  with each new fetch destination, so `'none'` would be an allow-list over somebody else's vocabulary,
  and the failure mode is a screen in a working restaurant that quietly stops showing something.
  `'self'` already denies every cross-origin origin, which is the threat; the directives that should
  be narrower are taken to `'none'` by name.

**What is deliberately absent**, recorded so that it is not re-litigated by addition: `X-Frame-Options`
(superseded by `frame-ancestors` in every browser that can run a Blazor circuit, and a second spelling
of one rule is a second thing to keep in step); `Strict-Transport-Security` (an operator decision with
a long memory, meaningless on the plain-HTTP hop between the tunnel and this process, and not
revocable from the application — O§14); `Permissions-Policy` (a deny-list by construction, which F-45
ruled against where a domain permits an allow-list, and the two features this application *does* use
are screen wake lock and WebAuthn, so a wrong entry is a kitchen board that sleeps mid-service and is
found by a cook); `upgrade-insecure-requests` (there is not one absolute URL in the markup);
`Cross-Origin-Opener-Policy` and its relatives (no popups, no cross-origin isolation requirement, and
no threat this application can name); and any reporting directive (there is no endpoint to report to).

**`Referrer-Policy: same-origin` is here for a product-specific reason** rather than as hygiene. §4.3's
join token travels in a query string — `/table/{id}?token=…`. Every current browser defaults to
`strict-origin-when-cross-origin` and would not leak it, but a secret in a URL protected by a browser
default is protected by something no deployment here controls.

**Middleware, and its position is normative.** After `PublicOriginMiddleware`, because the policy names
the normalized host; before everything that can produce a response, because a short-circuit must not
escape it; and the headers are written **before** the rest of the pipeline runs, because a header
written afterwards is written after the body was flushed. Not an endpoint convention and not a filter:
the responses most in need of `nosniff` are the static ones that never reach an endpoint, which is the
defect the framework's own convention demonstrates.

### 11.12 The handheld layout contract (every surface, every screen)

**Every surface this application serves is laid out for a handset first, and widened by exactly one breakpoint.** Normative, and the reason it is normative rather than a styling preference is R§1: *"Guests seated at a table order from their own phones"*, and the staff who run the floor are holding the same phones. Nobody carries an ultrawide monitor to a restaurant.

**The direction is the rule.** `wwwroot/app.css` states the narrow layout unconditionally and contains exactly one `@media (min-width: 48rem)` query, which is the only place a width appears. Three properties, and each fails differently:

- **Exactly one breakpoint.** A second is the same number written in a second place, and two places drift — the mechanism behind F-48, F-50 and F-56 in three unrelated files.
- **`min-width`, never `max-width`.** A max-width query says the wide layout is the default and the handset is the exception. That is the arrangement the defect was found in, and it fails in the worst available direction: a browser that does not apply the query, or a rule somebody forgot to write, lands on the layout that does not work on the screen the software is used from.
- **48rem, stated once.** A CSS custom property cannot be used in a media query, so the value is a literal in that one query and a comment beside it says so. The primary target is a 375px handset, nowhere near the boundary in either direction.

**Touch targets are 2.75rem** (`--touch-target`, 44px at the default root size) on every control a person presses: buttons, links that act as buttons, checkbox rows, and the session links in the header. **Every text input, `select` and `textarea` carries a 16px font floor** — under it, iOS Safari zooms the whole viewport on focus and does not zoom back out, so one undersized field breaks the layout of the page around it on the platform most guests are holding.

**A record list is the shape every index takes.** Below the breakpoint each row is a card; above it, the same markup is a table with a header row. **Every cell states its own label** from a `data-label` attribute, and a cell whose content already says what it is opts out with `data-label=""` — a decision written down rather than an omission. That is not decoration: overriding `display` on a table's parts drops the element's table semantics in every engine, so below the breakpoint the `<thead>` stops being what associates a cell with a column, and an unlabelled card is a column of bare values with nothing on screen or in the accessibility tree saying which is which. The `<thead>` is clipped rather than `display: none`, so it survives for a reader in table mode at any width.

**A row's action is never a right-hand column.** It is the last thing in the card and the full width of it, and the row's primary cell is also a link — so the way in is at x=0 whatever the viewport does. This is the finding stated as a rule: four administration index pages each declared their own copy of `.admin-row-actions { text-align: right; white-space: nowrap }` inside a wrapper with `overflow-x: auto`, which put the only affordance on the row off the right-hand edge of a 375px screen, reachable only by scrolling sideways (**F-59**).

**The shared vocabulary is declared once.** A component may keep an inline `<style>` for rules nobody else reads — that is this project's standing arrangement, since `App.razor` links the static stylesheet and CSS isolation is therefore not active anywhere. It may not re-declare a shared name: same specificity, later in the document, so the page always wins and the stylesheet always loses, silently. The eighty lines of table rules, the chip vocabulary and `.visually-hidden` were each declared four to five times inline before this section existed.

**What this section does not require.** That every surface be *optimised* for a handset — only that the handheld layout is the default and that the surface is legible and operable at 375px. `/kitchen` is the case that forces the distinction: §11.2 and §10.3 describe a wall-mounted kiosk with a wake lock and a loud alert, so its primary reader is a large screen. It satisfies this section by working on a phone, not by being designed for one.

**The contract is asserted twice, at two levels, and neither level can reach the other's claim.** Its *structure* — one breakpoint, one shared vocabulary, a label on every cell, the retired names gone — is arithmetic on text and is asserted by a unit test. Whether a control is **on the screen** is a question about layout, so it is asserted by a browser: §16.3 scenario 16 lays a context out at 375×667, walks the administration indexes, and compares three numbers the page computes against the viewport. That second assertion is the one F-59 would have failed, and the reason it took an extra slice to arrive is itself recorded (**F-62**). Both are specified in §16.4.

## 12. Observability


OpenTelemetry traces (ASP.NET Core + Npgsql instrumentation), logs, and metrics via OTLP (`OTEL_EXPORTER_OTLP_ENDPOINT` etc.; `run.sh` translates a legacy `UPTRACE_DSN` if present — any OTLP collector works). Custom meters (full snake_case):

`guest_submission_batches_total` · `order_lines_added_total` · `order_lines_removed_total` · `order_lines_fulfilled_total` · `kitchen_reminders_sent_total` · `sittings_closed_total` · `table_join_tokens_validated_total{result}` · `sign_ins_total{method=password|passkey, result=succeeded|failed}` · `password_hash_duration_milliseconds` (histogram). Health: `/healthz/live` (process up), `/healthz/ready` (DB reachable + migrations current); compose healthchecks target these.

The resource carries `service.name = myrestaurant` and **`service.version` = the full informational version** (`1.0.0+3f2a9c1…`, §11.9). The version is the whole build stamp rather than the semver alone, because the question a collector is asked after a deployment is *which build changed*, and two builds of the same tag are indistinguishable without the revision. A build that was not stamped reports its version with no `+` suffix, which is the honest answer rather than an absent attribute.

## 13. Configuration (environment only)

| Key | Default | Meaning |
|---|---|---|
| `RESTAURANT_NAME` | `My Restaurant` | display + TOTP issuer |
| `RESTAURANT_PUBLIC_ORIGIN` | `https://localhost:8443` (dev) | canonical origin: QR URLs, RP-ID fallback; production = stable named-tunnel domain (so passkeys persist) |
| `RESTAURANT_TRUSTED_ORIGIN_PATTERNS` | `https://*.trycloudflare.com` | extra wildcard origins allowed as the WebAuthn RP besides the origin above + loopback (§3.3, ADR-0005); comma/space/newline separated, each `scheme://host`, https, optional single leading `*.`, no path/port |
| `RESTAURANT_TIME_ZONE` | `America/New_York` | rendering only — **all** instants, for **all** readers (§8.1, §11.7) |
| `RESTAURANT_CLOCK_FORMAT` | `12-hour` | `12-hour` (3:04 PM) or `24-hour` (15:04); display only. Accepts `12`/`12h`/`12 hour` and the `24` equivalents; anything else fails startup |
| `RESTAURANT_CURRENCY_CODE` | `USD` | display only |
| `RESTAURANT_SOURCE_URL` | `https://github.com/kusl/myrestaurant` | where this instance publishes its corresponding source (§11.9). Absolute **http or https** — http accepted here only, so a self-hosted forge on a LAN is not refused. **If you modify the program and run it as a network service, point this at your fork**; AGPL §13 |
| `RESTAURANT_DATABASE_CONNECTION_STRING` | compose-internal default | Npgsql string |
| `POSTGRES_USER` / `POSTGRES_PASSWORD` / `POSTGRES_DB` | dev defaults | consumed by the postgres container; **must** be overridden in production |
| `DATA_PROTECTION_KEYS_DIRECTORY` | `/var/lib/myrestaurant/dataprotection` | named volume; §3.4 |
| `KITCHEN_SUBMISSION_REMINDER_SECONDS` | `60` | §10.2 |
| `TABLE_JOIN_TOKEN_ROTATION_SECONDS` | `60` | §4.3 |
| `TABLE_JOIN_GRANT_MINUTES` | `10` | §4.4 |
| `TABLE_DISPLAY_PAIRING_CODE_MINUTES` | `10` | §4.2 |
| `ARGON2_MEMORY_KIBIBYTES` / `ARGON2_ITERATIONS` / `ARGON2_PARALLELISM` / `ARGON2_MAX_CONCURRENT_HASHES` | `65536` / `3` / `1` / `4` | §3.2 + floor guard |
| `BACKUP_DIRECTORY` / `BACKUP_SCHEDULE_TIME` / `BACKUP_RETENTION_COUNT` | `/var/lib/myrestaurant/backups` / `03:30` / `14` | §15 |
| `OTEL_*` | unset | standard OTel variables; `UPTRACE_DSN` translated by `run.sh` only |
| `CLOUDFLARE_TUNNEL_TOKEN` | — | production profile, cloudflared |

Fail-fast validation at startup: origin parses as absolute https URL; source URL parses as absolute http-or-https URL; Argon2 floor (§3.2); rotation/grant/pairing values ≥ 10 s / ≥ 1 min / ≥ 1 min; connection string present.

**A setting reaches the process or it does not exist (F-50).** `compose.yaml`'s `web` service enumerates its environment key by key and takes no `env_file`, so this table is not a description of what a deployed container receives — it is a description of what the *program* reads, and the two are joined only by somebody having written the key in a third place. Every key in this table **must** therefore appear in the `web` service's `environment` mapping and in `.env.example`, and the agreement is asserted by `ConfigurationSurfaceTests` (§16.4) rather than remembered. The failure this rule exists to stop is silent by construction: an unpassed variable is not an error, it is the compiled-in default, rendering a page indistinguishable from a correctly configured one.

Two consequences worth stating. A variable whose default is more than a formatting choice — `RESTAURANT_SOURCE_URL` is the case in hand — **must** be passed through with an *empty* default rather than with its value repeated, so that the fallback stays decided in one place and a fork's own edit to that place is not overridden by this file. And the rule runs in one direction only: a key in `compose.yaml` that this table does not list is not a finding, because `POSTGRES_*` is consumed by the database image and `OTEL_*` by the exporter under its own published contract, and a gate that reported those would report findings on a correct tree (F-41).

## 14. Deployment, TLS, origins (ADR-0004, ADR-0005)

**14.1 Canonical stack** — `compose.yaml`, rootless Podman. Services: `web` (Containerfile build; listens 8080 HTTP inside the network), `postgres` (named volume), `caddy` (dev profile: terminates TLS at `https://localhost:8443` with Caddy's internal CA), `cloudflared` (**production profile**: named tunnel via `CLOUDFLARE_TUNNEL_TOKEN`, forwards to `web:8080`; TLS at Cloudflare's edge). Host ports stay ≥1024 (rootless); if 80/443 are ever wanted directly, that is a host `sysctl net.ipv4.ip_unprivileged_port_start` concern, not this project's default. `podman-compose up` = dev; `podman-compose --profile production up -d` = production.

**Every image reference in `compose.yaml` must be fully qualified** — `docker.io/library/postgres:17-alpine`, not `postgres:17-alpine` (**F-51**). This is a correctness requirement on the canonical engine, not a style preference. A short name is resolved through `unqualified-search-registries`, which Fedora's `containers-common` populates and a stock Debian ships commented out, so on Debian rootless Podman answers the canonical stack with `short-name "postgres:17-alpine" did not resolve to an alias`, `postgres` never starts, and `web` then fails its `depends_on` with a message about a missing container — three errors, none of which names the registry configuration that caused them. `scripts/restore_drill.sh` has defaulted `DRILL_POSTGRES_IMAGE` to a fully qualified name since Slice 16 for this exact reason; the rule existed and had been applied to the drill rather than to the stack the drill rehearses. **The rule is not about `compose.yaml`, and confining it to that file was itself a finding (F-60):** it holds at *every* container image reference in this repository — `Containerfile`'s `FROM` operands, the workflows' service containers, every `*_IMAGE` default in `scripts/`, and the image the Testcontainers fixtures start. Testcontainers does not normalise the reference it is given (`MatchImage.Match` records a registry only when the first slash-separated segment carries a `.` or a `:`, and its own comment says it "does not resolve or set the default domain and repository prefix"), so a short name there reaches the engine as a short name — and both fixtures turn a failed start into a *skip*, which means the canonical host answers with a green suite that ran no integration test and no §16.3 scenario. **A reference must also occupy a position that can be read:** a YAML `image:` key, a `Containerfile` `FROM` operand, or a value assigned to a name ending in `_IMAGE` (shell, YAML) or `Image` (C#). A reference spelled into a `podman run` command line or passed inline to a builder is outside every gate this project has, and two were. **One image name resolves to exactly one reference**, so the suite and the stack cannot disagree about which registry or which version the database comes from. Asserted rather than remembered — see §16.4. Caddy **may** additionally run in production as an optional staff-LAN fallback (self-signed `restaurant.lan`; staff-only; passkeys will not work on that origin; password+TOTP does) — off by default, documented in OPERATIONS §7.

**No `depends_on` in `compose.yaml` may be gated on a health condition** — the only permitted condition is `service_started` (**F-53**). This is the same kind of rule as the one above, and for the same reason: it is a correctness requirement on the canonical engine rather than a preference. podman-compose 1.3.0, which is what Debian trixie ships, implements `up -d` as `podman run -d` for every container **followed by** a wait on each dependency's condition, in an unbounded retry loop that logs at debug level and prints nothing. So a condition the host never satisfies does not fail — the whole stack starts, the container ids are printed, and the command never returns, with no output naming a cause. Whether `service_healthy` is ever satisfied is a property of the host and not of this repository: a health status only advances if something runs the healthcheck, and under rootless Podman that something is a systemd timer in the user's session. There is no flag that avoids the wait, either — `--no-deps` is accepted by `up` in that version and consulted only by `run`.

**Nothing is given up by the prohibition**, which is why it is stated as one. Waiting for the database to accept connections is the *application's* job and has been since M1: `SchemaMigrationRunner` retries a connection failure thirty times at two-second intervals before failing fast, and the comment beside that retry says what it is for — *"at compose start the web container can race PostgreSQL"* (ADR-0012). `web` losing that race is a race the code was written to lose safely. The health*check* on `postgres` stays, because `podman ps`, `scripts/dev_instance.sh status` and an operator at a terminal all read it; what it must not be is the thing standing between `up -d` and returning. The rule is asserted rather than remembered — see §16.4.

**The engine's variable substitution must be verified rather than assumed, and every variable `compose.yaml` interpolates must be assigned in `.env.example`** (**F-57**). This is the third rule of the same kind as the two above, and it is the one with the widest blast radius: `compose.yaml` sets twenty-three values with the `${NAME:-default}` form, and on Debian trixie's podman-compose — the canonical engine — the branch after `:-` **is not applied**. Every variable not already set in the process environment reaches its container as the placeholder text. Measured: the application printed five `Configuration error:` lines naming values like `'${RESTAURANT_TIME_ZONE:-America/New_York}'` and exited 1, and `POSTGRES_USER` reached `initdb` as punctuation, so `CREATE EXTENSION plpgsql` failed with *invalid character in extension owner*, initdb erased the data directory, and the container crash-looped. One engine behaviour, both containers, and no message anywhere naming the cause.

**What is known about the behaviour, and what is deliberately not claimed.** Substitution *works* when the variable is set in the environment — `RESTAURANT_PUBLIC_ORIGIN`, which `scripts/dev_instance.sh` exports, was the one value that arrived correct in the same run. So it is specifically the default branch that is unapplied. Which releases behave this way, and whether an *empty* assignment in `.env` counts as supplying a variable, are properties of a host that no file here can decide — so nothing in this repository predicts them. `scripts/check_compose_substitution.sh` asks the engine (it renders the file with `config` and looks for a surviving `${`), and `scripts/dev_instance.sh` asks again after `up -d` from the containers' own environment, which needs no subcommand and cannot be fooled by a `config` that renders differently from what it runs. The three helpers that start the stack — `dev_instance.sh`, `quick_tunnel.sh`, `run.sh` — refuse before doing work when the answer is no.

**Because the remediation is `.env`, `.env.example` must make that remediation complete.** Every variable interpolated inside an `environment:` mapping is **assigned** in `.env.example`, not commented out, empty where empty is the value — and the distinction is load-bearing rather than tidy. `OTEL_EXPORTER_OTLP_ENDPOINT`'s *emptiness* is what stops the application attaching an OTLP exporter (§12); commented out, the literal `${OTEL_EXPORTER_OTLP_ENDPOINT:-}` arrives instead, which is not empty, so the exporter is switched on and pointed at a hostname made of braces. `RESTAURANT_SOURCE_URL` is likewise assigned **empty** in that file, which is F-50's ruling applied one layer over: empty means `RestaurantOptions.DefaultSourceUrl`, the one place the fallback is written down, and a file that spelled the upstream URL would silently override the first edit a fork makes. `SOURCE_REVISION` is out of scope — it is a build argument, not an environment key, and `Containerfile`'s own `ARG` decides its fallback. The rule is asserted rather than remembered — see §16.4.

**14.1a Build context (normative)** — the image build must see the publish graph and nothing else. `.dockerignore` at the repository root is an **allow-list** — `.editorconfig`, `Directory.Build.props`, `Directory.Packages.props`, `global.json`, `src`, minus `src/**/bin` and `src/**/obj` — and both engines read that filename (Podman prefers `.containerignore` when both exist, so this project ships only the one). A deny-list is not acceptable here and the distinction is the whole finding (**F-45**): `.gitignore` is a deny-list, it was already correct, and it protected nothing, because a build context is not a commit. Everything it names — `.env`, the Data Protection key ring, every `*.dump` and `*-dataprotection.tar` §15 writes — was copied into the builder on every local `--build`, in the exact order OPERATIONS §12 documents. An allow-list is also the only form that covers the file nobody has added yet.

**The allow-list must be asserted, not assumed.** An ignore-file is an instruction to a tool, and it can be renamed, shadowed, or overridden with `--ignorefile` without any symptom other than a slower build — so `Containerfile` carries a guard immediately after `COPY . .` that fails the build unless the context root is exactly the allowed set, every required path is present, and no `bin` or `obj` survives under `src`. Stating the list twice is deliberate: one statement is the instruction and the other is the assertion, and a build that fails when they disagree is the only thing that distinguishes *excluded* from *excluded on the machine where somebody last checked*. This is F-38's rule — a row in the embodiment column names something executable — applied where the executable thing is the build itself, so it runs on a workstation and not only in CI.

**14.2 Origin truth** — one `RESTAURANT_PUBLIC_ORIGIN`. Everything (WebAuthn RP ID, QR URLs, links) derives from it. In-house guests hairpin through Cloudflare; **LAN ordering therefore depends on WAN health — accepted risk** per the F-06 ruling.

**14.3 Quick tunnels** — for demos. `scripts/quick_tunnel.sh` brings the stack up, opens a quick tunnel, discovers the assigned `*.trycloudflare.com` URL, exports it as `RESTAURANT_PUBLIC_ORIGIN` (so QR join URLs and the form-post host fallback are correct), recreates `web`, waits for `/healthz/ready`, and holds the tunnel in the foreground. Because the RP ID is derived per request (§3.3, ADR-0005) and `https://*.trycloudflare.com` is trusted by default, **passkeys work within a run** — including a passkey-only account. `*.trycloudflare.com` is on the Public Suffix List and every run gets a fresh random subdomain, so passkeys (and bookmarks) do **not** carry across runs and must be re-registered; the helper prints this caveat prominently. A quick tunnel must never carry the *bootstrap* of a real instance (§3.6) — bootstrap on the stable named-tunnel domain so the first administrator's credentials persist.

**A helper's cleanup must run exactly once, whichever way the helper is left** (**F-61**). `scripts/quick_tunnel.sh` installed one handler on `INT`, `TERM` and `EXIT`, which are three independent traps rather than one: bash runs the signal handler and then runs the `EXIT` handler on the way out, so one Ctrl+C executed the body twice and the helper announced that it was closing the tunnel twice. Nothing it did was harmful — the kill and the `rm` are both idempotent — and that is the reason the rule is about the *announcement*: two identical closing lines read as two tunnels, or as one that would not close, which is a helper telling an operator something untrue about the state of the machine. The handler carries a guard and disarms the remaining traps on first entry. Where a handler is silent and idempotent by construction the same registration is unobjectionable, which is why `run.sh`'s smoke trap is unchanged and why `scripts/backup.sh`, `scripts/restore.sh` and `scripts/restore_drill.sh`, which register on `EXIT` alone, were never in scope.

**14.3a Detached demo instances (normative for the helper, not for the deployment)** — `scripts/dev_instance.sh` serves the case §14.3 cannot: a machine that is not the developer's workstation, reached over SSH, running a build that testers will use for days, with **no .NET SDK on the host** (so `run.sh`'s default and `--smoke` modes are both unavailable there). Three properties distinguish it from `scripts/quick_tunnel.sh`, and each is a requirement rather than a convenience.

**The tunnel must not be a child of the shell.** `quick_tunnel.sh` blocks on `cloudflared` as a foreground child, so the URL lives exactly as long as the terminal — correct for a demo somebody is standing in front of, and fatal when the terminal is an SSH session that will be closed. `dev_instance.sh` runs `cloudflared` as a **detached container**, so nothing it starts is its own descendant and it can exit while the instance keeps serving. Rootless containers still belong to a user session; the script reports whether `loginctl enable-linger` is in force (OPERATIONS §2) rather than assuming either answer.

**The image must be built before the URL is announced.** Measured on the host this was written for, a cold `quick_tunnel.sh` printed a public URL and then left it unreachable for nineteen minutes, because it opens the tunnel and *then* builds. `dev_instance.sh` builds first. The window between announcing a hostname and serving from it is then the time it takes to start two containers.

**The origin must be known before `web` is created.** Because the build already happened, the tunnel URL is in hand before anything is created, so the stack comes up **once** with the real `RESTAURANT_PUBLIC_ORIGIN` — rather than coming up with a placeholder and being force-recreated. That ordering also avoids a flag whose behaviour is worth recording: podman-compose implements `up --force-recreate <service>` as a `down` of the **whole project** followed by an `up` of the named service, so recreating one container restarts the database and deletes and recreates the network. The engine's own recreate-on-change remains the mechanism relied on, and it is sound: the config hash it labels containers with is computed *after* interpolation, so a changed origin is a changed hash.

**Two behaviours follow from the tunnel outliving the shell, and both are deliberate.** A second `up` **reuses** the open tunnel rather than minting a new hostname, because a re-registered passkey is worth keeping and a new random subdomain discards every one of them; `--new-url` is the explicit way to ask for a fresh hostname. And nothing else will ever close the tunnel, so `down` is not housekeeping — it is the only thing that stops the instance.

**`up` exits on evidence, not on a return code.** After printing the URL it re-probes the *public* origin for `DEV_INSTANCE_SETTLE_SECONDS` before releasing the terminal, so a tunnel that came up and immediately fell over is reported to the operator rather than discovered by a tester. The probe itself assumes nothing about the host: `curl`, then `wget`, then the `curl` the runtime image installs for its own healthcheck, reached with `exec` — which is a client guaranteed to exist whenever there is anything to probe.

**No call in the helper may own the terminal indefinitely (F-53).** A script whose stated purpose is to hand the terminal back must not contain a call that can keep it, and the first run of this one did exactly that — inside `podman-compose up -d`, for the reason §14.1 now prohibits. So **every** compose invocation runs under a deadline: `DEV_INSTANCE_COMPOSE_WAIT` for ordinary commands, and a separate, far longer `DEV_INSTANCE_BUILD_WAIT` for the image build, because a watchdog that cuts off a legitimate nineteen-minute build would be a worse defect than the one it guards against. Killing a compose command is safe in the one way that matters here, and it is the same property the detached tunnel relies on: the containers it has already created belong to the engine, not to the shell.

**When a deadline trips, the helper must report the containers and continue rather than fail.** A compose command that did not return is not the same thing as a stack that did not start — in the observed failure the instance was serving the public internet throughout. So the helper names the finding, prints each service's state *and health status* read straight from the engine, starts anything that was created but never started, and then verifies `/healthz/ready` itself. `status` reports those same two lines **before** it asks compose anything, so they arrive even on a host where compose is wedged. Health is reported and never waited on: a `postgres` stuck at `starting` is the symptom of F-53's cause, and printing it is what turns a silent hang into a diagnosis.

**No wait may outlive its own evidence (F-55).** A deadline stops a wait that cannot end; only a liveness check stops a wait that cannot *succeed*, and the two failures call for opposite fixes. So every wait in the helper is bounded **and** watched: it polls the state of the container it is waiting for, and it ends as soon as that state says the wait is pointless. Concretely — the database wait ends early when `postgres` has been restarted `DEV_INSTANCE_CRASH_LOOP_RESTARTS` times, because a container the engine keeps restarting is not going to begin accepting connections later; the readiness wait ends early when `web` will not stay started; both start a stopped container again first, up to `DEV_INSTANCE_START_ATTEMPTS` times, because the application's own database retry is bounded at sixty seconds (ADR-0012) and a first `postgres` boot slower than that outlives it, leaving a correctly built image stopped with nothing wrong; and the public-origin settle phase is **skipped** when readiness has already failed, because probing a public URL for an application that does not answer on loopback cannot produce information. The waits are also **separated** — database, then application — because "the app did not answer" is equally true of a crash-looping database, a rejected configuration and an image that never started, and useful for none of them.

**A failed bring-up must print the log, and `logs` must be able to reach the application.** The observed failure printed a URL banner over a dead application and never showed either container's log, which was where the reason had been the whole time; and `logs` could only show the tunnel's. So on any path where the application does not answer, `up` prints each service's state, **exit code** and restart count, then a bounded tail of both containers' logs, then a key mapping the symptoms this program actually produces to their causes — a `Configuration error:` line means the application refused its own environment and exited 1 (that is the only path in `Program.cs` that returns 1); `Database not ready (attempt n/30)` means the cause is in the *other* log. `logs` takes a target and defaults to `web`, not to the tunnel. A stopped container must not be described with a health status: `(stopped, health: starting)` reads as a container on its way up, and it was reported six minutes after that container exited 1.

**`up`'s exit status is a claim about the instance.** 0 means the application answered `/healthz/ready` on this host; non-zero means the stack was started and it never did. The stack is **left running** on failure — the containers and their logs are the evidence — but the status must not say success, because `time bash scripts/dev_instance.sh` is how this helper is actually invoked and an exit code of 0 over a dead instance is a false report.

**The helper must offer the one repair that `down` cannot be (F-55).** `down` keeps the named volumes deliberately: the database and the Data Protection key ring are what make a test instance worth returning to. But a PostgreSQL data directory that cannot start — an interrupted first `initdb`, a hard reboot mid-write, a directory from another major version — survives `down` and `up` for exactly that reason, and `podman system prune -a` does not remove volumes either, so an operator can clear everything they know about and start the same poisoned directory again. `reset` is therefore specified: it stops the stack and removes **this project's** named volumes, enumerated from the engine rather than guessed at. It is destructive by definition — the database and the key ring, so every account, passkey and enrolled TOTP secret — and must therefore state what it will remove, name the volumes it found, require confirmation, and refuse outright rather than assume consent when stdin is not a terminal.

**Loopback is named as an address literal, not as a name (F-56).** `compose.yaml` publishes the web port as `127.0.0.1:8080:8080` — one address, IPv4, with nothing listening on `::1`. Every helper that dials it therefore names `127.0.0.1`: a hostname that resolves to `::1` first depends on each client falling back to the second address, and while curl and GNU wget do, BusyBox wget does not — and it is the second entry in the helper's own probe chain. The visible cost of the name is worse than the risk: cloudflared reports `dial tcp [::1]:8080: connect: connection refused`, so an operator reading the tunnel log goes looking for an IPv6 problem that does not exist. `run.sh` has probed the literal since M1 and the two tunnel helpers named the host, which is F-51's shape again — a rule applied to one example and never generalised.

Everything §14.3 says about what a quick tunnel is **not** for still applies here without exception, and applies more sharply because this instance is long-lived: the hostname is random per tunnel and on the Public Suffix List, so passkeys do not survive `--new-url`, and a real instance must never be bootstrapped (§3.6) through one.

**14.4 `run.sh`** — dev entry: checks prerequisites, starts compose (postgres [+caddy]), exports dev defaults (translating `UPTRACE_DSN` → `OTEL_*` if set), `dotnet watch` the web app. Idempotent; `run.sh --containers-only` starts the stack without watch.

**14.5 Aspire** — optional `AppHost` project may exist for F5 convenience; it must never be required by docs, scripts, or CI.

## 15. Backups

**A recovery set is two files, not one.** `scripts/backup.sh` writes both, sharing one timestamp, into `BACKUP_DIRECTORY`:

| File | Contents | Read out with |
|---|---|---|
| `myrestaurant-YYYYMMDD-HHMMSS.dump` | `pg_dump --format=custom --no-owner`, run **inside** the postgres container so the dump client always matches the server (F-16) | `podman exec` |
| `myrestaurant-YYYYMMDD-HHMMSS-dataprotection.tar` | the Data Protection key ring from `DATA_PROTECTION_KEYS_DIRECTORY` (§3.4) | `podman cp`, which streams a tar through the engine's own archive API and therefore needs nothing installed in the runtime image |

Either file alone is insufficient, and the second is the one that gets forgotten: **without the key ring every stored TOTP secret is undecryptable** — the accounts come back and the enrolled authenticators do not (§3.4, OPERATIONS §8). That rule has been normative since v1.0. Until **F-38** no script honoured it; both merely printed a reminder.

**`scripts/backup.sh`, normatively.** The dump is written to a hidden `.partial` file and renamed into place only once it is complete and carries a custom-format header, so a half-written dump can never become "the newest backup" and evict a good one on the *next* run — which is what makes F-16's "retention prunes only after a successful new dump" true across runs rather than within one. A dump is attempted only after `pg_isready` confirms the discovered container really is the database and the credentials work. Container discovery **refuses on ambiguity** instead of taking the first match, because dumping the wrong database succeeds, is the right size, and is worthless. Retention prunes whole *sets* to `BACKUP_RETENTION_COUNT`. Scheduled at `BACKUP_SCHEDULE_TIME` (systemd user timer or host cron). Exit codes are three-valued because "the database was dumped and the key ring was not" is neither success nor failure: **0** complete set, **2** database only, **1** nothing usable produced.

**`scripts/restore.sh [--yes] [--no-keys] <dump>`.** Verify the archive; stop `web`; `pg_restore --clean --if-exists --no-owner`; put the key ring back from the sibling tar while `web` is down, so the application reads it at startup rather than after minting a fresh ring of its own; start `web` (DbUp verifies the schema and rolls an older dump forward — there are no down-migrations, ADR-0012). **The web application is started again on every path out of the script**, from an `EXIT` trap. That is not defensive style, it is a fix: `pg_restore` exits non-zero whenever it ignored any error (`exit_code = AH->n_errors ? 1 : 0`) and `--clean --if-exists` ignores errors routinely, so under `set -e` the previous ordering — restore, *then* start `web` — left the database restored and the application down, silently. Ignored errors are reported and downgrade the exit code to **2** rather than aborting.

**The drill is executable, not a procedure.** `scripts/restore_drill.sh` rehearses a recovery set against a scratch PostgreSQL container it creates and destroys itself: no maintenance window, no scratch host, no published port, and no write of any kind to the live database. Seven gates:

| Gate | Asserts |
|---|---|
| A | the archive is a readable custom-format dump with a non-empty table of contents |
| B | it restores into an empty database, and how many errors `pg_restore` ignored doing so |
| C | every table and view the migrations declare is present — the expected set is read out of `src/MyRestaurant.DataAccess/Migrations/*.sql`, so a new migration extends this gate by itself, and DDL that stops matching the patterns is reported rather than silently passing on an empty expectation |
| D | DbUp's `schemaversions` carries one row per migration file, so the schema is at a version this code *accepts* rather than merely structurally plausible (ADR-0012) |
| E | every §8.3 projection view still resolves against the restored tables — the one place in the schema where one object's correctness depends on nine others |
| F | a row census, reported rather than asserted: it is how you notice you have been faithfully backing up an empty database for a month |
| G | the key ring is beside the dump, is a readable tar, and contains keys |

`--strict` promotes reservations (ignored errors, an empty key ring) to failures; `--from-live` takes a fresh set first; `--keep` leaves the scratch container for inspection. The drill deliberately does **not** boot the application against the restored database — that is §16.4's `boot-smoke`, which now runs the drill immediately after proving the image starts, so every push rehearses recovery. OPERATIONS §6 is the runbook.

## 16. Testing

**16.1 Unit (Domain):** projection fold; §6.5 validation table (every rule, both outcomes); token computation vectors (fixed secret/uuid/window → expected Base64Url); PHC encode/parse round-trips; obligations pipeline state machine. Hand-written fakes preferred; NSubstitute acceptable; no Moq (F-20).

**16.2 Integration (DataAccess, real PostgreSQL):** every Identity store method; every CHECK/UNIQUE/composite-FK in §8.2 (attempt each forbidden shape, assert rejection); view ≡ fold equivalence on randomized sequences; locking protocol (concurrent send vs close — no event after close, settled total consistent); lazy `guest_order` creation race; reminder scan semantics incl. `ON CONFLICT` idempotence; migration idempotence (run twice).

**16.3 End-to-end (Playwright), minimum scenarios:**
1. Fresh stack → `/setup` bootstrap (passkey via virtual authenticator, TOTP, admin granted) → `/setup` now 404.
2. Admin creates table → pairing code → device pairs at `/display/pair` → `/display/{table}` shows rotating QR that **changes across a window boundary**.
3. Guest scans (simulated URL from current token) → registers with passkey (slowly — grant outlives token) → joins; sitting created.
4. Guest stages 2 adds + note → Send → kitchen gets one loud alert → lines pending.
5. Second guest joins via fresh token → sees first guest's order live; first guest sees roster update.
6. Kitchen fulfills one line → guest sees fulfilled badge.
7. Guest tries to remove the fulfilled line → whole batch rejected with per-op reason; removing their pending line succeeds.
8. A send sits unfulfilled 60 s → exactly one reminder alert.
9. Counter adjusts a price with reason → guest sees old → new with reason.
10. Counter closes (pending-line warning shown) → table flips to settled read-only; totals match.
11. Guest hides a closed order → it disappears from their own history (staff and admin views unchanged); admin filters the hidden-records view by username → Unhide restores it.
12. Admin resets a TOTP-enrolled user → user password sign-in → forced password change → forced TOTP re-enrollment → lands home; passkey sign-in path also hits the pipeline.
13. Passkey sign-in of a TOTP-enrolled user → **no** TOTP challenge.
14. Expired token URL → friendly expiry page; token from previous window → accepted.
15. Admin rotates a table's join secret → in-flight token dies; display's next window works.
16. Admin works the four administration indexes at 375×667 → no surface is wider than its own viewport, every row's action lies inside it, every control is at least 44px tall (§11.12).

**16.4 CI:** GitHub Actions — **tree hygiene**, **repository governance**, shell lint, build, unit, integration (service container PostgreSQL), E2E (Playwright/Chromium, all sixteen §16.3 scenarios), then boot the production image against real PostgreSQL and **take a backup of that instance and drill the restore** (§15) in the same job; publish image on tag. The drill is a CI gate rather than a runbook step because a recovery procedure nobody executes is a hypothesis — see F-38.

The **tree gate** (`scripts/check_tree.sh`, run by the `tree` job and as the first gate of `scripts/ci_local.sh`) asserts five properties of the checkout itself, before any tool that would report their absence as something else: no context-dump separator line in any authored text file (`export.sh` exempt — it writes them); no line made only of whitespace; LF endings with a final newline on every authored text file; every tracked `.props`, `.targets`, `.csproj` and `.slnx` well-formed XML; every tracked `.yml` and `.yaml` valid YAML. The first four are blocking everywhere, needing only git, grep and the Python standard library; the YAML parse degrades to a reported skip where no parser is installed, as the shellcheck gate does.

**Scope: the gate asserts properties of authored text, and must say what it skipped.** Two classes of tracked file are out of scope, and both must be excluded by the *same* decision so that no two gates can disagree about one file (**F-41**). **Generated text** — everything under `docs/llm/`, which `export.sh` already excludes from its own output as its `EXCLUDED_DIRECTORY` — is out of scope because a context dump's structure *is* the separator gate 1 forbids, and because a dump is a copy of the authored files, so checking it reports every real finding twice while reporting every correct separator as a defect. **Binary files** are out of scope because neither LF endings nor a final newline is meaningful for a compressed archive: a gzip stream ends where it ends, and "no final newline" accuses an intact `.tar.gz` of being truncated. Binary-ness must be determined by inspection (`grep -I`, which is what gates 1 and 2 already use) rather than from an extension list, which is a list somebody must remember to update. The gate must report its skip counts alongside its total, because a gate that silently declines to look at a file is indistinguishable from one that looked and found nothing.

It is here because of **F-40**, and the argument for it is the failure *mode* rather than the mistake. MSBuild imports `Directory.Build.props` before it evaluates anything, so one malformed character in that file fails `clean`, `restore`, `build`, `test` and the container build with the same message — and the message is `MSB4024: Data at the root level is invalid`, which sends a reader to look at MSBuild. Of the twenty-one files damaged identically in the incident, six broke anything at all and fifteen absorbed it in silence, because the offending line is a comment in YAML, in a Containerfile and in `.env`, a heading rule in Markdown, literal text in Razor markup, and a discarded selector in CSS. Damage that is catastrophic in one file and invisible in fifteen belongs to something that runs on every push.

The **governance gate** (`scripts/check_repository.sh`, run by the `governance` job and as the second gate of `scripts/ci_local.sh`) asserts the one layer every other gate is blind to (**F-42**). It has two halves, and they must not share an authority.

The **tree half must be blocking** and must need nothing but git and grep. It asserts that `SECURITY.md` is tracked and non-empty, that it names a reporting channel and points a reporter at §17; that `README.md`, `CONTRIBUTING.md` and `SECURITY.md` each name the others, so that no one of them can be rewritten into isolation — the edges are asserted rather than the files, because the way this breaks is a rewrite that forgets one edge and not a deletion; and that **no tracked file asserts a GitHub repository setting**. That last rule is the finding made unrepeatable: a document may state policy, which is true wherever it is read, and must not state platform state, which nothing in the repository can verify. The forbidden phrasings are a short, named list, and the files whose job is to record what this tree *used* to say are exempt by literal path, the way `export.sh` is exempt from the separator gate.

The **live-surface contract test** (`tests/MyRestaurant.WebApplication.Tests/Components/LiveSurfaceContractTests.cs`, an ordinary unit test in the `unit` job) asserts §11.10 against the Razor sources themselves (**F-47**). It **derives the interactive set from `[ExcludeFromInteractiveRouting]`** rather than from a list, which is the whole point of it: F-44 fixed one surface and recorded that "the other four" carried the same race, and there were never four. Seven assertions — that the scan read the tree and classified it (so it cannot pass vacuously, **F-41**); that every interactive surface with a loading state publishes each bit; that the two are published the same number of times in a file, which is how a surface rendering its element from more than one branch is caught; that `data-live` is answered by `RendererInfo.IsInteractive` exactly; that `data-loaded` comes from a named property rather than an inline expression; and that nothing but an interactive surface publishes either.

It reads source text rather than rendering anything, deliberately. The property under test is a property of the markup, a test renderer would need a container and a database per surface to assert the same string, and the §16.3 scenarios already exercise these attributes in a real browser. What a scenario cannot do is notice a **seventh** surface nobody wrote a scenario for, which is exactly how F-47 survived four slices. One list remains — the expected interactive set, written down so that adding a surface is a decision rather than an omission — and it is compared against the set the rule produces, so the two can only agree by both being right.

The **response-header tests** (`tests/MyRestaurant.WebApplication.Tests/Security/`, three classes in the `unit` job) assert §11.11 (**F-49**), and they are three rather than one because they assert three different kinds of thing. `ResponseSecurityHeadersTests` asserts what the header *says* — the directive set with no repetition, that the value is one policy rather than two (a stray comma would make it two, both enforced, and it would look approximately right), that no directive admits a wildcard or dynamic code, and that the WebSocket sources are derived from the request's host in both the expressible and the address-literal case. `SecurityHeadersMiddlewareTests` asserts *when and to what* — that the headers are on the response before the inner pipeline runs, which is the property that makes them survive a short circuit, and that a 404, a 429, a redirect and a 503 all carry them. `ContentSecurityPolicyContractTests` asserts that the tree still fits inside the policy, and it is the one that will actually catch a regression.

That last one exists because **a Content Security Policy is the only configuration in this project that becomes wrong by editing a file it does not mention.** One `<script>` block added to a Razor page, and that page silently stops working in a browser while every other test stays green. So it computes the category rather than reading a list (F-47's habit): it scans the markup and the static assets for inline script, inline event-handler attributes, off-origin references, `url()` and `@import` in the stylesheets, and `data:` URLs — asserting every count non-zero first, so it cannot pass vacuously (F-41). It also asserts the **concessions in both directions**: `style-src 'unsafe-inline'` is checked against the twenty-one components that still carry a `<style>` block, and `img-src data:` against the single favicon, so that removing either fact fails a test whose message says to tighten the policy. Nothing else would ever cause a concession to be dropped. And it reads `Program.cs` to assert the wiring three ways — that the middleware is installed, that it precedes everything that can answer a request, and that the framework's appending `frame-ancestors` convention is switched off — because none of those three is visible from the middleware's own file.

The **configuration-surface test** (`tests/MyRestaurant.WebApplication.Tests/Configuration/ConfigurationSurfaceTests.cs`, an ordinary unit test in the `unit` job) asserts §13's transport rule (**F-50**). It is here for the reason the CSP contract test is: §13's table, `.env.example` and `compose.yaml` are three restatements of one fact, and a restatement is what stops being true when somebody adds a setting. So it **derives the key set from `RestaurantOptions.FromConfiguration`** rather than listing it (F-47's habit, seventh application) and checks the three restatements against it. Five assertions: that the scan read the tree at all and read no key twice (**F-41**, and the guard every assertion below it needs); that every variable a refusal message in `Validate()` names is a variable the binding method reads, which is a second, independent observation of the same set from the same file and the one that catches a rename applied to one half; and then the three restatements — the `web` service's `environment` mapping, `.env.example`, and this section's own table.

The `compose.yaml` half is the one that found something. It reads the mapping **bounded to the `web` service**, because every service in that file takes an `environment:` block and a variable set on the wrong one is the failure that would otherwise pass. It reads source text with plain string operations and no parser, deliberately: a YAML dependency in the unit test project would be a package taken on to check five lines of indentation, and the question — *does this key appear as a child of that mapping* — is answerable without one.

The **compose dependency test** (`tests/MyRestaurant.WebApplication.Tests/Deployment/ComposeDependencyContractTests.cs`, an ordinary unit test in the `unit` job) asserts §14.1's dependency-condition rule (**F-53**). Three assertions: that the scan found the graph at all — four services and at least three `depends_on` edges, because the assertion that matters passes vacuously on an empty set (**F-41**); that no edge asks for any condition other than `service_started`; and that every edge names a service the file declares. It parses the file with plain string operations for the reason the configuration-surface test does, and it accepts the list form of `depends_on` and records it as `service_started`, because that is what both engines normalize it to and failing it would be reporting a finding on a correct file.

Unlike F-51's rule, this one **is** made executable, and the distinction is worth stating because the two findings arrived together. F-51's would-be gate — *no `image:` value lacks a registry component* — is a text assertion standing in for a behavioural contract, and it passes on a tree where the images are qualified and the stack still cannot start for the next reason. This one is not standing in for anything: the condition *is* the thing that hangs, the file is the only place a condition is asked for, and which conditions this file asks for is decidable from the text with certainty. What neither test replaces is running the canonical stack on the canonical engine, which remains open.

The **loopback target test** (`tests/MyRestaurant.WebApplication.Tests/Deployment/DevInstanceLoopbackContractTests.cs`, an ordinary unit test in the `unit` job) asserts §14.3a's address rule (**F-56**). It is the F-50 pattern rather than a grep: the authoritative side is `compose.yaml`'s published port, the restatement is each helper's `TUNNEL_TARGET` default, and the test derives the first and checks the second against it. Four assertions: that the scan read the published port and both helpers' defaults, so nothing passes vacuously (**F-41**); that each default's host and port are the ones compose publishes; that each host is an IP literal rather than a name, which is the finding; and that the address published is a loopback address, because the reason the rule exists — one address, no listener on `::1` — stops being true if the port is ever published on `0.0.0.0`, and a test that kept passing through that change would be asserting a coincidence. It reads two shell scripts and one YAML file as text, for the reason the two tests above do.

Deliberately **not** asserted: that no script anywhere says `localhost`. `run.sh` prints `http://localhost:8080` in a sentence telling a human what to open in a browser, which is correct — browsers resolve both and try both — and a gate that failed on it would be reporting a finding on a correct tree (F-41). The rule is about what a *program* dials, and the only programmatic dial in each helper is the one variable this test reads.

The **compose-substitution contract test** (`tests/MyRestaurant.WebApplication.Tests/Deployment/ComposeSubstitutionContractTests.cs`, an ordinary unit test in the `unit` job) asserts §14.1's `.env.example` half (**F-57**). Three assertions: that the scan read both sides — at least twenty interpolated variables and twenty assignments, because the assertion that matters passes vacuously against an empty placeholder set (**F-41**); that every variable interpolated inside an `environment:` mapping is assigned in `.env.example`; and that a variable whose compose default is *empty* is assigned empty there rather than given a plausible value, derived from the compose file rather than listed. It reads two files as text, for the reason the two tests above do.

**What is deliberately not asserted, and where it is asserted instead.** Whether a given engine applies defaults is not decidable from the tree — it is a property of a host, and a test that guessed at it from a version number would be reporting findings on correct trees (F-41). That question is answered by `scripts/check_compose_substitution.sh`, which is **not** a CI gate for the same reason `check_repository.sh`'s platform half is advisory: its subject is the machine, not the repository. It is a preflight the three helpers run before they do work, and a command an operator can run by hand. Its exit codes are part of its contract: **0** the engine applies them or nothing depends on it, **3** it does not, **2** it could not be determined here.

The **image reference test** (`tests/MyRestaurant.WebApplication.Tests/Deployment/ContainerImageReferenceContractTests.cs`, an ordinary unit test in the `unit` job) asserts §14.1's qualification rule across the whole tree (**F-60**). Three assertions: that the scan found a reference in each of the three positions it reads and at least ten in total, because both facts below it pass against an empty set (**F-41**) and a renamed constant would produce exactly that in silence; that **every reference names a registry**, which is the finding; and that **every image name resolves to exactly one reference**, which is the fact the first two cannot reach — a fully qualified reference that has drifted to a different *version* from the one the canonical stack runs breaks nothing, reports nothing, and means the suite passed against a database this project does not deploy.

**Why this is not a reversal of F-51's ruling**, which declined to make the same section executable. That ruling's objection was that a check for a missing registry component is a text assertion about a file whose real contract is behavioural, and would pass on a tree where the images are qualified and the stack still cannot start for the next reason. It stands, the CI job it names remains the open item, and this test is not offered as a substitute. What this test asserts is a different property and one that is wholly a property of the tree: that a rule stated for the repository is applied at every place in the repository it applies to. It was not — §14.1 said it about `compose.yaml`, `restore_drill.sh` had been doing it since Slice 16, and four other references were short names. It reads text files as text, and has no opinion about anything under `docs/`, where both the correct and the incorrect spelling appear on purpose because F-51's own ledger row is about the difference.

The **handheld layout test** (`tests/MyRestaurant.WebApplication.Tests/Components/HandheldLayoutContractTests.cs`, an ordinary unit test in the `unit` job) asserts §11.12 (**F-59**). Four assertions, and the choice of which four is the interesting part, because whether a screen is *comfortable* is a judgement no test will make. What is decidable is the structure of the rule. **One breakpoint, expressed as `min-width`, with no `max-width` query anywhere** — three checks in one fact, because a second breakpoint, an inverted one and a single exception fail in three different ways, and the block is additionally asserted non-empty so the fact is not satisfied by deleting the wide layout (**F-41**). **No component re-declares the shared vocabulary**, scanned as two selector prefixes rather than an enumeration of class names, with Razor comments stripped first — a `<style>` mentioned inside an `@* … *@` comment is prose, and counting it would make the scan's own non-vacuity guard pass on nothing. **Every cell in a record list carries a `data-label`**, counted exactly, because that attribute is the replacement for the column header the card layout loses and a page that omits it renders bare values. And **the per-page vocabularies §11.12 replaces survive in exactly the set of files still expected to hold them**, which is F-47's pattern: the test keeps one list, compares it against the set the tree produces, and finishing the migration means deleting entries from it — a decision somebody makes rather than an omission nobody notices.

**What it deliberately cannot assert, and what does.** Whether a control is reachable inside a 375px viewport is not decidable from text at all: it is a question about layout, and only a layout engine answers it. **§16.3 scenario 16 is that answer** — an administrator walks the four administration indexes in a browser context laid out at 375×667, and three numbers the page computes are asserted. No surface is wider than its own viewport (`document.documentElement.scrollWidth` against `clientWidth`, one pixel of tolerance). Every row's action and every page's primary action lies inside it (`getBoundingClientRect`, same tolerance). Every control is at least 44px tall, which is `--touch-target` written as the number a finger has to hit rather than read back from the custom property — a page that redefined the variable would satisfy a check that asked the page for its own minimum.

**Three things about that barrier are rulings rather than implementation.** The **viewport is asserted before anything else**, from the document rather than from the option that set it: at any wider width every other assertion passes and means nothing (F-41), and the comparison is a ceiling with a scrollbar's allowance under it because `clientWidth` excludes a classic scrollbar and every one of these pages scrolls vertically. **A count of measured controls is asserted**, for the same reason and against the same failure: a renamed selector produces an empty set that satisfies every verdict. And **the widest element on the page is collected but never allowed to fail a run.** A page may legitimately contain an element wider than the viewport inside a scroll container of its own — `.page-head-areas` is exactly that — so a walk that failed on those would report a finding on a correct tree. The walk skips anything inside a scroller and even then only writes the sentence that explains a failure; the two numbers decide.

**The set of surfaces this barrier visits is a list, and the list is the migration.** Four today, the four Slice 30 restructured; Stage 1b of `docs/MENU_AND_HANDHELD_PLAN.md` adds a line to it per page it converts. Same arrangement as the handheld layout test's own `StillExpectedToCarryRetiredTableVocabulary` and for the same reason (F-47) — finishing is then something somebody decides rather than something nobody notices.

**Why this arrived a slice late, which is itself a finding.** It was deferred out of Slice 30 on the stated ground that the §16.3 scenarios share one browser context, so a second viewport meant either an extra context per run or a resize every later scenario inherits. The harness does not work that way and never did: it holds one *browser*, and `StartInstanceAsync` mints a fresh context per instance. A viewport belongs to a context, so there was nothing to leak. That is **F-62** — a gap justified by a property of the tree that the tree contradicts — and it is recorded because the same sentence had by then been copied into this section, into F-59's ledger row and into the plan.

Beside it, and smaller by design, the **document-version test** (`…/Documentation/SpecificationVersionTests.cs`) asserts that every versioned document in `docs/` states the version its own newest history entry states, and that the entries descend (**F-48**, **F-58**). Two assertions, no third. It is a test rather than a habit because it has drifted three times: the v1.3 entry below corrects exactly this from Slice 16, Slice 22 did it again — a v1.7 entry under a v1.6 header — and then `REQUIREMENTS.md` sat for six slices saying *"Revision 4"* above a revision history whose newest entry was *"Rev 5"*.

That third occurrence is why the test **no longer names a document.** F-48's fix pinned this file by name in a `const string`, which made the rule true of one file and silent about its sibling — F-46's lesson one register lower, and a list of one is still a list. The subject is computed now: every Markdown file in `docs/` with both a header version and a history section is checked, both vocabularies (`**Version … / ## Changelog / **v1.15`, and `**Revision … / ## Revision history / - **Rev 6`) are admitted by one pattern rather than tabled per filename, at least two documents must turn out to qualify, and a **half-versioned** document — a header version with no readable history, or the reverse — is a finding rather than a skip, because those are the two shapes in which a document could quietly leave the subject. It still deliberately checks nothing about content; a gate that reaches past what it can decide reports findings on correct trees (F-41).

**The forbidden list covers package settings as well as repository settings (F-46).** The first version of this gate enumerated the switches on the repository page — issues, pull requests, discussions, the wiki — passed on its first run, and was already wrong: OPERATIONS §14 had asserted the visibility of a *package*, in the indicative, about a package that did not yet exist. A repository's visibility and a package's visibility are separate switches, and GitHub's own documentation disagrees with itself about which way the second falls for a `GITHUB_TOKEN` publish, which is the strongest available argument for not asserting it in a document at all. A rule stated as a rule and enforced as a list of examples is enforced as a list of examples; the list is therefore maintained as part of the rule rather than as an afterthought, and the correct repair is always to state the intention and name where the switch lives.

The **platform half must be advisory**, must degrade to a reported skip with no token, no network, no `curl` or no `python3`, and must never move the exit code. **It must also reach the release run.** A called workflow sees only the secrets it is handed, so `release.yml` passes the one token this half needs by name rather than inheriting all of them — without that, the advisory settings report was silently absent from the only run that creates a package, which is the run whose report is worth the most (F-46). It reads the repository object and the private-vulnerability-reporting endpoint and reports the issue-tracker state, the wiki state, whether a description is set, and whether private reporting is enabled. Advisory is a ruling, not caution: a fork's settings are the fork's business, and a gate that failed somebody's build over this maintainer's disclosure preferences would be wrong about the licence this project ships under. A token without `administration:read` is reported as *unknown* rather than as a finding, because a fork's pull-request token will not have it.

Deliberately **not** folded into the tree gate. That script's five gates are all offline, all blocking, and all assertions that a file somebody wrote is machine-readable; half of this one is none of those, and a gate whose halves carry different authority should not answer to one exit code.

`boot-smoke` additionally **fetches `/source` anonymously and fails unless the response contains the commit the image was built from.** The stamp travels from a build argument through an MSBuild property, an assembly attribute, a parse and a component (§11.9), and every link in that chain fails *silently*: the page still renders, and it renders "Not recorded", which reads as a configuration choice rather than as a defect. The commit appearing in the response is the assertion nothing weaker can satisfy, and it doubles as a reachability check — no cookie is sent, so a regression that put the licence offer behind authentication fails here.

**Releases** (`release.yml`) call this workflow rather than restating its gates, then derive the version from the tag, pass it and the commit into the image build so the published image reports what the registry called it, and open a GitHub release on the tag. The release step is downstream of the push and idempotent, so a re-run updates the note instead of failing.

## 17. Security posture and accepted risks

Threats mitigated: **content injection and clickjacking, bounded by a Content Security Policy on every response** (§11.11, ADR-0013) — `script-src 'self'` with no hash, nonce or `unsafe-*`, so the six `MarkupString` sites are the only raw HTML in the system and an injection that got past Razor's escaping would still be inert; `frame-ancestors 'none'` on every response rather than the framework's `'self'` on component endpoints; `form-action 'self'`, which is the one thing antiforgery does not cover, since a token protects against a forged request and says nothing about where a real form posts to; and `Referrer-Policy: same-origin`, because §4.3's join token rides in a query string and a browser default is not a deployment guarantee; static-QR capability theft (rotating tokens, ≤120 s life, per-table secret rotation); Argon2 memory DoS (semaphore + rate limit + lockout); display theft (revocation; device holds no secret worth extracting; join secret never leaves the server); credential stuffing (Argon2id, lockout, passkeys-first); stale sessions after admin action (5-minute stamp revalidation); half-applied schema (fail-fast migrations); pairing brute force (hashed single-use codes, TTL, 5/min/IP).

Accepted, by ruling or by design: token replay within ≤120 s (bounded by membership/visibility rules); WAN dependence of in-house ordering (hairpin — F-06); quick-tunnel passkeys work per run but the per-run URL is not persistent (PSL — re-register each demo; named tunnel for durability); counter role may operate password-only (no passkey mandate); guest sees table-mates' display names and orders (that's the product); no rate limit on authenticated order sends beyond all-or-nothing validation (single-restaurant trust model).

**No rate limit on `/register` (§11.8), by ruling — F-37.** It is the second anonymous surface that writes, and the more consequential one: a `person` row outlives its request where a failed pairing attempt does not. Four things bound it meanwhile, none of them a policy: registration is a **two-request** flow behind an antiforgery token and a protected ticket cookie, so it is not a scriptable single POST; the password is capped at 256 characters, so an anonymous caller cannot ask for unbounded Argon2id work; §3.2's semaphore bounds concurrent hashes process-wide; and a spam account holds no capability — a guest is the absence of a grant (§3.7), so the worst outcome is rows, not access.

**Coordinated disclosure, and this section's part in it (F-42).** The project must offer a **private** channel for security reports, named in `SECURITY.md`, and that channel is the single exception to §18's no-outside-contributions rule. The asymmetry is the argument: refusing a feature costs the person who wanted it, and the AGPL has already given them the source and the freedom to build it; refusing a report costs an operator's *guests*, who never chose this software, cannot read `CONTRIBUTING.md`, and have no fork to run. **This section is part of the offer rather than a disclaimer.** Every ruling above was argued and written down, so `SECURITY.md` must send a reporter here first — a report that asks for one of them to be *re-ruled* is an argument and gets read as one; a report that presents one as news gets pointed back at the paragraph that decided it. What the project owes in return is stated as targets rather than as an SLA a single maintainer would miss, and the advisory is published when there is a release to upgrade to rather than held open-endedly. A fork operator is the security contact for their own instance, and `SECURITY.md` says so, because nothing here can reach their box, their data or their guests.

The reason this is not a two-line addition, recorded so it is not rediscovered: the limiter is configured inside `AddRestaurantDisplays`, and `RateLimiterOptions.OnRejected` and `RejectionStatusCode` are single-valued. A second `AddRateLimiter` call adding a registration policy silently takes over the rejection handler, and a refused registration would then answer with §4.2's *"Too many pairing attempts from this device"* — worse than no limit, because it is wrong and looks deliberate. Doing it properly means `OnRejected` dispatching on the endpoint. When it lands, §13 gains the window and permit count beside `DISPLAY_PAIRING_*`.

## 18. Governance

Single-owner project; no outside contributions (`CONTRIBUTING.md`) — **except a security report, which is not a contribution** (`SECURITY.md`, §17). **A document in this tree states policy, never platform state:** what the project will do is true wherever it is read, whereas a claim about a hosting provider's settings page is unverifiable from inside the repository and was in fact false for as long as it existed (F-42). The rule is enforced by `scripts/check_repository.sh` rather than remembered. **Atomic documentation:** a behavior change lands in one commit with its `REQUIREMENTS.md`, this specification, `DOCUMENTATION_REVIEW.md` ledger, and ADR edits. ADRs are edited in place with a History line (never duplicated); supersessions say so explicitly. This specification's version bumps (1.0 → 1.1 …) with a dated changelog appended at the bottom when normative content changes.

## 19. Build order (milestones)

- **M1 — skeleton:** solution layout (§2), Containerfile, compose dev profile, DbUp with `0001_initial_schema.sql`, health endpoints, OTel wiring, `run.sh`.
- **M2 — identity:** Dapper Identity stores, Argon2id hasher (+floor guard, semaphore), passkeys, TOTP + recovery codes, lockout, obligations pipeline, `/setup` bootstrap, roles/policies, security events, admin user management + reset, and the person's own profile page (§11.6: display name, contact details, voluntary password change). The profile page belongs to this milestone but was not listed here originally and landed after M3 — see F-35.
- **M3 — tables & joining:** table CRUD + join secrets + rotation, display pairing + device auth + `/display`, token generate/validate + metrics, grant cookie, join flow, sittings + membership.
- **M4 — ordering:** living order + locking protocol, staging UI, batch send + validation, staff edits, fulfillment/reversal, projections + fold + equivalence tests, kitchen surface + alerts + reminder service.
- **M4 close-out — restaurant time:** the §8.1 rendering rule actually honoured on every surface (`RestaurantTime`, replacing eighteen `ToLocalTime()` call sites), the §13 clock-format decision, and the §11.7 footer clock. Scheduled here rather than inside a feature slice because a half-applied time convention is worse than a uniformly wrong one, and ahead of M5 because a wrong time on a settled bill is a different order of problem from a wrong time on a roster — see F-36.
- **M5 — counter & administration:** bills, price adjustment, close & settle, end-of-day, counter fallback QR, menu management + events, event explorer, hide/unhide, post-close corrections.
- **M6 — hardening & production:** full E2E suite (§16.3), backups + restore drill, cloudflared production profile + tunnel docs, quick-tunnel demo script with warning, OPERATIONS runbooks, CI pipeline. The **guest registration surface** (§11.8) also lands here rather than in M2, where it belonged: R§4.3 required it from rev 2 and no milestone claimed it, and the gap surfaced only when §16.3 scenario 3 went to write it — see F-37.
- **M7 — the menu, and the screen it is read on:** the first work driven by a user rather than by this document. Stage 1 is the §11.12 handheld contract — its vocabulary and the four index pages in Slice 30, its 375px end-to-end barrier in Slice 32, the remaining surfaces still open — and it lands *first* even though the menu was asked for first: the menu work adds four surfaces that are all read from a phone, and writing them before the responsive vocabulary exists means writing them against the shape F-59 was found in and then touching all four again. Stage 2 is ADR-0014's schema — sections, descriptions and explicit ordering — Stage 3 is the surfaces that read it, and Stages 4 to 6 are images, likes and comments, the last of which is recorded as *not startable* until §17's rate-limit ruling is revisited. `docs/MENU_AND_HANDHELD_PLAN.md` is the plan and is struck through as it lands.
- **M6 close-out — the release:** the build stamp and the source offer (§11.9), because both are things a first tag makes true forever and neither is worth adding *after* the version people cite. Publishing images for other people to run is what turns "which build is this?" from a question the operator can answer from memory into one the instance must answer itself, and what makes `CONTRIBUTING.md`'s promise that a fork "owes its users the same" into something a fork can actually discharge — see F-39. Then `scripts/ci_local.sh --with-all`, a drill against the real stack, and the tag.

---

## Appendix A — Decisions register (ruling → embodiment)

| Ruling / finding | Decision | Embodied in |
|---|---|---|
| F-06 | Named Cloudflare tunnel = production origin (persistence, not a passkey prerequisite); Argon2id "robust" params; hairpin risk accepted; GoTunnels reference-only, bearer sessions rejected | §3.2, §3.3, §14, §17 · ADR-0005, ADR-0008 |
| F-06a | Quick tunnels support passkeys via per-request RP derivation (`ServerDomain=null` + `PublicOriginMiddleware` + `ValidateOrigin` against `RESTAURANT_TRUSTED_ORIGIN_PATTERNS`); only the per-run URL is non-persistent | §3.3, §14.3 · ADR-0005 |
| F-07 / Q1 | Living order per guest per sitting; client staging; batch sends; one alert per send; pending → fulfilled lifecycle; guests remove own pending only; reversal event | §6, §10, §11.1 · ADR-0007, ADR-0002 |
| F-08 / Q2 / Q3 (supersedes F-09 draft) | TOTP on password path only; no per-user toggle; reset wipes password+TOTP (if enrolled) and forces change + re-enrollment via obligations pipeline on any sign-in path | §3.4, §3.5, §3.7 · ADR-0010 |
| F-12 / Q4 / Q5 | Rotating HMAC join tokens; `table_display` device principal; pairing codes; join grants; counter fallback QR; printed QR removed | §4, §11.5 · ADR-0009 |
| F-10 / F-10b / F-11 | Post-close admin corrective events beside immutable settled total; deactivate-not-delete; guest as actor_role not stored role | §5.3, §6.7, §3.7, §8.2 |
| F-13 / F-14 / F-15 | No Redis v1 (broadcaster interface); compose canonical / Aspire optional; OTLP-generic (`UPTRACE_DSN` translated in run.sh only) | §9, §12, §14 · ADR-0006, ADR-0004 |
| F-16 / F-17 | Backups pg_dump -Fc + retention + keys volume; run.sh defined | §15, §14.4 |
| F-36 | All instants rendered in `RESTAURANT_TIME_ZONE` for every reader, through one type; `RESTAURANT_CLOCK_FORMAT` settles the 12-vs-24 question; ticking footer clock states the convention on every page | §8.1, §11.7, §13, §19 |
| F-18 / F-19 | Menu item event log; lockout 5/5min, username 3–64 citext, currency/timezone defaults | §7, §3.1, §13, §8.2 |
| F-20 | Hand-written fakes; NSubstitute ok; no Moq | §16.1 |
| F-37 | Guest self-registration is a real surface at `/register`, specified rather than assumed; a passkey is offered first and may be declined only when a password was set; no rate limit in v1, with the reason it is not a two-line addition recorded | §11.1, §11.8, §17, §19 · R§4.3 |
| F-38 | The restore path had never been executed and could not have completed: `pg_restore` exits non-zero on ignored errors, ahead of the line that restarted `web`. Nothing captured the Data Protection key ring despite §15 requiring it. A failed dump could evict a good one on the following run. Container discovery took whichever match came first | Both scripts rewritten; `scripts/restore_drill.sh` added and executed by CI on every push, so the drill is rehearsed rather than documented | §15, §16.4 · O§6, O§8, O§14 · `scripts/backup.sh`, `scripts/restore.sh`, `scripts/restore_drill.sh`, `.github/workflows/ci.yml` |
| F-39 | The program could not say what it was, and did not offer its own source. Nothing stamped a version, so every assembly reported the SDK's default and every trace left the process unversioned; and no surface discharged AGPL §13, although `CONTRIBUTING.md` told forks they owed it | Version and source revision stamped through the `Containerfile` into `AssemblyInformationalVersionAttribute`, read by `BuildInformation`, reported at `/source`, in a colophon on every page in both layouts, and as OpenTelemetry's `service.version`; `RESTAURANT_SOURCE_URL` set by the operator; no off switch; CI fails unless `/source` names the commit it was built from | §11.9, §12, §13, §16.4, §19 · R§8 · `Configuration/BuildInformation.cs`, `Configuration/SourceRoutes.cs`, `Components/Pages/Source.razor`, `Components/Layout/AppColophon.razor`, `Directory.Build.props`, `Containerfile`, `.github/workflows/` |
| F-40 | Twenty-one tracked files carried an appended context-dump separator line, and `docs/BUILD_PROGRESS.md` carried a second one buried mid-document. `Directory.Build.props` was one of them, which made it invalid XML and failed every MSBuild verb in the repository. Root cause: a tool read a context dump and treated the decoration between files as content, where the authoritative terminator is the byte count in each `METADATA` block | `scripts/check_tree.sh` added and run as the first gate both in CI (`tree` job) and locally; five properties asserted, four of them blocking with no dependencies; two pre-existing `.editorconfig` violations fixed so the gate lands at zero noise | §16.4 · O§14 · `scripts/check_tree.sh`, `scripts/ci_local.sh`, `.github/workflows/ci.yml`, `.editorconfig` |
| F-41 | The F-40 gate failed 1321 times on a clean tree the day after it landed, at gate 1, so the four gates behind it never ran — and CI's `tree` job was red for the same reason. It exempted `export.sh` because that script writes separators, but not `docs/llm/`, the directory `export.sh` writes them *into*: 1276 findings were the two committed context dumps, whose structure is the separator. The other 45 were `tail -c 1 \| wc -l` accusing every tracked `.tar.gz` and `.zip` of being truncated, gate 3 having no binary guard where gates 1 and 2 had one by accident | Scope is now decided in one place (`is_authored_text`) that gates 1, 2 and 3 all consult, so they cannot disagree about a file: generated text excluded by path prefix, binary excluded by inspection via `grep -I` rather than by extension list. The gate reports its skip counts beside its total. Sensitivity re-proven against all five damage patterns re-introduced outside `docs/llm/`, plus a binary file outside it that must be skipped rather than accused | §16.4 · `scripts/check_tree.sh` |
| F-42 | **The repository invited strangers to read the source and offered them nowhere to say what they found — and the one document that addressed the question asserted a repository setting that was not set and never had been.** Asked from outside on 2026-08-05, with every other gate green and a first tag about to be cut, the published repository answered: `has_issues: true`; no `SECURITY.md` at the root, in `.github/` or in `docs/`; private vulnerability reporting disabled; no description. `CONTRIBUTING.md` had said since rev 1, in the indicative, that the issue tracker was switched off. So a reader — and the AGPL exists to produce readers, who are the population that finds security defects — had: an open tab the only relevant document denied, no Security tab entry, no private form, no address anywhere in the tree, and a notice that pull requests are closed unreviewed. The only channel that worked was the one the documentation said did not exist, and it was public, so the first thing anybody would have done with a forgeable capability token was publish it. **A new category for this ledger.** F-40 was the specification right, the code right, and the transport between them damaging files. This is neither: the specification is right, the code is right, and the *repository settings* disagree with the documents — a layer no document had ever described, no test can reach, and `check_tree.sh` cannot see. Nothing had been lost yet only because nobody had tried. | `SECURITY.md` at the root: the private channel, no bounty said first rather than discovered afterwards, scope in both directions, single-maintainer timelines stated as targets rather than an SLA nobody would meet, newest-tag-only support, and a section sending a reporter to **§17** before they spend an evening re-deriving a decision that was already argued. `CONTRIBUTING.md` states the carve-out and its reason: refusing a feature costs the person who wanted it, who has the source and the freedom to build it, while refusing a report costs an operator's *guests*, who never chose this software and have no fork to run — so the two get opposite answers. **And the row names something executable** (F-38's lesson, third application): `scripts/check_repository.sh`, a sixth gate in two deliberately unequal halves. The tree half is blocking on git and grep alone — a policy exists, the three documents a reader arrives through cross-reference so none can be rewritten into isolation, and **no tracked file asserts a repository setting**, which is this finding made unrepeatable. The platform half reads the API and is advisory, because a fork's settings are the fork's business and failing their build over this maintainer's disclosure preferences would be wrong about the licence this project ships under. | R§8 (rev 4), R§10 · S§16.4, S§17, S§18 · O§13, O§14, O§16 · `SECURITY.md`, `CONTRIBUTING.md`, `scripts/check_repository.sh`, `scripts/ci_local.sh`, `.github/workflows/ci.yml`, `README.md` · BUILD_PROGRESS M6 Slice 20 |
| F-43 | `scripts/backup.sh` and `scripts/restore_drill.sh` picked a container engine by `PATH` order — podman first, per ADR-0004 — which is right on a host with one engine and wrong on a GitHub runner, whose image installs a static podman bundle beside a working Docker daemon. Every container in `boot-smoke` belongs to Docker, `podman exec <docker id>` fails with "no such container", and the script had one message for every way that command could fail: it reported a database that would not answer for its own credentials, two steps after `/healthz/ready` had returned 200 from an application talking to that database | `CONTAINER_ENGINE` honoured by both scripts and pinned to `docker` on the two `boot-smoke` steps; when a container is named, the engine is chosen by asking each available engine `container inspect` — a fact rather than a heuristic, and `inspect` rather than `ps --filter name=` because CI passes an id. Discovery without a name still refuses on ambiguity (F-38's rule, unchanged). "Knows it but it is not running" and "did not answer `pg_isready`" are now different lines, each naming the engine it asked | §15, §16.4 · O§6 · `scripts/backup.sh`, `scripts/restore_drill.sh`, `.github/workflows/ci.yml`, `.env.example` · BUILD_PROGRESS M6 Slice 21 |
| F-44 | §16.3 scenario 10's harness waited on `section.counter-board`, an element present in **every** state of the component including "Loading the floor…", so the barrier was satisfied by the first paint and asserted nothing. Prerendering emits fully loaded markup, which is why it passed on a workstation; the window is the circuit hand-over, when the component is rebuilt from nothing and the DOM returns to loading for as long as two queries take. The reader landed there and got two empty lists — which also satisfied the `Assert.DoesNotContain` on the line above, off the same empty screen, for the wrong reason | `CounterBoard.razor` publishes `data-live` (from `RendererInfo.IsInteractive`) and `data-loaded` (from `_loaded`), and the selector demands **both**: `data-live` alone steers a reader *to* the circuit's first render, which is the one instant neither list exists; `data-loaded` alone matches the prerendered markup, which is loaded and inert. Recorded and not fixed here: the other four live surfaces publish `data-live` with no loaded bit and carry the same latent race, passing only because their callers go on to wait for specific content | §11.3, §16.3 · `Components/Pages/Counter/CounterBoard.razor`, `tests/.../Harness/CounterJourneys.cs` · BUILD_PROGRESS M6 Slice 21 |
| F-45 | **The image build's context was the entire working tree, and `.gitignore` — correct, and irrelevant — was the only thing that had ever been asked about it.** `Containerfile` has said `COPY . .` since M1 with the repository root as context and no `.dockerignore` anywhere in the tree. Measured on a fresh clone: 458 files, 31 MB, of which `docs/llm/` is 16 MB and `.git/` is 11 MB — 87% the build cannot use. The size is the harmless half. A build context is not a commit, so every path `.gitignore` names was copied into the builder anyway: `.env`, `.dataprotection/`, and every `*.dump` and `*-dataprotection.tar` §15 writes — the file OPERATIONS §8 calls the key material in the clear. And the order was documented: O§12 step 1 takes a backup, step 2 runs `up -d --build`. CI escaped by accident of step ordering alone, `docker build` running before `backup.sh` writes anything. Nothing in this project could have seen it: `check_tree.sh` reads `git ls-files`, and every file at issue is git-ignored | `.dockerignore` as an **allow-list** (§14.1a) — a deny-list would have to be extended for tomorrow's secret by somebody who remembered to, and it is a deny-list that failed here. Context falls to 169 files and 1.6 MB. **And the row names something executable**: a guard in `Containerfile` immediately after `COPY . .` fails the build unless the context root is exactly the allowed set, every required path is present, and no `bin`/`obj` survives under `src` — so the assertion runs on a workstation and not only in CI, and an ignore-file that was renamed, shadowed by a `.containerignore`, or overridden with `--ignorefile` stops the build instead of producing a slower one | §14.1a, §15 · O§12, O§14 · `.dockerignore`, `Containerfile` · BUILD_PROGRESS M6 Slice 22 |
| F-46 | **The rule that made F-42 unrepeatable was enforced as a list of examples, and the example it omitted was already in the tree.** Gate 3 of `scripts/check_repository.sh` landed in Slice 20, passed on its first run, and reported "none" — while `docs/OPERATIONS.md` §14 told a reader, in the indicative, that the published images carried a particular visibility setting and that pulling one therefore needed no registry credentials: a claim about a **package** settings page, about a package that did not exist yet, in the paragraph telling an operator how to deploy from a registry. A repository's visibility and a package's visibility are separate switches, and GitHub's own documentation contradicts itself on which way the second falls for a `GITHUB_TOKEN` publish, so the sentence was not merely unverifiable — nobody could say whether it was true. Found by the same reading that found F-42 and F-39: the moment before publishing is a distinct review. A second half of the same blind spot: the gate's advisory report on the published settings never ran on a release at all, because a called workflow sees only the secrets it is handed and `release.yml` handed it none — so the one run that *creates* a package produced no report about packages | The forbidden list gains the package-settings group and, more to the point, is maintained as part of the rule rather than as an afterthought (§16.4). `OPERATIONS.md` §14 states the intention — the images are meant to pull without a login — and names where the switch lives for an operator who hits a 401, which is the form that stays true whichever way the checkbox falls. `release.yml` passes `ADMIN_READ_TOKEN` to the called workflow **by name** rather than inheriting every secret, so the advisory settings report reaches the run whose report is worth the most | §16.4, §18 · O§14 · `scripts/check_repository.sh`, `docs/OPERATIONS.md`, `.github/workflows/ci.yml`, `.github/workflows/release.yml` · BUILD_PROGRESS M6 Slice 22 |
| F-47 | **F-44's fix was correct and its account of the remaining work was a list, and the list was wrong.** Slice 21 gave `/counter` a `data-loaded` bit beside `data-live` and recorded that *the other four live surfaces* carried the same latent race. There is no list of four. `App.razor`'s `RenderModeForPage` makes every routable page interactive unless it carries `[ExcludeFromInteractiveRouting]` — six pages, plus the island hosted inside `/table/{id}` — and **`/table` had been one of them since M3 while publishing nothing at all**: no id, no attribute, and a "Looking up your tables…" state whose empty list is indistinguishable from *you are not seated at a table*. The enumeration "the five live surfaces" appeared in five doc comments and nobody had ever asked the routing rule. A second thing the list hid: on `/display/{table}` the missing bit is not a loading bit. Both branches carrying that surface's id are reachable fully resolved, and one of them is the "Preparing the join code…" card — so the barrier `[data-live='true']` returned happily on a screen with no QR on it and §16.3 scenario 2 then spent sixty seconds in `ReadJoinQrPathAsync` failing two steps from the cause. This is F-46's shape for the third time in three slices: a rule stated as a rule, enforced as a list of examples, green the whole time. | §11.10 states the contract, names the routing rule as its subject, and defines `data-loaded` as *the surface has what it renders itself for* rather than *a query returned* — which is why the display's predicate is `_loaded && _qr is not null` and is documented as the honest reading of the rule rather than an exception to it. All six surfaces publish both bits; the four §16.3 barriers demand both. **And the row names something executable** (F-38's lesson, fifth application, again unasked): `LiveSurfaceContractTests` derives the interactive set from the exclusion attribute rather than from a list, and fails on any surface that publishes one bit, the wrong expression, or neither. Run against the Slice 22 tree it fails on exactly the four F-44 named, plus `/table`, plus `TableDisplay` by count mismatch. | §11.10 (new), §16.4 · `Components/Pages/Table/TableArea.razor`, `.../Display/TableDisplay.razor`, `.../Kitchen/KitchenBoard.razor`, `.../Counter/CounterSitting.razor`, `.../Table/TableOrderSurface.razor`, `tests/MyRestaurant.WebApplication.Tests/Components/LiveSurfaceContractTests.cs` · BUILD_PROGRESS M6 Slice 23 |
| F-48 | This document's header read **v1.6** while its own changelog carried a **v1.7** entry. The v1.3 entry records the identical drift from Slice 16 and corrects it in the same paragraph, which makes this the second occurrence of a defect already found, corrected, and written down — the shape F-46 is about, one register lower. A stated version is the thing every other document, the ledger and `_CHANGES.md` all cite; two of them in one file is the file disagreeing with itself in the field a reader trusts most. | The header is the version the changelog says it is, and `SpecificationVersionTests` asserts it on every `dotnet test` — two assertions, header-matches-newest and entries-descend, the second because without it "newest" is a property of whoever last edited the top of the list rather than of the file. Nothing about dates or content: this is arithmetic on one string, and a gate that reaches past what it can decide reports findings on correct trees (F-41). | §16.4 · `tests/MyRestaurant.WebApplication.Tests/Documentation/SpecificationVersionTests.cs` · BUILD_PROGRESS M6 Slice 23 |
| F-49 | **This application has published a Content Security Policy since M1, nobody wrote it, no document mentioned it, and it covered one directive on a subset of its responses.** The tree contains no reference to CSP, `nosniff`, `Referrer-Policy` or `frame-ancestors` — not in the application, not in `Caddyfile`, not in a document. It emits one anyway: `AddInteractiveServerRenderMode` installs an endpoint convention that appends `frame-ancestors 'self'` to the header on component endpoints, because WebSocket compression plus cross-origin framing is an attack the framework will not ship one half of. So the honest statement of the gap is not "there is no policy" but "there is a policy this project cannot reason about": one directive, at `'self'` rather than `'none'`, on pages but not on static files, the health endpoints, the clock or the sign-out POST — and appended with `StringValues.Concat`, so anything written beside it would have been *delivered* beside it, as two policies enforced as an intersection. Nothing else was there at all: no `script-src`, so a Content Security Policy contributed nothing against injection through the six `MarkupString` sites; no `form-action`, which antiforgery does not cover; no `nosniff` on any static file; and no `Referrer-Policy`, on an application whose §4.3 join token travels in a query string. **A second half found on the way in, and it is the reason this is a slice rather than a line of configuration:** the obvious `connect-src 'self'` would have refused the Blazor circuit's WebSocket wherever the page is plain HTTP, because `'self'` is an origin comparison and CSP3's carve-out covers `wss:` and not `ws:` — which is to say it would have killed every live surface under `dotnet run` and all fifteen §16.3 scenarios | §11.11 is normative: three headers on **every** response from one middleware placed after `PublicOriginMiddleware` (so the policy can name the normalized host) and before anything that can answer (so a short circuit cannot escape it), writing on the way in rather than the way out. The framework's convention is switched off and replaced by something stronger in both directions — `frame-ancestors 'none'`, on everything. `connect-src` names `ws://{host} wss://{host}` derived from the request, with a recorded fallback for a host CSP's grammar cannot express. Every concession is tied to the fact that earns it, and `default-src 'self'` rather than `'none'` is recorded as the one place F-45's allow-list ruling is deliberately not applied, with the distinction stated. **And the row names something executable** (F-38's lesson, sixth application): `ContentSecurityPolicyContractTests` computes what the application loads by scanning the markup and the static assets, rather than trusting anybody's memory of it — because a CSP is the only configuration here that becomes wrong by editing a file it does not mention | §11.11 (new), §16.4, §17 · O§14 · ADR-0013 (new) · R§8 (rev 5) · `Security/ResponseSecurityHeaders.cs`, `Security/SecurityHeadersMiddleware.cs`, `Program.cs`, `tests/MyRestaurant.WebApplication.Tests/Security/` · BUILD_PROGRESS M6 Slice 24 |
| F-50 | **The one variable that discharges the licence was the one variable the stack did not pass.** `compose.yaml`'s `web` service enumerates its environment key by key and takes no `env_file`, so a key it does not name does not reach the process. Measured against everything `RestaurantOptions.FromConfiguration` reads, exactly one was missing, and it was `RESTAURANT_SOURCE_URL` — the variable F-39 built the AGPL §13 offer around, the one OPERATIONS §15 is titled after, and the only one in the table whose default is a *claim about who wrote the program* rather than a formatting preference. So a fork operator who modified this program, set the variable in `.env` exactly as instructed, and deployed through the only path this project documents, served every one of their users a §13 offer pointing at **this** repository. Silent by construction: no error, no warning, the page renders, the link resolves, and it resolves to the wrong source tree. **The shape is F-38's, one layer out** — S§11.9 stated the mechanism, §13 tabled the variable, `.env.example` documented it, O§15 wrote the runbook, and the transport between them dropped the value. Not reachable by any gate that existed: `check_tree.sh` reads files as text, `check_repository.sh` reads the platform, and every test in the suite constructs its own options object rather than receiving the one a container would | §13 states the transport rule: a key in that table must appear in the `web` service's `environment` mapping and in `.env.example`, and a variable whose default is more than a formatting choice is passed through with an **empty** default rather than its value repeated — so the fallback stays decided in `RestaurantOptions.DefaultSourceUrl`, which is a fork's natural first edit and which a repeated default here would silently override. **And the row names something executable** (F-38's lesson, seventh application): `ConfigurationSurfaceTests` derives the key set from the binding method and checks the three restatements against it, bounding the compose scan to the `web` service because a key set on the wrong service is the failure that would otherwise pass. The reverse direction is deliberately not asserted — `POSTGRES_*` belongs to the database image and `OTEL_*` to the exporter (F-41) | §13, §16.4 · O§15 · `compose.yaml`, `Configuration/RestaurantOptions.cs`, `tests/MyRestaurant.WebApplication.Tests/Configuration/ConfigurationSurfaceTests.cs` · BUILD_PROGRESS M6 Slice 25 |
| F-51 | **The canonical stack could not start on a stock Debian, and the rule that would have prevented it was already in the tree — applied to the drill that rehearses the stack, not to the stack.** `compose.yaml` named `postgres:17-alpine` and `caddy:2-alpine` as short names. A short name is resolved through `unqualified-search-registries`, which Fedora's `containers-common` populates and a stock Debian ships commented out, so ADR-0004's canonical engine on a Debian host answered `podman-compose up` with `short-name "postgres:17-alpine" did not resolve to an alias`, then `no container with name or ID "myrestaurant_postgres_1" found`, then `"myrestaurant_postgres_1" is not a valid container, cannot be used as a dependency` — three failures, in three different vocabularies, none naming a registry configuration. Every gate was green: this is well-formed YAML that parses, a tracked text file with LF endings and a final newline, and a compose file whose `environment` block F-50's own test had just finished auditing key by key. Nothing in this project runs `compose.yaml` anywhere but on a host where it already worked. **The uncomfortable part is that the rule pre-existed the finding.** `scripts/restore_drill.sh` has defaulted `DRILL_POSTGRES_IMAGE` to `docker.io/library/postgres:17-alpine` since Slice 16, with the reason written beside it — *"fully qualified so a short-name registry prompt cannot hang a drill"* — so a maintainer had reasoned this through once, for the scratch container, and left the canonical stack alone. This is F-46's shape in the reverse direction: not a rule enforced as a list of examples, but a rule *applied* to one example and never generalised | §14.1 states it normatively: every image reference in `compose.yaml` is fully qualified, and the reason it is a correctness requirement rather than a style preference is recorded beside it, because the symptom names neither the file nor the setting. The header comment of `compose.yaml` carries the same statement where somebody editing an image tag will read it | S§14.1 · O§1, O§10 · `compose.yaml` · BUILD_PROGRESS M6 Slice 26 |
| F-52 | **A document explained why a thing was impossible, and the thing was a design choice.** `README.md` told a reader that a quick tunnel *"cannot 'print a URL and exit', because exiting kills the URL"*, and OPERATIONS §10 said the same in stronger words: *"there is no detached mode and no 'print the URL and exit,' because the tunnel dies with the process that owns it."* Both sentences are true of `scripts/quick_tunnel.sh` and false of quick tunnels, and the difference between the two is one word in the third clause: the tunnel dies with the process that owns it, and what owns it is a choice. `quick_tunnel.sh` runs `cloudflared` as a foreground child of the shell; run as a detached container it is owned by the engine, and the shell can exit. **The cost was not the sentence, it was what the sentence closed off.** The case that needed a detached instance — a spare machine on the LAN, reached over SSH, serving a build testers use for days, on a host with no .NET SDK, where `run.sh`'s default and `--smoke` modes are both unavailable — had been documented as impossible for four milestones, so nobody looked for it. This is a category this ledger has not had before: not a document disagreeing with the code (F-38), nor with the platform (F-42), but a document correctly describing one implementation and stating its incidental property as a law | New §14.3a and `scripts/dev_instance.sh`, which exits and leaves the instance running. Both documents now say what is true of each script rather than of tunnels, and §14.3a records the three properties that make the detached shape different — the tunnel is not a child, the image is built before the URL is announced (nineteen minutes of unreachable public URL, measured), and the origin is known before `web` is created. The old sentences are corrected rather than deleted, because *why* the foreground script cannot detach is still worth a reader's time | S§14.3, S§14.3a · O§10 · `README.md`, `docs/OPERATIONS.md`, `scripts/dev_instance.sh` · BUILD_PROGRESS M6 Slice 26 |
| F-53 | **The documented command started the whole stack and then never returned, and nothing in its output said so.** `scripts/dev_instance.sh` hung on its first run, on the machine §14.3a was written for, at `podman-compose up -d`. podman-compose 1.3.0 — Debian trixie's version, and podman-compose is the canonical engine — implements `up -d` as `podman run -d` for every container **followed by** a wait on each dependency's `depends_on` condition, in an unbounded `while True:` loop that logs at debug level and prints nothing at all. `compose.yaml` asked `web` to wait for `postgres` to be `service_healthy`; the health status never advanced past `starting`; the loop never ended. **The instance was fine.** Both containers had been started by `podman run -d` before the wait began, the tunnel was open, and the public URL was serving the app throughout — so the observable symptom was a terminal that stopped after printing two container ids, on a host reached over SSH, from a script whose entire purpose is to hand that terminal back. Upstream has this as issues #1178 and #1183, the first reported from Debian with a traceback landing on those exact three frames. No flag avoids it: `--no-deps` is accepted by `up` in that version and consulted only by `run`. And every gate here was green, legitimately — this is well-formed YAML that parses, and F-50's test had audited this same file key by key, because a dependency condition is not an environment key | Two things, and the first is the fix. **§14.1 prohibits the condition:** the only condition `compose.yaml` may use is `service_started`, because whether `service_healthy` is ever satisfied is a property of the *host* — a health status only advances if something runs the healthcheck, and under rootless Podman that is a systemd timer in the user's session. Nothing is given up, which is why it is a prohibition rather than a workaround: `SchemaMigrationRunner` has retried a connection failure thirty times at two-second intervals since M1, with *"at compose start the web container can race PostgreSQL"* written beside it, so `web` losing the race is a race the code was written to lose safely. The health**check** stays — it is what `podman ps` and `status` read — it simply stops standing between `up -d` and returning. **§14.3a puts every compose call under a deadline,** with a separate longer one for the build, because the shape of this failure outlives its cause: a script that must release the terminal cannot contain a call that can keep it. When a deadline trips the helper names the finding, reports each service's state and health straight from the engine, starts anything created but not started, and verifies readiness itself. **And the row names something executable** (F-38's lesson, eighth application): `ComposeDependencyContractTests` asserts the rule on every `dotnet test`, and unlike F-51's rejected gate it is not a text assertion standing in for a behavioural one — the condition *is* the thing that hangs | S§14.1, S§14.3a, S§16.4 · O§2, O§10a · `compose.yaml`, `scripts/dev_instance.sh`, `tests/MyRestaurant.WebApplication.Tests/Deployment/ComposeDependencyContractTests.cs` · BUILD_PROGRESS M6 Slice 27 |
| F-54 | **A step in the production runbook described behaviour no script had, and the ledger's own F-16 row is where the claim came from.** OPERATIONS §2 step 2 told an operator to copy `.env.example` to `.env` and added, parenthetically, *"`run.sh` and the scripts do this automatically when `.env` is absent — F-16"*. All nine scripts were grepped: not one of them writes `.env`. F-16's ruling from 2026-07-17 does say *"`.env` is copied from `.env.example` when absent"*, so this is not a document drifting from the code — it is a *decision* that was recorded and never implemented, and then cited in the indicative by the document that depends on it. That is F-38's shape aimed inward, and the only reason it costs nothing today is that an absent `.env` is a working state: `compose.yaml` supplies a development default for every key | **The document is wrong and the scripts are right — the clause of F-16's ruling is reversed rather than implemented.** Materialising `.env.example` would create an untracked file carrying `POSTGRES_PASSWORD=myrestaurant` that nobody knowingly wrote, on a path `.gitignore` hides, which is the class of artefact F-45 was about; and because the stack starts without it, auto-creation would buy nothing while removing the one moment an operator is supposed to make decisions about credentials. §2 now says to copy it by hand and says why, §10a says the same in one clause, and `scripts/dev_instance.sh` says it in the warning it already prints when the file is missing. No script gains the behaviour | O§2, O§10a · `docs/OPERATIONS.md`, `scripts/dev_instance.sh` · BUILD_PROGRESS M6 Slice 27 |
| F-55 | **The bring-up did not hang this time. It waited out a five-minute deadline against a container that had already exited, printed a public URL over a dead application, and returned 0.** Six minutes fifty-five seconds, on the machine F-53 was found on, one slice after F-53 was fixed. `postgres` was being restarted in a loop by the engine; `web` had exited **1**, which in this program is reachable from exactly one place — `Program.cs` prints `Configuration error: …` and returns 1, and every other failure aborts with a different status. So the reason was a line of text sitting in `podman logs myrestaurant_web_1` from the first ten seconds onward, and **nothing ever printed it**: the readiness wait polled HTTP for the full 300 seconds without once asking whether the container was still alive, the DEV INSTANCE banner was printed unconditionally, the settle phase then spent twenty more seconds probing a public URL for an application that was not answering on loopback, `status` described the corpse as `(stopped, health: starting)` — a container on its way up — and `logs` could only ever show the tunnel's log, so the one command an operator would reach for showed forty lines of cloudflared saying `connection refused` and nothing about the application. Exit status 0. **F-53 was a wait with no deadline; this is a wait with a deadline and no evidence, and they need opposite fixes** — a deadline stops a wait that cannot end, and only a liveness check stops a wait that cannot succeed. Not reachable by any gate: every one of these is a property of a script's behaviour on a host, and `bash -n` and shellcheck were clean on all of it. Found the way F-51, F-52 and F-53 were, by the same act — running the documented command on the second machine — and this is the third consecutive slice where that act produced the finding | Four rules in §14.3a, one of which is a new command. **No wait may outlive its own evidence:** every wait is bounded and watched, ends early when the container it waits on is crash-looping or will not stay started, starts a stopped container again a bounded number of times first (because ADR-0012's sixty-second retry is outlived by a slow first `postgres` boot), and the database wait is separated from the readiness wait because one message cannot diagnose both. **A failure must print the log:** state, exit code, restart count and a bounded tail of *both* logs, plus a key mapping the symptoms this program produces to their causes; `logs` takes a target and defaults to `web`; a stopped container is described by its exit code and never by a health status. **`up`'s exit status is a claim about the instance,** so a stack that was started and never answered exits non-zero while being left running for inspection. **And `reset`,** because the one failure the helper cannot repair is a data directory that cannot start: `down` keeps volumes on purpose and `podman system prune -a` does not touch them, so nothing in the tree could clear it — `reset` removes this project's volumes, enumerated from the engine, after saying what that destroys and requiring confirmation. Measured against the failure: a rejected configuration now reports itself in fifteen seconds and a crash-looping database in seven, both with the causing log line on screen | S§14.3a · O§10a · `scripts/dev_instance.sh`, `.env.example` · BUILD_PROGRESS M6 Slice 28 |
| F-56 | **Three helpers dial the same port and one of them names it correctly.** `compose.yaml` publishes `web` as `127.0.0.1:8080:8080` — a single IPv4 loopback address, with nothing listening on `::1`. `run.sh` probes `http://127.0.0.1:8080/healthz/ready`, the literal, and has since M1. Both tunnel helpers defaulted `TUNNEL_TARGET` to `http://localhost:8080`, and that value is dialled by three different clients: cloudflared, and then whichever of curl or wget the host has. curl and GNU wget try the second address when the first refuses; **BusyBox wget does not**, and it is the second entry in the probe chain of a script whose whole premise is a host that may not have curl. The visible cost is worse than the risk, and it is what made this findable: cloudflared reports the address it failed on, so the tunnel log of the F-55 failure reads `dial tcp [::1]:8080: connect: connection refused` forty times, and an operator debugging that goes looking for an IPv6 misconfiguration that does not exist. **F-51's shape exactly, third occurrence:** a rule reasoned through once, applied to one script, never stated | `TUNNEL_TARGET` defaults to `http://127.0.0.1:8080` in both helpers, with the reason written beside it, and §14.3a states the rule generally: what a program in this tree dials is an address literal, because compose publishes one address and a name is a dependency on every client's fallback behaviour. **The row names something executable** (F-38's lesson, ninth application): `DevInstanceLoopbackContractTests` derives the published address from `compose.yaml` and checks both helpers' defaults against it — the F-50 pattern, authoritative side and restatements — and additionally asserts that what is published is a loopback address, because the rule's justification evaporates if that changes. Deliberately not asserted: that no script says `localhost` anywhere. `run.sh` prints it in a sentence for a human to paste into a browser, which is correct, and failing that would be a finding on a correct tree (F-41) | S§14.3a, S§16.4 · `scripts/dev_instance.sh`, `scripts/quick_tunnel.sh`, `tests/MyRestaurant.WebApplication.Tests/Deployment/DevInstanceLoopbackContractTests.cs` · BUILD_PROGRESS M6 Slice 28 |
| F-57 | **The canonical engine does not apply the default values in `compose.yaml`, so the stack was configured with the placeholder text, and both containers died of it.** `compose.yaml` sets twenty-three values as `${NAME:-default}`. On Debian trixie's podman-compose — ADR-0004's canonical engine — the branch after `:-` is not applied, so every variable not already set in the environment arrived as literal text. The application validates five of them and refused to start, naming values like `'${RESTAURANT_TIME_ZONE:-America/New_York}'`; `POSTGRES_USER` reached `initdb` as punctuation, so `CREATE EXTENSION plpgsql` failed with *invalid character in extension owner*, initdb erased the data directory, and the engine restarted it into the same failure, forever. **The eleven that arrive wrong in silence are the worse half:** `RESTAURANT_NAME` would render the placeholder as the restaurant's name on every page, an unparseable integer is indistinguishable from an absent one so the four Argon2 parameters quietly fell back to compiled-in values, and `OTEL_EXPORTER_OTLP_ENDPOINT` — whose *emptiness* is what switches the exporter off — arrived non-empty, which switches it on and points it at a hostname made of braces. **This is F-51's class and not F-51's mistake:** not a value this repository wrote wrongly, but a substitution feature of the canonical engine that does not work, so no reading of this tree could have found it. It was found by the diagnosis added one slice earlier — `web`'s log tail printed the five errors, `postgres`'s printed the initdb loop, and the two together name one cause. Green everywhere: the file is valid YAML, F-50's test audits its keys, F-53's audits its conditions, and CI runs Docker Compose, which applies defaults correctly. | Verified, not assumed, in three places, and the remediation made complete. **New `scripts/check_compose_substitution.sh`** asks the engine: it works out which variables actually depend on a default right now (a variable set in the environment or assigned in `.env` does not — the *set* branch is the half observed working), renders the file with `config`, and reports a surviving `${` as the finding, with the whole account and two ordered remediations. Exit 3 is the finding, 2 is *undetermined*, and the difference matters because a missing subcommand is not a broken engine. **`dev_instance.sh`, `quick_tunnel.sh` and `run.sh` run it before doing work** — the first refuses before a twenty-minute image build — and **`dev_instance.sh` asks again after `up -d` from the containers' own environment**, which needs no subcommand and is ground truth, because whether an empty assignment satisfies a given engine is not knowable from here. **§14.1 requires every interpolated variable to be assigned in `.env.example`**, empty where empty is the value: it was assigning nineteen of twenty-two, and a commented-out line supplies nothing. `RESTAURANT_SOURCE_URL` is now assigned empty there too — F-50's ruling one layer over, since that file spelling the upstream URL would silently override a fork's first edit. **The row names something executable** (F-38's lesson, tenth application): `ComposeSubstitutionContractTests`, three facts, each proven sensitive. | S§14.1, S§16.4 · O§2, O§10a · `scripts/check_compose_substitution.sh` (new), `scripts/dev_instance.sh`, `scripts/quick_tunnel.sh`, `run.sh`, `.env.example`, `tests/MyRestaurant.WebApplication.Tests/Deployment/ComposeSubstitutionContractTests.cs` · BUILD_PROGRESS M6 Slice 29 |
| F-58 | **The gate built to stop a version header disagreeing with its own history named one file, and the sibling document had been disagreeing with itself for six slices.** F-48 was this document's header reading v1.6 above a v1.7 changelog entry, corrected and made executable by `SpecificationVersionTests` — which pinned `docs/TECHNICAL_SPECIFICATION.md` in a `const string`. From Slice 24 (rev 5, 2026-08-06) until Slice 30, `docs/REQUIREMENTS.md` said **"Revision 4 — 2026-08-05"** in its header above a revision history whose newest entry was **"Rev 5 — 2026-08-06"**. Same defect, same field, sibling file, four ledger rows below the fix, green on every `dotnet test` for six slices. The same header also cited *"the companion `docs/TECHNICAL_SPECIFICATION.md` v1.6"* while that document was at v1.14 — a second, quieter drift of the same kind, in a citation nothing could check. **This is F-46's lesson one register lower, and the register is what makes it worth a row:** F-46 was a rule stated generally and enforced as six phrasings about one settings page; this is a rule stated generally and enforced against *one file*, and a list of one does not look like a list, which is precisely why nobody read it as one. Found while reading `REQUIREMENTS.md` to add a §8 principle — that is, by opening the file for an unrelated reason, which is the same way F-54 was found. | The subject is **computed** rather than named (F-47's habit): every Markdown file in `docs/` with both a header version and a history section is checked, both vocabularies are admitted by one pattern instead of a table keyed by filename, at least two documents must qualify, and a **half-versioned** document — a header version with no readable history, or the reverse — is reported as a finding rather than skipped, because those are the two shapes in which a document could quietly leave the subject. The class name stays `SpecificationVersionTests` because four documents cite it. `REQUIREMENTS.md` moves to rev 6 with the correction recorded in its own rev 6 entry, mirroring how the v1.3 changelog entry below records the first occurrence; the stale companion citation loses its version number rather than gaining a correct one, because a cross-document version is a restatement joined to its subject only by somebody remembering to edit it (F-50's class, at the smallest possible stakes) | §16.4 · `tests/MyRestaurant.WebApplication.Tests/Documentation/SpecificationVersionTests.cs`, `docs/REQUIREMENTS.md` · BUILD_PROGRESS M6 Slice 30 |
| F-59 | **The only affordance on an administration row was off the right-hand edge of the screen, and no document in this tree had ever had an opinion about layout at any width.** Reported by the first person shown the running application, in the plainest possible terms: the Manage button was on the right side while they were trying to manage a table, on a phone. It is exactly reproducible. Four administration index pages — `AdministrationHome`, `AdministrationTables`, `AdministrationMenu`, `AdministrationSittings` — each declared their own inline copy of the same eighty lines of table CSS, and each copy ended in `.admin-row-actions { white-space: nowrap; text-align: right }` inside a wrapper carrying `overflow-x: auto`. A five-to-eight column table in a 375px viewport therefore scrolled sideways, and the action column was the last thing in it. Nobody decided that; it was one paste, four times. **The same four pastes had also invented the chip vocabulary five times** (four inline, once in `app.css`, which carried a comment apologising for it) and `.visually-hidden` seven times. **What makes it a finding rather than a chore is that nothing could have caught it.** R§1 has said since rev 1 that guests order from their own phones and §11.7 budgets the footer clock for a handset, but no section said a *staff* surface must be operable on one — so there was no rule to enforce, no gate to write, and every one of the 1066 tests was passing while the pages were unusable. This is F-49's shape without the mitigating half: F-49 was a control that existed, worked, and was unowned; this is a property everything assumed and nothing stated. | New **§11.12**, normative for every surface: handheld-first, exactly one `min-width: 48rem` breakpoint, 2.75rem touch targets, a 16px input floor because iOS Safari zooms the viewport under it and does not zoom back, and a **record list** whose rows are cards below the breakpoint and table rows above it. Every cell states its own label from `data-label`, because overriding a table's `display` drops the header association in every engine and an unlabelled card is a column of bare values. **A row's action is the full width of the foot of its card and its primary cell is also a link**, so the way in is at x=0 whatever the viewport does. The shared vocabulary is declared once — a component may still keep rules nobody else reads, but not a shared name, because the inline copy wins on source order and the stylesheet loses silently. `AdministrationAreaLinks` renders the six area links once instead of six copies each omitting a different one, so no page was reachable from every other. **The row names something executable** (F-38's lesson, eleventh application): `HandheldLayoutContractTests`, four facts, each proven sensitive, including the F-47 pattern for the four pages Stage 1b still has to convert. **Recorded and not fixed here, then fixed in Slice 32:** a 375px Playwright barrier, which is the assertion this finding would have failed. It is §16.3 scenario 16. The reason given here for deferring it — that the scenarios share one browser context — was not true of this harness, and that is **F-62** | R§8 (rev 6) · S§11.12 (new), §16.4, §19 · `wwwroot/app.css`, the four `Components/Pages/Administration/Administration*.razor` index pages, `AdministrationAreaLinks.razor`, `AdministrationArea.cs`, `tests/MyRestaurant.WebApplication.Tests/Components/HandheldLayoutContractTests.cs` · BUILD_PROGRESS M6 Slice 30 |
| F-60 | **The rule that a container image reference must be fully qualified was stated for the repository and applied to two files, and on the canonical host the consequence is a green suite that ran nothing.** §14.1 has carried F-51's rule since v1.11; `compose.yaml` obeys it and `scripts/restore_drill.sh` has obeyed it since Slice 16. Four other references did not: both Testcontainers fixtures named `postgres:17-alpine`, and so did CI's service container and the drill's image in the same workflow. Testcontainers hands the reference to the engine verbatim — `MatchImage.Match` records a registry only when the first slash-separated segment carries a `.` or a `:`, and the comment beside it says it *does not resolve or set the default domain and repository prefix* — so on a host whose `unqualified-search-registries` is unpopulated the pull fails with F-51's message. **Both fixtures then convert every startup failure into a skip, correctly and by design, so the failure does not present as a failure.** It presents as `dotnet test` succeeding with the data-access integration tests and all fifteen §16.3 scenarios declining to run, behind a skip reason whose headline said the container engine was unreachable and whose remediation was to activate a socket that was already active. Two of the references were additionally in positions no reading of this tree could find: one spelled into a `podman run` command line, one passed inline to `new PostgreSqlBuilder(…)`. **F-46's shape for the third time** — a rule stated generally and enforced against the examples that prompted it — and F-51's row is where the narrowing happened, in the same commit that stated the rule. | §14.1 states the rule for **every** image reference rather than for one file, requires each to sit in a position that can be read, and requires one image name to resolve to one reference. All six references are now the same fully qualified string; the two hidden ones moved into `CLOUDFLARED_IMAGE` and a `PostgreSqlImage` constant, because naming them is what puts them in scope. Both fixtures split their diagnosis so an unresolvable reference is no longer reported as an unreachable engine, and both name the image, which neither message did. **The row names something executable** (F-38's lesson, eleventh application): `ContainerImageReferenceContractTests`, three facts, each proven sensitive. F-51's ruling against making it a *behavioural* gate is not reversed — the CI job on the canonical engine stays the open item, and §16.4 records why a tree-consistency assertion is a different claim. | S§14.1, S§16.4 · `tests/MyRestaurant.DataAccess.Tests/PostgreSqlFixture.cs`, `tests/MyRestaurant.EndToEnd.Tests/Harness/RestaurantHarness.cs`, `.github/workflows/ci.yml`, `scripts/quick_tunnel.sh`, `tests/MyRestaurant.WebApplication.Tests/Deployment/ContainerImageReferenceContractTests.cs` (new) · BUILD_PROGRESS M6 Slice 31 |
| F-61 | **A helper announced twice that it was closing the tunnel, because one handler was registered on both a signal and on exit.** `scripts/quick_tunnel.sh` ran `trap cleanup INT TERM EXIT` and then, forty lines later, registered a second handler on the same three. A signal trap and the `EXIT` trap are independent, so one Ctrl+C ran the body twice and printed the closing line twice. Observed on the workstation, at the end of a run in which every other gate was green. Nothing it did was harmful: the kill and the `rm` are both idempotent, and the second pass had nothing left to do. **What was wrong was the sentence**, and that is the whole finding — two identical closing lines read as two tunnels, or as one that would not close, from a helper whose entire job at that moment is to tell an operator what state the machine is in. Small, and the reason it earns a row is that this is the third consecutive slice in which a helper's *output* was the defect while its actions were correct (F-53's silence, F-55's false success, this). | The handler carries a first-entry guard and disarms the remaining traps, so it runs once whichever of the three arrives, rather than being made correct by a different choice of signals. The second registration is deleted, with the reason written where it was, and its work — killing the log tail — folded into the one handler. **The class was audited rather than the instance:** `run.sh`'s smoke trap has the identical registration and is unchanged, because its handler is silent and idempotent by construction and a rule that made it a defect would report findings on a correct tree (F-41); `scripts/backup.sh`, `scripts/restore.sh` and `scripts/restore_drill.sh` register on `EXIT` alone and were never in scope. §14.3 states the rule as being about the announcement. | S§14.3 · `scripts/quick_tunnel.sh` · BUILD_PROGRESS M6 Slice 31 |
| F-62 | **A gap was left open for a slice on the strength of a fact about this repository that this repository contradicts.** Slice 30 wrote §11.12, made its structure executable, and recorded one assertion it deliberately did not make: that a control is reachable inside a 375px viewport — the assertion F-59 would have failed. The reason given was that *the fifteen §16.3 scenarios all run in one default context, so giving one of them a second viewport is either a second browser context per run or a resize that every subsequent scenario inherits*. **`RestaurantHarness` holds one browser, not one context.** Every scenario calls `StartInstanceAsync`, which calls `browser.NewContextAsync` and hands back a context of its own; `OpenIsolatedPageAsync` mints and tracks further ones on request. A viewport is a property of a context. There was nothing to share and nothing to inherit, and the harness had been built that way since Slice 2 for a reason stated in its own summary. **What makes this a finding rather than a wrong guess is where the sentence ended up.** It was written once, in a plan, and by the close of the slice the same claim had been copied into §16.4, into F-59's row in this register, and into `docs/MENU_AND_HANDHELD_PLAN.md` — three documents asserting a property of the test harness, none of them written by reading it. F-50's shape (a cross-document citation that outlives what it cited) applied to a fact that was never true rather than to one that stopped being true, and the cost was a milestone's worth of Razor scheduled to be rewritten with no barrier able to check it. | §16.4 states the barrier rather than the gap, and states the three things about it that are rulings: the viewport is asserted before anything else and read from the document rather than from the option that set it; the count of measured controls is asserted, because a renamed selector produces an empty set that satisfies every verdict (F-41); and the widest element on the page is collected but may never fail a run, because an element wider than the viewport inside its own scroll container is correct and `.page-head-areas` is exactly that. §11.12's closing paragraph now says the contract is asserted at two levels and that neither can reach the other's claim. **The general rule, and it is the row's point:** a reason for not doing something is a claim about the tree, and it is checked against the tree before it is written down — the more so before it is cited. | S§11.12, S§16.4 · `tests/MyRestaurant.EndToEnd.Tests/Harness/HandheldReach.cs` (new), `…/Harness/RestaurantInstance.cs`, `…/Harness/RestaurantHarness.cs`, `…/EndToEndScenarios.cs`, `docs/MENU_AND_HANDHELD_PLAN.md` · BUILD_PROGRESS M6 Slice 32 |
| Menu sections and descriptions (enhancement, not a finding) | The first request from a user rather than from a document: a menu needs headings and each item needs a sentence explaining it, and `menu_item`'s four columns express neither | ADR-0014 rules the schema — `menu_section` as a table, `menu_item.menu_section_identifier` NOT NULL, `citext UNIQUE` on a section name where an item name is deliberately neither, non-unique explicit `display_order` on both, `description text NOT NULL DEFAULT ''` because a paired CHECK cannot bind an optional payload, three new item event types beside a new `menu_section_event`, and `created` deliberately still carrying name and price only. Recorded in Appendix A without an F-number because it is not a defect: the ledger is for findings, and an enhancement's trail is the ADR, this register, §19's M7 line and BUILD_PROGRESS | §7, §8.2, §19 · ADR-0014 · `docs/MENU_AND_HANDHELD_PLAN.md` Stage 2 · BUILD_PROGRESS M6 Slice 30 |
| F-21 – F-24 | Editorial: four experiences + display; abbreviation carve-out; generic paths; directives resolved | REQUIREMENTS rev 2 |
| F-25 – F-33 | export.sh fixes; REQUIREMENTS tracked in docs/ | export.sh header; repo layout |
| Claude judgment calls (owner-vetoable, recorded) | Reminder = once at threshold iff no line of the send fulfilled/removed; counter/admin line-changing staff edits also alert loudly; reset forces TOTP re-enrollment only if enrolled pre-reset; obligations pipeline runs on passkey path too; counter fallback = same rotating QR (no short-code) | §10.1–10.2, §3.5, §3.7, §4.5 · ledger notes |

---

## Changelog

**v1.17 — 2026-08-11.** The assertion the last version recorded as impossible, and the reason it was not. **§16.3** gains a sixteenth scenario, and it is the first in the matrix whose subject is not a flow: an administrator works the four administration indexes in a browser context laid out at 375×667, and three numbers the page computes are asserted against the viewport — no surface is wider than its own, every row's action and every page's primary action lies inside it, every control is at least 44px tall. **§16.4** replaces the paragraph that recorded this as a gap with the barrier itself, and states the three things about it that are rulings rather than implementation: the viewport is asserted first and read back from the document, because at any wider width every other assertion in the scenario passes and means nothing (F-41); the number of controls measured is asserted, because a renamed selector produces an empty set that satisfies every verdict; and the widest element on the page is collected for the failure message but may never fail a run, because an element wider than the viewport inside its own scroll container is correct and `.page-head-areas` is exactly that. The set of surfaces the barrier visits is four today and grows a line per page Stage 1b converts, which is F-47's pattern in a second place. **§11.12** closes by saying the contract is asserted at two levels — structure by a unit test, reachability by a browser — and that neither level can reach the other's claim. **§19's M7** names which parts of Stage 1 have landed. **Appendix A** gains **F-62**: v1.15 deferred this barrier on the stated ground that the §16.3 scenarios share one browser context, which is not true of this harness and never was — it holds one *browser* and mints a context per instance — and by the close of that slice the same untrue sentence had been copied into §16.4, into F-59's row and into the plan. F-59's row is corrected in place rather than rewritten, because what it recorded happened. No `REQUIREMENTS.md` edit, on the v1.2 and v1.11–v1.14 reasoning: rev 6 already requires the program to be operable on the screen §1 says it is used from, so this is a mechanism catching up with a contract this tree already states rather than new intent. No schema change, no ADR edit, no `compose.yaml` edit, and no application code — the four pages were already right, which is what the barrier now says out loud.

**v1.16 — 2026-08-11.** A rule that was true of two files, and a helper that said one thing twice. **§14.1** stops being about `compose.yaml`: the fully-qualified-image requirement now holds at *every* container image reference in this repository, because confining it to the file that prompted it was itself the finding (**F-60**) — `Containerfile`'s `FROM` operands, the workflows' service containers, every `*_IMAGE` default in `scripts/`, and the image the Testcontainers fixtures start were four places the rule was not applied. It adds two clauses the first version had no reason to need: a reference **must occupy a position that can be read** — a YAML `image:` key, a `FROM` operand, or a value assigned to a name ending in `_IMAGE` or `Image` — because two references were spelled where no reading of this tree could find them; and **one image name resolves to exactly one reference**, so the test suite and the canonical stack cannot disagree about which registry or which version the database comes from. The section also records what makes this worse than F-51 rather than merely wider: Testcontainers passes the reference to the engine unnormalised, and both fixtures convert a failed start into a *skip*, so the canonical host answers with a green suite in which no integration test and no §16.3 scenario ran. **§14.3** gains one rule about helpers: a cleanup handler must run exactly once however the helper is left (**F-61**), stated as a rule about the *announcement* rather than about the work, because the work was idempotent and the sentence was not. **§16.4** gains the image-reference contract test, its three facts, and the paragraph explaining why a tree-consistency assertion is not the behavioural gate F-51 ruled against — that gate is still the open item. **Appendix A** gains **F-60** and **F-61**. No `REQUIREMENTS.md` edit, on the v1.2 and v1.11–v1.14 reasoning: §14.1 has carried the image rule since v1.11 and §14.3 has specified this helper since v1.0, so both are mechanisms catching up with contracts this tree already stated rather than new intent. No schema change, no ADR edit, no `.slnx` edit, and no `compose.yaml` edit — that file was right, which is exactly how the rule came to be read as being about it.

**v1.15 — 2026-08-11.** The screen it is actually read on, and a menu that is not yet a menu. New **§11.12** is normative for every surface: the layout is written handheld-first and widened by exactly one `min-width: 48rem` query, which is the only place a width appears — and the *direction* is the rule rather than a preference, because a max-width query makes the wide layout the default and fails in the worst available direction, which is the arrangement four administration index pages were found in. It fixes touch targets at 2.75rem, puts a 16px floor under every text control because iOS Safari zooms the viewport below it and does not zoom back, specifies the **record list** whose rows are cards below the breakpoint and table rows above it, and requires every cell to state its own label — because overriding a table's `display` drops the header association in every engine, so an unlabelled card is a column of bare values. It states that a row's action is never a right-hand column, and it states what it does *not* require: that every surface be optimised for a handset, `/kitchen` being the wall-mounted case that forces the distinction. **§16.4** gains the contract test, its four facts, and the one assertion it cannot make — a 375px Playwright barrier — with the reason that is recorded as an open item rather than written badly. **§16.4** also records that the document-version gate no longer names a document: its subject is computed, both version vocabularies are read by one pattern, and a half-versioned document is a finding rather than a skip. **§7** gains a forward reference to ADR-0014 and restates two rules that are easy to lose while rewriting a menu — a deactivated item stays visible under its section heading, and deactivating a section does not deactivate its items. **§19** gains **M7**, the first milestone driven by a user rather than by this document, and says why its stages run in the order they do. **Appendix A** gains **F-58**, **F-59**, and one row that is not a finding. New **ADR-0014**. `REQUIREMENTS.md` moves to **rev 6** with one new §8 principle, on the v1.3, v1.6 and v1.9 reasoning rather than the v1.2 one: nothing in this tree previously asked the program to be operable on the screen R§1 says it is used from, so this is new intent and not a mechanism catching up with an existing contract. No schema change in this version — ADR-0014's is Stage 2's, and §8.2 moves with it.

**v1.14 — 2026-08-10.** The engine that does not read its own defaults. **§14.1** gains a third rule of the same kind as v1.11's and v1.12's, and the widest of the three: the engine's variable substitution must be verified rather than assumed, because on the canonical engine the branch after `:-` is not applied and every value in this file arrives as placeholder text — five errors from the application, an `initdb` loop from a `POSTGRES_USER` made of punctuation, and eleven more wrong in silence, including the OpenTelemetry endpoint whose emptiness is what switches the exporter off. The same section requires every interpolated variable to be **assigned** in `.env.example`, empty where empty is the value, because `.env` is the remediation and a commented-out line supplies nothing; and it records `RESTAURANT_SOURCE_URL` being assigned empty there as F-50's ruling applied one layer over. It also states what is deliberately not claimed: which releases behave this way, and whether an empty assignment satisfies them, are properties of a host. **§16.4** gains the contract test and records why the new script is a preflight rather than a CI gate — its subject is the machine, not the repository — along with its three-valued exit contract. **Appendix A** gains **F-57**. No `REQUIREMENTS.md` edit, on the v1.11–v1.13 reasoning. No schema change, no ADR edit, no `.slnx` edit, and **no `compose.yaml` edit**: the file is correct, and the engine that reads it is not.

**v1.13 — 2026-08-10.** Why the documented command came back saying nothing was wrong. **§14.3a** gains four rules and loses an assumption. No wait in the helper may outlive its own evidence: every wait is bounded *and* watched, ends early when the container it is waiting on is crash-looping or will not stay started, and the database wait is separated from the readiness wait because one timeout message cannot diagnose both — F-53 was a wait with no deadline, this is a wait with a deadline and no liveness check, and the two need opposite fixes. A failed bring-up must print each container's state, exit code, restart count and log tail, with a key mapping this program's actual symptoms to their causes; `logs` takes a target and defaults to `web`; a stopped container is described by its exit code and never by a health status. `up`'s exit status is a claim about the instance rather than about the script, so a stack that was started and never answered exits non-zero while being left running to read. And `reset` is specified, because the one failure the helper cannot repair is a PostgreSQL data directory that cannot start: `down` keeps the named volumes on purpose and `podman system prune -a` does not touch them, so until now nothing in this tree could clear one. §14.3a also states the address rule — what a program here dials is an IPv4 literal, because `compose.yaml` publishes exactly one address and a name is a dependency on every client's fallback. **§16.4** gains the loopback contract test and records what it deliberately does not assert. **Appendix A** gains **F-55** and **F-56**. No `REQUIREMENTS.md` edit, on the v1.11 and v1.12 reasoning: R§8 asks for an operable instance and ADR-0004 has named podman-compose canonical since M1, so a helper that reports success over a dead instance is a mechanism failing a contract this tree already carried. No schema change, no ADR edit, no `.slnx` edit.

**v1.12 — 2026-08-10.** Why the documented command did not come back. **§14.1** gains a second normative rule about `compose.yaml`, alongside v1.11's: no `depends_on` may be gated on a health condition, `service_started` is the only condition permitted, and the reason is recorded as correctness on the canonical engine rather than preference — podman-compose 1.3.0 starts every container and *then* waits on each condition in an unbounded loop that prints nothing, so a condition the host never satisfies makes `up -d` never return with the stack already up behind it. It also records why nothing is given up: the application's own bounded boot retry has covered that race since M1, and the health*check* stays because it is what an operator reads. **§14.3a** requires every compose invocation in the helper to run under a deadline, with a longer one for the image build, and requires that a tripped deadline report each service's state and health from the engine, repair anything created but not started, and verify readiness independently — because a compose command that did not return is not a stack that did not start. **§16.4** gains the contract test, and states why this rule is made executable where F-51's was not. **Appendix A** gains **F-53** and **F-54**, the second being a runbook step that described behaviour no script had, cited from this ledger's own F-16 row — resolved by reversing the clause rather than implementing it, with the reason stated. No `REQUIREMENTS.md` edit, on the v1.2 and v1.11 reasoning: R§8 asks for an operable instance and ADR-0004 has named podman-compose canonical since M1, so a documented command that cannot finish on a supported host is a mechanism failing a contract this tree already carried. No schema change, no ADR edit, no `.slnx` edit.

**v1.11 — 2026-08-10.** Where this runs, and who owns the tunnel. **§14.1** states normatively that every image reference in `compose.yaml` is fully qualified, and why that is correctness rather than style: a short name is resolved through a registry list Fedora populates and Debian leaves commented out, so the canonical engine on a stock Debian host cannot start the canonical stack, and says so in three messages that name neither the file nor the setting. New **§14.3a** specifies the detached demo instance — the tunnel is a container rather than a child of the shell, the image is built before any URL is announced, the origin is known before `web` is created, a second `up` reuses the hostname because passkeys are bound to it, and `up` exits on a re-probe of the public origin rather than on a return code. It also records podman-compose's `up --force-recreate <service>` behaviour, which is a `down` of the whole project, and why the engine's own recreate-on-change is sound to rely on instead. **Appendix A** gains **F-51** and **F-52**. No `REQUIREMENTS.md` edit, on the v1.2 and v1.10 reasoning: R§8 asks for an operable instance and ADR-0004 has named podman-compose canonical since M1, so a stack that cannot start on a supported host is a mechanism failing a contract this tree already carried rather than new intent. No schema change, no `.slnx` edit; **ADR-0005** gains one paragraph, because point 6 asserted the foreground shape as a property of quick tunnels.

**v1.10 — 2026-08-08.** Whether a setting arrives. **§13** gains the transport rule and, with it, a job that section did not previously have: it now says that the table describes what the *program* reads, that `compose.yaml` and `.env.example` are restatements joined to it only by somebody having written the key in a third place, and that the agreement is asserted rather than remembered. It also records the empty-default rule for a variable whose fallback is a claim rather than a format, and states the one direction the rule runs in. **§16.4** gains the test, why its subject is derived from the binding method, and why the compose scan is bounded to the `web` service. **Appendix A** gains **F-50**. No `REQUIREMENTS.md` edit, on the v1.2 and v1.7 reasoning: R§8 has carried the AGPL §13 obligation since rev 3 and §11.9 has specified the mechanism since v1.3, so this is a mechanism catching up with a contract this tree already had rather than new intent. No schema change, no ADR edit, no `.slnx` edit.

**v1.9 — 2026-08-06.** What every response says about how it may be used. New **§11.11** is normative: `Content-Security-Policy`, `X-Content-Type-Options` and `Referrer-Policy` on every response this application produces, emitted by the application because three different proxies front it and one of them is a dashboard. It records that a partial policy was already being emitted by the framework and is now switched off and replaced, why `connect-src` has to name the request's own host rather than say `'self'` (the entire §9 live-update layer is a WebSocket, and `'self'` is an origin comparison), what each concession is tied to, and what is deliberately absent and why. **§16.4** gains the three test classes and the reason a CSP needs a contract test at all: it is the only configuration here that becomes wrong by editing a file it does not mention. **§17** names the threats the policy bounds. **Appendix A** gains **F-49**. New **ADR-0013** carries the rationale for the application owning these headers rather than a proxy. `REQUIREMENTS.md` moves to **rev 5** with one new §8 principle, on the v1.3 and v1.6 reasoning rather than the v1.2 one: nothing in this tree previously asked the program to constrain what a browser may do with a page it serves, so this is new intent and not a mechanism catching up with an existing contract.

**v1.8 — 2026-08-05.** What a surface says about itself. New **§11.10** is normative: every interactive surface publishes `data-live` and `data-loaded`, *interactive* means what `App.razor`'s `RenderModeForPage` means by it rather than what a doc comment remembered, and `data-loaded` is defined as *the surface has what it renders itself for* — which is a completed read on five surfaces and a join code on the sixth. **§16.4** gains the contract test that asserts it, and the reason it derives its subject from `[ExcludeFromInteractiveRouting]` rather than from a list. **Appendix A** gains **F-47** and **F-48** — the second being this document's own header, which read v1.6 under a v1.7 changelog entry and is now checked by a test rather than remembered. No `REQUIREMENTS.md` edit, on the v1.2 reasoning: §11.5 has said since v1.0 that a frozen screen must not masquerade as a live one, and this is that contract stated once for every surface instead of five times for four of them.

**v1.7 — 2026-08-05.** What leaves this machine, and who can get it back. **§14.1a** is new and normative: the build context is an allow-list, the allow-list is asserted by the build rather than trusted, and the reason a deny-list is not acceptable here is recorded — `.gitignore` was already correct and protected nothing, because a build context is not a commit. **§16.4** records that the governance gate's forbidden list covers package settings as well as repository settings, that a rule enforced as a list of examples is enforced as a list of examples, and that the advisory half has to reach the release run or it does not report on the only run that creates a package. **Appendix A** gains **F-43** and **F-44**, which shipped in Slice 21 without their ledger rows, and **F-45** and **F-46**. No `REQUIREMENTS.md` edit, on the v1.2 and v1.4 reasoning: §15 has called the key ring a secret since v1.0 and §18 has forbidden platform-state claims since v1.6, so both findings are mechanisms catching up with contracts this tree already carried.

**v1.6 — 2026-08-05.** The layer no gate could see. **§16.4** gains the governance gate and, more importantly, the ruling that its two halves carry different authority: the tree half blocking on git and grep alone, the platform half advisory because a fork's settings are the fork's business. **§17** gains the coordinated-disclosure paragraph and, with it, a new job for §17 itself — it is part of the offer to a reporter rather than a disclaimer, so a decision already argued does not cost somebody an unpaid evening. **§18** records the one carve-out to no-outside-contributions and the rule that produced this finding: a document states policy, never platform state. **Appendix A** gains **F-42**. `REQUIREMENTS.md` moves to **rev 4** with one new §8 principle, on the v1.3 reasoning rather than the v1.2 one: nothing in this tree previously asked for a disclosure channel, so this is new intent and not a mechanism catching up with an existing contract.

**v1.5 — 2026-08-05.** **§16.4** gains the tree gate's *scope* rule: which tracked files it has an opinion about, why generated text and binary files are both out of scope, why one decision has to cover both, and the requirement that it report what it skipped. **Appendix A** gains **F-41**. No `REQUIREMENTS.md` edit, on v1.4's reasoning — the rules being checked have not changed, only the set of files they were ever about.

**v1.4 — 2026-08-05.** **§16.4** gains the tree gate: what `scripts/check_tree.sh` asserts, which of its checks are blocking without dependencies, and why a gate this cheap is worth a job of its own. **Appendix A** gains **F-40**.

Nothing else moved, and deliberately no `REQUIREMENTS.md` edit. This is the v1.2 call rather than the v1.3 one: `.editorconfig` has asked for LF endings, a final newline and trimmed whitespace since M1, and §16.4 has always been the section that says which of the project's own rules are enforced instead of remembered. Nothing new is being asked of the program — a rule the tree already carried is now checked. No schema change, no ADR edit, no `.slnx` edit.

**v1.3 — 2026-08-04.** The release. New **§11.9** specifies the colophon and `/source`: what the program says about itself on every page, the AGPL §13 offer it now carries, the build stamp that lets the offer name a revision, and the three normative details of that stamp — metadata after the first `+` is all revision, an unstamped build must say "Not recorded" rather than guess, and `SourceRevisionId` alone does nothing without SourceLink. **§12** records `service.version`. **§13** gains `RESTAURANT_SOURCE_URL` and its http-is-accepted-here-only rule. **§16.4** gains the gate that fails unless `/source` names the commit CI built, plus a paragraph on what a release does. **§19** gains an M6 close-out line. **Appendix A** gains **F-39**. `REQUIREMENTS.md` moves to **rev 3** with one new §8 principle, because unlike v1.2 this *is* new intent rather than a mechanism catching up with an existing contract.

One correction in passing: the header of this document read **v1.1** while the changelog below already carried a v1.2 entry — Slice 16 bumped one and not the other. The header is now the version the changelog says it is.

**v1.2 — 2026-08-04.** **§15** rewritten. A recovery set is now *defined* as two files — the database dump and the Data Protection key ring — and `scripts/backup.sh` writes both. The rule that the key ring must be backed up alongside the database has been normative since v1.0 and no script in the tree honoured it; that, and three other defects in the same two scripts, are recorded as **F-38**. §15 also now states the guarantees that make retention's promise true across runs rather than within one (hidden `.partial` write, header check, `pg_isready` before anything is written, refusal on ambiguous container discovery, whole-set pruning), records that `scripts/restore.sh` restarts `web` from an `EXIT` trap and the exact reason the previous ordering could not have worked, and specifies `scripts/restore_drill.sh` and its seven gates. **§16.4** names the drill as a CI gate. **Appendix A** gains F-38.

Nothing else moved: no schema change, no ADR edit, and deliberately no `REQUIREMENTS.md` edit. §15's key-ring sentence was already the contract, so this is a defect fix at the mechanism level rather than new intent — the documents were right and the code did not do what they said.

**v1.1 — 2026-08-02.** Guest registration written down. New **§11.8 `/register`** specifies the surface R§4.3 has required since rev 2 and which no milestone had claimed: the two-step ticket-backed flow, why a ticket is needed at all (a WebAuthn user handle must exist before the `person` row does), and the rule that declining a passkey is offered exactly when a password was set. **§11.1** points at it instead of naming registration in passing. **§17** records the absent rate limit as an accepted risk with the concrete reason it is not a small change. **§19** notes that the surface lands in M6 rather than M2, and why. **Appendix A** gains F-37.

Nothing else moved. §11.7 keeps its number — a dozen source files cite it — so the new subsection is appended rather than inserted, and no existing cross-reference changes meaning.

**v1.0 — 2026-07-17.** Initial accepted specification.
