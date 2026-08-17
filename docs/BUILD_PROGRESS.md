# Build progress

**This file is the second part of one log, and the log's name is `BUILD_PROGRESS`.** It holds **M6 Slice
40 onward** and is the file a new slice is appended to. Everything before it — M1 through **M6 Slice
39**, including the original *How this was produced*, *Staged plan* and *Known caveats* preamble — is in
[`docs/progress/BUILD_PROGRESS_THROUGH_M6_SLICE_39.md`](progress/BUILD_PROGRESS_THROUGH_M6_SLICE_39.md).

**A citation resolves by slice number, and there is exactly one rule.** Every row in
`docs/DOCUMENTATION_REVIEW.md` and in the specification's Appendix A ends with a reference of the form
*BUILD_PROGRESS M6 Slice N*. Those references are to the **log**, not to a file, and they were not
rewritten when the log was split — rewriting a hundred rows to record a filing decision would be a
hundred chances to introduce an error in service of nothing. Slices 1 to 39 are in the archive; 40 onward
are here.

**`export.sh` does not put the archive in `dump.txt`, and that is the point (F-96).** The log had reached
829 KiB, 13% of a 6.08 MiB context dump, nearly all of it slices closed months ago. `docs/progress/` is
therefore withheld from the dump by path, the way `docs/llm/` already was. **The consequence is a
hazard and is written down rather than left to be discovered:** a session working from `dump.txt` cannot
see the archive, so nothing in a session may reconstruct it, and **this file must never be delivered as
though it were the whole log.** `ContextDumpExclusionContractTests` holds the tree to the checkable half
of that — every withheld document is linked by path from a document the dump does contain, so history
that leaves the dump leaves a pointer behind.

**When this file gets long again, another tranche moves.** That is a deliberate manual decision each
time, not a rule a script applies: choosing where a log stops being working memory and starts being
archaeology is a judgement, and the last thing this project needs is a script that silently truncates its
own history on a size threshold.

---

# M6 Slice 40 — the heading every item has, and a vocabulary nobody could check

## Read this first: Slice 39 was green, and the number it predicted was finally tested

```
Test summary: total: 1124, failed: 0, succeeded: 1124, skipped: 0, duration: 147.5s
```

That number matters more than a green line usually does. **Slice 38 predicted 1124 and never reached a
count** — its migration threw at fixture initialisation, so the arithmetic went untested. Slice 39 re-predicted
1124 unchanged and shipped the F-78 repair. This run is the first time in three slices that a prediction
was compared against a run, and it matched exactly. The full local CI ran green as well: sixteen §16.3
scenarios in 147.6s, the boot smoke check, the restore drill, and a quick tunnel that served the
application over a public origin.

## The authoring environment has a database again, and it earned its keep in the first hour

Slice 39's "still open" ended on this, and it was the sharpest line in the file: *"No authoring-environment
database for a second consecutive slice… That is now a two-slice pattern rather than a one-off, and it is
the reason F-78 was found by a test run on your machine instead of by a migration in mine."*

PostgreSQL 16 was installed in the authoring environment before a line of `0005` was written, and **every
statement in this slice was executed rather than reasoned about**. What that bought, concretely:

- `0005` applied on an **empty** database (no seed section) and on a **populated** one (seed written, two
  items backfilled under it, `SET NOT NULL` and the foreign key applied, positions preserved). Both
  branches of the conditional seed were walked, which is the only way to walk them — a fresh container
  takes the first branch and can never take the second.
- `SELECT … FOR UPDATE` with a correlated `MAX + 1` subquery was run before it was written into
  `DapperMenuAdministration`. PostgreSQL rejects `FOR UPDATE` alongside aggregation in the *outer* query,
  and the shape that works is the one now in the file.
- The **three-event create** was executed as a transaction — `created`, `section_changed`,
  `description_changed` — and accepted.
- The new paired CHECK was **proven to bite**: an INSERT of a `created` event carrying a section is refused
  with `violates check constraint "menu_item_event_section_payload"`. That is the constraint doing its job
  under observation rather than a claim that it would.
- All five new `SchemaMigrationRunnerTests` probes were run against the live schema: `attnotnull` true, the
  named foreign key present, `menu_item_section_index` present, eight CHECK constraints on
  `menu_item_event`, and zero sections on a fresh database.

## F-80 — a vocabulary copied out of its own constraint, wrong for two migrations, with no symptom

`EventTypeCatalogue.MenuEventTypes` in `EventExplorerReads.cs` feeds §11.4's explorer dropdown. Its doc
comment said *"the five `menu_item_event.event_type` values"* and it listed five. `0004` added
`description_changed` and `reordered`. Neither the list nor the sentence was touched.

**Nothing broke, and that is the finding.** §11.4's explorer deliberately never *refuses* an unrecognised
type — `IsKnown` exists only to warn an administrator that a hand-edited `?type=` is not a word this build
catalogues, and the filter runs regardless, because a schema this build has not caught up with is exactly
the case where somebody most needs to see the rows. So a missing word has **no run-time symptom at all**.
It is two of the menu's verbs that cannot be chosen from the dropdown, on the page whose entire purpose is
choosing. Two slices, every gate green throughout.

**This is F-77 one register worse.** That row deleted a *count* of this same vocabulary, written in three
files and checkable in none. This is the vocabulary **itself**, copied into a second file, silently
drifting — in a file neither slice that widened the vocabulary had any reason to open.

