# M6 Slice 43 — the last verb gets its surface, and three numbers nothing could check

Extract at the repository root. Every path is repo-relative and every file is complete.

```
tar -xzf m6-slice-43-move-menu-item.tar.gz
git status
```

**Files to DELETE: none.**

**`git add` is NOT required.** Every file in this archive already exists in the tree and is tracked. No
file is new, no directory is new, and `scripts/check_tree.sh` walks `git ls-files`, so there is nothing
here it cannot already see.

**No schema change.** No migration is added and `0005` is untouched — the move writes `section_changed` and
`reordered`, two types `0005` already declared. No new package, no `compose.yaml` edit, no `.slnx` edit, no
ADR edit, no `REQUIREMENTS.md` edit, **and no CSS**: `.manage-inline-form` has styled `select` since
Slice 34, so the picker needed no rule of its own.

---

## Read this first: 1149 was predicted and 1151 ran

Your run came back green — `total: 1151, failed: 0, succeeded: 1151, skipped: 0` — against a prediction of
**1149**. Per §18 that difference is chased before anything else, and it resolves exactly.

The tree holds **1132** `[Fact]` plus `[InlineData]` cases. `SchemaMigrationRunnerTests` adds its 5 facts,
`KeyRelations`' **13** theory rows, and `KeyColumnsAddedByAlter`'s **6**. That is 1151. Substitute the
**four** §16.4 states and you get 1149 to the unit.

§16.4 says *"Four theory rows therefore name the columns that arrived by `ALTER`"*. `0005` added two more
columns to that `TheoryData` and the sentence did not move. That is **F-90**, and the part worth reading is
why the gate written for exactly this could not catch it: `TestingSectionContractTests` compares an
**assertion count per class**, which for that file is 7 methods and is still right. A theory-row count is a
different quantity in the same paragraph. A number went stale inside the one section written to stop
numbers going stale, in the one form its gate is structurally blind to.

**This is the first finding in the ledger that §18's habit produced rather than a reading.** That is what
F-70 established it for, and this is the first run in three slices where it could report anything.

---

## What ships

**`MoveMenuItemToSectionAsync`, and the picker on `ManageMenuItem` that calls it.** It was the last verb in
the whole menu enhancement written without a caller. With it, **no method behind `IMenuWorkflow` is
unreachable from a form** — the obligation that governed six verbs across seven slices is discharged rather
than narrowed.

**Three findings beside it**, and they are one defect three times: a number written in prose where no gate
reaches it. **F-90** above. **F-91**, scenario 16's expected-control census — itemised in a comment, wrong
since Slice 38, unreportable because the assertion above it is a floor and a floor that passes at fifteen
passes at seventeen. **F-92**, the specification's own opening sentence, citing `REQUIREMENTS.md` rev 5
since v1.15 moved it to rev 6.

All three are repaired the same way: **the number is deleted, not corrected** (F-77). In all three **no gate
is added** and the residual is stated, because a gate for one sentence leaves every other instance of the
class untouched (F-47).

---

## Four rulings, flagged for veto

**1. A move appends to the end of its new heading.** Carrying the item's old position across would drop the
dish into the middle of an ordering somebody chose for a different list, because a position is a position
*within* a heading. `MAX + 1` under a lock on the target section row is exactly what a create does, so the
two are now one rule. **To reverse:** keep `item.DisplayOrder` in `UpdateMenuSectionAndPositionSql` and
delete the conditional `reordered` write; `MovingAnItemToAnotherSectionAppendsItThereAndLogsBothEvents` and
scenario 17's step (i) are the two facts that would then need to change.

**2. A move writes two events when the position changed and one when it did not.** §8.2 binds
`new_display_order` to `reordered` alone, so the position cannot ride on `section_changed`. The condition
is the no-op rule applied to half of one verb.

**3. The floor in scenario 16 is not raised.** F-91's census was wrong and is deleted, but
`MinimumControlsMeasured` stays at **14**. Its value is a claim about controls rendered on ten surfaces
against rows one scenario arranges, which I cannot observe from here — and an unobservable raise is exactly
the edit that turns a green suite red for no gain. If you want it raised, the honest way is to read the
number off a real run first.

**4. The button reads "File here", not "Move".** The Position form's button already says *Move* and
Playwright's `has-text` is substring matching, so a second button containing that word would make every
harness locator ambiguous. The wording is chosen against a test constraint rather than for its own sake,
which looks arbitrary unless said out loud. **To reverse:** change the label in `ManageMenuItem.razor` and
the click in `AdministrationJourneys.MoveMenuItemToSectionAsync` together.

---

## Files in this archive

| Path | What changed |
|---|---|
| `src/MyRestaurant.DataAccess/Menu/MenuAdministration.cs` | `MoveMenuItemToSectionOutcome`, `MoveMenuItemToSectionAsync`, the section on the lock read, and the two-column UPDATE |
| `src/MyRestaurant.WebApplication/Menu/MenuWorkflow.cs` | the verb, publishing on `Moved` alone |
| `src/MyRestaurant.WebApplication/Components/Pages/Administration/ManageMenuItem.razor` | the section picker, its handler, two flash messages, and three stale comments |
| `tests/MyRestaurant.DataAccess.Tests/Menu/MenuAdministrationTests.cs` | 26 → **31** facts |
| `tests/MyRestaurant.WebApplication.Tests/Menu/MenuWiringTests.cs` | 18 → **19** facts, plus the fake's new verb |
| `tests/MyRestaurant.EndToEnd.Tests/Harness/AdministrationJourneys.cs` | `MoveMenuItemToSectionAsync` |
| `tests/MyRestaurant.EndToEnd.Tests/EndToEndScenarios.cs` | scenario 17 step (i); scenario 16's census replaced by its rule (F-91) |
| `docs/TECHNICAL_SPECIFICATION.md` | **v1.28** — §0, §7, §11.4, §16.4, Appendix A, changelog |
| `docs/DOCUMENTATION_REVIEW.md` | F-90, F-91, F-92 |
| `docs/MENU_AND_HANDHELD_PLAN.md` | the last deferred verb struck |
| `docs/BUILD_PROGRESS.md` | Slice 43, delivered whole |
| `_CHANGES.md` | this file |

---

## Test count

Predicted **1157** = 1151 observed + 5 + 1. Scenario 17 is **extended, not added**, so §16.3 stays at
**17**. Per §18, anything other than 1157 is the next thing to chase.

---

## What was NOT verified

**Nothing compiled and no test ran.** Named rather than left to be found: `MoveMenuItemToSectionAsync`
takes two `Guid`s in a row — item then section, the same order `CreateMenuItemAsync` uses — and they are
positionally interchangeable to the compiler. A transposition would compile and fail at run time as
`MenuItemNotFound`.

**No database saw the move.** The append reuses the query a create already uses and a fact already proves.
What no fact yet exercises is two events of different types in one transaction.

**No browser ran step (i).** Its arrival barrier is a CSS attribute selector requiring the rendered `href`
to match `ToString("D")` exactly. Razor renders a `Guid` in that format, but this environment cannot
observe the agreement.

The full account is in `docs/BUILD_PROGRESS.md`.
