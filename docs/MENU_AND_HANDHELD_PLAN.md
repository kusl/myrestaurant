# Menu modernization and the handheld contract — staged plan

**Opened 2026-08-11, at the close of M6 Slice 30.** This
is the execution plan for the first enhancement request the project has received from a person who was
shown the running application, together with the defect that request arrived beside. It is a working
document: a stage is struck through when it lands, and the ruling paragraphs are the part worth keeping
afterwards.

**The header used to carry a *last moved* date and it does not any more.** It said *"Last moved 2026-08-18,
at the close of Slice 51"* while the document below it had been moved by Slices 52 through 59 — a date
maintained by habit, eight slices stale, in a document three gates read for structure and none reads for
meaning. It is deleted rather than corrected, on the ruling F-112 settled about a number written beside an
enforced copy of itself: the durable form of *when did this last move* is the struck-through stage headings
below, each of which names its slice, and `docs/BUILD_PROGRESS.md`, which is a log rather than a claim.

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

**~~No end-to-end scenario drives either resequencing verb.~~ Closed in Stage 3d, M6 Slice 61.** §16.3's
scenario 17 is extended after all, thirteen slices later: the barrier measured the controls and nothing
pressed them, which is exactly the gap this paragraph named.

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

**~~No scenario drives either resequencing verb~~ — closed, M6 Slice 61 (Stage 3d below).** It was the
largest end-to-end gap in the menu for thirteen slices: the 375px barrier measured those controls and
nothing pressed them.

**`/kitchen` has no §16.3 scenario at all**, which is the largest end-to-end gap in the application. This stage
changed that surface and could not assert the change through a browser.

**The wide layout stacks each row's three controls** on the administration index. Carried on two registers.

**Ordering is complete for both tables, every menu surface groups by heading, and Stage 3 and 3a–3c are closed.**
Stage 4a landed next, in Slice 51.

---

## ~~Stage 3d — a scenario that presses the resequencing controls~~ — **landed, M6 Slice 61**

**The oldest open item this plan carried, and it outlived four stages that were opened, worked and closed
around it.** Stage 3a shipped `ResequenceMenuSectionsAsync` and its two buttons in Slice 47. Stage 3b
shipped `ResequenceMenuItemsAsync` and its two in Slice 48, and closed with the sentence this stage exists
to discharge: *"No end-to-end scenario drives either resequencing verb. §16.3's scenario 17 is not
extended, so nothing asserts through a browser that a heading or an item moves. The barrier measures the
controls; nothing exercises them."* Stage 3c repeated it, Stages 4a–4f and 5a–5c carried it, and thirteen
slices later it was the only end-to-end gap left in the menu.

### Why it was so easy to carry, which is F-109's lesson arriving where F-109 predicted it

**The controls were never unasserted.** §16.3 scenario 16 has measured them since the slice each landed
in — where they sit, how tall they are, that they lie inside a 375px viewport — and Stage 3b even recorded
a small finding about that: `.record-actions button` had matched *nothing* until the item rows became the
first submit controls to render in one. So every re-reading of the open item met a page whose controls were
demonstrably on the screen, in a repository whose data-access layer asserts both verbs against a real
PostgreSQL. The deferral read as diligence, and the sentence justifying it was never re-examined, because
re-examination is not what re-reading is for.

**What the two instruments have in common is that neither presses anything.** A barrier measures where a
control *is*; an integration fact calls the write service directly. The gap between them is the whole of
what a browser adds, and naming it precisely is what makes the scenario worth its minutes.

### What only a browser can say, stated as the three claims

**A press reaches the form that owns it.** Every heading renders two static-SSR forms named from its own
identifier, and every item row two more from its own — so a three-heading, three-item menu carries twelve
distinct `@formname` values on one page. Blazor dispatches a POST to the form that names it; a dispatch
that routed a press to the wrong one would move the wrong heading and **report success**. Nothing below the
browser can see that, because nothing below the browser renders a form.

**An already-open menu re-orders itself.** `ResequenceMenuSectionsAsync` and `ResequenceMenuItemsAsync`
publish `MenuChanged` on `Resequenced` and on nothing else, and §11.1 renders headings and items in stored
order — so a resequence changes the shape of every open guest picker without a name, a price or an
availability flag having moved. That publish is asserted by `MenuWiringTests` against a fake broadcaster;
whether a phone at a table actually re-reads is a different claim.

**An item moves within its heading and nowhere else.** §7 makes a position a position *within* a heading,
so the index sends that heading's ordering and not the menu's. The failure this catches is a page that sent
the whole menu and renumbered the starters because somebody moved a pudding — and it is caught only by
asserting the heading that was *not* touched, which is why both halves are read.

### Three rulings about the shape of the scenario

**Scenario 17 is extended rather than a scenario 22 added**, on the reason Slices 59 and 60 both gave and
which is now this project's default: the arrangement already exists. Seventeen ends with three headings, a
two-item heading, an empty heading, and a guest whose circuit has been open across five broadcasts. A
scenario 22 would have cost a second container, a second passkey registration and a second join to arrange
what is already standing.

**Both directions of both controls, each with its restoration**, and the restoration is the stronger half.
A resequence writes **absolute** positions `0…n-1` over the list it was sent, so an implementation writing a
relative offset — or writing the right rows in the wrong order — gets the first move right and cannot get
the move back right as well. And a page that wired Up and left Down inert passes every assertion scenario 17
held before this stage.

**The count of `reordered` events is deliberately not asserted here.** One event per row that *actually
moved* is the no-op rule, and it belongs to `MenuSectionResequenceTests` and `MenuItemResequenceTests`,
which run against a real database and can count rows. A browser reading a *Recent activity* feed would be a
weaker instrument for the same claim, and the section half is not even visible there — `menu_section_event`
has no cross-section feed, on the standing rule that a read with no caller is a defect.

### One §11.4 ruling that turned out to have no assertion anywhere

**"Disabled rather than omitted" had never been checked.** Both controls are rendered on every group and the
one that would exchange with nothing is disabled, because a control that vanishes at the end of a list moves
every other control up a row on the render after a move — and scenario 16 measures where controls *are*. The
ruling is in §7, in §11.4 and in the component's own comment, and nothing in the tree had an opinion about
it: a page that omitted the edge control would have satisfied every existing assertion, including the
barrier's, whose floor counts controls and would simply have counted fewer.

It is discharged in two halves, deliberately. **Presence is the reader's**: `ReadMenuIndexAsync` refuses a
group carrying anything other than two move controls, so the omit-at-the-edge implementation fails in the
harness with a sentence naming the heading. **Enabled-ness is the scenario's**: the first heading offers no
Up, the last offers no Down, and the middle one offers both. The split is the same one `MenuCard` already
makes between §7's `disabled` and the chip beside it — whichever of the two is the contract is the one to
read.

### What is open after this stage

**`/kitchen` still has no §16.3 scenario at all**, which is now the largest end-to-end gap in the
application by a clear margin. Scenario 21 opens that board and presses one control on it, which is the
only thing any scenario has ever done there.

**The wide layout stacks each row's three controls** on the administration index. Carried on two registers
since Slice 47, and this stage pressed those very controls without touching `app.css`, so it is carried
again with one more slice of evidence that nobody minds enough to open the stylesheet.

**Nothing in this repository measures §11.1 at 375px.** Carried from Slice 60. The handheld barrier is
scoped to §11.4's surfaces, and the guest menu's box model changed in that slice.

**Ordering is complete for both tables, every menu surface groups by heading, and every verb in §7 now has
both a surface and a browser that presses it.** The menu enhancement's open list is empty for the third
time. **The next thing in this plan is still Stage 6**, and it is still not startable, for the three
reasons it gives below — none of which this stage changed.

---

## ~~Stage 4a — images: the schema and the data access~~ — **landed, M6 Slice 51**

**The recommendation below was `bytea` in PostgreSQL, and that is what was built.** ADR-0015 carries the
rulings; `0006_menu_item_images.sql` carries the schema; §7 and §8.2 carry the mechanism.

