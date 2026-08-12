# M6 Slice 32 — the barrier F-59 would have failed, and the reason it took two slices (F-62)

Extract at the repository root. Every path is repo-relative and every file is complete.

```
tar -xzf m6-slice-32-handheld-reachability-barrier.tar.gz
git add tests/MyRestaurant.EndToEnd.Tests/Harness/HandheldReach.cs
git status
```

**Files to DELETE: none.**

**`git add` is required** for the one new file above — `scripts/check_tree.sh` enumerates with
`git ls-files`, so an untracked file is a file no gate looks at.

---

## What this slice is

Slice 30 fixed F-59, wrote §11.12, and made its *structure* executable. It also recorded, in three
places, the one assertion it deliberately did not make: that a control is **reachable** inside a 375px
viewport. That is the assertion F-59 would have failed, and it is the only one that would have.

This slice makes it. §16.3 gains a sixteenth scenario, and it is the first in the matrix whose subject is
not a flow.

---

## F-62 — the deferral rested on a fact the tree contradicts

The reason recorded in Slice 30, verbatim:

> the fifteen §16.3 scenarios all run in one default context, and giving one of them a second viewport is
> either a second browser context per run or a resize that every subsequent scenario inherits

`RestaurantHarness` holds one **browser**. Each scenario calls `StartInstanceAsync` → `StartAsync` →
`browser.NewContextAsync(...)` and gets a context of its own; `OpenIsolatedPageAsync` mints further ones
on request. A viewport belongs to a context. There was nothing to share and nothing to inherit — and
`RestaurantInstance`'s class summary has carried a paragraph headed *why more than one browser context*
since Slice 2.

**What makes it a finding is the propagation.** The sentence was written once while planning, and by the
close of that slice the same claim was in S§16.4, in F-59's ledger row and in the plan. Three documents
asserting a property of a file none of them had read. F-50's shape, applied to something that was *never*
true rather than to something that stopped being true — which is the worse direction, because there is no
moment at which it became wrong for a reader to notice.

---

## The design decision to veto, if you want to

**The stage order is swapped.** `docs/MENU_AND_HANDHELD_PLAN.md` said Stage 1b (converting the four
remaining surfaces) was next and 1c (this barrier) was open. This ships 1c.

The argument: 1b is roughly 2,400 further lines of Razor, and converting it before the barrier exists
means converting it exactly the way the four pages in F-59 were written — by hand, with nothing in the
tree able to decide whether the result is reachable. Building the barrier first also retro-proves Slice
30's four pages, which nothing until now could.

**To revert:** the plan's Stage 1c section is struck through with the reasoning above it; restore it to
`**open**`, drop the four paths from `HandheldAdministrationPaths`, and the scenario stands as a barrier
with nothing to measure. Nothing else in the slice depends on the ordering.

---

## Files

| Path | What changed |
|---|---|
| `tests/MyRestaurant.EndToEnd.Tests/Harness/HandheldReach.cs` | **new** — the measurement: one `EvaluateAsync` round trip returning document overflow, every action's box, every control's height |
| `tests/MyRestaurant.EndToEnd.Tests/Harness/RestaurantInstance.cs` | `HandheldViewportWidth`/`Height` (375×667), a shared `ContextOptions(baseUrl, handheld)` factory, the same option on `OpenIsolatedPageAsync` |
| `tests/MyRestaurant.EndToEnd.Tests/Harness/RestaurantHarness.cs` | `handheld` parameter on `StartInstanceAsync`, and the F-62 paragraph beside it |
| `tests/MyRestaurant.EndToEnd.Tests/EndToEndScenarios.cs` | §16.3 scenario 16 and its fixtures |
| `tests/MyRestaurant.WebApplication.Tests/Deployment/ContainerImageReferenceContractTests.cs` | one doc-comment sentence that counted the scenarios |
| `docs/TECHNICAL_SPECIFICATION.md` | **v1.17** — §11.12's close, §16.3's sixteenth scenario, §16.4's barrier paragraphs, §19's M7, F-59 corrected, **F-62** added, changelog |
| `docs/DOCUMENTATION_REVIEW.md` | **F-62**; F-59's closing sentence corrected in place |
| `docs/MENU_AND_HANDHELD_PLAN.md` | Stage 1c struck through; Stage 2's scenario count and Stage 3's scenario number corrected |
| `docs/BUILD_PROGRESS.md` | Slice 32 entry (complete file) |
| `docs/OPERATIONS.md` | §14's gate table: sixteen scenarios |
| `README.md` | four passages that counted the scenarios, and scenario 16's row in the matrix table |
| `.github/workflows/ci.yml` | one comment that counted the scenarios |
| `_CHANGES.md` | this file |

