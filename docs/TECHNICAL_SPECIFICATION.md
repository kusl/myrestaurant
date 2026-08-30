# myrestaurant — Technical Specification

**Version 1.58 — 2026-08-29 — Status: accepted, implementation-ready.** (Changelog at the bottom; v1.0 was 2026-07-17.)

This document is the normative implementation contract for the system described in `docs/REQUIREMENTS.md`. It is written so that a person or an LLM who has never seen the project can implement it without asking questions. The words **must**, **must not**, **should**, and **may** are used in their RFC 2119 sense. Where this specification and an ADR describe the same decision, they agree by construction; the ADRs in `docs/adr/` carry the rationale, this document carries the mechanism. Appendix A maps every ruling to its embodiment.

**This document is a contract, not a history.** The long form — every ruling's full argument, the account of what it replaced, and the complete changelog back to v1.0 — is in [`docs/progress/TECHNICAL_SPECIFICATION_THROUGH_V1_50.md`](progress/TECHNICAL_SPECIFICATION_THROUGH_V1_50.md). That file is withheld from the context dump and is still tracked; read it when a paragraph here is too terse to act on.

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
│   ├── progress/                          # archived build log; withheld from dump.txt by path (§18)
│   └── llm/                               # generated context dumps; not authored, not hygiene-checked
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

Pairing: administrator, from the table's admin page, generates a one-time code — 8 characters from the unambiguous alphabet `ABCDEFGHJKMNPQRSTUVWXYZ23456789`, stored **hashed** (SHA-256) in `table_display_pairing_code` with `expires_at = now() + TABLE_DISPLAY_PAIRING_CODE_MINUTES` (default 10), single-use (`used_at`). The device opens `/display/pair` (anonymous; rate-limited **5 attempts/minute/IP** — one of the two policies in `RateLimitedSurfaces`, §11.8 being the other, and this budget stays a compile-time constant because there is no operator decision in it), enters the code; on match the server creates the device row, sets the cookie, marks the code used, and redirects to `/display/{table}`. Failed attempts burn nothing but the rate budget.

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

`menu_item` carries `name`, `price_amount numeric(10,2) ≥ 0`, `is_active`, `description text NOT NULL DEFAULT ''`, `display_order int NOT NULL`, and a mandatory `menu_section_identifier`. Every item is filed under exactly one heading; there is no unsectioned item and no null section. The DDL is §8.2 and it is the schema of record — the paragraphs here state the behaviour that DDL cannot.

**Headings.** `menu_section` (name, `description`, `display_order`, `is_active`) is administered at `/administration/menu/sections/new` and `/administration/menu/sections/{id}`. Four verbs: rename, describe, reposition, activate/deactivate. Activation and deactivation publish `MenuChanged` because they change what a guest can order; rename and describe do not, because they change only how it reads. Every verb writes `menu_section_event` in the same transaction as the row it changes.

**Item verbs.** Each verb is its own call, its own event type, and its own no-op rule: a verb that would write the value already stored returns `NoChange` and writes nothing. The vocabulary is `created`, `name_changed`, `price_changed`, `description_changed`, `section_changed`, `reordered`, `activated`, `deactivated`, and §8.2's `menu_item_event_type_vocabulary` is the declaration of record. `created` carries the name and the price only, so an item created with a description writes two events.

**Placement and order.** A new item is appended at `MAX(display_order) + 1` under a lock on its section row, so two concurrent creations cannot collide. `MoveMenuItemToSectionAsync` refiles an item under another heading and appends it there. Resequencing writes one `reordered` event per heading that actually moved and refuses a list that is not a permutation of the section's current set. An item is reordered within its heading by a separate verb, because the two write different event types. `/administration/menu` is a list of headings each holding its own items, with Up and Down on the item's row as static-SSR forms named from the item's identifier.

**Reads.** Every menu read orders by `(section.display_order, section.name, section.menu_section_identifier, item.display_order, item.name, item.menu_item_identifier)`. The identifier tail is not decoration: it is what makes the order total, and a tie broken by anything else is a tie broken differently on two reads.

**Pictures.** An item may carry one picture and the bytes live in this database (`menu_item_image`, ADR-0015). Four properties are rulings rather than implementation. *One picture per item*, enforced by the primary key rather than by a query. *No `byte_length` column and no `width`/`height`*, because a stored length is a second copy of a fact `octet_length()` already answers. *The declared media type is checked against the bytes' own signature* by a pure function in `Domain`, so a lying `Content-Type` cannot decide what is stored. *`alt` is always emitted and its value is whatever is stored, `''` included* — an empty `alt` tells a screen reader to skip a decorative image, and a missing `alt` makes it read the URL, so the two are not interchangeable.

The picture is served on its own route and attached from the item's own page. `0006` names every constraint it creates, which is what lets §8.2's quoted vocabularies be compared against the migrations at all. `menu_item_image_event` carries `attached`, `replaced`, `removed`, `alt_text_changed`, and every one of them has a sentence on the surface that renders it.

**The browser resizes an oversized picture before the form posts it, and that is an optimisation rather than a check.** It never refuses anything: a refusal is the server's, and the client only ever makes the payload smaller. The server's cap is the one in §8.2 and it is enforced regardless of what the client did. The output is JPEG although §8.2 admits WebP, because the ground is the dining room — a phone that cannot encode WebP still has to be able to attach a photograph.

**Reactions.** A person may like a dish. `menu_item_reaction_event` is an event table rather than a row per person, and `liked`/`unliked` is its vocabulary. Four rulings: liking does not require having ordered the dish, because `order_current_line` records what was eaten and a like records an opinion; the count is staff-facing and a guest sees only their own press, because a count of three on a menu of sixty items is noise that reads as a verdict; the control is in the item's detail panel and never on its card, because a card is a link target and a button inside a link target is a control nobody can press reliably; and a reaction publishes nothing, because §9's `MenuChanged` means *re-read the menu* and an opinion has not changed the menu.

**Comments.** A person may say what they thought of a dish. `menu_item_comment_event` is an event table on `menu_item_reaction_event`'s shape and `submitted`/`withdrawn` is its vocabulary. Six rulings, and the first two are what make the rest small. *A comment is filed against the item and never against an order line*, because `order_current_line` records what was eaten and a comment records an opinion — the same argument the like already settled — and because an order line lives in an append-only log whose settled total is a snapshot, so a comment hanging off one would inherit §6.7's correction rules for no reason anybody asked for. *A comment is staff-facing and a guest sees only their own*, which is §11.4's ruling for the like count applied to text: nothing a guest writes is rendered to another guest, so there is no moderation question to answer and none is invented here. *One standing comment per person per dish*, which is why the fold partitions on `(menu_item_identifier, person_identifier)` exactly as the reaction's does — a thread per person per dish is a conversation, and a conversation needs staff replies, which this stage does not build. *Editing is resubmission and withdrawal is an event*: every version is kept and a withdrawn comment stops being rendered without leaving the log, because a comment cannot be edited out of history any more than an order line can (F-10b, §6.7). *The stored body is trimmed*, and the reason is mechanical rather than tidiness — HTML collapses leading and trailing whitespace, so storing it stores a difference nobody can see, and two bodies that render identically would then compare unequal and defeat the no-op rule this verb shares with every other menu verb. *The length cap is the schema's and is stated once*: `menu_item_comment_event_body_within_cap` is the only place in this repository that says how long a comment may be, `ReadDeclaredBodyCapAsync` asks `pg_get_constraintdef` for the bound, and the writer recognises the refusal by constraint name (**F-107**).

A comment publishes nothing, for the reaction's reason: §9's `MenuChanged` means *re-read the menu* and an opinion has not changed the menu.

**The guest's control.** It is in the item's detail panel beside the like, on the reasoning that put the like there and one step further: a `<textarea>` is interactive content and a `<button>` may hold none, so a box on a card is markup the parser takes apart rather than markup that merely reads badly. Five rulings. *The client's cap is an optimisation and every refusal is the server's* — the same ruling the picture already carries: `maxlength` states whatever `ReadDeclaredBodyCapAsync` read out of the constraint, and where that read cannot answer, no attribute is rendered at all rather than a number nobody checked. *A blank body is a refusal and never a withdrawal*, because clearing the box and pressing Save is what somebody will do, and reading it as a withdrawal would make the surface a second authority on what withdrawal means when the write service already refuses a blank body by name; the refusal names the control that does withdraw instead. *The withdraw control is rendered only where a standing comment exists*, since the verb refuses when there is nothing to withdraw. *The draft belongs to the chosen dish* — it is keyed to the item and reset whenever the chosen item changes, and a menu re-read refreshes the standing set without overwriting it, because a broadcast arriving while somebody is typing must not take their sentence away. *The outcome is declared beside the sentence*, on §11.10's reasoning for `data-live`: a barrier that reads the sentence is asserting the copywriting.

**The staff read.** It is on `/administration/menu` and nowhere else, and four things about it are rulings. *It is the whole-menu read rather than a per-item one*: `ListForPersonAsync` answers for one person and the administrator is the author of nothing here, so narrowing the staff read to the signed-in person would render an empty page to the only people allowed to read these at all — and it would do it without failing. *A dish's own page carries no list of its own*, because a second read narrowed to one item is a second query over the same rows for one surface; the index links every sentence to the dish it is about, which is what a per-item page would have been for. *The block is grouped by dish and presented in the menu's own order, newest first within a dish* — the read's own order sorts on `menu_item_identifier`, which is a UUID ordering shown to a person as though it were the menu's, so the surface projects the read through the item list it already holds and sorts nothing itself. *The count is a chip beside the dish's name and is absent where nobody has spoken*, which is the like count's ruling and its reason: a column exists on every row, and below §11.12's breakpoint a column is a labelled line on every card whether or not there is anything to say.

**Two rules survive every menu rewrite.** A deactivated item stays in history and in every settled order that already names it — deactivation is not deletion (F-10b). And a price change is an event beside the order lines that already carry the old price; it never rewrites them (§6.7).

## 8. Database schema (schema of record)

### 8.1 Conventions

PostgreSQL, current major. Extension: `citext`. All identifiers snake_case, unabbreviated (carve-out per requirements §8: TOTP/HMAC/QR/URL/SQL/TLS). Primary keys `uuid` named `{table}_identifier`, application-generated UUIDv7 (ADR-0011) — **no database defaults for identifiers**, and minted through `IIdentifierFactory`, which **must** hand out values that ascend under PostgreSQL's `uuid` ordering even when two land in the same millisecond. That is a requirement on the factory rather than a property of the format: nine reads in this specification order by an instant and break the tie on an identifier, every mutation stamps all the rows of its transaction with one `IClock.UtcNow`, and `Guid.CreateVersion7()` alone leaves the sub-millisecond bits random — so the tie-breaks were arbitrary until the factory guaranteed otherwise (F-95). Nothing may mint an identifier for a stored row by any other route. Timestamps `timestamptz`, UTC, named `…_at`; **rendered in `RESTAURANT_TIME_ZONE` — never in the reader's zone, and never in the server process's.** A restaurant is a physical place in one IANA zone: a guest abroad reading last week's bill wants the times the meal happened at, and a kitchen ticket must agree with the counter's bill to the minute on every screen in the building. One type performs that conversion — `WebApplication/Time/RestaurantTime` — and nothing else may call `ToLocalTime()`, whose answer is the container's `TZ` (unset, therefore UTC). Its formats are explicit and invariant-cultured for the same reason `MoneyText` refuses `"C"`; the one real choice, 12- versus 24-hour, is `RESTAURANT_CLOCK_FORMAT` (§13) rather than an accident of the base image. Money `numeric(10,2)`. The DDL below is the schema of record as it stands after every applied migration, not the text of any one script: `0001_initial_schema.sql` ships most of it verbatim (plus `CREATE EXTENSION IF NOT EXISTS citext;` at top), `0002` adds the passkey and WebAuthn-state tables, `0003` adds `menu_section` and `menu_section_event`, `0004` and `0005` reach `menu_item` and `menu_item_event` by `ALTER`, and `0006` adds `menu_item_image` and `menu_item_image_event`. Columns and constraints that arrived later carry the script number in a comment, because DbUp journals by script name and an applied script is never edited (F-34) — so reading the shape of a table off `0001` alone is reading it as it was, not as it is.

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
    created_at           timestamptz NOT NULL,
    -- 0004. Both NOT NULL with a DEFAULT, so no existing row is rewritten and no backfill runs.
    -- description: '' means "none" — an optional payload column cannot be tied to its event type by an
    -- equality, so the empty value is a value. display_order: 0, not an appended MAX + 1; see §7.
    description          text NOT NULL DEFAULT '',
    display_order        integer NOT NULL DEFAULT 0,
    -- 0005. NOT NULL from the moment it exists (§7): added nullable, backfilled, tightened, all inside
    -- one DbUp transaction, so no application ever observes the nullable window. NO ON DELETE clause,
    -- which means NO ACTION: §6.8 makes this system's answer to "get rid of it" a hiding flag, and a
    -- section with items under it must not be deletable.
    menu_section_identifier uuid NOT NULL REFERENCES menu_section (menu_section_identifier),
    CONSTRAINT menu_item_display_order_non_negative CHECK (display_order >= 0)
);
-- 0005. PostgreSQL does not index the referencing side of a foreign key, so without this every statement
-- touching a menu_section row scans menu_item. The trailing columns are the tail of §11.1's ORDER BY, so
-- one index answers both. (No index on the ordering columns alone: §11.1 and §11.4 read the whole table,
-- and a sequential scan over the cardinality of a restaurant menu beats reading an index.)
CREATE INDEX menu_item_section_index ON menu_item (menu_section_identifier, display_order, name);

-- Every CHECK on this table is NAMED as of 0004. 0001 declared four of them inline, so PostgreSQL
-- generated menu_item_event_event_type_check, menu_item_event_new_price_amount_check,
-- menu_item_event_check and menu_item_event_check1 — deterministic, undocumented, and not a thing for a
-- migration that runs at startup on somebody else's box to depend on. 0004 drops every CHECK by querying
-- pg_constraint inside a dollar-quoted DO block and adds these back, and 0005 collected on that: it drops
-- menu_item_event_type_vocabulary BY NAME and needs no dollar-quoting at all, which is the whole reason
-- F-78 was a one-migration problem rather than a recurring one.
CREATE TABLE menu_item_event (
    menu_item_event_identifier uuid PRIMARY KEY,
    menu_item_identifier       uuid NOT NULL REFERENCES menu_item (menu_item_identifier),
    actor_person_identifier    uuid NOT NULL REFERENCES person (person_identifier),
    event_type                 text NOT NULL,
    new_name                   text NULL,
    new_price_amount           numeric(10,2) NULL,
    new_description            text NULL,      -- 0004
    new_display_order          integer NULL,   -- 0004
    -- 0005. A real foreign key rather than a bare uuid: §11.4 renders this log to a person, and a section
    -- identifier naming nothing renders as a blank where a heading should be.
    new_menu_section_identifier uuid NULL
                               REFERENCES menu_section (menu_section_identifier),   -- 0005
    occurred_at                timestamptz NOT NULL,
    -- description_changed rather than menu_section_event's described, and reordered rather than
    -- display_order_changed: each table's vocabulary is internally consistent, and this one has said
    -- name_changed and price_changed since 0001. 0005 adds 'section_changed'.
    CONSTRAINT menu_item_event_type_vocabulary CHECK (event_type IN
        ('created', 'name_changed', 'price_changed', 'description_changed',
         'section_changed', 'reordered', 'activated', 'deactivated')),
    CONSTRAINT menu_item_event_new_price_non_negative
        CHECK (new_price_amount IS NULL OR new_price_amount >= 0),
    CONSTRAINT menu_item_event_new_display_order_non_negative
        CHECK (new_display_order IS NULL OR new_display_order >= 0),
    -- Five biconditionals, each total. 'created' carries the name and the price ONLY (§7) — not the
    -- description and not the section, although the menu_item row is written with both.
    CONSTRAINT menu_item_event_name_payload
        CHECK ((new_name IS NOT NULL) = (event_type IN ('created', 'name_changed'))),
    CONSTRAINT menu_item_event_price_payload
        CHECK ((new_price_amount IS NOT NULL) = (event_type IN ('created', 'price_changed'))),
    CONSTRAINT menu_item_event_description_payload
        CHECK ((new_description IS NOT NULL) = (event_type = 'description_changed')),
    CONSTRAINT menu_item_event_display_order_payload
        CHECK ((new_display_order IS NOT NULL) = (event_type = 'reordered')),
    CONSTRAINT menu_item_event_section_payload   -- 0005
        CHECK ((new_menu_section_identifier IS NOT NULL) = (event_type = 'section_changed'))
);
CREATE INDEX menu_item_event_item_index ON menu_item_event (menu_item_identifier, occurred_at);

