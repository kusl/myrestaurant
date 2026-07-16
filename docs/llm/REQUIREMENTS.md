# Restaurant Management System — Requirements

**Status:** Thought-experiment / pre-implementation. No code written yet.
**Scope:** Single restaurant per deployed instance. No multi-tenancy, no cross-instance awareness, no global system. Every restaurant is sovereign.

---

## 1. Purpose

A restaurant management system covering three physical roles — **table (customer)**, **kitchen**, and **counter (payment)** — built entirely on free/open-source components, self-hosted, with no dependency on paid external services (no payment processors, no SMS/email providers, no vendor-specific telemetry backends). 
However, it should be straightforward to use environment variables or .env files to store required stuff like 
`cd /home/kushal/src/dotnet/myrestaurant/; export UPTRACE_DSN="https://[redacted]@api.uptrace.dev?grpc=4317"; time bash run.sh`


Guests scan a per-table QR code, authenticate, browse the menu, and place orders. Kitchen sees orders live and prepares them. Counter closes out the table and settles the bill. All actions are transparent and auditable — nothing is silently overwritten.

---

## 2. Technology stack

| Concern | Choice |
|---|---|
| Language / runtime | C#, latest language features, .NET 10 (or latest stable at build time) |
| Web framework | ASP.NET Core, Blazor Server (single backend, live UI via SignalR — no separate push infra needed) |
| Data access | Dapper (no Entity Framework) |
| Database | PostgreSQL (latest stable) |
| Caching | Redis (or equivalent), containerized — optional infra component, not required for v1 correctness |
| Observability | OpenTelemetry (logs, metrics, spans — full end-to-end coverage). No vendor-specific packages. Optional Uptrace DSN via standard OTel env var configuration — this is just an OTel-compatible collector endpoint, not a special integration. |
| Orchestration (dev/prod) | Podman Compose, rootless. Runs on Fedora or Debian. |
| Containers | Containerfile (not Dockerfile) for every service, multi-stage, latest base images |
| CI | GitHub Actions — kept intentionally thin. All real logic lives in shell scripts invoked by the workflow, and those scripts drive Podman Compose. This keeps CI portable and reproducible locally. |
| Unit testing | xUnit v3 (latest) |
| Assertion style | Plain xUnit or AwesomeAssertions — no FluentAssertions, no Moq (avoid packages with restrictive/paid licensing) |
| Integration/E2E testing | Playwright, run inside a container (required on Fedora — no native Playwright support on the host) |
| Package/version management | Central Package Management via `Directory.Packages.props` / `Directory.Build.props` |
| Tunneling (demo) | `try.cloudflare.com` quick tunnels for fast iteration; setup/teardown shell scripts; must support multiple concurrent tunnels, properly scoped so they don't bleed into each other's environment |
| Tunneling (production) | Persistent named Cloudflare Tunnel (not the quick/temporary kind) — later phase, not v1 |
| App composition | Aspire may be used for local dev orchestration/observability wiring, latest version |
| Database migrations | DbUp, latest version, application to verify all migrations have applied and apply in sequence if necessary | 
| Database backups	| `pg_dump` shell script run by host timer; new file per backup; scheduled daily at configurable local time (`BACKUP_SCHEDULE_TIME`, default `06:00`); retains last `BACKUP_RETENTION_COUNT` files (default `7`, set in `.env`); `.env.example` generates `.env` with these defaults if missing; older backups auto-deleted on each successful new backup; bind-mounted host directory (not in Git)

**Non-goals for the stack:** no SQL Server, no EF Core, no Moq, no FluentAssertions, no vendor-locked telemetry SDKs, no native mobile app (see §7.4 for the one conditional exception), no external payment gateway, no external SMS/email sending service.

---

## 3. Applications (front ends)

One shared backend; the following are logically distinct experiences (could be distinct Blazor Server apps or distinct routed sections of one app — an implementation decision, not a requirements decision):

