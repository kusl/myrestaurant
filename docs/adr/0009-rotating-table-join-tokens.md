# ADR-0009 — Rotating HMAC table-join tokens on paired table displays; no printed QR codes

**Status:** Accepted (2026-07-17) — F-12 / Q4 / Q5 rulings
**Finding trail:** F-12
**Requirements:** `REQUIREMENTS.md` §3, §5.1

## Context

The original design put a **static** printed QR code on each table. A static code is a permanent capability: photograph it once and you can join that table's sitting from anywhere, forever. The owner ruled for rotating codes on cheap per-table display devices, with the counter able to show the current code if a display dies, and ruled (Q5) that printed static QR codes are **gone entirely** — not kept as a documented fallback.

## Decision

### Token construction

Each `restaurant_table` row holds a `join_secret` — 32 random bytes generated server-side, **never leaving the server** (displays receive a server-rendered QR image over their Blazor circuit, not the secret). The join token for a table at any instant is:

```
window_index = floor(unix_time_seconds / TABLE_JOIN_TOKEN_ROTATION_SECONDS)   -- default 60
message      = UTF8( lowercase-hyphenated-table-uuid + ":" + decimal(window_index) )
token        = Base64Url( HMAC_SHA256( join_secret, message ) )               -- full 32 bytes, no padding
```

The QR encodes `{RESTAURANT_PUBLIC_ORIGIN}/table/{table_identifier}?token={token}`.

### Validation

The server recomputes the HMAC for the **current and previous** window indices and compares each against the presented token with `CryptographicOperations.FixedTimeEquals`. Acceptance of two windows gives a worst-case token lifetime of `2 × rotation` (default 120 s), so a code never dies in a guest's hand mid-scan. Rotation runs continuously and is **independent of sitting state** — a table with no open sitting still rotates; scanning it creates the sitting through the normal flow. Validation results are counted in the `table_join_tokens_validated_total{result=valid|expired|invalid}` metric.

### Join grant

A guest who scans may still need minutes to register (passkey ceremony, choosing a display name) — far longer than 120 s. So a **valid token is exchanged immediately** for a *join grant*: a Data-Protection-encrypted cookie containing `{table_identifier, issued_at}` with a `TABLE_JOIN_GRANT_MINUTES` TTL (default 10). Authentication/registration proceeds; the actual join **consumes the grant**, not the original token. Existing members of the table's open sitting bypass tokens entirely — `/table/{id}` recognizes membership and renders the order surface.

### Display devices

A new principal kind, `table_display`, carried by a **device**, not a person account (`person_role` stays `administrator|kitchen|counter`):

- `table_display_device` — bound to one table; authenticated by a long-lived cookie whose 256-bit secret is stored **hashed (SHA-256)** server-side; `revoked_at` kills it on next request; `last_seen_at` updated at most once per minute.
- Pairing: an administrator generates a one-time 8-character code (unambiguous alphabet, stored hashed, `TABLE_DISPLAY_PAIRING_CODE_MINUTES` TTL, single-use). The device visits the anonymous, rate-limited (5/min/IP) `/display/pair`, enters the code, and receives its device cookie (Secure, HttpOnly, SameSite=Lax, ~365 d).
- `/display/{table}` renders full-screen: table label, the rotating QR (re-rendered server-side on a timer aligned to window boundaries), connection state, and party size when a sitting is open. Wake lock requested as on the kitchen display.

### Fallback

If a display dies, **counter and administrator surfaces can render the same rotating QR** for any table on demand; the guest scans the staff screen. There is no printed QR anywhere, and no human-readable short-code path in v1 (reading a Base64Url HMAC aloud is not a flow).

## Consequences

- Photographing a table code is now worth at most ~120 seconds of access from anywhere; afterwards the photo is inert. Admins can additionally rotate a table's `join_secret` at any time (stolen/cloned device paranoia), instantly invalidating all outstanding windows for that table.
- The restaurant buys one cheap device per table (any old tablet in kiosk mode suffices — see `OPERATIONS.md` §5). This is the accepted cost of eliminating static capabilities.
- A stolen display shows only the public rotating QR; it holds no secret worth extracting. Revocation removes even that.
- Residual risk, accepted: within the ≤120 s window a token is replayable by anyone who obtains it; the sitting-membership and visibility rules bound what a hostile joiner can see or do.

## History

- 2026-07-16 — drafted with static printed QR codes per table.
- 2026-07-17 — **superseded in place** by the F-12/Q4/Q5 ruling: rotating HMAC tokens, paired display devices, counter-screen fallback, printed codes removed entirely.