CREATE TABLE menu_section (
    menu_section_identifier uuid PRIMARY KEY,
    name                    citext NOT NULL UNIQUE
                            CHECK (char_length(name) BETWEEN 1 AND 80),
    description             text NOT NULL DEFAULT '',
    display_order           integer NOT NULL CHECK (display_order >= 0),
    is_active               boolean NOT NULL DEFAULT true,
    created_at              timestamptz NOT NULL
);

CREATE TABLE menu_section_event (
    menu_section_event_identifier uuid PRIMARY KEY,
    menu_section_identifier       uuid NOT NULL
                                  REFERENCES menu_section (menu_section_identifier),
    actor_person_identifier       uuid NOT NULL REFERENCES person (person_identifier),
    event_type                    text NOT NULL CHECK (event_type IN
                                  ('created', 'renamed', 'described', 'reordered',
                                   'activated', 'deactivated')),
    new_name                      citext NULL
                                  CHECK (new_name IS NULL
                                         OR char_length(new_name) BETWEEN 1 AND 80),
    new_description               text NULL,
    new_display_order             integer NULL
                                  CHECK (new_display_order IS NULL OR new_display_order >= 0),
    occurred_at                   timestamptz NOT NULL,
    CONSTRAINT menu_section_event_name_payload
        CHECK ((new_name IS NOT NULL) = (event_type IN ('created', 'renamed'))),
    CONSTRAINT menu_section_event_description_payload
        CHECK ((new_description IS NOT NULL) = (event_type IN ('created', 'described'))),
    CONSTRAINT menu_section_event_display_order_payload
        CHECK ((new_display_order IS NOT NULL) = (event_type IN ('created', 'reordered')))
);
CREATE INDEX menu_section_event_section_index
    ON menu_section_event (menu_section_identifier, occurred_at);

-- 0006. One picture per item, expressed as UNIQUE on the referencing column rather than as a bytea on
-- menu_item: a picture is replaced far more often than a dish is renamed, and a column on the item would
-- put every image inside every menu read (§11.1, §11.4). The UNIQUE is also the index the lookup needs. NO
-- ON DELETE clause, therefore NO ACTION, on 0005's reading of §6.8. There is deliberately NO byte_length
-- column — octet_length(bytes) is the length and a stored integer beside it is one fact written twice —
-- and deliberately no pixel_width/pixel_height, because nothing in this stack can measure them and a
-- dimension taken from the client's word is an unverifiable claim in the indicative (F-101). THE CAP IS
-- WRITTEN HERE AND NOWHERE ELSE: no number appears in C#, which reports a refusal by reading the violated
-- constraint's name. Two constraints rather than one BETWEEN, so an empty file and an oversized one are
-- distinguishable by name.
CREATE TABLE menu_item_image (
    menu_item_image_identifier uuid PRIMARY KEY,
    menu_item_identifier       uuid NOT NULL UNIQUE
                               REFERENCES menu_item (menu_item_identifier),
    content_type               text NOT NULL,
    bytes                      bytea NOT NULL,
    -- 0007. The sentence a screen reader reads instead of the picture. NOT NULL DEFAULT '' and '' means
    -- "none", on menu_item.description's precedent. ON THE PICTURE AND NOT ON menu_item, which is 0007's
    -- ruling: alternative text describes a photograph, so a column on the item would outlive the picture
    -- it described with nothing able to tell that it had stopped being true. A replace therefore CARRIES
    -- IT FORWARD onto the new row rather than resetting it, and a removal deletes it with the bytes.
    alt_text                   text NOT NULL DEFAULT '',
    uploaded_at                timestamptz NOT NULL,
    CONSTRAINT menu_item_image_content_type_vocabulary CHECK (content_type IN
        ('image/jpeg', 'image/png', 'image/webp')),
    CONSTRAINT menu_item_image_bytes_not_empty
        CHECK (octet_length(bytes) >= 1),
    CONSTRAINT menu_item_image_bytes_within_cap
        CHECK (octet_length(bytes) <= 524288)
);

