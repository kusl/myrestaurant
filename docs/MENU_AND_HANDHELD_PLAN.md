# Menu modernization and the handheld contract — staged plan

**Opened 2026-08-11, at the close of M6 Slice 30.** This is the execution plan for the first enhancement
request the project has received from a person who was shown the running application, together with the
defect that request arrived beside. It is a working document: a stage is struck through when it lands, and
the ruling paragraphs are the part worth keeping afterwards.

The specification governs. Where a stage below and `TECHNICAL_SPECIFICATION.md` disagree, the
specification is right and this file is stale — with one deliberate exception, marked per stage: a decision
that has been *taken* here and not yet written into §7 or §8.2 is recorded here first, because the
atomic-documentation rule (R§10 · S§18) binds a behaviour change to its specification edit and says
nothing about planning ahead of one.

---

## What was asked for, in the words it was asked in

> We need to enhance and modernize the menu. At the very least we need menu sections: drinks section,
> entrees, breakfast options, etc. However, I think we can do better — each menu item should come with a
> description field, so please rethink the UI/UX of the menu. In the future we might even have images for
> each menu item; perhaps the users might even want to leave comments and likes on menu items. I am not
> sure if it is doable right now but just something to keep in mind, because the application has not gone
> into production yet — I think we can make drastic changes to our models.
>
> Also I have found that some of the administration pages were not so mobile friendly. For example the
> Manage button was on the right side as the user was trying to manage a table. Because the users are very
> likely to use the website from a mobile phone, and most people don't bring a desktop computer with an
> ultrawide monitor to a restaurant, we need to make the website work well on mobile for all screens.

Two things, and the order they are done in is not the order they were asked in. The reason is in Stage 1.

---

## Why the layout comes before the menu

The menu work adds four surfaces: a section index, a section editor, a rewritten item editor, and a guest
menu that is a grouped list of described items rather than one `<select>`. Every one of those is read from
a phone at a table.

If the responsive vocabulary does not exist when they are written, they are written against the shape that
produced F-59 — a wide table with its only affordance in a right-hand column — and then need a second pass
that touches the same four files again. Building the foundation first is the cheaper order, and it is the
only argument for it: the enhancement request is not being deferred for its own good.

There is a second reason, and it is the one that decided it. **F-59 blocks user testing and the menu does
not.** The application was shown to somebody, and what came back was one enhancement request and one
report of a control they could not reach. A menu without sections is a menu somebody can still order from;
a Manage button off the right-hand edge of the screen is a page an operator cannot use.

---

## Stage 1 — the handheld contract

### 1a — the vocabulary and the four index pages — **landed, M6 Slice 30**

Normative in new **S§11.12**. `REQUIREMENTS.md` rev 6 carries the §8 principle it embodies.

- `app.css` rewritten handheld-first through exactly one `min-width: 48rem` query.
- One shared record-list vocabulary (`.record-*`, `.page-head*`), replacing four inline copies of the same
  eighty lines. Each row is a card below the breakpoint and a table row above it; every cell states its
  own label from `data-label`, because overriding a table's `display` drops the header association that
  would otherwise say what a cell holds.
- `.chip`, `.muted` and `.visually-hidden` are declared once, in `app.css`, with `clip-path` rather than
  the deprecated `clip`.
- Touch targets are `--touch-target` (2.75rem) on every control, and every text input has a 16px floor so
  iOS Safari does not zoom the viewport on focus and leave it there.
- `AdministrationHome`, `AdministrationTables`, `AdministrationMenu` and `AdministrationSittings`
  restructured; all four lost their inline `<style>` blocks entirely.
- New `AdministrationAreaLinks` + `AdministrationArea`: the six area links were copy-pasted into six pages
  and each copy omitted a different one — its own — so no page was reachable from every other. Rendered
  once now, self-link included and marked `aria-current="page"`.
- `HandheldLayoutContractTests`, four facts, each proven sensitive.

### 1b — the remaining surfaces — **next**

Four pages still carry the retired per-page table vocabulary, and
`HandheldLayoutContractTests.StillExpectedToCarryRetiredTableVocabularyIsExactlyWhatTheTreeCarries` names
them, so finishing this stage is deleting entries from that list:

| Page | What it holds that a record list does not | Roughly |
|---|---|---|
| `TableDisplays.razor` | a device roster with revoke actions and a pair-code panel | 440 lines |
| `ManageSitting.razor` | one sitting's complete record: lines, events, corrections | 1120 lines |
| `EventExplorer.razor` | a filter form over three event vocabularies | 570 lines |
| `HiddenRecords.razor` | every hidden order system-wide, unprojected | 910 lines |

Then the surfaces that were never record lists and were never measured on a handset: `ManagePerson`,
`ManageTable`, `ManageMenuItem`, `CounterBoard`, `CounterSitting`, `KitchenBoard`, `TableHistory`,
`TableJoinCode`, `CounterJoinCode`. Each keeps its own `<style>` for rules only it reads — that is this
project's standing arrangement for a statically linked stylesheet — but `.chip` and `.visually-hidden` come
out of all of them, and the forbidden-prefix list in `HandheldLayoutContractTests` is extended to cover
both in the same commit that empties them. **That extension is the stage, not a tidy-up afterwards**
(F-46: a rule enforced against a list of examples is enforced against a list of examples).

`KitchenBoard` needs its own judgement rather than the same treatment. It is the one surface in this
system that is *not* read from a phone — §11.2 and §10.3 describe a wall-mounted kiosk with a wake lock —
so it is the one page where a wide layout is the primary case. The rule in §11.12 is that the handheld
layout is the *default*, not that every surface is optimised for a handset; the kitchen board satisfies it
by being legible at 375px, not by being designed for it.

### 1c — an end-to-end barrier at 375px — **open, and the honest gap**

Nothing in this project has ever asserted anything about layout at any width, which is exactly why F-59
survived four milestones with every gate green. `HandheldLayoutContractTests` asserts the *structure* of
the rule — one breakpoint, one vocabulary, every cell labelled — and cannot assert that a control is
reachable.

Playwright can: the harness already drives a real browser, and a context at 375×667 with
`ElementHandle.BoundingBoxAsync` can assert that the action on the first row of `/administration/tables`
lies inside the viewport. One scenario, one assertion, and it is the assertion F-59 would have failed.

Deliberately **not** done in Slice 30, and the reason is F-41's: the fifteen §16.3 scenarios all run in one
default context, and giving one of them a second viewport is either a second browser context per run or a
resize that every subsequent scenario inherits. Getting that wrong produces a suite that fails on a correct
tree, which is worse than the gap. It is the first item in Stage 6.

---

## Stage 2 — sections and descriptions: schema and data access

**Not started.** This is the schema half of the enhancement request. It is deliberately one stage on its
own: every decision below is a `CREATE TABLE` or an `ALTER TABLE`, none of it is visible to anybody, and it
is the half that a surface cannot be written against until it exists.

The decisions here are **taken, not proposed** — they are what Slice 31 will implement unless vetoed. §7
and §8.2 are edited in the same commit that implements them.

### `menu_section`

```
menu_section
    menu_section_identifier  uuid PRIMARY KEY          -- application-generated UUIDv7 (ADR-0011)
    name                     citext NOT NULL UNIQUE    -- CHECK char_length BETWEEN 1 AND 80
    description              text NOT NULL DEFAULT ''  -- "Served until 11am" — optional, never NULL
    display_order            integer NOT NULL          -- CHECK >= 0; ties broken by name
    is_active                boolean NOT NULL DEFAULT true
    created_at               timestamptz NOT NULL
```

**`name` is `citext` and UNIQUE, and `menu_item.name` is neither.** That asymmetry is the ruling, not an
oversight. §7 already records why an item name carries no UNIQUE constraint — a kitchen runs "Soup" as a
rotating special, and two rows called the same thing is a real menu. Two sections called "Drinks" is never
a real menu; it is a mis-tap, and the guest sees the consequence as a heading that appears twice with the
items split arbitrarily between them. `citext` because "drinks" and "Drinks" are the same mistake.

**`display_order` is not UNIQUE.** A unique ordering column has to be reordered in two phases or with a
deferred constraint, for a menu with perhaps eight sections in it. Reads order by
`(display_order, name, menu_section_identifier)`, so ties are stable and never arbitrary.