**The repair is F-47's habit, and the three choices inside it are each deliberate.**
`MenuEventVocabularyContractTests` walks `Migrations/*.sql` in name order (which is DbUp's apply order),
takes the `event_type IN (…)` list from the last script declaring `menu_item_event_type_vocabulary`, and
compares the set against the C# list.

- **Not a count.** A count would have passed every version of this bug — which is precisely what F-77
  established about this exact vocabulary.
- **Non-vacuity is asserted.** A regex that matched the constraint and extracted no quoted words would
  otherwise pass against an empty list, which is F-41's failure mode.
- **SQL text, not a database.** `SchemaMigrationRunnerTests` owns "this constraint exists on a real
  PostgreSQL"; "the constraint and the C# list agree" is a different question and belongs in the fast suite,
  where a wrong answer is available in seconds.

Simulated against the real files before shipping: it identifies `0005` as the last declaration and derives
the same eight types the C# list now holds.

## `0005` needed no dollar-quoted block, and that is what `0004` bought

`0004` needed a `DO` block only because it had to query `pg_constraint` for names PostgreSQL had generated.
Since it replaced every one with a chosen name, the single CHECK that had to widen here was dropped **by
name** — two ordinary statements, nothing to query, nothing for dbup-core's variable substitution to
collide with. **F-78 was a one-migration problem rather than a recurring one because the previous slice paid
for the names.** The script says so in its header, so that a future migration does not reintroduce a block
it does not need.

## The seed carries two guards, and the plan specified one

`docs/MENU_AND_HANDHELD_PLAN.md` specified the seed as *one* section, *only if `menu_item` has rows*. That
is necessary and not sufficient. **"No surface calls `IMenuSectionAdministration`" is not the same claim as
"no row exists"** — Slice 37 shipped that write service and registered it, and a database where somebody
exercised it holds sections. Without a second guard the INSERT would trip `menu_section.name`'s `citext`
UNIQUE on any database holding a section called "Menu", and a migration that fails at startup takes the
whole application down.

So the seed is guarded by `EXISTS (SELECT 1 FROM menu_item) AND NOT EXISTS (SELECT 1 FROM menu_section)`,
and the backfill correspondingly targets the **first section in display order** rather than the seed's
literal identifier. Both paths converge: if the seed ran it *is* the first section; if it did not, the
orphans go under the earliest heading that already exists.

## Two decisions kept a mandatory column from reaching sixteen files

This is the transferable part of the slice, and it is worth stating as a rule rather than as two
implementation notes. **When a mandatory argument arrives late, give the arrangement helper a default rather
than threading the argument through every caller that does not care about it.**

- **`OrderTestWorld.AddMenuItemAsync` takes an optional section** and lazily creates a house heading named
  "Menu" when none is given, cleared by `TruncateAsync`. The dozen integration test files that put something
  on the menu — about ordering, settlement, the kitchen, visibility, none about headings — compile unchanged
  and mean exactly what they meant. The house section is created lazily rather than in `TruncateAsync`
  because several classes here count rows and should not carry one they did not ask for.
- **`AdministrationJourneys.CreateMenuItemAsync` arranges its own heading** through
  `EnsureMenuSectionAsync` before opening the form. **The sixteen existing §16.3 scenarios needed no edit at
  all.** `EnsureMenuSectionAsync` is idempotent by *looking first* rather than by submitting and swallowing
  a "name taken" failure — the latter would also pass on a form that reported the wrong error, and "taken"
  is a real outcome this project asserts elsewhere.

## Rulings inside `0005`, each of which could have gone the other way

**An item is appended at `MAX(display_order) + 1` within its section, reversing `0004`'s "created at
position 0".** The reason the rule could change is the reason it existed: "the end of the menu" was not a
defined place while an item had no heading, and it is defined now. `MAX + 1` rather than `COUNT(*)`, on the
rule `menu_section` has followed since `0003` — a count collides with an existing position as soon as
anything has been moved, and `AppendingUsesTheHighestPositionRatherThanTheCount` is the assertion.

**The lock is on the section row, not the item.** Locking the section is what serialises two administrators
creating an item under the same heading at the same moment: without it both read the same `MAX`, both write
the same position, and the menu has two dishes claiming one place — which the schema *permits*, positions
being deliberately non-unique, and which is therefore a defect nothing would ever report. It doubles as the
existence check, which is why a missing heading is a reported `MenuSectionNotFound` rather than PostgreSQL
error 23503 naming a constraint.

**`created` still carries the name and the price alone**, so an item created under a heading writes two
events and one with a description writes three. Widening it would relax an equality to an implication and
break every `created` row already written. **The position writes no event ever**, because
`new_display_order` is bound to `reordered` and a `created` row carrying a position would be false of every
row written before `0005`.

**One index, and it is not the one `0004` declined.** PostgreSQL does not index the referencing side of a
foreign key, so without `menu_item_section_index` every statement touching a `menu_section` row scans
`menu_item`. Its trailing columns are the tail of §11.1's `ORDER BY`, so one index answers both.

## §7's asymmetry, implemented, and it points two ways one sentence apart

An inactive **item** stays on the guest's menu, marked, unorderable. An inactive **section** is not rendered
to the guest at all. That is not a contradiction: switching off a heading is a decision about a whole part
of the menu ("no breakfast this evening"), where 86ing a dish is a decision about one thing a guest is still
entitled to know exists. Neither flag cascades to the other.

**Both are carried unfiltered by `IMenuDirectory` and the filtering is on the surface.** §11.4's
administrator must see every heading including the ones no guest can reach — that is precisely the row
somebody is looking for when they wonder why an available dish is not on the menu — so
`/administration/menu` and `ManageMenuItem` both carry a *Section hidden* chip.

## Two scope rulings, flagged for veto

**`MoveMenuItemToSectionAsync` is not in this slice.** The plan schedules it with Stage 2's data access. The
item editor that would call it is Stage 3, and this project's own rule — a verb with no caller is a code path
no test can reach through the interface meant to protect it — applies to it exactly as it applied to the
section verbs for three slices. **To reverse:** the verb is a `section_changed` event and an `UPDATE`
alongside `ReorderMenuItemAsync`, plus a picker on `ManageMenuItem.razor`; nothing in this slice blocks it.

**Only `CreateMenuSectionAsync` moved behind `IMenuWorkflow`.** The obligation carried four times **narrows
to four verbs rather than closing**. The create page is a caller, so that verb arrives and publishes
`MenuChanged` on a committed row; rename, describe, reorder and set-active have no surface. What changed is
the *cost* of leaving them: §11.1's guest menu groups by heading now, so a renamed section that announced
nothing would leave a stale heading in every open picker. That defect is real and merely **unreachable**.
`MenuWiringTests`' fake throws from all four with a message naming the obligation, so the next person to
wire one is told rather than left to notice.

## One assertion was cut from scenario 17, and the cut is recorded rather than quietly made

The scenario was drafted to deactivate a heading and watch it vanish from the guest's menu — §7's asymmetry,
and the one thing about it no unit test can see. That needs `SetMenuSectionActiveAsync` to have a surface,
which is the section editor this slice deliberately did not ship. Asserting it would have meant either a
harness reaching past the UI, which §16.3 refuses, or a verb wired for a test, which is worse.

It was replaced with an assertion this slice can actually make: a third item created under an existing
heading joins it rather than starting a new grouping, and lands at the end of it — `MAX + 1`-within-section
proven through a browser. The inactive-section rule is covered at the data layer by
`MenuDirectoryTests.AnInactiveSection_IsCarriedRatherThanFiltered` and is **unverified end to end**.

## What is in this slice

| Area | Change |
|---|---|
| Migration | `0005_menu_item_sections.sql` — **new**. Conditional two-guard seed, nullable column, backfill, `SET NOT NULL`, named foreign key, vocabulary widened by name, `new_menu_section_identifier` with its paired CHECK, one index |
| Data access | `MenuDirectory.cs` — three section members, INNER join, six-key ordering; `MenuAdministration.cs` — section on create, `MAX + 1` under a section lock, `CreateMenuItemOutcome`, widened result; `MenuEventLog.cs` — section payload and an aliased LEFT join |
| Web | `MenuWorkflow.cs` — `CreateMenuSectionAsync`, section on the item create, that publish made conditional; `OrdersServiceCollectionExtensions.cs` — the registration note rewritten |
| Surfaces | `CreateMenuSection.razor` — **new**; `CreateMenuItem.razor` — picker and first-use panel; `AdministrationMenu.razor` — Section column, Create section, `section_changed` arm; `ManageMenuItem.razor` — the heading and its arm; `TableOrderSurface.razor` — §11.1 grouped under headings |
| Stylesheet | `app.css` — `.order-menu-section` and its heading. No new breakpoint, no colour literal, no `var()` fallback |
| Explorer | `EventExplorerReads.cs` — the menu vocabulary corrected from five to eight (F-80) |
| Harness | `AdministrationJourneys.cs` — `CreateMenuSectionAsync`, `EnsureMenuSectionAsync`, `FindMenuSectionAsync`, a section on item creation; `TableOrderJourneys.cs` — `MenuCard.SectionName`, section-walking read, `ReadMenuSectionNamesAsync` |
| Tests | `MenuEventVocabularyContractTests.cs` — **new**, two facts; `MenuDirectoryTests` +2; `MenuAdministrationTests` +3; `SchemaMigrationRunnerTests` +2; `MenuWiringTests` +2; `MenuEventLogTests` counts corrected; `EndToEndScenarios.cs` — scenario 17; `OrderTestWorld.cs` — `AddMenuSectionAsync` and the optional section |
| Documents | S v1.25 (§7, §8.1, §8.2, §16.3, §16.4, Appendix A, changelog), ledger F-80, plan Stage 2 closed and Stage 3 partly struck, `_CHANGES.md` |

## What was verified

**Everything SQL, against a live PostgreSQL 16.** Listed in full at the top of this entry rather than
summarised here, because it is the difference between this slice and the two before it.

**The working tree was reconstructed from `dump.txt` and checked against the SHA-256 recorded for every
file: 341 of 341 byte-identical.** The only file the dump does not reproduce is `export.sh`, which
documents itself inside its own output. Every edit below was made against a verified tree.

**Three gates were simulated rather than assumed.** `MarkdownTableContractTests` was run in substance over
every Markdown file in the repository — fence-aware, escaped-pipe-aware — and reports **zero** mismatches,
including the two new four-cell rows. `SpecificationVersionTests`: header 1.25, newest entry 1.25, entries
descending. `MenuEventVocabularyContractTests`: derives eight types from `0005` and matches the C# list.

**Brace, paren and bracket balance** on every C# file touched, checked after each edit rather than at the
end. **Every `InsertEventAsync` call site** was enumerated after the signature change and each of the seven
confirmed to carry the new argument — by search, not by having written them.

**The contiguity assertion in `MenuDirectoryTests` was proven sensitive.** A grouped list of four returns 2
runs; a scattered list with the same set of names returns 4. An assertion that could not tell those apart
would be the whole fact rendered vacuous.

**Selector existence in both directions** for the new markup: `.order-menu-section` and
`.order-menu-section-name` exist in `app.css` and are read by both the surface and the harness;
`--hairline` and `--ink-soft` are declared in `:root`.

## What was NOT verified

**Nothing compiled.** No .NET SDK in the authoring environment. The likeliest sites of a complaint are named
rather than left to be found: **`InputSelect` bound to a `Guid`** on the item form, which this tree does
elsewhere for enums but not for `Guid`; **`_created is { Created: true } created`**, a property pattern with
a designation, on two Razor pages; and **`Assert.Single(menu, card => card.Name == …)`**, whose predicate
overload returns the element in xUnit v3 and returned `void` in some v2 lines.

**No test ran.** The SQL is executed and the C# is not. Every count below is arithmetic.

**No browser rendered the grouped menu.** Named consequences a first run may show: whether an uppercase
letter-spaced heading at 0.78rem is legible enough on a 375px handset; whether two headings with one item
each read as grouping or as clutter; and whether `aria-labelledby` on a per-section `<ul>` announces
sensibly when the same page has several, which only a screen reader decides.

**The `0005` backfill branch was walked by hand and cannot be walked by a test here.** A fresh container
takes the no-seed branch, which `Run_SeedsNoSectionOnAFreshDatabase` asserts. The populated branch is
recorded in this entry as manually exercised, and it is the branch that runs on your actual database.

**§7's inactive-section rendering rule is unverified end to end**, for the reason above.

## Test count

Last observed: **1124**, from Slice 39 — and, for the first time in three slices, a prediction that matched
its run.

Predicted here: **1136**. The arithmetic: `MenuEventVocabularyContractTests` +2, `MenuDirectoryTests` +2,
`MenuAdministrationTests` +3, `SchemaMigrationRunnerTests` +2, `MenuWiringTests` +2, §16.3 scenario 17 +1.
No fact was removed; several had their assertions corrected in place. §16.3 goes from **16** to **17**.

Per §18: if the run returns anything other than 1136, that difference is the next thing to chase.

## Still open

**The section editor, and it is now the highest-value thing outstanding.** A heading created with a typo can
only be worked around by creating another. It carries four workflow verbs, `MoveMenuItemToSectionAsync`, the
sections-first index, and the end-to-end assertion cut from scenario 17.

**A section's own description under its heading on the guest menu.** The surface groups from
`MenuItemSummary`, which carries the heading's name and not its description; showing it needs either a
second read or a widened record, and guessing between those was not this slice's business.

**The kitchen's "86" panel still groups by nothing.** Stage 3's remaining surface, and the last one.

**§16.3 scenario 17 does not deactivate a section.** Named above; lands with the editor.

**F-41 has no row in `DOCUMENTATION_REVIEW.md`.** Fifth slice carried. Still a decision rather than a repair.

**`.sitting-meta` is declared by two components and the two have drifted.** Deferred a seventh time.

**A CI job that runs the canonical stack on the canonical engine.** Sixteenth consecutive slice.

**`run.sh --containers-only` prints two `Error:` lines about a container that does not exist yet, then
starts it successfully.** Carried.

**The handheld barrier does not visit `/administration/menu/sections/new`.** Scenario 16 walks ten surfaces
and this slice added an eleventh administration page. It is a create form rather than a record list, so
none of the barrier's three reach selectors matches it and the control count would not move — which is
exactly why it would be easy to leave out permanently. Recorded so it is not.

# M6 Slice 41 — the section editor, a reserved word two files were named after (F-81), and the gate that never ran (F-82)

**Closes the deferred obligation this project has been counting down since `0003`.** All five of
`IMenuSectionAdministration`'s verbs now have a surface and all five are behind `IMenuWorkflow`. A heading
can be renamed, described, moved and switched off, and each of those announces `MenuChanged` on a committed
row and nothing on a write that committed nothing.

**And it unblocks the four things that were waiting on it**: `IMenuSectionEventLog`, without which §11.4's
"complete stored record" had nothing to read for a section; the menu index's Section column, which was a
column linking nowhere; the harness journey that had to recover a new section's identifier from a
neighbouring form; and §16.3 scenario 17's two cut steps.

## The two findings, and the order they were found in

**F-81 — two loop variables named after a Razor directive.** Slice 40 wrote
`@section.MenuSectionIdentifier` on the create-item form and `@section.MenuSectionName` on the guest
ordering surface. `@section` is MVC's **section directive**, reserved in Razor's own grammar, so the parser
read a directive with a malformed name and produced four errors across two files — `RZ9979`, `RZ2005`,
`RZ1011` — none of which mentions an identifier and none of which is about the markup. `RZ1011`'s column
lands on the `.` immediately after the seven characters of `section`, which is the only thing in the four
messages that points at the actual cause.

Two properties of it are worth keeping.

**It is invisible in review.** `@key="section.MenuSectionIdentifier"` one line above and
`@SectionHeadingId(section)` one line below compile perfectly, because neither puts the word directly after
an `@`. So the errors read as complaints about the `<option>` and the `<h4>`, and the identifier — which is
the whole cause — appears in none of them.

**It is blocking everywhere.** `MyRestaurant.WebApplication` is the project every other one references. The
unit suite, the integration suite and all seventeen §16.3 scenarios were unreachable together.

**F-82 — the gate against stale counts had gone stale, and said nothing, because it never ran.**
`TestingSectionContractTests` compares every assertion count §16.4 states against the file it names. It was
written after F-70, where exactly that drift concealed an undocumented gate for four slices. Slice 40 added
assertions to four classes §16.4 cites and moved none of the four numbers:

```
MenuAdministrationTests      §16.4 said 23   file holds 26
MenuDirectoryTests           §16.4 said  5   file holds  7
MenuWiringTests              §16.4 said 11   file holds 13
SchemaMigrationRunnerTests   §16.4 said  5   file holds  7
```

Slice 40's own delivery note predicted every one of those increments **by name**. The arithmetic was done,
written down, and never carried into the document.

**This is F-71 read from the other side.** That finding was a test project failing to compile behind a
summary line reading `total: 497, failed: 0`. This is a gate that never started, behind a build error
everybody was already looking at. The shared lesson: **a gate that cannot run is indistinguishable from a
gate that passed**, and nothing in this repository distinguishes them — `dotnet build` reports what failed
to compile and no artefact reports what consequently failed to *execute*.

No gate is added for F-82, deliberately. The gate that would have caught it is the gate that did not run,
and the repair for *that* is F-81's. What is added is the pairing, stated in §16.4 and in the class's own
summary: the first question about a red build is *what stopped being checked*, not only *what stopped
compiling*.

## What this slice does

- **`ManageMenuSection.razor`** at `/administration/menu/sections/{id}` — static SSR, four forms,
  post/redirect/get with a one-word outcome, a facts grid, the heading's items, and its complete uncapped
  event history. Declares no CSS: every class it uses is app.css's §11.12 vocabulary, checked in both
  directions.
- **`IMenuSectionEventLog` / `DapperMenuSectionEventLog`** — the per-heading history read.
- **Four verbs behind `IMenuWorkflow`**, each publishing `MenuChanged` only on a committed row.
- **Links into the editor** from the create panel, the menu index's Section column, and each item's page.
- **F-81's rule made executable** — `RazorDirectiveContractTests`, two facts.
- **F-82's four counts corrected**, and `MinimumCountedClasses` moved from sixteen to eighteen.
- **Scenario 17 regains its two cut steps**, and comes back larger than it was cut.

## Three rulings

**`IMenuSectionEventLog` is a second reader, not a widened `IMenuEventLog`.** Two tables, two vocabularies,
and the two share three type words while meaning different things by all three — a `renamed` section is not
a `name_changed` item, and neither log's payload columns exist on the other. A `UNION ALL` over both is a
real read §11.4's explorer may want one day; it is not this, and building one here would make the
per-section history pay for a merge it never uses.

**No cross-section activity feed.** `IMenuEventLog.ListRecentAsync` exists to fill a panel on
`/administration/menu`, which is an index over items. Sections have no such panel, and a read with no caller
is the same defect as a workflow verb with no caller — which is the rule this slice spent four verbs
discharging, so inventing a fifth instance of it in the same archive would be absurd.

**The editor reads the whole menu and filters in memory** rather than adding a per-section query with one
caller. `IMenuDirectory.ListAsync` already orders by section first and makes each heading's items
contiguous, so the filter preserves the order guests see without re-deciding it in a second file (§7). It is
a read that grows with the menu, on a database whose whole reason for existing is one restaurant. **Flagged
for veto**: the reversal is one method on `IMenuDirectory` and one call site.

## The four verbs' broadcasts, and why two of them are not optional

**A rename** is the one that had stopped being latent. §11.1 renders a heading above every card under it, so
a rename that committed and announced nothing leaves the old word on every open picker until that page
happens to reload.

**A visibility flip** is worse. §7 hides an inactive section from the guest *entirely* — the opposite of the
rule one paragraph away for an inactive item — so switching a heading off without a broadcast leaves a whole
part of the menu tappable on every phone already looking at it, until the send is refused server-side for a
reason the guest never saw coming (§6.5.9).

**A description** publishes and reaches no guest surface today, and that is the same ruling the item
description already carries: `MenuChanged` means *re-read the menu* and nothing else, and a workflow that
decided which columns were worth announcing must be edited again the moment a surface starts reading one.

**A move** publishes because §11.1 orders headings by `(display_order, name, identifier)`, so the whole menu
is in a different order even though no item moved.

## Scenario 17 came back larger than it was cut

Slice 40 drafted a deactivation assertion, cut it, and recorded the cut. The restored version does not only
watch the heading vanish. It asserts that the *other* heading's items stay present, in order, and orderable;
then switches the heading back on and checks the menu returns exactly as it was.

That second half is the only end-to-end proof that deactivating a section **does not cascade** to its items.
A cascade would come back with the pie marked unavailable. It was not in the draft — it became obvious once
the assertion was being written against a surface that existed, and it would not have been written at all if
the cut had been made silently.

## What was verified

**The working tree was reconstructed from `dump.txt` and checked against the SHA-256 recorded for every
file: 344 of 344 byte-identical.** Two files needed repair in the *reader* rather than the tree —
`export.sh` contains its own `# FILE:` banner as literal text, and the last file in the dump abuts the
`DUMP SUMMARY` footer — and both were confirmed against their recorded hashes after repair.

**`RazorDirectiveContractTests` was run in substance** over all 51 components: zero uses, and the
non-vacuity guard's floor of twenty is met four times over. Its five sensitivity cases were run and all five
behave as the second fact asserts.

**`TestingSectionContractTests` was run in substance**, before and after. Before: 16 counted classes, **4
disagreements** — which is F-82, found by simulation rather than by reading. After: 18 counted classes, 0
disagreements, 0 ambiguous, 0 uncited.

**`MarkdownTableContractTests`** over every Markdown file in the repository, fence-aware and
escaped-pipe-aware: 60 table runs, zero findings, including the three new four-cell Appendix A rows and the
two new ledger rows.

**`SpecificationVersionTests`**: header 1.26, newest changelog entry 1.26, 27 entries descending.

**`MenuEventVocabularyContractTests`**: eight types derived from `0005`, set-equal to the C# list. Unchanged
by this slice and re-run because the slice touches the menu.

**`HandheldLayoutContractTests`' data-label parity** across all 8 record-list components — up from 7,
because `ManageMenuSection` adds two lists — every one with cells equal to labels. Its palette fact was run
against the new page: zero `var()` references to undeclared properties, zero inline `<style>`, and every one
of the classes it names exists as a selector in `app.css`.

**Brace, paren and bracket balance** on all nine changed C# files, string- and comment-aware.

**The balance checker was itself proven, and it failed the proof first.** Its first run reported an
imbalance in the new `MenuSectionEventLog.cs`. Running it against two *untouched* sibling files —
`MenuEventLog.cs` and `MenuSectionDirectory.cs`, both byte-verified against the dump — produced the
identical report, which is what identified the checker rather than the file: it read `$"""` as an empty
interpolated string and parsed the SQL body as code. Fixed, re-run, all nine balanced. **A verification tool
that has not been run against a known-good input is a verification tool with no established false-positive
rate**, and this is the second slice in which that mattered.

**Byte hygiene** on every changed and new file: no CR, exactly one final newline, no whitespace-only line,
no context-dump separator.

## What was NOT verified

**Nothing compiled.** No .NET SDK in the authoring environment. The likeliest sites of a complaint are named
rather than left to be found: **`Assert.Single(restored, card => card.Name == pie.Name)`**, whose predicate
overload returns the element in xUnit v3; **`Assert.Equal(string.Empty, described.NewDescription)`** on a
`string?`, which is an equality rather than a null-flow narrowing precisely so it does not depend on how
xUnit annotates `Assert.NotNull`; and **the `foreach` over a `bool[]` inside a `[Fact]`** in
`ASectionVisibilityFlip_…`, which asserts twice in one loop and will report the first failure only.

**No test ran.** Every count below is arithmetic.

**No browser rendered the section editor.** Named consequences a first run may show: whether four forms plus
two record lists on one page reads as an editor or as a wall at 375px; whether *Hide from guests* as a
`link-button danger` is the right weight for an action that is fully reversible; and whether the two record
lists on one page need distinguishing headings for a screen reader, which only a screen reader decides.

**The `.manage-facts .chip` selector the new harness journey waits on is unexercised.** It is the first
harness read in this project that keys on a chip inside the facts grid rather than on a flash or a heading.
If scenario 17 times out at step (g), that selector is the first thing to check and the failure will name
it.

**§16.3 scenario 17 is longer than any scenario in the suite** and now spans two §9 broadcasts on an
already-open circuit. If it becomes flaky, the deactivation wait is the more likely half — it waits for an
*absence*, which `WaitForMenuAsync` expresses as a predicate over the whole menu.

**Nothing verified that F-82's four counts were the only stale ones.** The simulation compares what §16.4
states to what the files hold, which is exactly the gate's own reach; a class §16.4 never cites is invisible
to both, and that residual is stated in §16.4 rather than closed.

## Test count

Last predicted: **1136**, from Slice 40 — and **not observed**, because the build failed. The last observed
count is **1124**, from Slice 39.

**That gap is itself the finding.** §18's habit is that a predicted count the run contradicts is chased
before the slice closes; a predicted count that never meets a run cannot be chased at all, and F-82 is what
was sitting in it.

Predicted here: **1149**. The arithmetic, from 1136: `RazorDirectiveContractTests` +2,
`MenuSectionEventLogTests` +6, `MenuWiringTests` +5. Scenario 17 gains assertions and no new `[Fact]`, so
§16.3 stays at **17**. No fact is removed.

Per §18: if the run returns anything other than 1149, that difference is the next thing to chase — and this
slice is the first opportunity since Slice 39 to perform that check at all.

## Still open

**The sections-first index.** `/administration/menu` is still an item list with a Section column. The column
links now, which was the blocker; what remains is the restructure.

**`MoveMenuItemToSectionAsync`.** The last verb in the whole enhancement with no surface — `ManageMenuItem`
shows the heading, links to it, and cannot change it. It is now the only instance left of the rule this
slice spent four verbs discharging.

**A section's own description under its heading on the guest menu.** Unchanged from Slice 40: the surface
groups from `MenuItemSummary`, which carries the heading's name and not its description.

**The kitchen's "86" panel still groups by nothing.** Stage 3's last surface.

**The handheld barrier visits neither section surface.** Scenario 16 walks ten and this slice adds a twelfth
administration page. `ManageMenuSection` is a detail surface with `.manage-inline-form` buttons, so unlike
the create page it *would* move the control count — which makes leaving it out a real gap rather than a
neutral one. Carried, and now larger than when Slice 40 recorded it.

**Nothing reports which gates a failed build prevented from running.** F-82's residual, stated rather than
resolved.

**F-41 has no row in `DOCUMENTATION_REVIEW.md`.** Sixth slice carried.

**`.sitting-meta` is declared by two components and the two have drifted.** Deferred an eighth time.

**A CI job that runs the canonical stack on the canonical engine.** Seventeenth consecutive slice.

**`run.sh --containers-only` prints two `Error:` lines about a container that does not exist yet, then
starts it successfully.** Carried.

# M6 Slice 42 — seven defects behind one build failure, and a ruling reversed

**Nothing new ships.** This slice adds no surface, no verb, no migration and no gate. It makes the tree
build and the suite pass, and it writes down what was in the way — which turned out to be seven findings
rather than the two the build errors named.

## Read this first: the shape almost all of it shares

Six of the seven are the same mechanism, and naming it is worth more than any individual repair:

> **A schema widened by a migration reaches the test arrangement last**, because arrangement is the code
> nobody is looking at while implementing the thing being arranged for.

`0004` and `0005` between them added three columns to `menu_item`, three payload columns to
`menu_item_event`, three members to `MenuItemSummary`, and three types to the event vocabulary. Every one
of those landed correctly in `src/`. What did not land was: the INSERT the test world writes events with
(F-86), a positional stand-in in the unit suite (F-84), six assertions counting a create (F-87), and a
harness read that had never been about the schema at all (F-88). F-85 is the same slot — test arrangement
— reached from a different direction.

The seventh, F-83, is not that shape and is the one worth reading.

## F-83 — the repair for F-80 named a class that has never existed

`MenuEventVocabularyContractTests` referred to `EventTypeVocabulary` at four call sites and three `cref`s.
**There is no such type.** It is `EventTypeCatalogue`, declared in `EventExplorerReads.cs`, called by
`EventExplorerQuery.cs` and `EventExplorer.razor`, and spelled correctly in `BUILD_PROGRESS.md`'s own M5
file census.

Five occurrences spelled it wrongly, across three documents:

```
docs/TECHNICAL_SPECIFICATION.md   F-80's Appendix A row      x2
docs/DOCUMENTATION_REVIEW.md      F-80's ledger row          x2
docs/BUILD_PROGRESS.md            Slice 40's own narrative   x1
```

All five were written in the slice that wrote the test. **That is how an identifier gets copied wrong five
times without a reader noticing: the correct spelling and the incorrect spelling were never in one view.**
The test was written from the ledger rather than from the file the ledger describes.

**The consequence is the part to sit with.** `MyRestaurant.WebApplication.Tests` did not compile, so
F-80's repair did not run, so for a full slice the menu vocabulary was *correct in the source* and
*guarded by nothing* — precisely the state F-80 exists to prevent, arrived at through the fix for it.

**No gate is added**, on F-71's ruling in its second application: the compiler is the gate and it blocked.
A gate asserting that documentation names types the tree declares would be a gate about typography, and it
would not have found this any earlier than CSC did.

What is recorded instead is a pattern now on its third consecutive instance. **F-71, F-82, F-83**: three
build failures whose real cost was not the error everybody was reading but the gates downstream of it
reporting nothing.

## F-87 — six assertions, and the two that were reading the wrong row

`0005` makes a heading mandatory and always logged, so a create writes `created` then `section_changed` in
one transaction at one instant, and a create with a description writes a third row. Slice 40 moved five
counts in `MenuAdministrationTests` and left six behind.

Four were plain totals a row short:

```
DescribeWritesTheColumnAndItsEventTogether           2 -> 3
ClearingADescriptionIsAChangeAndStoresTheEmptyString 3 -> 4
ReorderWritesThePositionAndItsEvent...               2 -> 3   (both assertions)
ANegativePositionIsRefusedBeforeAnythingIsWritten    1 -> 2
```

**The other two are the failure worth reading.** They took a payload through the file's newest-event
helper, and after `0005` the newest event of a create is the section one — whose every payload column is
null by CHECK. So:

```
CreateTrimsTheName                     expected "Soup"  actual null
CreateRoundsToTheStoredScaleInBothRows expected 4.57    actual 0
```

Two value mismatches, in the one class whose entire subject is those two values, with nothing in either
message naming the event that had displaced them. A reader chasing that failure starts by suspecting
trimming and rounding — the two things the tests are named after — and neither is wrong.

**The evidence that the shape was understood and simply not carried across:**
`CreateWritesTheItemAndItsCreatedEventTogether`, in the same file, already reads its `created` payload by
explicit `event_type = 'created'`, under a comment saying the newest event is now the section one and that
this fact *"used to be reachable through EventTypeAsync and no longer is"*. The knowledge was in the file,
twenty lines up, in prose.

The repair hoists that query into `CreatedEventScalarAsync`. It carries no `ORDER BY`: a create writes
exactly one `created` row, and an ordering there would imply there might be two.

## F-86 — five of eight types were writable, and the constraint said so in constraint language

`OrderTestWorld.AddMenuItemEventAsync` listed `new_name` and `new_price_amount`. `menu_item_event` has five
payload columns, each tied to its type by a **named biconditional** — an equality, not a permission, so
`description_changed` without a description is refused by the same constraint that would refuse
`activated` *with* one.

So three of the eight admitted types were unwritable through the only helper the suite arranges menu
events with, and the failure surfaced as `menu_item_event_description_payload` two frames below the method
that caused it.

The three new payloads are **optional trailing parameters**, which keeps the five existing call sites
unchanged and expresses the biconditional in C#: a caller that says nothing about a description is a caller
writing a type that must not carry one.

## F-88 — a harness comparing a stylesheet's output against a name

`ReadMenuAsync` read each guest-menu heading with `InnerTextAsync`, which returns text **as rendered**, and
`app.css` declares `text-transform: uppercase` on `.order-menu-section-name`. Scenario 17 created a heading
called *Starters* through the real form and read back `STARTERS`.

**Both alternative repairs are wrong, which is why this has a number.** Asserting the uppercase string puts
a presentation rule inside a scenario about menu structure, so a designer removing the transform breaks a
test that is not about them. Comparing case-insensitively silently accepts a surface that had started
lower-casing headings. Only `TextContentAsync` distinguishes *the name* from *how the name is drawn*.

The audit is recorded rather than the conclusion asserted: `app.css` carries twelve `text-transform`
declarations, and this is the only one whose element the suite compares against a value. The four `dt`
terms under `.order-menu-facts` are read into a dictionary nothing asserts on.

## F-89 — a ruling reversed, and the evidence that reversed it

F-73 found a census count in prose that was stale on arrival, and ruled that it should be **kept** —
because it was the argument for `MinimumCountedClasses` — with the habit of moving it added beside it.

The habit was then tried for three slices:

```
                              floor   summary   S16.4
after F-73  (Slice 36)         ten      ten      ten
after 0004  (Slice 39)      sixteen      ten      ten
after editor (Slice 41)    eighteen  sixteen      ten
```

The floor moved both times. The summary moved once, late, and landed on a number its own next clause
contradicted eleven words later. §16.4 never moved at all and was stale by eight.

Neither prose copy is reachable by any gate: `TestingSectionContractTests` compares assertion counts *per
class* against files, and the census is a count of *paragraphs*, which nothing reads. **This is F-73
recurring inside the repair for F-73**, and the recurrence is the evidence — a habit tried for three slices
that did not hold is not a habit.

**So the ruling is reversed rather than restated.** F-77's habit wins: both prose copies are **deleted**,
leaving the floor as the only place the census is written. That copy is safe in the way the others were not
— it is asserted on every run, so it can go stale only *loudly*, which is the property F-73 was reaching
for and located in the wrong copy.

The floor moves eighteen -> **nineteen**, `MenuEventVocabularyContractTests` having become countable by
gaining a §16.4 paragraph of its own in this slice.

## F-84 and F-85, briefly, because they are what they look like

**F-84.** `MenuItemSummary` went seven members -> ten in `0005`; `OrderStagingTests.Item()` stayed at
seven. CS7036 named `DisplayOrder`, which is not the member that moved — a positional call binds left to
right, so inserting members re-points every later argument and the compiler reports the last parameter it
could not satisfy. **The defect is the good failure and the positional form is kept for that reason:** an
object initialiser would have compiled and left the file describing an item filed under no heading, which
is the one thing `0005` rules out.

**F-85.** `MenuSectionEventLogTests` asked for the username `"mo"` against
`CHECK (char_length(username) BETWEEN 3 AND 64)`. The insert is in `InitializeAsync`, so all six of that
class's facts went red before any assertion ran. `EventExplorerReadsTests` states that minimum in a comment
above its own four people; this is the sibling that did not. F-46's shape at the smallest scale this ledger
has recorded it.

## Two comments under `src/` that asserted the opposite of the code beneath them

Not findings — no behaviour, no gate, caught while reading. `ManageMenuItem.razor` and
`AdministrationMenu.razor` each carried a sentence saying `0005`'s `section_changed` *"will read as itself
until this arm is written"*, and in both files the arm is written twenty lines below. Slice 40 added the
arms and left the sentences. Corrected in the same pass, because a comment that describes the absence of
the code beside it is worse than no comment.

## What is in this slice

- **`MenuEventVocabularyContractTests.cs`** — `EventTypeCatalogue` at all seven references (F-83).
- **`OrderStagingTests.cs`** — ten-argument `MenuItemSummary` (F-84).
- **`MenuSectionEventLogTests.cs`** — a three-character username, and the rule beside the field (F-85).
- **`OrderTestWorld.cs`** — all five payload columns, three as optional parameters (F-86).
- **`EventExplorerReadsTests.cs`** — each type's bound payload, and a real section row for the FK (F-86).
- **`MenuAdministrationTests.cs`** — four counts, two payload reads, one new helper (F-87).
- **`TableOrderJourneys.cs`** — `TextContentAsync` on the heading (F-88).
- **`TestingSectionContractTests.cs`** — floor to nineteen, prose census deleted (F-89).
- **`ManageMenuItem.razor`, `AdministrationMenu.razor`** — two stale comments.
- **Documentation**: spec to v1.27, §16.4 gains three paragraphs and corrects one, Appendix A and
  `DOCUMENTATION_REVIEW.md` gain F-83 through F-89, the plan records what this unblocks.

## What was verified

**The working tree was reconstructed from `dump.txt` and checked against the SHA-256 recorded for every
file: 347 of 348 byte-identical.** The exception is `export.sh`, which contains the dump's own `# FILE:`
banner as literal text and therefore cannot be round-tripped by any parser that uses that banner as a
delimiter. It is excluded from this slice's work set rather than reconstructed and guessed at, and is not
delivered.

**`TestingSectionContractTests` was run in substance**, ported, before and after. Before: 18 counted
classes, 0 disagreements — the tree as delivered was correct on this gate, which is F-82's repair holding.
After: **19 counted classes, 0 disagreements, 0 ambiguous, 0 uncited**, floor met exactly. The nineteenth
is `MenuEventVocabularyContractTests`, which gains a §16.4 paragraph in this slice and is therefore
countable for the first time.

**`MarkdownTableContractTests` was run in substance** over every Markdown file in the repository,
fence-aware and escaped-pipe-aware: **60 table runs, zero findings**, including the fourteen new four-cell
rows across the two registers.

**`SpecificationVersionTests` was run in substance**: header 1.27, newest changelog entry 1.27, 28 entries
descending; `REQUIREMENTS.md` header 6 against newest entry 6; two documents qualify, which is its floor.

**Every `[Fact]` and `[Theory]` count §16.4 states was recomputed against its file.** `MenuAdministrationTests`
stays at **26** — this slice changes what six assertions expect and adds no test method, which is why no
count in §16.4 moves for it.

**The vocabulary gate's own extraction was re-run by hand** against `0005`: the regex matches
`ADD CONSTRAINT menu_item_event_type_vocabulary CHECK (event_type IN` across its newline, yields eight
quoted words, and those eight are set-equal to `EventTypeCatalogue.MenuEventTypes`. So the gate passes
*once it compiles*, which had never been established.

**Brace, paren and bracket balance** on all eight changed C# files, string- and comment-aware — and the
checker was proven against four **untouched, SHA-verified** siblings first, which is the habit Slice 41
paid for. All twelve clean.

**Razor tag-tree comparison** of the two changed components against their SHA-verified originals: markup
identical, 189 and 128 tags. **The checker failed its own proof first, and the failure is instructive.**
Its first run reported `ManageMenuItem.razor` losing four tags — and the four were `<c>` and `<para>`,
which are XML *doc-comment* elements rather than markup, deleted along with the stale sentence. A tag
scanner that does not strip `///` lines is comparing prose. Corrected, re-run, both identical. Independent
corroboration that nothing under `src/` moved: of 3 changed lines in one file and 7 in the other, the
number that are not `///` doc lines is **zero in both**.

**Byte hygiene** on every changed file: no CR, exactly one final newline, no whitespace-only line, no
context-dump separator.

## What was NOT verified

**Nothing compiled.** No .NET SDK in the authoring environment. Named rather than left to be found: the new
optional parameters on `AddMenuItemEventAsync` sit **after** a `CancellationToken`, which is legal and
which no other method in `OrderTestWorld` does — every existing call site passes the token positionally and
stops, so they bind correctly, but a future caller using named arguments will find the ordering unusual.
`CreatedEventScalarAsync` mirrors `ScalarAsync` exactly and uses the same `$"""` raw interpolated form,
which is the construct the balance checker mis-parsed two slices ago.

**No test ran.** Every count below is arithmetic.

**No database confirmed the six repaired assertions.** Each is a claim about how many rows `0005`'s create
writes, derived by reading `DapperMenuAdministration.CreateMenuItemAsync` — which writes `created`
unconditionally, `section_changed` unconditionally, and `description_changed` when the normalized
description is non-empty. The reading is corroborated by two facts in the same file that Slice 40 *did*
update and that consequently passed: `CreateWritesTheItemAndItsCreatedEventTogether` asserts 2, and
`TheHistoryKeepsEveryChangeFromBothWriteServices` asserts 5 for a create plus three later verbs. Both are
only consistent with a two-row create.

**No browser re-ran scenario 17.** `TextContentAsync` returns `string?` where `InnerTextAsync` returns
`string`; the null-coalesce is present, and whether Playwright ever returns null for a matched element with
text content is not something this environment can establish.

**Nothing verified that the five wrong spellings of `EventTypeCatalogue` were the only ones.** A repository
grep found five and they are repaired; a type name that appears in prose in a document nothing parses is
reachable by no gate here, which is F-83's residual and is stated rather than closed.

## Test count

Last predicted: **1149**, from Slice 41 — and **not observed**, because the build failed again. The last
observed count is **1124**, from Slice 39, which is now three slices back.

**Two slices in a row have now predicted a count that never met a run**, which is the F-82 residual
becoming a measurement rather than an argument.

Predicted here: **1149**, unchanged. This slice adds no `[Fact]` and removes none — every repair changes
what an existing assertion expects, or how it reads what it expects. §16.3 stays at **17**.

Per §18: if the run returns anything other than 1149, that difference is the next thing to chase. This is
the third consecutive slice in which that check has been scheduled and the first in which it is expected to
be possible.

## Still open

**`MoveMenuItemToSectionAsync`.** The last verb in the whole enhancement with no surface. Unchanged from
Slice 41, and now unblocked in a way it was not: the helper that would arrange its events could not write
`section_changed` at all until this slice (F-86).

**The sections-first index.** `/administration/menu` is still an item list with a Section column.

**A section's own description under its heading on the guest menu.** Unchanged.

**The kitchen's "86" panel still groups by nothing.** Stage 3's last surface.

**The handheld barrier visits neither section surface.** Scenario 16 walks ten; `ManageMenuSection` is a
detail surface with `.manage-inline-form` buttons and would move the control count. Carried from Slice 41.

**Nothing reports which gates a failed build prevented from running.** F-82's residual, now with a third
instance behind it (F-83).

**F-41 has no row in `DOCUMENTATION_REVIEW.md`.** Seventh slice carried.

**`.sitting-meta` is declared by two components and the two have drifted.** Deferred a ninth time.

**A CI job that runs the canonical stack on the canonical engine.** Eighteenth consecutive slice.

**`run.sh --containers-only` prints two `Error:` lines about a container that does not exist yet, then
starts it successfully.** Carried.

# M6 Slice 43 — the last verb gets its surface, and three numbers nothing could check

**`MoveMenuItemToSectionAsync` lands with the picker on `ManageMenuItem` that calls it.** It was the last
verb in the whole menu enhancement written without a caller, and with it **no method behind
`IMenuWorkflow` is unreachable from a form**. Three findings ship beside it — F-90, F-91 and F-92 — and
they are one defect three times.

## The first thing to read: 1149 was predicted and 1151 ran

Slice 42 predicted **1149**. The run returned **1151**, green, twice. Per §18 that difference is the first
thing chased, and it resolves to the unit.

The tree holds **1132** `[Fact]` plus `[InlineData]` cases. `SchemaMigrationRunnerTests` adds its 5 facts,
`KeyRelations`' **13** theory rows, and `KeyColumnsAddedByAlter`'s **6** — four columns added by `0004`,
two more by `0005`. That is 1151. Substitute the **four** §16.4 states and you get 1149.

**§16.4 says *"Four theory rows therefore name the columns that arrived by `ALTER`"*.** `0005` added
`menu_item.menu_section_identifier` and `menu_item_event.new_menu_section_identifier` to that
`TheoryData` and the sentence did not move. That is **F-90**, and the part worth carrying is why the gate
built for exactly this could not see it: `TestingSectionContractTests` compares an **assertion count per
class**, which for that file is 7 methods and is still correct. A theory-row count is a different quantity
living in the same paragraph. So a number went stale inside the one section written to stop numbers going
stale, in the one form its gate is structurally blind to.

**The instrument that reported it was §18's habit and nothing else.** This is the first finding in the
ledger produced by chasing a predicted count rather than by somebody reading a file — which is what F-70
established the habit for, three slices after the last opportunity to use it.

## F-91 and F-92, which are the same defect one register out

**F-91.** §16.3 scenario 16 says *fifteen controls are expected* and itemises them, including *a rename and
a reprice on the item*. `ManageMenuItem.razor` gained a third `.manage-inline-form` in Slice 38 and a
fourth in Slice 40; this slice makes five. Nothing reported it because the assertion above the census is a
**floor of fourteen**, and a floor that passes at fifteen passes at seventeen — so the itemisation was a
number only a person re-deriving it by hand could ever check.

**The floor is deliberately not raised, and that is a ruling.** Its value is a claim about controls
rendered on ten surfaces against rows one scenario arranges, which this authoring environment cannot
observe. An unobservable raise is precisely the edit that turns a green suite red for no gain. It is now
justified by the **smallest selector group** — `.filter-actions`, two controls, one per read-only
explorer — which is a property of the selector set rather than of a render. The residual is written into
the file: a floor notices a group that vanished and never one that grew, and making it a census honestly
would mean attributing each measured control to the selector that matched it, which
`HandheldReachReport` does not carry.

**F-92.** The specification's opening sentence has cited `REQUIREMENTS.md` (rev 5) since v1.15 moved that
document to rev 6. `SpecificationVersionTests` compares each document's header against its **own**
changelog, which is what F-58 asked for and is a different question — so both documents were internally
consistent and disagreed with each other. **The sibling document had already learned this and said so in
its own header:** `REQUIREMENTS.md` records that it deliberately does not restate the specification's
version, *because a version of another document is a restatement joined to its subject only by somebody
remembering to edit it*. F-58's lesson was written into one of the two documents and not the other, so the
citation that survived is the one pointing the other way.

All three are repaired identically — **the number is deleted, not corrected** (F-77) — and in all three
**no gate is added** and the residual is stated. A gate for one sentence leaves every other instance of
the class untouched (F-47).

## The move, and the three rulings inside it

**An item is appended to the end of its new heading.** Carrying the old position across would drop the dish
into the middle of an ordering somebody chose for a different list, because a position is a position
*within* a heading. `MAX(display_order) + 1` under a lock on the target section row is exactly what
`CreateMenuItemAsync` does, so after this verb the two are one rule: an item arriving in a heading,
however it arrives, arrives at the end of it.

**Two events, or one.** §8.2 binds `new_display_order` to `reordered` alone, so a move that also changes
the position must say so in a second event rather than move a number the log does not mention. It is
conditional on the number actually differing — a move into an empty heading from position 0 lands at 0
again, and an event reading *moved to position 0* beside an unchanged column is the "somebody pressed
Save" noise §11.4's history exists to refuse. Order: `section_changed` then `reordered`.

**The item lock is taken before the section lock.** It is the only nested acquisition in that file and it
runs in the direction every existing write already runs in — the item verbs lock an item and nothing else,
`CreateMenuItemAsync` locks a section and nothing else. Two administrators moving two dishes into each
other's headings therefore cannot deadlock: both take their item lock first, and neither holds a section
lock while waiting for one.

**Nothing else about the item moves.** Name, price, description and `is_active` are untouched, so an 86'd
dish refiled between headings is still 86'd — the item-side counterpart of §7's rule that deactivating a
heading does not cascade to its items.

## The publish is as loud as a section visibility flip

§11.1 groups the guest menu by heading, so a committed refile moves a card between groupings on every open
picker in the building. A refile **into an inactive heading** removes the card from the guest's menu
entirely, because §7 renders no such heading at all — which is the same reach `SetMenuSectionActiveAsync`
has, arriving from the other direction. Conditional on `Moved` alone: `NoChange`, `MenuItemNotFound` and
`MenuSectionNotFound` each commit nothing.

## The obligation is discharged rather than narrowed, and how it was carried is the transferable part

A workflow verb with no caller is a code path no test can reach through the interface meant to protect it.
Six verbs were held outside `IMenuWorkflow` under that rule across seven slices — five section verbs and
this one — and **the count of how many were outstanding was written down every single slice.** That is the
only reason its reaching zero is a fact somebody can state rather than something noticed later. A deferral
named every slice is a deferral; one named once is an omission with a date on it.

## What is in this slice

- **`Menu/MenuAdministration.cs`** — `MoveMenuItemToSectionOutcome` (four members, where the reorder
  outcome has three), `MoveMenuItemToSectionAsync`, `menu_section_identifier` added to the shared lock read
  and to `MenuItemLockRow`, and `UpdateMenuSectionAndPositionSql`.
- **`Menu/MenuWorkflow.cs`** — the verb, publishing on `Moved`.
- **`Administration/ManageMenuItem.razor`** — a `.manage-inline-form` section picker between Description
  and Position, pre-selected on the item's own heading, offering inactive headings marked.
- **`MenuAdministrationTests.cs`** — 26 → **31**.
- **`MenuWiringTests.cs`** — 18 → **19**, plus the fake's new verb.
- **`AdministrationJourneys.cs`** — `MoveMenuItemToSectionAsync`.
- **`EndToEndScenarios.cs`** — scenario 17 gains step (i); scenario 16's census replaced by its rule
  (F-91).
- **Documentation** — specification to **v1.28** (§0, §7, §11.4, §16.4, Appendix A, changelog),
  `DOCUMENTATION_REVIEW.md` gains F-90 through F-92, the plan strikes the last deferred verb.

## Two things about the surface that are decisions rather than markup

**The button reads "File here", not "Move".** The Position form's button already says *Move*, and
Playwright's `has-text` is substring matching — so a second button containing that word would make every
locator in the harness ambiguous the day somebody wrote one. The name is chosen against a test-harness
constraint rather than for its own sake, which is worth saying because it looks arbitrary otherwise.

**No CSS moves.** `.manage-inline-form` has styled `select` since Slice 34 — which is the same fact the
comment beside the description form states from the other end when it says that class has never styled a
`textarea`. The page's closing note used to say it carried *two small forms*; it carried four. That count
is deleted rather than corrected, on the same ruling as the three findings above.

## What was verified

**The working tree was reconstructed from `dump.txt` and checked against the SHA-256 recorded for every
file: 348 of 349 byte-identical.** The exception is `export.sh`, which contains the dump's own `# FILE:`
banner as literal text and therefore cannot be round-tripped by any parser using that banner as a
delimiter. It is excluded from the work set rather than reconstructed and guessed at, and is not delivered.

**`TestingSectionContractTests` was run in substance**, ported: **19 counted classes, 0 disagreements, 0
ambiguous, 0 uncited**, floor met exactly. The two counts this slice moves — `MenuAdministrationTests` to
31 and `MenuWiringTests` to 19 — were compared against the files by that port and agree.

**The new §16.4 paragraph names two test classes and states no count**, which the gate reads and correctly
declines to attribute. That is deliberate: a second paragraph claiming a count for a file another paragraph
already counts would be the same fact written twice in the section whose whole subject is that mistake.

**`SpecificationVersionTests` was run in substance**: header 1.28, newest changelog entry 1.28, **29
entries descending**.

**`MarkdownTableContractTests` was run in substance** over every Markdown file outside `docs/llm/`,
fence-aware and escaped-pipe-aware: **61 table runs, zero ragged**, including the six new four-cell rows
across the two registers.

**Brace, paren and bracket balance** on all six changed C# and Razor files, string- and comment-aware —
and the checker was proven against **four untouched, SHA-verified siblings** first, which is the habit
Slice 41 paid for. All ten clean.

**Razor tag-tree comparison** of `ManageMenuItem.razor` against its SHA-verified original: 188 tags before,
**208 after**, and the diff is **one contiguous insertion** and nothing else — the twenty tags of the new
form and the heading above it. The markup walk closes every element it opens, with nothing unbalanced and
nothing left on the stack.

**Byte hygiene** on every changed file: no CR, exactly one final newline, no whitespace-only line, no
context-dump separator.

**The CS4007 scan caught one, which is the reason it is run.** The new harness journey's failure message
was first written as `$" effect. {await DescribeFailureAsync(page)}"` — an `await` inside an interpolated
string hole bound to `DefaultInterpolatedStringHandler`, which does not compile. Repaired to the shape its
four neighbours in that file already use: the `await` is a separate operand of the concatenation rather
than a hole. Worth recording because it is a defect a reading finds only if it is looking for it, and
because §18's list of mechanical traps exists precisely so that this class is scanned rather than reviewed.

**The Razor directive gate was run in substance** over the changed component: no bare `@section` or
`@RenderSection`, and the one occurrence of the word after a transition is `@@section` inside a comment,
which is Razor's escape and which `RazorDirectiveContractTests` explicitly admits (F-81).

**The F-90 arithmetic was recomputed from the tree** rather than taken from the prediction it explains:
1132 counted directly, 13 and 6 counted out of the two `TheoryData` initialisers, and 5 facts in the file
itself.

## What was NOT verified

**Nothing compiled.** No .NET SDK in the authoring environment. Named rather than left to be found:
`MoveMenuItemToSectionAsync` is the third method on `IMenuAdministration` whose parameter list is two
`Guid`s in a row, and the two are `menuItemIdentifier` then `menuSectionIdentifier` — the same order
`CreateMenuItemAsync` uses, deliberately, but positionally interchangeable to the compiler. Every call site
in this slice passes them in that order; a transposition would compile and fail at run time as
`MenuItemNotFound`.

**No test ran.** Every count below is arithmetic.

**No database saw the move.** The append is derived from reading
`LockMenuSectionAndReadNextPositionSql`, which `CreateMenuItemAsync` already uses and which
`ItemsAreAppendedToTheEndOfTheirOwnSection` already proves against a real PostgreSQL — so what is new here
is the caller, not the query. That two events are permitted in one transaction rests on §8.2's paired
CHECKs, which `MenuAdministrationTests` exercises for each type separately and which no fact yet exercises
in this combination.

**No browser ran scenario 17's new step.** The arrival barrier is a CSS attribute selector on the facts
grid's Section link, `a[href='/administration/menu/sections/{guid}']`, which requires the rendered `href`
to match exactly — Razor renders a `Guid` with its default `D` format and `ToString("D")` produces the
same, but this environment cannot observe that agreement.

**Nothing verified that F-91's census was the only stale count of its kind in the harness.** A repository
reading found this one; a number in a comment nothing parses is reachable by no gate here, which is F-90's
residual restated in a second file.

## Test count

Last predicted **1149**; **observed 1151** — the first prediction to meet a run in three slices, and the
difference is F-90 above.

Predicted here: **1157**. Arithmetic on the observed number, per §18: 1151 + 5
(`MenuAdministrationTests` 26 → 31) + 1 (`MenuWiringTests` 18 → 19). Scenario 17 is **extended, not
added**, so §16.3 stays at **17** and the end-to-end project stays at 17 facts.

Per §18: if the run returns anything other than 1157, that difference is the next thing to chase.

## Still open

**The sections-first index.** `/administration/menu` is still an item list with a Section column. It is now
the largest remaining piece of Stage 3, and nothing blocks it — the editor it needs to open into has
existed since Slice 41.

**A section's own description under its heading on the guest menu.** Unchanged. It needs either a second
read or a widened `MenuItemSummary`, and F-84 is the reason widening that record is not free.

**The kitchen's "86" panel still groups by nothing.** Stage 3's last surface.

**The handheld barrier visits neither section surface, and now it also misses a form.** Scenario 16 walks
ten surfaces; `ManageMenuSection` is a detail surface with `.manage-inline-form` buttons and would move the
control count. As of this slice the item detail page it *does* visit has five inline forms where the
barrier's own account said two — corrected as F-91, but the gap it points at is unchanged.

**No gate can see a count written in a comment.** F-90's and F-91's shared residual, stated once here
rather than twice above.

**Nothing reports which gates a failed build prevented from running.** F-82's residual, carried.

**F-41 has no row in `DOCUMENTATION_REVIEW.md`.** Eighth slice carried.

**`.sitting-meta` is declared by two components and the two have drifted.** Deferred a tenth time.

**A CI job that runs the canonical stack on the canonical engine.** Nineteenth consecutive slice.

**`run.sh --containers-only` prints two `Error:` lines about a container that does not exist yet, then
starts it successfully.** Carried.

---

# M6 Slice 44 — the index becomes the menu, and a barrier that measures by list

**Stage 3's largest remaining piece.** `/administration/menu` was a flat list of every item with the heading
as a *column*; it is now a group per heading, each holding the items filed under it. That column was shipped
deliberately in Slice 40 as an honest intermediate and named as one in the same breath, on the reasoning that
a sections-first index needs an editor to open into and a record list whose rows link nowhere is a list of
dead ends. The editor landed in Slice 41, the refile verb in Slice 43, and **nothing had to be undone to get
here** — which is the claim that intermediate was chosen against, now testable and true.

## The surface

**A heading is a `<details>` and its summary is the heading.** A native disclosure, chosen for four
properties rather than for looking modern: it collapses with no script on a page that is static SSR and hosts
no island, it is a disclosure in the accessibility tree rather than a `div` somebody wired up, it needs no
state on the server, and `summary` keeps its own `display: list-item` so the marker is the affordance. The
last of those is why `.menu-group-summary` does not set `display: flex` — that removes the marker in every
engine that draws one, and then the control's own affordance has to be reinvented.

**It is rendered open on every request, and there are two independent reasons.** A heading a server
collapsed is a heading whose items nobody looking for an item can find. And §16.3 scenario 16 measures what a
layout engine laid out: a control inside a closed `<details>` has no box, so a collapsed group would remove
its own controls from the barrier that exists to catch controls going missing. Collapsing is the operator's
decision; the server does not make it for them.

**An empty heading is on this surface and on no other page in the application.** The old list was built from
`menu_item`, so a heading with nothing under it was invisible everywhere — §11.1 renders no empty heading to
a guest, and the index could not see one at all. A heading created with a typo could only be worked around
by making another, and the *reason* it could only be worked around was that nothing showed it existed. The
page now reads both directories: `IMenuSectionDirectory.ListAsync` for the headings, which is what that read
was written for and which says so in its own doc comment, and `IMenuDirectory.ListAsync` for the items.

**Filtered per heading rather than grouped, and that is §7's ordering being collected on.** The six-key
`ORDER BY` already makes each heading's items contiguous, so `Where(...)` preserves the order somebody chose,
where a `GroupBy` would re-order the headings into hash order and make the ordering decision a second time in
a second file. A per-section read would be a query with one caller, which `IMenuDirectory` declines to invent
for exactly this reason. `ManageMenuSection` has done the same filter since Slice 41, so the two surfaces now
group by one rule.

## The cut, and why it is a ruling rather than a shortfall

**The plan promised this index "with the section's own order controls" and they are not in it.** The reason
only becomes visible once somebody tries to write them. `ReorderMenuSectionAsync` sets an **absolute**
`display_order`, and §7 makes positions deliberately non-unique with a name tie-break — so *move this heading
above that one* is not expressible as one absolute write. Two headings sharing a position have an order
nobody assigned, and there is no single number that distinguishes them: writing the predecessor's position
onto the mover leaves both at the same number, ordered by name, which is where they already were.

An honest up/down control therefore needs a **resequencing verb** — one transaction, a lock per affected row,
and one `reordered` event per row whose number actually moved. That is a new write with new event semantics,
not a surface change, and it is a slice of its own. So the index makes the ordering **legible** — headings in
stored order, each one's position on its own summary — and the editor keeps the write.

**The transferable rule is narrower than "defer the hard part".** When a surface would need a verb the model
cannot express, the surface ships without it and says so, rather than shipping a control that is right in the
common case and silently wrong wherever the data is *permitted* to be ambiguous. The permission is the point:
non-unique positions are a decision this project made on purpose, so an up/down control would not be a rare
edge case — it would be wrong exactly where somebody had never bothered to assign distinct numbers, which is
the default state of every menu this application creates.

**Recorded for veto**, with the reversal spelled out in `_CHANGES.md`: `ResequenceMenuSectionsAsync` behind
`IMenuWorkflow`, and a two-button form per group whose `@formname` is derived from the section identifier —
the shape `ManageMenuSection`'s visibility toggle already uses, and the only shape that works for N forms
without N `[SupplyParameterFromForm]` properties, because that attribute's `FormName` must be a compile-time
constant.

## F-93 — the barrier measures by list, and a surface can go quiet inside it

**The finding is a property of the gate, not of this page.** §16.3 scenario 16's reach selector names
`.record-actions`, `.page-head-action`, `.filter-actions` and `.manage-inline-form button`. The membership
*rule* is good — *the thing an operator opened the page in order to press* — and it is enforced as a list of
class names, which means a surface can keep being **visited** and stop being **measured** with nothing
reporting it.

This slice is where that became concrete rather than hypothetical. The sections-first index replaces every
`.record-actions` row on its primary list with a `.menu-group` group, whose two controls — the disclosure and
the link into the editor — are in none of the four groups above. **The floor would have gone up while
coverage of the heading went to zero**, because the item rows *inside* the groups still carry
`.record-actions`. A floor notices a selector group that vanished and never one that was never counted, which
is F-91's stated residual arriving as a live defect one slice after it was written down as a note.

`.menu-group-summary` and `.menu-group-actions a` join the reach set, and **the repair recorded in §16.4 is a
rule rather than two selectors**: a surface that acquires a new kind of control acquires a selector in the
same slice, or it is a surface this barrier has stopped asserting anything about. A `<summary>` is admitted on
the existing membership rule rather than as an exception to it — it occupies the position `.record-primary`
holds on every other index, and it is the only control the new surface introduced.

**No gate is added and the residual is restated.** Making the floor a census honestly means attributing each
measured control to the selector that matched it, which `HandheldReachReport` does not carry. That was
declined in Slice 43 and is declined again here, for the same reason and now with a second instance behind it.

## F-94 — a count that named one set and computed another, under a comment that knew

`AdministrationMenu.razor` closed with *"N of M available, K described, across S sections"*, and `S` was
`_items.Select(item => item.MenuSectionIdentifier).Distinct().Count()` — the number of headings **with
something under them**. A menu with five headings and two stocked read *across 2 sections* to the one person
in the building entitled to see all five.

**The page knew, and said so.** The line above it carried a comment reading *"not the same number as the
section count — an empty heading exists and is not visible here"*. That is **F-65's mechanism** — a comment
asserting what the code beneath it does not do, in the position a reader checking the number would stop —
with one difference worth naming: F-65's comment was *false* about its declaration, and this one was
*accurate* about its computation and still left the screen wrong. An honest comment about a defect is not a
fix, and it is more durable than the defect, because the next reader stops at the explanation.

**No gate is added.** A number computed from a list two lines above it is not a restatement of anything, so
there is no second copy for a gate to compare against; what was wrong was the English. Prose describing a
computation inside a component is reachable by no gate this repository has, which is stated as the residual
(F-47).

## What is in this slice

- **`Administration/AdministrationMenu.razor`** — rewritten sections-first: two directory reads, a group per
  heading, the item list inside each, the counts corrected per F-94.
- **`wwwroot/app.css`** — the `.menu-group*` vocabulary, declared once, plus one rule inside the existing
  `min-width: 48rem` query.
- **`Harness/HandheldReach.cs`** — two selectors join the reach set, with the membership reasoning (F-93).
- **`Components/HandheldLayoutContractTests.cs`** — `.menu-group` joins `SharedSelectorPrefixes`, and the
  list becomes a multi-line initialiser so the next addition is a one-line diff.
- **`Harness/AdministrationJourneys.cs`** — `MenuHeadingOnTheIndex` and `ReadMenuIndexAsync`.
- **`EndToEndScenarios.cs`** — scenario 17 gains step (j).
- **Documentation** — specification to **v1.29** (§7, §11.4, §16.3, §16.4, Appendix A, changelog),
  `DOCUMENTATION_REVIEW.md` gains F-93 and F-94, the plan strikes the index and records the cut.

## Scenario 17's new step, and why the assertion is a disagreement

**A third heading is created and left empty.** §11.1 renders no empty heading to a guest; §11.4 renders the
complete record to the administrator. So the same instant must produce **three groups on the index and two
groupings on the guest's menu**, and the difference must be exactly the empty one.

**Neither surface alone says anything.** A heading missing from the guest's menu has three possible reasons —
it is inactive, it is empty, or the page is broken — and a heading present on the index has none. It is the
comparison that is the test, which is why the step reads both and why it is in scenario 17 rather than in a
scenario of its own.

The step also asserts the index's **ordering** (stored, not alphabetical — a page sorting its own headings
would put *Puddings* first and pass every other assertion here) and that the refile from step (i) is visible
from the administration side, so an index grouping by anything other than `menu_item.menu_section_identifier`
fails here even where the guest's menu is right.

