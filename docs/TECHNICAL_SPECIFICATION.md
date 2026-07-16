# Restaurant Management System — Technical Specification

**Status:** Draft v0.1 — implementation-ready except where marked ⚠, which depend on rulings for findings F‑01…F‑05 in `DOCUMENTATION_REVIEW.md`.
**Source of truth:** `REQUIREMENTS.md` (commit `af51017`). Where the requirements conflict or are silent, this document records the interpretation chosen; every such interpretation is cross-referenced to a finding ID (`F‑nn`) in `DOCUMENTATION_REVIEW.md` so it can be vetoed before code exists.
**Normative language:** *must* = hard requirement; *should* = default unless an ADR says otherwise.

---

## 1. Architecture overview

One ASP.NET Core **Blazor Server** application (interactive server render mode, .NET 10) serves all four experiences as routed areas of a single app: `/table`, `/kitchen`, `/counter`, `/administration`. A single app (rather than four) is chosen because all experiences share one SignalR-backed live-update model, one identity system, and one database; separate apps would multiply deployment surface without any isolation benefit at single-restaurant scale. *(ADR‑0001)*

Runtime topology (one restaurant instance = one Podman Compose stack):

| Service | Image | Purpose |
|---|---|---|
| `web` | built from `Containerfile` (sdk:10.0 → aspnet:10.0, multi-stage, non-root) | Blazor Server app, DbUp migrations at startup |
| `postgres` | `docker.io/library/postgres:18` | Only durable store |
| `caddy` | `docker.io/library/caddy:2` | TLS termination (see §12), reverse proxy with WebSocket pass-through |
| `cloudflared` *(optional, later phase)* | `docker.io/library/cloudflare/cloudflared` | Persistent named tunnel — production remote access |

**Redis is not part of v1.** No v1 requirement consumes it; its future roles (SignalR backplane if ever multi-node, cache) are documented but not scaffolded. *(F‑13, ADR‑0006)*

**Podman Compose is the canonical orchestrator** for dev, CI, and prod. An Aspire AppHost may exist as an optional developer convenience but no script, test, or CI path may require it. *(F‑14, ADR‑0004)*

Scale assumption: single `web` instance per restaurant. All live updates flow through an in-process broadcaster (§8); no distributed backplane exists or is needed.

---

## 2. Solution and repository layout

```
/
├── run.sh                          # canonical entry point (defined in §14)  (F‑17)
├── Containerfile                   # web application image
├── compose.yaml                    # podman compose stack
├── Directory.Build.props           # shared build settings (nullable, analyzers, LangVersion)
├── Directory.Packages.props        # Central Package Management — single version source
├── .env.example                    # template; run.sh copies to .env if missing (§14)
├── source/
│   ├── MyRestaurant.Domain/        # entities, order-event model, projection logic (no I/O)
│   ├── MyRestaurant.DataAccess/    # Dapper repositories, DbUp migration scripts (embedded)
│   └── MyRestaurant.WebApplication/# Blazor areas, identity, SignalR-adjacent services, JS interop
├── tests/
│   ├── MyRestaurant.UnitTests/
│   ├── MyRestaurant.IntegrationTests/
│   └── MyRestaurant.EndToEndTests/ # Playwright, container-only
├── scripts/                        # all real logic; CI only calls these (§15–§16)
├── docs/
│   ├── REQUIREMENTS.md             # tracked here, NOT under docs/llm/  (F‑26)
│   ├── TECHNICAL_SPECIFICATION.md  # this file
│   ├── DOCUMENTATION_REVIEW.md
│   ├── adr/                        # one file per decision; edited in place, never duplicated
│   └── llm/                        # generated dumps only — gitignored output target
└── .github/workflows/continuous-integration.yml   # thin: checkout + call script
```

Dependency direction: `WebApplication → DataAccess → Domain`. Domain has zero package references beyond the BCL, so all projection/state logic is unit-testable without infrastructure (SOLID: dependency inversion via interfaces defined in Domain, implemented in DataAccess).

**Naming convention** *(F‑22)*: full words everywhere — code, SQL identifiers, script names, config keys — with an explicit carve-out for industry-standard initialisms whose expansion would harm clarity: `TOTP`, `QR`, `URL`, `HTTPS`, `OTLP`, and third-party-defined keys (`OTEL_*`, `POSTGRES_*`, `ASPNETCORE_*`, `UPTRACE_DSN`). `id` is an abbreviation; the word is `identifier` (e.g., `person_identifier`).

---

## 3. Identity and authentication

### 3.1 Building blocks

ASP.NET Core **Identity core services with custom Dapper-backed stores** (no EF): `IUserStore`/`IUserPasswordStore`/`IUserTwoFactorStore`/etc. implemented in `MyRestaurant.DataAccess` over the `person` tables of §6. This buys the hardened primitives — PBKDF2 password hashing (`PasswordHasher` v3), RFC 6238 TOTP verification, recovery-code handling, lockout counters — without EF. Passkeys use the **built-in WebAuthn/passkey support that ASP.NET Core Identity ships in .NET 10**; if any gap is found in practice, `fido2-net-lib` (MIT) is the approved fallback. Cookie authentication (secure, HttpOnly, SameSite=Lax), 24 h sliding expiry, security-stamp revalidation every 5 minutes so admin actions (role revoke, password reset) bite quickly. TOTP secrets are stored encrypted with ASP.NET Data Protection; the key ring is persisted to a named volume (`DATA_PROTECTION_KEYS_DIRECTORY`) so cookies and protected secrets survive container restarts. *(ADR‑0003)*

