# Menu modernization and the handheld contract — staged plan

**Opened 2026-08-11, at the close of M6 Slice 30. Last moved 2026-08-18, at the close of Slice 50.** This
is the execution plan for the first enhancement request the project has received from a person who was
shown the running application, together with the defect that request arrived beside. It is a working
document: a stage is struck through when it lands, and the ruling paragraphs are the part worth keeping
afterwards.

**~~Where Stage 2 stands~~ — Stage 2 is closed. `0005` landed in Slice 40 with the three surfaces it
forces.** The boundary moved twice and then the expensive cut arrived exactly as expensive as every
paragraph in this file predicted: `menu_item.menu_section_identifier uuid NOT NULL REFERENCES
menu_section`, `section_changed` with its payload column and a fifth named biconditional, a section create
page, a required picker on the item form, a harness journey, and §16.3's seventeenth scenario — in one
slice, because there is no version of that migration that is green on its own.

**Two decisions kept a mandatory column from reaching sixteen files, and they are the transferable part.**
`OrderTestWorld.AddMenuItemAsync` takes an *optional* section and lazily creates a house heading when none
is named, so the dozen integration test files that put something on the menu — about ordering, settlement,
the kitchen, none about headings — compile unchanged and mean what they meant. And
`AdministrationJourneys.CreateMenuItemAsync` arranges its own heading before opening the form, so the
**sixteen existing §16.3 scenarios needed no edit at all**. The general rule: when a mandatory argument
arrives late, give the arrangement helper a default rather than threading the argument through every caller
that does not care about it.

**`0005` needed no dollar-quoted block, and that is what `0004` bought.** The one CHECK that had to widen
was `menu_item_event_type_vocabulary`, which `0004` had named, so it was dropped **by name** — two ordinary
statements, nothing to query, nothing for dbup-core's variable substitution to collide with. F-78 was a
one-migration problem rather than a recurring one because the previous slice paid for the names.

**The rejected nullable-then-tighten alternative stayed rejected.** The column went from non-existent to
`NOT NULL` inside one DbUp transaction — added nullable, backfilled, tightened — so no application ever
observed the nullable window and no reading surface ever acquired an "Uncategorized" code path.

**~~The deferred obligation narrowed from five verbs to four~~ — it is closed. Slice 41 brought the other
four in.** `CreateMenuSectionAsync` came behind `IMenuWorkflow` in Slice 40 because the create page called
it; rename, describe, reorder and set-active arrived together with the section editor, because they are
four forms on one page and shipping a subset would have left the same hole under a smaller name. The rule
that governed the whole sequence never changed and is the transferable part: **a workflow verb with no
caller is a code path no test can reach through the interface meant to protect it**, so a verb arrives when
its surface does — and the count of how many are outstanding is the honest statement of how much of the
interface is untested, which is why it was written down every slice.

**`MenuWiringTests`' fake threw from those four for four slices, and the throw was the mechanism rather
than a note.** It was reachable from every test in the file through the default overloads, so a verb that
quietly answered would have let a workflow start calling one with nothing noticing. It is gone because the
obligation is discharged, not because it became inconvenient — and it is worth remembering that a fake that
refuses is how a stated obligation is made to hold between the slice that states it and the slice that
discharges it.

**Where Stage 1 stands.** 1a landed in Slice 30 (the vocabulary and the four administration indexes). 1c
landed in Slice 32 and ahead of 1b (the 375px end-to-end barrier), for the reason F-62 records. **1b closed
for the whole `/administration` area in Slice 34**: every §11.4 surface is on the shared vocabulary, the
retired names are gone from the tree rather than tracked in a list, and nine of the ten reachable surfaces
are measured at 375px by a browser. What remains of 1b is the counter, kitchen and table surfaces, which
were never record lists and were never the subject of F-59. **Stage 2 is next**, and its boundary
correction is below.

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

### 1b — the remaining surfaces — **the administration half landed, M6 Slice 34**

Four pages carried the retired per-page table vocabulary when this stage opened. All four are gone, and the
assertion that used to name the survivors is an emptiness assertion now that there are none.

| Page | What it holds that a record list does not | Roughly | State |
|---|---|---|---|
| ~~`EventExplorer.razor`~~ | a filter form over three event vocabularies | 570 lines | **landed, Slice 33** |
| ~~`HiddenRecords.razor`~~ | every hidden order system-wide, unprojected | 910 lines | **landed, Slice 33** |
| ~~`TableDisplays.razor`~~ | a device roster with revoke actions and a pair-code panel | 440 lines | **landed, Slice 34** |
| ~~`ManageSitting.razor`~~ | one sitting's complete record: lines, events, corrections | 1120 lines | **landed, Slice 34** |

Slice 34 also took the three pages that were never record lists but shared one vocabulary anyway, because
`SharedSelectorPrefixes` could not be extended until every holder was empty (F-46) — and because the
vocabulary they shared turned out to be the fifth copy of F-59, with the drift and the missing touch
targets F-66 records.

| Page | What it declared inline | State |
|---|---|---|
| ~~`ManageMenuItem.razor`~~ | `.manage-*`, a chip set, `.visually-hidden`, and its own history table | **landed, Slice 34** |
| ~~`ManagePerson.razor`~~ | the same, plus `.chip-role` | **landed, Slice 34** |
| ~~`ManageTable.razor`~~ | the same | **landed, Slice 34** |

**Why those two went together, and it is the ruling rather than the order they appear in.** They were the
last two pages carrying a hand-rolled copy of §11.4's row of area links — five `<a class="button-secondary">`
elements inside a `.admin-header-actions` div, each copy omitting its own page. That is the F-59 defect
that `AdministrationAreaLinks` was written to end, and it was two-thirds ended: Slice 30 converted four
pages and left two, so the strip was still a different strip on a third of the administration area. They
were also the two copies of one filter form — `.event-filter` and `.hidden-filter`, the same twelve lines
of `display: flex; flex-wrap: wrap; align-items: flex-end` with no column fallback, which on a 375px screen
wrapped five fields into five rows of unequal width with the submit button wherever the wrap left it. One
shared `.filter-form` / `.filter-actions` vocabulary in `app.css` replaces both.

**Both are now in the §16.3 scenario 16 barrier**, which took it from four surfaces to six, and the barrier's
reach selector grew to cover `.filter-actions` — because §11.4 makes both explorers read-only, so a filter's
submit is the only control either page has and a barrier that measured only record actions would have
visited two pages and measured nothing. `/administration/hidden-records` is measured **empty**, on the same
terms sittings already was and stated the same way: putting a row on it needs a guest, a token, a join, an
order and a close, which is scenario 11's arrangement.

