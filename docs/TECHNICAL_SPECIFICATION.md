# myrestaurant — Technical Specification

**Version 1.6 — 2026-08-05 — Status: accepted, implementation-ready.** (Changelog at the bottom; v1.0 was 2026-07-17.)

This document is the normative implementation contract for the system described in `docs/REQUIREMENTS.md` (rev 4). It is written so that a person or an LLM who has never seen the project can implement it without asking questions. The words **must**, **must not**, **should**, and **may** are used in their RFC 2119 sense. Where this specification and an ADR describe the same decision, they agree by construction; the ADRs in `docs/adr/` carry the rationale, this document carries the mechanism. The decisions register in Appendix A maps every ruling to its embodiment.

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

## 14. Deployment, TLS, origins (ADR-0004, ADR-0005)

**14.1 Canonical stack** — `compose.yaml`, rootless Podman. Services: `web` (Containerfile build; listens 8080 HTTP inside the network), `postgres` (named volume), `caddy` (dev profile: terminates TLS at `https://localhost:8443` with Caddy's internal CA), `cloudflared` (**production profile**: named tunnel via `CLOUDFLARE_TUNNEL_TOKEN`, forwards to `web:8080`; TLS at Cloudflare's edge). Host ports stay ≥1024 (rootless); if 80/443 are ever wanted directly, that is a host `sysctl net.ipv4.ip_unprivileged_port_start` concern, not this project's default. `podman-compose up` = dev; `podman-compose --profile production up -d` = production. Caddy **may** additionally run in production as an optional staff-LAN fallback (self-signed `restaurant.lan`; staff-only; passkeys will not work on that origin; password+TOTP does) — off by default, documented in OPERATIONS §7.

**14.1a Build context (normative)** — the image build must see the publish graph and nothing else. `.dockerignore` at the repository root is an **allow-list** — `.editorconfig`, `Directory.Build.props`, `Directory.Packages.props`, `global.json`, `src`, minus `src/**/bin` and `src/**/obj` — and both engines read that filename (Podman prefers `.containerignore` when both exist, so this project ships only the one). A deny-list is not acceptable here and the distinction is the whole finding (**F-45**): `.gitignore` is a deny-list, it was already correct, and it protected nothing, because a build context is not a commit. Everything it names — `.env`, the Data Protection key ring, every `*.dump` and `*-dataprotection.tar` §15 writes — was copied into the builder on every local `--build`, in the exact order OPERATIONS §12 documents. An allow-list is also the only form that covers the file nobody has added yet.

**The allow-list must be asserted, not assumed.** An ignore-file is an instruction to a tool, and it can be renamed, shadowed, or overridden with `--ignorefile` without any symptom other than a slower build — so `Containerfile` carries a guard immediately after `COPY . .` that fails the build unless the context root is exactly the allowed set, every required path is present, and no `bin` or `obj` survives under `src`. Stating the list twice is deliberate: one statement is the instruction and the other is the assertion, and a build that fails when they disagree is the only thing that distinguishes *excluded* from *excluded on the machine where somebody last checked*. This is F-38's rule — a row in the embodiment column names something executable — applied where the executable thing is the build itself, so it runs on a workstation and not only in CI.

**14.2 Origin truth** — one `RESTAURANT_PUBLIC_ORIGIN`. Everything (WebAuthn RP ID, QR URLs, links) derives from it. In-house guests hairpin through Cloudflare; **LAN ordering therefore depends on WAN health — accepted risk** per the F-06 ruling.

