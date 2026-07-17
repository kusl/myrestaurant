# Documentation Review — Findings and Resolutions

**Status:** Resolved — nothing open.
**Scope of the original review (2026-07-16):** `REQUIREMENTS.md` rev 1 (commit `af51017`) and the `dump.txt` produced by `export.sh`.
**Rulings:** the owner ruled on every finding on 2026-07-17 — F-01–05, F-09–11, F-13–19, F-21–24 accepted as raised; F-06, F-07 (Q1), F-08 (Q2/Q3), and F-12 (Q4/Q5) resolved with the defaults proposed in the ruling exchange ("all of this is fine").

This revision converts the finding-by-finding prose into a disposition ledger. The full original finding texts are preserved in this file's prior revision in git history. **How to read a row:** the finding in one line → the ruling → where it is embodied. `R§` = `REQUIREMENTS.md` rev 2, `S§` = `TECHNICAL_SPECIFICATION.md` v1.0, `O§` = `OPERATIONS.md`, `ADR-nnnn` = `docs/adr/`.

---

## Group A — Contradictions between requirement sections (all ruled)

| ID | Finding | Ruling | Embodied in |
|---|---|---|---|
| F-01 | §3.3 listed "password reset assistance" among counter duties while §4.5 made reset administrator-only | Reset capability is **administrator-only**. Counter "assistance" is procedural: identify the guest in person, relay to an administrator. Every reset lands in `security_event`. | R§4.5 · S§3.5, S§3.7 |
| F-02 | §6.3 named counter as the price-adjusting role; §6.5 granted "kitchen and counter" price edits | Composition edits (add/remove lines): kitchen + counter + administrator. **Price adjustment: counter + administrator only** — money stays with money-handling roles. Enforced by same-row `CHECK` constraints, not just UI. | R§6.4 · S§3.7, S§6, S§8 |
| F-03 | The first registered user is auto-granted administrator, but admins must hold a passkey and TOTP *at grant time* — impossible for a brand-new account | `/setup` bootstrap wizard: create account → register passkey → enroll TOTP → grant administrator, in **one guarded transaction** (advisory lock re-checks "zero admins" under lock); the path returns 404 forever once any administrator exists. | R§4.4 · S§3.6 |
| F-04 | §5.4 said "only counter can close and settle"; §4.3 gave administrators full authority over everything | Counter **or** administrator may close and settle. | R§5.4 · S§3.7, S§5 |
| F-05 | The §4.4 heading covered kitchen/counter gating but imposed a passkey only on kitchen — counter, which handles money, had no credential precondition | Accepted **as literally written**: counter carries no grant-time credential mandate. Recorded as an accepted risk. | S§3.7, S§17 |

## Group B — Gaps and ambiguities