-- 0006. IT REFERENCES menu_item AND NOT menu_item_image, which is the load-bearing decision of that
-- script. A replace mints a new image identifier and drops the old row (§7: the route is keyed on the
-- image, so the URL changes with the bytes and an immutable cache header is truthful), and a removal drops
-- it outright — so the row an event is about is gone by design, and a foreign key to it could only forbid
-- the deletion or cascade the history away. menu_item_image_identifier is therefore a bare uuid with no
-- reference, the opposite of 0005's ruling about new_menu_section_identifier and opposite on purpose: it is
-- evidence that the URL changed rather than a pointer a reader can follow. new_byte_length IS a column
-- here although it is not one above, because after a removal this is the only place that number can live.
CREATE TABLE menu_item_image_event (
    menu_item_image_event_identifier uuid PRIMARY KEY,
    menu_item_identifier             uuid NOT NULL
                                     REFERENCES menu_item (menu_item_identifier),
    menu_item_image_identifier       uuid NOT NULL,
    actor_person_identifier          uuid NOT NULL
                                     REFERENCES person (person_identifier),
    event_type                       text NOT NULL,
    new_content_type                 text NULL,
    new_byte_length                  integer NULL,
    new_alt_text                     text NULL,      -- 0007
    occurred_at                      timestamptz NOT NULL,
    -- 0007 widened this BY NAME, which is the return 0006 collected on naming every constraint it declared:
    -- two ordinary statements, nothing to query and nothing to dollar-quote (contrast 0004's DO block over
    -- 0001's unnamed CHECKs, and F-78).
    CONSTRAINT menu_item_image_event_type_vocabulary CHECK (event_type IN
        ('attached', 'replaced', 'removed', 'alt_text_changed')),
    -- Three biconditionals, all total. 'attached' and 'replaced' carry the format and the size;
    -- 'alt_text_changed' carries the caption and neither of the other two; 'removed' carries none of the
    -- three, being the one type whose whole payload is its own name. 0007 WIDENED NEITHER OF THE FIRST TWO
    -- and that is the surprising half: a caption is not a fact about the file, so the new type sits outside
    -- both right-hand sides and passes each with NULL.
    CONSTRAINT menu_item_image_event_content_type_payload
        CHECK ((new_content_type IS NOT NULL) = (event_type IN ('attached', 'replaced'))),
    CONSTRAINT menu_item_image_event_byte_length_payload
        CHECK ((new_byte_length IS NOT NULL) = (event_type IN ('attached', 'replaced'))),
    CONSTRAINT menu_item_image_event_alt_text_payload
        CHECK ((new_alt_text IS NOT NULL) = (event_type = 'alt_text_changed')),
    CONSTRAINT menu_item_image_event_new_content_type_vocabulary
        CHECK (new_content_type IS NULL OR new_content_type IN
            ('image/jpeg', 'image/png', 'image/webp')),
    CONSTRAINT menu_item_image_event_new_byte_length_bounded
        CHECK (new_byte_length IS NULL
               OR new_byte_length BETWEEN 1 AND 524288)
);
CREATE INDEX menu_item_image_event_item_index
    ON menu_item_image_event (menu_item_identifier, occurred_at);

-- 0008. Two types, no payload columns, so no paired biconditional: each type carries its own name and
-- nothing else, which is order_visibility_event's shape. NO actor_person_identifier, deliberately — the
-- subject of a reaction IS the person reacting, and no surface in §11 could offer to press this on
-- somebody else's behalf, so an actor column would be constrained to equal its neighbour on every row
-- that will ever exist. Both references are real foreign keys, unlike menu_item_image_event's bare uuid,
-- because nothing here is ever deleted.
CREATE TABLE menu_item_reaction_event (
    menu_item_reaction_event_identifier uuid PRIMARY KEY,
    menu_item_identifier                uuid NOT NULL
                                        REFERENCES menu_item (menu_item_identifier),
    person_identifier                   uuid NOT NULL
                                        REFERENCES person (person_identifier),
    event_type                          text NOT NULL,
    occurred_at                         timestamptz NOT NULL,
    CONSTRAINT menu_item_reaction_event_type_vocabulary CHECK (event_type IN
        ('liked', 'unliked'))
);
-- The fold's prefix, and the write's own lookup is the same prefix, so one index serves both directions.
-- Deliberately NOT UNIQUE on any prefix: a person may like, unlike and like again, and a unique index
-- would be the delete-on-unlike schema arriving through the back door.
CREATE INDEX menu_item_reaction_event_item_person_index
    ON menu_item_reaction_event (menu_item_identifier, person_identifier, occurred_at);

-- 0009. menu_item_reaction_event's shape with one payload column, so the paired biconditional is back:
-- 'submitted' carries a body and 'withdrawn' carries none, and the CHECK is an equality between two
-- predicates rather than two implications, because an equality cannot be half-written. body is NULLABLE
-- and that nullability IS the fold's answer — a standing comment is a row whose body is not null, which
-- the biconditional makes exactly equivalent to "the last event was a submission" without the view
-- having to filter on event_type after a DISTINCT ON it cannot filter after.
--
-- THREE named constraints over one column rather than one conjunction, because each is a different
-- refusal an operator or a caller has to be able to tell apart: a vocabulary, a payload rule, a blank
-- body, and a cap. The cap is the ONLY statement in this repository of how long a comment may be
-- (F-107) and DapperMenuItemComments recognises it by conname, which is why it is named and separate:
-- a conjunction would report the whole expression and the writer could not tell over-cap from blank.
CREATE TABLE menu_item_comment_event (
    menu_item_comment_event_identifier uuid PRIMARY KEY,
    menu_item_identifier               uuid NOT NULL
                                       REFERENCES menu_item (menu_item_identifier),
    person_identifier                  uuid NOT NULL
                                       REFERENCES person (person_identifier),
    event_type                         text NOT NULL,
    body                               text,
    occurred_at                        timestamptz NOT NULL,
    CONSTRAINT menu_item_comment_event_type_vocabulary CHECK (event_type IN
        ('submitted', 'withdrawn')),
    CONSTRAINT menu_item_comment_event_body_payload
        CHECK ((event_type = 'submitted') = (body IS NOT NULL)),
    CONSTRAINT menu_item_comment_event_body_not_blank
        CHECK (body IS NULL OR btrim(body) <> ''),
    CONSTRAINT menu_item_comment_event_body_within_cap
        CHECK (body IS NULL OR length(body) <= 1000)
);
-- The reaction index's reasoning, unchanged: the fold's prefix is also the write's own lookup, and it is
-- deliberately NOT UNIQUE on any prefix, because a person may comment, withdraw and comment again.
CREATE INDEX menu_item_comment_event_item_person_index
    ON menu_item_comment_event (menu_item_identifier, person_identifier, occurred_at);

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

Note on the `menu_item_event` CHECKs: they are **biconditionals**, one per payload column, each binding that column's presence to exactly the event types that carry it — the DDL above is the enumeration and **this sentence states no count of either** (**F-111**). It said *five types* and named two payload columns for three migrations after the table had eight and five, directly beneath a DDL block that was correct the whole time: the block is what a person adding a table copies, and the prose is what nobody re-reads. It also carried a testing obligation — *assert all ten combinations* — that §16.4 separately **rules against writing**: a payload the CHECK refuses is refused loudly, by name, on the first insert, and a test re-asserting what a constraint already enforces is a monument (F-47). What integration tests owe this table is the class of failure the database *cannot* see — whether the pair of rows was written at all, whether a no-op quietly appended an event saying nothing happened, and whether a position was assigned by appending or invented by counting — and §16.4 names where each of those lives. The agreement between this document's quoted DDL and the migrations that apply it is asserted by `MenuEventVocabularyContractTests` rather than remembered.

Note on the `menu_section_event` CHECKs: there are three of them, they are biconditionals of the same shape, and they are **named** rather than declared inline. `new_name` is present exactly when the type is `created` or `renamed`, `new_description` exactly when it is `created` or `described`, `new_display_order` exactly when it is `created` or `reordered`; `activated` and `deactivated` therefore carry none of the three, and `created` carries all three — a section is created with a name, a description that may be the empty string, and the position it was appended at. They are named because `0001` declared `menu_item_event`'s inline and PostgreSQL generated `menu_item_event_check` and `menu_item_event_check1`, which are deterministic, undocumented, and not a thing for `0004` to depend on when it has to widen them. `0004` accordingly did not depend on them: it dropped every CHECK on that table by querying `pg_constraint` and added named ones back, so `0005` faces the position this note anticipated.

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

-- 0008. The same fold with a two-column partition: the last press from each person about each dish is
-- that person's current opinion, and everything before it is history and is kept. The identifier
-- tie-break is load-bearing rather than defensive here — two presses in one millisecond is an ordinary
-- gesture, and one transaction stamps one instant (§8.1), so without it DISTINCT ON returns the OLDEST
-- row and a double-tap reads back as the state before it.
--
-- (event_type = 'liked') rather than a NOT IN or a CASE, on the line above's precedent, and the equality
-- is what makes a third word in the vocabulary a VISIBLE change: a 'loved' added to the CHECK would fold
-- to FALSE here, which is wrong and which a reader can see, where "not unliked" would fold to TRUE and
-- look correct.
CREATE VIEW menu_item_reaction_current AS
SELECT DISTINCT ON (menu_item_identifier, person_identifier)
    menu_item_identifier,
    person_identifier,
    (event_type = 'liked') AS is_liked
FROM menu_item_reaction_event
ORDER BY menu_item_identifier,
         person_identifier,
         occurred_at DESC,
         menu_item_reaction_event_identifier DESC;

-- 0009. The reaction fold with a payload, and the event identifier is projected rather than only sorted
-- on: §7 requires every menu read's order to end in an identifier, and a staff read of every standing
-- comment orders by item and then by recency, which two comments stamped in one millisecond do not
-- total on their own.
--
-- No WHERE clause, deliberately. DISTINCT ON has to see the withdrawal to know it is the latest row, so
-- filtering here would return the submission BEFORE a withdrawal and report a withdrawn comment as
-- standing. The caller filters on body IS NOT NULL instead, which the payload biconditional makes an
-- exact test of "the last event was a submission".
CREATE VIEW menu_item_comment_current AS
SELECT DISTINCT ON (menu_item_identifier, person_identifier)
    menu_item_comment_event_identifier,
    menu_item_identifier,
    person_identifier,
    body,
    occurred_at
FROM menu_item_comment_event
ORDER BY menu_item_identifier,
         person_identifier,
         occurred_at DESC,
         menu_item_comment_event_identifier DESC;
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

The guest ordering surface. An **interactive island inside a static-SSR page**: `/table/{id}` is `[ExcludeFromInteractiveRouting]` because the join flow writes the grant cookie and issues redirects, and a Blazor circuit can do neither. The island carries the picker, the basket, the totals and the roster; everything that responds to a press is inside it. The menu renders grouped under its headings, each item showing its description, a thumbnail beside the name where a picture is stored, and — in the item's detail panel only — the like control and the guest's own comment box. Staging is client-side; a Send is one batch and produces exactly one kitchen alert. A guest may remove only their own pending lines. Closed orders may be hidden from the guest's own history and from nobody else's (§6.8).

### 11.2 `/kitchen`

Interactive server, prerendered. The queue in send order, one loud alert per send, one reminder per unfulfilled send after `KITCHEN_SUBMISSION_REMINDER_SECONDS`. Fulfilling a line is a line-level verb (§6.4). Audio is armed by a press, never on load, and the wake lock is requested only while the board is visible (§10.3).

### 11.3 `/counter`

Per-person and per-table totals for every open sitting, price adjustment with a mandatory reason, close and settle. Closing warns when pending lines remain and proceeds if confirmed. After close the table is read-only and the settled total is immutable (§5.3).

### 11.4 `/administration`

Six areas: accounts, tables, menu, sittings, hidden records, events. Static SSR throughout — a full-page form post, post/redirect/get, no circuit. The row of area links is rendered once by `AdministrationAreaLinks`, all six including the self-link marked `aria-current="page"`; a page may not carry its own copy.

The menu index carries the two staff-facing reads the guest's surface writes and cannot see: the like count as a neutral chip beside a dish's name, and every standing comment as a block of its own, grouped by dish in the menu's order. Both are absent rather than zero where nobody has pressed or said anything (§7). A withdrawn comment is not in the block and has not left `menu_item_comment_event`; this is the read of what stands, not the record.

### 11.5 `/display/{table}`

The table-side screen. Anonymous only until paired, then a `table_display` device principal. Interactive server, so the rotating QR advances without a reload (§4.2, §4.3).

### 11.6 `/account`

Profile, password, passkeys, authenticator, recovery codes. Every credential change writes a security event.

### 11.7 The wall clock (every page, both layouts)

Every instant this application renders is rendered in `RESTAURANT_TIME_ZONE`, through one type, for every reader (F-36). `RESTAURANT_CLOCK_FORMAT` settles 12-versus-24. The footer carries a ticking clock that states the convention in words, on every page, so a reader never has to guess whose midnight a timestamp means.

### 11.8 `/register`

Guest self-registration. Anonymous, static SSR, two steps over a Data-Protection-protected ticket cookie, reached from `/sign-in` by a link carrying the return URL. A passkey is offered first and may be declined only when a password was set. The endpoint refuses beyond `GUEST_REGISTRATION_ATTEMPTS_PER_WINDOW` in `GUEST_REGISTRATION_WINDOW_MINUTES` (§11.11).

### 11.9 The colophon and `/source` (every page, both layouts)

AGPL §13 is discharged by the program itself: every page carries a colophon naming the version and the abbreviated revision, and `/source` gives the offer of source with the full revision and `RESTAURANT_SOURCE_URL`. A build told neither a version nor a revision says so rather than inventing one.

### 11.10 The live-surface contract (every interactive surface)

Prerendering renders a surface that responds to nothing, and a dead island is indistinguishable from a live one by looking at it. So every interactive surface carries `data-live` and, once its first load completes, `data-loaded` — and the interactive set is **derived from `[ExcludeFromInteractiveRouting]` rather than from a list** (F-47). A test barrier waits on a destination-specific element, never on markup a component emits in every state (F-44), and never on `WaitForURLAsync`, which resolves on `history.pushState` before content arrives.

### 11.11 Response security headers (every response, ADR-0013)

The headers are the application's, not the proxy's, because the application is the only thing that is present in every deployment. `SecurityHeadersMiddleware` sets `Content-Security-Policy`, `X-Content-Type-Options: nosniff`, `Referrer-Policy`, `frame-ancestors`, and the rest of ADR-0013's set on every response including errors.

The policy admits no inline script and no inline event handler. `img-src 'self' data:` is what lets a same-origin picture fetch and the single `data:` icon in `App.razor` both work; there is exactly one `data:` URL in the tree and it is that icon. An inline `<style>` block in a component is permitted and is this project's standing arrangement for rules nobody else reads.

Rate limiting is opt-in per endpoint and every policy is registered once. A surface that refuses is refused by the endpoint rather than by a page, and no two surfaces are refused in identical words.

### 11.12 The handheld layout contract (every surface, every screen)

**Every surface is laid out for a handset first and widened by exactly one breakpoint.** Normative, on R§1: the people who use this are standing up, holding a phone.

- **Exactly one breakpoint in the whole tree.** `wwwroot/app.css` states the narrow layout unconditionally and contains exactly one `@media (min-width: 48rem)` query. A second is the same number written in a second place, and two places drift (F-48, F-50, F-56). A component may not declare a media query at all: a media query is the arrangement, not a detail of one page.
- **`min-width`, never `max-width`.** A max-width query says the wide layout is the default and the handset is the exception, which is the arrangement the defect was found in.
- **48rem, stated once.** A custom property cannot be used in a media query, so the value is a literal in that one query. The primary target is a 375px handset.

**Touch targets are 2.75rem** (`--touch-target`, 44px at the default root size) on every control a person presses. **The number is written once, as the property; a literal beside it is a finding (F-65).** **Text in a control is at least 16px**, because iOS Safari zooms a smaller one — and that floor carries **no layout tolerance**, so a 15px control fails even though it is within a pixel (Slice 64). **The floor is enforced over the selectors the §16.3 barrier names, and until Slice 71 every one of them was an `<input>`** — so §11.1's like control declared 15.2px for thirteen slices with nothing able to say so (**F-127**). The detail panel's buttons are named subjects now, and the floor is a floor on controls rather than on fields.

**A record list is the shape every index takes.** Below the breakpoint each row is a card; above it the same markup is a table with a header row, and **every cell states its own label** from a `data-label` attribute. **A row's action is never a right-hand column**: it is the last thing in the card and the full width of it, and the row's primary cell is also a link, so the way in is at x=0 whatever the viewport.

**The shared vocabulary is declared once**, in `app.css`. A shared name is re-declared when a *selector* declares it, not when a comment mentions it (F-67). **Every custom property a rule reads is declared in `:root`** (F-64), **every colour a rule renders is a value `:root` declares** (F-68), and **a reference to a declared property carries no fallback** (F-69) — a fallback is what let F-64's missing declaration render plausibly for nine slices. **`overflow-wrap` is declared exactly once, on `body`, as `anywhere`**; it inherits, so a second declaration only ever reaches the elements somebody remembered.

**What this does not require:** that every surface be *optimised* for a handset, only that the handheld layout is the default and the surface is legible and operable at 375px.

**The contract is asserted at two levels and neither reaches the other's claim.** Its structure — one breakpoint, one vocabulary, a label on every cell, no literal where a property belongs — is decidable from text, and `HandheldLayoutContractTests` decides it. Whether a declared rule *reaches* a rendered element is only answerable by a browser, and the §16.3 barrier measures it at 375×667. Colour is reached by neither level, and that is stated rather than left as an omission.

**The browser level reaches the surfaces `HandheldSurface` names, and no others.** It names four: §11.4's administration pages (scenario 16), §11.1's guest ordering surface (scenario 21), and §11.3's counter board and bill at the till (scenario 10). **§11.2's kitchen board is outside them and is named here rather than left to be inferred from a file** (**F-128**) — R§1 justifies this whole section with a phone in somebody's hand and the kitchen is the station most of that sentence is about, so its absence is a deferral rather than a decision. It is not measured because a handheld kitchen page needs a kitchen credential on a second browser context, which is arrangement rather than assertion, and no scenario holds that arrangement today. A surface this section governs and the barrier does not reach is unmeasured whichever of the two is the reason (**F-118**, **F-127**), which is why the set is written down and not counted.

**A control is what a person presses to make something happen**, and that is what the barrier's selectors name: a button, a link that acts as one, a field. A link inside a sentence is body text and is measured by nothing here. That boundary is deliberate and is the reason `.counter-back a` and its kind are absent from the set above.

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
| `GUEST_REGISTRATION_ATTEMPTS_PER_WINDOW` | `60` | §11.8 — `/register` attempts per window **per client address**. Partitioned by address, and over the tunnel a venue's whole dining room shares one, so this is a per-venue budget; the default is generous on purpose and the floor of 10 protects guests rather than the server (F-115) |
| `GUEST_REGISTRATION_WINDOW_MINUTES` | `10` | §11.8 — the window the count above is taken over |
| `ARGON2_MEMORY_KIBIBYTES` / `ARGON2_ITERATIONS` / `ARGON2_PARALLELISM` / `ARGON2_MAX_CONCURRENT_HASHES` | `65536` / `3` / `1` / `4` | §3.2 + floor guard |
| `BACKUP_DIRECTORY` / `BACKUP_SCHEDULE_TIME` / `BACKUP_RETENTION_COUNT` | `/var/lib/myrestaurant/backups` / `03:30` / `14` | §15 |
| `OTEL_*` | unset | standard OTel variables; `UPTRACE_DSN` translated by `run.sh` only |
| `CLOUDFLARE_TUNNEL_TOKEN` | — | production profile, cloudflared |

Fail-fast validation at startup: origin parses as absolute https URL; source URL parses as absolute http-or-https URL; Argon2 floor (§3.2); rotation/grant/pairing values ≥ 10 s / ≥ 1 min / ≥ 1 min; registration budget ≥ 10 attempts / ≥ 1 min; connection string present.

**A setting reaches the process or it does not exist (F-50).** `compose.yaml`'s `web` service enumerates its environment key by key and takes no `env_file`, so this table is not a description of what a deployed container receives — it is a description of what the *program* reads, and the two are joined only by somebody having written the key in a third place. Every key in this table **must** therefore appear in the `web` service's `environment` mapping and in `.env.example`, and the agreement is asserted by `ConfigurationSurfaceTests` (§16.4) rather than remembered. The failure this rule exists to stop is silent by construction: an unpassed variable is not an error, it is the compiled-in default, rendering a page indistinguishable from a correctly configured one.

Two consequences worth stating. A variable whose default is more than a formatting choice — `RESTAURANT_SOURCE_URL` is the case in hand — **must** be passed through with an *empty* default rather than with its value repeated, so that the fallback stays decided in one place and a fork's own edit to that place is not overridden by this file. And the rule runs in one direction only: a key in `compose.yaml` that this table does not list is not a finding, because `POSTGRES_*` is consumed by the database image and `OTEL_*` by the exporter under its own published contract, and a gate that reported those would report findings on a correct tree (F-41).

## 14. Deployment, TLS, origins (ADR-0004, ADR-0005)

**14.1 Canonical stack** — `compose.yaml`, rootless Podman. Services: `web` (Containerfile build; listens 8080 HTTP inside the network), `postgres` (named volume), `caddy` (dev profile: terminates TLS at `https://localhost:8443` with Caddy's internal CA), `cloudflared` (**production profile**: named tunnel via `CLOUDFLARE_TUNNEL_TOKEN`, forwards to `web:8080`; TLS at Cloudflare's edge). Host ports stay ≥1024. `podman-compose up` = dev; `podman-compose --profile production up -d` = production. Every image is named by a **fully qualified** reference — a short name is resolved against whatever registry list the host happens to carry, which is how the stack failed to start on a stock Debian (F-51).

**14.1a Build context (normative)** — the image build must see the publish graph and nothing else. `.dockerignore` at the repository root is an **allow-list**: `.editorconfig`, `Directory.Build.props`, `Directory.Packages.props`, `global.json`, `src`, minus `src/**/bin` and `src/**/obj`. A deny-list is not acceptable and the distinction is the finding (F-45): `.gitignore` is a deny-list, it was already correct, and it protected nothing, because a build context is not a commit. An allow-list is also the only form that covers the file nobody has added yet.

**14.1b Substitution (normative for the host, not for the file)** — `compose.yaml` sets values with the `${NAME:-default}` form, and not every engine applies the default. `scripts/check_compose_substitution.sh` asks this host whether it does, and exits 3 when the placeholder reaches the container as literal text (F-57).

**14.2 Origin truth** — one `RESTAURANT_PUBLIC_ORIGIN`. Everything (WebAuthn RP ID, QR URLs, links) derives from it. In-house guests hairpin through Cloudflare; **LAN ordering therefore depends on WAN health — accepted risk** per the F-06 ruling.

**14.3 Quick tunnels** — for demos. `scripts/quick_tunnel.sh` brings the stack up, opens a quick tunnel, discovers the assigned `*.trycloudflare.com` URL, exports it as `RESTAURANT_PUBLIC_ORIGIN`, recreates `web`, waits for `/healthz/ready`, and holds the tunnel in the foreground. Because the RP ID is derived per request (§3.3, ADR-0005) and `https://*.trycloudflare.com` is trusted by default, **passkeys work within a run** — including a passkey-only account. Every run gets a fresh random subdomain, so passkeys and bookmarks do **not** carry across runs. A quick tunnel must never carry the *bootstrap* of a real instance (§3.6).

**14.3a Detached demo instances** — `scripts/dev_instance.sh` serves the case §14.3 cannot: a host that is not the developer's workstation, reached over SSH, running a build testers will use for days, with **no .NET SDK**. Three properties distinguish it and each is a requirement: it **exits** rather than holding a terminal; it **proves the instance answers** before printing the URL rather than printing two container ids (F-53); and it **diagnoses** — `diagnose` prints both logs and says how to read them, because the failure an operator actually hits is a 502 from the edge with a healthy-looking stack behind it.

**14.4 `run.sh`** — dev entry: checks prerequisites, starts compose (postgres [+caddy]), exports dev defaults (translating `UPTRACE_DSN` → `OTEL_*` if set), `dotnet watch` the web app. Idempotent. `run.sh --containers-only` starts the stack without watch; `run.sh --smoke` boots once, checks health, and exits.

**14.5 Aspire** — an optional `AppHost` project may exist for F5 convenience; it must never be required by docs, scripts, or CI.

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
10. Counter closes (pending-line warning shown) → table flips to settled read-only; totals match. **And the counter does all of it from a 375×667 handset**, which is the first time anything in this repository has laid a staff station out at the width R§1 says it is worked at. That is a second subject in one scenario, and it is here rather than in a scenario 22 on the reason Slices 59, 60, 61 and 71 each gave: *the arrangement already exists*. A barrier over §11.3 wants a signed-in counter member, an open sitting with at least one line still with the kitchen, a bill carrying the per-line controls, the staff-add form and the settle panel — which is this scenario's middle, exactly as it already stands. The counter is the only page made handheld: the administrator's page and the guest's are untouched, so the assertions above and below it measure nothing new. **Two measurements, at the two moments the surface has its controls.** The board is measured while the sitting is still open, because `.counter-sitting-actions` is the way in to a bill and it exists only while there is one — measuring after the close would report a page whose only controls had gone. The bill is measured **where it stands**, immediately before the close is begun, because `_confirmingClose` swaps the settle panel's controls and the close then removes the per-line actions, the staff-add form and the close button together; a navigation would have to re-arrange all of it. **Nothing above the barrier can tell what width it ran at** — Playwright scrolls an element into view before pressing it — which is what makes the viewport one argument rather than a rewrite, and the two subjects fail in ways nothing could confuse: a total that does not match is a fold or a projection, and a control under 44px is a stylesheet. **Every selector in both sets is required to match**, so a renamed class fails by name rather than by a floor a group going quiet cannot move.
11. Guest hides a closed order → it disappears from their own history (staff and admin views unchanged); admin filters the hidden-records view by username → Unhide restores it.
12. Admin resets a TOTP-enrolled user → user password sign-in → forced password change → forced TOTP re-enrollment → lands home; passkey sign-in path also hits the pipeline.
13. Passkey sign-in of a TOTP-enrolled user → **no** TOTP challenge.
14. Expired token URL → friendly expiry page; token from previous window → accepted.
15. Admin rotates a table's join secret → in-flight token dies; display's next window works.
16. Admin works §11.4's administration surfaces at 375×667 → no surface is wider than its own viewport, every row's action, every filter's submit and every detail form's button lies inside it, every control is at least 44px tall (§11.12). Four surfaces when written, six once Stage 1b converted the two explorers, ten since it finished the indexes and added the detail pages beside them — every §11.4 surface but the one that needs a sitting to exist.
17. Admin names two headings and puts a described item under each → a guest at a table reads the menu **grouped under those headings, in the order the administrator chose**, with each description on its card and in the detail panel → a third item created under an existing heading joins it rather than starting a new grouping, and lands at the end of it → the admin **switches one heading off from the section editor** and the guest's already-open menu loses that heading and everything under it, while the other heading's items stay present, in order, and orderable → switching it back on restores the menu exactly as it was. Appended rather than inserted, because the harness names scenarios by number in a great many places. This is the first scenario that reads `menu_item.description` end to end: `0004` shipped the column and Slice 39 built the card that can show it, and neither had anything asserting the sentence arrives. **The last two steps were drafted for Slice 40 and cut from it**, and the cut was recorded rather than made quietly — §7's asymmetry needs `SetMenuSectionActiveAsync` to have a surface, and asserting it without one would have meant either a harness reaching past the UI, which §16.3 refuses, or a verb wired for a test, which is worse. They are also the only end-to-end proof that **deactivating a heading does not cascade to its items**: a flip that had cascaded comes back with the pie marked unavailable, and the restoration step is what says so. **A final step reads the administrator's own sections-first index against the guest's menu, and the assertion is where the two disagree.** A third heading is created and left empty: §11.1 renders no empty heading to a guest and §11.4 renders the complete record to the administrator, so the same instant must produce three groups on the index and two groupings on the menu, with the difference being exactly the empty one. Reading either surface alone says nothing — a heading missing from the guest's menu has three possible reasons, and a heading present on the index has none — so it is the comparison that is the test. The step also asserts the index's own ordering, which is stored rather than alphabetical, and that the refile in the previous step is visible from the administration side too, so an index grouping by anything other than `menu_item.menu_section_identifier` fails here even where the guest's menu is right. **And then it moves things, which is the oldest open item the menu plan carried.** The two resequencing verbs landed in Slices 47 and 48 with the controls that call them, and §16.3 scenario 16 has measured those controls ever since — where they sit, how tall they are, that they lie inside a 375px viewport — while nothing in this repository had ever pressed one. A heading is moved **down** and the guest's already-open menu re-orders itself, then moved **back up** and it returns; an item is moved **up within its heading** and the other heading is untouched, then moved back. Both directions of both controls, because a page that wired Up and left Down inert passes every assertion written before this step. **What only a browser can say is that the whole-ordering POST reaches the form that owns it:** every heading renders two static-SSR forms named from its own identifier, so a three-heading menu carries six distinct `@formname` values and a dispatch that routed a press to the wrong one would move the wrong heading and report success — which the write service's own integration facts cannot see, because they never render a form. **The restoration is the stronger half of each pair**, on the reasoning the visibility flip already uses one step up: a resequence writes absolute positions `0…n-1` over the list it was sent, so an implementation writing a relative offset gets the first move right and cannot get the second one right as well. **The disabled edges are read off the index too**, which is a §11.4 ruling nothing had an opinion about: both controls are rendered on every group and the one that would exchange with nothing is disabled rather than omitted, so the first heading offers no Up and the last offers no Down — presence is asserted by the reader, which refuses a group carrying anything but two, and enabled-ness is what the scenario compares. **What is deliberately not asserted here is the count of `reordered` events**: one per row that actually moved is the write service's rule, asserted against a real PostgreSQL by `MenuSectionResequenceTests` and `MenuItemResequenceTests`, and a browser adds nothing to it.
18. Admin attaches a photograph to a dish → the redirected page **renders**, the thumbnail decodes in the browser, §11.4's picture history carries the attach, and the caption editor writes one. The second clause is the scenario: attaching worked before Slice 55 — the row committed and the redirect was issued — and what did not work was the page the redirect landed on, because a `ValidationMessage` sat outside its `EditForm` inside `@if (_picture is not null)` and threw on the first render in which a picture existed (**F-106**). Every assertion here is a render of that block. The thumbnail is required to have *decoded* rather than merely to be in the markup, which is the only way to assert in one place that §7's route answered, that the stored content type suits the stored bytes, and that §11.11's `img-src` admitted it.
19. Admin chooses a picture **over §8.2's cap** → the browser reduces it and the server stores the smaller one. The whole of Stage 4e, and not assertable anywhere else in this repository: the resizing happens in a `<canvas>` in a real browser, on a file chosen through a real `<input type="file">`, and what proves it worked is not that a smaller file exists but that §11.4's panel reports a JPEG of fewer bytes than were chosen. **The cap is never written in the scenario** — it is read off the rendered `data-picture-byte-budget`, and what is asserted is the pair of inequalities that hold whatever the cap is.
20. Admin uploads a picture the browser **cannot name** — no extension, `application/octet-stream` — and the server stores it as what its **bytes** are. The open item this feature carried for six slices (**F-109**): `IFormFile.ContentType` is the operating system's extension map rather than a fact about the file, so a genuine PNG from a system with no mapping was refused for a format that was not its own. The picture is deliberately **under** §8.2's cap, which is what makes the scenario about F-109 and nothing else — Stage 4e narrowed this defect without closing it, because anything the downscaler touches returns from `canvas.toBlob` as `image/jpeg` whatever it was labelled, and a file that already fits is left completely alone. The closing assertion is that the stored format is `image/png` and is **not** the declared one, because accepting the upload alone would also be satisfied by a server that believed the label and then handed it back as a response header on this origin for a year.
21. A guest at a table **likes a dish, and the opinion is still there after the page is reloaded**. The reload is the scenario. Everything before it — the control renders, it reports unpressed, pressing it reports pressed — is satisfied exactly as well by a `bool` field on a Blazor component that no database ever hears about, and that implementation is not a straw man: it is what *make the heart fill in when you tap it* produces, it is smaller than the real one, and every unit fact and every other scenario stays green against it. Reloading destroys the circuit and every field on it, so the second reading can only have come from `menu_item_reaction_current`. Four further claims ride on the same arrangement, each refusing an implementation that passes the ones before it: there is **nothing to press until an item is chosen**, which is where §7 puts the control and is what keeps the card a single element; the *other* dish, chosen straight after, reports **unpressed**, so the opinion is about a dish rather than about the surface or about whether this person has liked anything; and the press is then **withdrawn and reloaded again**, because a verb that only ever appended `'liked'` rows passes every step above — the fold would answer from the last row it wrote. **A sixth claim closes the feature and is the only place in this repository where §11.1's write and §11.4's read meet:** while the press stands, the administrator's index reports **one** like against that dish and **none** against the other; once it is withdrawn, it reports none against either. Two different queries against the same rows, written for two different people, and nothing but a browser can say they describe the same event — a count over `'liked'` *events* rather than over current opinions passes every step before it, and *the count is 1* is also what a page hard-wired to report 1 would say, which is why the second dish is asserted alongside. **No count is on the guest's screen to read**, which is Stage 5a's ruling and is held as a unit fact over the whole guest directory rather than here, in two seconds rather than in a browser. **A seventh claim is Stage 5c's and it is the only thing in this repository that can make it:** the kitchen 86s the dish, the guest's open menu marks the card unavailable, and the panel is reached through the second control beside that card — where the like reports unpressed, is pressed, and is read back as **one** by §11.4's index. Before that control existed the panel never opened for a refused card, so *the salmon is off tonight and it is still the best thing here* was an opinion this surface could not record. The pudding is chosen first, deliberately: the salmon's panel is still open from the step before and going off the menu does not close it, so without that the claim would be satisfied by a panel that had simply never gone away. The panel's *Available* row is read by the term the markup **declares** rather than the one the stylesheet paints, and this step is the first caller `ChosenItemDetail.Facts` has ever had (**F-113**). It ships in the slice that built the control rather than a slice later: this project deferred a picture scenario four times with a recorded reason each time and the cost was **F-106**, so a control with the identical profile — an interactive island, a circuit event, a toggle that looks right in source — gets its scenario on the way in (**F-109**). **And the guest has been on a 375×667 phone since the join, because this scenario now closes with §11.12's barrier over §11.1** (Stage 1d). That is a second subject in one scenario and it is here rather than in a scenario 22 for the reason Slices 59, 60 and 61 each gave: *the arrangement already exists*. A barrier over the guest's menu wants an available dish and a refused one, the way-in control beside the refused card, a panel open on it with a like inside, and a staged line so the basket has controls and Send has something to send — which is this scenario's closing state plus one staging. A scenario 22 would have bought a second container, a second passkey registration and a second join to arrange what is already standing, and the two subjects fail in ways nothing could confuse: an opinion that does not survive a reload is a fold reading the wrong row, and a control under 44px is a stylesheet. **Everything before the barrier is a DOM read or a click**, and Playwright scrolls an element into view before pressing it, so no assertion above can tell what width the context was laid out at — which is what makes the viewport one boolean rather than a rewrite. The barrier is the last thing in the enhancement's own title to be honoured: Stage 1 is *the handheld contract*, R§1 justifies the whole of §11.12 with a sentence about the phone in a **guest's** hand, and every slice of Stage 1 measured the surfaces **staff** use, because F-59 was found there. Meanwhile this surface acquired headings, descriptions, a photograph, a detail panel, a like and a second control beside a refused card, and not one of them was ever laid out at the width it is read at. **It is measured where it stands rather than navigated to**, which is the one structural difference from scenario 16's ten: those are static-SSR pages and arriving at one is a navigation, while the chosen dish, the open panel and the staged line here are circuit state that a navigation would destroy in order to look at. **Every selector in the guest set is required to match**, so a renamed class or an arrangement that did not build fails by name rather than by a total floor that a group going quiet cannot move — which is the residual scenario 16 recorded and could not close. **And a computed font size is asserted, which is F-118**: §11.12's 16px floor is declared in `app.css` against `.form-field input`, §11.1's basket declares its quantity box as a bare `<input>` inside a `<label class="order-basket-quantity">`, and whether a page put its control inside the arrangement that carries the rule is a fact about markup that no reading of a stylesheet produces. **And one dish carries a photograph** (Stage 1e), which is arrangement rather than a further claim and is what the barrier was measuring the surface without. A dish with a picture renders a *different card* — `.order-menu-item.has-picture .order-menu-choice` is two columns where every other card is one — and its open panel renders the whole frame uncropped under `max-width: 100%`. Six stages of the menu plan built both and nothing had laid either out at 375px, because this scenario put a picture on nothing. **On one dish and not both**, deliberately: a menu where every card is two columns is a menu where the one-column card is untested, and both shapes stand on this surface at once in any dining room. It goes on the dish that is later 86'd, so the card carrying the picture is also the card carrying the unavailable mark with a way-in control beneath it, which is the busiest box model §11.1 can produce. **The fixture is intrinsically wider than the screen and inside §8.2's cap**, and both halves are load-bearing: `.order-menu-detail-picture`'s `max-width: 100%` acts on nothing when the picture already fits, so a small fixture would let that rule be deleted with the barrier still green; and a fixture over the cap would hand the stored dimensions to the browser's downscaler, making the step about a ladder instead of about a layout. The cap is not written in the scenario — §8.2's constraint stays the only place in this repository that says how large a picture may be. **Both pictures are required to have DECODED before anything is measured**, which is a gate rather than tidiness: an `<img>` whose bytes have not arrived has no intrinsic size, so its box is `0×0`, which lies inside every viewport there is and appears in the census as a one — the barrier would report a placeholder reachable and the required-selector refusal would pass. The decoded width is also asserted on the *guest's* page, which is a different claim from the same assertion on §11.4's panel: that route answered for an administrator, this one answers for a table member holding a join grant. **The upload journey is the harness's** (`MenuPictureJourneys`), extracted in the slice that needed it from a second scenario class rather than pasted, on the ruling `TableJourneys.SeatGuestAsync` moved under one slice earlier. **And the guest says what they thought of a dish, which is Stage 6d and rides this arrangement for the reason every claim above it does — the panel is already open on a chosen dish with the like inside it.** A comment is the first content in this application authored by somebody who is not staff, and what only a browser can say is that the sentence *arrives*: a `string` field on a Blazor component satisfies every unit fact and is what *let them type something* produces. Six claims, each refusing an implementation that passes the ones before it. The box is **empty until something is saved**, and its value is asserted rather than its presence. **The body is saved with trailing whitespace and read back trimmed**, which is §7's trimming ruling and the reason for it — two bodies that render identically must compare equal or the no-op rule below cannot fire. **It survives a reload**, so the second reading came from `menu_item_comment_current` rather than from a field. **The other dish's box is empty and the first dish's is not**, so a standing comment is per dish rather than per surface. **Saving the identical body again reports that nothing was written**, which is the no-op rule §7 gives every menu verb, and a verb that appended a second `submitted` row would report a save. **And it is withdrawn and reloaded**, because a write path that only ever appended `submitted` passes every step above — the fold would answer from the last row it wrote. Each verdict is read from the outcome the surface **declares** beside the sentence rather than from the sentence, on **F-113**'s ruling one register up. A final comment is saved so the panel carries both controls when the barrier runs, and **the barrier's own subject set grows in the same slice the controls arrive in** (**F-93**): the box, both comment controls, and — this being the finding — the like beside them, whose 15.2px nothing had ever measured (**F-127**). **And then somebody else reads it, which is Stage 6e and is the only reason any of this was built.** §7 rules a comment staff-facing, and until this step the whole feature was a guest writing to themselves — every claim above is satisfied by a table nobody but its author can query. Six claims, on the like count's arrangement one register up: **before anything is said the index reports nothing** against the dish, so the absence below is an absence rather than a page that never renders a comment at all; **while the sentence stands the index reports it, character for character**, which is where §7's trimming ruling is proved end to end for the second time and for a different reader — a body stored with the trailing whitespace it was typed with reads identically on the guest's own box and unequally here; **the chip beside the dish's name reports one**, because a chip wired to the like dictionary passes every source scan there is; **the other dish reports nothing**, so the read is about a dish rather than about whether this instance has any comments; and **once it is withdrawn the index reports nothing and the chip is gone**, which is the ruling *a withdrawn comment stops being rendered and stays in the log* — a staff read filtering on the event type rather than on the fold's `body IS NOT NULL` still shows the sentence here, and nothing else in this repository can say so. The sentence is read from `data-comment-body` on a row keyed by `data-comment-item`, never from the column heading above it (**F-113**). What is deliberately not asserted is that a *second guest* cannot see the first one's words: this scenario seats one guest, the prohibition is a unit fact over every component in the tree, and a browser adds nothing to it.

**The fixture picture is generated rather than carried**, which is what makes 19 possible at all. `PictureFixtures` writes a real PNG of any size — truecolour, unfiltered, uninterlaced, with the pixels in **stored** deflate blocks so the byte length is `edge × (1 + edge × 3)` and a caller can ask for one comfortably over the cap without guessing. The plan deferred a picture scenario four times on the ground that inventing bytes would be a test arranging what it asserts about; half of that is right — a checked-in photograph would be an opaque blob — and half is not, because nothing downstream asserts anything about these bytes. The image is a smooth gradient rather than noise for a reason that is load-bearing: JPEG's whole design is that smooth content is cheap, and a random raster can survive every rung of the downscaler's ladder and still exceed the cap, which would fail scenario 19 for a reason that has nothing to do with the product.

**16.4 CI:** GitHub Actions — **tree hygiene**, **repository governance**, shell lint, build, unit, integration (service container PostgreSQL), E2E (Playwright/Chromium, all twenty-one §16.3 scenarios), then boot the production image against real PostgreSQL and **take a backup of that instance and drill the restore** (§15) in the same job; publish image on tag. **The runner is Microsoft.Testing.Platform**, selected once for the repository by the `test` stanza in `global.json`, and no test project carries a VSTest adapter (**F-97**): `xunit.v3` 4 pins MTP 2, which removed the VSTest target for the .NET 10 SDK, so `Microsoft.NET.Test.Sdk` and `xunit.runner.visualstudio` are prohibited rather than merely unused. In that mode a solution or a project is named with `--solution` or `--project`, `--logger` does not exist, and an argument for the test application goes after a `--`. The drill is a CI gate rather than a runbook step because a recovery procedure nobody executes is a hypothesis (**F-38**).

CI builds with `-p:ContinuousIntegrationBuild=true`, which flips `TreatWarningsAsErrors` in `Directory.Build.props`; a plain `dotnet build` is deliberately more forgiving, and `scripts/ci_local.sh` is what asks the stricter question on a workstation.

**The contract tests below are the mechanical half of this document.** Each paragraph names one class and states how many assertions it holds, and `TestingSectionContractTests` compares every one of those numbers against the file — so a count here is not a claim, it is a checked fact. A paragraph may describe a class without stating a count, but at least thirty-seven must state one.

`tests/MyRestaurant.WebApplication.Tests/Documentation/SourceCommentContractTests.cs` — 2 assertions: no authored C# or Razor file carries a comment, with a floor on files and on bytes so the walk cannot pass on nothing, and a sensitivity proof against composed fixtures rather than against the tree (**F-120**).

`tests/MyRestaurant.WebApplication.Tests/Documentation/TestingSectionContractTests.cs` — 1 assertions: every assertion count this section states agrees with the file it names, that every class it cites exists, and that at least thirty-seven paragraphs still state a count — the last being the anti-evasion guard, because deleting the number is the cheapest way to make a comparison green (**F-70**).

`tests/MyRestaurant.WebApplication.Tests/Documentation/SpecificationVersionTests.cs` — 2 assertions: every versioned document in `docs/` has a header version matching its newest history entry and a history that descends. The subject is computed, so no filename is pinned (**F-48**, **F-58**).

`tests/MyRestaurant.WebApplication.Tests/Documentation/MarkdownTableContractTests.cs` — 2 assertions: every run of table lines in every tracked Markdown file is a table: a header, a delimiter beneath it, no second delimiter inside it, and every row carrying the column count its header declares (**F-72**).

`tests/MyRestaurant.WebApplication.Tests/Documentation/ContextDumpExclusionContractTests.cs` — 4 assertions: every path `export.sh` holds out of the dump exists and holds something, every withheld document is linked by path from a document the dump contains, the two `GENERATED_DIRECTORIES` arrays agree, and archived history is exempt from the platform-state rule (**F-96**).

`tests/MyRestaurant.WebApplication.Tests/Configuration/ConfigurationSurfaceTests.cs` — 5 assertions: every variable the application reads is a variable §13 documents, `compose.yaml` passes, `.env.example` assigns, and `Validate()` refuses by a name that is actually read. The binding scan ends at a declaration rather than at a comment (**F-50**, **F-119**).

`tests/MyRestaurant.WebApplication.Tests/Security/RateLimitingContractTests.cs` — 6 assertions: rate limiting is opt-in per endpoint, no policy is registered twice, no two surfaces are refused in identical words, and the scan reads code with comments removed rather than prose (**F-116**).

`tests/MyRestaurant.WebApplication.Tests/Security/RawHtmlContractTests.cs` — 2 assertions: raw HTML has a closed set of sources in this application and none of them is a person, over a floor of fifty-odd source files (**F-116**, ADR-0014).

`tests/MyRestaurant.WebApplication.Tests/HarnessSnapshotContractTests.cs` — 2 assertions: every composite the end-to-end harness evaluates a `Func<T, bool>` against is read from the browser in one evaluation, over a subject set computed from the harness sources rather than listed, with a sensitivity proof against composed fixtures — a torn reading, a whole one, and one no predicate is ever asked about (**F-121**).

`tests/MyRestaurant.WebApplication.Tests/SourceCodeTests.cs` — 4 assertions: the comment-removing reader used by every scan that must not read prose behaves the same way on every form this tree contains, proven against composed fixtures (**F-116**).

`tests/MyRestaurant.WebApplication.Tests/Security/ContentSecurityPolicyContractTests.cs` — 10 assertions: the policy admits no inline script and no inline event handler, no off-origin resource reference survives, and exactly one `data:` URL exists in the tree (**F-49**, ADR-0013).

`tests/MyRestaurant.WebApplication.Tests/Components/HandheldLayoutContractTests.cs` — 9 assertions: §11.12's structure is decidable from text and is decided: one breakpoint in the whole tree, no component media query, the shared vocabulary declared once, every custom property a rule reads declared in `:root`, every colour a declared value, no fallback on a declared reference, and `overflow-wrap` written once (**F-63**–**F-69**).

`tests/MyRestaurant.WebApplication.Tests/Components/LiveSurfaceContractTests.cs` — 7 assertions: §11.10 holds against the Razor sources, deriving the interactive set from `[ExcludeFromInteractiveRouting]` rather than from a list (**F-47**).

`tests/MyRestaurant.WebApplication.Tests/Components/EditContextConsumerContractTests.cs` — 2 assertions: no component consumes an `EditContext` it did not create, which is the shape a form that silently stops validating takes.

`tests/MyRestaurant.WebApplication.Tests/Components/RazorDirectiveContractTests.cs` — 2 assertions: the token `@section` appears nowhere in this tree as an identifier, because it is a reserved directive word and two files were named after it (**F-81**).

`tests/MyRestaurant.WebApplication.Tests/Events/MenuEventVocabularyContractTests.cs` — 4 assertions: every named `event_type` vocabulary this document quotes is the one the migrations declare, in both directions, and every picture event type has a sentence on the surface that renders it (**F-105**, **F-111**).

`tests/MyRestaurant.WebApplication.Tests/Menu/MenuItemImageContentTypeContractTests.cs` — 3 assertions: what decides the stored media type is the bytes rather than the declaration, and the vocabulary may only be written where §8.2 declares it (**F-109**, **F-110**).

`tests/MyRestaurant.WebApplication.Tests/Menu/MenuItemImageSurfaceContractTests.cs` — 12 assertions: the picture surface renders an `alt` on every `<img>`, posts through the endpoint rather than a circuit, and names the byte cap the migration declares (**F-108**).

`tests/MyRestaurant.WebApplication.Tests/Menu/MenuItemCommentSurfaceContractTests.cs` — 6 assertions: §11.1's comment box is inside the item's detail panel and never inside its card, the guest surface reads its own standing comment and never the staff-facing read, the body cap is asked for rather than restated, a blank save is a refusal rather than a withdrawal, and the workflow never reaches the comment write (**F-107**). The two reads are told apart by the **receiver** and not by the method name: `ListAsync(` is declared by three of the menu directories and two of them are read by this one component, so a marker without the identifier the page gave the service would forbid reads §11.1 requires — F-67's parenthesis is not enough where the name is shared.

`tests/MyRestaurant.WebApplication.Tests/Menu/MenuItemCommentStaffReadContractTests.cs` — 5 assertions: §11.4's menu index reads the whole menu and never the per-person read, the whole-menu read is reached from no component outside `Pages/Administration/`, the comment row declares its dish and its sentence and the count chip is absent where nobody has spoken, the author and the instant go through §11.7's one clock, and the surface writes no sort verb of its own. The prohibition's subject set is **computed** from every `.razor` under `Components/` rather than listed (**F-47**), over a floor of forty files, and it needs two injectors to have both halves — §11.1 reads its own author's comment and §11.4 reads everybody's, so a tree with one injector is a walk whose permitted set is its whole set.

`tests/MyRestaurant.WebApplication.Tests/Menu/MenuItemReactionSurfaceContractTests.cs` — 7 assertions: §11.1's like control is in the item's detail panel and never on its card, and the guest surface never reads the count (**F-107**).

`tests/MyRestaurant.WebApplication.Tests/Menu/MenuWiringTests.cs` — 30 assertions: the menu's composition root registers every workflow, read and administration service the surfaces resolve.

`tests/MyRestaurant.WebApplication.Tests/Menu/MenuGroupingTests.cs` — 11 assertions: a flat item list becomes headings holding items, in §7's total order, with the identifier tail that makes the order total.

`tests/MyRestaurant.DataAccess.Tests/Menu/MenuDirectoryTests.cs` — 8 assertions: the guest-facing menu read returns only active items under active headings, grouped and ordered as §7 requires.

`tests/MyRestaurant.DataAccess.Tests/Menu/MenuAdministrationTests.cs` — 31 assertions: every item verb writes its own event type, returns `NoChange` for a write that would store the value already there, and appends a new item under a lock on its section row.

`tests/MyRestaurant.DataAccess.Tests/Menu/MenuSectionAdministrationTests.cs` — 20 assertions: every heading verb writes `menu_section_event` in the same transaction as the row it changes, and only activation and deactivation publish `MenuChanged`.

`tests/MyRestaurant.DataAccess.Tests/Menu/MenuItemResequenceTests.cs` — 8 assertions: resequencing refuses a list that is not a permutation of the section's current set and writes one `reordered` event per item that actually moved.

`tests/MyRestaurant.DataAccess.Tests/Menu/MenuSectionResequenceTests.cs` — 8 assertions: the heading-level reordering verb writes to its own event type and refuses a non-permutation, which is why it is a separate verb.

`tests/MyRestaurant.DataAccess.Tests/Menu/MenuEventLogTests.cs` — 9 assertions: the item event log is append-only and every verb's row lands in the same transaction as the change it records (**F-18**).

`tests/MyRestaurant.DataAccess.Tests/Menu/MenuSectionEventLogTests.cs` — 6 assertions: the heading event log is append-only and the section editor's uncapped history read returns it in order.

`tests/MyRestaurant.DataAccess.Tests/Menu/MenuItemImageEventLogTests.cs` — 6 assertions: every picture verb writes its own event type and the log carries the alt-text change as a verb rather than as a field.

`tests/MyRestaurant.DataAccess.Tests/Menu/MenuItemImageTests.cs` — 15 assertions: one picture per item enforced by the primary key, the byte cap enforced by the constraint, and no stored length or dimension beside bytes that already answer for themselves.

`tests/MyRestaurant.DataAccess.Tests/Menu/MenuItemReactionTests.cs` — 9 assertions: a like is an event rather than a row per person, `liked`/`unliked` is idempotent per person per item, and liking requires no prior order.

`tests/MyRestaurant.DataAccess.Tests/Menu/MenuItemCommentTests.cs` — 13 assertions: a comment is an event rather than a row per person, resubmission and withdrawal are both appends, the stored body is trimmed, and each of the four named constraints refuses its own forbidden shape by its own name — including the cap, which is read out of the constraint rather than written down (**F-107**). The four are read out of `pg_constraint` rather than listed, and every probe row is paired with a **control** that differs from it in one attribute and must insert, which is what proves the probe broke one CHECK rather than two (**F-123**).

`tests/MyRestaurant.DataAccess.Tests/Menu/MenuAvailabilityTests.cs` — 7 assertions: a deactivated item stays in history and in every settled order that already names it — deactivation is not deletion (**F-10b**).

`tests/MyRestaurant.Domain.Tests/ImageFormatTests.cs` — 8 assertions: the declared media type is checked against the bytes' own signature by a pure function in `Domain`, so a lying `Content-Type` cannot decide what is stored.

`tests/MyRestaurant.DataAccess.Tests/SchemaMigrationRunnerTests.cs` — 7 assertions: migration is idempotent, `citext` is installed, every relation and column §8.2 declares exists afterwards, and every constraint the newest migration names is present.

`tests/MyRestaurant.Domain.Tests/UuidV7IdentifierFactoryTests.cs` — 7 assertions: identifiers are monotonic within a millisecond, which `Guid.CreateVersion7()` alone does not give, and order is compared big-endian because `Guid.CompareTo` disagrees with PostgreSQL (**F-45**).

`tests/MyRestaurant.WebApplication.Tests/Deployment/ContainerLoggingContractTests.cs` — 1 assertion: every `new PostgreSqlBuilder(` under `tests/` reaches its `.Build()` through a `.WithLogger(`, over a subject set computed from the sources that construct a builder (**F-125**, **F-126**). Testcontainers seeds each builder with its own console logger, so the silencer is a value on that builder's configuration and there is no global setting left to assign, nor any order to assign it in (**F-41**).

`tests/MyRestaurant.WebApplication.Tests/Deployment/ComposeDependencyContractTests.cs` — 3 assertions: every service the canonical stack needs is declared with the dependency ordering that lets it start.

`tests/MyRestaurant.WebApplication.Tests/Deployment/ComposeSubstitutionContractTests.cs` — 3 assertions: every `${NAME:-default}` in `compose.yaml` is a variable the application actually reads, so the host-substitution check has a subject (**F-57**).

`tests/MyRestaurant.WebApplication.Tests/Deployment/ContainerImageReferenceContractTests.cs` — 3 assertions: every image reference in the tree is fully qualified, because a short name resolves against whatever registry list the host carries (**F-51**).

`tests/MyRestaurant.WebApplication.Tests/Deployment/DevInstanceLoopbackContractTests.cs` — 4 assertions: the detached demo helper proves the instance answers before printing a URL, rather than printing container ids (**F-53**).

`tests/MyRestaurant.WebApplication.Tests/Deployment/VulnerabilityAuditParityContractTests.cs` — 2 assertions: the local gate runner and CI ask the audit question in the same words, so a green run locally means the same thing.

`tests/MyRestaurant.WebApplication.Tests/Deployment/TestRunnerContractTests.cs` — 4 assertions: the runner is Microsoft.Testing.Platform, selected once in `global.json`, and no project carries a VSTest adapter (**F-97**).

`tests/MyRestaurant.WebApplication.Tests/BuildInformationTests.cs` — 11 assertions: the assembly reports the version and revision it was built with, and a build told neither says so rather than inventing one (**F-39**).

`tests/MyRestaurant.WebApplication.Tests/Time/RestaurantTimeTests.cs` — 27 assertions: every instant renders in `RESTAURANT_TIME_ZONE` through one type, in the format `RESTAURANT_CLOCK_FORMAT` selects (**F-36**).

`tests/MyRestaurant.WebApplication.Tests/Identity/WebAuthnOriginPolicyTests.cs` — 8 assertions: the relying-party id is derived per request from the public origin, and a trusted-origin pattern admits a quick tunnel without admitting the Public Suffix List (**F-06a**).

`tests/MyRestaurant.WebApplication.Tests/RestaurantOptionsTests.cs` — 33 assertions: every configured variable has a default that starts, a validation that refuses, and a refusal message naming a variable the binder reads.

## 17. Security posture and accepted risks

Threats mitigated: **content injection and clickjacking, bounded by a Content Security Policy on every response** (§11.11, ADR-0013) — `script-src 'self'` with no hash, nonce or `unsafe-*`, so the raw-HTML sites are a **closed set of values this application computed** — held by `RawHtmlContractTests` rather than counted here (**F-116**, Stage 6b) — and an injection that got past Razor's escaping would still be inert; `frame-ancestors 'none'` on every response rather than the framework's `'self'` on component endpoints; `form-action 'self'`, which is the one thing antiforgery does not cover, since a token protects against a forged request and says nothing about where a real form posts to; and `Referrer-Policy: same-origin`, because §4.3's join token rides in a query string and a browser default is not a deployment guarantee; static-QR capability theft (rotating tokens, ≤120 s life, per-table secret rotation); Argon2 memory DoS (semaphore + rate limit + lockout); display theft (revocation; device holds no secret worth extracting; join secret never leaves the server); credential stuffing (Argon2id, lockout, passkeys-first); stale sessions after admin action (5-minute stamp revalidation); half-applied schema (fail-fast migrations); pairing brute force (hashed single-use codes, TTL, 5/min/IP).

Accepted, by ruling or by design: token replay within ≤120 s (bounded by membership/visibility rules); WAN dependence of in-house ordering (hairpin — F-06); quick-tunnel passkeys work per run but the per-run URL is not persistent (PSL — re-register each demo; named tunnel for durability); counter role may operate password-only (no passkey mandate); guest sees table-mates' display names and orders (that's the product); no rate limit on authenticated order sends beyond all-or-nothing validation (single-restaurant trust model).

