# Slice 73 — Stage 1f: the station the contract was written about

Extract at the repository root. Spec goes to **v1.58**. Findings floor moves to **F-128**.

## Files in this archive

| Path | State |
|---|---|
| `tests/MyRestaurant.EndToEnd.Tests/Harness/HandheldReach.cs` | changed — two `HandheldSurface` records appended |
| `tests/MyRestaurant.EndToEnd.Tests/EndToEndScenarios.cs` | changed — scenario 10 renamed, its counter page opened handheld, two measurements, one shared verdict helper |
| `docs/TECHNICAL_SPECIFICATION.md` | changed — header, §11.12, §16.3 scenario 10, Appendix A, changelog |
| `docs/MENU_AND_HANDHELD_PLAN.md` | changed — Stages 1f and 1g, the 1b row, two rulings, *what is left* rewritten |
| `docs/DOCUMENTATION_REVIEW.md` | changed — header count, the standing-rules row, the F-128 ledger row, two residuals |
| `docs/BUILD_PROGRESS.md` | changed — the Slice 73 row and narrative |
| `_CHANGES.md` | changed (this file) |

**Files to DELETE: none.** Nothing is added and nothing is removed, so there is no `git add` and no `git rm` — every path above is already tracked. `check_tree.sh` reads `git ls-files`, so a plain `git status` after extracting should show six modified files and nothing untracked.

**No production code changed.** No `.razor`, no `.cs` under `src/`, no `app.css`, no migration, no service, no endpoint. The barrier is two records in the harness and one private helper in the scenario file.

## Expected test count

**1332 — unchanged.** Scenario 10 is one `[Fact]` before and after; its method is renamed rather than split, exactly as scenario 21's was in Slice 72. `AssertHandheldBarrier` is a private helper with no attribute, and the two `HandheldSurface` records carry none. §16.4's counted-class census stays at **49** over a floor of 37: `HandheldReach.cs` is harness, not a counted class, so no paragraph gained or lost a number.

Any other number is worth investigating before reading assertion text.

## What landed

**F-128.** `docs/MENU_AND_HANDHELD_PLAN.md` has said *what is left: nothing in this plan* since Slice 72 and, in the same file, *Stage 1 — open* and *Stage 1b — administration half landed* since Slice 34. The half that never landed is §11.2 and §11.3. §11.12 is titled *every surface, every screen*; R§1 justifies it with somebody standing up holding a phone; the kitchen and the counter are where somebody is standing up, and neither had ever been laid out at 375px by anything in this repository.

**Stage 1f, the counter.** §16.3 scenario 10's counter member now works a 375×667 handset from sign-in to settlement. Two measurements: the **board** while the sitting is still open, because `.counter-sitting-actions` is the way in to a bill and exists only while there is one; the **bill** where it stands, immediately before the close, because `_confirmingClose` swaps the settle panel's controls and the close then removes the per-line actions, the staff-add form and the close button together. The counter's page is the only one made handheld — the administrator's and the guest's are untouched, so nothing above or below the barrier moves.

**Stage 1g, the kitchen, is named as open** rather than left to be inferred. It has no arrangement to ride: `KitchenJourneys.OpenAsync` runs on the administrator's wide page in every scenario that opens a board, so measuring it needs a kitchen credential on a second browser context. That is arrangement rather than assertion, and it is a slice of its own.

**§11.12 writes the barrier's surface set down** — four surfaces named, §11.2 named as the one outside them — because a surface the barrier does not name is unmeasured whichever the reason (F-118, F-127). It also states the boundary the selector lists had been deciding silently: a control is what a person presses, and a link inside a sentence is body text.

## Veto points

1. **The counter and not the kitchen.** Half of Stage 1b, so the plan still has an open row and this slice does not close Stage 1. To reverse: create a kitchen staff account in scenario 6, open an isolated handheld page, sign it in by password, complete the forced change, and add a third `HandheldSurface` — which is a second sign-in journey and a second surface's worth of unmeasured selectors riding on a slice that cannot execute either.
2. **The bill is measured before the close rather than after.** A settled bill at 375px is unmeasured. That is deliberate — after the close the surface has no controls, and a barrier over a page whose subjects have all gone is the vacuous gate F-41 forbids — but it means *settled, read-only, on a handset* is a state nothing has looked at. To reverse: add a third measurement after `ConfirmCloseAsync` with a set whose required selectors are the ones that survive settlement.
3. **`counter.Url` names the measured page rather than a route constant.** The verdict message carries an absolute URL including the host. To reverse: make `CounterJourneys.PathFor` internal and call it — which puts a second copy of `/counter/sittings/` in the scenario file, and the URL cannot drift from the page it was read off.
4. **`.counter-back a` and its kind are outside every set.** A link in a paragraph is measured by nothing. To reverse: widen the reach sets to `#counter-sitting-surface a` — which puts every prose link under a 44px floor, which is not what §11.12 says.

## Sensitivity — reasoned against the edited tree, not executed

Nothing here was run. What follows is what each assertion is *reasoned* to catch, which is a different claim from a record of runs (**F-124**).

`handheld: true` removed: reported by the first assertion of both measurements, naming the measured width against 375. The board measured after the close: `.counter-sitting-actions a` matches nothing and `MeasureHereAsync` throws by name with the full census — the required-selector refusal rather than a verdict. The bill measured after settlement: three of four reach selectors and three of five font-floor selectors go silent, reported the same way. `--touch-target` dropped from `.link-button`: reported by the height assertion naming *Adjust price* and *Remove*. `font-size: max(1rem, 1em)` dropped from `.form-field select`: reported by the text assertion naming both selects. A control moved back into a right-hand column: reported by the reach assertion, which names F-59. The whole slice reverted: the scenario passes and the plan goes back to claiming it is finished, which is the state F-128 is about — and is why the remedy is a paragraph as well as a barrier.

Also emulated green against the delivered tree: §16.4's counted-assertion census (49 paragraphs, none disagreeing with its file, floor 37), `SpecificationVersionTests` (1.58 header, 1.58 newest, descending), and the Markdown table shape of every row added to the three documents.

## What was NOT verified

Nothing was compiled: no .NET SDK here, so 1332 is arithmetic and neither measurement has ever executed as C#. **No browser: neither measurement has ever been taken.** Every required selector in both sets was read out of `CounterBoard.razor` and `CounterSitting.razor` and matched against `app.css` and those files' own `<style>` blocks — reading a declaration is not measuring a box. Three things are reasoned rather than observed: that `.counter-sitting-actions`' two buttons wrap rather than overflow inside a ~313px panel at 375px; that `.link-button`, `.button-primary` and `.form-field select` compute to exactly 16px under an unstyled `body`, which is what makes a floor with no tolerance safe; and that both counter pages report `data-loaded='true'` with their controls already rendered, which is the anchor both measurements wait on. No container engine ran.

**The API of every new marker was read at the pinned version** (**F-126**): `Microsoft.Playwright` 1.62.0, where `IPage.Url` is a property and a context's viewport is fixed at creation — which is why the counter's page is opened handheld rather than resized.

**If it comes back red**, the first thing to read is the census in the message. A required-selector refusal means the arrangement is not what this slice thought it was; a reach or height verdict naming a control means §11.3 has an F-59 that eleven slices of §11.4 work never touched, and that is the finding rather than a defect in the barrier.