**`description` is `NOT NULL DEFAULT ''` rather than nullable**, and so is `menu_item.description` below.
The reason is the paired CHECK on the event log: an *optional* payload column cannot be tied to its event
type by `(new_description IS NOT NULL) = (event_type = 'description_changed')`, because clearing a
description would then have to write NULL and break the constraint. With `''` as "none", clearing is a
value like any other and the CHECK is total. This project has both idioms already — `person.display_name`
is nullable and read through `NULLIF(btrim(…), '')` — and the tie-breaker is the constraint.

### `menu_section_event`

Append-only, mirroring every mutation in the same transaction, exactly as `menu_item_event` does (R§6.8 ·
S§7 · ADR-0002). Vocabulary: `created | renamed | described | reordered | activated | deactivated`. Typed
nullable payload columns `new_name`, `new_description`, `new_display_order`, each CHECK-bound to the types
that carry it. Actor and `occurred_at` NOT NULL.

### `menu_item` gains three columns

```
menu_item
    …
    menu_section_identifier  uuid NOT NULL REFERENCES menu_section   -- every item is in exactly one
    description              text NOT NULL DEFAULT ''
    display_order            integer NOT NULL DEFAULT 0              -- within its section
```

**Every item is in exactly one section, and the column is NOT NULL.** The alternative — nullable, with an
"Uncategorized" bucket rendered when it is not — is a second code path on every reading surface for a state
that exists only because the schema permitted it. A restaurant menu has headings; an item under no heading
is an item nobody decided about. The cost is that the first menu item cannot be created until a section
exists, which is one extra step on the very first use and is where the create-item form sends the
administrator when there are none.

**`display_order` on the item, not just the section.** Alphabetical is wrong on a real menu — "Fries"
before "Truffle Fries" is an ordering somebody chose, and `ORDER BY name` cannot express it. Same
non-unique treatment, same stable tiebreak.

### `menu_item_event` gains three types and three payload columns

New types: `description_changed`, `section_changed`, `reordered`. New payload columns `new_description`,
`new_menu_section_identifier`, `new_display_order`, each CHECK-bound.

**The `created` event keeps carrying name and price only.** An item created with a description and a
section writes `created`, then `description_changed`, then `section_changed`, in one transaction. The
alternative — widening `created` to carry all five — breaks the two existing paired CHECKs against every
`created` row already in the database, because a description is optional and those CHECKs are equalities.
The log reads *"Created as "Soup" at $4.50 / Description set / Filed under Starters"*, which is three lines
where one would do and is honest about it.

**The existing CHECK constraints are replaced by name, and the names are new.** `0001` declared them
inline, so PostgreSQL generated `menu_item_event_event_type_check`, `menu_item_event_check` and
`menu_item_event_check1` — deterministic, undocumented, and not a thing to depend on in a migration that
runs at startup on somebody else's box. `0003` drops every CHECK constraint on the table by querying
`pg_constraint` inside a `DO` block and adds back explicitly named ones. DbUp's PostgreSQL statement
splitter handles dollar-quoting correctly (verified against `PostgresqlQueryParser.ParseRawQuery` in
`DbUp/dbup-postgresql` — the `DollarQuoted` state machine consumes the whole tagged block, so a `;` inside
the `DO` body does not split the statement), so the block is safe in an embedded script.

### The migration, in order

`0003_menu_sections_and_item_descriptions.sql`. `0001` and `0002` are **not** touched: DbUp journals by
script name, so editing an applied script is a change that never runs (F-34's precedent, stated in its own
row).

1. `CREATE TABLE menu_section`, `CREATE TABLE menu_section_event`, indexes.
2. Seed **one** section — and only if `menu_item` has rows. A fresh database gets no sections and the
   administrator names their own; an existing one gets a section to hold what is already there. The seed's
   identifier is a fixed UUIDv7 literal written into the script rather than `gen_random_uuid()`, because
   ADR-0011 puts identifier generation in the application and a migration is the one place with no
   application to ask — a literal is at least auditable and identical on every host.
3. `ALTER TABLE menu_item ADD COLUMN description`, `display_order`, and
   `menu_section_identifier uuid NULL`.
4. Backfill `menu_section_identifier` to the seed.
5. `ALTER COLUMN menu_section_identifier SET NOT NULL` and add the foreign key.
6. Replace `menu_item_event`'s CHECK constraints, add the three payload columns and their CHECKs.