**`/register` is rate-limited as of v1.47 (§11.8, §13) — F-37's *no rate limit* ruling is discharged, and F-115 is the mechanism that made discharging it possible.** F-37's *reasoning* survives intact and is now the argument for the budget's shape rather than for its absence: registration is a **two-request** flow behind an antiforgery token and a protected ticket cookie, so it was never a scriptable single POST; the password is capped at 256 characters, so an anonymous caller cannot ask for unbounded Argon2id work; §3.2's semaphore bounds concurrent hashes process-wide; and a spam account holds no capability — a guest is the absence of a grant (§3.7), so the worst outcome is rows, not access. Those four are why the limit is a **volume ceiling sized for a dining room** rather than a brute-force defence sized against an attacker: there is nothing here to brute-force, and the partition is a client address that a whole venue shares.

**What the eleven slices are the finding, and they are worth more than the code (F-115).** The paragraph below stated the ruling, the concrete reason it was not a two-line change, *and* the fix — and it was correct in all three from the day it was written. It then sat in an accepted-risks section for eleven slices while nothing in this repository could tell whether any of it had been acted on. **A wall that is documented is not a wall that is closed**, and the ledger had no way to distinguish the two: an accepted risk and an unstarted repair read identically. The habit that follows is small and is stated in §18 rather than left here — an accepted risk whose own paragraph names its remedy is a **deferral**, and a deferral belongs where deferrals are tracked.