---

## The three assertions

Each against the viewport, one pixel of tolerance:

1. **No surface is wider than its own viewport** — `documentElement.scrollWidth` vs `clientWidth`. F-59's
   mechanism as a number.
2. **Every action lies inside it** — `getBoundingClientRect()` on `.record-actions` and
   `.page-head-action` controls. The finding itself, per element.
3. **Every control is ≥ 44px tall** — the same rects, plus the area-link pills.

### Three properties of it are rulings

**The viewport is asserted first**, read from the document rather than from the option that set it: at
Playwright's default 1280 everything else passes and means nothing (F-41). It is a *ceiling* with twenty
pixels of allowance under it, because `clientWidth` excludes a classic scrollbar and headless Chromium
draws one here — an equality assertion would have failed on a correct tree on the first run.

**The count of measured controls is asserted.** Seven expected; floor of six. A renamed `.record-actions`
leaves three, a renamed `.page-head-action` leaves four.

**The widest element is collected and may never fail a run.** `.page-head-areas` is a horizontally
scrolled strip by design and its pills extend past the right edge. The walk skips anything inside a
scroller and even then only writes the sentence that explains a failure.

---

## One fixture that is doing real work

The counter account's display name is `Anastasia Featherstonehaughwolstenholmeworthington`. A single
token longer than the card is wide is the one input that can push a record card past the viewport, and
§11.12 relies on `overflow-wrap: anywhere`. **The keyword is load-bearing:** `break-word` breaks the line
but leaves min-content at the token's length, so the page still scrolls sideways; only `anywhere` shrinks
min-content. `app.css` says `anywhere`. Without this fixture the scenario asserts that the stylesheet
contains the right word; with it, that the word does the right thing.

---

## Verification

- **The embedded JavaScript ran.** Extracted from the raw string literal, parsed by `node --check`, then
  executed against a hand-built fake DOM. A pill 100px past the right edge inside an `overflow-x: auto`
  strip was correctly **ignored**; a rogue element outside any scroller was correctly **named**.
- **Balance, CS1620 and CS4007 scans** on all four C# files with an untouched sibling as a control: clean,
  and each scan proven sensitive by its own regression.
- **Every `StartInstanceAsync` call site audited** — fifteen, all naming `cancellationToken:` — before the
  parameter was inserted before it.
- **CA1861 caught during authoring**: the selector pair was an array literal at a call site, which is an
  error under `ContinuousIntegrationBuild`. It is a `static readonly` field now.
- **`SpecificationVersionTests` ported and run**: header 1.17 against newest entry 1.17, descending.
- **Byte hygiene** on every delivered file.

**Not verified: nothing compiled, and no browser rendered anything.** The Chromium download is blocked by
the authoring sandbox's egress allow-list. That these four pages *pass* the barrier rests on reading
`app.css`, not on a measurement — the first run on the workstation is what proves it, and a red result
there names the widest element outside a scroll container, which is the diagnosis.

**Predicted test count: 1074**, from 1073, one new `[Fact]`. Arithmetic, not an observation.