**One direction is weaker and it is named in the file.** The closing assertion is that the guest never sees
the empty heading, and an assertion of absence cannot prove §9's broadcast landed. What carries it is the
disagreement with the index above, which was read from a page that had to be fetched.

## What was verified

**The working tree was reconstructed from `dump.txt` and checked against the SHA-256 recorded for every file:
348 of 349 byte-identical.** The exception is `export.sh`, which contains the dump's own `# FILE:` banner as
literal text and therefore cannot be round-tripped by any parser using that banner as a delimiter. It is
excluded from the work set rather than reconstructed and guessed at, and is not delivered.

**The balance checker reported findings on a correct file three times before it was trusted, and that is the
habit paying for itself rather than an anecdote.** It was run against four untouched, SHA-verified siblings
first (F-41's rule, and Slice 41's lesson). It failed on `ManageMenuSection.razor` three times in
succession, each time for a different reason, and each reason is a real property of this tree: an apostrophe
in Razor *prose* read as the start of a C# character literal, so the scan swallowed text to the next
apostrophe and lost a brace; `<ValidationMessage For="…" />` counted as an unclosed element, because the
self-close was being read out of a lazily-matched attribute group; and `For="@(() => Input.Name)"` truncating
the tag match at the `>` of the lambda arrow. **A checker that had been pointed at the changed files first
would have reported all three as defects in this slice's work.**

