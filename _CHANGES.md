# M6 Slice 49 — the arithmetic a test got wrong about a tree that was right, and the sentence a guest could finally read

Extract at the repository root. Every path is repo-relative and every file is complete.

```
tar -xzf m6-slice-49-resequence-arithmetic-and-heading-descriptions.tar.gz
git status
```

**Files to DELETE: none.**

**`git add` is NOT required.** No file in this archive is new — every one of the sixteen replaces a tracked
file. `git status` should show sixteen modifications and no untracked paths; anything untracked means the
archive was extracted somewhere other than the repository root.

**No schema change, no migration, no new read, no new test class, no ADR edit, no `compose.yaml` edit, no
`.slnx` edit, no `REQUIREMENTS.md` edit, and no `export.sh` edit.** One CSS rule is added and it declares
nothing new.

---

## Read this first: your run was exact, and the one red assertion was the test's fault

`total: 1190, failed: 1, succeeded: 1189, skipped: 0` — and the prediction was **1190**. §18's arithmetic has
never landed cleaner: the count matched to the digit, so the single failure had nowhere to hide and no second
candidate to be confused with. The end-to-end suite passed all seventeen scenarios against real browsers,
twice, Debug and Release.

```
Assert.Equal() Failure: Values differ
Expected: 2
Actual:   3
  MenuItemResequenceTests.ResequencingOneHeadingLeavesTheOtherHeadingAlone
```

**Actual 3 is correct.** Nothing under `src/` was wrong. The fact arranges three drinks at 0, 1, 2 and
resequences them to `[cola, tea, coffee]` — a **rotation**, which moves all three, so §7's rule of one event
per item that actually moved writes three.

**The 2 came from forty lines up.** `OnlyTheItemsThatMovedGetAnEvent` uses `[cola, coffee, tea]`, a
**reversal**, which leaves coffee at 1 and writes two. The list came from a third fact that rotates on
purpose. So the fact was assembled out of two correct facts, took the arrangement from one and the count from
the other, and reported a defect that did not exist.

This is **F-99**, and the reason it earns a row rather than a one-token edit is the symptom shape: one named
assertion printing two numbers is *precisely* what a real off-by-one in that verb's `WHERE` clause would have
produced. Nothing in the failure told you which of the two it was. What decided it was reading the
arrangement — and the assertions that carry the fact's actual claim (`Assert.Empty` on trifle and sorbet,
`[0, 1]` on their stored positions) all passed, which means the verb, the `WHERE` clause, the permutation test
and Slice 45's monotonicity fix are vindicated by this run rather than implicated in it.

### The repair, and the decision inside it you may want to reverse

The count becomes **3**, and the arithmetic is written into the fact's own summary instead of being left for
the next reader to re-derive.

**Keeping the rotation is a ruling, and this is the veto.** The alternative repair — change the list to
`[cola, coffee, tea]` and keep the 2 — would let this one fact carry two properties at once: the puddings
wrote nothing *and* only movers wrote. I kept the rotation because this fact is about a write not reaching
past its heading, and the arrangement with the most chances to reach past it is the one that touches **every**
row under it; a reversal writes two of three and never touches the row at the end of the heading's run.

What that costs is recorded in the fact rather than discovered later: three moved of three listed means this
total cannot also witness the per-row no-op rule, so that rule stays where a reversal can see it, and the
summary points there.

**To reverse:** in `ResequencingOneHeadingLeavesTheOtherHeadingAlone`, change the list to
`[cola, coffee, tea]`, restore `Assert.Equal(2, …)`, and delete the two `<para>` blocks in the summary that
explain the 3. Nothing else depends on the choice.

**No new gate**, on F-47 and F-71. The suite named the fact, the file, the line and both numbers on the first
run after the fact landed. A gate over the arithmetic *inside* an assertion is the assertion.

---

## The menu progress: a heading's own description, on the guest's phone

**This closes the last outstanding piece of Stage 3's guest menu**, which the plan named and nine slices
carried unchanged in their *Still open* sections. §11.1 renders a heading's description beneath its name, and
a heading with none renders **no paragraph** rather than an empty one — `''` is what §7 stores for *none*, and
an empty box is indistinguishable from a surface that failed to load.