**Two findings came out of doing it**, and both were rules that were already true and not enforced.
**F-63:** §11.12's *exactly one breakpoint* was asserted against `app.css` and nothing else, while the same
section grants twenty-one components an inline `<style>` — so a width query in any of them was a second
breakpoint nothing could see. Found by needing to write one. **F-64:** five custom properties were read
fifty-five times across eight components and declared nowhere, and an undeclared property in CSS renders
its fallback in silence, so eight surfaces had been drawing a palette nobody chose. Fixed across all eight
files in the same slice rather than deferred, on F-47's reasoning — the repair is a name substitution and a
list that exists to defer that is a list this project has ruled against writing.

`HandheldLayoutContractTests` goes from four facts to six: the breakpoint fact is renamed and widened, and
two are new — every custom property the tree reads is declared, and §11.4's area row is rendered once. That
last one had no assertion at all until now, which is worth naming: the sentence has been in the ledger, in
§11.12 and in the component's own doc comment since Slice 30 while nothing in the tree had an opinion about
a seventh administration page.

**What Slice 34 turned out to be, which is not what this paragraph used to predict.** It predicted a
conversion plus an extension of `SharedSelectorPrefixes`, and said that the extension *is* the stage rather
than a tidy-up afterwards (F-46). Both happened. What it did not predict is that the extension was
**blocked, and by the gate rather than by the pages**: the fact behind it matched a prefix against the
*text* of a `<style>` block, and three counter and kitchen components name `.chip` and `.muted` in CSS
comments explaining what they lean on — so adding those two prefixes reported findings on three correct
pages. That is **F-67**, and it makes three slices running whose finding was discovered by attempting the
work the previous slice had scheduled. Two more came with it: **F-65**, two control rules in `app.css` eight
pixels under the number §11.12 states, one of them beneath a comment asserting that it complied; and
**F-66**, the four `.manage-*` copies, whose overlaps with the stylesheet were a deprecated `clip` on four
pages, two chip colours off the palette, and inline form controls with no touch-target height at all.

**What is left of 1b.** The surfaces that were never record lists and were never the subject of F-59:
`CounterBoard`, `CounterSitting`, `KitchenBoard`, `TableHistory`, `TableJoinCode`, `CounterJoinCode`. Each
keeps its own `<style>` for rules only it reads — this project's standing arrangement for a statically
linked stylesheet — and **none of them re-declares a shared name any more**, so the prefix list already
covers them and there is nothing left to extend. What remains is a judgement per surface about whether its
own layout works at 375px, which is a different kind of work from a migration and is not tracked as one.

**The fallbacks are gone, and the paragraph that used to stand here is F-69.** It said *around a hundred
still, harmless where the name exists*, and it repeated a figure §11.12, §16.4 and F-64's ledger row all
carried: over a hundred, across sixteen components. By the time it was written the last time, Slice 34 had
emptied nine of the sixteen blocks and the truth was **fifty, across seven** — so the number that was the
entire argument for the rule being a *should* had been made wrong by the work in the same slice that cited
it. Fifty is an afternoon. All fifty are removed, §11.12 states the rule as a **must**, and no document
carries the count any more, because a count that can be derived from the tree must not be written into
prose beside it (F-47).

**And opening those seven blocks to do it turned up the slice's actual subject.** The first file read —
`TableHistory` — had the guest's irreversible-hide warning drawn in `#fdecea` on `#f5c2c0`, which is the
palette's `--danger-surface` and `--danger-hairline` copied and then drifted: **the same pair of values
Slice 34 removed from four `.chip-warn` copies, in a fifth place, one area over from where that sweep was
looking.** The class turned out to be ninety-five colour values written outside `:root`, twenty of them
byte-identical to a property declared there, three of those *inside `app.css` itself*, and three more
written as `rgba(22, 32, 43, …)`, which is `--ink` in decimal and the one form no scan for `#hex` could
have found. That is **F-68**, and it makes four slices running whose finding was discovered by attempting
the work the previous slice had scheduled. Every colour in the tree is now a property, and a ninth fact in
`HandheldLayoutContractTests` says so.

**One honest gap, recorded rather than glossed.** `/administration/sittings/{sitting}` is converted and is
the one §11.4 surface the barrier does not measure. Reaching a sitting needs a guest, a table token and a
join before there is an identifier to put in the route — scenario 3's arrangement, three scenarios of setup
for one measurement — and navigating to an invented identifier meets the not-found panel, which has no page
head, so the barrier would fail on arrival rather than measure anything. Its conversion rests on the
contract test and on reading `app.css`. That is the trade `/administration/hidden-records` is already
measured with, one route deeper.

`KitchenBoard` needs its own judgement rather than the same treatment. It is the one surface in this
system that is *not* read from a phone — §11.2 and §10.3 describe a wall-mounted kiosk with a wake lock —
so it is the one page where a wide layout is the primary case. The rule in §11.12 is that the handheld
layout is the *default*, not that every surface is optimised for a handset; the kitchen board satisfies it
by being legible at 375px, not by being designed for it.

### ~~1c — an end-to-end barrier at 375px~~ — **landed, M6 Slice 32, and ahead of 1b**

Nothing in this project had ever asserted anything about layout at any width, which is exactly why F-59
survived four milestones with every gate green. `HandheldLayoutContractTests` asserts the *structure* of
the rule — one breakpoint, one vocabulary, every cell labelled — and by construction cannot assert that a
control is reachable.

Normative in **S§16.4**; the scenario is **S§16.3's sixteenth**. An administrator walks all four
administration indexes in a context laid out at 375×667, and three numbers the page computes are asserted
against the viewport: `documentElement.scrollWidth` against `clientWidth`, every action's
`getBoundingClientRect` against the same, and every control's height against the 44px `--touch-target`
resolves to. `Harness/HandheldReach.cs` takes all of it in one `EvaluateAsync` round trip, so every number
describes the same moment rather than a dozen consecutive ones.

Three things about it are rulings rather than implementation, and each answers a way this could have been
a barrier that asserts nothing:

- **The viewport is asserted first**, and read back from the document rather than from the option that set
  it. At Playwright's default 1280 every other assertion in the scenario passes (F-41). It is compared as
  a ceiling with twenty pixels of allowance under it, because `clientWidth` excludes a classic scrollbar
  and headless Chromium draws one on every page here.
