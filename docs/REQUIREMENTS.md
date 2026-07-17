# myrestaurant — Requirements

**Revision 2 — 2026-07-17.** This revision folds in the owner's rulings on documentation-review findings F-01 through F-33 (see `docs/DOCUMENTATION_REVIEW.md` for the ledger and `docs/adr/` for the decision records). Revision 1 is preserved in git history. The companion `docs/TECHNICAL_SPECIFICATION.md` v1.0 is the normative implementation contract; where this document states intent and that document states mechanism, the mechanism governs.

---

## 1. Purpose

A self-hosted ordering system for one small restaurant, run by its owner on their own hardware, published as free software. Guests seated at a table order from their own phones; the kitchen sees what to cook the moment it is sent; the counter settles the bill; the owner administers everything. No cloud vendor holds the data; no per-seat fee exists; anyone may run their own copy under the AGPL.

There are **four human experiences** — three physical stations (table, kitchen, counter) plus administration, which is a role rather than a place — and one **device experience**, the per-table display that shows the rotating join code. All five are areas of a single application (§3).

The system is deliberately small: one restaurant, one host, one PostgreSQL database, tens of tables, not thousands. Every design choice below is allowed to assume that scale.

## 2. Technology stack

| Concern | Choice | Notes |
|---|---|---|
| Runtime | .NET 10 | |
| Web framework | ASP.NET Core Blazor Server (interactive server render mode) | one application, routed areas |
| Database | PostgreSQL (current major) | `citext` extension for case-insensitive text |
| Data access | **Dapper** | **Entity Framework is forbidden** anywhere in the solution |
| Migrations | DbUp, plain SQL, executed at startup | ADR-0012 |
| Identity | ASP.NET Core Identity core services over custom Dapper stores | ADR-0003 |
| Password hashing | **Argon2id** via custom `IPasswordHasher` (Konscious.Security.Cryptography.Argon2, MIT) | ADR-0008; robust parameters, configurable |
| Passkeys | .NET 10 built-in WebAuthn support | `fido2-net-lib` (MIT) is the approved fallback |
| Live updates | Blazor Server circuits + in-process broadcaster | no Redis in v1 — ADR-0006 |
| Observability | OpenTelemetry (traces, metrics, logs), OTLP export | collector-agnostic; any OTLP endpoint works |
| Containers | **rootless Podman + podman-compose is canonical** | Aspire AppHost is an optional dev convenience, never required — ADR-0004 |
| Public origin / TLS | **Cloudflare named tunnel** on the owner's stable domain | ADR-0005; Caddy serves dev TLS and an optional staff-LAN fallback |
| Unit-test doubles | hand-written fakes preferred; NSubstitute acceptable | Moq is not used |
| QR rendering | server-side SVG generation | no client-side QR libraries |
| License | **AGPL-3.0-only** | |
| Dependencies | free/libre only | nothing that requires payment or a license key |

## 3. Applications (areas)

One Blazor Server application serves five routed areas (ADR-0001):

