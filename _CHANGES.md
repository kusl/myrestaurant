# Slice 74 — Stage 1g: the station most of the sentence was about

Extract at the repository root. Spec goes to **v1.59**. Findings floor moves to **F-129**.

## Files in this archive

| Path | State |
|---|---|
| `tests/MyRestaurant.EndToEnd.Tests/Harness/HandheldReach.cs` | changed — two `HandheldSurface` records appended |
| `tests/MyRestaurant.EndToEnd.Tests/EndToEndScenarios.cs` | changed — scenario 6 renamed and rearranged, three constants, one shared verdict message made surface-neutral |
| `docs/TECHNICAL_SPECIFICATION.md` | changed — header, §11.12, §16.3 scenario 6, §19's M7 paragraph, Appendix A, changelog |
| `docs/MENU_AND_HANDHELD_PLAN.md` | changed — Stages 1, 1b and 1g, one ruling row, closing paragraphs rewritten |
| `docs/DOCUMENTATION_REVIEW.md` | changed — header count, two standing-rules rows, the F-129 ledger row, one residual |
| `docs/BUILD_PROGRESS.md` | changed — the Slice 74 row and narrative |
| `_CHANGES.md` | changed (this file) |

**Files to DELETE: none.** Nothing is added and nothing is removed, so there is no `git add` and no `git rm` — every path above is already tracked. `check_tree.sh` reads `git ls-files`, so a plain `git status` after extracting should show six modified files and nothing untracked.

**No production code changed.** No `.razor`, no `.cs` under `src/`, no `app.css`, no migration, no service, no endpoint. The barrier is two records in the harness and a rearranged scenario.

## Expected test count

**1332 — unchanged.** Scenario 6 is one `[Fact]` before and after; its method is renamed rather than split, exactly as scenario 21's was in Slice 72 and scenario 10's in Slice 73. The two `HandheldSurface` records carry no attribute, and `AssertHandheldBarrier` is an existing private helper. §16.4's counted-class census stays at **49** over a floor of 37: `HandheldReach.cs` and `EndToEndScenarios.cs` are end-to-end sources, not counted classes, so no paragraph gained or lost a number. End-to-end scenarios stay at **21**.

Any other number is worth investigating before reading assertion text.

## What landed

**Stage 1g, the kitchen.** §16.3 scenario 6 now creates a kitchen account, signs it in by password on an isolated 375×667 context, completes the forced change, opens the board **before** the guest sends, and **fulfils the line from there**. The administrator's board stays open and becomes the observer. The station R§1's sentence is most about had been worked by an administrator at a desk in every scenario this repository has.

**The order is load-bearing.** §10.3 renders `.kitchen-alert-badge` only while something is unseen, and `KitchenAlertState` is a field on the circuit. A board opened after the send has an unseen count of zero, so the badge would not exist to be measured. Open first, and the alert arrives at a page that is watching.

**Two measurements.** The **pass** while the alert still stands, because `AppendAsync` acknowledges on every successful write — measured after the away, `.kitchen-alert-badge` matches nothing. The **board once a line is away**, because `.kitchen-recent-line`'s Undo exists only inside the fifteen-minute fulfilment window. Both are measured *where they stand*: the unseen count and the armed state are circuit fields that a navigation resets.

**Every selector in both sets is required for reach and for text.** Five on the pass — alert badge, *Enable sound*, *Fulfill all*, the line button, the 86 toggle — and three after: Undo, the surviving line button, *Fulfill all*. The text floor gets its button subjects in the slice that names the surface rather than a slice later (**F-127**).

**F-129.** Slice 73 wrote *it names four* into §11.12 and closed the same paragraph with *which is why the set is written down and not counted*. The next slice makes the set six. The count is **deleted rather than corrected** (F-77, F-89), and what replaces it is what a list cannot carry: what is outside the set and why — §11.5's display is a wall screen; §11.6 and §11.8 declare no control of their own. The kitchen's deferral sentence is deleted rather than softened, because its subject has ceased to exist (**F-41**).

**Stage 1 closes**, and with it the last open row `docs/MENU_AND_HANDHELD_PLAN.md` opened on 2026-08-11. Every row in its stage table now carries a slice number.

## Not verified

Nothing was compiled — no .NET SDK in the authoring environment — so 1332 is arithmetic rather than an observation and neither measurement has ever matched an element. No browser ran. The claim that the kitchen board lays out inside 375px is read out of `app.css`: a 313px panel content box against a widest declared row of about 279px. That margin is thin, and a real run is the only thing that settles it. The claim that a `KitchenAlert` reaches a second browser context is read from §9's broadcaster being in-process; scenario 4 makes the same claim on one context and nothing has made it on two.

## Veto points

1. **The fulfilment moves from the administrator's page to the handset.** Scenario 6's original claim is preserved and strengthened — the wide board still asserts the line left the pass — but the appending actor is now `OrderActorRole.Kitchen` rather than `Administrator`. Nothing in this scenario reads the actor. To reverse: call `KitchenJourneys.FulfillLineAsync(service.Kitchen, …)` as before and measure the handheld board without ever pressing anything on it — which costs the second measurement, because Undo would never exist.
2. **The alert badge is a required selector.** If a `KitchenAlert` does not reach a second circuit, `WaitForBoardAsync` fails before any measurement runs, naming what it waited for. To reverse: drop `snapshot.UnseenAlertCount >= 1` from the wait and make `button.kitchen-alert-badge` optional in `KitchenPass` — at the cost of leaving §10.3's own control unmeasured, which is the state F-127 was about.
3. **Two measurements rather than three.** The board is never measured *empty* — the state a kitchen looks at for most of a shift. That is deliberate: an empty board's only controls are *Enable sound* and the 86 panel, and a barrier over a page whose subjects have mostly gone is the vacuous gate **F-41** forbids. To reverse: measure once immediately after `KitchenJourneys.OpenAsync` on the handheld, with a set whose required selectors are the two that survive an empty pass.
