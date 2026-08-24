# M6 Slice 58 — likes: the guest's control, and a number in its third generation

Extract at the repository root. Every file in the archive is a **complete file**; nothing is a patch, and
there are no scripts to run.

```
tar -xzf m6-slice-58-likes-the-guests-control.tar.gz
```

## Files to delete

**None.** Nothing is removed by this slice.

## New files — these must be `git add`ed

Several gates in this tree enumerate their subject with `git ls-files`, so an untracked new file is a file
they do not see:

```
git add tests/MyRestaurant.WebApplication.Tests/Menu/MenuItemReactionSurfaceContractTests.cs
git add tests/MyRestaurant.EndToEnd.Tests/MenuReactionScenarios.cs
```

Those are the only two.

## What is in the archive

| Path | Why |
| --- | --- |
| `src/MyRestaurant.WebApplication/Components/Pages/Table/TableOrderSurface.razor` | The control, in the detail panel; `_likedByMe` read beside the pictures; `ToggleLikeAsync` |
| `src/MyRestaurant.WebApplication/wwwroot/app.css` | `.order-menu-detail-actions` and the `.order-menu-like*` set — `--touch-target` floor, no new breakpoint |
| `tests/MyRestaurant.WebApplication.Tests/Menu/MenuItemReactionSurfaceContractTests.cs` | **new** — four facts, two of them rulings rather than mechanisms |
| `tests/MyRestaurant.EndToEnd.Tests/MenuReactionScenarios.cs` | **new** — §16.3 scenario 21 |
| `tests/MyRestaurant.EndToEnd.Tests/Harness/TableJourneys.cs` | `SeatGuestAsync` moves here, taking a patience parameter |
| `tests/MyRestaurant.EndToEnd.Tests/EndToEndScenarios.cs` | The private method becomes a one-line forwarder; its **eight call sites are untouched** |
| `tests/MyRestaurant.EndToEnd.Tests/Harness/TableOrderJourneys.cs` | `ReadChosenItemLikedAsync` and `PressLikeAsync`, plus the panel-scoped selector |
| `tests/MyRestaurant.WebApplication.Tests/Documentation/TestingSectionContractTests.cs` | **F-112**: the narrative census deleted, the floor 29 → 33 |
| `docs/TECHNICAL_SPECIFICATION.md` | **v1.43**: §7's control paragraph, §11.1, §16.3 scenario 21, the CI scenario count, §16.4's new paragraph, two Appendix A rows, changelog |
| `docs/DOCUMENTATION_REVIEW.md` | The F-112 row and a status paragraph about why a ruling does not survive by being right |
| `docs/MENU_AND_HANDHELD_PLAN.md` | Stage 5b becomes Stage 5b-i (landed) with Stage 5b-ii named |
| `docs/BUILD_PROGRESS.md` | The Slice 58 narrative, shipped whole |
| `_CHANGES.md` | This file |

## Test count

Baseline **1273**, verified two independent ways rather than carried. Your terminal log reports
`total: 1273, failed: 0` on both the Debug and the Release run; the arithmetic rebuilt from the tree
reproduces it exactly — 921 `[Fact]` + 329 `[InlineData]` + 17 + 6.

*(A naive count of `[InlineData]` gives **330**. The extra one is inside a comment in
`ProfileDetailsTests.cs` explaining why a bare `[InlineData(null)]` needs a cast. Recorded because §18's
method is to chase a one-count discrepancy, and this one is a scanning artefact.)*

| Where | Facts | Running |
| --- | --- | --- |
| Baseline (verified) | — | 1273 |
| `MenuItemReactionSurfaceContractTests` (new) | +4 | 1277 |
| `MenuReactionScenarios` (new, §16.3 scenario 21) | +1 | 1278 |

**Predicted: 1278**, and the §16.3 suite moves **20 → 21**. Anything else is the first thing to
investigate.

## The session opened three slices behind

Session memory said Slice 54 with an unverified baseline near 1250. The SHA-256 reconstruction said
**Slice 57** with a verified 1273, and it said so before anything was authored. Fourth time this has
caught drift; second time it has caught more than two slices' worth.

## Why the guest's control came before the administrator's count

Stage 5a closed with two reads and one write having no caller, and named which was worse: *a write nothing
calls is a code path no test can reach through the interface meant to protect it*. Two further arguments
agree.

**A count surface shipped first would be a read with no writer** — the same defect inverted and harder to
see, because the column renders, the page is correct, and every number on it is zero. A read with no caller
shows up in a census; a read whose only writer is an integration test looks like a working feature nobody
uses.

**And only this surface can produce a press**, so scenario 21 is writable now and the count slice
**extends** it rather than inventing an arrangement.

## The one thing worth reading twice

**The placement was predicted; the failure mode was not.** The plan has said since Stage 5a that the
control belongs in the detail panel because *a second interactive element inside a button is not markup
this application can write*. True, and one register short. The HTML parser does not **reject** a nested
button, it **repairs** it: meeting the inner `<button>` it closes the outer one, so the card becomes two
elements and the half carrying the dish's name and price stops being a control. Nothing throws, the Razor
compiles, §16.1 rules out bUnit so nothing renders a component, and §16.3's barrier measures where controls
*are* rather than whether they still do anything. A guest would find it by tapping a dish and having
nothing happen. That is why it is now a test rather than a sentence.

## The gate had to key on the call, not the name

The count prohibition's first draft keyed on the bare identifier `ListLikeCountsAsync` and **failed against
the tree** — because the file that must not call it is also the natural place to write down *why*, and the
surface's comments name it twice. A gate that reports a finding on a component whose only offence is
explaining the ruling it obeys is a gate that gets deleted. Both keys now carry an open parenthesis: the
rule is that a guest surface must not **call** the read, and a call cannot omit its parentheses. F-67's
shape — a mention is not a use.

