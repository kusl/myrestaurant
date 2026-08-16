# M6 Slice 41 — the section editor, a reserved word (F-81), and the gate that never ran (F-82)

Extract at the repository root. Every path is repo-relative and every file is complete.

```
tar -xzf m6-slice-41-section-editor.tar.gz
git status
```

**Files to DELETE: none.**

**`git add` IS required this time — four files are new and untracked.** `scripts/check_tree.sh` walks
`git ls-files`, so an unstaged file is a file gate 1 does not see:

```
git add src/MyRestaurant.DataAccess/Menu/MenuSectionEventLog.cs
git add src/MyRestaurant.WebApplication/Components/Pages/Administration/ManageMenuSection.razor
git add tests/MyRestaurant.WebApplication.Tests/Components/RazorDirectiveContractTests.cs
git add tests/MyRestaurant.DataAccess.Tests/Menu/MenuSectionEventLogTests.cs
```

**No schema change.** No migration is added and `0005` is untouched. No new package, no `compose.yaml`
edit, no `.slnx` edit.

---

## Read this first

**Slice 40 did not build, so its predicted count never met a run.** The four `RZ` errors you reported are
one defect — F-81 — and they are all the same defect: two loop variables named `section`, which is MVC's
section directive and a reserved word in Razor's grammar.

**Fixing that exposed a second one.** With the build broken, `MyRestaurant.WebApplication.Tests` never ran,
and `TestingSectionContractTests` — the gate that compares §16.4's assertion counts to the files — was one
of the things that never ran. It would have failed. Slice 40 added assertions to four classes §16.4 cites
and moved none of the four numbers. That is **F-82**, and it is corrected here.

So this archive is red-to-green on two gates, not one, and the second was invisible behind the first.

---

## What this slice does

**Ships the section editor**, which was the highest-value thing outstanding in Stage 3 and which four other
deferred items were waiting on.

- **`/administration/menu/sections/{id}`** — four forms (rename, describe, move, show/hide), the heading's
  items, and its complete uncapped event history. Declares no CSS of its own.
- **`IMenuSectionEventLog`** — the per-heading history read. Nothing in this tree could read
  `menu_section_event` before this.
- **The last four section verbs behind `IMenuWorkflow`**, each publishing `MenuChanged` on a committed row
  and nothing on a write that committed nothing. The obligation counted down since `0003` is closed.
- **Links into the editor** from the create panel, the menu index's Section column, and each item's page.
- **§16.3 scenario 17** regains the two steps Slice 40 cut and recorded — and comes back larger.
- **F-81** made executable: `RazorDirectiveContractTests` refuses `@section` and `@RenderSection` across the
  component tree.
- **F-82**: four counts corrected, floor moved sixteen → eighteen.

---

## The four build errors, and why they read the way they did

```
CreateMenuItem.razor(125,40): RZ9979  code blocks delimited by '@{...}' … no longer supported
CreateMenuItem.razor(125,41): RZ2005  the 'section' directive must appear at the start of the line
CreateMenuItem.razor(125,48): RZ1011  the 'section' directives value(s) must be separated by whitespace
TableOrderSurface.razor(223,41): RZ1011  same
```

Line 125 column 40 is the `@`. Columns 41–47 are the seven characters of `section`. **Column 48 is the
`.`** — which is the only thing in four messages that points at the cause.

The reason it is invisible in review is on the neighbouring lines:

```razor
<div class="order-menu-section" @key="section.MenuSectionIdentifier">   ← compiles
    <h4 id="@SectionHeadingId(section)">                                ← compiles
        @section.MenuSectionName                                        ← four errors
```

Neither of the first two puts the word directly after an `@`, so the errors read as complaints about the
`<option>` and the `<h4>`.

Both variables are now `menuSection`. `TableOrderSurface.razor`'s inner card loop is also re-indented — it
had kept its pre-Slice-40 indentation when the grouping loop was wrapped around it, and whitespace is what a
reader uses to see which loop a `@key` belongs to.

---

## Three decisions flagged for veto

**1. The editor reads the whole menu and filters in memory.** `ManageMenuSection` calls
`IMenuDirectory.ListAsync` and filters for its own items rather than adding a per-section query with one
caller. The directory already orders by section first and makes each heading's items contiguous, so the
filter preserves the order guests see without re-deciding it in a second file. It is a read that grows with
the menu, on a database whose whole reason for existing is one restaurant. **To reverse:** add
`ListForSectionAsync` to `IMenuDirectory` and change one call site in `OnInitializedAsync`.

**2. No cross-section activity feed.** `IMenuSectionEventLog` has one method. The item log's
`ListRecentAsync` exists to fill a panel on `/administration/menu`; sections have no such panel, and a read
with no caller is the same defect as a workflow verb with no caller — which is the rule this slice spent
four verbs discharging. **To reverse:** one method and one panel.

**3. *Hide from guests* is a `link-button danger`.** Same weight as *Deactivate table*. It is fully
reversible and does not touch the items, so it may be one notch too heavy. **To reverse:** change one class
to `button-secondary`.

---

## What was verified, and one thing that failed its own proof

Full detail is in `docs/BUILD_PROGRESS.md` under the Slice 41 heading. The short version:

- **344 of 344 files** in `dump.txt` matched their recorded SHA-256.
- **`RazorDirectiveContractTests` run in substance**: 51 components, zero uses, five sensitivity cases
  behaving as the second fact asserts.
- **`TestingSectionContractTests` run in substance** before and after: 16 counted with **4 disagreements**
  before — that is F-82 — and 18 counted with 0 disagreements after.
- **`MarkdownTableContractTests`**: 60 table runs, zero findings.
- **`SpecificationVersionTests`**: header 1.26, newest entry 1.26, 27 entries descending.
- **Data-label parity** across all 8 record-list components.
- **Brace balance** on all nine changed C# files.

**The brace checker failed its own proof first, and that is worth one paragraph.** Its first run reported an
imbalance in the new `MenuSectionEventLog.cs`. Running it against two *untouched* sibling files —
`MenuEventLog.cs` and `MenuSectionDirectory.cs`, both byte-verified against your dump — produced the
identical report, which is what identified the checker rather than the file: it read `$"""` as an empty
interpolated string and parsed the SQL body as code. Fixed, re-run, all nine balanced. A verification tool
that has not been run against a known-good input has no established false-positive rate.

---

## What was NOT verified

**Nothing compiled** — no .NET SDK here. **No test ran.** **No browser rendered the editor.** The likeliest
sites of a complaint are named in `BUILD_PROGRESS.md` rather than left to be found.

The one to watch in the harness: the new `SetMenuSectionVisibilityAsync` journey waits on
`.manage-facts .chip`, which is the first harness read in this project keyed on a chip inside the facts grid
rather than on a flash or a heading. If scenario 17 times out at step (g), that is the first thing to check.

---

## Test count

Last **observed**: **1124**, from Slice 39. Slice 40 predicted 1136 and never met a run, because the build
failed — which is precisely where F-82 was sitting.

Predicted here: **1149**. From 1136: `RazorDirectiveContractTests` +2, `MenuSectionEventLogTests` +6,
`MenuWiringTests` +5. §16.3 stays at 17 — scenario 17 gains assertions, not facts.

Per §18: if the run returns anything other than 1149, chase the difference before anything else. **This is
the first opportunity to perform that check since Slice 39**, and the last two slices are why it matters.