**14.3 Quick tunnels** — for demos. `scripts/quick_tunnel.sh` brings the stack up, opens a quick tunnel, discovers the assigned `*.trycloudflare.com` URL, exports it as `RESTAURANT_PUBLIC_ORIGIN` (so QR join URLs and the form-post host fallback are correct), recreates `web`, waits for `/healthz/ready`, and holds the tunnel in the foreground. Because the RP ID is derived per request (§3.3, ADR-0005) and `https://*.trycloudflare.com` is trusted by default, **passkeys work within a run** — including a passkey-only account. `*.trycloudflare.com` is on the Public Suffix List and every run gets a fresh random subdomain, so passkeys (and bookmarks) do **not** carry across runs and must be re-registered; the helper prints this caveat prominently. A quick tunnel must never carry the *bootstrap* of a real instance (§3.6) — bootstrap on the stable named-tunnel domain so the first administrator's credentials persist.

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

**16.4 CI:** GitHub Actions — **tree hygiene**, **repository governance**, shell lint, build, unit, integration (service container PostgreSQL), E2E (Playwright/Chromium, all fifteen §16.3 scenarios), then boot the production image against real PostgreSQL and **take a backup of that instance and drill the restore** (§15) in the same job; publish image on tag. The drill is a CI gate rather than a runbook step because a recovery procedure nobody executes is a hypothesis — see F-38.

The **tree gate** (`scripts/check_tree.sh`, run by the `tree` job and as the first gate of `scripts/ci_local.sh`) asserts five properties of the checkout itself, before any tool that would report their absence as something else: no context-dump separator line in any authored text file (`export.sh` exempt — it writes them); no line made only of whitespace; LF endings with a final newline on every authored text file; every tracked `.props`, `.targets`, `.csproj` and `.slnx` well-formed XML; every tracked `.yml` and `.yaml` valid YAML. The first four are blocking everywhere, needing only git, grep and the Python standard library; the YAML parse degrades to a reported skip where no parser is installed, as the shellcheck gate does.

**Scope: the gate asserts properties of authored text, and must say what it skipped.** Two classes of tracked file are out of scope, and both must be excluded by the *same* decision so that no two gates can disagree about one file (**F-41**). **Generated text** — everything under `docs/llm/`, which `export.sh` already excludes from its own output as its `EXCLUDED_DIRECTORY` — is out of scope because a context dump's structure *is* the separator gate 1 forbids, and because a dump is a copy of the authored files, so checking it reports every real finding twice while reporting every correct separator as a defect. **Binary files** are out of scope because neither LF endings nor a final newline is meaningful for a compressed archive: a gzip stream ends where it ends, and "no final newline" accuses an intact `.tar.gz` of being truncated. Binary-ness must be determined by inspection (`grep -I`, which is what gates 1 and 2 already use) rather than from an extension list, which is a list somebody must remember to update. The gate must report its skip counts alongside its total, because a gate that silently declines to look at a file is indistinguishable from one that looked and found nothing.

It is here because of **F-40**, and the argument for it is the failure *mode* rather than the mistake. MSBuild imports `Directory.Build.props` before it evaluates anything, so one malformed character in that file fails `clean`, `restore`, `build`, `test` and the container build with the same message — and the message is `MSB4024: Data at the root level is invalid`, which sends a reader to look at MSBuild. Of the twenty-one files damaged identically in the incident, six broke anything at all and fifteen absorbed it in silence, because the offending line is a comment in YAML, in a Containerfile and in `.env`, a heading rule in Markdown, literal text in Razor markup, and a discarded selector in CSS. Damage that is catastrophic in one file and invisible in fifteen belongs to something that runs on every push.

The **governance gate** (`scripts/check_repository.sh`, run by the `governance` job and as the second gate of `scripts/ci_local.sh`) asserts the one layer every other gate is blind to (**F-42**). It has two halves, and they must not share an authority.

The **tree half must be blocking** and must need nothing but git and grep. It asserts that `SECURITY.md` is tracked and non-empty, that it names a reporting channel and points a reporter at §17; that `README.md`, `CONTRIBUTING.md` and `SECURITY.md` each name the others, so that no one of them can be rewritten into isolation — the edges are asserted rather than the files, because the way this breaks is a rewrite that forgets one edge and not a deletion; and that **no tracked file asserts a GitHub repository setting**. That last rule is the finding made unrepeatable: a document may state policy, which is true wherever it is read, and must not state platform state, which nothing in the repository can verify. The forbidden phrasings are a short, named list, and the files whose job is to record what this tree *used* to say are exempt by literal path, the way `export.sh` is exempt from the separator gate.