- **The count of measured controls is asserted.** Seven when it was written and nine since Slice 33 — two
  rows and a create button on people, one and one on tables, one and one on menu, nothing on sittings, and
  one filter submit on each explorer. A renamed `.record-actions` leaves five, a renamed
  `.page-head-action` leaves six and a renamed `.filter-actions` leaves seven, so the floor of eight
  catches all three.
- **The widest element is collected and may never fail a run.** An element wider than the viewport inside
  its own scroll container is correct — `.page-head-areas` is exactly that — so the walk skips anything
  inside a scroller and even then only writes the sentence that explains a failure. The two numbers decide.

The set of surfaces it visits is a list — four when it was written, six since Slice 33 — and it grows a
line per page 1b converts, which is the same arrangement `StillExpectedToCarryRetiredTableVocabulary`
already has and for the same reason (F-47). Slice 33 also widened its reach selector to cover a filter's
own submit, because §11.4's two explorers are read-only and have no other control at all.

**Why this ran before 1b, and the correction that decided it.** Slice 30 deferred this on the stated
ground that *the fifteen §16.3 scenarios all run in one default context*, so a second viewport meant
either an extra context per run or a resize every later scenario inherits. **That is not true of this
harness and never was.** `RestaurantHarness` holds one *browser*; `StartInstanceAsync` calls
`browser.NewContextAsync` per instance and `OpenIsolatedPageAsync` mints further ones on request. A
viewport belongs to a context, so there was nothing to share. The sentence was written once here and then
copied into S§16.4, into F-59's ledger row and into the Slice 30 BUILD_PROGRESS entry — three documents
asserting a property of a file none of them had read. It is **F-62**, and it is F-50's shape applied to
something that was never true rather than to something that stopped being true.

Given that, the order swapped. 1b is roughly 2,400 further lines of Razor; converting them before the
barrier existed would have meant converting them exactly the way the four pages in F-59 were written — by
hand, with nothing in the tree able to decide whether the result is reachable. Building the barrier first
also retro-proves Slice 30's four pages, which nothing until now could.

*(The previous version of this section closed by calling itself "the first item in Stage 6", noted at the
time as a typo for "the first open item". The sentence is gone with the gap it described.)*


---

## ~~Stage 2 — sections and descriptions: schema and data access~~ — **closed, M6 Slice 40**

**`0003` (Slice 37) the section tables; `0004` (Slice 38) the item's description and position; `0005`
(Slice 40) the section reference and the three surfaces it forces. Nothing of this stage is outstanding.**
This is the schema half of the enhancement request. It was written as one stage on its own because every
decision below is a `CREATE TABLE` or an `ALTER TABLE`, none of it is visible to anybody, and it is the half
that a surface cannot be written against until it exists.

The decisions here are **taken, not proposed**. §7 and §8.2 are edited in the same commit that implements
each of them.

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

### The boundary has now moved twice, and the second move is Slice 38's

**Slice 37 cut between the two tables.** The correction below explains why the stage as first written could
not ship green, and its answer was to pull three surfaces forward. Slice 37 declined that and cut between
`menu_section` and `menu_item` instead, so that `0003` touched nothing existing.

**Slice 38 cut again, between the item's own columns**, by the same test one register lower. `0004` adds
`menu_item.description` and `menu_item.display_order`, both `NOT NULL` with a `DEFAULT`, plus
`description_changed` and `reordered` with their payload columns and named CHECKs. **Nothing existing
changes meaning:** no backfill runs, no form is required to supply anything, and because `display_order`
defaults to 0 the new `ORDER BY (display_order, name, identifier)` *is* the `ORDER BY (name, identifier)`
every reader already had. The suite stays green by construction rather than by inspection.

`0005` is therefore the whole of what is left, and it is exactly the expensive part the correction below
identified: `menu_section_identifier uuid NOT NULL REFERENCES menu_section`, the conditional one-section
seed and the backfill beside it, `section_changed` with its payload column and a widened vocabulary CHECK —
droppable **by name** now, because `0004` replaced `0001`'s four generated names — and the three surfaces:
the section create page, the section picker on the item form, and a harness `CreateMenuSectionAsync` the
five ordering scenarios call before their first `CreateMenuItemAsync`. That last file is the one that decides
whether the ordering integration tests compile.

**Two rulings were settled by `0004` and are recorded here because they are not obvious from the schema.**
An item is created at position **0**, not appended at `MAX + 1` as a section is, because an item's position
is *within its section* and "the end of the menu" is undefined until `0005`. And the item's new event types
are spelled `description_changed` and `reordered` rather than `menu_section_event`'s `described` — each
table's vocabulary is internally consistent, and this one has said `name_changed` since `0001`.

### A correction to this stage's boundary, made before it was authored

**Stage 2 as written above cannot ship green, and the reason is one word in its own schema.**
`menu_section_identifier` is `NOT NULL`, so the moment migration `0003` applies, `CreateMenuItem.razor`
cannot create an item without naming a section — and `AdministrationJourneys.CreateMenuItemAsync` drives
that real form in six of the sixteen §16.3 scenarios — the five ordering scenarios and, since Slice 32,
the handheld barrier, which needs a row on `/administration/menu` to have anything to measure. A slice
that lands the schema and the data access
and leaves the surfaces for Stage 3 therefore lands a red suite, whatever the quality of the two halves.

So Stage 2 pulls three things forward out of Stage 3, and only three: the **section create page**, the
**section picker and description field on the item form**, and a harness `CreateMenuSectionAsync` the five
scenarios call before their first `CreateMenuItemAsync`. The section *index*, the section *editor* with its
event history, the rewritten guest menu and the kitchen panel's grouping all stay in Stage 3 — none of
them is on the path between `NOT NULL` and a green suite.

The alternative was considered and rejected: make the column nullable in `0003`, ship the surfaces in
Stage 3, and tighten it in `0004`. That is three migrations for two decisions, it puts an
"Uncategorized" state into the schema for exactly one slice, and every reading surface written during
that slice acquires a code path for it that then has to be removed. The ruling above — that an item under
no heading is an item nobody decided about — is worth more than the neatness of the stage boundary.

### The migration, in order

