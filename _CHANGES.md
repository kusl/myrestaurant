# M6 Slice 61 — the controls the barrier had been measuring, and a comment that described somebody else

**Apply this to a tree at Slice 60.** It edits nine files that earlier slices created or last touched, and
adds one. Extracting it over an older tree will leave a scenario referencing harness members that do not
exist and a §16.4 census that does not match.

Extract at the repository root. Every file in the archive is a **complete file**; nothing is a patch, and
there are no scripts to run.

```
tar -xzf m6-slice-61-press-the-resequencing-controls.tar.gz
```

## Files to delete

**None.** Nothing is removed by this slice.

## New files — these must be `git add`ed

| Path |
| --- |
| `tests/MyRestaurant.WebApplication.Tests/Documentation/DocumentationCommentContractTests.cs` |

**One file, and it must be added rather than merely extracted.** The tree and repository gates enumerate
with `git ls-files`, so an untracked file is invisible to every one of them — including, this time, the gate
itself.

## What is in the archive

| Path | Why |
| --- | --- |
| `tests/MyRestaurant.EndToEnd.Tests/Harness/AdministrationJourneys.cs` | `MoveMenuHeadingAsync`, `MoveMenuItemAsync`, `PressMoveAsync`; the index reader now reads the two move controls and refuses an omitted edge; `MenuHeadingOnTheIndex` gains two members; two F-114 repairs |
| `tests/MyRestaurant.EndToEnd.Tests/EndToEndScenarios.cs` | scenario 17 gains steps (k) through (n); still one `[Fact]` |
| `tests/MyRestaurant.WebApplication.Tests/Documentation/DocumentationCommentContractTests.cs` | **new** — F-114's gate, two assertions |
| `tests/MyRestaurant.WebApplication.Tests/Documentation/TestingSectionContractTests.cs` | `MinimumCountedClasses` 33 → 34 |
| `src/MyRestaurant.WebApplication/Components/Pages/Administration/HiddenRecords.razor` | F-114: `ListPath`'s comment returns to `ListPath` |
| `tests/MyRestaurant.DataAccess.Tests/Orders/OrderTestWorld.cs` | F-114: the falsified pre-F-86 paragraph deleted; two summaries on `AddMenuItemAsync` merged |
| `tests/MyRestaurant.EndToEnd.Tests/Harness/RestaurantInstance.cs` | F-114: the class's summary moves onto the class |
| `tests/MyRestaurant.EndToEnd.Tests/Harness/CounterJourneys.cs` | F-114: a forward and an inverse whose comments were swapped |
| `tests/MyRestaurant.EndToEnd.Tests/Harness/TableOrderJourneys.cs` | F-114: `ReadBadgeAsync`'s summary returns to `ReadBadgeAsync` |
| `docs/TECHNICAL_SPECIFICATION.md` | **v1.46**: §16.3 scenario 17, a new §16.4 paragraph, two Appendix A rows, changelog |
| `docs/MENU_AND_HANDHELD_PLAN.md` | Stage 3d, landed; the carried open item struck through in both stages that held it |
| `docs/DOCUMENTATION_REVIEW.md` | **F-114**, and the register's status line |
| `docs/BUILD_PROGRESS.md` | the Slice 61 narrative, shipped whole |
| `_CHANGES.md` | this file |

## Test count

| Where | Facts | Running |
| --- | --- | --- |
| Baseline — **predicted, not measured** | — | 1281 |
| `DocumentationCommentContractTests` (new) | +2 | 1283 |

**Predicted 1283**, and the §16.3 suite stays at **21**. Two facts rather than more: the resequencing work
adds assertions *inside* an existing `[Fact]`, and F-114's eight repairs add none.

**The baseline is carried rather than measured, so a deviation is not attributable to this slice alone.**
Slice 60 measured 1279 and predicted 1281; no run of 1281 exists. A run returning **1281** means the new
class did not execute. A run returning **1279** means Slice 60's two facts are missing too, and that is the
thing to chase first.

## Session start

**No drift.** The SHA-256 reconstruction from `dump.txt` verified 374 of 375 files byte-exact and agreed
with the incoming belief on every fact — Slice 60, v1.45, F-113, twenty-one scenarios, 1281 carried. The two
that did not verify are the two that never do: `export.sh`, whose dump rewrites its own header into the copy
it embeds, and `LICENSE`, elided to metadata by design. First session in five with nothing to correct.

## What this slice closes

**The oldest open item in the project.** Stage 3a shipped `ResequenceMenuSectionsAsync` and its two buttons
in Slice 47; Stage 3b shipped `ResequenceMenuItemsAsync` and its two in Slice 48, and closed with the
sentence this slice discharges: *no end-to-end scenario drives either resequencing verb — the barrier
measures the controls, nothing exercises them.* Thirteen slices, and by Slice 60 the only end-to-end gap
left in the menu.

**Why it was so easy to carry, which is the transferable part.** The controls were never *unasserted*.
Scenario 16 has measured them since each landed — where they sit, how tall they are, that they lie inside a
375px viewport — and both verbs are asserted against a real PostgreSQL. Every re-reading of the open item
met a page whose controls were demonstrably on the screen. **What the two instruments have in common is
that neither presses anything**, and the gap between them is the whole of what a browser adds. That is
F-109's mechanism on a second kind of deferral: a justifying sentence is not re-examined, because
re-examination is not what re-reading is for.