### 3.2 Credential model

- First factor: **password** or **passkey**, alternatives per REQUIREMENTS §4.1. `password_hash` is nullable; an account must have at least one first factor (enforced in application logic — cross-table constraint). Registration forms use `autocomplete="username"` / `autocomplete="new-password"`; passkey enrollment is offered post-registration and after sign-in as a dismissible nudge, never a gate, for guests.
- Usernames: unique per instance, case-insensitive (`citext`), 3–64 chars, no whitespace. *(F‑19)*
- **TOTP is two independent facts** *(F‑08)*: *enrolled* (`totp_secret_protected` non-null) and *required* (`totp_required` boolean). Sign-in matrix:

| enrolled | required | sign-in behavior |
|---|---|---|
| no | no | first factor only |
| yes | yes | first factor + TOTP (or recovery code) |
| yes | no | first factor only — challenge skipped (admin has explicitly relaxed it) |
| no | yes | first factor, then **forced TOTP enrollment** before proceeding |

  User self-enrolls/removes TOTP while signed in (enrolling sets `required = true`, per §4.2 "required by default"). Admin may toggle `totp_required` for any **non-admin** as a standalone action. Ten single-use recovery codes are generated at enrollment, stored hashed, regenerable.
- **Administrator accounts:** must possess ≥1 passkey and have TOTP enrolled+required. Enforced **at grant time only** — the grant action fails unless both exist; no continuous background check, no auto-demotion (REQUIREMENTS §4.3). ⚠ An admin may still *sign in* with password+TOTP (possession ≠ mandatory use), and a passkey sign-in is *also* followed by a TOTP challenge, because §4.3 says TOTP is always required — unusual UX, flagged for confirmation. *(F‑09)*
- **Kitchen role:** must possess ≥1 passkey, enforced at grant time. **Counter role:** no credential precondition — as literally written. ⚠ *(F‑05)*
- Sign-in on staff devices and passkey ceremonies require a secure context; see §12.

### 3.3 First-admin bootstrap *(F‑03)*

While zero administrators exist, the app exposes only `/setup`: a wizard that (1) creates the user, (2) **requires** passkey registration, (3) **requires** TOTP enrollment, then (4) grants `administrator` — all before the account is usable. Steps 1–4 conclude in one guarded transaction (re-check "no admin exists" under lock) so a race cannot mint two bootstrap admins. Once any administrator exists the path returns 404 permanently. This is the only way to satisfy §4.3's credential mandates on an account that is auto-granted admin.

### 3.4 Password reset ⚠ *(F‑01)*

- Self-service change (knows current password): any signed-in user.
- Admin reset: generates a 20-character random password (CSPRNG), displays it exactly once to the admin, sets `must_change_password = true`; the user is forced into a change-password screen at next sign-in before any other action. The admin may, separately, toggle `totp_required` off if the user also lost their authenticator.
- **Spec assumption pending ruling:** reset capability is **administrator-only**; counter's "password reset assistance" (REQUIREMENTS §3.3) is procedural — counter identifies the guest and summons/relays to an admin. If the ruling instead grants counter a reset capability, it must be scoped to accounts holding no role, and every reset is a `security_event` either way.
- No unauthenticated "forgot password" flow exists (no email/SMS in v1) — human-mediated only.

### 3.5 Authorization

Role claims from `person_role`; area policies: `/table` → authenticated; `/kitchen` → kitchen ∨ administrator; `/counter` → counter ∨ administrator; `/administration` → administrator. Administrator satisfies every policy (REQUIREMENTS §4.3 full authority). Fine-grained action matrix:

| Capability | Guest (owner) | Kitchen | Counter | Administrator |
|---|---|---|---|---|
| Submit order (own, open sitting) | ✔ | — | — | ✔ |
| Acknowledge order | — | ✔ | — | ✔ |
| Add/remove order items after submission | — | ✔ | ✔ | ✔ |
| Adjust locked-in line price ⚠ *(F‑02)* | — | — | ✔ | ✔ |
| Activate/deactivate menu item | — | ✔ | ✔ | ✔ |
| Create/edit menu items | — | — | — | ✔ |
| Close + settle sitting ⚠ *(F‑04)* | — | — | ✔ | ✔ |
| Hide own historical order | ✔ | — | — | — |
| Unhide any hidden order | — | — | — | ✔ |
| Reset passwords / toggle TOTP / manage users & roles | — | — | ✖ (assist only, F‑01) | ✔ |

Account "deletion" is **deactivation** (`is_active = false`, sign-in blocked, sessions invalidated via security stamp). Rows are never deleted — required by the append-only history model; hard erasure is explicitly deferred. *(F‑10b)*

---

## 4. Tables, sittings, and sessions