**Razor tag-tree walk** of the rewritten page: **75 tags**, every element closed, nothing left on the stack —
and the same walk run over two untouched SHA-verified siblings, `ManageMenuSection.razor` (112 tags) and
`AdministrationTables.razor` (39 tags), both clean, which is what makes the first number mean anything.

**Every class the new page names is declared in `app.css`:** 32 class tokens, none undeclared. Run over the
two control files as well — 28 and 20 tokens, none undeclared.

**The cell-label gate was run in substance** on the rewritten page: **9 `<td>` and 9 `data-label=`**, exact,
which is the arithmetic `EveryRecordListCellCarriesTheLabelThatReplacesItsColumnHeader` performs. The page
still contains two record lists, so the gate's floor of seven pages is unaffected.

**The stylesheet's own facts were checked in substance:** **two media queries in the file and exactly one
carrying a width**, `min-width`, no `max-width` anywhere; **no undeclared custom property read**; **zero
`var()` fallbacks**; **no colour literal outside `:root`**; and every `min-height` in the file is
`var(--touch-target)`, exactly `0`, `5.5rem` or `100dvh` — the floor holds with nothing new added to it.

**No component declares a `.menu-*` selector inline**, checked before `.menu-group` was added to
`SharedSelectorPrefixes`, so the new prefix reports nothing on a correct tree (F-67's rule, and the reason
that ruling had to exist before any prefix could be added).

