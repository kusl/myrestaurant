# myrestaurant — Technical Specification

**Version 1.0 — 2026-07-17 — Status: accepted, implementation-ready.**

This document is the normative implementation contract for the system described in `docs/REQUIREMENTS.md` (rev 2). It is written so that a person or an LLM who has never seen the project can implement it without asking questions. The words **must**, **must not**, **should**, and **may** are used in their RFC 2119 sense. Where this specification and an ADR describe the same decision, they agree by construction; the ADRs in `docs/adr/` carry the rationale, this document carries the mechanism. The decisions register in Appendix A maps every ruling to its embodiment.

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

.NET 10 Identity WebAuthn. RP ID = host of `RESTAURANT_PUBLIC_ORIGIN` (full host, not registrable domain). Registration options: `residentKey=preferred`, `userVerification=preferred`, `attestation=none`. Sign-in supports username-first and username-less (discoverable credential) flows. Store per §8 `passkey_credential` (credential id unique, public key, sign counter, optional transports and label). Fallback library if a framework gap is found: fido2-net-lib (record the fallback by editing ADR-0003). Registration and change-password forms follow platform conventions — `autocomplete="username"` / `autocomplete="new-password"` — so operating-system password managers generate and offer strong passwords automatically. Passkey enrollment is offered to guests after registration and after sign-in as a **dismissible nudge**: always offered, never required, never a gate for guests (the grant-time passkey mandate applies only to the kitchen and administrator roles, §3.7). **Consequence of RP ID binding (ADR-0005):** passkeys are only durable on the stable named-tunnel domain; on quick tunnels (`*.trycloudflare.com`, a Public Suffix List entry) they bind to the per-run subdomain and die with it — quick tunnels are demo-only with password+TOTP.

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

PostgreSQL, current major. Extension: `citext`. All identifiers snake_case, unabbreviated (carve-out per requirements §8: TOTP/HMAC/QR/URL/SQL/TLS). Primary keys `uuid` named `{table}_identifier`, application-generated UUIDv7 (ADR-0011) — **no database defaults for identifiers**. Timestamps `timestamptz`, UTC, named `…_at`. Money `numeric(10,2)`. The DDL below ships verbatim as `src/MyRestaurant.DataAccess/Migrations/0001_initial_schema.sql` (plus `CREATE EXTENSION IF NOT EXISTS citext;` at top).

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

