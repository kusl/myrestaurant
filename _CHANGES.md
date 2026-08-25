# M6 Slice 64 — the surface the contract was written for, measured last, and the control no gate could reach

**Apply this to a tree at Slice 63.** It edits nine files and adds none. Extracting it over an older tree
will leave a specification two versions behind its own changelog and a menu plan whose Stage 1 status
paragraph describes work that is not there.

Extract at the repository root. Every file in the archive is a **complete file**; nothing is a patch, and
there are no scripts to run.

```
tar -xzf m6-slice-64-the-guest-surface-at-375px.tar.gz
```

## Files to delete

**None.** Nothing is removed by this slice.

## New files — these must be `git add`ed

**None.** Every path in the archive is already tracked, so `git ls-files` sees all of it and no gate is
blind to anything here.

## What is in the archive

| Path | Why |
| --- | --- |
| `src/MyRestaurant.WebApplication/wwwroot/app.css` | **F-118** — `.order-basket-quantity input` joins the `.form-field` selector list and the matching focus ring; the `max-width` rule gains the comment saying what is and is not true of exactly those two |
| `tests/MyRestaurant.EndToEnd.Tests/Harness/HandheldReach.cs` | rewritten — `HandheldSelector`, `HandheldSurface` with `Administration` and `GuestOrder`, `MeasureHereAsync`, the per-selector census, the font-floor verdict |
| `tests/MyRestaurant.EndToEnd.Tests/Harness/TableJourneys.cs` | `SeatGuestAsync` gains `handheld = false` — a default, so no existing caller moves |
| `tests/MyRestaurant.EndToEnd.Tests/MenuReactionScenarios.cs` | scenario 21's guest is seated at 375×667; steps (k), (l) and (m) added inside the existing `[Fact]` |
| `docs/TECHNICAL_SPECIFICATION.md` | **v1.49** — §11.12 (one paragraph added, the closing one rewritten), §16.3 scenario 21, §16.4 (three paragraphs), Appendix A (F-118, Stage 1d), changelog |
| `docs/DOCUMENTATION_REVIEW.md` | F-118's ledger row and two *Going forward* paragraphs |
| `docs/MENU_AND_HANDHELD_PLAN.md` | **Stage 1d** added and marked landed; Stage 1's status paragraph updated; the §11.1 gap struck through where it was carried |
| `docs/BUILD_PROGRESS.md` | the Slice 64 entry, appended |
| `_CHANGES.md` | this file |

**`tests/MyRestaurant.EndToEnd.Tests/EndToEndScenarios.cs` is deliberately NOT in the archive.**
`MeasureAsync(page, path)` keeps its signature and `MeasuredControl` gained fields rather than losing any,
so scenario 16's ten call sites compile and mean exactly what they meant. A file of three thousand lines
not touched is three thousand lines that cannot have been broken.

## The count

**1302, unchanged**, and for the first time in four slices the baseline is an observation rather than a
prediction — your run reported 1302 passed, 0 failed, 0 skipped, which confirms Slice 62 and Slice 63 at
once. Nothing here adds or removes a `[Fact]`: scenario 21 is extended *inside* its existing method.
`MinimumCountedClasses` stays **37** — no test class is added, and the §16.4 paragraphs cite
`HandheldReach.cs`, which is not a `*Tests.cs` and is therefore not a counted class.

**If the run returns anything other than 1302, that is the first thing to investigate**, because nothing in
this slice was supposed to move it.

## What this slice is

**Stage 1d.** §11.1 — the guest's own ordering surface — is laid out at 375×667 by a browser for the first
time. Stage 1 of the menu plan is *the handheld contract*, §11.12 is justified by R§1's sentence about the
phone in a **guest's** hand, and every slice of Stage 1 measured the surfaces **staff** use, because that
is where F-59 was found. Six stages of this plan then gave §11.1 headings, descriptions, a thumbnail, an
uncropped picture, a detail panel, a like and a second control beside a refused card, and none of it was
ever laid out at the width it is read at.