### The ruling reversal, and the argument for it

The plan deferred a choice: *"showing it needs either a second read or a widened record."* Four places in the
tree had already assumed the answer — §7, `MenuWorkflow`, `MenuWiringTests` and the section editor's lede all
said §11.1 renders a heading's name and not its description *because the grouping is built from
`MenuItemSummary`, which carries the one and not the other* — and `TableOrderSurface`'s render record went
further: *"A surface that needed the section's own description would read the directory rather than widen
this."*

**I widened the record, and the argument is correctness rather than cost.** Two reads happen at two instants.
A heading renamed between them renders its **new name above its old sentence**, and there is no lock a guest's
picker could sensibly take to prevent that — it is not in a transaction and should not be. One row of one
query cannot disagree with itself. `MenuSectionDescription` is one more aliased column on the INNER JOIN that
has carried `MenuSectionName` since `0005`, for the same reason that one was joined: a heading edited once
reads under its new text everywhere at once.

The cost is that a heading's sentence repeats on every item row under it. That is already true of the name, it
is what a denormalised read model is, and the walk takes the value from the first row of each run — so the
copies are read once each and never compared.

**To reverse:** drop the member from `MenuItemSummary`, `MenuItemRow`, the SQL alias list, `ToSummary`,
`MenuSectionOnTheMenu` and `OrderStagingTests.Item`, and read `IMenuSectionDirectory.ListAsync` beside the
menu instead. Note that `MenuDirectoryTests.List_CarriesEachHeadingsOwnDescription_OnEveryItemUnderIt` and
scenario 17's step (c2) both assert the widened shape.

### The publish needed no edit, which is Slice 40's ruling paying out

`DescribeMenuSectionAsync` has broadcast `MenuChanged` since the day it reached no guest surface at all, on
the ruling that `MenuChanged` means *re-read the menu* and nothing else. **This is the moment that ruling was
made for, and neither the workflow nor its wiring fact changed.** A tree that had made the publish conditional
would have shipped a menu showing the new sentence to whoever reloaded and the old one to every phone already
looking at it. Three prose sites and one lede are corrected instead — that is the whole of what the ruling
cost.

### Two smaller rulings on the surface

**No `aria-describedby` on the list.** It is the more precise ARIA and expressing *no description, no
attribute* needs an attribute whose value is null, which this tree has no precedent for anywhere — so the
honest alternative was rendering the `<ul>` twice. The paragraph sits between the heading and the list in
document order instead, which is where a screen reader meets it either way.

**No new CSS vocabulary.** `.order-menu-section-description` declares the same three properties
`.menu-group-description` already carries on the administration index, because it is the same sentence read by
a different person. No `text-transform`, on F-88 — the heading above it has one, and the new harness read uses
`TextContent` pre-emptively for the same reason.

### Scenario 17 needed no new arrangement

It has created *Starters* **with** a description and *Puddings* **without** one since Slice 40, and nothing
had ever asserted on either. Step (c2) names the literal and asserts both halves of the rule from what was
already there. It is now the only place `menu_section.description` is carried from the form that typed it to
the phone that reads it — which is what step (d) does one register down for the item's own description.

---

## Test count arithmetic

Uncompiled, per §18. **1190 → 1191.**

| Where | Assertions |
| --- | --- |
| `MenuDirectoryTests` | 1 |
| **Total added** | **1** |

The resequence repair changes an assertion inside an existing fact, so it moves no count. Scenario 17 is
extended rather than added, so §16.3 stays at seventeen, and no test class is added, so §16.4's counted-class
floor of twenty-four does not move. **Any deviation from 1191 is the first thing to investigate.**

---

## Files in this archive