- Each physical table is a `restaurant_table` row; its immutable `table_identifier` (UUIDv7) is encoded in the QR as `{RESTAURANT_PUBLIC_ORIGIN}/table/{table_identifier}` so a phone camera opens the browser directly. The admin area renders printable QR pages (QRCoder, MIT — also reused for TOTP provisioning QR).
- Scanning routes to sign-in/registration for that table (scan alone creates nothing). After authentication, in one transaction: if no open `table_sitting` exists for the table, insert one (creator becomes first `person_at_table`); the partial unique index `one open sitting per table` (§6) makes the concurrent-scan race lose gracefully — on unique violation, re-read and fall through to the join path. If an open sitting exists, show it (table label, party size, opened-at) with an explicit **Join** action; declining lands on the personal profile/history. Joins are recorded (`person_at_table.joined_at`) and broadcast to the party — mitigation for the static-QR risk, which is otherwise accepted as written. *(F‑12)*
- A person may appear in multiple open sittings (e.g., moved tables before staff closed the first); the UI operates in the context of the most recently scanned/selected sitting, and orders always bind to the sitting they were placed in. There is **no anonymous ordering** — every guest authenticates, which the requirements imply but never state. *(F‑19)*
- **Visibility:** while a sitting is open, every member sees every member's orders at that table live; cross-table visibility never exists. On close, the server broadcasts a visibility-revoked event to the sitting's subscribers and tears down the subscription group — connected guests' UIs switch immediately (not on next navigation) to personal-history mode.
- **Close = settle, atomically** ⚠ *(F‑04)*: counter (or administrator) triggers one transaction that verifies the sitting is open, stamps `closed_at`/`closed_by_person_identifier`/`closure_type`, and records `settled_total_amount` computed from the projections at that instant. No closed-but-unpaid state exists. End-of-business-day batch close iterates open sittings, one transaction and one log entry each, `closure_type = 'end_of_business_day'`.

---

## 5. Orders — lifecycle and event model

### 5.1 Cardinality and states *(F‑07, ADR‑0007)*

A **submission is an order**: a guest builds a cart client-side (circuit state, nothing persisted), and submitting creates one `guest_order` with its initial events atomically. A person may therefore hold several orders within one sitting (round two of drinks = a new order), each with its own kitchen lifecycle. Guests never edit an order after submission (REQUIREMENTS §6.5 grants edit rights only to kitchen/counter); wanting more is a new order, wanting less is a request to staff.

States, exactly two, derived from events: **Submitted** (creation, timer starts, kitchen alerted) → **Acknowledged** (single kitchen tap). Served is deliberately untracked.

### 5.2 Append-only event log

The source of truth per order is `order_event` plus **typed subtype tables** (class-table inheritance — fully relational, no EAV, no JSON payloads). Current state is a projection (SQL views §6.3 + an equivalent pure fold in `MyRestaurant.Domain` for in-memory use and unit testing). Events only roll forward; corrections supersede, never erase. *(ADR‑0002)*

| `event_type` | Subtype table / payload | Permitted actor roles |
|---|---|---|
| `item_added` | `order_line_identifier` (new), `menu_item_identifier`, `quantity`, `unit_price_amount` (locked-in copy of menu price at that moment), `customization_note` | guest (only within the creating transaction), kitchen, counter, administrator |
| `submitted` | — (exactly once per order, at creation) | guest, administrator |
| `acknowledged` | — (at most once per order) | kitchen, administrator |
| `item_removed` | `order_line_identifier` → an existing added line | kitchen, counter, administrator |
| `item_price_adjusted` ⚠ *(F‑02)* | `order_line_identifier`, `new_unit_price_amount` | counter, administrator |

Quantity change = `item_removed` + `item_added` (two events, both visible — simplest honest history). Sequence numbers are assigned per order under `SELECT … FOR UPDATE` on the `guest_order` row; `UNIQUE (order_identifier, sequence_number)` makes concurrent writers fail loudly rather than interleave silently.

Every event records `actor_person_identifier` and `actor_role` (the role being exercised). Tamper-evidence display: staff and admin views show full actor identity per event; **guest-facing** live edit notices show the role label only ("kitchen removed Salmon — unavailable"), not staff usernames. *(F‑11)*

**Edit window** *(F‑10)*: kitchen/counter may append events only while the sitting is open. After close, orders are read-only except that an administrator may append corrective events (each is itself logged; `settled_total_amount` on the closure is never rewritten, so any post-close correction is visibly divergent rather than silently reconciled).

### 5.3 History, hiding, unhiding

Post-close, an order belongs to its owner's unlimited history. Hiding appends `order_visibility_event(action = 'hidden_by_owner')`; the owner's views filter on the latest visibility action; no user-facing unhide exists (UI confirms: "This cannot be undone from your account"). Admin **hidden-records view**: lists all currently-hidden orders system-wide, filterable by username, date range, and table; each row expands to the complete stored record — full event log, visibility log, sitting context, unprojected (REQUIREMENTS §4.3, §6.6) — with a per-record Unhide action appending `unhidden_by_administrator`.

### 5.4 Menu

`menu_item` holds current state (name, description, `current_price_amount`, `is_active`); every mutation simultaneously appends a `menu_item_event` (created / renamed / description_changed / price_changed / activated / deactivated, with typed old/new columns) in the same transaction — this supplies the price-change timelines §4.3 promises the admin but §6 never mandated recording. *(F‑18)* Deactivated items stay visible on the guest menu, marked "currently unavailable," unorderable; submission re-validates `is_active` per line inside the creating transaction and rejects with a live menu refresh if stale. Deactivation never touches existing orders. Kitchen/counter can toggle `is_active`; only admin creates/edits items. Prices: `numeric(10,2)`, single instance currency, display driven by `RESTAURANT_CURRENCY_CODE` (ISO 4217). *(F‑19)*

---

## 6. Database schema (PostgreSQL 18, DbUp migration `0001_initial_schema.sql`)