Scenario 21 is **extended rather than a scenario 22 added**, on the rule Slices 59, 60 and 61 each applied:
the arrangement already exists. It ends holding a menu with an available dish and a refused one, the way-in
control beside the refused card, and a panel open on it — everything the barrier wants but a staged line.

## The defect it found (F-118)

§11.1's basket renders its quantity box as a bare `<input>` inside a `<label class="order-basket-quantity">`.
`app.css` declares §11.12's nine control declarations against `.form-field` and `.manage-inline-form`, and
that label is neither — so **exactly one rule in the whole stylesheet matched it**, `max-width: 8rem`. The
control therefore had **neither half** of the control rule: no `--touch-target` floor and no 16px font
floor, rendering at a user-agent default of roughly 13px in roughly 21px of height.

On the **guest's own basket**, which is the surface R§1 names, so the font floor there is a behaviour
rather than a preference: iOS Safari zooms the viewport when a focused control's text is under 16px and
does not zoom back out.

**F-66's shape a second time**, and the two together are one sentence: *a control written outside the
arrangement that carries a rule is invisible to every gate that reads the rule.*
`HandheldLayoutContractTests` asserts the declaration exists and cannot know which element a page renders;
the 375px barrier could have seen it and was scoped to §11.4. The repair is a **selector**, never a copy of
the declarations.

## Decisions worth your veto

**1. Scenario 21 now has two subjects.** Likes, and a layout barrier. They are in one scenario because the
arrangement is shared, and the "one change, one green run" rule is satisfied by their failure modes being
distinguishable rather than by separation: an opinion that does not survive a reload is a fold reading the
wrong row, and a control under 44px is a stylesheet.

**To revert:** move steps (k), (l) and (m) and the `ScrollbarAllowancePixels` constant into a new
`MenuHandheldScenarios` class as scenario 22, and pass `handheld: false` at the `SeatGuestAsync` call.
Costs a second container, a second passkey registration and a second join per run.

**2. `.manage-inline-form` keeps its second copy of the control declarations.** Folding it into the shared
list is the obviously correct end state and it is four administration surfaces, in a slice about the
guest's menu. Carried as an open item in three documents rather than done quietly.

**To revert the deferral:** add `.manage-inline-form input, .manage-inline-form select` to the
`.form-field` list and delete the standalone block. Do it in a slice that can run scenario 16.

**3. The font floor is asserted on §11.1 and nowhere else.** Turning it on for §11.4's ten surfaces is the
obvious next move and it was declined on F-116's remedy — those pages *surely* comply, and *surely* is what
cost a session one slice ago. Every administration selector is `Optional`; every guest selector is
`Required`.

**To widen:** change the relevant `HandheldSelector.Optional(…)` calls in `HandheldSurface.Administration`
to `Required`, and add a `FontFloorSelectors` list to it. Do it from a green run, not from a paragraph.

## What was NOT verified

Nothing was compiled and nothing was run. The risks, in order of what they would cost:

**The barrier may report on arrival, and if it does the report is the finding.** §11.1 has never been laid
out at 375px by anything that would tell you. If a second layout defect is there, this scenario is what
says so — **do not delete the barrier**, send me the message. That is written here because deleting a gate
that reports on arrival is the tempting move and the wrong one, and it is the exact mistake F-116 is about
from the other side.

**The `width: 100%` the F-118 repair brings with it** is the only part of that fix that is not purely
additive. `.order-basket-quantity` is a flex container; a percentage width resolves against an indefinite
main size, `max-width: 8rem` caps it, and `.order-basket-controls` carries `flex-wrap: wrap`, so the worst
case is the Take-out button wrapping to its own line. `.order-picker-quantity input` has carried exactly
these declarations since the picker was written, which is evidence from the tree rather than from a
compile.

**The measurement script returns a new JSON shape** — `{selector, controls[]}` per group, where it
previously returned a flat array per group. A mis-shaped read surfaces in `ReadGroups` as a
`KeyNotFoundException` from `GetProperty`, not as a wrong number.

**Nothing proves the eight guest selectors match what scenario 21 arranges.** They were derived by reading
the markup — and the required-selector refusal is exactly the instrument that will say so if one is wrong,
by name and with the full census, rather than by a verdict quietly computed over a smaller page.