| ID | Finding | Ruling / resolution | Embodied in |
|---|---|---|---|
| F-06 | TLS / secure contexts never addressed; WebAuthn, Wake Lock, and reliable audio all need HTTPS, and quick tunnels get a random hostname per run — passkeys registered through one are dead on the next | **Named Cloudflare tunnel on the owner's stable domain is the production origin.** Verified sharp edge: `trycloudflare.com` is on the Public Suffix List, so a quick-tunnel passkey binds to the per-run subdomain and dies with it — quick tunnels are **demo-only, password+TOTP**. The owner's "ROBUST Argon2" directive became a custom Argon2id `IPasswordHasher`. Accepted risk: in-house traffic hairpins through Cloudflare, so LAN ordering depends on WAN health. GoTunnels adopted as tunnel/TLS/observability *reference only*; its never-expiring localStorage bearer session is rejected as unfit for Blazor Server's cookie/circuit model. | R§2, R§10 · S§3.2, S§3.3, S§14, S§17 · ADR-0005, ADR-0008 · O§2, O§10 |
| F-07 | "Their own individual order" (singular) didn't model multi-round dining | **Q1 ruling** (supersedes the draft order-per-submission interpretation): **one living order per guest per sitting.** Edits are staged client-side and sent explicitly; each send is one batch event and one kitchen alert (never per-keystroke); lines carry a pending → fulfilled lifecycle (with a visible reversal for kitchen mistakes) replacing order-level Acknowledged; guests may remove only their own not-yet-fulfilled lines. | R§6 · S§6, S§10 · ADR-0007, ADR-0002 |
| F-08 | "TOTP enabled" and "TOTP required" conflated — two independent facts treated as one | **Q2/Q3 ruling:** TOTP is challenged **on password sign-ins only**, never after a passkey (the authenticator is already a second factor). The per-user admin "require TOTP" toggle disappears — requiredness collapses to enrollment. Admin reset wipes the password and, **if enrolled**, the TOTP secret + recovery codes; the next sign-in (any path) runs the post-authentication obligations pipeline: forced password change, then forced TOTP re-enrollment. | R§4.2, R§4.5 · S§3.4, S§3.5 · ADR-0010 |
| F-09 | A literal reading forced a TOTP challenge even after a passkey sign-in — flagged as unusual UX pending confirmation | **Superseded by the Q2 ruling**, which lands where the finding pointed: the passkey path never receives a TOTP challenge. ADR-0010's History records the supersession explicitly. | S§3.5 · ADR-0010 |
| F-10 | Post-close edit window undefined (tamper-evident editing vs atomic settlement) | Orders are **read-only after close**, except administrators may append corrective events — each itself logged. `settled_total_amount` is **never rewritten**, so any post-close correction is visibly divergent rather than silently reconciled. | R§6.6 · S§5, S§6.7 |
| F-10b | "Delete any user" would orphan the append-only history | Deletion is **deactivation** (`is_active = false`, sign-in blocked, sessions revoked via security stamp). Rows are never removed; hard erasure is explicitly out of scope for version 1. | S§3.7 |
| F-11 | Attribution granularity toward guests unstated — do guests see staff usernames? | Guests see the **role** ("kitchen removed Salmon — unavailable"); staff and administration views show full actor identity; complete identity is always in the stored log. | R§6.5 · S§6, S§11.1 |
| F-12 | The static, never-rotated table QR was a permanent capability token — one photograph grants indefinite remote access to that table's sittings | **Q4/Q5 ruling:** printed static QR codes are **gone entirely** (not kept as a flagged fallback). Each table holds a server-side 32-byte join secret; a paired `table_display` device shows a rotating 60-second HMAC token (current + previous window accepted, ≤120 s validity); a 10-minute join grant survives slow registration; the counter renders the same rotating QR on its own screen if a display dies; administrators rotate a table's join secret at will. | R§5.1 · S§4, S§11.5 · ADR-0009 · O§5 |
| F-13 | Redis had no version-1 consumer | Excluded from the v1 stack; documented as the future SignalR backplane/cache with explicit trigger conditions. | R§2 · ADR-0006 |
| F-14 | Aspire and Podman Compose named without a hierarchy | **Compose is canonical** for dev, CI, and prod; an Aspire AppHost may exist as optional developer convenience that nothing may require. | R§2 · S§14.5 · ADR-0004 |
| F-15 | `UPTRACE_DSN` is not a "standard OpenTelemetry environment variable" | The application reads **only `OTEL_*`**; `run.sh` performs a courtesy translation when `UPTRACE_DSN` is set. Any OTLP-compatible collector works identically. | S§12, S§13, S§14.4 |
| F-16 | Backup mechanics underspecified: client/server version match, "`.env.example` generates `.env`", timer rendering | `pg_dump -Fc` runs **inside the postgres container** via `podman exec` (version match guaranteed); `.env` is copied from `.env.example` when absent; the schedule renders from `BACKUP_SCHEDULE_TIME`; retention prunes only after a successful new dump. The Data Protection keys volume is backed up alongside the database. | S§15 · O§6, O§8 |
| F-17 | `run.sh` invoked in the quick start but never defined | Defined: ensure environment → translate `UPTRACE_DSN` if present → start the stack → health wait → developer watch/URLs. Idempotent. | S§14.4 |
| F-18 | Admins were promised "price-change timelines" but nothing mandated recording them | Append-only `menu_item_event` written **in the same transaction** as every `menu_item` mutation, with typed old/new columns. | R§6.8 · S§7, S§8 |
| F-19 | Currency, time zone, username policy, lockout, and anonymity all silent | `RESTAURANT_CURRENCY_CODE` (ISO 4217, display only); `RESTAURANT_TIME_ZONE`; case-insensitive `citext` usernames 3–64 characters; lockout 5 failures / 5 minutes; **no anonymous ordering** — every guest authenticates. | R§4.1, R§8 · S§3, S§13 |
| F-20 | Moq-exclusion rationale factually off: Moq is free (BSD) — the community concern was the 2023 SponsorLink telemetry incident; FluentAssertions v8 is the paid one | Bans stand; the stated *reason* corrected. Hand-written fakes preferred; NSubstitute (BSD-3) permitted where a substitute genuinely helps. Accepted by default — the owner's ruling enumeration skipped F-20, and the blanket approval covers spec-chosen defaults of this kind. | R§2 · S§16.1 |