**The mechanism, kept because it is the reason a second policy is still not a two-line addition anywhere else.** `RateLimiterOptions.OnRejected` and `RejectionStatusCode` are properties of the *limiter*, not of a policy. A second `AddRateLimiter` call adding a registration policy silently takes over the rejection handler, and a refused registration would then answer with §4.2's *"Too many pairing attempts from this device"* — worse than no limit, because it is wrong and looks deliberate. Doing it properly means `OnRejected` dispatching on the endpoint, which is what `RateLimitedSurfaces` now does: **one** `AddRateLimiter`, owned by `Security/RateLimitingServiceCollectionExtensions.cs` rather than by a surface's own extension method, walking one list in which a policy cannot be represented without a refusal sentence of its own. The dispatch reads `EnableRateLimitingAttribute` out of the endpoint's metadata — *the same read the middleware performed one instant earlier to select the policy* — so a lookup that failed would describe a request that could not have been refused; the fallback sentence is therefore about honesty rather than coverage, and its property is that an unidentifiable policy produces a **vague** answer and never a wrong one. Asserted by `RateLimitingContractTests` (§16.4) rather than remembered.

**Coordinated disclosure, and this section's part in it (F-42).** The project must offer a **private** channel for security reports, named in `SECURITY.md`, and that channel is the single exception to §18's no-outside-contributions rule. The asymmetry is the argument: refusing a feature costs the person who wanted it, and the AGPL has already given them the source and the freedom to build it; refusing a report costs an operator's *guests*, who never chose this software, cannot read `CONTRIBUTING.md`, and have no fork to run. **This section is part of the offer rather than a disclaimer.** Every ruling above was argued and written down, so `SECURITY.md` must send a reporter here first — a report that asks for one of them to be *re-ruled* is an argument and gets read as one; a report that presents one as news gets pointed back at the paragraph that decided it. What the project owes in return is stated as targets rather than as an SLA a single maintainer would miss, and the advisory is published when there is a release to upgrade to rather than held open-endedly. A fork operator is the security contact for their own instance, and `SECURITY.md` says so, because nothing here can reach their box, their data or their guests.