1. **Table (customer-facing)** — menu browsing, ordering, viewing table-mates' orders, personal order history, personal profile/account.
2. **Kitchen-facing** — live order queue, acknowledge orders, edit orders (tamper-evidently), deactivate/reactivate menu items.
3. **Counter-facing** — per-table billing view, close/settle sessions, edit orders (tamper-evidently), deactivate/reactivate menu items, password reset assistance.
4. **Admin** — full read/write authority; user and role management; TOTP policy overrides; unhide hidden records; menu management.

---

## 4. Identity & authentication

### 4.1 Credentials

- Two supported first-factor methods, offered as **alternatives**, not both required:
  - **Username + password**, using standard `autocomplete="username"` / `autocomplete="new-password"` form conventions so OS-level password managers on iOS/Android generate and offer strong passwords automatically.
  - **Username + passkey** (WebAuthn/FIDO2), which should work out of the box on modern iOS/Android/desktop browsers.
- **Passkeys are always offered, never required**, for ordinary guests. UI should nudge toward passkey enrollment (e.g., "want a faster way to sign in next time?") without blocking any flow.
- Usernames are **unique per instance only**. No instance is aware of any other instance. No global user directory exists anywhere in the system.

### 4.2 TOTP (two-factor)

- If a user has TOTP enabled, it is **required by default** on every login going forward.
- A user may remove/re-add their own TOTP at any time **while logged in** (e.g., lost authenticator app, wants to reconfigure).
- An **admin can independently toggle whether TOTP is required for a given non-admin user**. This is its own standalone admin action — it is not required to be bundled with a password reset, though a password reset is one common trigger for it (e.g., user is locked out because they lost their authenticator app and their password).
- TOTP recovery codes are supported.
- **Admin accounts always require TOTP.** This cannot be disabled by anyone, including another admin.

### 4.3 Admin accounts

- Admin is a **per-restaurant, per-instance role** — never global, matching the "every restaurant is sovereign" principle.
- The **first user created during initial setup automatically becomes the first admin.** This bootstrap path (`no admin exists yet → next registered/created user becomes admin`) is only available before any admin exists, and is permanently closed off afterward.
- **Admin accounts must have a passkey.** This is enforced when the passkey is registered / at admin-role grant time.
- If an admin's passkey is later lost/removed while they are already logged in and using the system, the system does **not** auto-demote them or lock them out — enforcement is at registration/grant time, not a continuous background check. (Deliberately no aggressive enforcement here — avoids a support nightmare during service hours.)
- Admins have full authority: create/read/update/delete on any user, role, menu item, order, or hidden record. Admins are the only role that can unhide hidden historical records (§6.5).
- Admin visibility of full database state: Where ordinary users see only current or visible state (e.g., current menu price, own visible order history), the admin-facing interface displays the complete record as stored — including append-only event histories, hidden records (§6.6), and price-change timelines. Any filtering or search controls are applied at the admin's explicit request; the underlying data shown is never projected or truncated for the admin role.

### 4.4 Role gating (kitchen / counter)

- **Kitchen role requires a passkey** on the account holding that role. Enforced at role-grant time.
- No TOTP requirement is imposed on kitchen/counter roles by default (TOTP for non-admins is opt-in / admin-toggleable, per §4.2) — the passkey-for-kitchen rule stands on its own.
- No continuous re-verification or auto-demotion if a credential is later removed while the session is active — same posture as admin (§4.3).

### 4.5 Password reset

- **Self-service:** any logged-in user can change their own password (requires knowing the current one).
- **Admin-initiated reset:** an admin can reset any user's password to a **randomly generated password**.
  - The affected user is **forced to change their password on next login.**
  - This reset is one of the triggers that can make TOTP optional for that user (see §4.2) — but is not the *only* way to toggle that flag.
- **Forgot password, not logged in, no self-service path:** because there is no automated email/SMS sending in v1, this always falls back to a human-mediated in-person/admin reset. There is no automated "forgot password" email flow.

