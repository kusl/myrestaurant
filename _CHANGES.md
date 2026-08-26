# M6 Slice 65 — the picture the barrier had never seen, and the element that is present with no area at all

**Apply this to a tree at Slice 64.** It edits eight files and adds one. Extracting it over an older tree
will leave a specification two versions behind its own changelog and a menu plan whose Stage 1 status
paragraph describes work that is not there.

Extract at the repository root. Every file in the archive is a **complete file**; nothing is a patch, and
there are no scripts to run.

```
tar -xzf m6-slice-65-the-picture-on-a-card.tar.gz
```

## Files to delete

**None.** Nothing is removed by this slice.

## New files — this must be `git add`ed

```
git add tests/MyRestaurant.EndToEnd.Tests/Harness/MenuPictureJourneys.cs
```

**This matters more than it looks.** Every gate in this repository that walks the tree walks
`git ls-files`, so an untracked file is invisible to all of them — the byte-hygiene gate, the
documentation-comment gate, the raw-HTML scan and the §16.4 census included. A file that compiles and is
never added is a file no gate has an opinion about.

## What is in the archive

| Path | Why |
| --- | --- |
| `tests/MyRestaurant.EndToEnd.Tests/Harness/MenuPictureJourneys.cs` | **new** — the picture upload journey the menu plan named as this stage's blocker, the decode wait, and the six selectors that stop being spelled twice |
| `tests/MyRestaurant.EndToEnd.Tests/Harness/HandheldReach.cs` | a fourth selector group (`ReachOnlySelectors`), `MeasuredControl.Width`, the collapsed-box refusal, `AllSelectors` extracted, the script returns `width` and a fourth group |
| `tests/MyRestaurant.EndToEnd.Tests/MenuReactionScenarios.cs` | scenario 21 attaches a 400px photograph to one dish and waits for both pictures to decode — steps (n) and (o) inside the existing `[Fact]` |
| `tests/MyRestaurant.EndToEnd.Tests/MenuPictureScenarios.cs` | six selector constants now taken from the harness; `WaitForResizeReportAsync` calls the shared wait instead of carrying a copy of it |
| `docs/TECHNICAL_SPECIFICATION.md` | **v1.50** — §16.3 scenario 21, §16.4 (the fourth group and the refusal; the barrier paragraph says four groups), Appendix A (Stage 1e), changelog |
| `docs/MENU_AND_HANDHELD_PLAN.md` | **Stage 1e** added and marked landed; Stage 1's status paragraph updated; Stage 1d's carried §11.1-picture gap struck through and closed |
| `docs/DOCUMENTATION_REVIEW.md` | two *Going forward* rules — what a census of matches can and cannot answer, and where the line between a journey and a scenario falls |
| `docs/BUILD_PROGRESS.md` | the Slice 65 entry, appended |
| `_CHANGES.md` | this file |

**`src/` is deliberately untouched.** No stylesheet, no Razor component, no C# under `src/`. Nothing in
this slice repairs a defect, because nothing in this slice found one — it points an existing instrument at
an arrangement it had never seen, and adds the refusal that arrangement turns out to need.

**`tests/MyRestaurant.EndToEnd.Tests/EndToEndScenarios.cs` is deliberately NOT in the archive.**
`MeasureAsync(page, path)` keeps its signature, `MeasuredControl` gained a field rather than losing any,
`MeasuredCount` still means what it meant, and §11.4's surface declares an **empty** fourth group — so
scenario 16's ten reports are numerically identical and that file compiles and means exactly what it meant.

## The count

**1302, unchanged.** The baseline is Slice 64's measurement — your run reported 1302 passed, 0 failed, 0
skipped. Nothing here adds or removes a `[Fact]`: scenario 21 is extended *inside* its existing method, and
`MenuPictureJourneys.cs` is a harness file with no test method in it. §16.3 stays at **21** scenarios.
`MinimumCountedClasses` stays **37** — no test class is added, and the new §16.4 paragraph cites
`MenuPictureJourneys` and `HandheldReach.cs`, neither of which is a `*Tests.cs`.

**If the run returns anything other than 1302, that is the first thing to investigate**, because nothing in
this slice was supposed to move it.

## What this slice is

**Stage 1e**, and the stage before it named it in one line with the blocker stated correctly: *"nothing
measures §11.1 with a picture on a card … attaching a photograph inside scenario 21 means extracting the
upload journey into the harness."*

Slice 64 pointed the 375px barrier at the guest's ordering surface and reported on ten selectors correctly.
What it reported on was **the one-column card**, because that scenario creates two dishes and attaches a
picture to neither. A dish with a photograph is a different arrangement of the same markup —
`.order-menu-item.has-picture .order-menu-choice` is two columns, with a 4rem cropped square in the first
and the text in the second — and its open panel renders the same photograph again, uncropped, under
`max-width: 100%`. Stages 4c through 4f built all of it and nothing had ever laid any of it out narrow.

### The fixture is wider than the screen, and that is the design