**As authored this was one script. It shipped as three, and all three have applied.**
`0003_menu_sections.sql` (Slice 37) is step 1 below; `0004_menu_item_descriptions.sql` (Slice 38) is steps 3
and 6 for the two columns that carry defaults; `0005_menu_item_sections.sql` (Slice 40) is steps 2, 4 and 5
plus the section half of step 6. `0001` and `0002` are **not** touched, and neither are `0003`, `0004` or
`0005` now that they are applied: DbUp journals by script name, so editing an applied script is a change
that never runs (F-34's precedent, stated in its own row).

**One thing about `0005`'s step 2 is not what this plan specified, and it is a repair rather than a
deviation.** The seed carries **two** guards, not one. `EXISTS (SELECT 1 FROM menu_item)` is the rule below.
`NOT EXISTS (SELECT 1 FROM menu_section)` is the one this plan missed: "no surface calls
`IMenuSectionAdministration`" is not the same claim as "no row exists", and without that guard the INSERT
would trip `menu_section.name`'s UNIQUE on any database that happened to hold a section called "Menu" — and
a migration that fails at startup takes the whole application down. The backfill correspondingly targets the
**first section in display order** rather than the seed's literal identifier, so both paths converge: if the
seed ran it *is* the first section, and if it did not the orphans go under the earliest heading that
exists.

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
| ~~`Menu/MenuSectionDirectory.cs`~~ | **new** — `MenuSectionSummary`, `IMenuSectionDirectory`, `DapperMenuSectionDirectory` — **landed, Slice 37** |
| ~~`Menu/MenuSectionAdministration.cs`~~ | **new** — create / rename / describe / reorder / set-active, one transaction each, `FOR UPDATE` before every comparison — **landed, Slice 37**; `display_order` is assigned by appending rather than supplied, and a rename is compared ordinally though the column is `citext` |
| ~~`Menu/MenuDirectory.cs`~~ | `MenuItemSummary` gained `Description` and `DisplayOrder` (Slice 38), then `MenuSectionIdentifier`, `MenuSectionName` and `MenuSectionIsActive` with a six-key ordering and an INNER join — **landed, Slice 40**. `ListBySectionAsync` was **not** written: the ordering makes each heading's items contiguous, so a surface groups by walking one list, and a second read would be a verb with no caller |
| ~~`Menu/MenuAdministration.cs`~~ | `CreateMenuItemAsync` takes a description (Slice 38), then a section, with `MAX + 1` positioning under a lock on the section row and a `MenuSectionNotFound` outcome — **landed, Slice 40**. `MoveMenuItemToSectionAsync` — **landed, Slice 43**, with the picker on `ManageMenuItem` that calls it. It appends rather than carrying the old position across, takes the item lock before the section lock so the file has one nesting direction, and writes `reordered` beside `section_changed` only when the number actually moved |
| ~~`Menu/MenuEventLog.cs`~~ | `new_description` and `new_display_order` (Slice 38), then `new_menu_section_identifier` with a LEFT join aliased `new_section` — **landed, Slice 40**. The alias is load-bearing: `menu_item` now has its own `menu_section_identifier`, so an unaliased join would read the item's *current* heading rather than the one the event recorded. `ListForSectionAsync` and the `UNION ALL` over both logs wait for Stage 3 |
| ~~`WebApplication/Menu/MenuWorkflow.cs`~~ | a verb per write, `MenuChanged` published only when something actually moved — the item verbs landed in Slice 38; Slice 40 added the section to the create, **made that publish conditional** (a create can now report a missing heading rather than throw), and brought `CreateMenuSectionAsync` in. Four section verbs remain outside, narrowed from five |
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

**Most of it has landed. The guest menu's cards came first (Slice 39), its headings second (Slice 40), and
the section editor third (Slice 41).** The UI/UX half, on the Stage 1 foundation.

**What Slice 41 shipped, and what it closed by shipping it.** A section **editor** at
`/administration/menu/sections/{id}` — four forms, post/redirect/get, the heading's items, and its complete
uncapped event history, which needed `IMenuSectionEventLog` because nothing in this tree could read
`menu_section_event` at all. With it: the last **four workflow verbs**, each publishing `MenuChanged` on a
committed row; **links into it** from the create panel, the menu index's Section column and each item's own
page, which is the thing the index was waiting for; the harness recovering a new section's identifier from
its own *Manage this section* link like every other create journey; and **scenario 17's two cut steps**,
restored.

**~~What is still outstanding~~ — shorter again. `MoveMenuItemToSectionAsync` landed in Slice 43 with the
picker on `ManageMenuItem`, and it was the last verb in the whole enhancement with no surface.** It appends
the item to the end of its new heading, on the same rule a create follows and for the same reason: a position
is a position *within* a heading. Because §8.2 binds `new_display_order` to `reordered` alone, a move that
changes the position writes a second event, conditional on the number actually differing — the no-op rule
applied to half of one verb, which is the arm most easily left out and therefore the one with its own fact.

**The rule that governed six verbs across seven slices is now discharged rather than narrowed, and the way it
was carried is the transferable part.** A workflow verb with no caller is a code path no test can reach
through the interface meant to protect it. Five section verbs and this one arrived when their surfaces did;
the count of how many were outstanding was written down every single slice, which is the only reason its
reaching zero is a fact somebody can state rather than something noticed later. A deferral that is named
every slice is a deferral; one that is named once is an omission with a date on it.

**~~What is left of this stage~~ — shorter again. The section index landed in Slice 44, and it was the
largest of the three.** `/administration/menu` is a group per heading, each a `<details>` rendered open,
holding that heading's items as a record list. What is left: a section's own **description under its
heading** on the guest menu, and the kitchen's 86 panel, which still groups by nothing. Neither is a verb;
both are surfaces reading things that already exist.

**Two rulings came out of the index and both are transferable.** The group is rendered **open on every
request**, because a heading a server collapsed is a heading whose items nobody looking for an item can
find — and because §16.3 scenario 16 measures what a layout engine laid out, so a control inside a closed
`<details>` has no box and a collapsed group would withdraw its own controls from the barrier that exists
to catch exactly that. And **an empty heading is visible on that surface and nowhere else in the
application**: the old index was built from `menu_item`, so a heading with nothing under it appeared on no
page at all, which made a heading created with a typo a row no surface could show. The page now reads both
directories, which is what `IMenuSectionDirectory.ListAsync` was written for.

**The order controls this plan promised are cut, and the cut is the paragraph worth keeping.** This file
said the index would arrive *“with the section's own order controls”*. It does not, for a reason
that only becomes visible once somebody tries to write them: `ReorderMenuSectionAsync` sets an **absolute**
`display_order`, and §7 makes positions deliberately non-unique with a name tie-break — so *move this
heading above that one* is not expressible as one absolute write. Two headings sharing a position have an
order nobody assigned, and no single number distinguishes them. An honest up/down control needs a
**resequencing verb** writing several rows, and therefore several `reordered` events, in one transaction:
a new write with new event semantics, not a surface change. So the index makes the ordering **legible** —
headings in stored order, each one's position on its own summary — and the editor keeps the write. **The
general rule, which is the part to carry:** when a surface would need a verb the model cannot express, the
surface ships without it and says so, rather than shipping a control that is right in the common case and
silently wrong wherever the data is allowed to be ambiguous.

**And the index cost a gate rather than a migration, which is the third slice in a row where that was the
expensive part.** The 375px barrier chooses what it measures from a list of class names, so replacing
`.record-actions` rows with `.menu-group` groups would have left this surface visited and unmeasured while
the floor above the check went *up* — the item rows inside the groups still carry `.record-actions`. That
is F-93, it is F-91's stated residual arriving as a live defect, and the repair is a rule in §16.4 rather
than two selectors in a harness.

**Slice 42 added nothing to this stage and unblocked all of it.** That slice is defects only: Slice 41
shipped an archive that did not compile, and behind the five build errors sat fourteen failing integration
and end-to-end facts that could not have run either. Six of the seven findings are one mechanism — **a
schema widened by a migration reaches the test arrangement last** — which is worth carrying into the
remaining work here, because every item left on the list above touches `menu_item.menu_section_identifier`
and therefore touches the arrangement that kept getting missed. Concretely: `MoveMenuItemToSectionAsync`
will write a second `section_changed`, so any fact that counts an item's events has to be written knowing
that a create already contributes one (F-87), and `OrderTestWorld.AddMenuItemEventAsync` can now write that
type at all, which it could not before (F-86).

**What Slice 40 shipped here, ahead of the rest of this stage, because `0005` forced it.** A section
**create** page at `/administration/menu/sections/new`; a **required picker** on the item form, which
renders a first-use panel instead of a form when there are no headings, because a required control over an
empty list is one nobody can satisfy and a validation message that blames a person for a menu that has no
headings yet; a **Section column** on `/administration/menu` with a *Section hidden* chip; the same fact on
`ManageMenuItem`; and **§11.1's grouping**, which was always going to be an outer loop around markup that
did not change, and was.

**What is still outstanding, and it is most of the administration side.** The section **index** and the
section **editor** with its uncapped event history — which is what four of the five section verbs are
waiting for, and what `MoveMenuItemToSectionAsync` is waiting for. Until that lands, a heading created with
a typo can be worked around only by creating another; that is a real rough edge and it is named here rather
than discovered.

**Why the order changed, and it is a ruling rather than opportunism.** This stage was written to run after
`0005`, on the reasoning that a guest menu grouped by section needs sections to exist. Slice 39 took the
picker first, without sections, because the two halves turned out to be separable: **a card per item needs
`menu_item.description`, which `0004` delivered, and nothing else.** Grouping those cards under headings is
an outer loop added later around markup that does not change. Against that, leaving the picker as a
`<select>` until `0005` meant leaving the *only* part of the request the person who made it could see —
*"please rethink the UI/UX of the menu"* — behind two migrations, with the column that exists to be read
already in the schema and read by nothing. The cost of the swap is that the guest surface will be edited
twice; under this project's full-file delivery that costs nothing, and F-64's ruling is the precedent.

**~~`/administration/menu`~~ becomes sections-first** — **landed, Slice 44.** A group per heading, each a
`<details>` rendered open, holding that heading's items. The flat name-ordered list of every item is gone —
it was what a menu looks like when the model cannot express a menu. **Without the section's own order
controls**, which this sentence promised and which are cut with a recorded reason above: an absolute
`display_order` cannot express *move this above that* while positions are non-unique with a name tie-break,
so an up/down control needs a resequencing verb rather than a surface.

**~~Slice 40 took the honest intermediate rather than half of this~~ — and Slice 44 replaced it, exactly as
that intermediate predicted it would.** The index gained a *Section* column and a *Create section* button
in Slice 40 and did not become a list of headings, because a sections-first index needs an editor to open
into and a record list whose rows link nowhere is a list of dead ends. The editor landed in Slice 41, the
refile verb in Slice 43, and the column was replaced by the grouping in Slice 44 — three slices in which
nothing had to be undone, which is what the intermediate was chosen for. **The transferable claim is
narrower than “ship something”:** an intermediate is honest when replacing it costs no more than
building the destination would have, and a column inside a record list is that, where a half-built grouping
with dead links would not have been.

**~~`/administration/menu/sections/new` and `/administration/menu/sections/{id}`~~ — both landed**, matching
the shape `CreateTable`/`ManageTable` and `CreateMenuItem`/`ManageMenuItem` already have: static SSR, one
form per verb, post/redirect/get with a one-word outcome, and the section's complete uncapped event history
at the bottom (§11.4 — the complete stored record, never truncated for the administrator). The create page
landed in Slice 40 and the editor in Slice 41.

The consequence of the create page having shipped alone showed up in the harness rather than in the
application, and it resolved exactly as predicted: for one slice a section had **no management page to link
to**, so its success panel linked onward to the item form and
`AdministrationJourneys.CreateMenuSectionAsync` recovered the new identifier from that form's
`<option value>`. That was reading the surface rather than reaching past it, which is what §16.3 asks for,
and it was recorded at the time as a shape that goes away when the editor exists. It does. The journey now
reads a *Manage this section* link like `CreateTableAsync`, `CreateMenuItemAsync` and
`CreateStaffAccountAsync` all do, and `FindMenuSectionAsync` is left doing the one job it was written for —
answering "does a heading with this name already exist" for the idempotent wrapper.

**The transferable part is the shape of the intermediate rather than the repair.** A surface shipped one
slice ahead of its destination will grow a workaround somewhere, and the choice is whether that workaround
is *in the product* or *in the harness*. Putting it in the harness kept the create page honest — it never
grew a link to a page that did not exist, never invented a placeholder route — and cost one method that
knew a fact about a neighbouring form for one slice.

**~~`/administration/menu/new`~~ and `/{id}`** gain a required section picker, a description `textarea`
(`app.css` already styles one — added in Slice 30 for this), and a position control. Reprice, rename and
the 86 toggle are unchanged. **The create form landed in Slice 40** with the picker and the first-use panel;
`ManageMenuItem` shows the heading and cannot yet change it, which is `MoveMenuItemToSectionAsync`'s missing
caller.

**Inactive sections are offered by the picker and marked** *(hidden from guests)*. §7 hides an inactive
heading from the **guest**, not from §11.4's administrator, whose job on that page may be stocking next
week's breakfast menu before switching it on.

### ~~The guest menu — the part that was actually asked for~~ — **landed, M6 Slice 39, minus the section headings**

It was one `<select>` with every item flattened into it and the price glued onto the label, which is
unreadable at eleven items and absurd at sixty and had nowhere to put a description. It is now **a card per
item** — name, price, description where the item has one, an availability chip, and a `disabled` control
where §7 says so — and **choosing a card opens a detail panel** naming what is recorded about that item.
The basket, the Send button, the all-or-nothing rejection panel and the party totals below it did not
change: this was the picker, not the order surface, exactly as this paragraph predicted.

**Three things about it are rulings rather than implementation.**

- **The panel says when it has nothing to say.** "More information about that item *if such information
  exists*" is how the request was worded, and the honest reading is a sentence rather than an empty box: a
  blank panel is indistinguishable from a surface that failed to load, which is the confusion §11.10's
  `data-loaded` bit exists to prevent one level up. Today the panel names a price, an availability line and
  when the item first appeared; Stages 4 and 5 add rows to it rather than rewriting it, which is the whole
  reason it is a `<dl>` of terms rather than three hard-coded lines.
- **A card is a `<button>` with `aria-pressed`, not a radio.** A radio group is the more precise ARIA for
  "choose exactly one" and it was refused for a concrete reason: Blazor reconciles the `checked`
  *attribute* while browsers track the checked *property*, so a radio whose state a component owns can
  drift out of step with the DOM in ways only a browser can observe — and this slice had no browser. Every
  other control on that island is an `@onclick`. A one-of-many toggle set is a slight stretch of
  `aria-pressed` and it is the better trade against emulating a radio group's keyboard semantics.
- **No breakpoint.** `.order-menu` and `.order-menu-facts` are `auto-fit` grids, so one column on a 375px
  handset and as many as fit on a counter's laptop is the same rule either way. §11.12 asks for exactly
  this in preference to a width query, and a width written for the menu would have been the tree's second
  breakpoint.

**What the harness gained, and it is the half that could not have existed before.** `TableOrderJourneys`
adds `ChooseAsync`, `ReadMenuAsync`, `ReadChosenItemDetailAsync` and `WaitForMenuAsync`. An `<option>`
renders text and nothing else, so the only thing a harness could read off the old picker was one
concatenated label and the only assertion available was containment — which is why `0004`'s description
column shipped with nothing behind it. A card has an element per fact. `AdministrationJourneys.CreateMenuItemAsync`
takes an optional description so a scenario can arrange an item that has one.

**~~What is left of the guest menu:~~ the headings and the grouping landed in Slice 40.** `.order-menu` is
wrapped in a `.order-menu-section` per heading, the `<ul>` points at its own `<h4>` with
`aria-labelledby`, and the grouping walks the directory's ordering once rather than re-deciding it with a
`GroupBy`. No new breakpoint — a heading is a block above a grid that was already intrinsic.

**~~A section's own description under its heading is still outstanding~~ — landed, M6 Slice 49**, and the
choice this paragraph deferred was made in favour of the **widened record**. The two routes named here were a
second read or a wider one, and what decided it was not cost: two reads happen at two instants, so a heading
renamed between them renders its new name above its old sentence, and a guest's picker is not in a
transaction and should not be. `MenuItemSummary` gains `MenuSectionDescription`, joined from the same
`menu_section` row as the name and by the same INNER JOIN, so one row of one query cannot disagree with
itself. A heading with no description renders **no paragraph** rather than an empty one.

**Three things about it are rulings rather than implementation, and two reverse sentences this tree carried.**

- **The render record was widened, against its own comment.** `MenuSectionOnTheMenu` said a surface needing
  the heading's description "would read the directory rather than widen this". Reversed, with the reversion
  instructions written into the record's summary rather than left in a changelog. *(That record was renamed
  `MenuHeadingGroup` and moved out of the component in Slice 50 — see Stage 3c. The reversion instructions
  moved with it.)*
