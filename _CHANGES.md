# M6 Slice 50 — the last surface that read the menu flat, and the rule that had nowhere to be tested

Extract at the repository root. Every path is repo-relative and every file is complete.

```
tar -xzf m6-slice-50-menu-grouping-and-the-86-panel.tar.gz
git status
```

**Files to DELETE: none.**

**`git add` IS required — two new files, one of them in a new directory.**

```
git add src/MyRestaurant.WebApplication/Menu/MenuGrouping.cs
git add tests/MyRestaurant.WebApplication.Tests/Menu/MenuGroupingTests.cs
```

`tests/MyRestaurant.WebApplication.Tests/Menu/` already exists (it holds `MenuWiringTests.cs`).
`src/MyRestaurant.WebApplication/Menu/` also already exists (it holds `MenuWorkflow.cs`). So neither
directory is new to git — but both files are, and an untracked file is invisible to `git ls-files` and
therefore to every gate and to CI. `git status` should show **eight modifications and two untracked
paths**; anything else untracked means the archive was extracted somewhere other than the repository root.

**No schema change, no migration, no new read, no new event type, no workflow verb, no ADR edit, no
`compose.yaml` edit, no `.slnx` edit, no `REQUIREMENTS.md` edit, no `export.sh` edit, and no `app.css`
edit.** The two new CSS rules are component-local.

---

## Read this first: your last run was clean, and this slice is one change

`total: 1191, failed: 0, succeeded: 1191, skipped: 0` — against a prediction of **1191**. §18's arithmetic
matched to the digit for the second consecutive slice, with no red assertion beside it this time. Seventeen
scenarios passed against real browsers three times over, and `ci_local.sh --with-all --with-e2e` reported
every gate passing.

So there was nothing to chase, and this slice does not need v1.32's distinguishable-failure argument: the
extraction and the surface change **are the same edit**. The grouping the 86 panel needed could not be
reached from a second component until it left the first one.

---

## The finding: a rule with nowhere to be tested (F-100)

`GroupedMenu` was a **private property inside `TableOrderSurface.razor`**, and had been since Slice 40. What
it implements is not incidental — §11.1's grouping, plus the pair §7 restates every time it mentions either,
because both are easy to lose and they point opposite ways one sentence apart:

- an inactive **item** stays on the menu, marked, unorderable;
- an inactive **heading** is not rendered to the guest at all.

§16.1 records there is no bUnit here. So for ten slices the only thing asserting any of that was §16.3
scenario 17 — a browser, a database, two and a half minutes, and asserting it incidentally.

**The tree had already written the rule down and this was the one place not following it.** `KitchenQueue`'s
own summary: *a rule that can only be checked by rendering a Razor component is a rule nobody checks* — and
it names `OrderStaging` and `OrderNarrative` as the two others outside their components for that reason.
Three pure functions exist because of this argument. The fourth was inside a component.

### Why it is a defect and not a preference

**§11.2's "86" panel needs the same walk with the opposite rule about hidden headings, and a private
property cannot be called from a second component.** The only other route to grouping that panel was to
paste the walk into `KitchenBoard.razor` — two copies, two sets of §7's rules drifting independently, nothing
able to see it. That is **F-59's mechanism** exactly.

### The second, smaller half

The walk's summary said it read each heading's name and description **from the first row of each run**. It
assigned inside the loop body on every iteration, so it read the **last**.

Nothing could ever have failed on that: the INNER JOIN makes every row of a run carry byte-identical values.
Which is exactly why no test could falsify the claim — F-99's residual in a fourth form, a claim written
beside a computation.

F-77's habit deletes such a claim. **Here the cheaper direction was available, so it is made true instead**:
the assignment moves to where a run begins. One `if`, and the sentence is now the code.

---

## Two rulings you may want to reverse

**Two named doors rather than one boolean.** `VisibleToGuests` and `EveryHeading`. I chose this because a
boolean at a call site is a rule nobody reading the call site can see, and this is the pair §7 restates every
time. **To reverse:** collapse both into one method taking `bool includeHeadingsHiddenFromGuests`, and give
each of the two call sites an argument. `MenuGroupingTests` calls both doors in nine of eleven facts, so the
test file changes too.

**Hidden headings are listed on the 86 panel and marked.** This is the opposite of §11.1's rule for the same
flag, and I have written it into §11.2 as **required rather than permitted**. The argument is §7's own:
deactivating a heading does not deactivate its items, and this panel is the only surface that can read or
change an item's availability — so a hidden heading dropped here is a run of dishes the kitchen can neither
86 nor bring back until somebody switches the heading on. **To reverse:** call `VisibleToGuests` in
`KitchenBoard.razor` instead, delete the chip and its `@if`, delete §11.2's clause, and delete
`AHeadingHiddenFromGuestsIsAbsentForThemAndPresentForTheKitchen` and
`AMenuWithEveryHeadingSwitchedOffIsEmptyForGuestsAndWholeForTheKitchen` (predicted count drops to 1200).

**And one decision flagged rather than made silently.** ADR-0014's Context sentence calling the panel a flat
`<ul>` is **left as written**, on the precedent of the guest `<select>` named in the same clause and made
obsolete in Slice 39: an ADR's Context records the world that motivated the decision, not the world today.
Say so if you would rather it were updated, and it is a one-line edit.

---

## The menu progress: the 86 panel, grouped

**This was the last surface in the application that read the menu flat.** Three smaller rulings on it:

- **No per-heading toggle.** That decision is about what guests see and belongs to §11.4's section editor. On
  this screen it would sit beside the control that removes one dish while emptying a quarter of the menu.
- **No heading description.** The record carries it because §11.1 needs it; *"served until 11am"* is for a
  guest choosing, not a cook counting.