`app.css`'s own comment beside `.order-menu-detail-picture` says what its one width declaration is for: an
`<img>` with no constraint renders at whatever a camera produced, so a photograph wider than the viewport
makes the *document* wider than the viewport. **That had been a prediction for eleven slices.** Neither
existing fixture turns it into a claim — a 12px picture renders 12px wide, so the rule could be deleted with
every assertion still green; a 640px picture is over §8.2's cap, so the browser's ladder decides the stored
dimensions and the step becomes a test of a downscaler.

**400 is the number, and both properties are load-bearing.** `PictureFixtures` writes
`edge × (1 + edge × 3)` bytes plus framing, so 400 is **480,503 bytes against a cap of 524,288** — about
43 KB of headroom. Under the cap the file is stored verbatim, so the panel renders a 400px photograph inside
a panel roughly 300px across and `max-width: 100%` is the only thing standing between that and a sideways
scroll. The cap is never written in the scenario; §8.2's constraint stays the only place that says how large
a picture may be.

**On one dish and not both.** A menu where every card is two columns is a menu where the one-column card is
untested, and both shapes stand on this surface at once in any real dining room. It goes on the dish that is
later 86'd, so the card with the picture is also the card with the unavailable mark and a sibling control
stacked beneath it — the busiest box model §11.1 can produce.

### The hole it found, which is the part worth reading twice

**An `<img>` whose bytes have not arrived has no intrinsic size.** Its box is `0×0`, which lies inside every
viewport there is — so a barrier measuring it reports the element reachable, having measured a placeholder.

**And Slice 64's refusal cannot see that.** That refusal fires when a required selector matches **nothing**,
which is what made every other verdict on that surface mean something. An undecoded image *matches*: it
counts in the census as a one, the refusal passes, and every verdict is computed over an element that is not
there yet. The one instrument built to notice a group that went quiet is blind to an element that is present
and empty.

So there are two mechanisms rather than one, deliberately. `WaitForDecodedAsync` waits until every match
reports a non-zero `naturalWidth`; the collapsed-box refusal is what says so if a later slice removes the
wait. **The general rule is in `DOCUMENTATION_REVIEW.md`**: a census of matches answers *did the arrangement
build* only for elements whose presence and whose extent are the same fact — true of a `<button>`, sized by
its own padding and text, false of anything sized by bytes that arrive later.

### A fourth selector group, because nobody presses a photograph

The reach group asserts two things at once: the box is inside the viewport, and it clears §11.12's 44px
touch-target floor. **The second is a claim about a thumb.** A 4rem thumbnail clears 44px incidentally, so
putting one in that group attaches a verdict that is accidentally true and that nobody means — a floor that
cannot fail, which is the mistake F-41 records. `ReachOnlySelectors` is the mirror of the height-only group
the barrier has carried since Slice 33: reach without height, beside height without reach.

## Veto points

**1. `MenuPictureScenarios.cs` was edited and it is currently green.** This is the one change in the slice
made to something already working. Six `private const` declarations now initialise from `internal const`
fields in the same assembly — legal, and it cannot change behaviour — and `WaitForResizeReportAsync` lost its
inline script to `MenuPictureJourneys.SettleAsync`.

*Why it is here:* leaving copies behind would be two spellings of each selector, and
`AdministrationJourneys` already carries the sentence for why that is a defect rather than a style
preference — *"a second spelling of it would make one of them silently stop matching"*. An `id` renamed on
the form is not a compile error and not an exception; it is a locator that waits a minute and then reports
the wrong thing, once per file that spelled it.

*To revert:* restore the six literals as the values of those six `private const` fields, and restore the
five-line `WaitForFunctionAsync` body inside `WaitForResizeReportAsync`. Nothing else in this slice depends
on either. `MenuPictureJourneys` keeps its own constants and `SettleAsync` can go back to `private`.

**2. The collapsed-box refusal is scoped to the reach-only group.** It could have been applied to all four.

*Why it is not:* nothing in the other three groups can be present-and-empty — a `<button>` has a box the
moment it is in the document — so the check there would be a floor that cannot fail, and turning it on from
this argument rather than from a green run is F-116 with the name changed.

*To widen it:* change the filter in `MeasureHereAsync` from `reachOnly` to the concatenation, and expect it
to be a no-op. It should be widened from evidence, not from this paragraph.

**3. Scenario 21 is now a long scenario with three subjects.** A like that survives a reload, §11.12 at
375px, and a photograph on a card.

*Why it is here:* the arrangement already exists, which is the rule Slices 59, 60, 61 and 64 each applied.
A scenario 22 would buy a second container, a second passkey registration and a second join to arrange what
is already standing. The three fail in ways nothing could confuse — a fold reading the wrong row, a
stylesheet, and an upload — and each failure message names its own surface.

*If you would rather split it:* the natural cut is after step (j), with a scenario 22 taking the barrier and
the picture. It costs one container per run and buys nothing except a shorter method.

## What to expect on the first run

**The best outcome is green at 1302.** The second-best is green at 1302 with a red assertion inside scenario
21, because that means the barrier is reporting on a box model nothing had ever measured — and the message
will name the element, print the census, and say which of the four verdicts failed. **That is the finding,
and the next slice is the repair.** Deleting the group would be the tempting move and the wrong one.

The failure to be suspicious of is a **count other than 1302**. Nothing here was supposed to move it.