## 18. Governance

**This document is the contract; `docs/REQUIREMENTS.md` is the requirement it implements.** Where they disagree, the disagreement is a finding and gets a row in `docs/DOCUMENTATION_REVIEW.md`. Where this document and an ADR describe one decision, they agree by construction: the ADR carries the rationale, this document carries the mechanism.

**Atomic documentation.** A behaviour change ships in the same slice as its specification edit, its ledger row, its plan update, its `BUILD_PROGRESS.md` narrative, and its `_CHANGES.md`. There is no follow-up slice for documentation, because a follow-up slice is a promise and the register is full of what promises became.

**A count in prose is a second copy of a fact.** Prose stating a number no gate can check is **deleted rather than corrected** (F-77, F-89, F-112). Where a rule can be executed against the tree, no hardcoded list of its subjects exists (F-47, F-58) — a list of one is still a list.

**No comment in authored source.** `.cs`, `.razor`, `.sql`, `.css`, `.js` and `.sh` carry no comments, with two exceptions that are not commentary: a shebang, and the contiguous header block of a script whose `--help` prints it. The reasoning that a comment would have carried belongs in this document, in the findings register, or in the commit that made the change — all three of which something can check or a reader can date, and a comment is neither (F-120). `SourceCommentContractTests` holds the rule for C# and Razor.

**An accepted risk that names its own remedy is a deferral.** It belongs where deferrals are tracked — a stage in `docs/MENU_AND_HANDHELD_PLAN.md` or a row in `docs/DOCUMENTATION_REVIEW.md` — and not in §17, where an unstarted repair and a considered acceptance read identically (**F-115**). When the remedy lands, the paragraph that promised it is **deleted rather than left beneath the account of the landing**, because two accounts of one mechanism read as diligence and the reader cannot date either (**F-114**, **F-122**).

**History is archived rather than deleted.** The long form of every document that has one is under `docs/progress/`, withheld from the context dump by `export.sh` and still tracked, still hygiene-checked, and still linked by path from a document the dump contains — asserted by `ContextDumpExclusionContractTests`, because a session working from the dump cannot see these files and has no way to learn they exist.

**Every gate is proven able to fail** before it ships, against a planted defect (F-41). A gate whose subject ceases to exist is **deleted rather than weakened**: a floor over a set that can no longer be non-empty is the vacuous gate this rule exists to forbid.

**A sensitivity proof is a record of an execution, and a slice that could not execute says so instead of describing runs.** A planted-defect proof presupposes a green baseline, so a slice narrating four mutations of a file it never ran is narrating four runs of a red suite. Where the authoring environment cannot execute the assertion, the paragraph states which defect the assertion is *reasoned* to catch and says it was not run — the two are different claims and only one of them is evidence (**F-124**).

**An emulated proof establishes what an assertion decides; it does not establish that the source it scans compiles.** A tree-scan gate keys on a string, the code it scans is a second copy of that string, and nothing in this tree holds the two together — only the compiler does. A marker naming a member the pinned package does not have therefore passes the emulation and fails the build. `ContainerLoggingContractTests` named `TestcontainersSettings.Logger`, removed from Testcontainers before the version `Directory.Packages.props` pins; the two fixtures that assigned it did not compile, and an emulation over file text could not have said so (**F-126**). Where a slice cannot compile, the API surface of every new marker is read **at the pinned version**, and the paragraph says which version was read.

**An assertion may not depend on which of several simultaneous failures a tool reports first.** A row breaking two CHECKs is named by whichever constraint sorts first, because PostgreSQL evaluates them in name order; the assertion then decides a fact about a sort rather than about the schema. Construct the subject so exactly one thing about it is wrong, and prove that by a **control** differing in one attribute that must succeed (**F-123**).

**No file in this tree asserts a platform setting it cannot check** (F-42, F-46). A sentence about a checkbox on a settings page was false for as long as it was written down, and nothing in the tree could have known, because a grep cannot see a checkbox. State the policy instead: "issues are not triaged" says what the project will do, is true wherever it is read, and survives somebody toggling the tab. `scripts/check_repository.sh` holds the rule, and the files whose job is to record what this tree used to say are exempt by name.

## 19. Build order (milestones)

- **M1 — skeleton:** solution layout (§2), Containerfile, compose dev profile, DbUp with `0001_initial_schema.sql`, health endpoints, OTel wiring, `run.sh`.
- **M2 — identity:** Dapper Identity stores, Argon2id hasher (+floor guard, semaphore), passkeys, TOTP + recovery codes, lockout, obligations pipeline, `/setup` bootstrap, roles/policies, security events, admin user management + reset, and the person's own profile page (§11.6: display name, contact details, voluntary password change). The profile page belongs to this milestone but was not listed here originally and landed after M3 — see F-35.
- **M3 — tables & joining:** table CRUD + join secrets + rotation, display pairing + device auth + `/display`, token generate/validate + metrics, grant cookie, join flow, sittings + membership.
- **M4 — ordering:** living order + locking protocol, staging UI, batch send + validation, staff edits, fulfillment/reversal, projections + fold + equivalence tests, kitchen surface + alerts + reminder service.
- **M4 close-out — restaurant time:** the §8.1 rendering rule actually honoured on every surface (`RestaurantTime`, replacing eighteen `ToLocalTime()` call sites), the §13 clock-format decision, and the §11.7 footer clock. Scheduled here rather than inside a feature slice because a half-applied time convention is worse than a uniformly wrong one, and ahead of M5 because a wrong time on a settled bill is a different order of problem from a wrong time on a roster — see F-36.
- **M5 — counter & administration:** bills, price adjustment, close & settle, end-of-day, counter fallback QR, menu management + events, event explorer, hide/unhide, post-close corrections.
- **M6 — hardening & production:** full E2E suite (§16.3), backups + restore drill, cloudflared production profile + tunnel docs, quick-tunnel demo script with warning, OPERATIONS runbooks, CI pipeline. The **guest registration surface** (§11.8) also lands here rather than in M2, where it belonged: R§4.3 required it from rev 2 and no milestone claimed it, and the gap surfaced only when §16.3 scenario 3 went to write it — see F-37.
- **M7 — the menu, and the screen it is read on:** the first work driven by a user rather than by this document. Stage 1 is the §11.12 handheld contract — its vocabulary and the four index pages in Slice 30, its 375px end-to-end barrier in Slice 32, the remaining surfaces still open — and it lands *first* even though the menu was asked for first: the menu work adds four surfaces that are all read from a phone, and writing them before the responsive vocabulary exists means writing them against the shape F-59 was found in and then touching all four again. Stage 2 is ADR-0014's schema — sections, descriptions and explicit ordering — Stage 3 is the surfaces that read it, and Stages 4 to 6 are images, likes and comments. Stage 6 was recorded as *not startable* until §17's rate-limit ruling was revisited; it was revisited in Slice 62 and the two prerequisites that followed from it are discharged, so the stage is open work rather than blocked work (**F-122**). `docs/MENU_AND_HANDHELD_PLAN.md` is the plan and is struck through as it lands.
- **M6 close-out — the release:** the build stamp and the source offer (§11.9), because both are things a first tag makes true forever and neither is worth adding *after* the version people cite. Publishing images for other people to run is what turns "which build is this?" from a question the operator can answer from memory into one the instance must answer itself, and what makes `CONTRIBUTING.md`'s promise that a fork "owes its users the same" into something a fork can actually discharge — see F-39. Then `scripts/ci_local.sh --with-all`, a drill against the real stack, and the tag.

---

## Appendix A — Decisions register (ruling → embodiment)

One row per ruling: what was decided, and where in this document or which ADR carries it. What was *wrong* is `docs/DOCUMENTATION_REVIEW.md`; why it was decided that way is the ADR or the commit. The long form of this register, with the full account beside every row, is in [`docs/progress/TECHNICAL_SPECIFICATION_THROUGH_V1_50.md`](progress/TECHNICAL_SPECIFICATION_THROUGH_V1_50.md).

### Rulings F-06 through F-52

