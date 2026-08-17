# M6 Slice 44 — the index becomes the menu, and a barrier that measures by list

Extract at the repository root. Every path is repo-relative and every file is complete.

```
tar -xzf m6-slice-44-sections-first-index.tar.gz
git status
```

**Files to DELETE: none.**

**`git add` is NOT required.** Every file in this archive already exists in the tree and is tracked. No
file is new, no directory is new, and `scripts/check_tree.sh` walks `git ls-files`, so there is nothing
here it cannot already see.

**No schema change and no new read.** No migration is added and `0005` is untouched. Both directory verbs
the rewritten index calls — `IMenuSectionDirectory.ListAsync` and `IMenuDirectory.ListAsync` — have existed
since Slice 40, and one of them says in its own doc comment that listing the headings themselves, empty ones
included, is what it is for. No new package, no `compose.yaml` edit, no `.slnx` edit, no ADR edit, no
`REQUIREMENTS.md` edit.

## What changed

**`/administration/menu` is sections-first.** A group per heading — a `<details>` rendered open, its
summary carrying the name, §7's visibility chip, the item count and the position, its body carrying that
heading's items as §11.12's record list and the link into its editor. The flat list of every item with the
heading as a *column* is gone. That column was shipped deliberately in Slice 40 as an honest intermediate
and named as one at the time, because a sections-first index needs an editor to open into and a record list
whose rows link nowhere is a list of dead ends. The editor landed in Slice 41 and the refile verb in Slice
43. This is the destination, and nothing had to be undone to reach it.

**An empty heading is visible on this surface and nowhere else in the application.** The old list was built
from `menu_item`, so a heading with nothing under it appeared on no page at all — not on the guest's menu,
which §11.1 renders no empty heading to, and not on the index. A heading created with a typo, or one stocked
for next week and not yet filled, was a row no surface could show. That is **F-94** from the other side: the
page's closing sentence said *across N sections* and counted the headings that had items, under a comment
that described the discrepancy accurately rather than fixing it.

**The group is rendered open on every request, and that is a decision.** A heading a server collapsed is a
heading whose items nobody looking for an item can find; and §16.3 scenario 16 measures what a layout engine
laid out, so a control inside a closed `<details>` has no box and a collapsed group would silently withdraw
its own controls from the barrier that exists to catch exactly that.

**F-93 is the finding, and it is a gate rather than a page.** The 375px reachability barrier chooses what to
measure from a list of class names. Replacing this surface's `.record-actions` rows with `.menu-group` groups
would have left it *visited* and *unmeasured* — and the floor above the check would have gone **up**, because
the item rows inside the groups still carry `.record-actions`. A floor notices a selector group that
vanished and never one that was never counted, which is F-91's stated residual arriving as a live defect.
`.menu-group-summary` and `.menu-group-actions a` join the reach set, and the repair recorded in §16.4 is a
**rule**: a surface that acquires a new kind of control acquires a selector in the same slice, or it is a
surface the barrier has stopped asserting anything about.

## The cut, flagged for veto

**The index does not reorder a heading.** This plan promised it *"with the section's own order controls"* and
they are not here. `ReorderMenuSectionAsync` sets an **absolute** `display_order`, and §7 makes positions
deliberately non-unique with a name tie-break — so *move this heading above that one* is not expressible as
one absolute write: two headings sharing a position have an order nobody assigned, and no single number
distinguishes them. An honest up/down control needs a **resequencing verb** writing several rows and
therefore several `reordered` events in one transaction, which is a new write with new event semantics rather
than a surface change. The index makes the ordering legible instead and the editor keeps the write.

**To reverse this ruling**, the next slice adds `ResequenceMenuSectionsAsync` to `IMenuSectionAdministration`
and `IMenuWorkflow` — one transaction, a lock per affected row in identifier order, one `reordered` event per
row whose number actually moved — and the index grows a two-button form per group with a `@formname` derived
from the section identifier, which is the shape `ManageMenuSection`'s visibility toggle already uses and the
only shape that works for N forms without N `[SupplyParameterFromForm]` properties.

## Files in this archive

```
src/MyRestaurant.WebApplication/Components/Pages/Administration/AdministrationMenu.razor
src/MyRestaurant.WebApplication/wwwroot/app.css
tests/MyRestaurant.EndToEnd.Tests/EndToEndScenarios.cs
tests/MyRestaurant.EndToEnd.Tests/Harness/AdministrationJourneys.cs
tests/MyRestaurant.EndToEnd.Tests/Harness/HandheldReach.cs
tests/MyRestaurant.WebApplication.Tests/Components/HandheldLayoutContractTests.cs
docs/TECHNICAL_SPECIFICATION.md
docs/DOCUMENTATION_REVIEW.md
docs/MENU_AND_HANDHELD_PLAN.md
docs/BUILD_PROGRESS.md
_CHANGES.md
```

## Test count

Last predicted **1157**, by Slice 43, and not known to have run here. **This slice predicts 1157 as well,
and the number is unchanged for a reason rather than by coincidence:** no test class is added, no `[Fact]` or
`[Theory]` row is added, `HandheldLayoutContractTests` gains a list *entry* rather than an assertion, and
§16.3 scenario 17 is **extended** rather than added — so §16.3 stays at seventeen and the end-to-end project
stays at seventeen facts.

Per §18: if the run returns anything other than 1157, the difference belongs to Slice 43's arithmetic rather
than to this slice's, and that is where to look first.