**`SpecificationVersionTests` was run in substance:** header **1.29**, newest changelog entry **1.29**, **30
entries descending**.

**`MarkdownTableContractTests` was run in substance** over every Markdown file outside `docs/llm/`,
fence-aware and escaped-pipe-aware: **61 table runs, zero ragged**, including the three new four-cell rows
across the two registers.

**`TestingSectionContractTests` was run in substance**, ported: **19 counted classes, 0 disagreements, 1
ambiguous paragraph** (one that names two classes and is correctly skipped), floor of nineteen met exactly.
The two new §16.4 paragraphs name no test class and state no count, so the gate reads them and correctly
declines to attribute anything — which is deliberate, F-89's ruling being that the floor is the only census.

**Byte hygiene** on every changed file: no CR, exactly one final newline, no whitespace-only line, no
context-dump separator.

**The CS4007 scan found nothing this time**, which is worth one line rather than none: the new harness method
composes its failure message by concatenating an `await` as a separate operand rather than putting it in an
interpolation hole, which is the shape its four neighbours in that file already use and the shape Slice 43
had to repair.

## What was NOT verified

**Nothing compiled.** No .NET SDK in the authoring environment. Named rather than left to be found: the new
page's `@code` block declares `_items` as `IReadOnlyList<MenuItemSummary>` initialised to `[]` and `_sections`
as nullable, and the markup branches on `_sections is null` while `ItemsUnder` dereferences `_items` — which
is safe only because both are assigned in the same `OnInitializedAsync` before any branch reads either. A
static SSR intermediate render fires `StateHasChanged` the moment `OnInitializedAsync` suspends, so the
branch order matters: `_sections is null` is tested first and is the only branch that reaches `ItemsUnder`.