## F-112, in one paragraph

F-73 ruled that the §16.4 census should be kept in prose and moved by habit; the habit failed twice. F-89
reversed it, deleted both prose copies, and kept `MinimumCountedClasses` as the only copy because a
constant asserted on every run can go stale only *loudly*. The doc comment **on that constant** then grew a
narrative census, and by this slice it recited *"…and now twenty-seven"* one line above `= 29`, guarding a
§16.4 that stated **32** — three numbers, two wrong, in the one place a ruling had cleared. Unreachable by
the gate for the reason F-89 already recorded: it compares assertion counts per class against files, and a
census is a count of paragraphs. The narrative is deleted and the floor moves 29 → 33. **The transferable
claim is narrower than "keep one copy":** twice a ruling has said this number will be moved by habit and
twice it was not, and the variable is not diligence — a number written beside an enforced copy of itself
*reads as part of the enforcement*, which is worse camouflage than an ordinary duplicate.

## Veto points

Three decisions are worth reversing if you disagree, and each is reversible on its own.

**1. `SeatGuestAsync` moved into the harness, and it is a passenger in a slice about likes.** It was
private inside `EndToEndScenarios`; a second scenario file needs it and a private method cannot be called
from one, so the alternative was pasting it (F-59, with F-100 already ruling against it). *To reverse:*
delete `TableJourneys.SeatGuestAsync`, restore the body into `EndToEndScenarios`' private method, and give
`MenuReactionScenarios` its own eight-line arrangement. What you buy is one less file touched; what you pay
is two copies of a journey that will drift. **Its failure mode is why it can ride along at all:** if the
move is wrong, every scenario that seats a guest fails at once, under names that have nothing to do with
likes.

**2. There is no in-flight guard on the press, and a reader will add one by analogy with `_sending`.** *To
reverse:* add a `_pressing` bool and disable the button while it is set. Note what you are buying: a
double-tap is the ordinary gesture on a heart, Blazor dispatches circuit events serially, so two taps today
are a like and then an unlike — exactly what the gesture means. A guard swallows the second half of it, and
it is the gesture `menu_item_reaction_current`'s identifier tie-break was written for (F-95).

**3. The floor moved 29 → 33 and no gate was added to keep it moving.** *To reverse:* add an equality
between `MinimumCountedClasses` and the computed census. Note what you are buying: an equality reports a
finding on every §16.4 paragraph that describes a test class without enumerating its assertions, which
§16.4 legitimately does for the three response-header classes covered as a group by directory. F-89 ruled
against it for exactly that reason and this slice does not reopen the ruling — it removes the copy that
could go stale in silence.

## One consequence, recorded rather than repaired

**A guest cannot like an unavailable dish.** §7 renders a deactivated item on the menu, marked, with a
`disabled` card — so the detail panel never opens for it and the control is unreachable. *The salmon is off
tonight and it is still the best thing here* is a real opinion this surface cannot record. It follows from
the placement rather than from a decision; the repair is a second route into the panel for items that
cannot be staged, which is a surface change with its own questions. Written down so it is not assumed to
have gone unnoticed.

## What was verified before packaging, and what was not

**Verified mechanically.** The tree was reconstructed from `dump.txt` and SHA-256 checked file by file: 371
clean, the only differences being `export.sh` (it embeds its own file marker) and `LICENSE` (elided by
design). **All four new contract facts were emulated against the edited tree and each proven sensitive by a
planted defect** — nesting the control inside the card is reported by two facts at once; deleting the
control is reported as a count of zero; redirecting the guest's read to the count is reported both as a
prohibited call and as the positive half going vacuous; a `SetLikedAsync` mention added to `MenuWorkflow`
is reported by name. The §16.4 counted-class gate was emulated over the edited specification: **33 classes,
no disagreement between any claimed count and its file**, and the floor set to match. The Markdown table
gate was emulated with the real unescaped-pipe splitter over all **25** tracked documents: clean. The
version gate was emulated on both versioned documents — headers matching their newest entries, 44 entries
strictly descending. A Razor tag-tree walk over the edited panel confirms the new `<div>`, `<button>` and
two `<span>` pairs balance. The standing authoring scans ran over every changed file: no `await` in an
interpolated hole (CS4007), every operand of every `string.Create` chain an interpolated string (CS1620),
no `stackalloc`, no `@section`, no `IndexOf(char, int, StringComparison)`. Byte hygiene — no CR, exactly
one final newline, no whitespace-only or trailing-space line — was checked on every file in the archive.

**Not verified.** Nothing was compiled and nothing was run; there is no .NET SDK reachable from where this
slice was authored, so the C# and the Razor are reviewed rather than built (F-71's standing caveat). The
specific risks, in order. **The scenario is the largest.** `PressLikeAsync` waits for `aria-pressed` to
change; if a circuit re-render is slower than the poll interval in a way the other journeys have not met,
it fails as a timeout rather than as a wrong answer. The message names the state it is still reporting, so
the diagnosis is in the failure, but it would be a red run about the harness rather than the product.
**`ToHashSet()` on `IReadOnlyList<Guid>`** is the one call shape in the new component code with no exact
precedent in that file — the neighbouring read uses `.ToDictionary(…)`, and both resolve from the same
`System.Linq`. **The `\u2665` / `\u2661` escapes inside a Razor explicit expression** follow the shape of
the `@(chosenItem.IsActive ? "…" : "…")` two lines above them, which compiles today, but they are the only
Unicode escapes in markup content in this tree. And **the forwarder changes a bad scan's failure** from an
xUnit assertion to an `InvalidOperationException` in eight scenarios this slice is not otherwise about;
both fail the test and nothing asserts on the exception type.