**Scenario 17 is extended rather than a scenario 22 added.** Seventeen ends with three headings, a two-item
heading, an empty heading, and a guest whose circuit has been open across five broadcasts — which is
exactly the arrangement a resequence needs. A scenario 22 would have cost a second container, a second
passkey registration and a second join to arrange what is already standing. Slices 59 and 60 both made this
call; it is now the default.

**Both directions of both controls, each with its restoration.** A resequence writes **absolute** positions
`0…n-1` over the list it was sent, so an implementation writing a relative offset gets the first move right
and cannot get the move back right as well. And a page that wired Up and left Down inert passes every
assertion scenario 17 held before now.

**A §11.4 ruling that had no assertion anywhere is discharged in passing.** "Disabled rather than omitted"
is in §7, in §11.4 and in the component's own comment, and nothing in the tree had an opinion about it — a
page that omitted the edge control would have satisfied the barrier, whose floor merely counts controls and
would have counted fewer. Presence is now the reader's business and enabled-ness the scenario's.

## The finding — F-114

**A documentation comment can describe a member other than the one it is attached to.** A `///` block binds
to the next declaration; C# has no file-level documentation comment; and a block holding two `<summary>`
elements compiles, publishes malformed XML documentation, and renders whichever one the tooling reaches
first. **Eight were in the tree, in five files, one under `src/`.**

Seven are misplaced prose, each arriving a different way — a class-level essay bound to whichever record was
declared first (twice), a comment orphaned by Slice 59 inserting a method between it and its subject, a
forward and an inverse whose comments were swapped, `ListPath`'s comment on `IsExpanded`.

**The eighth is what gives it a number.** `OrderTestWorld`'s `menu_item_event` INSERT carried its own
pre-F-86 description — *the payload columns `0004` and `0005` added are omitted rather than passed as NULL*,
and *the casts on the two columns that remain* — six lines above a statement that lists all five and casts
all five. **F-86 wrote its correction underneath the claim it falsified instead of over it**, and because
two summaries are legal the falsified sentence stayed first for twenty-two slices. This mechanism does not
merely misplace prose: it preserves a refuted claim where a reader meets it first, with nothing able to
report it.

**All eight repaired**, and where two summaries describe the same member the superseded one is **deleted**
rather than merged (F-77, F-89). **The rule is then made executable because it is total** — XML
documentation gives a member one summary and no other element is affected, so there is nothing legitimate
to exempt. F-81's class, not F-104's. Subject computed, no file named (F-58); an escaped mention is outside
the rule by construction, which matters because every repair refers to the tag that way.

**Writing the sensitivity proof found this gate's own instance of its subject.** The synthesised defect,
written as a literal, would have put two consecutive `///` lines carrying a summary each into the test file
— which the walk reads as text and cannot tell from a real comment. The first emulation reported a finding
on the file proving the gate works. The fixtures are composed at run time instead.

## Why two changes in one slice

The scenario fails as a named step of one Playwright fact in `MyRestaurant.EndToEnd.Tests`. F-114 fails as
one of two named unit facts in `MyRestaurant.WebApplication.Tests`, computing over source text, and its
eight repairs change no behaviour at all. Neither can make the other red, and a red run names which — which
is v1.32's distinguishable-failure exemption. **The gate could not have shipped later than the repair**,
because a gate on a tree that violates it is red on arrival.

## Veto candidates

**`ReadMenuIndexAsync` now throws on a group with anything other than two move controls.** This is the
"disabled rather than omitted" claim placed in the reader rather than in the scenario, and the cost is a new
way for scenario 17 to fail that has nothing to do with ordering: a §11.4 markup change now surfaces as a
harness exception. **To revert**, delete the `moveCount != 2` guard in
`AdministrationJourneys.ReadMenuIndexAsync` and pass `false, false` for the two new record members where the
controls are absent — the scenario's two `OffersMoveUp`/`OffersMoveDown` assertions then become the only
statement about the edges, and presence stops being asserted anywhere.

**The new gate reads `tests/` as well as `src/`.** Five of F-114's eight sites were under `tests/`, and the
harness is where this project keeps the reasoning that makes a scenario readable. **To revert**, remove
`"tests"` from `SourceRoots` in `DocumentationCommentContractTests` and lower both non-vacuity floors —
`MinimumFilesScanned` to roughly 120 and `MinimumBlocksScanned` to roughly 700 — or the gate will fail on a
correct tree for having read less than it expects to.

**The eight F-114 repairs each leave a paragraph explaining what moved.** That is F-70's rule — an
assertion nobody can find the reason for is an assertion the next person deletes — and it costs eight
paragraphs of housekeeping prose in files about other things. **To revert**, delete the paragraph beginning
*"This block sat at…"*, *"This paragraph was attached to…"* or *"A falsified paragraph stood above…"* in each
of the five files; nothing depends on them and the gate is indifferent.