**No browser rendered a `<details>`.** The disclosure is the one piece of this surface whose behaviour is a
browser's rather than this project's, and three claims about it are unobserved here: that `min-height` on a
`display: list-item` box produces a 44px target, that the marker survives the padding, and that a group
rendered with the `open` attribute is laid out with its body measurable. All three are ordinary HTML and all
three are what scenario 16 would fail on.

**No test ran.** Every count below is arithmetic.

**Nothing measured the new controls.** F-93's repair is two selectors in a reach set, and whether the
disclosure and the editor link actually lie inside a 375px viewport is precisely the question this
environment cannot answer — which is the same reason F-91's floor was not raised.

**Nothing verified that F-93 is the only surface the barrier has gone quiet on.** The mechanism is general: any
surface that changes vocabulary does this, and the gate has no way to report a class it was never told about.
The rule is now in §16.4, which makes the next instance a rule violation rather than an accident, and does
nothing about a past one.

**Nothing verified that F-94 is the only count of its kind in a component.** A repository reading found this
one; a number computed inside a `@code` block and described in prose beside it is reachable by no gate here.

## Test count

Last predicted **1157**, by Slice 43, and **not known to have run** — this session has no observation of it.
Predicted here: **1157**, unchanged, and unchanged for a reason rather than by coincidence. No test class is
added. No `[Fact]` or `[Theory]` row is added. `HandheldLayoutContractTests` gains a list *entry* rather than
an assertion, so its count of nine is untouched and §16.4's census does not move. Scenario 17 is **extended,
not added**, so §16.3 stays at **17** and the end-to-end project stays at 17 facts.

Per §18: if the run returns anything other than 1157, the difference belongs to **Slice 43's** arithmetic
rather than to this slice's, and that is the first place to look.

## Still open

**A section's own description under its heading on the guest menu.** Unchanged, and now the largest remaining
piece of Stage 3. It needs either a second read or a widened `MenuItemSummary`, and F-84 is the reason
widening that record is not free.

**The kitchen's "86" panel still groups by nothing.** Stage 3's last surface.

**Reordering a heading from the index.** New, and named rather than discovered: it needs
`ResequenceMenuSectionsAsync`, which is a write rather than a surface, for the reason recorded above.

**The handheld barrier visits neither section surface.** Scenario 16 walks ten surfaces; `ManageMenuSection`
is a detail surface with `.manage-inline-form` buttons and would move the control count. Carried, and now
larger than it was: the menu index it *does* visit has changed shape entirely since the barrier last had
anything to say about which controls it holds.

**No gate can see a count written in a comment, or a sentence describing a computation beside it.** F-90's,
F-91's and now F-94's shared residual, stated once.

**Nothing reports which gates a failed build prevented from running.** F-82's residual, carried.

**F-41 has no row in `DOCUMENTATION_REVIEW.md`.** Ninth slice carried.

**`.sitting-meta` is declared by two components and the two have drifted.** Deferred an eleventh time.

**A CI job that runs the canonical stack on the canonical engine.** Twentieth consecutive slice.

**`run.sh --containers-only` prints two `Error:` lines about a container that does not exist yet, then starts
it successfully.** Carried.

---

# M6 Slice 45 — the tie-break that was a coin flip

**A test failed, then passed, on an unchanged tree, and the failure message was the whole finding.** The
first `dotnet test` of the session reported one inverted pair in `MenuEventLogTests`: `section_changed`
expected where `created` arrived. Those are two events of one create transaction, written at one instant, and
their order in the log is decided by the identifier tie-break. So the tie-break was not breaking the tie.
`bash scripts/ci_local.sh --with-all --with-e2e` then ran the same suite green, and the count came back at
1157 exactly as Slice 43 predicted — which is what a 50% property looks like from inside a green run.

**`Guid.CreateVersion7()` is ordered between milliseconds and random inside one.** Verified against
`dotnet/runtime` `release/10.0`: the method is `Guid.NewGuid()` with the 48-bit Unix-millisecond timestamp
written over the top and the version and variant nibbles set. The other 74 bits are left exactly as the
cryptographic source produced them. Two values minted in the same millisecond therefore share their entire
ordered prefix and are separated only by random bits — 49.8% of such pairs invert under PostgreSQL's `uuid`
comparison, measured over 20,000 pairs.

## Why the schema was leaning on the missing half

Every mutation in §8 reads `IClock.UtcNow` **once** and stamps every row of its transaction with it. That is
deliberate and it is right: a row and its event describe one change and should agree about when it happened.
The consequence is that same-instant rows are the normal case rather than the edge case, so nine reads in
`MyRestaurant.DataAccess` order by an instant and then break the tie on an identifier, and `OrderProjection`
does the same for lines. Every one of those was resolving the tie at random.

**Three menu surfaces and one guest surface were affected, and the guest one is the worst.**

- **§11.4's per-item history.** A menu item created under a heading with a description writes `created`,
  `section_changed` and `description_changed` at one instant. Six orderings, one of them right: the history
  read *"Created as “Soup” at 4.50 / Filed under Starters / Description set"* **one time in six**, and read
  the effect before the cause the rest of the time.
