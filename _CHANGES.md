# Slice 72 — Stage 6e: the reader the whole feature was built for

Extract at the repository root. Spec goes to **v1.57**. Findings floor stays **F-127** — nothing new was found.

## Files in this archive

| Path | State |
|---|---|
| `src/MyRestaurant.WebApplication/Components/Pages/Administration/AdministrationMenu.razor` | changed — one injected read, the comment block, the count chip, four helpers |
| `tests/MyRestaurant.WebApplication.Tests/Menu/MenuItemCommentStaffReadContractTests.cs` | **NEW — needs `git add`** |
| `tests/MyRestaurant.EndToEnd.Tests/Harness/AdministrationJourneys.cs` | changed — two readers and one shared opener |
| `tests/MyRestaurant.EndToEnd.Tests/MenuReactionScenarios.cs` | changed — six staff claims in scenario 21; the test method renamed |
| `docs/TECHNICAL_SPECIFICATION.md` | changed — §7, §11.4, §16.3 scenario 21, §16.4, Appendix A, changelog, header |
| `docs/MENU_AND_HANDHELD_PLAN.md` | changed — 6e and Stage 6 close; three rulings recorded; *what is left* rewritten |
| `docs/BUILD_PROGRESS.md` | changed — the Slice 72 row and narrative |
| `_CHANGES.md` | changed (this file) |

**Nothing is deleted in this slice.** One file is added, so `git add tests/MyRestaurant.WebApplication.Tests/Menu/MenuItemCommentStaffReadContractTests.cs` — no new directory, and nothing needs `git rm`. `check_tree.sh` reads `git ls-files`, so an unstaged new file is invisible to tree hygiene.

**`app.css` is deliberately untouched.** The block is built entirely from the `record-list` vocabulary §11.12 already declares, so no new selector, no new custom property, and nothing for the handheld contract to decide. The two gates that would have moved — `HandheldLayoutContractTests` and the §16.3 barrier's selector sets — have no new subject.

## Expected test count

**1332.** 1327 observed + five `[Fact]` in the new contract class. Nothing was deleted and no existing count moved: scenario 21 is one `[Fact]` before and after and its method is renamed rather than split, and the two harness readers carry no attributes. §16.4's counted-class census goes 48 → 49 over a floor of 37, emulated at 49 with no paragraph disagreeing with its file.

Any other number is worth investigating before reading assertion text.

## What landed

Stage 6e, the last buildable row of the menu plan. `/administration/menu` gains every standing comment — grouped by dish, in the menu's own order, newest first within a dish — and a count chip beside the like's on each item row. Over the read Slice 68 built. No schema change, no migration, no service, no endpoint, no rate-limit policy.

Four rulings, in §7. **The staff read is the whole-menu read**, because `ListForPersonAsync` answers for one person and the administrator authored nothing here — narrowing it to the signed-in person renders an empty page to the only people §7 permits to read these, and does it silently. **A dish's own page carries no list of its own**, because a second read narrowed to one item is a second query over the same rows for one surface. **The block is grouped by dish in the menu's own order**: the read's order is total, as §7 requires, and is a UUID ordering when shown to a person, so the surface projects the read through the item list it already holds and sorts nothing itself. **The count chip is absent rather than zero**, which is the like count's ruling and its reason — a column exists on every row, and below the breakpoint a column is a labelled line on every card whether or not there is anything to say.

`IMenuItemCommentDirectory.ListAsync` had been a read with no caller since Slice 68, which §7 calls the same defect as a workflow verb with no caller. It was a **named deferral** — the plan's stage table has carried *6e, open* through four slices — so it is discharged rather than written up, and the plan's *what is left* paragraph is rewritten rather than annotated (F-122).

## Veto points

1. **The block is on the index rather than on each dish's page.** A staff member looking at one dish has to find its row in a block ordered by the menu. To reverse: add `ListForItemAsync` to `IMenuItemCommentDirectory` with its own integration facts, render it on `ManageMenuItem.razor`, and drop the block — which buys a second query over the same rows and a second surface to keep in step, and is why the ruling went the other way.
2. **`data-comment-body` is an empty marker attribute.** The barrier finds the sentence by it rather than by the column heading above it (F-113). To reverse: give the cell a class and select on that — which puts a name in `app.css`'s vocabulary that no rule reads, and F-67's register is the reason that is worse than a data attribute.
3. **Scenario 21 rather than a scenario 22.** Seventh application of *the arrangement already exists*: a guest with a standing comment and an administrator already signed in is exactly what a staff read needs. To reverse: lift the six claims into a new class and renumber; §16.3's *all twenty-one scenarios* in §16.4 moves with it.
4. **The fifth fact forbids every sort verb in the file, not just one over comments.** A future legitimate sort on this page fails it. That is intended — the failure is the conversation about which order is authoritative — but it is a gate that will be argued with, so it is flagged rather than buried.

## Sensitivity — emulated against the edited tree and seven planted defects, not executed

The delivered tree passes all five facts. `ListAsync` swapped for `ListForPersonAsync`: reported by the first fact and by the second, which loses its only reader — and the same prohibition written as a bare `ListAsync(` marker was confirmed to fire on `MenuSections.ListAsync(` and `MenuItems.ListAsync(`, which is why the receiver is in the marker. The guest's surface switched from its own read to everybody's: reported by the second, naming the path. The chip moved into a column of its own: reported by the third. `CommentCount` made to return `int` with a zero default: reported by the third. `RestaurantClock.DateAndTime` replaced by `OccurredAt.ToString("g")`: reported by the fourth, whose two assertions catch it independently. An `OrderByDescending` inside the projection: reported by the fifth. The whole block deleted, which is the pre-slice tree: reported by **all five**.

Also emulated green: §16.4's counted-assertion gate (49 paragraphs, none disagreeing, floor 37), `SpecificationVersionTests` (1.57 header, 1.57 newest, descending), and the Markdown table shape of every row added to the three documents.

## What was NOT verified

Nothing was compiled: no .NET SDK here, so 1332 is arithmetic and the five new assertions have never executed as C#. No browser: scenario 21's six new claims are unverified and the two new harness readers have never matched an element — in particular `td[data-comment-body]` rests on Blazor rendering an empty-valued attribute as a present one, which is read rather than observed, as is `@key` on a string over a static-SSR render. No container engine: nothing exercised `ListAsync` against a real database in this slice, so *a withdrawn comment leaves the block* is the fold's claim, held by `MenuItemCommentTests` at the data-access level and by nothing here.