Steps 3–5 are the standard safe sequence for adding a NOT NULL foreign key to a populated table, and the
order matters: `SET NOT NULL` before the backfill fails on the existing rows.

**No projection view changes.** `order_current_line` joins `menu_item` for its name and needs nothing
else; the kitchen groups tickets by table and person, not by menu section. That is worth stating because
it is the surprising half: the schema of record grows four columns and two tables, and §8.3 does not move.

### Data access

| File | Change |
|---|---|
| `Menu/MenuSectionDirectory.cs` | **new** — `MenuSectionSummary`, `IMenuSectionDirectory`, `DapperMenuSectionDirectory` |
| `Menu/MenuSectionAdministration.cs` | **new** — create / rename / describe / reorder / set-active, one transaction each, `FOR UPDATE` before every comparison |
| `Menu/MenuDirectory.cs` | `MenuItemSummary` gains `Description`, `MenuSectionIdentifier`, `MenuSectionName`, `DisplayOrder`; new `ListBySectionAsync` returning sections with their items |
| `Menu/MenuAdministration.cs` | `CreateMenuItemAsync` takes a section and a description; new `DescribeMenuItemAsync`, `MoveMenuItemToSectionAsync`, `ReorderMenuItemAsync` |
| `Menu/MenuEventLog.cs` | payload columns; `ListForSectionAsync`; `ListRecentAsync` becomes a `UNION ALL` over both logs with a subject discriminator |
| `WebApplication/Menu/MenuWorkflow.cs` | one verb per write, `MenuChanged` published only when something actually moved — the rule that file already exists to honour |
| `WebApplication/Orders/OrdersServiceCollectionExtensions.cs` | registers the two new services, in the menu group, for the reason recorded there |

Tests that move with it: `MenuDirectoryTests`, `MenuAdministrationTests`, `MenuAvailabilityTests`,
`MenuEventLogTests`, `MenuWiringTests`, `SchemaMigrationRunnerTests`, and `OrderTestWorld` — which creates
menu items directly in SQL and is therefore the file that decides whether every ordering integration test
compiles.

Documentation in the same commit: **S§7** rewritten, **S§8.2** DDL, **S§16.4**, **S Appendix A**,
**S changelog v1.16**, **R§6.8**, **ADR-0014** (already written, marked accepted), **DOCUMENTATION_REVIEW**
(this is an enhancement, so it takes no F-number — enhancements are recorded in BUILD_PROGRESS and the
specification changelog, and the ledger is for findings).

---

## Stage 3 — sections and descriptions: the surfaces

**Not started.** The UI/UX half, on the Stage 1 foundation.

**`/administration/menu`** becomes sections-first: a record list of sections, each expandable to its items,
with the section's own order controls. The flat name-ordered list of every item goes away — it is what a
menu looks like when the model cannot express a menu.

**`/administration/menu/sections/new` and `/administration/menu/sections/{id}`**, matching the shape
`CreateTable`/`ManageTable` and `CreateMenuItem`/`ManageMenuItem` already have: static SSR, one form per
verb, post/redirect/get with a one-word outcome, and the section's complete uncapped event history at the
bottom (§11.4 — the complete stored record, never truncated for the administrator).

**`/administration/menu/new` and `/{id}`** gain a required section picker, a description `textarea`
(`app.css` already styles one — added in Slice 30 for this), and a position control. Reprice, rename and
the 86 toggle are unchanged.

**The guest menu is the part that was actually asked for.** Today it is one `<select>` with every item
flattened into it and the price glued onto the label — which is unreadable at eleven items and absurd at
sixty, and has nowhere to put a description. It becomes: a section heading, then a card per item with its
name, price, description, and an "Add" control; a section's own description under its heading; and
`disabled` items still present and marked *currently unavailable*, because §7 requires that and it is the
one thing about the current picker that is right. The basket, the Send button, the all-or-nothing rejection
panel and the party totals below it do not change — this is the picker, not the order surface.

**The kitchen 86 panel** groups by section, for the reason the guest menu does: a cook looking for the
salmon looks under the heading it is on.