### 4.6 Contact info

- Users may optionally store a phone number and/or email address on their profile.
- These are used for **manual escalation only** in v1 — e.g., an admin resets a password and then manually calls/texts/emails the new password to the user outside the system. They are not wired to any automated notification pipeline (no automated "your order is ready" texts, etc.) — building that would require a paid third-party sending service, which conflicts with the no-paid-dependency principle. This may be revisited later as an explicit, named future integration, not assumed as in-scope.

### 4.7 User profile page

- Every user has a profile page where they may (all optional):
  - Manage passkeys / password / TOTP.
  - Add phone number / email.
  - Add **addresses with free-text labels** (e.g., "Home," "Work," "Grandparents' house") — structured street/city/zip fields are a reasonable implementation detail, but the label is always free text chosen by the user. Not currently consumed by any other feature (no delivery/takeout flow exists yet) — this is intentionally scaffolding for a possible future feature, not dead weight to be removed, but also not to be treated as load-bearing for anything in v1.

---

## 5. Table / session model

### 5.1 QR codes and table identity

- Every physical table has its own QR code, encoding a stable **Table ID**.
- Scanning the QR code does **not** by itself create or join a session — it identifies which table the device is at and routes the guest into the sign-in flow (§4.1) for that table.
- The underlying Table ID is static — it is never rotated, regenerated, or invalidated by staff.
- When the QR code is scanned:
    - If no open TableSitting exists for that Table ID, the system creates a new TableSitting and routes the authenticated user into it.
    - If an open TableSitting exists, the scanning user is shown the existing session and may join it; the system does not create a concurrent second TableSitting for the same Table ID.
    - If a session was accidentally left open (e.g., a device left at the table), staff resolve it by closing the sitting at the counter (§5.4); there is no separate "reset" or "invalidate QR" action — only session closure.
- Operational assumption: a new party does not occupy the table until the previous TableSitting has been closed and the bill settled by counter staff (§5.4).

### 5.2 Table sitting vs. person session

- A **Table Sitting** represents one party occupying one table for one meal — the parent record that ties everything at that table together for that visit.
- Each person who signs in at that table creates their own **Person-at-Table** record (join between an authenticated `Person` and the current `TableSitting`).
- **Each person has their own individual order** — orders belong to the person, not to the table as an undifferentiated cart. There is no shared/merged cart across devices at a table.
- Because guest identity is now persistent (via passkey/password accounts) and repeat visits are expected, a `TableSitting` from one visit is fully independent of a `TableSitting` from another visit, even for the same returning `Person` at the same physical table.

### 5.3 Visibility during an active sitting

- **While a Table Sitting is open**, everyone signed in at that table can see everyone else's order **at that same table** (items, modifiers, running total) — full transparency within the party.
- Guests at **different tables** can never see each other's orders, regardless of session state. Table-to-table privacy is absolute.

### 5.4 Closing a sitting

- Only **counter** can close a Table Sitting.
- **Closing the sitting and marking the bill paid happen atomically as a single action.** There is no "closed but unpaid" state and no separate walkout/comp flow in v1 — that is explicitly out of scope for now, not silently unsupported-but-assumed-fine.
- The moment a sitting closes/is paid, the table-wide visibility from §5.3 ends immediately for everyone still connected (this is a live permission change pushed over the same SignalR connection that drives the rest of the UI — not just a rule enforced on next page load). From that point forward, each person can only see their own order in their personal history.
- Counter staff are responsible for closing the sitting after the bill is settled; this is the only mechanism that frees the table for the next TableSitting. No automated timeout or rotation is used, except at the End of Business Day, when counter staff may perform a batch closure of all remaining open TableSitting records; each such closure is still logged individually as a normal close event.

### 5.5 Post-close visibility

- After closing, each person's order becomes part of their **personal order history** (§6.6), visible only to them (plus admin, plus kitchen/counter in their normal operational capacity while relevant — see §6).

---