| Path | What changed |
| --- | --- |
| `tests/MyRestaurant.DataAccess.Tests/Menu/MenuItemResequenceTests.cs` | **the fix** — the count, the comment, and the arithmetic in the summary |
| `src/MyRestaurant.DataAccess/Menu/MenuDirectory.cs` | `MenuSectionDescription` on the record, the row, the alias list and the projection; the reversal argued |
| `src/MyRestaurant.WebApplication/Components/Pages/Table/TableOrderSurface.razor` | the paragraph under the heading, the walk carrying it, the render record widened |
| `src/MyRestaurant.WebApplication/wwwroot/app.css` | `.order-menu-section-description` |
| `src/MyRestaurant.WebApplication/Menu/MenuWorkflow.cs` | prose only — the publish now reaches a guest surface |
| `src/MyRestaurant.WebApplication/Components/Pages/Administration/ManageMenuSection.razor` | prose only — the lede said guests do not read this |
| `tests/MyRestaurant.DataAccess.Tests/Menu/MenuDirectoryTests.cs` | one new fact: the sentence on every row of the run, and the empty case |
| `tests/MyRestaurant.WebApplication.Tests/Orders/OrderStagingTests.cs` | the one positional construction site, plus what it cost |
| `tests/MyRestaurant.WebApplication.Tests/Menu/MenuWiringTests.cs` | prose only — the bet the publish was making has paid |
| `tests/MyRestaurant.EndToEnd.Tests/Harness/TableOrderJourneys.cs` | `ReadMenuSectionDescriptionsAsync` |
| `tests/MyRestaurant.EndToEnd.Tests/EndToEndScenarios.cs` | scenario 17 step (c2), and the heading description named as a constant |
| `docs/TECHNICAL_SPECIFICATION.md` | v1.34; §7 and §11.1; §16.4's count 7 → 8 and F-99's ruling; Appendix A F-99 and the Stage 3 row; changelog |
| `docs/DOCUMENTATION_REVIEW.md` | F-99 row; status line |
| `docs/BUILD_PROGRESS.md` | the Slice 49 entry |
| `docs/MENU_AND_HANDHELD_PLAN.md` | the outstanding line struck with the route taken; Stage 3 closed |
| `_CHANGES.md` | this file |

---

## What to run, in this order

```
dotnet build
dotnet test
bash scripts/check_repository.sh --offline
bash scripts/ci_local.sh --with-all
```

`dotnet build` first this time, because the widened record is the one change that can fail at the door: a
positional constructor call I missed reports CS7036 naming the parameter, and there is exactly one such site
in the tree.

---

## What was NOT verified

**Nothing was compiled and nothing ran.** This archive is a prediction until `dotnet build` says otherwise,
which is the habit F-71 bought.

**No browser rendered the paragraph.** `ReadMenuSectionDescriptionsAsync` is new and scenario 17's step (c2)
is its only caller, so the likeliest red is a locator finding nothing —
`p.order-menu-section-description` inside `div.order-menu-section`. It fails as a length or value mismatch
naming `null` where the sentence should be, which is distinguishable from the names assertion above it.

**No database answered the new column.** `List_CarriesEachHeadingsOwnDescription_OnEveryItemUnderIt` is the
first read of `menu_section.description` through `IMenuDirectory`; a Dapper binding disagreement fails naming
the parameter.

**Whether the paragraph looks right under the uppercase heading at 375px.** Nothing here can render.

**What was done instead:** the tree was reconstructed from `dump.txt` and every one of its 353 SHA-256 hashes
matched, against a second copy fetched from the remote; the Razor edit was checked by diffing the tag-event
stream before and after, which showed exactly one balanced `<p>` between `</h4>` and `<ul>` and nothing else
moved; brace and paren balance is zero-delta on all eight edited C# files; the `TestingSectionContractTests`
gate was simulated and reports twenty-four counted classes against a floor of twenty-four with zero
disagreements; every construction site of both widened records was found by search rather than by memory; and
`TreatWarningsAsErrors` under CI is why step (c2) is two array assertions rather than one tuple comparison.

**One instrument was wrong and is worth recording.** The first Razor walker reported faults in *untouched*
control files, which is how it was known to be the broken party rather than the tree. A checker whose control
group fails is a checker, not a finding.