**The forbidden list covers package settings as well as repository settings (F-46).** The first version of this gate enumerated the switches on the repository page — issues, pull requests, discussions, the wiki — passed on its first run, and was already wrong: OPERATIONS §14 had asserted the visibility of a *package*, in the indicative, about a package that did not yet exist. A repository's visibility and a package's visibility are separate switches, and GitHub's own documentation disagrees with itself about which way the second falls for a `GITHUB_TOKEN` publish, which is the strongest available argument for not asserting it in a document at all. A rule stated as a rule and enforced as a list of examples is enforced as a list of examples; the list is therefore maintained as part of the rule rather than as an afterthought, and the correct repair is always to state the intention and name where the switch lives.

The **platform half must be advisory**, must degrade to a reported skip with no token, no network, no `curl` or no `python3`, and must never move the exit code. **It must also reach the release run.** A called workflow sees only the secrets it is handed, so `release.yml` passes the one token this half needs by name rather than inheriting all of them — without that, the advisory settings report was silently absent from the only run that creates a package, which is the run whose report is worth the most (F-46). It reads the repository object and the private-vulnerability-reporting endpoint and reports the issue-tracker state, the wiki state, whether a description is set, and whether private reporting is enabled. Advisory is a ruling, not caution: a fork's settings are the fork's business, and a gate that failed somebody's build over this maintainer's disclosure preferences would be wrong about the licence this project ships under. A token without `administration:read` is reported as *unknown* rather than as a finding, because a fork's pull-request token will not have it.

Deliberately **not** folded into the tree gate. That script's five gates are all offline, all blocking, and all assertions that a file somebody wrote is machine-readable; half of this one is none of those, and a gate whose halves carry different authority should not answer to one exit code.

`boot-smoke` additionally **fetches `/source` anonymously and fails unless the response contains the commit the image was built from.** The stamp travels from a build argument through an MSBuild property, an assembly attribute, a parse and a component (§11.9), and every link in that chain fails *silently*: the page still renders, and it renders "Not recorded", which reads as a configuration choice rather than as a defect. The commit appearing in the response is the assertion nothing weaker can satisfy, and it doubles as a reachability check — no cookie is sent, so a regression that put the licence offer behind authentication fails here.

**Releases** (`release.yml`) call this workflow rather than restating its gates, then derive the version from the tag, pass it and the commit into the image build so the published image reports what the registry called it, and open a GitHub release on the tag. The release step is downstream of the push and idempotent, so a re-run updates the note instead of failing.

## 17. Security posture and accepted risks

Threats mitigated: static-QR capability theft (rotating tokens, ≤120 s life, per-table secret rotation); Argon2 memory DoS (semaphore + rate limit + lockout); display theft (revocation; device holds no secret worth extracting; join secret never leaves the server); credential stuffing (Argon2id, lockout, passkeys-first); stale sessions after admin action (5-minute stamp revalidation); half-applied schema (fail-fast migrations); pairing brute force (hashed single-use codes, TTL, 5/min/IP).

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
| F-21 – F-24 | Editorial: four experiences + display; abbreviation carve-out; generic paths; directives resolved | REQUIREMENTS rev 2 |
| F-25 – F-33 | export.sh fixes; REQUIREMENTS tracked in docs/ | export.sh header; repo layout |
| Claude judgment calls (owner-vetoable, recorded) | Reminder = once at threshold iff no line of the send fulfilled/removed; counter/admin line-changing staff edits also alert loudly; reset forces TOTP re-enrollment only if enrolled pre-reset; obligations pipeline runs on passkey path too; counter fallback = same rotating QR (no short-code) | §10.1–10.2, §3.5, §3.7, §4.5 · ledger notes |

---

## Changelog

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