## 6. Menu, ordering, and the order lifecycle

### 6.1 Menu items

- Every menu item has a name, description (optional), and a **displayed price**.
- Menu items have an **active/inactive** flag, settable by kitchen, counter, or admin.
- **Deactivating an item does not remove it from any existing order** that already references it — history is immutable in that sense.
- **Deactivating an item does not hide it from the menu view** — it remains visible, but guests cannot select/order it while inactive. (This is a deliberate choice: better for a guest to see "Salmon — currently unavailable" than for the item to mysteriously vanish.)

### 6.2 Inventory — explicitly out of scope for v1

- There is **no inventory tracking or stock-decrement system.** All active menu items are assumed to be orderable in unlimited quantity.
- If an item genuinely runs out, kitchen/counter deactivate it (§6.1).
- If a specific customization can't be honored (e.g., "eggless omelette," "gold foil sushi"), the system does not attempt to model or validate this — a staff member walks to the table and tells them in person. Free-text customization requests are not validated against any rules engine.

### 6.3 Placing an order

- A person adds items (with optional free-text customization notes) from the active menu and submits an order.
- **Price is locked in at order time** — the order stores the price as it was when submitted, independent of any later menu price changes. This locked-in price is itself editable later by counter under the same tamper-evident rules as everything else (§6.5).

### 6.4 Order state and the kitchen workflow

There is **no multi-step "order tracker"** (no submitted → in-prep → ready granularity). The only kitchen-relevant states are:

1. **Submitted** — order placed by the guest, visible to kitchen, timer starts.
2. **Acknowledged** — kitchen has tapped/clicked to acknowledge the order. This is the single kitchen action; there is no further per-item status tracking.

"Served" is **not tracked by the system at all** — it is handled by physical reality (food goes out, staff and guests both know it happened). No "mark as served" feature should be added later without recognizing this is a deliberate simplification, not an oversight.

### 6.5 Editing orders — tamper-evidence

- **Kitchen and counter can both edit orders** (add/remove items, adjust the locked-in price, etc.) after submission.
- All such changes must be **tamper-evident**: it must always be obvious *who* changed *what* and *when*.
- Implementation implication (not a code decision here, but a hard constraint on the data model): this requires an **append-only event log per order** — every change is a new event referencing the previous state, never an in-place mutation of a "current" row with no history. "Current state" is a read-model/projection over this event stream, not the source of truth.
- Edits only ever **roll forward** — there is no true "undo"/delete of a prior event. Correcting a mistake means adding a new event that supersedes the old one, and both remain visible in the order's history.
- **Guests see kitchen/counter edits to their order in real time** (same live-UI mechanism as everything else) — e.g., if kitchen removes an item because it's unavailable, the guest's screen updates immediately reflecting the change and who made it.

### 6.6 Order history and hiding

- Every user can view their own **full, unlimited order history** across all their visits to this restaurant instance. There is no retention limit or automatic deletion.
- A user may **hide** an individual historical order from their own view.
  - Hiding removes it from that user's own view only — it is not deleted from the system, and no other party's view of it changes.
  - **There is no "show hidden" toggle for the user** — once hidden, it is gone from their perspective, permanently, from their own point of view.
  - **Only an admin can unhide** a hidden record, via an admin-facing view listing hidden records system-wide. (This view needs to exist and be usable — e.g., filterable by user/date/table — otherwise "admin can unhide" has no way to actually locate the thing to unhide. Exact filtering/search UX is an implementation detail, but the view itself is a requirement, not optional.)
  - Admin hidden-records view: The view must display every hidden historical order as it exists in the database. It must be filterable and searchable by user identifier, date range, and table identifier, so the admin can locate a specific record to unhide it. The view shows the full, unprojected database state of the record (not a summary); unhide is available per record.
---

## 7. Notifications

### 7.1 Mechanism