- **The section history**, same shape, one register up.
- **The index's activity feed**, added last slice, reordered itself between page loads on unchanged data.
- **A guest's basket order did not survive being sent.** `OrderStaging.Build` mints one line identifier per
  basket row in a tight loop **in basket order** — the loop that builds the human-readable descriptions
  alongside them depends on that order and shows it correctly. `OrderProjection` then orders lines by their
  added-at instant and breaks the tie on `order_line_identifier`, and every line of one send shares that
  instant. So the basket was ordered, the descriptions were ordered, and the order itself was shuffled: on
  the guest's own surface, on the kitchen ticket, and on the bill.

## F-95 — and what the comments were doing

Four comments asserted the property. Two in `MenuEventLog`, one in `AdministrationMenu.razor`'s `@key`
explaining why an index key would be wrong, one in `MenuEventLogTests` explaining why `section_changed` sits
second. Every one of them reasoned **correctly from a false premise**: the values are UUIDv7, UUIDv7 is
time-ordered, therefore these two are ordered. The first two clauses are true and the conclusion does not
follow for two values inside one millisecond.

That is a different failure from F-65's and F-77's, which were two copies of one number disagreeing. Here
there was one claim, load-bearing in four places, and **half right in a way that reads as fully right** —
which is why four separate readings of those files never caught it. So the repair is not deletion, and this
is the rare case where F-77's delete-the-number ruling does not apply: the prose was right about the
mechanism and wrong about who provided it. The comments now name the factory's **contract** as what makes the
tie-break hold, which is a sentence a test can fail.

## What is in this slice

- **`IIdentifierFactory.Create`** gains the contract: successive calls ascend under PostgreSQL's `uuid`
  ordering, including inside one millisecond. Stated on the interface because nine reads depend on it and
  none of them can do anything about it individually — the F-47 habit, applied to a guarantee rather than to
  a list.
- **`UuidV7IdentifierFactory`** keeps it with a 12-bit counter in `rand_a`, which is RFC 9562 §6.2's first
  named method for exactly this. `Guid.CreateVersion7()` is still called, for its 62 random `rand_b` bits
  from the same cryptographic source and for the version and variant nibbles it has already placed; only
  bytes 0–7 are replaced. No new dependency, no new randomness source.
- **The concurrency argument is one `long`.** It packs the millisecond count above the counter, which is the
  first 60 bits of the value *in the order PostgreSQL compares them* — so "the next identifier ascends" and
  "the next packed value is larger" are the same statement, and the whole proof is that one `long` only ever
  increases. Advanced by `Interlocked.CompareExchange`; a failed exchange returns the value that beat it, so
  a thread only ever loses to a thread that made progress. 48 + 12 = 60 bits cannot overflow a `long`.
- **The two edges both resolve the same way.** Counter exhaustion carries into the timestamp rather than
  wrapping, because a wrap hands out a value sorting *before* its predecessor — the defect back under a
  smaller name. A clock stepping backwards is that case seen from the other side: the candidate is behind
  what was issued, so issued-plus-one is used and the stream never doubles back.
- **The state is static**, and that is tested rather than asserted. The application registers a singleton so
  an instance field would suffice in production, but the guarantee is about the *process's* stream: two
  factories each ascending independently would satisfy an instance field while handing two callers values
  that interleave wrongly.
- **`UuidV7IdentifierFactoryTests`** goes from 2 assertions to 7. The five new ones are the burst, the
  two-instance interleave, the counter-exhaustion carry, the concurrent sort-key distinctness, and the
  variant bits surviving the overwrite.
- **§16.4** gains a paragraph and its enforced census floor moves 19 → 20. **§8.1** carries the requirement.
  **ADR-0011** is amended with the counter and both edges. **Appendix A** gains F-95;
  `DOCUMENTATION_REVIEW.md` gains its row and the narrative about probabilistic evidence.
- **`MENU_AND_HANDHELD_PLAN.md` gains Stage 3a**, the resequencing verb, fully specified and recorded as
  unblocked — see the ruling below.

## Every comparison is the database's, and that is the load-bearing choice

`Guid.CompareTo` is **not** PostgreSQL's `uuid` order. The BCL compares a `Guid` field by field and the
second field is a *signed* 16-bit integer holding the low sixteen bits of the millisecond, so it reads as
negative for half of every 65.536-second window; the two orders disagree wherever a pair straddles a boundary
at which those bits cross 0x7FFF. A concrete disagreeing pair was constructed and checked.

So every assertion in the new tests compares `ToByteArray(bigEndian: true)` byte by byte, and a private
`SortKeyOf` exists so that no assertion can quietly acquire the wrong comparison later. A version of that
file written with `CompareTo`, `Comparer<Guid>.Default`, or `OrderBy(identifier => identifier)` would pass
while pinning a relation no read uses — F-41's shape arriving through a comparison operator rather than
through a scope.

## The reordering verb is deliberately not in this slice

§7 records `/administration/menu`'s inability to reorder a heading as a cut, and Slice 44 named
`ResequenceMenuSectionsAsync` as what it needs. **It is not here, and the reason is this slice's own
subject.** A resequencing verb writes several `reordered` events in one transaction, therefore at one
instant, therefore in an order decided entirely by the property F-95 is about. Shipping it before the fix
would have written a correct set of rows in a random order onto the surface whose whole job is legibility.
Shipping it *with* the fix would have made the verb's ordering test simultaneously the first test of the fix,
so a red run could not have said which change caused it — and §18's habit of chasing a count deviation
before the slice closes is cheap with one candidate and expensive with two.

Stage 3a in `MENU_AND_HANDHELD_PLAN.md` carries the full design so the next slice is arrangement rather than
design, including the two obligations it inherits: F-93's selector, and the explicit statement that the
item-level mirror is a third slice rather than an oversight.

## What was verified

- **The root cause, against the framework source.** `Guid.cs` on `dotnet/runtime` `release/10.0` was read
  through the GitHub API. `CreateVersion7(DateTimeOffset)` calls `NewGuid()`, overwrites `_a` and `_b` from
  `unix_ts_ms`, sets the version nibble in `_c` and the variant in `_d`, and returns. There is no counter and
  no monotonic state, so the finding is a property of the method rather than an inference from a failure.
- **The ordering claim, by measurement.** A byte-accurate model of both the old and the new value layout:
  same-millisecond pairs invert 49.8% of the time before, 0 times in 20,000 after; a three-event create reads
  in the minted order 16.7% of the time before; 50,000 values across a moving clock give 0 inversions; 10,000
  inside one millisecond give 0 inversions with the timestamp borrowing 2 ms.
- **The exact byte arithmetic of the shipped `Create`, transliterated and run.** 5,000 values inside one
  millisecond: 0 inversions, every value version 7, every variant nibble `0x80`, timestamp carried forward by
  1 ms, all sort keys distinct.
- **The `CompareTo` trap, by construction.** A pair for which PostgreSQL's order and the BCL's disagree was
  built and both answers printed.
- **`TestingSectionContractTests` simulated in full** against the edited specification: every cited
  `*Tests.cs` resolves, 20 paragraphs state a count, and every count equals the `[Fact]`/`[Theory]` total in
  the file it names. 94 uniquely named test classes found, against the walk's floor of 20.
- **`SpecificationVersionTests` simulated.** Header `1.30` matches the newest changelog entry `v1.30`; the
  history descends; both versioned documents still parse.
- **`MarkdownTableContractTests` simulated.** 20 documents, 56 tables, 583 rows, every row the width of its
  header. This caught a real defect before packaging: the F-95 row contained a literal `|` inside backticks,
  which the gate reads as a cell boundary, so the row had five cells in a four-column table. The phrase was
  rewritten in words.
- **Byte hygiene** on every changed file: LF only, exactly one final newline, no trailing whitespace, no
  whitespace-only lines.
- **Brace and paren balance** on both changed C# files, and the new test file's `[Fact]` count read back as 7
  by the same regex the census gate uses.
- **The reconstructed tree itself.** All 349 files from `dump.txt` SHA-256 verified against the manifest the
  exporter wrote.

## What was NOT verified

- **Nothing was compiled, and nothing was run.** There is no .NET SDK in this environment and the package
  feeds it would need are not reachable from it. Every claim above about ordering is a claim about a model
  and about arithmetic, not about `MyRestaurant.Domain.dll`. Per §18 an uncompiled archive is a prediction.
- **The five new assertions have never executed.** In particular `Create_MintsDistinctSortKeysUnderConcurrency`
  uses `Parallel.For`, and a compare-and-swap loop is exactly the kind of code whose first real test is the
  first real run.
- **No integration test was run**, so it is not observed that `MenuEventLogTests.ListRecent_…` now passes
  deterministically. It should — that is the whole point — but the evidence for it is the model, and the
  original failure was 50% so a single green run would not be evidence either. **The honest check is to run
  the DataAccess suite several times.**
- **Whether any test elsewhere depended on the old randomness** is reasoned about rather than observed. The
  reasoning: the fix makes an order deterministic that was previously arbitrary, and an assertion that passed
  reliably against an arbitrary order cannot have been asserting that order. Assertions about version,
  uniqueness and distinctness are unaffected by construction. But it is reasoning.
- **The guest-basket consequence is not covered by a test in this slice.** `OrderProjection`'s line ordering
  is now correct and nothing asserts that a multi-line send reads back in basket order. That is a real gap
  and it is named in *Still open* rather than quietly closed.
- **No end-to-end scenario was added or changed**, so no browser has observed any of this.

## Test count

Last predicted **1157**, by Slice 44, and **observed at 1157** — twice in one session, once with a failure
and once without.

Predicted here: **1162**. The arithmetic is one term. `UuidV7IdentifierFactoryTests` goes from 2 `[Fact]`
methods to 7, so `MyRestaurant.Domain.Tests` gains **5**. No other test file gains or loses a method:
`TestingSectionContractTests` changes one `const` and one doc paragraph, `MenuEventLog` changes comments only,
and §16.3 is untouched at **17** scenarios.

Per §18: if the run returns anything other than 1162, the first thing to check is the `[Fact]` count in
`UuidV7IdentifierFactoryTests`, because it is the only term in the sum and it is also the number §16.4 now
claims — so a miscount there fails twice, once as a total and once as
`TestingSectionContractTests`.

## Still open

**A multi-line send is not asserted to read back in basket order.** New, and the largest residual of this
slice. `OrderProjection` orders lines by their added-at instant and then by identifier, and the identifier is
now monotonic, so the property holds — but it holds because of a guarantee two projects away and nothing
states it where it is consumed. The right shape is a `MyRestaurant.Domain.Tests` fact over
`OrderProjection.Project` with several lines added in one event, asserting they come back in the order the
operations were listed in.

**Nothing prevents a stored-row identifier being minted outside the factory.** `Guid.NewGuid()` is correct
for security stamps, broadcast tokens and staging keys and appears in production code for all three, so the
rule is not "never call it" — it is "never call it for a value a row will be ordered by", which is a
distinction a tree gate could draw from the call site's context and this tree does not draw at all. F-95
reached the guest's basket through code that used the factory correctly; a future one could reach it through
code that does not.

**A section's own description under its heading on the guest menu.** Unchanged, and still the largest
remaining piece of Stage 3. It needs either a second read or a widened `MenuItemSummary`, and F-84 is the
reason widening that record is not free.

**The kitchen's "86" panel still groups by nothing.** Stage 3's last surface.

**Reordering a heading from the index.** Now specified rather than merely named — Stage 3a in
`MENU_AND_HANDHELD_PLAN.md` — and unblocked as of this slice.

**Reordering items within a heading.** The same design against `menu_item`, recorded now so the section verb
does not arrive looking complete.

**The handheld barrier visits neither section surface.** Scenario 16 walks ten surfaces; `ManageMenuSection`
is a detail surface with `.manage-inline-form` buttons and would move the control count. Carried.

**No gate can see a count written in a comment, or a sentence describing a computation beside it.** F-90's,
F-91's and F-94's shared residual, and F-95 is a sharper version of it: no gate can see a *claim* written in
a comment either, and this one was wrong for eight months in four files.

**Nothing reports which gates a failed build prevented from running.** F-82's residual, carried.

**Nothing treats a test that fails and then passes as evidence.** New, and deliberately not made executable.
A retry that goes green is indistinguishable from a fixed flake by any artefact this repository produces, and
F-95 arrived exactly that way. §18 already says a predicted count the run contradicts is chased; the sibling
habit — a run that contradicts *itself* is chased — is now written in `DOCUMENTATION_REVIEW.md` rather than
left as folklore.

**F-41 has no row in `DOCUMENTATION_REVIEW.md`.** Tenth slice carried.

**`.sitting-meta` is declared by two components and the two have drifted.** Deferred a twelfth time.

**A CI job that runs the canonical stack on the canonical engine.** Twenty-first consecutive slice.

**`run.sh --containers-only` prints two `Error:` lines about a container that does not exist yet, then starts
it successfully.** Carried.

# M6 Slice 46 — the dump that had become mostly history

## Read this first: Slice 45 was green, and it was green twice

