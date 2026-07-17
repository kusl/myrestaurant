# ADR-0002 — Relational append-only event logs with typed operation tables

**Status:** Accepted (2026-07-17)
**Finding trail:** F-07 (revised model), F-10, F-18; explicit owner directive in `REQUIREMENTS.md` §10 ("do NOT use entity attribute value")
**Requirements:** `REQUIREMENTS.md` §6.5, §6.6

## Context

Tamper-evidence is a hard requirement: it must always be obvious who changed what and when, and corrections must roll forward rather than erase. The owner explicitly forbade entity-attribute-value modelling and JSON payload columns as ways to "punt problems". The order model (per the F-07 ruling, ADR-0007) is a batch model: one guest send may carry several line additions and removals, and must land as **one** event.

## Decision

Every order mutation is an append-only `order_event` row. An event carries identity (`actor_person_identifier`, `actor_role`), a per-order `sequence_number`, and an `event_type`. The event's payload lives in **typed operation tables** — one table per operation kind, fully relational, no JSON, no EAV:

- `order_operation_line_added`
- `order_operation_line_removed`
- `order_operation_line_price_adjusted`
- `order_operation_line_fulfilled`
- `order_operation_line_fulfillment_reverted`

One event may own **multiple** operation rows (a guest batch), or exactly one (a typical staff action). Which operation kinds an event type may carry is enforced in the database with the composite foreign key pattern: `order_event` carries `UNIQUE (order_event_identifier, event_type)`, every operation table stores a redundant `event_type` column constrained by `CHECK` to the permitted set, and its foreign key references the composite `(order_event_identifier, event_type)`. A `fulfillment` event therefore physically cannot own a `line_added` row.

Same-row `CHECK` constraints on `order_event` bind event types to permitted actor roles (for example `price_adjustment` requires `actor_role IN ('counter','administrator')`).

Current state is a projection: SQL views (`order_current_line`, `order_current_state`, `sitting_bill`, and friends) plus an equivalent pure fold in `MyRestaurant.Domain` (`OrderProjection.FromEvents`). The views and the fold must agree; the equivalence is asserted by integration tests. Neither projection is ever the source of truth.

The same pattern (append-only, typed columns, same-transaction write) applies to `menu_item_event` (F-18) and, in simpler single-row form, to `order_visibility_event` and `security_event`.

## Consequences

- History is complete and structurally honest: a removed line's addition, removal, price adjustments, and fulfillment flips all remain visible forever.
- Removal is terminal at the database level (`UNIQUE (order_line_identifier)` on the removal table): a line can be removed at most once, and re-adding means a new line with a new identifier.
- Cross-table shape rules the database cannot express (an event must own at least one operation; a referenced line must belong to the same order; fulfilled/reverted must alternate) are application-enforced inside the serialized order transaction and covered by integration tests. These invariants are listed in the technical specification §6.
- Queries pay a projection cost. At single-restaurant volume this is negligible; the views are indexed via the underlying operation-table indexes.

## History

- 2026-07-16 — drafted around single-operation events (one child row per event).
- 2026-07-17 — accepted in batch form after the F-07 ruling replaced order-per-submission with the living-order model (ADR-0007).
