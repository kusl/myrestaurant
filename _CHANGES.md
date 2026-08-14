# M6 Slice 39 — the migration that could not run (F-78), and the dropdown that was the request

Extract at the repository root. Every path is repo-relative and every file is complete.

```
tar -xzf m6-slice-39-migration-fix-and-guest-menu.tar.gz
git status
```

**Files to DELETE: none.**

**No `git add` is required.** Every file in this archive already exists and is tracked, so
`scripts/check_tree.sh` — which walks `git ls-files` — sees all ten without any staging.

**No new directory, no new package, no schema change.** `0004`'s text moves; its effect does not.

---

## Read this first

Slice 38 was **red**, and the count it predicted was never reached. A migration is the first thing every
data-access fixture applies and the first thing the end-to-end harness applies, so one unparseable script
took the integration suite and all sixteen §16.3 scenarios down together:

```
System.InvalidOperationException : Variable migrate_menu_item_event_checks has no value defined
   at MyRestaurant.DataAccess.SchemaMigrationRunner.Run() … line 61
MyRestaurant.DataAccess.SchemaMigrationException : Database migration failed on script
   'MyRestaurant.DataAccess.Migrations.0004_menu_item_descriptions.sql'.
```

**F-78.** `0004`'s constraint sweep is a dollar-quoted `DO` block tagged
`$migrate_menu_item_event_checks$`. dbup-**core** runs `VariableSubstitutionPreprocessor` *before* the
PostgreSQL splitter, and it reads `$name$` as a variable reference — the same four characters around the
same kind of identifier PostgreSQL spells a dollar-quoted body with. The comment Slice 38 wrote above that
block, asserting it was safe because `dbup-postgresql`'s splitter consumes tagged blocks, is **true and
about the wrong component**: the splitter was never reached. That is F-62's shape at a dependency boundary.

**The fix is one builder call:** `SchemaMigrationRunner` adds `.WithVariablesDisabled()`, read from
dbup-core's public API at tag `6.1.1` rather than recalled. Nothing in this tree has ever used a DbUp
variable.

**`0004` keeps its TAGGED body rather than becoming `$$`, and that is the ruling.** An empty tag would also
survive substitution, so writing both fixes would leave the tree green with the rule deleted — F-64, F-69
and F-75's mechanism arriving as a second belt that hides the first. A tagged body makes the builder call
load-bearing: remove it and `SchemaMigrationRunnerTests` fails immediately. **So the row names something
executable by adding nothing** (F-47).

**`0004` is edited in place**, which F-34 forbids for an applied script — allowed here for the one reason
that makes the rule inapplicable: it had never applied anywhere, DbUp journals only on success, so there is
no journal row to be stale.

**If `.WithVariablesDisabled()` does not resolve** against the pinned `dbup-postgresql` 7.0.1 (unlikely — it
has been public API for years), the one-line alternative is to change both `$migrate_menu_item_event_checks$`
to `$$` and drop the builder call. Recorded rather than shipped, for the reason above.

---

## The picker is no longer a dropdown

> the menu choice should no longer be a dropdown … you should be able to select each menu item and see a lot
> more information about that item if such information exists

A closed `<select>` shows exactly one option, so comparing two items means opening a modal list twice; and an
`<option>` renders text and nothing else, so `menu_item.description` — the column `0004` exists to deliver —
had nowhere to go. It is now **a card per item** (name, price, description, an availability chip, a
`disabled` control where §7 says so) and **choosing a card opens a detail panel** naming what is recorded.

**Where nothing is recorded the panel says so in a sentence**, because a blank panel is indistinguishable
from a surface that failed to load. The facts are a `<dl>` of terms, so ratings and images add rows rather
than rewriting markup.

**Stage 3 ran before `0005`, which is a decision to veto if you disagree.** The plan put the guest menu after
sections; the two halves turned out to be separable — a card per item needs the description column and
nothing else, and grouping cards under headings is an outer loop added later. **To revert:**
`TableOrderSurface.razor` and the `.order-menu*` block in `app.css` go back, and `StageAsync` returns to
`SelectOptionAsync`. The F-78 fix is independent of it.

**F-79.** §7 carried one paragraph twice, consecutively, from Slice 38. Deleted. No gate is added: the three
gates over that document read table shape, header-versus-changelog and §16.4's counts, and none reads a
sentence — and a no-verbatim-repeats gate would fail on a tree that restates rulings across documents on
purpose (F-41).

---

## The ten files

| File | Change |
|---|---|
| `src/MyRestaurant.DataAccess/SchemaMigrationRunner.cs` | `.WithVariablesDisabled()`, documented at the call site |
| `src/MyRestaurant.DataAccess/Migrations/0004_menu_item_descriptions.sql` | safety claim corrected to name both components; tag kept; in-place edit justified |
| `src/MyRestaurant.WebApplication/Components/Pages/Table/TableOrderSurface.razor` | card list, detail panel, `ChooseItem`, `PickedMenuItem` |
| `src/MyRestaurant.WebApplication/wwwroot/app.css` | `.order-menu*`, two `auto-fit` grids, no new breakpoint |
| `tests/MyRestaurant.EndToEnd.Tests/Harness/TableOrderJourneys.cs` | `ChooseAsync`, `ReadMenuAsync`, `ReadChosenItemDetailAsync`, `WaitForMenuAsync`, two records, two helpers |
| `tests/MyRestaurant.EndToEnd.Tests/Harness/AdministrationJourneys.cs` | optional description on `CreateMenuItemAsync` |
| `tests/MyRestaurant.DataAccess.Tests/SchemaMigrationRunnerTests.cs` | summary records that it is the gate on the builder call |
| `docs/TECHNICAL_SPECIFICATION.md` | v1.24 — §7, §11.1, §16.4, Appendix A, changelog |
| `docs/DOCUMENTATION_REVIEW.md` | F-78, F-79 |
| `docs/MENU_AND_HANDHELD_PLAN.md` | Stage 3 partially struck |
| `docs/BUILD_PROGRESS.md` | Slice 39 |

**No assertion is added or removed anywhere**, so the predicted count stays **1124** and §16.3 stays at
**16**. That number has never been observed — Slice 38 failed before reaching a count — so this run is the
first honest test of its arithmetic.

## What was not verified

**Nothing compiled**; likeliest complaints named in `BUILD_PROGRESS.md`. **No database ran the migration** —
a failure still saying `Variable … has no value defined` means the builder call did not take. **No browser
rendered the picker**, which is the largest visual change since Slice 30.