**The cut is `0003`'s, a second time.** Two new tables, nothing existing touched, so every read, every write,
every integration fact and all seventeen §16.3 scenarios mean exactly what they meant before — green by
construction rather than by inspection. `OrderTestWorld.TruncateAsync` needed **no edit at all**, because
`TRUNCATE … CASCADE` on `menu_item` reaches both new tables.

### What was built

`menu_item_image` — `menu_item_image_identifier`, `menu_item_identifier` (`NOT NULL UNIQUE`),
`content_type`, `bytes bytea`, `uploaded_at`, plus a media-type vocabulary CHECK and **two** byte-length
CHECKs. `menu_item_image_event` — `attached | replaced | removed`, `new_content_type` and `new_byte_length`
bound to the first two by named biconditionals, referencing `menu_item` rather than the image.

`IMenuItemImageDirectory` (`ListAsync`, `FindForItemAsync`, `ReadContentAsync`) and
`IMenuItemImageAdministration` (`AttachMenuItemImageAsync`, `RemoveMenuItemImageAsync`), plus
`MyRestaurant.Domain.Menu.ImageFormat`, which decides what a run of bytes is from its own signature.

### Five places this differs from the sketch below, and each is a ruling

**1. There is no `byte_length` column, and no `pixel_width` or `pixel_height`.** That is **F-101**, and it is
recorded as a finding rather than a refinement because of where the sketch lived: three gates read this
document — for table structure, version agreement and hygiene — and none of them reads a fenced SQL block for
meaning, so a DDL sketch in a plan is authored prose with the authority of a schema and none of the checking.
`byte_length` is `octet_length(bytes)`, one fact written twice where one `UPDATE` can separate them.
`pixel_width` and `pixel_height` are worse: point 3 of the sketch below says the server stores what it is
given, so neither number could ever have come from anywhere but the uploading browser's word — recorded in
the indicative, beside columns the database actually knows. `new_byte_length` **is** kept on the event table,
and the asymmetry is the point: after a removal the bytes are gone, so the log is the only place that number
can live.

**2. The size cap is written once, in the DDL, and reported by constraint name.** No number appears in C#:
the write catches the check violation, compares `menu_item_image_bytes_within_cap`, and answers
`BytesOverCap`. Two constraints rather than one bounded `BETWEEN`, because an empty file and a
four-megabyte photograph need different sentences and the constraint name is what carries the difference
back up. `MenuItemImageTests` reads the bound out of `pg_get_constraintdef`, so a migration that moves the
cap moves the test with it.

**3. The row carries no actor.** `menu_item` and `menu_section` both record `created_at` and no
`created_by`; the actor is the event log's. So `uploaded_by_person_identifier` left the sketch too, and
`menu_item_image` references only `menu_item`.

**4. `menu_item_image_event` references `menu_item`, not `menu_item_image`.** A replace mints a new
identifier and drops the old row, and a removal drops it outright, so the row an event describes is gone by
design — a foreign key to it could only forbid the deletion or cascade the history away with the bytes. The
image is named as a bare `uuid`, which is the **opposite** of `0005`'s ruling about
`new_menu_section_identifier`, and opposite for a stated reason: there it is a pointer §11.4 renders, here it
is evidence that the URL changed.

**5. The declared media type is checked against the bytes.** Not in the sketch at all, and it is the one
addition rather than a subtraction. §7's route hands the stored `content_type` back out as a response header
on this application's own origin, so a column that disagreed with its bytes would make this program mislabel
its own responses. `ImageFormat` lives in `Domain` on **F-100's** argument — a pure function of a byte span,
whose interesting cases are the malformed ones, each of which behind an `INSERT` would cost a container and
arrive as a constraint name instead of a sentence. **Both halves of WebP's RIFF header are required**, since
`RIFF` alone is also an AVI and a WAV.

### What is open after this stage, and one of it is a real question rather than a deferral

**No surface reads or writes a picture, and that re-opens the obligation Slice 43 closed.** Two data-access
services now exist with no caller outside their integration tests, which is the state
`IMenuSectionAdministration` was in from `0003` until the section editor. It is the weaker form — **nothing
is added behind `IMenuWorkflow`**, so no surface can change a picture without announcing it for the reason
that no surface can change one at all — and it is named on every slice until 4b discharges it.

**How an upload reaches a static-SSR page is not settled, and it is Stage 4b's first decision.** §11.4's
administration pages are static SSR with form posts; Blazor's `InputFile` needs an interactive render mode,
and `[SupplyParameterFromForm]` does not bind a file. So 4b has to choose between a plain
`<form enctype="multipart/form-data">` posting to a minimal API endpoint beside `AccountEndpoints`, and
making one page interactive. **This was not foreseen in the sketch below** and it is the reason Stage 4 was
cut here rather than shipped whole.

**Whether a browser downscales before upload is still the open question point 3 named.** A phone camera
produces four megabytes against a 512 KiB cap, so without it the answer to most uploads is *too large*. It
is `wwwroot/js/` and a `<canvas>` round trip, it changes no schema, and it is the thing that decides whether
this feature is usable by the person it was asked for by.

---

## ~~Stage 4b — images: the route and the administrator's form~~ — **landed, M6 Slice 52**

**Three of the four consequences below are settled and the fourth became Stage 4c.** The route exists, the
policy question is asserted rather than assumed, the transport is decided — and §11.1's thumbnail is split
off, because it is the one of the four that is about the *guest's* screen rather than about getting bytes
in and out.

### The transport, which was the open question, and the answer neither option named

The two candidates were a multipart form posting to a **minimal API endpoint** beside `AccountEndpoints`,
and making **one page interactive**. Neither was taken.

A plain `enctype="multipart/form-data"` form posts to **the page itself** under an ordinary `@formname`,
and the handler reads the part back out of `HttpContext.Request.Form.Files`. That costs nothing, and the
reason is the part worth keeping: Blazor's static form handling **has already read the body** in order to
find `_handler` and dispatch to the right callback, so `Request.Form` is a cached collection by the time a
handler runs. `[SupplyParameterFromForm]` refuses one field; the request it refuses to bind is sitting
right there.

**What that buys, stated as what the two alternatives would have cost.** An endpoint would have acquired an
authorization rule of its own that has to agree with `ManageMenuItem`'s `[Authorize]` — two places that can
disagree about who may change a menu, which is the shape §3.7 exists to prevent. An interactive page would
have put a circuit under §11.4's largest form surface to move one file, and would have made this the only
administration page whose forms behave differently from every other one's.

**What it gives up: model binding for exactly one field**, which is the field the model binder refuses.

### The rest of what landed

`GET /menu/image/{menu_item_image_identifier}` — **anonymous**, because §11.1's guest menu is what it exists
for and §4.3 puts registration at the moment of joining a table, so a guest reading a menu may have no
session at all. 404 for an identifier the table does not hold, which is what a URL naming a picture since
replaced or removed becomes. `Cache-Control: public, max-age=31536000, immutable`, **true** because the
route is keyed on the image. **No §3.5 obligations exemption**, unlike the clock and the source offer: those
are asked for *by* a page a locked-down principal is looking at, where this is a subresource of a page such
a principal was redirected away from before it rendered.

`AttachMenuItemImageAsync` and `RemoveMenuItemImageAsync` behind `IMenuWorkflow`, publishing on the **two
outcomes that wrote a row** rather than on "not a refusal" — that enum's refusal set has grown twice
already, and a member added to it must not become an announcement by default. **This discharges the
obligation Stage 4a re-opened**, on the schedule it was re-opened with.

**No number was added anywhere.** The cap stays §8.2's named CHECK, reported by constraint name; the
ceiling on what the process buffers is the one every other POST in this application already has, so the
picture form declares no transport limit of its own.

**The browser's declared media type is handed on unaltered**, and the surface deliberately does not identify
the format itself even though `ImageFormat` is public and could. The write is the one place that decides
what an image is, and a surface that pre-judged would leave two of that verb's outcomes unreachable from the
only form that can produce them.

### What is open after this stage

**`IMenuItemImageDirectory.ListAsync` has no caller.** It is §11.1's, and §11.1 is Stage 4c. Named on the
same rule that named the write, and weaker than it: an unread read cannot change anything without telling
anybody.