Anonymous with valid token → grant → sign-in/registration (passkey-first, password offered) → join. Member view: the party roster; **my order** — staging area (add item pickers from the menu — deactivated items greyed out and unselectable (§7) — with quantity 1–100 and note; mark-my-pending-line-for-removal) with a Send button that is disabled while empty and shows an all-or-nothing error panel (per-operation reasons) on rejection; below it the committed living order, each line badged pending/fulfilled, removed lines struck-through with actor + reason, price adjustments shown old → new with reason; **party orders** — read-only equivalents for other members; running personal and table totals; history (the guest's **own** past orders at this restaurant — cross-member history is never shown); a per-order **Hide** control on closed orders, confirmed as irreversible from the guest's account (§6.8); and a **profile page** — manage passkeys, password, TOTP and recovery codes; optional phone number and email address (used for manual staff escalation only — nothing in the system sends to them automatically); postal addresses with **free-text labels** ("Home", "Work", "Grandparents' house") — deliberate scaffolding for a possible future delivery/takeout feature, consumed by nothing in version 1 and not to be removed as dead weight. On `SittingClosed`, the surface flips to a read-only settled-bill view.

### 11.2 `/kitchen`

Queue of `kitchen_pending_line` grouped by (table label → person display name → order), ordered by the group's oldest `added_at`; each group shows the send timestamp(s); customization notes prominent. Tap a line → one `fulfillment` event; "fulfill all for this order" → one event, N operations; an Undo affordance on recently-fulfilled lines → `fulfillment_reversal`. An "86" panel lists menu items with active toggles. Loud alert + badge per §10.3.

### 11.3 `/counter`

Open sittings with per-person and table totals (`sitting_bill`); drill-in shows lines with states; price adjustment dialog (new price + required reason); add/remove line (staff edit, optional reason on removal); pending-lines warning then **Close & settle**; a per-table "Show join code" button rendering the rotating QR full-screen (§4.5); closed-sitting lookup (read-only).

### 11.4 `/administration`

Users (create staff, roles grant/revoke, activate/deactivate, **Reset credentials** per §3.7); Tables (create/edit/deactivate, rotate join secret with confirmation, display devices list with pair-code generation and revoke, show rotating QR); Menu (CRUD + activity, event history per item); Sittings (open + recent, end-of-day batch close, post-close corrective actions per §6.7); **Hidden records** (every hidden order system-wide, filterable by username / date range / table, full unprojected record per row, per-record Unhide — §6.8); Event explorer (filter security/order/menu events by subject, actor, type, time); no printed-QR page exists. Administration renders the **complete stored record** everywhere — full event streams, visibility logs, security events — never projected or truncated for the administrator; filters narrow only on explicit request.

### 11.5 `/display/{table}`

Unpaired device → redirect `/display/pair` (code entry). Paired: full-screen table label + rotating QR (server SVG, window-aligned refresh), party-size chip when a sitting is open (via `SittingMemberJoined`/`SittingClosed`), connection-state indicator (circuit down → prominent "offline — see the counter" state; the QR must not silently freeze stale), wake lock. Revoked → pairing screen with "this display was disconnected".

## 12. Observability

OpenTelemetry traces (ASP.NET Core + Npgsql instrumentation), logs, and metrics via OTLP (`OTEL_EXPORTER_OTLP_ENDPOINT` etc.; `run.sh` translates a legacy `UPTRACE_DSN` if present — any OTLP collector works). Custom meters (full snake_case):

`guest_submission_batches_total` · `order_lines_added_total` · `order_lines_removed_total` · `order_lines_fulfilled_total` · `kitchen_reminders_sent_total` · `sittings_closed_total` · `table_join_tokens_validated_total{result}` · `sign_ins_total{method=password|passkey, result=succeeded|failed}` · `password_hash_duration_milliseconds` (histogram). Health: `/healthz/live` (process up), `/healthz/ready` (DB reachable + migrations current); compose healthchecks target these.

## 13. Configuration (environment only)

| Key | Default | Meaning |
|---|---|---|
| `RESTAURANT_NAME` | `My Restaurant` | display + TOTP issuer |
| `RESTAURANT_PUBLIC_ORIGIN` | `https://localhost:8443` (dev) | **the** origin: RP ID host, QR URLs; production = named-tunnel domain |
| `RESTAURANT_TIME_ZONE` | `America/New_York` | rendering only |
| `RESTAURANT_CURRENCY_CODE` | `USD` | display only |
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

Fail-fast validation at startup: origin parses as absolute https URL; Argon2 floor (§3.2); rotation/grant/pairing values ≥ 10 s / ≥ 1 min / ≥ 1 min; connection string present.

## 14. Deployment, TLS, origins (ADR-0004, ADR-0005)

**14.1 Canonical stack** — `compose.yaml`, rootless Podman. Services: `web` (Containerfile build; listens 8080 HTTP inside the network), `postgres` (named volume), `caddy` (dev profile: terminates TLS at `https://localhost:8443` with Caddy's internal CA), `cloudflared` (**production profile**: named tunnel via `CLOUDFLARE_TUNNEL_TOKEN`, forwards to `web:8080`; TLS at Cloudflare's edge). Host ports stay ≥1024 (rootless); if 80/443 are ever wanted directly, that is a host `sysctl net.ipv4.ip_unprivileged_port_start` concern, not this project's default. `podman-compose up` = dev; `podman-compose --profile production up -d` = production. Caddy **may** additionally run in production as an optional staff-LAN fallback (self-signed `restaurant.lan`; staff-only; passkeys will not work on that origin; password+TOTP does) — off by default, documented in OPERATIONS §7.

**14.2 Origin truth** — one `RESTAURANT_PUBLIC_ORIGIN`. Everything (WebAuthn RP ID, QR URLs, links) derives from it. In-house guests hairpin through Cloudflare; **LAN ordering therefore depends on WAN health — accepted risk** per the F-06 ruling.

**14.3 Quick tunnels** — demo-only. `*.trycloudflare.com` is on the Public Suffix List: a passkey's RP ID binds to the random per-run subdomain and dies with the tunnel. The quick-tunnel helper script must print this warning. Demos authenticate with password+TOTP.

**14.4 `run.sh`** — dev entry: checks prerequisites, starts compose (postgres [+caddy]), exports dev defaults (translating `UPTRACE_DSN` → `OTEL_*` if set), `dotnet watch` the web app. Idempotent; `run.sh --containers-only` starts the stack without watch.

**14.5 Aspire** — optional `AppHost` project may exist for F5 convenience; it must never be required by docs, scripts, or CI.

## 15. Backups

`scripts/backup.sh`: `pg_dump -Fc` to `BACKUP_DIRECTORY/myrestaurant-YYYYMMDD-HHMMSS.dump`, prune to `BACKUP_RETENTION_COUNT`; scheduled at `BACKUP_SCHEDULE_TIME` (systemd timer or host cron invoking the script via `podman exec`). `scripts/restore.sh <dump>`: stop web, `pg_restore --clean --if-exists`, start web (migrations verify). **The Data Protection keys volume must be backed up alongside the database** — without it, TOTP secrets and cookies are unrecoverable (§3.4). Restore drill documented in OPERATIONS §6.

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

**16.4 CI:** GitHub Actions — build, unit, integration (service container PostgreSQL), E2E (compose), publish image on tag.

## 17. Security posture and accepted risks

Threats mitigated: static-QR capability theft (rotating tokens, ≤120 s life, per-table secret rotation); Argon2 memory DoS (semaphore + rate limit + lockout); display theft (revocation; device holds no secret worth extracting; join secret never leaves the server); credential stuffing (Argon2id, lockout, passkeys-first); stale sessions after admin action (5-minute stamp revalidation); half-applied schema (fail-fast migrations); pairing brute force (hashed single-use codes, TTL, 5/min/IP).

Accepted, by ruling or by design: token replay within ≤120 s (bounded by membership/visibility rules); WAN dependence of in-house ordering (hairpin — F-06); quick-tunnel passkey loss (PSL — demos only); counter role may operate password-only (no passkey mandate); guest sees table-mates' display names and orders (that's the product); no rate limit on authenticated order sends beyond all-or-nothing validation (single-restaurant trust model).