- **No `app.css` edit and no `.menu-group` name.** `.kitchen-` is deliberately absent from
  `SharedSelectorPrefixes`, so component-local is correct here; reaching for `.menu-group*` would be the F-66
  defect, where a page-local rule sharing a shared name wins from later in the document and the stylesheet
  loses in silence.

---

## Test count arithmetic

Uncompiled, per §18. **1191 → 1202.**

| Where | Assertions |
| --- | --- |
| `MenuGroupingTests` | 11 |
| **Total added** | **11** |

No test is removed and none moves file. §16.3 stays at seventeen — no scenario added or extended. §16.4 gains
one paragraph, so the counted-class census **and** its enforced floor both move 24 → 25. **Any deviation from
1202 is the first thing to investigate.**

---

## Files in this archive

| Path | What changed |
| --- | --- |
| `src/MyRestaurant.WebApplication/Menu/MenuGrouping.cs` | **NEW** — the walk, `MenuHeadingGroup`, and the two named doors |
| `tests/MyRestaurant.WebApplication.Tests/Menu/MenuGroupingTests.cs` | **NEW** — eleven facts, two of which nothing else here could hold |
| `src/MyRestaurant.WebApplication/Components/Pages/Kitchen/KitchenBoard.razor` | the 86 panel grouped and chipped; the id helper; two component-local CSS rules |
| `src/MyRestaurant.WebApplication/Components/Pages/Table/TableOrderSurface.razor` | the walk removed and called instead; the private record deleted; markup byte-identical |
| `tests/MyRestaurant.WebApplication.Tests/Documentation/TestingSectionContractTests.cs` | the census floor, 24 → 25 |
| `docs/TECHNICAL_SPECIFICATION.md` | v1.35; §11.2 normative on the panel; §16.4's new paragraph; Appendix A F-100; changelog |
| `docs/DOCUMENTATION_REVIEW.md` | F-100 row; status line |
| `docs/BUILD_PROGRESS.md` | the Slice 50 entry |
| `docs/MENU_AND_HANDHELD_PLAN.md` | Stage 3c added and closed; Stage 3's closure sentence corrected; the stale `MenuSectionOnTheMenu` citation annotated |
| `_CHANGES.md` | this file |

---

## What to run, in this order

```
git add src/MyRestaurant.WebApplication/Menu/MenuGrouping.cs
git add tests/MyRestaurant.WebApplication.Tests/Menu/MenuGroupingTests.cs
dotnet build
dotnet test
bash scripts/check_repository.sh --offline
bash scripts/ci_local.sh --with-all
```

`git add` **first**, because two of the ten files are new and every gate reads `git ls-files`. `dotnet build`
before `dotnet test` because the likeliest failure in this archive is at the door: a `MenuHeadingGroup`
construction site I missed reports CS7036 or CS1503 naming a parameter, and there are exactly two
construction sites, both inside `MenuGrouping.Walk`.

Then look at `/kitchen` on a phone. Nothing in this archive has been rendered.

---

## What was NOT verified

**Nothing was compiled and nothing ran.** This archive is a prediction until `dotnet build` says otherwise.

**No browser rendered the grouped panel, and there is no scenario that could.** `/kitchen` has no §16.3
scenario at all — the board is covered by unit tests over pure functions and integration tests over the
writes, and nothing drives the surface. So the likeliest red here is not a test: it is the panel looking
wrong. Three specific things to look at:

- **The uppercase heading.** `.kitchen-menu-group-name` declares `text-transform: uppercase` and
  `--ink-soft`, matching `.kitchen-menu-state` beside it rather than the guest menu's
  `.order-menu-section-name`. Reversible in one declaration.
- **The chip inside an `<h3>`.** `.chip-warn` is app.css's, consumed here as `.chip-ok` already is fifty
  lines up. The flex container should baseline it acceptably beside `letter-spacing`; no engine was asked.
- **Vertical rhythm at 375px.** The panel gained a heading per group inside a `<section>` whose `font-size`
  is 1.05rem by deliberate choice, on a screen read from a step back with both hands full.

**What was done instead:** the tree was reconstructed from `dump.txt` and 351 of 352 SHA-256 hashes matched
(the one exception is `LICENSE`, elided by design since Slice 46). The Razor edits were checked by diffing
the tag-event stream before and after — **with a second pristine extraction as a control**, and
`AdministrationMenu.razor` came back 0 changed and 0 faults, so the instrument was trusted only after it
proved itself. `KitchenBoard.razor` shows exactly one balanced `<div>` wrapping a balanced `<h3>` containing
a balanced `<span>`, between `</p>` and `<ul>`; **`TableOrderSurface.razor` is 247 → 247 markup events, zero
delta**, so the guest surface is unchanged by construction rather than by inspection. Brace, paren and
bracket balance is zero on both new C# files. Five gates were simulated: `TestingSectionContractTests` (25
counted classes against a floor of 25, zero disagreements, zero ambiguous, zero unresolvable citations),
`MarkdownTableContractTests` (33 runs, 0 problems), `HandheldLayoutContractTests` (14 style blocks, 0
shared-vocabulary re-declarations, 0 undeclared custom properties, 0 colour literals, still one breakpoint),
`RazorDirectiveContractTests` (51 components, 0 collisions), and the version and platform-state gates. Tree
hygiene was checked on every touched file.

**One thing worth knowing about the vocabulary check.** `.kitchen-menu-group` does not trip the
`.menu-group` prefix, because prefix matching is on the **start** of a simple selector — I verified that by
running the real extraction logic rather than by reasoning about it, since a false positive there would have
been a red gate on a correct tree.