| Ruling | Decision | Embodied in |
|---|---|---|
| F-06 | Named Cloudflare tunnel = production origin (persistence, not a passkey prerequisite); Argon2id "robust" params; hairpin risk accepted; GoTunnels… | §3.2, §3.3, §14, §17 · ADR-0005, ADR-0008 |
| F-06a | Quick tunnels support passkeys via per-request RP derivation (`ServerDomain=null` + `PublicOriginMiddleware` + `ValidateOrigin` against… | §3.3, §14.3 · ADR-0005 |
| F-07 / Q1 | Living order per guest per sitting; client staging; batch sends; one alert per send; pending → fulfilled lifecycle; guests remove own pending only… | §6, §10, §11.1 · ADR-0007, ADR-0002 |
| F-08 / Q2 / Q3 (supersedes F-09 draft) | TOTP on password path only; no per-user toggle; reset wipes password+TOTP (if enrolled) and forces change + re-enrollment via obligations pipeline on any… | §3.4, §3.5, §3.7 · ADR-0010 |
| F-12 / Q4 / Q5 | Rotating HMAC join tokens; `table_display` device principal; pairing codes; join grants; counter fallback QR; printed QR removed | §4, §11.5 · ADR-0009 |
| F-10 / F-10b / F-11 | Post-close admin corrective events beside immutable settled total; deactivate-not-delete; guest as actor_role not stored role | §5.3, §6.7, §3.7, §8.2 |
| F-13 / F-14 / F-15 | No Redis v1 (broadcaster interface); compose canonical / Aspire optional; OTLP-generic (`UPTRACE_DSN` translated in run.sh only) | §9, §12, §14 · ADR-0006, ADR-0004 |
| F-16 / F-17 | Backups pg_dump -Fc + retention + keys volume; run.sh defined | §15, §14.4 |
| F-36 | All instants rendered in `RESTAURANT_TIME_ZONE` for every reader, through one type; `RESTAURANT_CLOCK_FORMAT` settles the 12-vs-24 question; ticking… | §8.1, §11.7, §13, §19 |
| F-18 / F-19 | Menu item event log; lockout 5/5min, username 3–64 citext, currency/timezone defaults | §7, §3.1, §13, §8.2 |
| F-20 | Hand-written fakes; NSubstitute ok; no Moq | §16.1 |
| F-37 | Guest self-registration is a real surface at `/register`, specified rather than assumed; a passkey is offered first and may be declined only when a… | §11.1, §11.8, §17, §19 · R§4.3 |
| F-38 | Both scripts rewritten; `scripts/restore_drill.sh` added and executed by CI on every push, so the drill is rehearsed rather than documented | §15, §16.4 · O§6, O§8, O§14 |
| F-39 | Version and source revision stamped through the `Containerfile` into `AssemblyInformationalVersionAttribute`, read by `BuildInformation`, reported at… | §11.9, §12, §13, §16.4, §19 · R§8 |
| F-40 | `scripts/check_tree.sh` added and run as the first gate both in CI (`tree` job) and locally; five properties asserted, four of them blocking with no… | §16.4 · O§14 |
| F-41 | Scope is now decided in one place (`is_authored_text`) that gates 1, 2 and 3 all consult, so they cannot disagree about a file: generated text excluded by… | §16.4 |
| F-42 | `SECURITY.md` at the root: the private channel, no bounty said first rather than discovered afterwards, scope in both directions, single-maintainer… | R§8 (rev 4), R§10 · S§16.4, S§17, S§18 |
| F-43 | `CONTAINER_ENGINE` honoured by both scripts and pinned to `docker` on the two `boot-smoke` steps; when a container is named, the engine is chosen by… | §15, §16.4 · O§6 |
| F-44 | `CounterBoard.razor` publishes `data-live` (from `RendererInfo.IsInteractive`) and `data-loaded` (from `_loaded`), and the selector demands **both**… | §11.3, §16.3 |
| F-45 | `.dockerignore` as an **allow-list** (§14.1a) — a deny-list would have to be extended for tomorrow's secret by somebody who remembered to, and it is a… | §14.1a, §15 · O§12, O§14 |
| F-46 | The forbidden list gains the package-settings group and, more to the point, is maintained as part of the rule rather than as an afterthought (§16.4) | §16.4, §18 · O§14 |
| F-47 | §11.10 states the contract, names the routing rule as its subject, and defines `data-loaded` as *the surface has what it renders itself for* rather than… | §11.10 (new), §16.4 |
| F-48 | The header is the version the changelog says it is, and `SpecificationVersionTests` asserts it on every `dotnet test` — two assertions… | §16.4 |
| F-49 | §11.11 is normative: three headers on **every** response from one middleware placed after `PublicOriginMiddleware` (so the policy can name the normalized… | §11.11 (new), §16.4, §17 · O§14 |
| F-50 | §13 states the transport rule: a key in that table must appear in the `web` service's `environment` mapping and in `.env.example`, and a variable whose… | §13, §16.4 · O§15 |
| F-51 | §14.1 states it normatively: every image reference in `compose.yaml` is fully qualified, and the reason it is a correctness requirement rather than a… | S§14.1 · O§1, O§10 |
| F-52 | New §14.3a and `scripts/dev_instance.sh`, which exits and leaves the instance running | S§14.3, S§14.3a · O§10 |
| F-53 | Two things, and the first is the fix | S§14.1, S§14.3a, S§16.4 · O§2, O§10a |
| F-54 | **The document is wrong and the scripts are right — the clause of F-16's ruling is reversed rather than implemented.** Materialising `.env.example` would… | O§2, O§10a |
| F-55 | Four rules in §14.3a, one of which is a new command | S§14.3a · O§10a |
| F-56 | `TUNNEL_TARGET` defaults to `http://127.0.0.1:8080` in both helpers, with the reason written beside it, and §14.3a states the rule generally: what a… | S§14.3a, S§16.4 |
| F-57 | Verified, not assumed, in three places, and the remediation made complete | S§14.1, S§16.4 · O§2, O§10a |
| F-58 | The subject is **computed** rather than named (F-47's habit): every Markdown file in `docs/` with both a header version and a history section is checked… | §16.4 |
| F-59 | New **§11.12**, normative for every surface: handheld-first, exactly one `min-width: 48rem` breakpoint, 2.75rem touch targets, a 16px input floor because… | R§8 (rev 6) · S§11.12 (new), §16.4, §19 |
| F-60 | §14.1 states the rule for **every** image reference rather than for one file, requires each to sit in a position that can be read, and requires one image… | S§14.1, S§16.4 |
| F-61 | The handler carries a first-entry guard and disarms the remaining traps, so it runs once whichever of the three arrives, rather than being made correct by… | S§14.3 |
| F-62 | §16.4 states the barrier rather than the gap, and states the three things about it that are rulings: the viewport is asserted before anything else and… | S§11.12, S§16.4 |
| F-63 | The fact is renamed to say what it is about — `TheTreeIsWrittenHandheldFirstThroughExactlyOneBreakpoint` — and walks every component `<style>` block as… | S§11.12, S§16.4 |
| F-64 | Every reference names the declared property the rule always wanted: `--rule` → `--hairline`, `--muted-foreground` → `--ink-soft`, `--surface-sunken` →… | S§11.12, S§16.4 |
| F-65 | Both rules read `var(--touch-target)`, and the comment states what happened rather than being deleted, because a comment that asserted its own compliance… | S§11.12, S§16.4 |

### Rulings F-53 through F-92

| Ruling | Decision | Embodied in |
|---|---|---|
| F-66 | One `.manage-*` vocabulary in `app.css`, handheld-first as a column with the controls inheriting `--touch-target` and the 16px floor rather than restating… | S§11.12, S§16.3, S§16.4 |
| F-67 | The scan reads the **simple selectors a block declares**: comments stripped, preludes taken as the text after each rule's closing brace, at-rule preludes… | S§11.12, S§16.4 |
| F-68 | `:root` gains ten properties for the values that had none — `--accent-ink`, `--accent-surface`, `--accent-surface-soft`, `--accent-hairline`… | S§11.12, S§16.4 |
| F-69 | The *should* is finished rather than the number corrected: all fifty fallbacks are removed, §11.12 states the rule as a **must**, and the number is… | S§11.12, S§16.4 |
| F-70 | The gate is **adopted, not deleted**: §11.12 states the rule and why `anywhere` rather than `break-word` (only `anywhere` collapses min-content, which is… | S§11.12, S§16.4, S§18 |
| F-71 | The three sites drop the third argument, which is behaviour-identical — `IndexOf(char, StringComparison.Ordinal)` delegates to `IndexOf(char)` in the… | S§18 |
| F-72 | Appendix A moves to the four columns the ledger has always used — `Ruling / finding`, `What happened`, `Decision`, `Embodied in` — because the newer rows… | S§16.4 |
| F-73 | Both numbers move to **ten**, which is what this slice's own §16.4 paragraph makes true, and the summary now records that it said eight when nine was the… | S§16.4 |
| F-74 | One `<PackageVersion Include="SSH.NET" Version="2026.0.0" />` in `Directory.Packages.props` | §16.4 |
| F-75 | The audit becomes gate 7 of `scripts/ci_local.sh`, running the identical command on identical terms — `continue-on-error: true` in the workflow, `\\|\\|… | §16.4 |
| F-76 | The check earns the word: a third half counts the newlines in the **last two bytes** with the same `tail -c … \\| wc -l` trick the second half already… | §16.4 |
| F-77 | The counts are **deleted rather than corrected**, and that is the ruling | S§7, S§8.2, S§16.4 |
| F-78 | One builder call: `SchemaMigrationRunner` adds `.WithVariablesDisabled()`, verified present in dbup-core's public API at tag `6.1.1` rather than recalled | S§7, S§16.4 |
| F-79 | The duplicate is deleted | S§7 |
| F-80 | The list is corrected and, more to the point, **derived rather than maintained** (F-47): `MenuEventVocabularyContractTests` reads the migrations, takes… | S§7, S§16.4 |
| F-81 | Both variables become `menuSection`, and the rule is made executable rather than remembered (F-47) | S§16.4 |
| F-82 | The four counts are corrected and the floor moves from sixteen to eighteen with the census, which is F-73's habit on its third application | S§16.4, S§18 |
| F-83 | The rename, in the test and in all three documents | S§16.4 |
| F-84 | Ten arguments, and the comment rewritten to say what the stand-in is doing: every member `OrderStaging` does not read is at its least interesting value… | S§16.4 |
| F-85 | `"moe"`, at both the arrangement and the assertion that reads the fallback back | S§16.4 |
| F-86 | The statement lists all five columns and the method takes the three new payloads as **optional trailing parameters**, so the five existing call sites keep… | S§16.4 |
| F-87 | The four counts move, and the two payload reads move to `CreatedEventScalarAsync`, which names the type instead of trusting recency and which is the… | S§16.4 |
| F-88 | `TextContentAsync`, which returns the DOM's text and is therefore the stored name | S§16.3, S§16.4 |
| F-89 | **The ruling is reversed, which is rare enough to state plainly.** F-77's habit wins over F-73's: both prose copies are **deleted** rather than corrected… | S§16.4 |
| F-90 | The number is **deleted rather than corrected**, on F-77's ruling: the rows are derivable from the tree, this is the second time this sentence has been… | S§16.4, S§18 |
| F-91 | The itemisation is **deleted** and replaced by the rule for what is counted, on F-77's ruling and F-90's in the same slice | S§16.4 |
| F-92 | The parenthetical is **deleted**, which is F-77's ruling on its third application in one slice: the sibling document states its own revision in its own… | S§0 |
| F-93 | Both selectors join the reach set with the surface, and the membership **rule** is written into §16.4 rather than left in the harness: a surface that… | S§16.4 |
| F-94 | The page reads `IMenuSectionDirectory.ListAsync` as well, so the count is of headings and the empty ones are on the screen | S§7, S§11.4 |
| F-95 | The ordering becomes a **contract of `IIdentifierFactory`** rather than a hoped-for property of the format, and `UuidV7IdentifierFactory` keeps it with a… | §8.1, §16.4 · ADR-0011 |
| F-96 | The log splits at `# M6 Slice 40` into a retained half and `docs/progress/`, byte-exact and unedited; `export.sh` gains **three named kinds** of held-out… | S§2, S§16.4, S§18 |
| F-97 | The VSTest half is **deleted rather than pinned back**, in all four projects and in the version list, and the mode is opted into once in `global.json`… | S§16, S§16.4 |
| F-98 | **The claim is described rather than quoted**, which is the only repair available | S§16.4 |
| F-99 | The count becomes 3 and the **arithmetic is written into the fact's own summary** rather than left to be re-derived: a rotation of three moves three, a… | S§16.4 |
| F-100 | The walk becomes `MenuGrouping`, the fourth member of that set, with `MenuHeadingGroup` replacing the component-private record | S§11.2, §16.4 |
| F-101 | All three columns are **dropped before `0006` was written**, and each reason is recorded in §7 and in the script rather than left as a silence | S§7, §8.2, §16.4 · ADR-0015 |
| Menu enhancement, Stage 4a images in the schema (enhancement, not a finding) | **ADR-0015** rules the storage: `bytea` in PostgreSQL rather than a volume, because §15 *defines* a recovery set as two artefacts and `restore_drill.sh`… | R§6.8 · S§7, §8.2, §16.4 |
| Menu enhancement, Stage 3 heading descriptions on the guest menu (enhancement, not a finding) | **The record is widened**, which reverses that sentence, and the argument is correctness rather than cost: two reads happen at two instants, so a heading… | S§7, S§11.1, S§16.3, S§16.4 |
| Menu enhancement, Stage 4b images: the route and the administrator's form (enhancement, not a finding) | `GET /menu/image/{menu_item_image_identifier}` — anonymous, 404 for a picture since replaced or removed, `Cache-Control: immutable` **true** because the… | S§7, S§11.4, S§11.11, S§16.4 · ADR-0015 |
| F-102 | **Deleted rather than corrected**, on F-77's standing ruling: the members are the census and the summary now says so | S§7 |

### Rulings F-93 onward

| Ruling | Decision | Embodied in |
|---|---|---|
| F-104 | The statement loses its transition | §16.4, §18 |
| F-105 | The list is **corrected rather than deleted**, on F-77's cheaper direction as for F-100 and F-103 — it is a useful list and the item log's own is blessed… | §7, §16.4 |
| F-106 | The tag moves inside the form, where the page's other five editors have always had theirs | §7, §16.3, §16.4 |
| F-107 | The number travels rather than being copied: `IMenuItemImageDirectory.ReadDeclaredByteCapAsync` asks `pg_get_constraintdef` for the bound — the read… | §7, §16.4 |
| F-108 | The declaration is hoisted above the loop, which is behaviour-identical because every pass overwrites all four bytes before reading any of them | S§18 |
| F-109 | The surface asks `ImageFormat.IdentifyContentType` what the bytes are and passes that; unidentifiable bytes become the empty string, which is in no… | S§7, S§16.3, S§16.4 |
| F-110 | Both render `MenuItemImageUpload.RecognisedTypesForOperators`, derived from the same census the `accept` hint is | S§7, S§16.4 |
| Menu enhancement, Stage 4d images: the picture's history (enhancement, not a finding) | `IMenuItemImageEventLog` and its Dapper reader, on `IMenuSectionEventLog`'s shape and with **one deliberate divergence that is the whole design of the… | §7, §11.4, §16.4 · ADR-0015 |
| F-103 | The column is **kept for the narrower and true reason** — a picture on a guest's card may carry something its name does not — and the sentence is… | S§7, S§11.1, S§16.4 |
| Menu enhancement, Stage 4c images: the guest's menu (enhancement, not a finding) | A **4rem square thumbnail beside the name** rather than a hero above it, cropped under `object-fit: cover` because nothing in this stack can resize an… | S§7, S§8.2, S§11.1, S§11.4, S§16.4 · ADR-0015 |
| Menu enhancement, Stage 3b item resequencing verb (enhancement, not a finding) | `ResequenceMenuItemsAsync(menuSectionIdentifier, orderedMenuItemIdentifiers, …)` assigns `0…n-1` within one heading, writes one `reordered` event per item… | S§7, S§11.4, S§16.4 |
| Menu enhancement, Stage 3a resequencing verb (enhancement, not a finding) | `ResequenceMenuSectionsAsync` takes the **whole ordering** and assigns `0…n-1` from it, locks every row `FOR UPDATE` ordered by identifier, writes one… | S§7, S§16.4 |
| Menu enhancement, Stage 3 sections-first index (enhancement, not a finding) | The index becomes a group per heading — a `<details>` rendered open, its summary carrying the name, §7's visibility chip, the item count and the position… | §7, §11.4, §16.3, §16.4 · ADR-0014 |
| Menu enhancement, Stage 3 section editor (enhancement, not a finding) | `/administration/menu/sections/{id}` — static SSR, four forms, post/redirect/get with a one-word outcome, the heading's items, and its **complete… | §7, §11.4, §16.3, §16.4 · ADR-0014 |
| Menu enhancement, Stage 3 first surface (enhancement, not a finding) | The picker becomes a card per item: name, price, description, an availability chip, a `disabled` control where §7 says so, and a detail panel that names… | §7, §11.1, §16.4 · ADR-0014 |
| Menu sections and descriptions (enhancement, not a finding) | ADR-0014 rules the schema — `menu_section` as a table, `menu_item.menu_section_identifier` NOT NULL, `citext UNIQUE` on a section name where an item name… | §7, §8.2, §19 · ADR-0014 |
| Menu enhancement, Stage 5c likes: a dish that is off tonight (enhancement, not a finding) | **A path, not a looser refusal.** The card stays `disabled` — it is the *staging* control and §7's rule is about staging — and a **sibling** of it inside… | §7, §11.1, §16.3, §16.4 |
| Menu enhancement, Stage 5b-ii likes: §11.4's count (enhancement, not a finding) | A **neutral chip beside the dish's name**, and **no chip where nobody has pressed it** | §7, §11.4, §16.3, §16.4 |
| Menu enhancement, Stage 5b likes: the guest's control (enhancement, not a finding) | §11.1's detail panel gains the guest's like | §7, §11.1, §16.3, §16.4 |
| F-114 | All eight repaired: an orphan moves onto the member it describes, and where two summaries describe the *same* member the superseded one is **deleted**… | §16.4 |
| F-115 | **One `AddRateLimiter`**, owned by `Security/RateLimitingServiceCollectionExtensions.cs` rather than by a surface, walking `RateLimitedSurfaces.All` — a… | S§4.2, S§11.8, S§13, S§16.4, S§17, S§18 |
| Stage 6 prerequisite — `/register`'s rate limit (enhancement, not a finding) | The limit lands on `/register` because that is the surface that needed it on its own merits and the one whose absence was already a recorded risk… | S§11.8, S§17 |
| F-116 | The pattern is left alone and the **input** changes: `SourceCode.WithoutComments` removes comments, and both this gate and Stage 6b's new one scan through… | S§16.4, S§18 |
| F-117 | The count is **deleted rather than corrected**, which is F-89's ruling and the standing pattern for a census that can be derived — the rhetorical point… | S§16.4 |
| F-118 | **The repair is a selector, not a rule.** `.order-basket-quantity input` joins the `.form-field` list rather than taking a copy of the nine declarations… | R§1 · S§11.12, §16.3 scenario 21, §16.4 |
| Stage 6b prerequisite — raw HTML has a closed set of sources (enhancement, not a finding) | `RawHtmlContractTests` holds the set — six entries, each a QR-code SVG built from a rotating join token, a pairing code or a TOTP enrolment URI — and… | S§11.11, S§16.4, S§17 · ADR-0014 |
| Menu resequencing, Stage 3d (enhancement, not a finding) | Scenario 17 is extended rather than a scenario 22 added, on Slice 59's and 60's reason: it already holds three headings, a two-item heading and a guest… | §16.3 scenario 17 · §16.4 |
| F-113 | The term goes through `ScreenText.DeclaredAsync`, which is the helper that already exists for this and that `ReadTotalsAsync` already uses — one decision… | §16.3, §18 |
| F-112 | The narrative is **deleted rather than corrected**, which is F-89's own remedy applied to the copy F-89 created, and the paragraph now says where the… | §16.4 |
| F-111 | The counts are **deleted rather than corrected**, on F-77's ruling and this project's now-standing pattern — the DDL above is the enumeration and the note… | §7, §8.2, §16.4 |
| Menu likes, Stage 5a (enhancement, not a finding) | Both are ruled and recorded in §7 | §7, §8.2, §8.3, §16.4 |
| Menu Stage 1e (enhancement, not a finding) | The journey is extracted rather than pasted (`MenuPictureJourneys`), on the ruling `TableJourneys.SeatGuestAsync` moved under one slice earlier, and the… | R§1 · S§11.12, §16.3 scenario 21, §16.4 |
| Menu Stage 1d (enhancement, not a finding) | §16.3 scenario 21's guest is seated in a handheld context and the scenario closes with the barrier | R§1 · S§11.12, §16.3, §16.4 |
| Menu comments, Stage 6c (enhancement, not a finding) | Six rulings in §7, and the two that make the rest small are *filed against the item, never an order line* and *staff-facing, a guest sees only their own* — the second of which is what removes the moderation question rather than answering it | §7, §8.2, §8.3, §16.4 |
| F-122 | The pre-fix paragraph is **deleted rather than left beneath the account of the landing** (F-114), §19's *not startable* clause is rewritten to what is true, and §18 gains the deferral habit §17 had been pointing at | §17, §18, §19 |
| F-123 | Every probe row breaks exactly one CHECK, and a **control** row differing in one attribute proves it; the four constraint names are read out of `pg_constraint` rather than listed, so a fifth cannot silently shadow a probe. §18 gains the general rule | §16.4, §18 |
| F-124 | Slice 68's sensitivity paragraph is **corrected in place** on F-114's ruling, and §18 gains the distinction it was missing: a proof is an execution, and a slice that could not execute says *reasoned, not run* | §18 · BUILD_PROGRESS M6 Slice 68 |
| F-125 | Both fixtures install `WithLogger(NullLogger.Instance)` on the builder chain that reaches `Build`, inside the `try` that already turns a container failure into a skip; `ContainerLoggingContractTests` holds it over a computed subject set | §16.4 |
| Menu comments, Stage 6d (enhancement, not a finding) | §11.1's detail panel gains the guest's comment box beside the like. Five rulings in §7, and the load-bearing two are *a blank body is a refusal and never a withdrawal* and *the client's cap is an optimisation, every refusal the server's* | §7, §11.1, §16.3 scenario 21, §16.4 |
| F-127 | The like's declaration goes to `1rem`, the comment controls are written at `1rem`, and §11.1's barrier gains three button and textarea subjects — so §11.12's 16px floor is enforced over controls rather than over fields. The remedy is a **selector set**, exactly as F-118's was | R§1 · S§11.12, §16.3 scenario 21 |
| Menu comments, Stage 6e (enhancement, not a finding) | §11.4's menu index gains the staff read: the whole-menu read rather than a per-item one, grouped by dish in the menu's own order because the read's own order is a UUID ordering, with the count as a chip that is absent rather than zero. The prohibition that makes *staff-facing* mean something is computed over every component in the tree | §7, §11.4, §16.3 scenario 21, §16.4 |
| F-128 | The plan's closing paragraph is **rewritten rather than annotated** (F-114, F-122), Stage 1f lands §11.3's two surfaces in the barrier, and Stage 1g — §11.2's kitchen board — is the one row still open. §11.12 now writes down the set of surfaces the browser level reaches and names the one outside it, because a surface the barrier does not name is unmeasured whichever the reason | R§1 · S§11.12, §16.3 scenario 10 · `MENU_AND_HANDHELD_PLAN.md` |
| F-126 | The fixtures keep the mechanism and change the call; the gate's ordering fact is **deleted rather than reworded**, because a per-builder value has no global assignment to be late (F-41), and the fact that remains is per-builder rather than per-file. §18 gains the rule the failure produced | §16.4, §18 · `Directory.Packages.props` |
| F-21 – F-24 | Editorial: four experiences + display; abbreviation carve-out; generic paths; directives resolved | REQUIREMENTS rev 2 |
| F-25 – F-33 | export.sh fixes; REQUIREMENTS tracked in docs/ | export.sh header; repo layout |
| Claude judgment calls (owner-vetoable, recorded) | Reminder = once at threshold iff no line of the send fulfilled/removed; counter/admin line-changing staff edits also alert loudly; reset forces TOTP… | §10.1–10.2, §3.5, §3.7, §4.5 |

## Changelog

**The full changelog through v1.50 is archived** in [`docs/progress/TECHNICAL_SPECIFICATION_THROUGH_V1_50.md`](progress/TECHNICAL_SPECIFICATION_THROUGH_V1_50.md), and every entry it holds is also a commit. The most recent are kept here because `SpecificationVersionTests` reads the newest one as this document's current version and asserts the header agrees with it. How many is not written down: nothing can check it, and it had already drifted by one (**F-77**).

- **v1.58 — 2026-08-29.** The plan said it was finished and its own table said otherwise. `docs/MENU_AND_HANDHELD_PLAN.md` closed with *what is left: nothing in this plan* while the same file recorded Stage 1 as **open** and Stage 1b as *administration half landed* — and the missing half is §11.2 and §11.3, the two stations R§1 is actually written about (**F-128**). **§11.12** stops leaving the browser level's reach to be inferred from a file and writes the set down: four surfaces named, §11.2 named as the one outside them, and the boundary between a control and a link inside a sentence stated rather than left implicit. **§16.3 scenario 10** gains the counter's half — the counter member works a 375×667 handset from sign-in to settlement, the board measured while a sitting is still open because its way-in control exists only then, and the bill measured *where it stands* immediately before the close collapses the per-line actions, the staff-add form and the close button together. The administrator's page and the guest's are untouched, so nothing above the barrier moves. No production code changed and no new test class exists: the barrier is two `HandheldSurface` records and one shared verdict.
- **v1.57 — 2026-08-28.** Stage 6e, and the end of the menu enhancement plan's buildable rows. **§7** gains the staff read's four rulings — the whole-menu read rather than a per-item one, no per-dish list on a dish's own page, grouped by dish in the menu's order because the read's own order is a UUID ordering shown to a person, and a count chip that is absent rather than zero. **§11.4** gains the block and the chip; **§16.4** gains `MenuItemCommentStaffReadContractTests`, whose central prohibition — the whole-menu read is reached from no component outside `Pages/Administration/` — is computed over every `.razor` in the tree rather than listed (**F-47**). **§16.3 scenario 21** gains six claims for the reader the feature was built for: the sentence arrives at somebody who did not write it, and stops arriving when it is withdrawn without leaving `menu_item_comment_event`. `IMenuItemCommentDirectory.ListAsync` had been a read with no caller since Slice 68, which §7 calls the same defect as a workflow verb with no caller; it was a named deferral rather than a finding, and this is the slice that discharges it.
- **v1.56 — 2026-08-27.** Stage 6d: the guest can say what they thought, and the control beside it had been under a floor nobody measured. **§7** gains the five surface rulings — the box is in the detail panel because a `<textarea>` inside a `<button>` is markup a parser takes apart, a blank body is a refusal and never a withdrawal, the withdraw control renders only where something stands, the draft belongs to the chosen dish and a menu re-read never overwrites it, and the outcome is declared beside the sentence. **§11.1** gains the box; **§16.4** gains `MenuItemCommentSurfaceContractTests`, whose two comment reads are told apart by the receiver rather than by a method name four directories share. **§16.3 scenario 21** gains six claims and, in the same slice as the controls, their barrier selectors (**F-93**) — which is what found **F-127**: §11.12's 16px floor had only `<input>` subjects, so the like control had declared 15.2px since Slice 58. **§11.12** now states that the floor is enforced over the selectors the barrier names.
- **v1.55 — 2026-08-27.** A gate that named an API the pinned package does not have. Testcontainers moved logger configuration off the static `TestcontainersSettings.Logger` and onto the builder before 4.14.0, the version this tree pins: both container fixtures assigned the static property and neither assembly compiled, and `ContainerLoggingContractTests` asserted that they assigned it (**F-126**). The fixtures install `WithLogger` on the chain that reaches `Build`; **§16.4**'s paragraph becomes **one** assertion over the same computed subject set, per builder rather than per file, and the ordering half is **deleted rather than reworded** because a per-builder value has no global assignment to be late (**F-41**). **§18** gains the rule: an emulated proof establishes what an assertion decides, not that the source it scans compiles. `Directory.Packages.props` records the per-builder mechanism and loses the sentence claiming every pin was resolved on one date (**F-77**).
- **v1.54 — 2026-08-27.** An assertion that decided a fact about a sort order, and the proof that was recorded rather than run. `TheSchemaRefusesEveryForbiddenShapeOfAnEventRow` probed the vocabulary CHECK with a row that broke the payload CHECK as well; PostgreSQL evaluates CHECKs in name order, so it named the one that sorts first and the fact failed on a correct schema (**F-123**). Every probe now breaks one CHECK and carries a control row that proves it, the constraint inventory is read out of `pg_constraint`, and the cap gets the fifth probe the fact's name had been promising. **§18** gains two rules: an assertion may not depend on which of several simultaneous failures is reported first, and a sensitivity proof is a record of an execution — which Slice 68's was not (**F-124**). **§16.4** gains `ContainerLoggingContractTests`; both container fixtures silence the Testcontainers logger, whose several hundred Information lines per run were what buried the failing assertion (**F-125**).
- **v1.53 — 2026-08-26.** The last stage the menu plan carried, started, and the two documents that still said it could not be. **§7** gains the six comment rulings, **§8.2** gains `menu_item_comment_event` with four named constraints over one nullable column, and **§8.3** gains the fold that has no `WHERE` clause on purpose. **§17 loses the paragraph that promised the rate limiter it already had** and **§19 loses the clause that called Stage 6 unstartable** — both eleven slices stale, both deleted rather than annotated (**F-122**); **§18** gains the habit §17 had been claiming was stated there. **§16.4** gains `MenuItemCommentTests`.
- **v1.52 — 2026-08-26.** A reading composed out of two instants, and the flake it had been producing since the harness was written. **§16.4** gains `HarnessSnapshotContractTests`: a composite that a `Func<T, bool>` is evaluated against is read in one `EvaluateAsync`, over a subject set computed from the harness rather than listed. `KitchenJourneys.ReadBoardAsync` and `TableOrderJourneys.ReadBasketAsync` are rewritten to one evaluation each (**F-121**). No production code changed.
- **v1.51 — 2026-08-26.** House-cleaning slice. Every comment removed from authored `.cs`, `.razor`, `.sql`, `.css`, `.js` and `.sh` — 2,087,175 bytes, 42% of all code — and §18 now states the rule that keeps it that way, held by `SourceCommentContractTests`. `DocumentationCommentContractTests` deleted: its subject no longer exists (**F-120**). `ConfigurationSurfaceTests` no longer terminates its scan on a documentation comment (**F-119**). The long form of this document, of the findings register, of the build log and of the menu plan archived to `docs/progress/`; §7, §11, §14, §16, §18, Appendix A and this changelog rewritten as registers. No behaviour changed.
- **v1.50 — 2026-08-25.** The picture the barrier had never seen, and the element that can be present with no area at all. **§16.3 scenario 21** attaches a photograph to one of its two dishes: a dish with a picture renders a different card grid and its panel renders the whole frame uncropped, and six stages of the menu plan built both…
- **v1.49 — 2026-08-25.** The guest's own menu is measured at the width it is read at, and the first thing that measurement found. **§11.12** gains the paragraph the finding is about — *whether a declared rule reaches a rendered element is a third question, sitting between the two levels, and only the browser level answers it* — and its…
- **v1.48 — 2026-08-25.** A gate that could not tell a use from a mention, and the menu plan's last rendering prerequisite made executable. **§16.4** gains the two-assertion raw-HTML contract test and the four-assertion proof of the reader both tree scans now go through, and **loses a count of how many files a directory holds**…
- **v1.47 — 2026-08-25.** `/register` has a rate limit, and the eleven slices it did not have one are the finding. **§11.8** gains the budget — `GUEST_REGISTRATION_ATTEMPTS_PER_WINDOW` per `GUEST_REGISTRATION_WINDOW_MINUTES` per client address, default 60 per 10 minutes — and four paragraphs of why it is shaped the way it is. **§13** gains…
- **v1.46 — 2026-08-25.** The controls the barrier had been measuring for thirteen slices are finally pressed, and a documentation comment that described somebody else. **§16.3 scenario 17** gains the two resequencing verbs: a heading moves down and a guest's already-open menu re-orders itself, then moves back and it returns; an item moves…
- **v1.45 — 2026-08-24.** A dish that is off tonight can still be liked, and a harness read that had been wrong since the slice that fixed the read beside it. **§7** and **§11.1** gain Stage 5c's second control. §11.1 puts the like in the detail panel and the panel opens only for a *chosen* item, so §7's `disabled` card meant **a guest…
- **v1.44 — 2026-08-24.** The other half of the like, and the end of the menu enhancement's open list. **§11.4** gains the count: a **neutral chip beside the dish's name**, and **no chip at all where nobody has pressed it**. Three things about that are rulings. It is **beside the name rather than in a column of its own**, which is Stage…
- **v1.43 — 2026-08-24.** The half of the like a guest can see, and a number that was written three times in the one place a ruling had cleared. **§7** gains §11.1's control: it is in the item's **detail panel and never on its card**, and the reason is mechanical rather than aesthetic — a card *is* a `<button>`, the HTML parser closes the…
- **v1.42 — 2026-08-24.** The first stage of the menu enhancement that is not about a dish's own columns, and a paragraph that had been quietly asking for tests nobody should write. **Two changes, and v1.32's distinguishable-failure ruling governs both:** one is a migration with nine integration facts against a real database and one…
- **v1.41 — 2026-08-23.** A red CI build, and the open item this feature had carried longer than any other. **Two changes, and v1.32's distinguishable-failure ruling governs both:** one is a `stackalloc` moved up seven lines, which fails as a CA2014 diagnostic from the compiler naming a file and a column in the end-to-end project; the…