- **The publish needed no edit.** `DescribeMenuSectionAsync` has broadcast `MenuChanged` since the day it
  reached no guest surface at all, on the ruling that `MenuChanged` means *re-read the menu* and nothing
  else. This is the moment that ruling was made for, and neither the workflow nor its wiring fact changed —
  a tree that had made the publish conditional would show the new sentence to whoever reloaded and the old
  one to every phone already looking at it.
- **No `aria-describedby`.** It is the more precise ARIA and expressing "no description, no attribute" needs
  an attribute whose value is null, which this tree has no precedent for, so the honest alternative was
  rendering the `<ul>` twice. The paragraph sits between the heading and the list in document order instead,
  which is where a screen reader meets it either way.

**Scenario 17 gained the assertion and needed no new arrangement**, which is the argument for arranging
scenarios out of real forms: it has created *Starters* with a description and *Puddings* without one since
Slice 40, and nothing had ever asserted on either. It is now the only place `menu_section.description` is
carried from the form that typed it to the phone that reads it.

**§7's asymmetry is now implemented and is the thing to be careful about here.** An inactive *section* is
not rendered to the guest at all; an inactive *item* is rendered and marked. The filter is on the surface
rather than in the directory, because §11.4's administrator must see every heading.

**The kitchen 86 panel** groups by section, for the reason the guest menu does: a cook looking for the
salmon looks under the heading it is on.