UUIDv7 everywhere a GUID appears, **application-generated** via `Guid.CreateVersion7()` (keys known before round-trip; PG 18's native `uuidv7()` is available but the app is the single generator). Timestamps `timestamptz`. Reserved word avoided: the order table is `guest_order`.

```sql
CREATE EXTENSION IF NOT EXISTS citext;

-- ── identity ────────────────────────────────────────────────────────────────
CREATE TABLE person (
    person_identifier       uuid PRIMARY KEY,
    username                citext NOT NULL UNIQUE
                            CHECK (char_length(username) BETWEEN 3 AND 64),
    display_name            text NOT NULL CHECK (char_length(display_name) BETWEEN 1 AND 100),
    password_hash           text NULL,                 -- NULL ⇒ passkey-only (app enforces ≥1 first factor)
    security_stamp          uuid NOT NULL,
    totp_secret_protected   text NULL,                 -- Data-Protection-encrypted
    totp_required           boolean NOT NULL DEFAULT false,
    must_change_password    boolean NOT NULL DEFAULT false,
    email_address           citext NULL,
    phone_number            text NULL,
    is_active               boolean NOT NULL DEFAULT true,
    failed_sign_in_count    integer NOT NULL DEFAULT 0,
    lockout_end_at          timestamptz NULL,
    created_at              timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE person_role (
    person_identifier       uuid NOT NULL REFERENCES person,
    role_name               text NOT NULL CHECK (role_name IN ('administrator','kitchen','counter')),
    granted_at              timestamptz NOT NULL DEFAULT now(),
    granted_by_person_identifier uuid NULL REFERENCES person,   -- NULL only for bootstrap self-grant
    PRIMARY KEY (person_identifier, role_name)
);

CREATE TABLE passkey_credential (
    passkey_credential_identifier uuid PRIMARY KEY,
    person_identifier       uuid NOT NULL REFERENCES person,
    credential_identifier   bytea NOT NULL UNIQUE,     -- WebAuthn credential ID
    public_key              bytea NOT NULL,
    signature_counter       bigint NOT NULL DEFAULT 0,
    transports              text[] NULL,
    authenticator_aaguid    uuid NULL,
    label                   text NOT NULL DEFAULT 'passkey',
    created_at              timestamptz NOT NULL DEFAULT now(),
    last_used_at            timestamptz NULL
);

CREATE TABLE totp_recovery_code (
    totp_recovery_code_identifier uuid PRIMARY KEY,
    person_identifier       uuid NOT NULL REFERENCES person,
    code_hash               text NOT NULL,
    created_at              timestamptz NOT NULL DEFAULT now(),
    used_at                 timestamptz NULL
);

CREATE TABLE person_address (
    person_address_identifier uuid PRIMARY KEY,
    person_identifier       uuid NOT NULL REFERENCES person,
    label                   text NOT NULL,             -- free text, user-chosen
    street_line_one         text NULL,
    street_line_two         text NULL,
    city                    text NULL,
    region                  text NULL,
    postal_code             text NULL,
    country                 text NULL,
    created_at              timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE security_event (
    security_event_identifier uuid PRIMARY KEY,
    occurred_at             timestamptz NOT NULL DEFAULT now(),
    actor_person_identifier uuid NULL REFERENCES person,       -- NULL ⇒ system/bootstrap
    subject_person_identifier uuid NOT NULL REFERENCES person,
    event_type              text NOT NULL CHECK (event_type IN (
        'account_created','account_deactivated','account_reactivated',
        'password_changed','password_reset_by_administrator','forced_password_change_completed',
        'totp_enabled','totp_disabled',
        'totp_requirement_enabled_by_administrator','totp_requirement_disabled_by_administrator',
        'recovery_code_used','recovery_codes_regenerated',
        'passkey_registered','passkey_removed',
        'role_granted','role_revoked',
        'sign_in_succeeded','sign_in_failed','account_locked_out')),
    detail                  text NULL
);

-- ── tables and sittings ─────────────────────────────────────────────────────
CREATE TABLE restaurant_table (
    table_identifier        uuid PRIMARY KEY,          -- the value encoded in the QR; never rotated
    table_label             text NOT NULL UNIQUE,      -- human name/number
    is_active               boolean NOT NULL DEFAULT true,
    created_at              timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE table_sitting (
    table_sitting_identifier uuid PRIMARY KEY,
    table_identifier        uuid NOT NULL REFERENCES restaurant_table,
    opened_at               timestamptz NOT NULL DEFAULT now(),
    opened_by_person_identifier uuid NOT NULL REFERENCES person,
    closed_at               timestamptz NULL,
    closed_by_person_identifier uuid NULL REFERENCES person,
    closure_type            text NULL CHECK (closure_type IN ('standard','end_of_business_day')),
    settled_total_amount    numeric(10,2) NULL CHECK (settled_total_amount >= 0),
    CHECK (num_nulls(closed_at, closed_by_person_identifier, closure_type, settled_total_amount) IN (0, 4))
);
CREATE UNIQUE INDEX one_open_sitting_per_table
    ON table_sitting (table_identifier) WHERE closed_at IS NULL;

CREATE TABLE person_at_table (
    person_at_table_identifier uuid PRIMARY KEY,
    table_sitting_identifier uuid NOT NULL REFERENCES table_sitting,
    person_identifier       uuid NOT NULL REFERENCES person,
    joined_at               timestamptz NOT NULL DEFAULT now(),
    UNIQUE (table_sitting_identifier, person_identifier)
);

-- ── menu ────────────────────────────────────────────────────────────────────
CREATE TABLE menu_item (
    menu_item_identifier    uuid PRIMARY KEY,
    name                    text NOT NULL UNIQUE,
    description             text NULL,
    current_price_amount    numeric(10,2) NOT NULL CHECK (current_price_amount >= 0),
    is_active               boolean NOT NULL DEFAULT true,
    created_at              timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE menu_item_event (                          -- append-only; written in same txn as menu_item change
    menu_item_event_identifier uuid PRIMARY KEY,
    menu_item_identifier    uuid NOT NULL REFERENCES menu_item,
    sequence_number         bigint NOT NULL,
    occurred_at             timestamptz NOT NULL DEFAULT now(),
    actor_person_identifier uuid NOT NULL REFERENCES person,
    event_type              text NOT NULL CHECK (event_type IN
        ('created','renamed','description_changed','price_changed','activated','deactivated')),
    previous_name           text NULL,
    new_name                text NULL,
    previous_price_amount   numeric(10,2) NULL,
    new_price_amount        numeric(10,2) NULL,
    previous_description    text NULL,
    new_description         text NULL,
    UNIQUE (menu_item_identifier, sequence_number)
);

-- ── orders: append-only event log with typed subtypes ──────────────────────
CREATE TABLE guest_order (
    order_identifier        uuid PRIMARY KEY,
    table_sitting_identifier uuid NOT NULL REFERENCES table_sitting,
    person_identifier       uuid NOT NULL REFERENCES person,   -- owner; app verifies membership in sitting
    created_at              timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE order_event (
    order_event_identifier  uuid PRIMARY KEY,
    order_identifier        uuid NOT NULL REFERENCES guest_order,
    sequence_number         bigint NOT NULL,
    occurred_at             timestamptz NOT NULL DEFAULT now(),
    actor_person_identifier uuid NOT NULL REFERENCES person,
    actor_role              text NOT NULL CHECK (actor_role IN ('guest','kitchen','counter','administrator')),
    event_type              text NOT NULL CHECK (event_type IN
        ('submitted','acknowledged','item_added','item_removed','item_price_adjusted')),
    UNIQUE (order_identifier, sequence_number)
);
CREATE UNIQUE INDEX one_submission_per_order
    ON order_event (order_identifier) WHERE event_type = 'submitted';
CREATE UNIQUE INDEX one_acknowledgement_per_order
    ON order_event (order_identifier) WHERE event_type = 'acknowledged';

CREATE TABLE order_event_item_added (
    order_event_identifier  uuid PRIMARY KEY REFERENCES order_event,
    order_line_identifier   uuid NOT NULL UNIQUE,       -- names the logical line for later events
    menu_item_identifier    uuid NOT NULL REFERENCES menu_item,
    quantity                integer NOT NULL CHECK (quantity BETWEEN 1 AND 100),
    unit_price_amount       numeric(10,2) NOT NULL CHECK (unit_price_amount >= 0),  -- price locked at add time
    customization_note      text NULL
);

CREATE TABLE order_event_item_removed (
    order_event_identifier  uuid PRIMARY KEY REFERENCES order_event,
    order_line_identifier   uuid NOT NULL REFERENCES order_event_item_added (order_line_identifier)
);

CREATE TABLE order_event_item_price_adjusted (
    order_event_identifier  uuid PRIMARY KEY REFERENCES order_event,
    order_line_identifier   uuid NOT NULL REFERENCES order_event_item_added (order_line_identifier),
    new_unit_price_amount   numeric(10,2) NOT NULL CHECK (new_unit_price_amount >= 0)
);

CREATE TABLE order_visibility_event (
    order_visibility_event_identifier uuid PRIMARY KEY,
    order_identifier        uuid NOT NULL REFERENCES guest_order,
    occurred_at             timestamptz NOT NULL DEFAULT now(),
    actor_person_identifier uuid NOT NULL REFERENCES person,
    action                  text NOT NULL CHECK (action IN ('hidden_by_owner','unhidden_by_administrator'))
);

CREATE TABLE kitchen_notification (                     -- audit of alerts; enforces single reminder
    kitchen_notification_identifier uuid PRIMARY KEY,
    order_identifier        uuid NOT NULL REFERENCES guest_order,
    kind                    text NOT NULL CHECK (kind IN ('initial','reminder')),
    sent_at                 timestamptz NOT NULL DEFAULT now(),
    UNIQUE (order_identifier, kind)
);

-- supporting indexes
CREATE INDEX order_event_by_order        ON order_event (order_identifier, sequence_number);
CREATE INDEX guest_order_by_sitting      ON guest_order (table_sitting_identifier);
CREATE INDEX guest_order_by_person       ON guest_order (person_identifier, created_at DESC);
CREATE INDEX person_at_table_by_person   ON person_at_table (person_identifier);
CREATE INDEX table_sitting_by_table      ON table_sitting (table_identifier, opened_at DESC);
CREATE INDEX order_visibility_by_order   ON order_visibility_event (order_identifier, occurred_at DESC);
```

### 6.1 Projection views (read models — rebuildable, never authoritative)

```sql
CREATE VIEW order_current_line AS
SELECT added_event.order_identifier,
       added.order_line_identifier,
       added.menu_item_identifier,
       added.quantity,
       COALESCE(latest_adjustment.new_unit_price_amount, added.unit_price_amount) AS unit_price_amount,
       added.customization_note
FROM order_event_item_added AS added
JOIN order_event AS added_event USING (order_event_identifier)
LEFT JOIN LATERAL (
    SELECT adjusted.new_unit_price_amount
    FROM order_event_item_price_adjusted AS adjusted
    JOIN order_event AS adjustment_event USING (order_event_identifier)
    WHERE adjusted.order_line_identifier = added.order_line_identifier
    ORDER BY adjustment_event.sequence_number DESC
    LIMIT 1
) AS latest_adjustment ON true
WHERE NOT EXISTS (SELECT 1 FROM order_event_item_removed AS removed
                  WHERE removed.order_line_identifier = added.order_line_identifier);

CREATE VIEW order_current_state AS
SELECT o.order_identifier, o.table_sitting_identifier, o.person_identifier, o.created_at,
       min(e.occurred_at) FILTER (WHERE e.event_type = 'submitted')    AS submitted_at,
       min(e.occurred_at) FILTER (WHERE e.event_type = 'acknowledged') AS acknowledged_at
FROM guest_order o LEFT JOIN order_event e USING (order_identifier)
GROUP BY o.order_identifier, o.table_sitting_identifier, o.person_identifier, o.created_at;

CREATE VIEW order_visibility_current AS
SELECT DISTINCT ON (order_identifier) order_identifier, action, occurred_at
FROM order_visibility_event
ORDER BY order_identifier, occurred_at DESC, order_visibility_event_identifier DESC;

CREATE VIEW sitting_bill AS                              -- counter's per-table view
SELECT s.table_sitting_identifier, o.person_identifier,
       sum(l.quantity * l.unit_price_amount) AS person_total_amount
FROM table_sitting s
JOIN guest_order o        USING (table_sitting_identifier)
JOIN order_current_line l USING (order_identifier)
GROUP BY s.table_sitting_identifier, o.person_identifier;
```

`MyRestaurant.Domain` contains an equivalent pure fold (`OrderProjection.FromEvents(...)`) — the unit-tested reference implementation; the views must agree with it (asserted in integration tests).

### 6.2 Migrations and backups

DbUp runs at `web` startup: embedded scripts, ordered, transaction per script, journal table `schema_version_journal`; the app verifies all known scripts are journaled and applies any missing in sequence, failing fast (container exits non-zero) on error. Backups *(F‑16)*: `scripts/database_backup.sh` runs `pg_dump --format=custom` **inside the postgres container** via `podman exec` (guarantees client/server version match), writes `backup_YYYYMMDDTHHMMSS.dump` to the bind-mounted, git-ignored `BACKUP_DIRECTORY`, then prunes to the newest `BACKUP_RETENTION_COUNT` (default 7) — pruning only after a successful new dump. `scripts/install_database_backup_timer.sh` renders a **systemd user timer** (`OnCalendar` from `BACKUP_SCHEDULE_TIME`, default 06:00 host-local time) and reminds the operator to `loginctl enable-linger` so the rootless timer runs without a login session. Restore procedure documented alongside the script (`pg_restore` into a fresh volume).

---

## 7. Live updates (in-process, single node)

A singleton `IDomainEventBroadcaster` in `WebApplication` (built on `System.Threading.Channels`) publishes typed notifications: `OrderSubmitted`, `OrderAcknowledged`, `OrderEdited`, `MenuChanged`, `SittingMemberJoined`, `SittingClosed`, `VisibilityRevoked`. Blazor components subscribe on initialization with a scope key (sitting identifier, or the kitchen/counter channel) and re-render via `InvokeAsync(StateHasChanged)`; unsubscribe on circuit disposal. Blazor Server's own SignalR circuit is the delivery mechanism to browsers — no extra hub, no client push infrastructure. Publication happens **after** the owning DB transaction commits. Because there is exactly one `web` instance, no backplane exists; that assumption is recorded in ADR‑0006 as the trigger condition for ever introducing Redis.

---

## 8. Kitchen alerting and the always-on display

- **Server side:** submitting an order writes `kitchen_notification(kind='initial')` and broadcasts. A `BackgroundService` scans every 5 s for orders with `submitted_at` older than `KITCHEN_ACKNOWLEDGEMENT_REMINDER_SECONDS` (default 60), no acknowledgement, and no `reminder` row — then inserts the `reminder` row (unique index makes the once-only rule a database fact, resilient to restarts) and broadcasts the re-alert. No further escalation, by design.
- **Client side (kitchen area JS interop module):** a start-of-shift **"Arm audio"** button (one user gesture) resumes the `AudioContext` and primes the locally-bundled alert sample (CC0 asset in `wwwroot`, no CDN); the armed state is visibly indicated and its absence is a prominent warning. Screen Wake Lock is acquired on arm and re-acquired on `visibilitychange`. Blazor's reconnection UI is replaced with a full-screen, high-contrast banner + audible chirp on circuit drop so a dead connection is unmissable. Deployment guidance (kiosk mode, OS sleep settings) ships in `docs/`. Wake Lock and stable audio behavior require a secure context — see §12. Native app remains a conditional fallback only, untouched in v1.

---

## 9. Application surfaces (behavioral spec)

- **Table:** menu (active + greyed-out unavailable items), cart, submit; live party view — every table-mate's orders with items, notes, per-person and table running totals; live edit notices with role attribution *(F‑11)*; nudge (dismissible) toward passkey enrollment; profile page (credentials, TOTP, recovery codes, phone/email, free-label addresses); personal history with per-order hide (confirmed irreversible-for-you).
- **Kitchen:** queue of unacknowledged orders oldest-first with live age timers, table label, person display name, lines + customization notes; one-tap Acknowledge; recent acknowledged list; order edit (add/remove lines); menu item activate/deactivate; audio-arm status.
- **Counter:** open sittings grid → per-sitting bill (per person, per line, totals from `sitting_bill`); order edits including price adjustment ⚠ *(F‑02)*; **Close & settle** with confirmation showing final total; end-of-day batch close; menu activate/deactivate; reset-assistance flow per F‑01 ruling.
- **Administration:** user management (create staff accounts with temporary password + forced change, grant/revoke roles with passkey/TOTP grant-time checks, deactivate/reactivate, reset password, TOTP-required toggle for non-admins); menu CRUD with per-item event timeline (including the price-change timeline); tables CRUD + printable QR; hidden-records view (§5.3); sittings/orders explorer rendering **complete stored state** — full event streams, visibility logs, security events — with filters applied only on explicit request and never projected/truncated (REQUIREMENTS §4.3).

---

## 10. Observability

OpenTelemetry only, zero vendor packages: `AspNetCore` + `Npgsql` instrumentation, runtime metrics, a custom `ActivitySource`/`Meter` pair (`MyRestaurant.Domain`) covering order submission→acknowledgement spans and counters (`orders_submitted_total`, `orders_acknowledged_total`, `kitchen_reminders_sent_total`, `sittings_closed_total`), and logs via the OTel `ILogger` provider — end-to-end request → circuit → SQL coverage. Export is **OTLP configured exclusively through standard `OTEL_*` environment variables** (`OTEL_EXPORTER_OTLP_ENDPOINT`, `_PROTOCOL`, `_HEADERS`, `OTEL_SERVICE_NAME=myrestaurant.web`); exporters activate only when an endpoint is configured, console logging always on. Because `UPTRACE_DSN` is a vendor-shaped variable the app must not read *(F‑15)*, `run.sh` performs the courtesy translation when it is present: parse the DSN → set `OTEL_EXPORTER_OTLP_ENDPOINT`, `OTEL_EXPORTER_OTLP_HEADERS="uptrace-dsn=${UPTRACE_DSN}"`, `OTEL_EXPORTER_OTLP_PROTOCOL=grpc`. Any OTLP-compatible collector therefore works identically.

---

## 11. Configuration (`.env`, all keys full-word except the §2 carve-out)

| Key | Default | Consumer |
|---|---|---|
| `RESTAURANT_NAME` | `My Restaurant` | display |
| `RESTAURANT_PUBLIC_ORIGIN` | `https://localhost:8443` | QR URLs, WebAuthn relying-party identifier (§12) |
| `RESTAURANT_TIME_ZONE` | host TZ | display, end-of-day framing |
| `RESTAURANT_CURRENCY_CODE` | `USD` | price display |
| `RESTAURANT_DATABASE_CONNECTION_STRING` | composed by compose from `POSTGRES_*` | web |
| `POSTGRES_DATABASE` / `POSTGRES_USERNAME` / `POSTGRES_PASSWORD` | dev defaults; password generated into `.env` on first bootstrap | postgres, compose |
| `DATA_PROTECTION_KEYS_DIRECTORY` | named volume path | web |
| `KITCHEN_ACKNOWLEDGEMENT_REMINDER_SECONDS` | `60` | web |
| `BACKUP_DIRECTORY` / `BACKUP_SCHEDULE_TIME` / `BACKUP_RETENTION_COUNT` | `./backups` (git-ignored) / `06:00` / `7` | backup scripts/timer |
| `OTEL_EXPORTER_OTLP_ENDPOINT` / `_PROTOCOL` / `_HEADERS`, `OTEL_SERVICE_NAME` | unset ⇒ export disabled | web |
| `UPTRACE_DSN` | unset | `run.sh` translation only *(F‑15)* |

Nothing environment-specific is hardcoded; the image is identical across instances (REQUIREMENTS §8). `.env` is git-ignored; `.env.example` is committed; `scripts/ensure_environment_file.sh` copies it into place when `.env` is absent *(F‑16)*.

---

## 12. TLS and secure contexts *(F‑06, ADR‑0005)*

WebAuthn passkeys, the Screen Wake Lock API, and dependable media autoplay all require HTTPS — and passkeys additionally **bind to the relying-party domain**, so the origin must be *stable* wherever passkeys are used. The requirements are silent on certificates; this spec closes the gap:

1. **LAN (normal service):** Caddy terminates TLS using its internal CA for a stable local hostname (e.g., `restaurant.lan`); the CA root is installed once on staff devices and the kitchen kiosk. `RESTAURANT_PUBLIC_ORIGIN` points here; guest phones on the venue Wi-Fi resolve it via local DNS.
2. **Production remote access:** persistent **named** Cloudflare tunnel with a stable domain (later phase, as required).
3. **Quick tunnels (`try.cloudflare.com`):** demo-only. Because each run gets a **random hostname, passkeys registered through a quick tunnel are unusable on the next one** (relying-party mismatch) and TOTP-protected accounts remain reachable only by password. Tunnel scripts print this warning. Quick tunnels must never carry the bootstrap of a real instance.
4. Rootless Podman cannot bind 80/443 by default; the stack publishes 8080/8443, and the deployment doc offers the `net.ipv4.ip_unprivileged_port_start=80` sysctl for installations that want standard ports.

Quick-tunnel scripts (`scripts/quick_tunnel_start.sh <name> <port>`, `quick_tunnel_stop.sh <name>`) keep per-name state directories (`.tunnels/<name>/` — PID file, captured URL, log; git-ignored), so multiple concurrent tunnels never share environment or trample each other.

---

## 13. Testing

| Layer | Tooling | Scope |
|---|---|---|
| Unit | xUnit v3; plain asserts or AwesomeAssertions (Apache-2.0); **no Moq, no FluentAssertions** — hand-written fakes preferred, NSubstitute (BSD-3) permitted *(F‑20)* | `Domain` event fold/projections, price-lock behavior, TOTP sign-in matrix, authorization matrix, bootstrap guard |
| Integration | xUnit v3 against a disposable `postgres:18` container started by `scripts/run_integration_tests.sh` (compose profile) | Dapper repositories, migration idempotency, partial-unique-index races (double-open sitting, double-acknowledge), view-vs-domain-fold equivalence |
| End-to-end | Playwright **inside** `mcr.microsoft.com/playwright/dotnet` container on the compose network (Fedora-safe, per REQUIREMENTS §2) | bootstrap wizard (passkey via WebAuthn virtual authenticator + TOTP); guest registers at QR URL, orders; second guest joins, sees party orders; kitchen alert fires (JS test hook records the play call — CI is deaf), acknowledge; 60 s reminder (interval shortened via env); counter edits price, closes → connected guest's view flips to history live; hide → admin locates in hidden-records view → unhide; admin reset → forced password change |

---

## 14. Scripts and CI

`run.sh` (repo root — the entry point REQUIREMENTS §1 invokes but never defined *(F‑17)*): ensure `.env` → translate `UPTRACE_DSN` if present → `podman compose up --build --detach` → wait for web health → print URLs. Companions: `shutdown.sh`, and under `scripts/`: `ensure_environment_file.sh`, `run_unit_tests.sh`, `run_integration_tests.sh`, `run_end_to_end_tests.sh`, `continuous_integration.sh`, `database_backup.sh`, `install_database_backup_timer.sh`, `quick_tunnel_start.sh`, `quick_tunnel_stop.sh`. All logic lives here; the GitHub Actions workflow is intentionally the whole of:

```yaml
jobs:
  continuous-integration:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - run: bash scripts/continuous_integration.sh   # verifies podman, then build → unit → integration → end-to-end → teardown; uploads test artifacts
```

Everything CI does is reproducible locally with the same command (REQUIREMENTS §2).

---

## 15. Security posture and accepted risks

Cookie hardening + antiforgery (Blazor built-in); Identity lockout (5 failures → 5 min) plus fixed-window rate limiting on authentication endpoints; TOTP secrets encrypted at rest, recovery codes stored hashed; admin-generated passwords are single-display, forced-change, CSPRNG; every credential/role action lands in `security_event`; HSTS + standard security headers at Caddy on public domains; secrets only in `.env`/CI secrets, never in git. Accepted, documented risks: the static never-rotated table QR is a permanent capability to join that table's open sitting (REQUIREMENTS §5.1 forbids rotation — joins are logged and party-visible; a join-approval step is proposed as a requirements amendment, F‑12); counter role carries no credential precondition ⚠ *(F‑05)*; a `web` restart drops circuits (reconnect UI makes it obvious; DB state is unaffected); password recovery is human-mediated by design.

## 16. Governance and conventions

License `AGPL-3.0-only` (`LICENSE` at root; SPDX headers in source). Copyright remains with the authors; **no outside contributions**: GitHub Issues disabled, and because GitHub cannot disable pull requests on public repos, `CONTRIBUTING.md` states unsolicited PRs are closed unreviewed. Engineering rules: SOLID (interfaces in `Domain`, implementations injected; small role-focused services), nullable reference types + analyzers as errors, full-word naming (§2 carve-out), Central Package Management only, spec-first with **atomic documentation** — every change lands with its `REQUIREMENTS.md`/spec/ADR updates in the same commit, ADRs edited in place. Initial ADR set to create with the first commit: 0001 single-app-with-areas, 0002 relational order event log, 0003 Identity-over-Dapper, 0004 compose-canonical/Aspire-optional, 0005 TLS via Caddy internal CA + named tunnel, 0006 no Redis in v1, 0007 order-per-submission.

## 17. Build order

M1 skeleton: solution, compose, Containerfile, DbUp, health, OTel, `run.sh` → M2 identity: registration, password+passkey, TOTP, bootstrap wizard, roles, profile → M3 tables/sittings/orders: QR flow, event log, guest + kitchen areas, alerting → M4 counter: bills, edits, close/settle, live visibility revocation, history + hide → M5 administration surfaces → M6 hardening: full E2E matrix, backups + timer, tunnel scripts, deployment docs.

---

## Appendix A — Decisions register (interpretations awaiting veto)

| Finding | Decision embodied in this spec |
|---|---|
| F‑01 ⚠ | Password reset is administrator-only; counter "assists" procedurally |
| F‑02 ⚠ | Price adjustment: counter + administrator; item add/remove: kitchen + counter + administrator |
| F‑03 | Bootstrap wizard enforces passkey + TOTP before the first admin exists |
| F‑04 ⚠ | Close/settle: counter or administrator |
| F‑05 ⚠ | Counter role has no passkey requirement (as written) — accepted risk |
| F‑07 | One order per submission; multiple orders per person per sitting; guests never edit post-submission |
| F‑08/F‑09 | TOTP enrolled/required as two flags; admin passkey possession-only; TOTP challenged even after passkey sign-in |
| F‑10 | Orders read-only after close except admin corrective events; users deactivated, never deleted |
| F‑11 | Guests see role-level attribution of staff edits; full identity in staff/admin views |
| F‑12/F‑13/F‑14/F‑15/F‑16/F‑17/F‑18 | As specified in §4, §1, §10, §6.2, §14, §5.4 respectively |
