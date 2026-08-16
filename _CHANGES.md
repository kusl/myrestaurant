# M6 Slice 40 — the heading every item has (`0005`), and a vocabulary nobody could check (F-80)

Extract at the repository root. Every path is repo-relative and every file is complete.

```
tar -xzf m6-slice-40-menu-sections.tar.gz
git status
```

**Files to DELETE: none.**

**`git add` IS required this time — three files are new and untracked.** `scripts/check_tree.sh` walks
`git ls-files`, so an unstaged file is a file gate 1 does not see:

```
git add src/MyRestaurant.DataAccess/Migrations/0005_menu_item_sections.sql
git add src/MyRestaurant.WebApplication/Components/Pages/Administration/CreateMenuSection.razor
git add tests/MyRestaurant.WebApplication.Tests/Events/MenuEventVocabularyContractTests.cs
```

**A schema change, and it is the expensive one this stage has been deferring since Slice 37.** `0005` adds
`menu_item.menu_section_identifier uuid NOT NULL REFERENCES menu_section`. It applies at startup like every
other migration; on a database that already has menu items it seeds one section called "Menu" and files
them under it. No new package, no `compose.yaml` edit, no `.slnx` edit.

---

## Read this first

**Slice 39 was green and its prediction held: 1124 tests, 0 failed.** That is the first time in three
slices that a predicted count met a run — Slice 38 predicted 1124 and never reached a count, and Slice 39
re-predicted it unchanged.

**The authoring environment has a database again.** Slice 39's "still open" named two consecutive slices
without one as the reason F-78 was found on your machine rather than in mine. PostgreSQL 16 was installed
before a line of `0005` was written, and every statement in this slice was **executed**: both branches of
the conditional seed, the `FOR UPDATE` with its `MAX + 1` subquery, the three-event create as a
transaction, and the new paired CHECK refusing a `created` event that carries a section.

---

## What this slice does

**Closes Stage 2 of `docs/MENU_AND_HANDHELD_PLAN.md`.** Every menu item is now under exactly one heading,
and §11.1's guest menu renders grouped under those headings — which is the half of the enhancement request
the person who made it can see.

- **`0005`** — the seed, the backfill, the `NOT NULL`, the named foreign key, `section_changed` with its
  payload column and a fifth paired CHECK, and one index on the referencing side of the foreign key.
- **A section create page** at `/administration/menu/sections/new`, and a **required picker** on the item
  form which renders a first-use panel instead of a form when there are no headings yet.
- **The guest menu grouped**, with §7's asymmetry implemented: an inactive *item* stays visible and marked,
  an inactive *section* is not rendered to the guest at all, and neither cascades to the other.
- **An item is appended** at `MAX + 1` within its section under a lock on the section row, reversing
  `0004`'s "created at position 0" now that "the end of the menu" is a defined place.
- **F-80** — the event explorer's menu vocabulary said "the five values" and had been wrong since `0004`,
  with no run-time symptom because §11.4's explorer refuses nothing. Corrected, and made **derivable** from
  the migration that declares it rather than maintained beside it.

## How a mandatory column avoided reaching sixteen files

`OrderTestWorld.AddMenuItemAsync` takes an **optional** section and lazily creates a house heading when none
is named, so a dozen integration test files compile unchanged. `AdministrationJourneys.CreateMenuItemAsync`
arranges its own heading before opening the form, so **the sixteen existing §16.3 scenarios needed no edit
at all**.

## Two decisions worth your attention

**`MoveMenuItemToSectionAsync` is deliberately absent.** The plan schedules it here; the item editor that
would call it is Stage 3, and this project's rule about verbs without callers applies to it exactly as it
applied to the section verbs. **To reverse:** it is an `UPDATE` and a `section_changed` beside
`ReorderMenuItemAsync`, plus a picker on `ManageMenuItem.razor`.

**Only one of the five section verbs moved behind `IMenuWorkflow`.** The obligation carried four times
narrows to four verbs rather than closing. `MenuWiringTests`' fake throws from the other four with a message
naming the obligation, so the next person to wire one is told.

## Test count

Predicted **1136**, up from 1124: contract test +2, directory +2, administration +3, migration gate +2,
wiring +2, scenario 17 +1. §16.3 goes from 16 to 17. **Uncompiled arithmetic** — the SQL in this slice ran,
the C# did not. If the run returns anything else, that difference is the next thing to chase (§18).