**One new §16.3 scenario**: create a section, create an item in it with a description, and read both back
from the guest surface. Numbered 16, appended rather than inserted, because the harness names scenarios by
number in fifteen places.

---

## Stage 4 — images

**Not started, and the model consequence is recorded now because that is what was asked for.** "In the
future we might even have images" is a question about where bytes live, and the answer is much cheaper to
give before there is data than after.

**Recommendation: `bytea` in PostgreSQL, one image per item, hard size cap.**

The alternative is a volume on disk, and the argument against it is F-38's. §15 *defines* a recovery set as
exactly two files — the database dump and the Data Protection key ring — and `restore_drill.sh` gates both
on every push. A third artefact means editing that definition, both scripts, the drill, and the runbook;
and an operator who takes a backup the old way from then on has a set that restores an application whose
menu has no pictures in it. Object storage is worse: MinIO is a service, S3 is a paid dependency, and both
contradict R§1's self-hosted premise.

The cost of `bytea` is honest and small at this scale: sixty items at 200 KB is 12 MB, inside a `pg_dump
-Fc` that already compresses, on a database whose whole reason for existing is one restaurant.

```
menu_item_image
    menu_item_image_identifier  uuid PRIMARY KEY
    menu_item_identifier        uuid NOT NULL UNIQUE REFERENCES menu_item   -- one image per item, in v1
    content_type                text NOT NULL CHECK (content_type IN ('image/jpeg', 'image/png', 'image/webp'))
    byte_length                 integer NOT NULL CHECK (byte_length BETWEEN 1 AND 524288)
    pixel_width                 integer NOT NULL CHECK (pixel_width BETWEEN 1 AND 4096)
    pixel_height                integer NOT NULL CHECK (pixel_height BETWEEN 1 AND 4096)
    bytes                       bytea NOT NULL
    uploaded_by_person_identifier uuid NOT NULL REFERENCES person
    uploaded_at                 timestamptz NOT NULL
```

Plus `menu_item_image_event` (`attached | replaced | removed`), because every other mutation in this schema
leaves a log and an image is the one a guest sees.

Four consequences that have to be settled in the same slice:

1. **The route and its caching.** `GET /menu/image/{menu_item_image_identifier}` rather than
   `…/{menu_item_identifier}`, so the URL changes when the image does and `Cache-Control: public,
   max-age=31536000, immutable` is truthful. Keying on the item identifier would need an ETag and a
   revalidation round trip per image per page load, on phones.
2. **The content security policy.** F-49's whole lesson is that this is the one configuration that becomes
   wrong by editing a file it does not mention. §11.11 sets `default-src 'self'` and declares no
   `img-src`, so `'self'` already covers bytes this application serves — the policy needs **no** change,
   and `ContentSecurityPolicyContractTests` should be made to say so rather than left to be true by
   accident.
3. **No resizing, and say so.** There is no free-libre .NET image library in this stack — ImageSharp's
   licence is not AGPL-compatible for this use and SkiaSharp is a native dependency in a rootless
   container. So the server validates and stores what it is given, and the size cap is the whole defence.
   Whether a browser downscales before upload (a `<canvas>` round trip, perhaps 60 lines of
   `wwwroot/js/`) is the open question of this stage, and it is a real one: a phone camera produces 4 MB
   and the cap above is 512 KB, so without it the answer to most uploads is "too big".
4. **The 375px layout.** An image per card doubles the height of the guest menu. A thumbnail beside the
   name rather than a hero above it, and `loading="lazy"` on everything below the first section.

**Reversible if the recommendation is wrong.** `bytea` → volume is a migration that reads rows and writes
files; volume → `bytea` is a migration that cannot find the files. Choosing the reversible direction first
is the whole reason to choose now.

---

## Stage 5 — likes

**Not started.** Deliberately split from comments, and the split is the recommendation: a like is a
number, a comment is a person's words shown to strangers, and only one of those raises a question this
system has never answered.

The shape is already in the tree. §8.3's `order_visibility_event` + `order_visibility_current` is an
append-only per-person boolean folded by `DISTINCT ON`, which is exactly a like:

```
menu_item_reaction_event
    menu_item_reaction_event_identifier uuid PRIMARY KEY
    menu_item_identifier                uuid NOT NULL REFERENCES menu_item
    person_identifier                   uuid NOT NULL REFERENCES person
    event_type                          text NOT NULL CHECK (event_type IN ('liked', 'unliked'))
    occurred_at                         timestamptz NOT NULL
```

with `menu_item_reaction_current` as the `DISTINCT ON (menu_item_identifier, person_identifier)` fold and a
count per item on top of it. No new idiom, no moderation, no text, no rate-limit question worth the name —
a like and an unlike from one person is a row per press and the fold is the answer.

Two rulings needed before it ships. **Who sees the count** — a guest, or only staff? A count of 3 on a
menu of sixty items is noise that makes the restaurant look empty, so the honest answer is probably staff
until it is not, which makes this an administration read first and a guest-facing number later. And
**whether it requires having ordered the item**, which is the same question Stage 6 asks about comments
and is cheaper to answer here.

---

## Stage 6 — comments, and what has to be settled first

**Not started, and not startable.** This is the one item in the request that cannot be planned into a
slice yet, and it is worth being precise about why rather than calling it "future work".

A comment is the first **user-generated content** in this system. Every text field a guest can write today
— a display name, a customization note — is read by staff. A comment is read by other guests. Four things
follow, and three of them are edits to documents rather than to code.

1. **Rate limiting, and it is a known wall.** §17 records that `/register` has no rate limit and states the
   concrete reason it is not a two-line addition: a second naive `AddRateLimiter` policy hijacks §4.2's
   single-valued rejection handler, so a refused registration would answer *"too many pairing attempts"* —
   wrong, and deliberate-looking. Comments hit the identical wall. **This stage cannot land before that
   ruling is revisited**, and revisiting it is a slice of its own with no menu in it.
2. **Privacy, and it is new intent.** §5.3 gives absolute table-to-table privacy: a guest never learns who
   else is in the building. A comment signed with a display name is this system disclosing one person's
   name to strangers for the first time. Initials, an opt-in, a pseudonym, or "a guest" are all defensible;
   none of them is currently written down, and choosing means a `REQUIREMENTS.md` revision under rev 3's
   reasoning rather than rev 2's — new intent, not a mechanism catching up.
3. **Moderation is not optional, and §6.8 is the model.** The vocabulary already exists: hiding, never
   deletion, with an administration surface that can find what was hidden and unhide it. A comment gets
   `posted | edited | hidden | unhidden` and the hidden-records page gains a second kind of record.
4. **Rendering, and the second line of defence is already there.** Comment text goes through Razor's
   default HTML encoding and must never reach a `MarkupString`; §11.11's `script-src` carries no
   `'unsafe-inline'`, which is what F-49 built it for. `ContentSecurityPolicyContractTests` computes what
   the application loads by scanning the tree, so it will notice a new inline handler on its own.

**Recommendation: do Stage 5 and stop.** Likes answer "which of these is popular" with no new question
attached. Comments answer a question nobody has asked for yet at the cost of a rate-limiting slice, a
requirements revision about guest privacy, and a moderation surface — and the request itself says *"I am
not sure if it is doable right now"*, which is the correct instinct.

---

## Not in any stage, and recorded so it is not mistaken for an oversight

- **Allergens, dietary flags, and nutrition.** Not asked for, and a system that displays "gluten free"
  without a mechanism guaranteeing it is worse than one that says nothing. §7's existing sentence about
  customization notes — *"an impossible request is handled by a human walking to the table"* — is the
  project's position on this whole class, and it holds.
- **Per-section availability windows** ("Breakfast, served until 11am"). Tempting, because "breakfast
  options" was named in the request. It is a scheduling feature: it needs a time-of-day model, a decision
  about what happens to a basket at 11:00:01, and `RESTAURANT_TIME_ZONE` in a code path that currently only
  formats. A section *description* saying "served until 11am" costs nothing and is honest; the 86 toggle
  already exists for the rest. Revisit only if somebody asks twice.
- **Sub-sections.** "Drinks → Hot / Cold" is a tree, and a tree is a recursive query, a recursive renderer
  and an ordering question at every level, for a menu with eight headings on it. One level, and if a menu
  outgrows it that is evidence rather than a guess.
- **Menu item images in the kitchen ticket.** A cook does not need a picture of what they are cooking.