- Live updates (new orders, acknowledgments, edits, visibility changes at sitting-close) are delivered via **Blazor Server's built-in SignalR circuit** — this is sufficient for push-style delivery and does not require a separate push notification system, service workers, or the Web Push API.

### 7.2 Kitchen alerting

- When a guest submits an order, the kitchen display must produce a **loud, attention-getting notification** (audio, not just a silent UI update) — kitchen staff are not assumed to be staring at a screen.
- If an order is **not acknowledged within 60 seconds**, the system **re-notifies once** (repeats the same alert). There is **no escalation** (no increasing volume, no alternate channel, no repeated re-notification beyond this) — deliberately kept simple.

### 7.3 Browser constraints on "loud"

Reliable audio playback in a browser is non-trivial: browsers restrict autoplay without a prior user gesture, and a backgrounded/idle tab may be throttled or the screen may sleep. The intended approach:

- A dedicated, always-on kitchen display (tablet or PC) running the kitchen-facing app in kiosk mode.
- A one-time "arm audio" user gesture at the start of a shift (tap to acknowledge sound is enabled) to satisfy browser autoplay restrictions for the rest of the session.
- Screen Wake Lock API (or OS-level kiosk settings) to prevent the display from sleeping.
- Clear reconnect handling/UI if the SignalR circuit drops, so a missed connection is visibly obvious to staff rather than silently swallowed.

### 7.4 Native app — conditional fallback, not a v1 commitment

If browser-based kiosk-mode audio/wake-lock proves unreliable in real kitchen conditions, a native kitchen-display app is an acceptable fallback to revisit later. It is **not** part of v1 scope, and should not be started speculatively — the browser-based approach above should be tried first and given a real chance to fail before native is pursued.

---

## 8. Cross-cutting principles carried in from prior team practice

These aren't new to this project but apply here as much as anywhere:

- **Environment-agnostic configuration** — no environment-specific values hardcoded anywhere; derive at runtime or inject via CI/CD secrets, since (even though this project has no multi-tenancy) the same codebase/image may still be deployed to different restaurant instances with different config.
- **No dependencies that require payment for any use.**
- **Full names, no abbreviations**, in code, CLI commands, and config keys.
- **Spec-first development** — this document exists so implementation can be checked against explicit decisions rather than inferred assumptions.
- **Atomic documentation and architectural decision records**: Any code change must be accompanied by the corresponding update to this requirements document and to any affected architectural decision record in the `docs/adr` folder, all within the same prompt or commit. The documentation is never updated separately or left behind; it moves atomically with the code it describes. If a decision changes, the existing ADR in `docs/adr` is edited (not duplicated) and the requirement here is updated to match. No implementation is considered complete until both the code and its documentation (requirements plus `docs/adr` records) are consistent.

---

## 9. Explicitly out of scope for v1 (do not build speculatively)

- Inventory/stock tracking (§6.2).
- Multi-step kitchen order tracking / "pizza tracker" UI (§6.4).
- Automated email/SMS sending of any kind (§4.5, §4.6) — contact info is manual-escalation-only.
- External payment gateway integration — the counter produces a bill/summary; actual payment handling is outside the system.
- Walkout / comp / "closed but unpaid" session states (§5.4).
- Cross-instance or multi-tenant anything — every instance is fully sovereign and unaware of every other instance.
- Native mobile app — only a conditional future fallback for kitchen display reliability (§7.4), not a general-purpose app.
- Delivery/takeout using the address book (§4.7) — the address book exists as profile scaffolding only; nothing currently consumes it.

---

## 10. Open items for the next design pass (not yet decided)

These were identified during discussion but intentionally deferred — flagged here so they aren't lost:

- Exact entity/table schema for `Person`, `TableSitting`, `PersonAtTable`, `Order`, `OrderEvent`, `MenuItem`.
  I want the LLM to come up with a good normalized table structure, do NOT use entity attribute value to punt problems, do use uuidv7 when using guid
- Resolved: Admin hidden-records view and full-state visibility rules (§4.3, §6.6).