## Group C — Editorial issues (applied in `REQUIREMENTS.md` rev 2)

| ID | Finding | Resolution | Embodied in |
|---|---|---|---|
| F-21 | "Three physical roles" vs four applications read as a counting error | Reworded: three physical stations (table, kitchen, counter) plus administration — a role, not a place — plus the table-display device surface: five areas of one application. | R§1, R§3 |
| F-22 | The no-abbreviations rule collided with mandated initialisms (`TOTP`, `QR`, `OTEL_*`, …) | Explicit carve-out for industry-standard initialisms more recognizable than their expansions; `id` remains an abbreviation — the project word is `identifier`. | R§8 |
| F-23 | A hardcoded personal path in the §1 example contradicted §8's "nothing hardcoded" | Example genericized to repository-relative form. | R§1 |
| F-24 | A "Resolved:" bullet sat inside the "not yet decided" list, falsifying the list's contract | §10 retitled **"Resolved design directives"**; every entry there is now genuinely resolved and points at its embodiment. | R§10 |

## Group D — `dump.txt` / `export.sh` defects (all remediated)

All nine are fixed in the current `export.sh`; the script's own comments cite these IDs at each fix site, and the current dump demonstrates the fixes (single self-emission, single tree root, correct pluralization, `REQUIREMENTS.md` present).

| ID | Finding | Fix in the current script |
|---|---|---|
| F-25 | The script emitted itself twice (self-documentation section *and* the tracked-file loop) | The loop excludes the script **by whatever name it currently has** (`EXCLUDED_FILES=("$SCRIPT_NAME")`), so a rename can never reintroduce the double emission. |
| F-26 | `REQUIREMENTS.md` was absent from the dump — untracked or hidden under the excluded `docs/llm/` path | Requirements tracked at `docs/REQUIREMENTS.md`; `docs/llm/` is reserved for *generated* output only. The file appears in the current dump. |
| F-27 | `exec > >(tee …)` could reach the rename and exit while `tee` was still draining — a reader could observe a truncated dump | Plain redirection into a temp file, atomic `mv`, and only then a `cat` of the final file; stderr never enters the dump. |
| F-28 | Doubled root line in the FILE TREE section | The heredoc header no longer prints its own `.`; the tree renderer prints exactly one. |
| F-29 | Redundant `&>/dev/null 2>&1` | Trailing `2>&1` dropped; `&>` already covers both streams. |
| F-30 | Unchecked `bc` dependency killed the entire dump under `set -e` on machines without it | `human_size` uses `awk`, which `file_sha256` already required. |
| F-31 | Fallback tree renderer used `└──`/`├──` by node kind instead of by position | Correct connectors and `│` continuation lines rendered by position. |
| F-32 | `yarn.lock` leftover in a .NET repository's exclusion list | Removed. |
| F-33 | "all 1 included files" | Singular/plural noun selected from the count. |

---

## Going forward

No findings remain open. New issues enter this ledger as fresh `F-nn` rows — finding, ruling, embodiment — landed in the **same commit** as the change they describe, together with the matching `REQUIREMENTS.md`, specification, and ADR edits (atomic documentation, R§10 · S§18).