**~~One new §16.3 scenario~~ — scenario 17 landed in Slice 40**: two headings created in an order that is
not alphabetical, a described item under each, and a guest reading them back grouped, in order, with each
description on its card and in its detail panel. A third item under an existing heading then joins it rather
than starting a new grouping, and lands at the end of it, which is `MAX + 1`-within-section proven through a
browser. **It is the first scenario to read `menu_item.description` end to end** — `0004` shipped the column
and Slice 39 built the card that shows it, and nothing had ever asserted that the sentence arrives.

Numbered 17, appended rather than inserted, because the harness names scenarios by number in a great many
places. It was numbered 16 when this was written; Slice 32's handheld barrier took that number, which is
what appending costs and is cheaper than renumbering sixteen of them.

**~~One assertion was cut from it during the slice~~ — it landed in Slice 41, with the editor, as recorded.**
The scenario was drafted to deactivate a heading and watch it disappear from the guest's menu — §7's
asymmetry, and the one thing about it no unit test can see. That needed `SetMenuSectionActiveAsync` to have
a surface, which Slice 40 deliberately did not ship, and asserting it anyway would have meant either a
harness reaching past the UI, which §16.3 refuses, or a verb wired for a test, which is worse.

**It came back larger than it was cut, and that is the argument for naming a cut rather than dropping it.**
The restored steps do not only watch the heading vanish: they assert that the *other* heading's items stay
present, in order, and orderable, and then switch the heading back on and check the menu returns exactly as
it was — which is the only end-to-end proof that deactivating a section **does not cascade** to its items.
A cascade would come back with the pie marked unavailable. That second half was not in the draft; it was
obvious once the assertion was being written against a surface that existed, and it would not have been
written at all if the cut had been made silently.

