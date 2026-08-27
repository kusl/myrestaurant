# Slice 71 — Stage 6d: the sentence a guest can finally leave

Extract at the repository root. Spec goes to **v1.56**. Findings floor **F-127**.

## Files in this archive

| Path | State |
|---|---|
| `src/MyRestaurant.WebApplication/Components/Pages/Table/TableOrderSurface.razor` | changed — the comment box, two verbs, one status line |
| `src/MyRestaurant.WebApplication/wwwroot/app.css` | changed — the box joins four existing selector lists; four new rules; the like's text size |
| `tests/MyRestaurant.WebApplication.Tests/Menu/MenuItemCommentSurfaceContractTests.cs` | **NEW — needs `git add`** |
| `tests/MyRestaurant.EndToEnd.Tests/Harness/HandheldReach.cs` | changed — six barrier selectors |
| `tests/MyRestaurant.EndToEnd.Tests/Harness/TableOrderJourneys.cs` | changed — one reader, two verbs, two private helpers |
| `tests/MyRestaurant.EndToEnd.Tests/MenuReactionScenarios.cs` | changed — six comment claims in scenario 21; the test method renamed |
| `docs/TECHNICAL_SPECIFICATION.md` | changed — §7, §11.1, §11.12, §16.3 scenario 21, §16.4, Appendix A, changelog |
| `docs/DOCUMENTATION_REVIEW.md` | changed — F-127, one standing rule, one residual |
| `docs/MENU_AND_HANDHELD_PLAN.md` | changed — Stage 6d lands, five rulings recorded |
| `docs/BUILD_PROGRESS.md` | changed — the Slice 71 row and narrative |
| `_CHANGES.md` | changed (this file) |

**Nothing is deleted in this slice.** One file is added, so `git add tests/MyRestaurant.WebApplication.Tests/Menu/MenuItemCommentSurfaceContractTests.cs` — no new directory, and nothing needs `git rm`. `check_tree.sh` reads `git ls-files`, so an unstaged new file is invisible to tree hygiene.

## Expected test count

**1327.** 1321 observed + six `[Fact]` in the new contract class. Nothing was deleted and no existing count moved: scenario 21 is one `[Fact]` before and after, and the harness additions carry no attributes. §16.4's counted-class census goes 47 → 48 over a floor of 37, emulated at 48 with no paragraph disagreeing with its file.

Any other number is worth investigating before reading assertion text.

## What landed

Stage 6d, the last buildable row of the menu plan bar the staff read. One `<textarea>` and two buttons in the panel that already holds the like, over Slice 68's data access. No schema change, no endpoint, no rate-limit policy: the write rides the circuit as the like's does.

Five rulings, in §7. The box is **in the detail panel and never on the card** — a `<button>` may not contain interactive content, so a box there is markup the parser takes apart rather than markup that reads badly. **A blank body is a refusal and never a withdrawal**, because clearing the box and pressing Save is what somebody will do and reading it as a withdrawal puts a second authority on withdrawal inside a component whose write service already refuses a blank body by name. **The client's cap is an optimisation and every refusal is the server's** — `maxlength` is whatever `ReadDeclaredBodyCapAsync` read out of the constraint, and where the read cannot answer no attribute is rendered at all. **The draft belongs to the chosen dish**, keyed to it and reset when it changes, and `LoadMenuAsync` deliberately leaves it alone so a broadcast cannot take somebody's half-typed sentence away. **The outcome is declared beside the sentence**, so the barrier reads a verdict rather than the copywriting.

## F-127, and why it rides along

F-93 makes a new control kind bring its barrier selectors in the same slice. Writing them raised the only question that mattered — what type size do these get — and the answer, `1rem`, would have put two sizes in one panel, because `.order-menu-like` has declared `0.95rem` (15.2px) since Slice 58. §11.12's 16px floor carries no tolerance, and what enforces it is `FontFloorSelectors`, both of whose entries were `input[type=…]`. A **button** had never been measured for text at all. The remedy is a selector set rather than a rule, exactly as F-118's was: the like goes to `1rem`, and the box, the panel's button row and the comment row become named subjects.

The two changes are not separable — F-127 exists because Stage 6d brought the selectors, and shipping the selectors without moving the like's declaration leaves the barrier red — and they fail in ways nothing could confuse: six source-scan assertions naming a Razor file, against one `UndersizedText` verdict naming a selector and a pixel size.

## Veto points

1. **`data-comment-outcome` on the status line.** A machine-readable verdict beside the human sentence, so the scenario asserts `"NoChange"` rather than a sentence somebody may reword. Precedent is `data-live`, `data-loaded` and `data-picture-byte-budget`. To reverse: delete the attribute, and have `TableOrderJourneys` read the notice's text instead — the scenario then asserts copywriting, which is what F-113 ruled against one register up.
2. **The like's `0.95rem` → `1rem`.** The visible change is 0.8px on one control. To reverse: put `0.95rem` back and drop `#table-order-surface .order-menu-detail-actions button` from `FontFloorSelectors` — F-127 then reverts to a residual saying the floor has no button subjects, which is a weaker document and an unmeasured control.
3. **Scenario 21 rather than a scenario 22.** Sixth application of *the arrangement already exists*: a panel open on a chosen dish with a like in it is exactly what a comment needs, and a 22nd scenario buys a second container, a second passkey registration and a second join to arrange it. To reverse: lift the six claims into a new class and renumber; §16.3's *all twenty-one scenarios* in §16.4 moves with it.
4. **`MenuReactionScenarios.cs` keeps its filename** while its one scenario now covers two subjects. The method is renamed, which is what a failing run prints; the file is not, because a rename is a delete plus an add for a name no gate reads.

## Sensitivity — emulated against the edited tree and eight planted defects, not executed

The delivered tree passes all six facts. A copy of the box inside the card's `<button>`: reported. A second copy inside the panel: reported at two. `ListForPersonAsync` swapped for `ListAsync` on the injected directory: reported — and the same prohibition written as a bare `ListAsync(` marker was confirmed to fire on the menu read and the picture read that §11.1 requires, which is why the receiver is in the marker. `maxlength` written as the literal `1000`: reported twice. `ReadDeclaredBodyCapAsync` deleted: reported. The `BodyBlank` arm made to call `WithdrawAsync`, with the prose *press Withdraw* left in place: reported, so the fact keys on the call and not the word. `IMenuItemComments` added to `MenuWorkflow`: reported. The whole block deleted, which is the pre-slice tree: reported by four of the six.

Also emulated green: §16.4's counted-assertion gate (48 paragraphs, none disagreeing), `SpecificationVersionTests` (1.56 header, 1.56 newest, descending), the seven text-decidable halves of `HandheldLayoutContractTests` this slice can move (no undeclared property, no colour literal outside `:root`, no fallback, one width breakpoint, no `min-height` literal under 44px, `overflow-wrap` once, no shared-prefix redeclaration), `HarnessSnapshotContractTests` (20 files, three subjects, six readers, none multi-read — the new readers return `string` and are not subjects), and byte hygiene on all ten changed files.

## What was NOT verified

Nothing was compiled: no .NET SDK here, so 1327 is arithmetic and the six new assertions have never executed as C#. No browser: scenario 21's six new claims are unverified, the six new barrier selectors have never matched anything, and *`1rem` computes to 16.0px against a floor with no tolerance* is read off `app.css` rather than measured. No container engine: nothing exercised `SubmitAsync` or `WithdrawAsync` against a real database in this slice. Two Blazor behaviours are read rather than observed — that a null `int?` renders no `maxlength`, and that `@key` on the box makes the value follow the chosen dish rather than the element, which is the whole reason the key is there.