`total: 1162, failed: 0` — predicted 1162, observed 1162, on both the plain `dotnet test` and the full
`scripts/ci_local.sh --with-all --with-e2e`. §16.3's seventeen scenarios passed on their own run as well.
And the honest check Slice 45 asked for was performed: `MenuEventLogTests` was run **five consecutive
times**, 9 of 9 every time. F-95 was a 50% property, so one green run would not have been evidence and
the delivery note said so; five is.

That closes Slice 45 with nothing outstanding, and it means **the resequencing verb is unblocked** — its
whole reason for waiting was that it writes several `reordered` events at one instant and therefore leans
entirely on the identifier tie-break F-95 fixed.

**This slice is not that verb.** It is the dump, for a reason given in full below, and Stage 3a is next.

## Why this slice is not the menu

The context dump had reached **6.08 MiB and 87% of the project capacity it is loaded into**, and the
composition is lopsided in a way that made the fix obvious once measured:

| Artefact | Size | Share of dump | Content a session reads |
| --- | --- | --- | --- |
| `docs/BUILD_PROGRESS.md` | 829 KiB | 13% | the last six slices; the rest is archaeology |
| per-file metadata blocks | 164 KiB | 2.6% | none of it, ever |
| `LICENSE` | 34 KiB | 0.5% | none — verbatim AGPL-3.0-only |
| everything else | 5.09 MiB | 84% | all of it |

**The argument for doing it before Stage 3a is scheduling, and it is the same argument Stage 1 made
about the handheld contract.** A dump at 87% is a dump that will refuse a session partway through the
next feature, and the cost of hitting that ceiling mid-slice is a slice delivered from a partial tree.
Fixing the container before putting more in it is cheaper than fixing it around the thing already inside.

**And it is deliberately alone in this slice**, on the ruling Slice 45 made and this slice is the second
application of: **one change, one green run, then the feature.** Slice 45 declined to ship the
resequencing verb beside the identifier fix, because the verb's ordering test would simultaneously have
been the first test of the fix and a red run could not have said which change caused it. The same logic
holds here from the other side — this slice moves 730 KiB of documentation that four gates read and
changes what a fifth script enumerates. If Stage 3a's new write rode along, a red run would have two
candidate causes and §18's habit of chasing a count deviation gets expensive with two.

## What is in this slice

**The log is split in two, and neither half is edited.** `docs/BUILD_PROGRESS.md` keeps M6 Slice 40
onward, 106 KiB; `docs/progress/BUILD_PROGRESS_THROUGH_M6_SLICE_39.md` takes M1 through M6 Slice 39, 749
KiB, byte-identical to what was above the `# M6 Slice 40` heading apart from the H1 line being replaced
by an orientation header. `export.sh` withholds `docs/progress/` from the dump by path.

**Every citation of the form *BUILD_PROGRESS M6 Slice N* is left exactly as it was**, in about a hundred
ledger rows across two documents. They cite the *log*, and the log still contains every slice; which of
two files holds slice N is a filing detail, stated once in both files' headers, rather than a fact worth
rewriting a hundred rows to record. This is F-47's habit — where a rule can be executed, a list should
not exist — applied to a rule a reader executes rather than a script.

**`export.sh` names three kinds of held-out path instead of one, because other gates care about the
difference.** `GENERATED_DIRECTORIES` is tool output, and `scripts/check_tree.sh` skips it because a
dump's own structure is the separator that script forbids. `ARCHIVED_DIRECTORIES` is authored history:
still hygiene-checked, still hand-edited, still a record file for the platform-state rule — withheld only
to keep the dump small. `ELIDED_FILES` is listed with its metadata and hash and no body.

**The per-file metadata block goes from twelve lines to three.** Relative path, size, SHA-256 — the three
a session actually uses, which is exactly why they survive: the path is where a change goes back to, and
the size and the hash are what make a reconstruction *checkable* rather than approximate. Gone: absolute
path, last modified, permissions, owner, inode, hard links, MIME type, last git commit, and the file name,
which was the tail of the relative path on the line above it. **Four of the nine described the authoring
machine rather than the repository**, so the dump is now both smaller and less about a computer nobody
asked about. The header's `Host`, `User` and `OS` lines go for the same reason.

**`LICENSE`'s body is elided and its hash is not.** That is the property that makes the elision safe
rather than merely cheap: a modified licence is still detectable from the dump alone. This was flagged as
a veto candidate in the session and cleared — **to revert it, delete `LICENSE` from `ELIDED_FILES` in
`export.sh`**, which is a one-word edit and needs nothing else.

**`ContextDumpExclusionContractTests`, four assertions, and it is the part of this slice that will
outlive it.** The dump reduction is a one-time edit; the *hazard* it creates is permanent, and three of
the four facts are about relationships between files that nothing was comparing.

## The gate, and the defect it caught before packaging

**Fact 2 is the load-bearing one: every withheld document must be linked by path from a document the dump
contains.** Without it, the archive is a file that exists, is in git, is read by nobody, and is invisible
to every future session — which is the state a document is in one careless slice before somebody
regenerates the log without it. History that leaves the dump leaves a pointer behind, or it is gone in
the only sense that matters.

**Facts 3 and 4 are one fact written twice, caught by writing it a third time.** `scripts/check_tree.sh`
carried a comment saying its `GENERATED_DIRECTORIES` was *"kept in step with export.sh's
EXCLUDED_DIRECTORY by hand"* — and after this slice that sentence is false in a way that matters: the
exporter now withholds two directories and only one of them is generated. Hygiene-exempting the archive
because it happens to be excluded from the dump would have stopped checking 749 KiB of tracked text for
the exact defect F-40 was about. So the gate asserts set **equality** against the generated list and
**non-membership** for the archived one.

**And gate 3 of `scripts/check_repository.sh` would have failed on this archive.** That gate forbids a
document from asserting platform state, exempting the files whose job is to quote such a claim —
`DOCUMENTATION_REVIEW.md`, `BUILD_PROGRESS.md`, and the script's own pattern list. The archive contains
F-42's quoted sentence, *"Issues are disabled. There is no bug…"*, at what is now line 6252 of
`docs/progress/`. Moving history out of an exempt file into a non-exempt one carries the exemption with
it or lands red. `docs/progress/*` joins `RECORD_FILES`, and fact 4 asserts that it is there rather than
leaving the next archive to rediscover it.

## What was verified

- **`export.sh` was run.** Not reasoned about — executed, against a `git init`ed copy of the
  reconstructed tree, exit 0, and the output read back. `Withheld paths : docs/llm/, docs/progress/`;
  `Elided files : LICENSE`; zero `FILE: docs/progress` banners in the dump; `LICENSE`'s block carrying
  its real size and hash above the elision line; a sample metadata block showing exactly three fields.
- **The reduction was measured on the produced file rather than predicted.** 6,377,323 bytes before,
  5,483,728 after — roughly **894 KiB, about 14%**, taking 87% of capacity to about **75%**. Stated as a
  round number deliberately: the dump contains this entry, which contains the measurement, so the
  artefact measures itself and a byte-exact saving would be false precision. The first run of the new
  exporter reported 5,436,795, before this slice's own documentation was written into the tree it dumps —
  which is the whole 47 KiB difference and worth knowing rather than quietly reconciling.
- **`bash -n` and `shellcheck --severity=warning` on all twelve scripts, clean**, which is the blocking
  severity CI uses. `--severity=style` is also clean on `export.sh`.
- **Two dead symbols found and removed rather than left.** `file_mime()` had exactly one caller — the
  metadata block — and `is_binary_file` calls `file` itself, so the helper became unreachable;
  `HOSTNAME_VALUE`, `USER_VALUE` and `OPERATING_SYSTEM_VALUE` likewise. An unused function that shellcheck
  does not complain about is still a thing the next reader has to decide about.
- **The split is byte-exact.** The archive is the original bytes above `# M6 Slice 40` with the H1
  replaced; the boundary blank line is consumed by the split rather than left as a trailing blank, which
  is the shape that would have failed the *exactly one final newline* half of tree hygiene — and did, on
  the first attempt, which is why the split asserts its own boundary line numbers.
- **Byte hygiene on both halves and every changed file:** LF only, exactly one final newline, no
  whitespace-only lines, no CR.
- **The platform-state scan was run over the archive before packaging**, against gate 3's own ten
  patterns, which is how the `RECORD_FILES` omission was found rather than shipped.
- **`TestingSectionContractTests` simulated** against the edited §16.4: every cited `*Tests.cs` resolves,
  the new paragraph states exactly one parseable count, and the census reaches 21 against a floor of 21.
- **`MarkdownTableContractTests` simulated** over every changed and new document, including the archive.
- **The reconstructed tree itself:** all 349 files from `dump.txt` SHA-256 verified against the
  exporter's own manifest, zero mismatches.

## What was NOT verified

- **Nothing was compiled and no test was run.** There is no .NET SDK here and the package feeds are
  unreachable. Per §18 an uncompiled archive is a prediction. `ContextDumpExclusionContractTests` has
  never executed — and it is a new file that parses shell arrays with regular expressions, which is
  precisely the kind of code whose first honest test is its first real run.
- **The four new assertions have not been proven sensitive on a pre-fix tree**, which this project
  normally requires before delivery. Two of them **were** proven against a real defect — the
  `RECORD_FILES` omission and the trailing-blank-line boundary were both found by running the check by
  hand against the unfixed state — but by hand, in this container, not by the assertion. Fact 2's
  sensitivity is untested against anything.
- **`shellcheck` here is `shellcheck-py`, not the distribution binary**, so a version difference could
  in principle report differently on your machine. The gate is advisory at `--severity=style` and
  blocking at `--severity=warning`; both were clean here.
- **No gate other than the exporter was executed.** `check_tree.sh`, `check_repository.sh` and
  `ci_local.sh` were reasoned about and hand-simulated against their own pattern lists, not run.
- **The dump this slice produces has never been consumed by a session.** The claim that three metadata
  fields are sufficient rests on nine fields never having been used, which is an observation about past
  sessions rather than a guarantee about the next one. If a future slice needs the last-commit line back,
  that is evidence and not a mistake.

## Test count

Last predicted **1162**, observed **1162**, twice, plus a five-times-repeated `MenuEventLogTests`.

Predicted here: **1166**. The arithmetic is one term. `ContextDumpExclusionContractTests` is new with four
`[Fact]` methods, so `MyRestaurant.WebApplication.Tests` gains **4**. No other test file gains or loses a
method: `TestingSectionContractTests` changes one `const` and one doc paragraph, and §16.3 is untouched at
**17** scenarios.

Per §18: if the run returns anything other than 1166, check the `[Fact]` count in
`ContextDumpExclusionContractTests` first, because it is the only term in the sum. If it returns 1166 and
a *documentation* gate is red, the cause is §16.4's census — the floor moves 20 → 21 in the same slice as
the paragraph that raises it, so an error there fails twice.

## Still open

**Stage 3a — `ResequenceMenuSectionsAsync` — is next, and it is fully specified.** Unblocked as of Slice
45, deferred by name here rather than dropped, which is the discipline that let six earlier verbs be
reported as discharged rather than noticed. The shape is in `MENU_AND_HANDHELD_PLAN.md`: whole-list
permutation rather than a pairwise swap, because equal positions are permitted and a swap would have to
decide something the model leaves ambiguous; refuse a non-permutation as a stale page; `FOR UPDATE` over
all rows ordered by identifier; one `reordered` event per section that actually moved. **It carries F-93's
obligation**: up and down are `<button>` elements and the 375px barrier's reach selector currently reads
`.menu-group-actions a`, so `.menu-group-actions button` joins the set in the same slice or the index is a
surface the barrier has stopped asserting anything about.

**Two §16.4 counts will move with it** — `MenuSectionAdministrationTests` from 20 and `MenuWiringTests`
from 19 — and `TestingSectionContractTests` compares both, so a stale one is loud rather than silent.

**A multi-line send is still not asserted to read back in basket order.** Slice 45's largest residual,
carried unchanged. `OrderProjection` orders lines by instant then identifier and the identifier is now
monotonic, so the property holds — because of a guarantee two projects away that nothing states where it
is consumed.

**Nothing prevents a stored-row identifier being minted outside the factory.** Carried.

**A section's own description under its heading on the guest menu.** The largest remaining piece of Stage
3. Needs a second read or a widened `MenuItemSummary`, and F-84 is why widening that record is not free.

**The kitchen's "86" panel still groups by nothing.** Stage 3's last surface.

**Reordering items within a heading.** The same design as Stage 3a against `menu_item`, a separate slice
because the two write to different event tables with different paired CHECKs.

**The handheld barrier visits neither section surface.** Carried.

**No gate can see a count written in a comment, or a sentence describing a computation beside it.**
F-90's, F-91's, F-94's and F-95's shared residual, carried.

**Nothing reports which gates a failed build prevented from running.** F-82's residual, carried.

**Nothing treats a test that fails and then passes as evidence.** Carried from Slice 45.

**F-41 has no row in `DOCUMENTATION_REVIEW.md`.** Eleventh slice carried.

**`.sitting-meta` is declared by two components and the two have drifted.** Deferred a thirteenth time.

**A CI job that runs the canonical stack on the canonical engine.** Twenty-second consecutive slice.

**`run.sh --containers-only` prints two `Error:` lines about a container that does not exist yet, then
starts it successfully.** Carried.

**Nothing decides when the next tranche of the log moves to the archive.** New, and deliberately left as
a judgement rather than made executable. A size threshold that moved history automatically would be a
script silently rewriting the record of what this project did, which is worse than a document somebody
has to notice is long.