**A browser that sends `application/octet-stream`** for a genuine PNG — an operating system with no
extension mapping — gets *"not a picture format this menu serves"*. The message names what the browser sent,
so the failure is diagnosable rather than mysterious, and the fix if it ever bites is one line: identify
from the bytes and pass that. It is not taken pre-emptively, because it would make two of the write's
outcomes unreachable from the only surface that can reach them.

**No §16.3 scenario.** The seventeen are unchanged. A picture scenario needs a fixture image the harness has
no way to produce yet, and inventing one inside the harness would be a test arranging bytes it also asserts
about. Named rather than quietly skipped.

---

## ~~Stage 4c — images: the guest's menu~~ — **landed, M6 Slice 53**

**Landed, and it was the half that was actually asked for.** §11.1's thumbnail. Two things have to be
decided together and both are about a 375px screen rather than about bytes:

1. **The card, re-laid out rather than decorated.** An image per card doubles the height of the menu. A
   thumbnail *beside* the name rather than a hero above it, and `loading="lazy"` on everything below the
   first section.
2. **`alt_text`.** One `ALTER` with a `DEFAULT ''` on `0004`'s precedent. §11.4's own panel needs none and
   renders `alt=""` deliberately — the picture sits under the item's name in the page's `<h1>`, so
   alternative text there would make a screen reader read the dish twice — but a guest's card may carry a
   picture that says something its name does not.

   **Two clauses that used to end this point were wrong, and the first is F-103.** It read: *"an `<img>`
   with no alternative text on a menu is a card a screen reader renders as nothing."* That conflates a
   **missing** `alt` attribute, where a screen reader falls back to announcing the URL — here a bare
   UUIDv7 — with `alt=""`, which marks an image decorative and is correctly **skipped**. §11.1's card is a
   `<button>` holding the dish's name and its price as text, so the button's accessible name is already
   *"Grilled salmon £24.00"*: sufficient, and `""` is therefore the **right** value for most pictures on
   this menu. The column is kept for the narrower and true reason above, and the sentence is corrected
   rather than deleted because the cheaper direction was available (F-77's habit, as for F-100). The second
   clause said the caption is *"a field on the item's picture form"*; it is **its own form and its own
   verb**, because the upload form requires a file and a caption settable only there would make correcting
   a typo cost a re-upload — see below.

### What landed, and the four places it differs from the text above

**The thumbnail is a fixed 4rem square under `object-fit: cover`.** Point 1 said *beside the name* and
stopped there; the size and the crop are this slice's. `height: auto` was the obvious alternative and it
defeats the point: nothing in this stack can resize an image, so a portrait photograph would render twice
as tall as it is wide and put the card height straight back where the whole re-layout started. **What the
crop costs is the edges of the frame**, and it is paid back in the detail panel, which renders the picture
uncropped — §11.1 has named that panel the surface images are read on since Slice 39, so it was already
owed one. Reverting the crop is one declaration and the consequence is written beside it.

**`loading="lazy"` is per heading, not "everything below the first section".** Those are the same rule; what
the plan did not say is why the cut is not per *card*. How many cards sit above the fold depends on how long
each description is, how wide the viewport is and how large the reader has set their text — none of which
the server knows. A heading is the coarsest unit that is certainly right at one end, and a card count would
be a number invented here and wrong on somebody's phone. `eager` is written out rather than left to the
default, so that the next reader can tell a decision from an oversight.

**The caption is its own verb, and `AttachMenuItemImageAsync`'s signature did not change.** Point 2 called
it *a field on the item's picture form*, which would have been a sixth parameter on the attach. It is
refused for a concrete reason: the upload form requires a file, so a caption settable only there makes
correcting a typo cost a re-upload — a new `menu_item_image_identifier`, every cached copy of an unchanged
photograph invalidated across the building for a year, and a `replaced` event recording a replacement that
replaced nothing. So `0007` adds `alt_text_changed`, `SetMenuItemImageAltTextAsync` writes one `text`
column, and **the attach carries the caption forward** from the row it deletes onto the row it writes,
without an event, because nothing about the caption changed. Somebody replacing a photograph of the salmon
with a better photograph of the salmon has not withdrawn what they wrote about it.

**The `<img>` is last in the document and first on the screen**, which nothing above anticipated. A button's
accessible name is computed from its contents in document order, so a captioned picture placed first
announces its own description *before* the dish it describes. `grid-column` moves it into the left column;
reordering a non-interactive element visually is free, because there is no focus order for the visual order
to disagree with — and that is exactly why the same trick would be wrong for a control.

### What is open after this stage

**No §16.3 scenario touches a picture.** The seventeen are unchanged. A picture scenario needs a fixture
image the harness has no way to produce, and inventing bytes inside the harness would be a test arranging
what it asserts about. It is now the largest end-to-end gap in the menu after the two resequencing verbs.

**~~Nothing reads `menu_item_image_event`.~~ Closed in Stage 4d, one slice later** — the panel and its
reader arrived together, as `IMenuSectionEventLog` did with the section editor and on the rule this item
was named under.

**Whether a browser downscales before upload.** Fourth slice carried, and it has stopped being a nicety: a
phone camera produces four megabytes against a 512 KiB cap, so the answer to most uploads is still *too
large*, and now the pictures those uploads would have become are the feature a guest sees. It changes no
schema — a `<canvas>` round trip in `wwwroot/js/` — and it is the thing that decides whether this stage is
usable by the person who asked for it.

**A browser that sends `application/octet-stream` for a genuine PNG is refused.** Carried with the fix
written down.

**No `alt_text` on the administrator's own thumbnail, deliberately.** It renders `alt=""` and shows the
caption as visible text instead. Not an omission and not open — recorded so it is not repaired by mistake.

### The four consequences this stage was planned from

Three are discharged above; the fourth is point 4. They were named before Stage 4a was written:

1. **The route and its caching.** `GET /menu/image/{menu_item_image_identifier}` — already satisfied by the
   schema, which is what decision 2 of ADR-0015 bought. `Cache-Control: public, max-age=31536000, immutable`
   is truthful because the identifier changes with the bytes.
2. **The content security policy needs no change, and that must be asserted rather than assumed.**
   **Settled, Slice 52.** §11.11 carries `img-src 'self' data:`, so `'self'` already covers bytes this
   application serves. **F-49's whole lesson is that a CSP is the one configuration that becomes wrong by
   editing a file it does not mention**, so `ContentSecurityPolicyContractTests` carries the fact in two
   halves — that the directive admits `'self'`, and that every `<img>` in the tree is still same-origin.
   The second is the one that would fail, because a CDN or a thumbnail service is exactly what somebody
   reaches for when a menu grows pictures.
3. **The upload transport**, which was the genuinely open one. **Settled, Slice 52** — see above.
4. **The 375px layout.** An image per card doubles the height of the guest menu. A thumbnail beside the name
   rather than a hero above it, `loading="lazy"` on everything below the first section, and an `alt_text`
   column — one `ALTER` with a `DEFAULT ''`, on `0004`'s precedent — because an `<img>` with no alternative
   text on a menu is a card a screen reader renders as nothing.

### The sketch this stage was planned from, kept for the argument rather than for the DDL

**The recommendation — `bytea` in PostgreSQL, one image per item, hard size cap — was accepted and is
ADR-0015.** The reasoning is kept here because it is the part that transfers; the DDL below is **superseded
by §8.2** and is left as written so that F-101's row has something to point at.

The alternative was a volume on disk, and the argument against it is F-38's. §15 *defines* a recovery set as
exactly two files — the database dump and the Data Protection key ring — and `restore_drill.sh` gates both
on every push. A third artefact means editing that definition, both scripts, the drill, and the runbook; and
an operator who takes a backup the old way from then on has a set that restores an application whose menu
has no pictures in it. Object storage is worse: MinIO is a service, S3 is a paid dependency, and both
contradict R§1's self-hosted premise.

The cost of `bytea` is honest and small at this scale: sixty items at 200 KB is 12 MB, inside a `pg_dump
-Fc` that already compresses, on a database whose whole reason for existing is one restaurant.

```
menu_item_image                                            -- SUPERSEDED: see §8.2 and F-101
    menu_item_image_identifier  uuid PRIMARY KEY
    menu_item_identifier        uuid NOT NULL UNIQUE REFERENCES menu_item   -- one image per item, in v1
    content_type                text NOT NULL CHECK (content_type IN ('image/jpeg', 'image/png', 'image/webp'))
    byte_length                 integer NOT NULL CHECK (byte_length BETWEEN 1 AND 524288)   -- F-101: dropped
    pixel_width                 integer NOT NULL CHECK (pixel_width BETWEEN 1 AND 4096)     -- F-101: dropped
    pixel_height                integer NOT NULL CHECK (pixel_height BETWEEN 1 AND 4096)    -- F-101: dropped
    bytes                       bytea NOT NULL
    uploaded_by_person_identifier uuid NOT NULL REFERENCES person            -- dropped; the actor is the log's
    uploaded_at                 timestamptz NOT NULL
```

Plus `menu_item_image_event` (`attached | replaced | removed`), because every other mutation in this schema
leaves a log and an image is the one a guest sees.

**No resizing, and say so.** There is no free-libre .NET image library in this stack — ImageSharp's licence
is not AGPL-compatible for this use and SkiaSharp is a native dependency in a rootless container. So the
server validates and stores what it is given, and the size cap is the whole defence. Whether a browser
downscales before upload (a `<canvas>` round trip, perhaps 60 lines of `wwwroot/js/`) is the open question of
this stage, and it is a real one: a phone camera produces 4 MB and the cap above is 512 KB, so without it the
answer to most uploads is "too big".

**Reversible if the recommendation is wrong.** `bytea` → volume is a migration that reads rows and writes
files; volume → `bytea` is a migration that cannot find the files. Choosing the reversible direction first is
the whole reason to choose now.

---

## ~~Stage 4d — images: the picture's history~~ — **landed, M6 Slice 54**

**Not in any earlier version of this plan, and it is not scope creep — it is the item Stages 4a, 4b and 4c
each carried by name.** `0006` created `menu_item_image_event`, three slices wrote to it, `0007` made it four
event types deep, and **nothing in the tree could read it**. §16.4 recorded the absence of
`IMenuItemImageEventLog` on each of those slices under the standing rule that a read arrives with the surface
that renders it; the integration facts that needed the history selected from the table directly and said so
rather than hiding it. So §11.4 could not answer *when did this photograph last change, and who changed it*
— and `alt_text_changed`, added in Stage 4c, was written from its first day and rendered nowhere.

It is filed as its own stage rather than folded back into 4c because *what a picture used to be* is a
different question from *the thumbnail a guest sees*, which is the same shape of reason Stage 3c was filed
apart from Stage 3.

### The one thing in this stage worth reading twice

**The reader must not join `menu_item_image`, and that is the whole design rather than an optimisation.**

Every other reader in this family joins the row its events are about. `DapperMenuEventLog` joins `menu_item`,
`DapperMenuSectionEventLog` joins `menu_section`, and both are right to: those rows are never deleted, because
§6.8's answer to *get rid of it* is a flag. **A picture is the stated exception.** A replace mints a new
`menu_item_image_identifier` and deletes the old row — required, so that §7's route can carry
`Cache-Control: immutable` as a *true* statement — and a removal deletes the row outright. So an event on this
table names a row that, in the ordinary case, is **gone**.

Which makes both joins a maintainer reaches for wrong, and wrong in the worst available way:

- an **INNER JOIN** returns only the events about whichever picture is attached *now*, so the history
  silently **begins at the current photograph**. It reads like a complete history. Nothing in the application
  fails.
- a **LEFT JOIN** returns everything and adds a column null on every row but the newest, which is a column
  about this schema rather than about the restaurant.

`0006` declared no foreign key on that column precisely so the log can outlive its subject, and its own
comment says the identifier is *"not a pointer to a row a reader can open; it is the evidence that the URL
changed"*. This stage asserts that rather than trusting the comment: `MenuItemImageEventLogTests` replaces a
picture, removes the replacement, and requires all three events back with the two identifiers compared
**individually** — because *all three rows came back* would also pass on a reader that returned three rows
carrying one identifier.

### What landed, and the three places it decided something this plan had not

**The panel renders whether or not a picture is attached now**, and that is the placement rather than a
detail. *No picture now, three of them previously* is precisely the state §11.4 could not describe before this
stage, so hiding the panel when nothing is attached would hide it in the one case somebody opens the page to
ask about. It sits under the picture forms as an `<h3 class="manage-subheading">` — a subsection of the
Picture panel rather than a peer of Section and Position — and the item's own event history stays at the foot
of the page, because that one is about the dish.

**No identifier and no link is rendered, although every row carries one.** A URL for a replaced picture
answers 404 by design (§7), so a link would be a link that mostly does not work, and a bare UUIDv7 in a table
cell is a fact about this schema rather than about the restaurant. What the identifier is *for* is making it
legible that a replacement produced a **new address**, and the words *Replaced with a new picture* say that
without it.

**What a picture WAS goes inside the sentence rather than into columns of its own.** The format and the size
are carried by `attached` and `replaced` and by neither of the other two, and after a removal the event is the
only record of them (F-101) — so they are worth rendering, and worth rendering on exactly half the rows. Two
columns empty half the time would put a `data-label` reading *Format* beside nothing on the row somebody came
to read, which is the card-layout failure §11.12's label rule exists to prevent. Three columns, the same three
the item history uses, and **no new CSS at all**: `.record-list`, `.manage-subheading` and `data-label` are
§11.12's existing vocabulary, and `.manage-subheading` was already declared and already used by
`ManageSitting.razor`.

### A defect found on the way in (F-105)

Reading §7 in order to write the reader turned up the specification describing this table as
`attached | replaced | removed`, CHECK-bound by **two** named biconditionals. `0007` had made it four types
and three, one slice earlier. **Three sections above it**, §7 states in bold that `menu_item_event`'s
vocabulary is *not counted in prose anywhere* and that the list there is the only copy — a rule written down
after F-77 and then not applied to the table this project added next. F-93's timing for the fifth time:
caught in the slice that would have consumed it.

The list is corrected and the count deleted, and — because a corrected sentence is worth nothing when the next
migration will be written by somebody who has not read it — `MenuEventVocabularyContractTests` gains a fact
that every type the migration admits has a sentence on the surface that renders that log. Its subject is the
**surface**, because §11.4 falls back to the raw string for an unrecognised type *by design*, so a missing arm
throws nothing and shows up only as a cell reading `alt_text_changed` where a sentence belongs.

### What is open after this stage

**~~Browser downscaling is the only thing left between this feature and the person who asked for it, and it
is named here as the NEXT slice rather than as a fifth deferral.~~ Landed as Stage 4e in Slice 55**, and both
of the things named as making it a real slice turned out to be true — the multipart form is handed the
downscaled file through a `DataTransfer` rather than through anything the form knows about, and §11.11 was
the first thing checked. It needed no change, because `createImageBitmap` and `canvas.toBlob` between them
never produce a URL.

**~~No §16.3 scenario touches a picture.~~ Closed in Stage 4e — and this paragraph is the reason that stage
had to happen when it did.** The objection was half right. A checked-in photograph would be an opaque blob and
a base64 constant would be the same blob with worse ergonomics; but *inventing bytes* was never the problem,
because nothing downstream asserts anything about the bytes. `PictureFixtures` generates a real PNG at any
size, and scenarios **18** and **19** are the result. **The cost of leaving it open for four slices is
recorded in the ledger as F-106**: a `ValidationMessage` outside its `EditForm` made every menu item with a
picture on it answer HTTP 500, the upload having succeeded first, and the operator found it.

**A browser that sends `application/octet-stream` for a genuine PNG is refused.** Carried with the fix written
down, for a fifth slice.

**There is deliberately no cross-item picture feed** to match `IMenuEventLog.ListRecentAsync`. That one fills
a panel on `/administration/menu` and there is no such panel for pictures, so inventing a read with no caller
in the slice whose subject is a read that finally has one would be a poor joke.

---

## ~~Stage 4e — images: a picture a phone can actually upload~~ — **landed, M6 Slice 55**

**The stage this whole feature was for.** §8.2 caps a stored picture at half a megabyte and a phone camera
produces four, so for four slices the honest answer to almost every real upload was *too large*. The schema
was right, the write service was right, both surfaces were right, and the feature was unusable.

### Why the resizing is in the browser, stated as a constraint rather than a preference

Nothing on the server can do it. There is no free-libre .NET image library available to this stack for this
use — ImageSharp's licence does not admit it, SkiaSharp is a native dependency inside a rootless container —
which is why `ImageFormat` reads signatures and never decodes, and why `0006` deliberately stores no
`pixel_width` and no `pixel_height` (F-101). The one decoder every guest and every member of staff already
has is the browser's, so `wwwroot/js/menu-picture.js` decodes the chosen file, redraws it into a `<canvas>`
no larger than a declared longest edge, re-encodes as JPEG down a ladder of dimension-and-quality pairs
until one fits, and replaces the file input's selection through a `DataTransfer`. The multipart form, the
antiforgery token and the post/redirect/get are untouched.

### The four rulings

**It never refuses anything.** Every refusal is still the write service's and the schema's, and every
failure path leaves the operator's chosen file exactly where it was so the server answers. A downscaler that
refused would be a second authority on what may be stored, in the one place an attacker controls entirely.

**A picture already inside the cap is left completely alone**, bytes and declared media type both — §7
stores what it was given, and re-encoding something that fits would throw away quality to solve a problem
nobody has.

**The budget is read, never written (F-107).** `IMenuItemImageDirectory.ReadDeclaredByteCapAsync` asks
`pg_get_constraintdef` for `menu_item_image_bytes_within_cap`'s bound and the page splats it onto the input
as `data-picture-byte-budget`. **The attribute's presence is the switch**: a `null` cap renders no attribute
and the mechanism turns itself off. No file under `src/` contains the number, comments included, and the
twelfth fact on `MenuItemImageSurfaceContractTests` computes that claim out of the migration itself.

**JPEG rather than WebP**, although `0006` admits both. WebP is smaller at equal quality; the stored bytes
are served back to whatever a guest is holding, and a picture that will not decode on an older handset at a
table is worse than one forty kilobytes larger. The canvas is filled white first, because JPEG has no alpha
and a transparent PNG would otherwise re-encode onto a ground that renders black.

### The defect this stage was scheduled behind (F-106)

**The upload was already broken and nothing in the repository knew.** `ManageMenuItem.razor` carried
`<ValidationMessage>` one line outside its `EditForm`, inside `@if (_picture is not null)`. A sibling of an
`EditForm` gets no cascading `EditContext` and `ValidationMessage` throws without one — so the attaching
POST, which renders while `_picture` is still null, **succeeded and committed**, and the redirected GET
answered **500**. Every administrator view of a decorated item answered 500 thereafter, including the one
carrying the Remove button, so nothing in the product could undo it.

It is filed here rather than in a stage of its own because the two are one story: the reason nobody saw it
is the reason this plan gave for deferring a picture scenario four times, and both are closed together.

### What is open after this stage

**A browser that sends `application/octet-stream` for a genuine PNG is refused.** ~~Carried for a sixth
slice~~ — **closed in Stage 4f below, M6 Slice 56.** The narrowing recorded here was correct and was the
reason it stayed easy to defer: a downscaled picture arrives as `image/jpeg` whatever it was labelled, so
only files already under the cap still reproduced it.

**Nothing here resizes on the guest's side of the menu.** §11.1 renders the stored bytes at whatever size
they are, inside `.order-menu-thumbnail`'s own box. With a 1600px longest edge that is a handset
downloading rather more than it displays. A `srcset` would need stored variants, which is a schema change
and a second copy of every photograph; revisit only if somebody measures it on a real service.

---

## Stage 4f — images: the bytes decide the format — **landed, M6 Slice 56**

**The oldest open item this plan has carried, and it was never a hard problem.** Stage 4b recorded it in
its own "what is open" list with the fix written out in full — *identify from the bytes and pass that* —
and it was then copied forward, unchanged, into Stage 4c's list, Stage 4d's, and Stage 4e's. Six slices.
An administrator whose desktop has no MIME mapping, whose browser handed the form a file from a document
provider, or who simply saved a screenshot without an extension, uploaded a perfectly good PNG and was
told it was *"not a picture format this menu serves"*.

### Why the deferral held for so long, which is the part worth keeping

The reason given each time was one sentence: taking the fix *"would make two of the write's outcomes
unreachable from the only surface that can reach them"*. **That sentence is true and it is not a cost**,
and the difference is the whole ruling. `UnsupportedContentType` and `ContentTypeContradictedByBytes` are
the write service's answers, and the write service is a **library** — its contract is for any caller, and
`MenuItemImageTests` reaches both of them directly, without a surface, on every integration run. An
outcome no *form* can produce is not an outcome nothing tests.

There is a real version of the worry underneath it, and it is answered rather than dismissed: a surface
that decided for itself what an image is would be a **second authority** on what may be stored. That is
F-64/F-69's mechanism and it would be right to refuse. But what the surface passes is the answer of
**the same pure function the write consults** — one decision procedure called twice, not two that can
disagree. The write still checks the census and still checks the signature; both are now true by
construction rather than by luck, which is a stronger arrangement than the one being replaced.

**The general lesson is about deferrals rather than about media types, and it is in the ledger as
F-109.** A recorded fix with a recorded reason is comfortable to carry: it reads as diligence every time
it is re-read, and the justifying sentence is never re-examined, because re-examination is not what
re-reading is for. F-62 established that a reason for not doing something is a claim about the tree and is
checked before it is written down. This adds the other half: **a claim used to defer is re-checked each
time it is used again.**

### The three rulings

**Unidentifiable bytes are handed on as the empty string rather than refused at the surface.** The empty
string is in no census, so the write answers `UnsupportedContentType` exactly as it did and this page
renders the sentence for it. A local refusal would have been shorter code and would have made this form
the first place in the application able to turn an upload away without the write service having seen it.

**`ContentTypeContradictedByBytes`'s arm is kept although nothing can now produce it here.** Deleting it
would answer a future refusal with a redirect and silence, which is the worst failure available on an
upload surface. A surface's defence in depth is not a second opinion about what may be stored.

**The refusal names the formats by rendering the census, not by spelling them.** Found on the way in
(**F-110**): the refusal said *"Choose a JPEG, a PNG or a WebP"* and the paragraph above the form said
*"JPEG, PNG or WebP"* — a fourth and fifth declaration of the vocabulary, both invisible to every gate,
both silently wrong the day a migration admits a fourth format. It renders **media types** rather than
English names deliberately: a map from `image/webp` to *"a WebP"* would itself be the copy being removed,
and the operator now sees exactly the list their file picker was filtered by.

### The build break this stage shipped behind (F-108)

Stage 4e's fixture generator declared a `stackalloc` inside a loop. CA2014 — and the reason it shipped is
that `Directory.Build.props` deliberately leaves warnings non-fatal for a plain `dotnet build` and makes
them errors under the flag CI passes, so the same defect is one line of scrollback locally and a halt in
the pipeline. `dotnet test` returned **1256** green, exactly as predicted, while `scripts/ci_local.sh`
stopped at step 5. It is filed with this stage rather than in one of its own because the two are one
delivery, and because the honest lesson is about which verdict counts rather than about spans.

### What is open after this stage

**Nothing about the picture feature.** Stage 4's open list is empty for the first time since 4a — the
guest-side `srcset` question named in 4e is not carried forward as an item, because it was explicitly
conditioned on somebody measuring it on a real service and nobody has.

**The next thing in this plan is Stage 5**, and the two rulings it needed are made below rather than
carried. That is F-109's other half applied on the first opportunity: a claim used to defer is re-checked
each time it is used again, and a ruling that costs nothing to make is not a deferral at all.

---

## ~~Stage 5a — likes: the schema and the data access~~ — **landed, M6 Slice 57**

**Both of Stage 5's open rulings are made, and neither cost anything to make.** They had been sitting in
this plan since it was written, described as *"two rulings needed before it ships"* — and re-reading them
in the slice that had to act on them took under an hour, which is the argument for making a decision at
the moment it is cheap rather than at the moment it is forced.

### The two rulings

**Who sees the count: staff.** The plan's own instinct was right and is now the rule. A count of 3 on a
menu of sixty items is noise that makes a restaurant look empty, and the number's only honest audience is
the person deciding what to stock. So *which of these is popular* is §11.4's question and *which of these
do I like* is §11.1's — **two reads over one fold**, rather than one read handing every guest the
restaurant's opinion. The guest still needs their own press back, or the control is an affordance with no
feedback; what they do not get is everybody else's.

**Whether a like requires having ordered the item: no.** This is the ruling worth reading, because it is
the one Stage 6 will inherit. `order_current_line` records what somebody **ordered**, not what they ate,
and a table shares — so the requirement refuses the case it most wants to admit (*I ate my partner's
dessert and it was the best thing on the menu*) while admitting the one it wants to refuse (*I ordered it,
sent it back, and liked it anyway*). It would also make a menu write read order history, which inverts
§6.5.4's direction: an order prices itself **from** the menu, and nothing in the menu has ever looked the
other way. What bounds the number instead is the door — §4.3 authenticates every person at a table and R§8
permits no anonymous ordering — so a like is one authenticated person's press. **And the decision is
reversible additively rather than baked in:** if the restriction is ever wanted it belongs on the *read*,
as a second and narrower count, not on the write as a refusal a guest at a table has to be given a
sentence about.

### What landed

`0008_menu_item_reactions.sql`, one new data-access file, two registrations, nine integration facts and
one wiring fact. **No surface**, which is the whole of the cut and is Stage 4a's cut applied a second
time: the schema and the reads first, the two surfaces second, because they belong to different people
and neither is a small page.

- **`menu_item_reaction_event`** — `liked | unliked`, the person, the dish, the instant. Two types, **no
  payload columns and therefore no paired biconditional**: each type carries its own name and nothing
  else, which is `menu_item_image_event`'s `removed` and is the whole of `order_visibility_event`.
- **`menu_item_reaction_current`** — `DISTINCT ON (menu_item_identifier, person_identifier)`, the last
  press from each person about each dish. `order_visibility_current`'s shape with a two-column partition.
- **`IMenuItemReactionDirectory`** — `ListLikeCountsAsync` for §11.4, `ListLikedByAsync` for §11.1. Both
  are whole-menu reads and neither takes an item, on `IMenuItemImageDirectory.ListAsync`'s argument: a
  per-item lookup inside a render loop turns a sixty-dish menu into sixty queries.
- **`IMenuItemReactions.SetLikedAsync`** — locks the `menu_item` row, compares against the fold, appends
  when that is a change and nothing when it is not.

### The four rulings the schema needed, and each is a thing a later reader would repair by mistake

**It is an event table and not a row per like.** The small schema is `(menu_item_identifier,
person_identifier)` `UNIQUE` with a `DELETE` to withdraw, and it gets every visible answer right — the
fold, the count, the guest's own state — while destroying the record. R§6.8 makes this system append-only
because a record that can be removed is a record nobody can audit, and §6.8's hide-never-delete rule has
exactly **one** stated exception, the image bytes, granted because the history of those lives in a log
beside them. A like has no log beside it, because the log **is** the record.

**There is no `actor_person_identifier`**, which every other event table in this schema has. Elsewhere the
subject and the actor genuinely differ: an administrator renames somebody else's dish, and §11.1 renders
*"kitchen removed Salmon"*. A reaction's subject **is** the person reacting, and no surface in §11 could
offer to press it on somebody else's behalf, so an actor column would be constrained to equal its
neighbour on every row that will ever exist.

**There is no count column and no count view.** `SELECT menu_item_identifier, count(*) FROM
menu_item_reaction_current WHERE is_liked GROUP BY 1` is one line, and §8.3's views exist to give the
application a shape it would otherwise assemble from event tables by hand rather than to save a `GROUP
BY`. A `like_count` on `menu_item` is refused one register harder, on F-101's reasoning: a stored total
beside the rows it totals is one fact written twice, in the one table in this schema that grows a row
every time a thumb moves.

**The fold's identifier tie-break is load-bearing here in a way it is not on `order_visibility_current`.**
Nobody hides an order twice in one millisecond. **Everybody taps a heart twice**, and one transaction
stamps its rows with one `IClock.UtcNow` (§8.1), so two presses genuinely share an `occurred_at`. Without
the tie-break `DISTINCT ON` returns whichever row the scan reached first — the *oldest* — and a double-tap
reads back as the state before it. It is an answer only because §8.1 requires `IIdentifierFactory` to
ascend inside a millisecond, which is the property **F-95** found nothing was keeping. That is the fact
this stage contributes that nothing else in the tree has, and it has an integration assertion of its own.

### A reaction publishes nothing, and it is the first menu write not behind `IMenuWorkflow`

Stated here rather than left to be inferred from an absence. §9's `MenuChanged` means *re-read the menu*.
A like moves no name, no price, no heading, no position, no availability flag and no photograph — there is
nothing for a picker to re-read — and this is **the one write in this application that can fire many times
a minute at one table**, so a broadcast would make one thumb re-read the whole menu on every phone in the
building. The presser's own feedback needs no announcement either: a static surface re-renders through
post/redirect/get and an interactive one re-renders itself.

`MenuWiringTests`' standing fact is therefore narrowed to say what it always meant — the workflow covers
every write **that changes the menu** — and the two reaction services get a registration fact of their own
rather than a line in that one, because adding a write that is deliberately outside the workflow to the
fact whose name says it covers every write would make that fact assert its own negation.

### The defect found on the way in (F-111)

Not in this feature. §8.2's note beneath `menu_item_event`'s DDL described a table with **five** event
types and **two** payload columns — twelve lines below a DDL block correctly showing eight and five — and
required *"integration tests must assert all ten combinations"*, an obligation whose arithmetic was wrong
even for the schema it was written against and which §16.4 separately **rules against writing** (F-47).
Found by opening §8.2 in order to add a table to it, which is how F-54, F-58 and F-79 arrived, and which
is **F-93's timing for the sixth time**: the slice that edits a section is the last moment its content is
free to be wrong. The counts are deleted rather than corrected, and the quotation is made executable —
every named `event_type` vocabulary the specification quotes is now compared against the migrations, both
directions, subject computed on each side.

### What is open after this stage

**~~Stage 5b, and only Stage 5b.~~ Half of it landed in Slice 58 as Stage 5b-i below; §11.4's count is
what remains.** Both rulings were made here, so what was left was two surfaces and no decisions:

- **§11.4's count.** The administrator's menu index or the item's own page — probably the index, since the
  question is comparative and a per-item number answers it one dish at a time. `ListLikeCountsAsync` is
  the read, and a heading's group is where a total would go if one is wanted.
- **§11.1's control.** The guest's detail panel is where it belongs rather than the card: the card is a
  button that stages an item, and a second interactive element inside a button is not markup this
  application can write. `TableOrderSurface` is an interactive island, so the press is a circuit event and
  needs no post/redirect/get.
- **The two obligations Stage 5a re-opened**, which close when those surfaces land: two reads with no
  caller and one **write** with no caller — the second being the stronger of the two, on the standing
  rule that a write nothing calls is a code path no test can reach through the interface meant to protect
  it.

**Not carried forward:** a guest-visible count, and any notion of a like that requires an order. Both were
ruled against above, and neither is an item.

---

## ~~Stage 5b-i — likes: the guest's control~~ — **landed, M6 Slice 58**

**The half a guest can see, and it is deliberately first.** Stage 5a left two reads and one write with no
caller and said which was worse: *a write nothing calls is a code path no test can reach through the
interface meant to protect it*. So the press comes before the count.

**The order is a ruling rather than a preference, and the argument is what the other order produces.**
§11.4's count shipped first would be a column reading zero on every row, on a surface whose only writer is
an integration test — a read with no *writer*, which is the same defect inverted and harder to notice
because the page renders correctly. And only this surface can produce a press, so an end-to-end scenario is
writable in this slice and can be *extended* by the count slice rather than invented by it.

### The three rulings the markup needed

**The control is in the detail panel and never on the card, and the reason is mechanical.** The card *is* a
`<button>` — it is what stages a dish. The HTML parser does not permit a button inside a button and does not
report the attempt: it closes the outer element when it meets the inner one, so the card silently becomes
two elements and the half carrying the dish's name and price stops being a control at all. Nothing throws,
the Razor compiles, §16.1 rules out bUnit so nothing renders it, and the §16.3 barrier measures where
controls *are* rather than whether they still work. This plan predicted the placement before the markup was
written; what it did not have was the failure mode, which is why the contract test refuses the other
placement by name rather than trusting the sentence.

**A toggle's accessible name does not move with its state.** A screen reader announces name-then-state, so a
button whose label changed to *Liked* would announce the state twice, in two vocabularies, one of them
guessable. The word is constant, `aria-pressed` carries the state, and the glyph beside it changes **shape**
rather than only colour — filled against outlined — so the two states are distinguishable without colour
vision. **The card one loop up legitimately does the opposite** and that is worth stating, because it looks
like an inconsistency: a card is a one-of-many *choice* whose chip says which one is chosen, not a toggle.

**The press applies the write's answer rather than the tap's intent.** `SetMenuItemReactionResult` carries
the state the transaction left the person in, which is a stronger thing to render than what the surface
asked for: an `AlreadyInThatState` answer settles the control on the truth. **And there is no in-flight
guard**, which is the decision a later reader would reverse by analogy with `_sending`: a double-tap is the
ordinary gesture on a heart rather than an edge case, Blazor dispatches circuit events serially, so two taps
are a like and then an unlike — exactly what the gesture means and what the log should record. A guard would
swallow the second half of it. It is also the gesture `menu_item_reaction_current`'s identifier tie-break
was written for (F-95).

### One consequence, recorded so it is not repaired by mistake

**A guest cannot like an unavailable dish**, and that follows from the placement rather than from a
decision. §7 renders a deactivated item on the menu, marked, with a `disabled` card — so the detail panel
never opens for it and the control is unreachable. *The salmon is off tonight and it is still the best thing
here* is a real opinion and this surface cannot record it. It is not repaired here because the repair is a
second path to the panel for items that cannot be staged, which is a surface change with its own questions;
it is written down because a reader meeting the gap will otherwise assume nobody noticed.

### Two rulings stopped being paragraphs

Stage 5a decided that the count is **staff-facing** and that a reaction **publishes nothing**, and wrote both
into §7 — where each is one line away from being improved into a defect. A span on a card renders the count;
a forwarding verb on `MenuWorkflow` makes a heart-tap re-read the entire menu on every phone, kitchen board
and display in the building, with **load rather than an error** as the symptom and the tree staying green.
`MenuItemReactionSurfaceContractTests` holds both: no surface under `Components/Pages/Table/` **calls**
`ListLikeCountsAsync`, subject computed over the directory rather than named as a file (F-47); and
`MenuWorkflow` mentions neither reaction symbol. **Both keys carry an open parenthesis**, which is the
difference between a use and a mention (F-67's shape) — the file that must not call the count read is also
the natural place to write down why, and a gate keyed on the bare identifier would report a finding on a
component whose only offence was explaining the ruling it obeys.

### The scenario ships with the control, and that is F-109 rather than diligence

This plan deferred a picture scenario **four times**, each time with a recorded and reasonable-sounding
reason, and the cost was **F-106**: an upload that committed, a redirect onto a page answering HTTP 500,
every administrator view of a decorated dish broken including the one carrying the Remove button, and eleven
hundred unit facts, every integration fact and seventeen scenarios green throughout. The operator found it.
A like control has the identical profile — an interactive island, a circuit event, a toggle that looks right
in source — so **§16.3 scenario 21** ships in the same slice.

**The reload is the scenario.** Everything before it is satisfied exactly as well by a `bool` field on a
Blazor component that no database ever hears about, and that is not a straw man: it is what *make the heart
fill in when you tap it* produces, it is smaller than the real implementation, and every unit fact and every
other scenario stays green against it. Four further claims ride on the same arrangement, each refusing an
implementation that passes the ones before it — nothing to press until an item is chosen; the *other* dish
reports unpressed, so the opinion is about a dish rather than about the surface; and the press is withdrawn
and reloaded again, because a verb that only ever appended `'liked'` rows passes every step above.

### One thing moved that is not about the menu

`SeatGuestAsync` was `private static` inside `EndToEndScenarios` from Slice 5, which was right while exactly
one file seated guests. A second scenario file needs one, and a private method cannot be called from a
second file, so the alternative to moving it was pasting it — F-59's mechanism, with **F-100's** ruling
already written down against it one register up. It is now `TableJourneys.SeatGuestAsync`, taking a patience
parameter; the old file keeps a one-line forwarder supplying its own constant, so its **eight call sites are
untouched**. It throws rather than asserting, which is the one thing that changed in the move: every other
journey in that directory reports a failure as an exception naming what the surface was showing instead, and
only `RestaurantHarness` references xUnit at all.

### ~~What is open after this stage~~ — closed by Stage 5b-ii below

**~~Stage 5b-ii, and only Stage 5b-ii: §11.4's count.~~ Landed, M6 Slice 59.** `ListLikeCountsAsync` was
the last read in this feature with no caller, and it was the weaker of the two defects rather than the
stronger — an unread read cannot change anything without telling anybody.

**~~A guest cannot like an unavailable dish~~ — closed by Stage 5c below, M6 Slice 60.** The repair is
the one this paragraph predicted: a second path into the panel for items that cannot be staged.

**Not carried forward:** a guest-visible count, and any notion of a like that requires an order. Both were
ruled against in Stage 5a, and neither is an item.

---

## ~~Stage 5b-ii — likes: §11.4's count~~ — **landed, M6 Slice 59**

**The half that answers the question the number exists for.** Stage 5a ruled the count staff-facing and
said what it is for: *which of these is popular*, which is **comparative** — so it belongs on the index,
where sixty dishes are on one screen, rather than on an item's own page where it answers one dish at a
time.

### Three rulings about where a number goes

**Beside the name rather than in a column of its own, and this is Stage 4d's ruling a second time.** A
column empty on most rows puts a `data-label` reading *Liked* beside nothing on the handheld card, which
is precisely the failure §11.12's label rule exists to prevent — Stage 4d refused two columns that would
have been empty on *half* the rows, and this one would be empty on most of them. What is gained by the
column, sortability, is not on offer anyway: the index sorts by heading and position, and adding a sort is
a feature rather than a cell. On a menu of sixty the chips **are** the comparison.

**Neutral rather than `-ok` or `-warn`.** A count of likes is neither good news nor a warning, and both
modifiers already mean something one cell over — available and unavailable. A `chip-ok` here would read as
*this dish is fine*, which is a different claim about the same row.

**Nothing rather than a zero.** `ListLikeCountsAsync` lists the dishes that *are* liked instead of
left-joining the menu, and its own summary says so. Rendering *0 likes* on fifty-eight rows of a sixty-dish
menu would be this surface inventing a fact the read deliberately declined to state, and would bury the
four rows that answer the question.

### The fifth contract fact is the half that fails plausibly

The index must call `ListLikeCountsAsync` and must never call `ListLikedByAsync`. **They are one keystroke
apart and only one of them is about the person reading the page.** An index calling the wrong one renders
*perfectly*: every chip says *1 like* or is absent, because the page is showing the administrator their own
opinion presented as the restaurant's. Nothing throws, no number is malformed, no other test goes red, and
the surface answers *which of these do I like* on a page that asks *which of these is popular*. Two reads
over one fold is the whole of Stage 5a's first ruling, and the failure is that **both call sites compile**.

Both halves are asserted, because either alone is satisfied by an index that reads neither — an index that
had simply lost the read renders a menu with no counts on it, which a prohibition cannot see.

### Scenario 21 is extended rather than a scenario added

This plan predicted that, in Stage 5b-i's own open list, and the prediction is worth checking rather than
just noting: *"21 already presses a heart, so the count slice adds an administrator reading the number
back."* That is what happened, and the extension is stronger than a second scenario would have been.
**It is the only place in this repository where §11.1's write and §11.4's read meet.** Two different
queries against the same rows, written for two different people on two different surfaces; nothing but a
browser can say they describe the same event.

Three assertions, and each refuses an implementation that passes the others. One like against the dish
while the press stands. **None against the other dish**, because *the count is 1* is also what a page
hard-wired to report 1 would say. And **none against either once the press is withdrawn** — a count over
`'liked'` *events* rather than over current opinions passes every step before this one, which is exactly
the plausible wrong implementation the data-access layer's own summary names.

### No CSS, and the reason the hook is an attribute

`.chip` has been declared in `app.css` since Slice 30, so the chip needs nothing. The **number's** hook is
`data-like-count` — a data attribute rather than a class — for two reasons that agree. The harness reads an
integer instead of parsing *"3 likes"*, so the assertion does not depend on the one part of the chip that
is free to change. And `.record-` is §11.12's shared vocabulary prefix: a hook borrowing that prefix
without declaring a rule would put a name in the shared namespace that the stylesheet has never heard of,
which is F-67's neighbourhood.

### What is open after this stage

**Nothing about likes.** Every read and every write `0008` introduced now has a caller, no verb in §7 is
without a surface, and Stage 5's open list is empty for the first time since 5a.

**~~A guest cannot like an unavailable dish~~ — closed, M6 Slice 60.** It followed from the control's
placement rather than from a decision, and the repair is exactly what this paragraph named: a second route
into the detail panel for items that cannot be staged. Stage 5c below.

**The next thing in this plan is Stage 6**, which is *not startable* and says why below: a comment is the
first user-generated content in this system, and it needs a rate-limiting slice with no menu in it, a
`REQUIREMENTS.md` revision about guest privacy, and a moderation surface. The recommendation recorded there
— **do Stage 5b and stop** — is now discharged rather than pending.

## ~~Stage 5c — likes: a dish that is off tonight~~ — **landed, M6 Slice 60**

**The last item on this plan's open list, and the only one that did not need a stage nobody can start.**
Stage 5b-i put §11.1's like in the detail panel for a mechanical reason, wrote down what that placement
costs, and declined to pay it: the panel opens only for a *chosen* item, §7 renders a deactivated item's
card `disabled`, and so **a guest could not like an unavailable dish**. *The salmon is off tonight and it
is still the best thing here* is a real opinion and this surface could not record it.

### The repair is a path rather than a looser refusal, and that is the whole ruling

The obvious change is to drop the card's `disabled` so one control does both jobs. It works. §7's
"cannot be added to a send" would still hold — `OrderStaging.Stage` refuses an inactive item **by name**
and the send transaction re-reads under the lock (§6.5.4, §6.6) — the markup gets smaller, and every
existing test stays green.

**It is refused, and the argument is what a guest would then experience.** §7's rule is about *staging*,
and the card is the staging control; a card that accepted a tap on a dish the surface already knows is off
would be inviting somebody to press *Add to basket* and be told no. What was missing was never a looser
refusal — it was a second **path** to the panel. So the card stays `disabled` and gains a **sibling**
inside the same `<li>`.

### A sibling and not a child, which is the parser ruling for the second time

Stage 5b-i's reason for putting the like in the panel applies here unchanged: the card *is* a `<button>`,
the HTML parser does not permit a button inside a button and does not report the attempt — it closes the
outer element when it meets the inner one, so the card silently becomes two elements and the half carrying
the dish's name and price stops staging anything. Nothing throws, no C# is wrong, the Razor compiles, and
§16.1 rules out bUnit so nothing renders it. **The contract fact therefore asserts the placement
structurally rather than by absence**: the control's index must fall between the card's `</button>` and
the `</li>`, which is the only place a sibling can be.

### Three smaller rulings

**It is rendered only where the card is refused.** An available dish already has a way into its panel —
its card — and a second control beside every card is sixty controls on a menu of sixty, read from a phone.

**Its accessible name carries the dish and its visible text does not.** A column of buttons all reading
*Read about* is unusable to anybody navigating by control. The name is appended in a `.visually-hidden`
span rather than supplied by an `aria-label`, and the difference matters in one direction: an `aria-label`
**replaces** the content, and voice control matches on what is on screen — so a label would break *"click
Read about"* for exactly the population most likely to be using it. The visible words stay a prefix of the
accessible name, which is the arrangement §11.4's own record ticks already use.

***Add to basket* is never disabled, and that is asserted rather than assumed.** It is the same ruling read
backwards. Now that a guest can choose a dish that is off, the next tidy-up is to disarm that button while
such an item is chosen — considerate-looking, and it costs two things: the guest gets a dead control and no
reason where `OrderStaging` would have named the dish, and the component acquires a second opinion about
availability beside the staging area's, which is F-65's mechanism. The Send button one region down *is*
disabled while the basket is empty and that is not the same case — an empty basket has no refusal to
explain.

### One layout consequence, recorded because it is invisible in the markup

`.order-menu-item` was `display: flex` with a single full-width child, and the default `align-items:
stretch` was what made every card in a row the same height. A second child makes it a **column**, where the
axis that stretches is the other one — so the card takes `flex-grow: 1` and the row stays level. Without
that line the change is correct HTML that looks broken on the first menu with two cards of different
lengths beside each other.

### A defect found on the way in (F-113)

`ChosenItemDetail.Facts` is keyed by the detail panel's `<dt>` terms and its own paragraph names them
*Price*, *Available* and *On the menu since*; the reader built it with `InnerTextAsync`, and `app.css`
upcases `.order-menu-facts dt`. **F-88's mechanism a second time inside the file F-88 was found in** — and
the comment that fix left behind claims it was the only affected read, written in the same slice that fixed
`ReadTotalsAsync` forty lines down for exactly this reason. It was narrowly true only because `Facts` had
no caller, which is the condition that hid it, and this stage's scenario step is the first caller it has
ever had. Found by needing `Facts["Available"]`, which is F-93's timing again.

### The scenario is an extension, on Slice 59's reason

**Scenario 21 gains four steps rather than a scenario 22 being added.** The kitchen 86s the salmon, the
guest's open menu marks the card unavailable, the **pudding is chosen first** — deliberately, because the
salmon's panel is still open from the step before and going off the menu does not close it, so without that
the claim would be satisfied by a panel that had simply never gone away — and then the second control is
what gets back to it. The like reports unpressed, is pressed, and §11.4's index reads **one** against that
dish. A surface that had merely opened a panel and toggled a field passes every step before the last one.

The cost of a scenario 22 would have been a second container, a second passkey registration and a second
join, for an arrangement this scenario already has standing.

### What is open after this stage

**Nothing.** Every read and every write `0008` introduced has a caller, no verb in §7 is without a surface,
and the consequence Stage 5b-i recorded is discharged rather than carried. The menu enhancement's open list
is empty for the second time, and this time nothing was deferred into it.

**The next thing in this plan is Stage 6**, which is *not startable* and says why below. That has not
changed: a comment is the first user-generated content in this system and it needs a rate-limiting slice
with no menu in it, a `REQUIREMENTS.md` revision about guest privacy, and a moderation surface.

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
1b. **One of Stage 6's questions is already answered.** *Does it require having ordered the item* is the
   same question for a comment as for a like, and Stage 5a ruled on it: **no**, because
   `order_current_line` records what somebody ordered rather than what they ate and a table shares. That
   ruling was made where it was cheap — a like has no privacy question attached to it — which is exactly
   why this plan said the question was *"cheaper to answer here"*. It transfers, and Stage 6 inherits it
   rather than re-deliberating it.
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

**Recommendation: do Stage 5b and stop.** Likes answer "which of these is popular" with no new question
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