---

## ~~Stage 3a — the resequencing verb~~ — **landed, M6 Slice 47**

**Landed exactly as specified below, which is recorded because it is the point of specifying it: the slice was arrangement rather than design.** The paragraphs that follow are the design as written before the code, left unedited apart from this header and the closing note, so the two can be compared.

**Unblocked as of Slice 45 and shipped in Slice 47.** §7 records the cut in the index's own
words: `ReorderMenuSectionAsync` sets an **absolute** `display_order`, positions are deliberately non-unique
with a name tie-break, so *"move this heading up"* is not expressible as one absolute write — two headings
sharing a position have an order nobody assigned and no single number distinguishes them.

**Why it waited for Slice 45, which is the part worth keeping.** A resequencing verb writes several rows and
therefore **several `reordered` events in one transaction**, and one transaction stamps every row it writes
with one `IClock.UtcNow`. So the events of one resequence share an instant, and their order in
`menu_section_event` is decided entirely by the identifier tie-break — which is exactly the property F-95
found nothing was keeping. Shipping the verb before the fix would have produced a log that recorded the right
rows in an order chosen at random, on the surface whose whole job is to be readable. Shipping it *with* the
fix would have been worse in a specific way: the verb's ordering test would simultaneously be the first test
of the fix, so a red run could not say which of the two changes caused it, and §18's habit of chasing a
count deviation before the slice closes gets expensive when there are two candidates. **One change, one green
run, then the feature.**

The shape, so the next slice is arrangement rather than design:

- `ResequenceMenuSectionsAsync(IReadOnlyList<Guid> orderedIdentifiers, Guid actorPersonIdentifier, …)`.
  Absolute positions `0…n-1` assigned in the order given, which is what makes "up" expressible: the surface
  swaps two entries in a list it already has and sends the whole list.
- **It must be the whole list, not a pair.** A pairwise swap has to decide what happens when the two
  positions are equal, and equal positions are permitted. Taking the full ordering means the verb has one
  precondition — the list is exactly the set of sections — and no ambiguity to resolve.
- Refuse a list that is not a permutation of the stored set, rather than reconciling it. A list missing a
  section is a stale page, and a page that stale should be reloaded, not partially obeyed.
- `SELECT … FOR UPDATE` over all rows **ordered by identifier** before comparing, on the existing lock rule,
  and ordered so two concurrent resequences cannot deadlock against each other.
- One `reordered` event per section whose position **actually moved**, on the existing no-op rule: a
  resequence that leaves six of eight headings where they were writes two events, not eight. The events
  share an instant and now read in the order the rows were written.
- Outcome enum in the established shape: `Resequenced` / `NoChange` / `MenuSectionSetChanged`.

Two obligations the slice carries, both already written down:

1. **F-93.** The index acquires a new *kind* of control, so the 375px barrier acquires a selector in the same
   slice, or it is a surface the barrier has stopped asserting anything about.
2. **The item-level mirror is deliberately out of scope**, and saying so is what keeps the slice honest:
   `menu_item.display_order` has the same absolute-write shape and the same non-unique positions, so items
   within a heading need the same verb. It is the same design applied to a second table, and it is a second
   slice, because the two write to different event tables with different paired CHECKs. **That is Stage 3b
   below, landed in M6 Slice 48.**

### What landed, and the three places it differs from the text above

**Nothing in the design changed.** The verb takes the whole list, refuses a non-permutation, locks ordered by
identifier, writes one event per moved row, and returns `Resequenced` / `NoChange` / `MenuSectionSetChanged`.
Three things the specification above did not settle were settled in the writing, and they are here rather
than only in the code:

- **The refusal is one outcome for three shapes.** A short list, a repeated identifier and an unknown heading
  all return `MenuSectionSetChanged`. The alternative — three outcomes — would put a distinction on the
  surface that the surface cannot act on differently: all three mean *this page is stale, reload it*.
- **The permutation test de-duplicates before it resolves.** A list of the right length whose members all
  exist can still name one of them twice, and that is the one shape a length check and a resolution check
  both admit. There is an integration fact for exactly it.
- **Both obligations are discharged.** F-93: the barrier gains `.menu-group-actions button` in this slice,
  which is the first time that rule has been obeyed on the way in rather than after the fact. And the
  item-level mirror is **still out of scope and still named** — `menu_item.display_order` has the same
  shape, needs the same verb, and writes to a different event table with different paired CHECKs, so it is
  the next ordering slice rather than a widening of this one.

**One thing was deliberately not done: no CSS.** The two buttons reuse `.menu-group-actions .button-secondary`,
which has been styling that row since Slice 44, so they are full width and `--touch-target` tall on a handset
with no new declaration. On the wide layout the three controls stack, because a `<form>` is a block element.
That is recorded as an open item rather than fixed, because fixing it means opening `app.css` and this slice
had no other reason to.

---

## ~~Stage 3b — the item resequencing verb~~ — **landed, M6 Slice 48**

**Stage 3a named this as out of scope and as the next ordering slice in the same paragraph, rather than
widening itself into it.** This section is the design and the outcome together, written after the fact,
because there was nothing left to design: the shape was settled one register up, and the interesting part
was what the second table made different.

`ResequenceMenuItemsAsync(menuSectionIdentifier, orderedMenuItemIdentifiers, actorPersonIdentifier, …)`
assigns `0…n-1` within one heading, writes one `reordered` event per item whose position actually changed,
refuses a non-permutation whole, and returns `Resequenced` / `NoChange` / `MenuItemSetChanged`.

**Why a second slice and not a wider first one.** The design is identical; the table is not.
`menu_item_event` carries five named paired CHECKs where `menu_section_event` carries three, and a verb is a
write to a table rather than an idea about ordering. One file serving two tables would have had to be right
about both vocabularies at once, and the cost of getting that wrong is a constraint name rather than a
sentence.

### The three things Stage 3a's design did not settle

**1. The heading is a parameter, not inferred from the list.** A position is a position *within* a section,
so the set is one heading's items. Two alternatives were considered and both are worse. Deriving the heading
from the first item's row admits a list spanning two headings and answers it with a silent partial write,
where the whole point of taking a whole ordering is that nothing is left to infer. Taking the entire menu
asks the write to renumber the puddings because somebody moved a drink.