## 18. Governance

Single-owner project; no outside contributions (`CONTRIBUTING.md`). **Atomic documentation:** a behavior change lands in one commit with its `REQUIREMENTS.md`, this specification, `DOCUMENTATION_REVIEW.md` ledger, and ADR edits. ADRs are edited in place with a History line (never duplicated); supersessions say so explicitly. This specification's version bumps (1.0 → 1.1 …) with a dated changelog appended at the bottom when normative content changes.

## 19. Build order (milestones)

- **M1 — skeleton:** solution layout (§2), Containerfile, compose dev profile, DbUp with `0001_initial_schema.sql`, health endpoints, OTel wiring, `run.sh`.
- **M2 — identity:** Dapper Identity stores, Argon2id hasher (+floor guard, semaphore), passkeys, TOTP + recovery codes, lockout, obligations pipeline, `/setup` bootstrap, roles/policies, security events, admin user management + reset.
- **M3 — tables & joining:** table CRUD + join secrets + rotation, display pairing + device auth + `/display`, token generate/validate + metrics, grant cookie, join flow, sittings + membership.
- **M4 — ordering:** living order + locking protocol, staging UI, batch send + validation, staff edits, fulfillment/reversal, projections + fold + equivalence tests, kitchen surface + alerts + reminder service.
- **M5 — counter & administration:** bills, price adjustment, close & settle, end-of-day, counter fallback QR, menu management + events, event explorer, hide/unhide, post-close corrections.
- **M6 — hardening & production:** full E2E suite (§16.3), backups + restore drill, cloudflared production profile + tunnel docs, quick-tunnel demo script with warning, OPERATIONS runbooks, CI pipeline.

---

## Appendix A — Decisions register (ruling → embodiment)

| Ruling / finding | Decision | Embodied in |
|---|---|---|
| F-06 | Named Cloudflare tunnel = production origin; quick tunnels demo-only (PSL passkey caveat); Argon2id "robust" params; hairpin risk accepted; GoTunnels reference-only, bearer sessions rejected | §3.2, §3.3, §14, §17 · ADR-0005, ADR-0008 |
| F-07 / Q1 | Living order per guest per sitting; client staging; batch sends; one alert per send; pending → fulfilled lifecycle; guests remove own pending only; reversal event | §6, §10, §11.1 · ADR-0007, ADR-0002 |
| F-08 / Q2 / Q3 (supersedes F-09 draft) | TOTP on password path only; no per-user toggle; reset wipes password+TOTP (if enrolled) and forces change + re-enrollment via obligations pipeline on any sign-in path | §3.4, §3.5, §3.7 · ADR-0010 |
| F-12 / Q4 / Q5 | Rotating HMAC join tokens; `table_display` device principal; pairing codes; join grants; counter fallback QR; printed QR removed | §4, §11.5 · ADR-0009 |
| F-10 / F-10b / F-11 | Post-close admin corrective events beside immutable settled total; deactivate-not-delete; guest as actor_role not stored role | §5.3, §6.7, §3.7, §8.2 |
| F-13 / F-14 / F-15 | No Redis v1 (broadcaster interface); compose canonical / Aspire optional; OTLP-generic (`UPTRACE_DSN` translated in run.sh only) | §9, §12, §14 · ADR-0006, ADR-0004 |
| F-16 / F-17 | Backups pg_dump -Fc + retention + keys volume; run.sh defined | §15, §14.4 |
| F-18 / F-19 | Menu item event log; lockout 5/5min, username 3–64 citext, currency/timezone defaults | §7, §3.1, §13, §8.2 |
| F-20 | Hand-written fakes; NSubstitute ok; no Moq | §16.1 |
| F-21 – F-24 | Editorial: four experiences + display; abbreviation carve-out; generic paths; directives resolved | REQUIREMENTS rev 2 |
| F-25 – F-33 | export.sh fixes; REQUIREMENTS tracked in docs/ | export.sh header; repo layout |
| Claude judgment calls (owner-vetoable, recorded) | Reminder = once at threshold iff no line of the send fulfilled/removed; counter/admin line-changing staff edits also alert loudly; reset forces TOTP re-enrollment only if enrolled pre-reset; obligations pipeline runs on passkey path too; counter fallback = same rotating QR (no short-code) | §10.1–10.2, §3.5, §3.7, §4.5 · ledger notes |