- **`/table`** — the guest experience: join a table, build and send orders, watch the party's orders live, see the running bill.
- **`/kitchen`** — the kitchen queue: loud alert on new work, pending lines grouped by guest order, tap-to-fulfill, item 86'ing (deactivate menu items).
- **`/counter`** — billing: per-sitting bill view, price adjustments with reasons, close & settle, and the on-screen rotating join QR for any table (the fallback if a table's display dies).
- **`/administration`** — everything: users, roles, resets, tables, display-device pairing and revocation, join-secret rotation, menu management, hidden-records view (locate and unhide guest-hidden orders, §6.7), event explorer, end-of-day batch close. The administrator always sees the **complete stored record, unprojected** — full event histories, hidden records, price-change timelines; filters narrow only on the administrator's explicit request.
- **`/display/{table}`** — the table display device surface: a full-screen rotating join QR for exactly one table, on a cheap paired device (ADR-0009). Devices are principals of kind `table_display`; they are not person accounts.

## 4. Identity and authentication

### 4.1 Accounts and credentials

Everyone who orders, cooks, or administers is a `person` with a unique username (case-insensitive, 3–64 characters) and an optional display name shown to their table-mates and the kitchen. Credentials are any combination of:

- **Password** — hashed with Argon2id (ADR-0008). Minimum length 12; no composition rules; no forced rotation.
- **Passkey** — the preferred credential for everyone; required for kitchen role holders and administrators at grant time.
- **TOTP** — RFC 6238, 6 digits, 30-second step; enrollable by any signed-in user; secrets stored encrypted at rest.

Account lockout: 5 consecutive failed attempts (password, TOTP, or recovery code) locks the account for 5 minutes.

Usernames are unique **per instance only**. No instance is aware of any other instance and no global user directory exists anywhere — every restaurant is sovereign. Registration and change-password forms use `autocomplete="username"` / `autocomplete="new-password"` so OS-level password managers on phones generate and offer strong passwords automatically. **Passkeys are always offered, never required, for ordinary guests** — a dismissible "want a faster way to sign in next time?" nudge after registration and after sign-in, never a gate; the grant-time passkey mandate applies only to the kitchen and administrator roles.

### 4.2 Two-factor policy (revised by the Q2 ruling)

**TOTP is challenged on password sign-ins only, and only when the account has TOTP enrolled.** The passkey sign-in path never challenges TOTP — a passkey-capable authenticator is already a second factor. There is **no per-user "require TOTP" toggle**; requiredness collapses to enrollment.

Administrators must always be **enrolled** in TOTP (enrolled at grant time and in the bootstrap wizard) and cannot remove their own enrollment. A passkey-only administrator with no password set is permitted; the enrollment then has no path on which to be challenged, which is acceptable because there is no password to phish. Recovery codes substitute for a TOTP code on the password path only.

### 4.3 Registration

Guests self-register at the moment of joining a table (§5.1): username, optional display name, and at least one credential — passkey offered first, password accepted. Staff accounts are created by an administrator.

### 4.4 First administrator

On a fresh database, `/setup` runs a one-time bootstrap wizard: create the account, register a passkey, enroll TOTP, grant `administrator` — all inside one guarded transaction. Once any administrator exists, `/setup` returns 404 forever.

### 4.5 Administrative reset (revised by the Q3 ruling)

An administrator resetting a user's credentials: sets a temporary password, always flags `must_change_password`, and — **if TOTP was enrolled at reset time** — wipes the TOTP secret and all recovery codes and flags `must_enroll_totp`. Reset never forces TOTP onto an account that never had it.

After **any** subsequent successful sign-in (password *or* passkey), a post-authentication obligations pipeline runs before the user reaches any destination: forced password change if flagged, then forced TOTP re-enrollment if flagged, then proceed. Every step writes a `security_event`.

Guests who lose access ask the counter, who identifies them in person and relays to an administrator; there is no self-service reset (no email/SMS infrastructure exists on purpose).

### 4.6 Profile, contact details, and addresses

Every person has a profile page where they manage their credentials (passkeys, password, TOTP and recovery codes) and may, all optionally:

- Store a **phone number and/or email address**. These exist for **manual escalation only** — e.g., an administrator resets a password and then calls or texts the user outside the system. They are wired to no automated pipeline (no "your order is ready" texts); building one would require a paid sending service, which conflicts with the no-paid-dependency principle. This may be revisited later as an explicit, named future integration — never assumed in scope.
- Store **postal addresses with free-text labels** ("Home", "Work", "Grandparents' house"). Structured street/city/postal-code fields are a reasonable implementation detail, but the label is always free text chosen by the user. Nothing consumes addresses in version 1 — they are deliberate scaffolding for a possible future delivery/takeout feature, not dead weight to be removed, and not load-bearing for anything now.

## 5. Tables, sittings, and joining

### 5.1 Joining a table (revised by the F-12 / Q4 / Q5 rulings)

Every table has a **rotating join code**, not a printed one. Printed static QR codes are gone entirely.

- Each table holds a server-side 32-byte `join_secret`, never disclosed to any client. The current join token is `HMAC-SHA256(join_secret, table-uuid ":" time-window)` where the window advances every 60 seconds (configurable); the server accepts the current and previous windows (≤120 s validity).
- A cheap **table display device**, paired once by an administrator with a one-time code, shows the current QR full-screen and refreshes on the window boundary (`/display/{table}`, ADR-0009).
- Scanning yields `{origin}/table/{id}?token=…`. A valid token is immediately exchanged for a short-lived **join grant** (10 minutes, encrypted cookie) so that slow registration cannot outlive the token. After sign-in/registration, joining consumes the grant. Expired tokens get a friendly "code expired — scan the display again" page.
- If a display dies, the **counter (or an administrator) shows the same rotating QR on their own screen** and the guest scans that. Rotation runs whether or not a sitting is open.
- Existing members of the table's open sitting reach `/table/{id}` without any token.

### 5.2 Sittings

A **sitting** is one party's occupation of one table from first join to close. The first successful join with no open sitting on that table creates one; later joiners with a valid grant become members of the open sitting. A person may be a member of multiple open sittings (edge case, permitted). Each member has exactly **one living order** per sitting (§6.1). Operationally, a new party is not expected to occupy a table until the previous sitting has been closed and settled at the counter; an accidentally-left-open sitting (a device abandoned at the table) is resolved by closing it — there is no separate "reset table" action.

### 5.3 Visibility

Members of a sitting see each other's display names, orders, and line states live. Guests at **different tables never** see each other's orders — table-to-table privacy is absolute. The moment a sitting closes, party-wide visibility ends **immediately** for everyone still connected: a live permission change pushed over the same circuit that drives the rest of the interface, not a rule enforced on next page load. Kitchen and counter see everything current. History visibility is governed by §6.7.

### 5.4 Close and settle (revised)

Counter or administrator closes a sitting. Closing computes and stamps the **settled total** — the sum over all non-removed lines at their latest price, including still-pending lines — under a lock that excludes concurrent order writes. Before closing, the counter reviews still-pending lines and either removes them (with a reason) or knowingly charges for them. After close the sitting is read-only history, except administrator corrective events (§6.6). Administration offers an end-of-day batch close for stragglers.

## 6. Orders

### 6.1 One living order per guest per sitting (revised by the Q1 ruling)

Each member has exactly one order per sitting, created lazily on their first send. There is no order-per-submission.

### 6.2 Staging and sending

Guests build changes **client-side** (a staging area holding line additions — menu item, quantity 1–100, optional customization note — and removals of their own pending lines) and press **Send** explicitly. Each send commits as **one batch event** and produces **one kitchen alert**. There are no per-keystroke events or alerts.

### 6.3 Line lifecycle (replaces order-level acknowledgment)

Every added line is **pending** until the kitchen (or an administrator) marks it **fulfilled** — prepared and dispatched — or a permitted actor removes it. There is no order-level Acknowledged state and no in-preparation granularity; "served to the table" is deliberately untracked beyond fulfillment. A mistaken fulfillment is corrected by a visible **fulfillment reversal** (kitchen or administrator), returning the line to pending.

### 6.4 Who may do what

- **Guest (order owner):** add lines; remove **their own pending** lines. Fulfilled lines cannot be removed by the guest. Only while the sitting is open.
- **Kitchen / counter / administrator:** add or remove any line (staff edit, reason optional on removal).
- **Counter / administrator:** adjust a line's price, with a reason (comps, corrections).
- **Kitchen / administrator:** fulfill lines; revert fulfillments.
- Removal is terminal; re-adding is a new line. All mutations are all-or-nothing per event: if any operation in a send fails validation (line just fulfilled, item just deactivated), the whole batch is rejected with per-operation reasons and the guest restages against fresh state.

### 6.5 Tamper-evidence

Orders are **append-only event logs** with fully relational, typed operation tables — no JSON payloads, no entity-attribute-value (ADR-0002). Every event records who (person and role), what, and when. Current state is a projection (SQL views plus an equivalent pure fold in the domain layer, equivalence-tested). Nothing is ever updated or deleted in an order's history; corrections roll forward.

### 6.6 After close

Closed sittings are read-only, except that an **administrator** may append corrective events (staff edits, price adjustments, fulfillments, reversals — never guest submissions). The settled total stamped at close is never rewritten; corrections live visibly beside it.

### 6.7 History, hiding

Every person has full, unlimited access to their **own** order history across all visits — no retention limit, no automatic deletion. Cross-member history is never shown. A guest may **hide** an individual order from a closed sitting **from their own view**:

- Hiding removes it from that person's own view only — nothing is deleted, and no other party's view changes (kitchen/counter operational lookups and administration are unaffected).
- **There is no "show hidden" toggle for the user.** Once hidden, it is gone from their perspective, permanently, and the confirmation dialog says so.
- **Only an administrator can unhide**, via a dedicated hidden-records view listing every hidden order system-wide, filterable by username, date range, and table, showing the full, unprojected stored record per row, with unhide available per record. This view is a requirement, not an implementation nicety — without it, "admin can unhide" has no way to locate anything.
- Hiding and unhiding are themselves append-only visibility events.

### 6.8 Menu

Administrators create and edit menu items (name, price, active flag); kitchen, counter, and administrators may activate/deactivate ("86") items instantly. Menu changes are their own append-only event log. Guest adds re-validate item activeness inside the send transaction; prices are captured on the line at add time, so later menu price changes never move an existing line. Deactivating an item does **not** hide it from the guest menu: it remains visible, marked "currently unavailable", and unorderable — better for a guest to see the salmon is out than to watch it mysteriously vanish. Deactivation never touches existing orders. There is no inventory or stock tracking (§9); if an item runs out, staff deactivate it. Customization notes are free text and are never validated by any rules engine — an impossible request ("eggless omelette") is handled by a human walking to the table.

## 7. Kitchen alerting

### 7.1 Alerts

The kitchen display plays a **loud audible alert** (after a one-time user-gesture audio arm, with a persistent visual fallback badge) and updates instantly when new work arrives: every guest send, and any counter/administrator staff edit that adds or removes lines. The kitchen's own edits, price adjustments, and fulfillment actions are silent. The display requests a screen wake lock.

### 7.2 Reminder (revised for the batch model)

If, `KITCHEN_SUBMISSION_REMINDER_SECONDS` (default 60) after a guest send containing at least one added line, **none** of that send's added lines has been fulfilled or removed, the kitchen is alerted **once more** for that send. One reminder maximum per send; pure-removal sends alert once and never remind.

## 8. Cross-cutting principles

- **Naming:** long, unabbreviated, snake_case database names (`{table}_identifier` primary keys); no abbreviations anywhere in schema or code identifiers. **Carve-out:** industry-standard initialisms that are *more* recognizable than their expansions — TOTP, HMAC, QR, URL, SQL, TLS, API — are permitted and preferred over awkward expansions.
- **Identifiers:** application-generated UUIDv7 everywhere (ADR-0011).
- **Money:** `numeric(10,2)`; a single restaurant-wide currency code (`RESTAURANT_CURRENCY_CODE`, default `USD`) used for display only.
- **Time:** everything stored `timestamptz` UTC; rendered in `RESTAURANT_TIME_ZONE` (default `America/New_York`).
- **Configuration:** environment variables only, all prefixed and long-named (technical specification §13); sensible defaults for development; the app fails fast on invalid security-relevant configuration.
- **Observability:** OpenTelemetry everywhere; metrics named in full snake_case words (technical specification §12).
- **Honesty in UI:** removed lines render struck-through with actor and reason, price adjustments show old → new with reason, reversals stay visible. The interface never pretends history didn't happen.
- **Accessibility of operations:** `./run.sh` (dev) and `podman-compose up` (prod) must work from a fresh clone/host with no manual database steps.

## 9. Out of scope for version 1

Payments and card processing; tips accounting; inventory; reservations; multi-restaurant/multi-tenant anything; native mobile apps (the web app is the app; a native kitchen-display app remains a **conditional fallback only** — revisited if, and only if, the browser kiosk approach to audio and wake lock demonstrably fails in real kitchen conditions after being given a genuine chance, and never started speculatively); printing (receipts or otherwise); email/SMS (hence no self-service reset); per-line preparation-stage tracking beyond pending → fulfilled; loyalty/discount engines (price adjustment with a reason covers comps); analytics dashboards beyond the event explorer and OTLP metrics; Redis or any external message backplane (ADR-0006); horizontal scaling of the web tier.

## 10. Resolved design directives

Earlier revisions carried open directives; all are now resolved and embodied:

- *"Do NOT use entity attribute value; do not punt problems with JSON columns"* → typed operation tables with composite-FK subtype enforcement (ADR-0002, technical specification §8).
- *"ROBUST Argon2"* → Argon2id 64 MiB / t=3 / p=1 configurable, PHC strings, rehash-on-verify, concurrency cap, startup floor guard (ADR-0008).
- *"Cloudflare tunnels"* → named tunnel on the owner's stable domain is the production origin; quick tunnels are demo-only (passkeys die with the per-run subdomain — trycloudflare.com is on the Public Suffix List); accepted risk: in-house ordering hairpins through Cloudflare, so LAN ordering depends on WAN health (ADR-0005).
- *"GoTunnels as reference"* → adopted for tunnel/TLS/observability patterns only; its never-expiring localStorage bearer session is rejected as unfit for Blazor Server's cookie/circuit model (ADR-0005).
- Order model, TOTP policy, rotating join codes → ADR-0007, ADR-0010, ADR-0009 respectively, as summarized in §§4–7 above.

License: **AGPL-3.0-only** (`LICENSE`). Governance: single-owner project, no outside contributions (`CONTRIBUTING.md`); documentation changes are atomic — a behavior change lands with its requirement, specification, review-ledger, and ADR edits in one commit; ADRs are edited in place with a History line rather than duplicated.

---

## Revision history

- **Rev 2 — 2026-07-17.** Folded in rulings on F-01–F-33. Headline changes: one living order per guest per sitting with client-side staging, explicit batched sends, and a pending → fulfilled → (reversible) line lifecycle replacing order-level Acknowledged (§6, Q1); TOTP challenged on password sign-ins only, per-user admin TOTP toggle removed, reset wipes password+TOTP and forces re-enrollment via a post-auth obligations pipeline (§4.2/§4.5, Q2/Q3); rotating HMAC table-join tokens on paired `table_display` devices, printed static QR codes removed entirely, counter-screen fallback (§5.1, Q4/Q5); Cloudflare named tunnel fixed as production origin with the Public-Suffix-List passkey caveat and Argon2id hashing (§2/§10, F-06); four-experiences-plus-display phrasing (§1, F-21); abbreviation carve-out (§8, F-22); repository-generic paths (F-23); §10 retitled to resolved directives (F-24). The earlier "fulfilled ≈ served" ambiguity is resolved: fulfilled means prepared and dispatched; serving is untracked (§6.3). All rev-1 material untouched by a ruling carries forward unchanged — profile contact details and free-label address scaffolding (§4.6), owner-view hiding semantics with the admin hidden-records view (§6.7), deactivated-items-stay-visible (§6.8), absolute table-to-table privacy with live visibility revocation at close (§5.3), and the conditional (not cancelled) native kitchen-display fallback (§9).
- **Rev 1 — 2026-07-16.** Initial requirements.
