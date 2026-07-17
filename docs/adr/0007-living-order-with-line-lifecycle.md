# ADR-0007 — One living order per guest per sitting; batched sends; line-level lifecycle

**Status:** Accepted (2026-07-17) — F-07 / Q1 ruling
**Finding trail:** F-07
**Requirements:** `REQUIREMENTS.md` §5.2, §6.3–§6.5, §7.2

## Context

The requirements originally said each person has "their own individual order" (singular), which failed to model multi-round dining. The first draft resolution made every submission a separate order with an order-level Submitted → Acknowledged lifecycle and forbade guests from ever editing after submission. The owner ruled against that model in favor of a living order.

## Decision

1. **Cardinality:** exactly **one living `guest_order` per (person, sitting)**, enforced by `UNIQUE (table_sitting_identifier, person_identifier)`. The row is created lazily inside the transaction of the guest's first send; a concurrent-first-send race loses on the unique constraint and retries as a read.
2. **Guest editing is staged, then sent explicitly.** Guests build changes client-side (circuit state, nothing persisted): line additions (menu item, quantity 1–100, optional free-text customization note) and removals of their own currently-pending lines. Pressing **Send** commits the staged set as **one** `guest_submission` event carrying all its operations — one batch event, one kitchen alert. There are no per-keystroke events and no per-change alerts, by explicit ruling, to avoid alert fatigue.
3. **Line lifecycle replaces order-level acknowledgment.** Every added line is **pending** until kitchen (or an administrator) marks it **fulfilled** — meaning prepared and sent out — or a permitted actor **removes** it. There is no order-level Acknowledged state, no "in preparation" step, and no further granularity; "served" remains untracked beyond fulfillment.
4. **Guest removal is limited to their own pending lines.** Fulfilled lines cannot be removed by the guest (the food exists); removed lines are terminal (re-adding is a new line with a new identifier). Staff removal and price adjustment rules are in the capability matrix (technical specification §3.7).
5. **Fulfillment is reversible by roll-forward.** A mistapped fulfillment is corrected by a `fulfillment_reversal` event (kitchen or administrator), returning the line to pending. Both events remain visible. This exists because guests' removal rights hinge on fulfillment state, so a mistap must be honestly correctable rather than papered over.
6. **All-or-nothing validation.** Every operation in a send is validated inside one serialized transaction (per-order `SELECT … FOR UPDATE`); if any operation fails (line just fulfilled, menu item just deactivated), the whole batch is rejected with per-operation reasons and the client restages against fresh state. A removal may not reference a line added in the same event.

## Consequences

- The guest's screen shows one coherent, evolving order per visit — matching how people actually dine — with live pending/fulfilled/removed states per line.
- The kitchen alert unit is the send (batch), with a single reminder rule defined in the technical specification §10; the previous order-level acknowledge timer is gone.
- The bill is the sum over non-removed lines at their latest price, regardless of fulfillment state; counter reviews still-pending lines before closing (technical specification §5.4).
- The event model gains multi-operation events, handled by ADR-0002's typed operation tables.

## History

- 2026-07-16 — drafted as "order-per-submission, guests never edit, order-level Submitted → Acknowledged".
- 2026-07-17 — **superseded in place** by the owner's Q1 ruling: living order, batched sends, line-level pending → fulfilled lifecycle, guest removal of pending lines, fulfillment reversal.