**2. An unknown heading returns `MenuItemSetChanged`, and there is no fourth outcome.** An unknown heading
has no items under it, so any non-empty list fails the permutation test on the same line every other refusal
fails on. And the surface cannot act on the distinction: an unknown heading and a stale item set both mean
*this page is stale, reload it*. That is Stage 3a's collapse-three-refusals-into-one ruling applied to a
fourth shape. There is an integration fact for it, because "no rows came back" is also what an empty heading
looks like, and the two agreeing should be a decision on the record rather than something a reader
reconstructs.

**3. The section row is deliberately not locked, and the argument is arithmetic.** A concurrent create or
refile appends at `MAX(display_order) + 1`, computed from the very positions the resequence is holding
`FOR UPDATE`: `n` rows with maximum `m` give `m ≥ n - 1`, so the arrival lands at `m + 1 ≥ n` — strictly
after every position a resequence of those `n` rows can assign, which is exactly the append those verbs
promise. The interleaving is therefore correct with no lock. Taking none is worth more than the lock would
be: this verb takes item locks and nothing else, where a refile takes an item lock and then a section lock,
so a section lock here would invert that nesting and make the deadlock question live for the first time in
that file. Item rows are locked ordered by identifier, on Stage 3a's rule.

**What is deliberately not tested is that interleaving.** Two transactions racing is a property of a
scheduler; a test passing on one ordering would be F-41's shape rather than evidence.

### F-93 was discharged by a selector added early, which is its own small finding

`.record-actions button` has been in the 375px barrier since the barrier was written and matched **nothing**
until this slice — every index's actions cell held a link and only a link. The item rows are the first submit
controls to render in one, so no edit was needed. The uncomfortable half is recorded rather than enjoyed: a
selector matching nothing is indistinguishable from a selector matching everything it should, and nothing in
the harness could have said which of the two this was. What makes it safe rather than lucky is that
`.menu-group-actions button` was already asserting the same claim on the same page.

### What is still open after this stage

**No end-to-end scenario drives either resequencing verb.** §16.3's scenario 17 is not extended, so nothing
asserts through a browser that a heading or an item moves. The barrier measures the controls; nothing
exercises them.

**The wide layout stacks each row's three controls.** A `<form>` is a block element and `app.css` has no rule
for a row of them. It is now true on two registers, which makes it slightly more worth fixing than it was.

**Ordering is complete for both tables**, so no ordering hole remains in the menu enhancement.

**Stage 3 is closed as of Slice 49.** Its last outstanding piece — a heading's own description under its
heading on the guest menu — landed, and the paragraph above records which of the two deferred routes was
taken and why. **The kitchen's 86 panel was the one surface left that grouped by nothing, and it landed in
Slice 50** — recorded as Stage 3c below rather than folded into this stage, because a cook's panel is a
different question from a guest's menu and it was never a Stage 3 obligation. **No surface in the
application now reads the menu flat.** The next stage is Stage 4.

---

## ~~Stage 3c — the kitchen's 86 panel~~ — **landed, M6 Slice 50**

**Never a Stage 3 obligation, and recorded here so it is not mistaken for scope creep.** Stage 3's subject is the
guest's menu and the administrator's index. §11.2's panel is a third reader of the same list, it was named in
Stage 3's text as the surface that grouped by nothing, and it is filed as its own stage because a cook's panel
answers a different question: *what has run out*, not *what can I have*.

**It was the last surface in the application that read the menu flat.** The guest's picker has been grouped since
Slice 39/40 and the index since Slice 44. A cook was still scrolling one undivided list of every dish in the
building looking for the one that just ran out.

### What forced the shape, which is the part worth keeping

The walk that groups the guest menu was a **private property inside `TableOrderSurface.razor`**, and a private
property cannot be called from a second component. So this stage had exactly two routes: paste the walk into
`KitchenBoard.razor`, or move it out. Pasting is F-59's mechanism — one walk, two copies, two sets of §7's rules
drifting independently with nothing able to see it.

Moving it out also closed a hole nobody had named. That walk carries §11.1's grouping **and** §7's asymmetry
between an inactive item and an inactive heading, and §16.1 records that this repository has no bUnit — so the
only thing asserting any of it was §16.3 scenario 17, incidentally, on the way to something else. `KitchenQueue`'s
own summary had already written the rule down: *a rule that can only be checked by rendering a Razor component is
a rule nobody checks*. That is **F-100**, and `MenuGrouping` is now the fourth member of the set `KitchenQueue`,
`OrderStaging` and `OrderNarrative` belong to.

### The one rule that is the opposite of §11.1's, and is required rather than permitted

**Headings guests cannot see are listed, and marked.** §7 says deactivating a heading does not deactivate its
items — their `is_active` is untouched, and switching the heading back on restores the menu exactly. **This panel
is the only surface in the application that can read or change those flags.** Drop the hidden headings and that
rule becomes unmanageable: a cook cannot 86 the eggs they will need the moment breakfast returns, and cannot bring
back something 86'd last week. The heading is chipped *Hidden from guests* instead — the consequence rather than
the flag, which is the wording §11.4's index and the section editor already use.

The two rules are reached through **two named entry points rather than one flag**: `VisibleToGuests` and
`EveryHeading`. A boolean at a call site is a rule nobody reading the call site can see, and this is the pair §7
restates every time it mentions either.

### What was deliberately not built

- **No per-heading toggle.** Switching a heading off is a decision about what guests see and belongs to §11.4's
  section editor. On this screen it would sit beside the control that removes one dish while emptying a quarter of
  the menu, and §7 is explicit the two are different requests.
- **No heading description.** The record carries it because §11.1 needs it. *"Served until 11am"* is a sentence for
  a guest choosing, not a cook counting.
- **No `app.css` edit.** Two component-local rules, legitimate because `.kitchen-` is deliberately absent from
  `SharedSelectorPrefixes`. Reaching for a `.menu-group*` name would be the F-66 defect — a page-local rule sharing
  a shared name wins from later in the document and the stylesheet loses in silence.
- **No end-to-end scenario.** `/kitchen` has none at all, which this stage did not change and which is now recorded
  as a carried item in its own right.

### What is still open after this stage

**No scenario drives either resequencing verb**, which is the largest end-to-end gap in the menu: the 375px
barrier measures those controls and nothing presses them.

**`/kitchen` has no §16.3 scenario at all**, which is the largest end-to-end gap in the application. This stage
changed that surface and could not assert the change through a browser.

**The wide layout stacks each row's three controls** on the administration index. Carried on two registers.

**Ordering is complete for both tables, every menu surface groups by heading, and Stage 3 and 3a–3c are closed.**
The next stage is Stage 4.

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
