# ADR-0014 — A menu has sections, and every item is in exactly one

**Status:** Accepted (2026-08-11), implemented in Stage 2 of `docs/MENU_AND_HANDHELD_PLAN.md`
**Trail:** the first enhancement request from a person shown the running application
**Requirements:** `REQUIREMENTS.md` §6.8 (menu), §8 (naming, identifiers, honesty in UI)
**Specification:** `TECHNICAL_SPECIFICATION.md` §7, §8.2, §11.1, §11.2, §11.4
**Related:** ADR-0002 (relational append-only event logs), ADR-0011 (application-generated UUIDv7),
ADR-0012 (DbUp migrations at startup)

## Context

`menu_item` has been four columns since M1: `name`, `price_amount`, `is_active`, `created_at`. That was
enough for §6.5.4 to price a line and for §11.2 to list what to cook, and it is not a menu. A menu has
headings, and each item under a heading has a sentence explaining it — which is not decoration: it is how a
guest with no waiter standing over them decides between two things they have not eaten.

The gap was invisible from inside the project because every reading surface had adapted to it. The guest
picker (§11.1) is one `<select>` with every item flattened into it and the price concatenated onto the
label; the kitchen's 86 panel is a flat `<ul>`; the administration index orders by name, and §7 records why
that is *permitted* — `menu_item.name` carries no UNIQUE constraint, so a rotating "Soup" sorts next to the
other one. None of that is wrong. All of it is what a correct implementation of a model that cannot express
a menu looks like.

The application has not gone into production, so a schema change here costs one additive migration and
nothing else. That will not be true again.

## Decision

**1. `menu_section` is a table, and `menu_item.menu_section_identifier` is `NOT NULL`.**

Every item is in exactly one section. The alternative — a nullable column with an "Uncategorized" bucket
rendered when it is null — puts a second branch on every reading surface, forever, for a state that exists
only because the schema allowed it. An item under no heading is an item nobody decided about, and the
honest place to refuse that is the column.

The cost is real and is accepted: on a database with no sections, no menu item can be created. That is one
extra step on the first ever use of the menu screen, and the create-item form's job is to say so and link to
section creation rather than to fail.

**2. `menu_section.name` is `citext NOT NULL UNIQUE`, and `menu_item.name` stays neither.**

The asymmetry is deliberate and it is the interesting half of this decision. §7's existing ruling is that a
duplicate item name is a real menu — a special that changes weekly is genuinely two rows called "Soup", and
a constraint inventing uniqueness would be the data-access layer overruling the schema of record. A
duplicate *section* is never a real menu: it is a mis-tap whose consequence a guest sees as the same heading
twice with the items split arbitrarily between them. `citext` rather than `text` because "drinks" and
"Drinks" are the same mis-tap.

**3. Ordering is an explicit non-unique integer, on both the section and the item.**

`ORDER BY name` cannot express "Fries" before "Truffle Fries", and that ordering is a decision somebody
made about their own menu. `display_order integer NOT NULL`, not UNIQUE, with reads ordering by
`(display_order, name, identifier)` so ties are stable rather than arbitrary. Not unique because a unique
ordering column has to be rewritten in two phases or behind a deferred constraint, and the largest menu this
system is designed for has perhaps sixty items on it (R§1: "tens of tables, not thousands").

**4. `description` is `text NOT NULL DEFAULT ''`, on both, and empty means absent.**

This is the one place the decision was forced by a constraint rather than chosen. §7's event log ties each
nullable payload column to exactly the event types that carry it, with paired equality CHECKs — the pattern
`(new_name IS NOT NULL) = (event_type IN ('created', 'name_changed'))` that `0001` established. An
*optional* payload cannot be tied that way: clearing a description has to write something, and if that
something is NULL the CHECK is violated by the very event that records the clearing. With `''` as "none",
clearing is a value like any other and the constraint stays total.

This project carries both idioms — `person.display_name` is nullable and read through
`NULLIF(btrim(…), '')` — so there was no house rule to follow. The tie-breaker is the constraint, and it
only exists because the log exists.

**5. The append-only log extends rather than forks.** `menu_item_event` gains `description_changed`,
`section_changed` and `reordered`; `menu_section` gets its own `menu_section_event` with
`created | renamed | described | reordered | activated | deactivated`. Every mutation still writes its row
and its event in one transaction (ADR-0002, R§6.8). A section is a thing an administrator changes and a
guest reads, so it earns a log for the same reason an item did.

**6. The `created` event keeps carrying name and price only.** An item created with a description and a
section writes three events in one transaction. Widening `created` to carry all five fields would break
`0001`'s two paired CHECKs against every `created` row already in the database, because those CHECKs are
equalities and a description is optional. The log then reads *"Created as "Soup" at $4.50 / Description set
/ Filed under Starters"* — three lines where one would do, which is a cost paid in prose rather than in a
constraint that cannot be stated.

**7. `0001` is not edited.** DbUp journals by script name (ADR-0012), so an edit to an applied script is a
change that never runs on any database that has already seen it. `0003_menu_sections_and_item_descriptions.sql`
is additive, and its constraint replacements name their constraints — `0001` declared them inline, so
PostgreSQL generated `menu_item_event_event_type_check`, `menu_item_event_check` and
`menu_item_event_check1`, which are deterministic, undocumented, and not a thing to depend on in a script
that runs at startup on somebody else's box.

## Consequences

**No projection view moves.** §8.3's `order_current_line` joins `menu_item` for its name and needs nothing
else, and the kitchen groups tickets by table and person rather than by section. Four columns and two tables
arrive and §8.3 does not change — worth stating, because it is the surprising half and because a reader
looking for the view edit should find out here that there isn't one.

**Prices and the capture rule are untouched.** §6.5.4 still copies `unit_price_amount` onto the line inside
the send transaction, so moving an item between sections or rewriting its description changes nothing that
is already on a bill. `OrderReadModelTests` owns that fact against a real database and needs no edit.

**The 86 rule is untouched.** A deactivated item stays on the guest's menu marked *currently unavailable*
(§7), and it stays there **under its section heading** — which is strictly better than the current flat
list, because "the salmon exists and is out" reads properly under *Mains* and reads as noise in an
alphabetical list of sixty things.

**Deactivating a section is not deactivating its items.** An inactive section is not rendered to the guest
and its items keep their own `is_active`; reactivating the section brings the menu back exactly as it was.
The alternative — cascading the flag down — would silently rewrite every item's availability and lose which
of them the kitchen had 86'd, which is the same class of mistake as deleting instead of deactivating
(F-10b).

**One extra step on first use, and it is where the design pays for itself.** See decision 1.

**Guest-visible text is now guest-authored-adjacent, and it is not.** A description is written by an
administrator and rendered to guests, so it goes through Razor's default HTML encoding like every other
string on that surface and must never reach a `MarkupString`. §11.11's `script-src` carries no
`'unsafe-inline'` (ADR-0013), which is the second line of defence F-49 built. This is *not* user-generated
content — that question arrives with comments, in Stage 6 of the plan, and the plan says why it is not
startable yet.

## History

- **2026-08-11.** Written and accepted with `docs/MENU_AND_HANDHELD_PLAN.md` in M6 Slice 30, ahead of the
  Stage 2 implementation, deliberately: every ruling above is a `CREATE TABLE` or an `ALTER TABLE` and is
  cheaper to argue with on a page than in a migration. §7 and §8.2 are edited in the commit that implements
  it, per the atomic-documentation rule (R§10 · S§18) — which binds a behaviour change to its specification
  edit and says nothing about deciding ahead of one.
