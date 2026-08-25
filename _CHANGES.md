# M6 Slice 62 — the wall that was documented for eleven slices, and the refusal the endpoint now decides

**Apply this to a tree at Slice 61.** It edits fourteen files that earlier slices created or last touched,
and adds three. Extracting it over an older tree will leave a `RestaurantOptions` with two `required`
members nothing constructs, a §16.4 census that does not match, and a page opting into a policy that does
not exist.

Extract at the repository root. Every file in the archive is a **complete file**; nothing is a patch, and
there are no scripts to run.

```
tar -xzf m6-slice-62-the-refusal-the-endpoint-decides.tar.gz
```

## Files to delete

**None.** Nothing is removed by this slice.

## New files — these must be `git add`ed

| Path |
| --- |
| `src/MyRestaurant.WebApplication/Security/RateLimitedSurfaces.cs` |
| `src/MyRestaurant.WebApplication/Security/RateLimitingServiceCollectionExtensions.cs` |
| `tests/MyRestaurant.WebApplication.Tests/Security/RateLimitingContractTests.cs` |

**Three files, and they must be added rather than merely extracted.** The tree and repository gates
enumerate with `git ls-files`, so an untracked file is invisible to every one of them — including, again,
the new gate itself.

## What is in the archive

| Path | Why |
| --- | --- |
| `src/MyRestaurant.WebApplication/Security/RateLimitedSurfaces.cs` | **new** — `RateLimitedSurface` (three `required` members) and the list of two; both policy-name constants; `GenericRefusal`; `RefusalFor`; both partitioners |
| `src/MyRestaurant.WebApplication/Security/RateLimitingServiceCollectionExtensions.cs` | **new** — the one `AddRateLimiter`, and `OnRejected` dispatching on the endpoint |
| `tests/MyRestaurant.WebApplication.Tests/Security/RateLimitingContractTests.cs` | **new** — F-115's gate, six assertions |
| `src/MyRestaurant.WebApplication/Displays/DisplaysServiceCollectionExtensions.cs` | the limiter registration removed; three data services remain; summary records why it moved |
| `src/MyRestaurant.WebApplication/Displays/DisplayRoutes.cs` | `PairingRateLimitPolicy` **deleted** (moved); the budget stays, with the line between them stated |
| `src/MyRestaurant.WebApplication/Components/Pages/Display/DisplayPair.razor` | the attribute now names `RateLimitedSurfaces.PairingPolicy`; one new `@using` |
| `src/MyRestaurant.WebApplication/Components/Account/Pages/Register.razor` | gains `@attribute [EnableRateLimiting(…)]`, two `@using` directives, and the partition ruling in its comment block |
| `src/MyRestaurant.WebApplication/Program.cs` | `AddRestaurantRateLimiting()` added; two comments corrected — `AddRestaurantDisplays` no longer claims the limiter, `UseRateLimiter` now describes two policies |
| `src/MyRestaurant.WebApplication/Configuration/RestaurantOptions.cs` | two `required` properties, two defaults and two floors as named constants, two binding calls, two validation refusals |
| `.env.example` | both keys, with the partition hazard written out for the operator |
| `compose.yaml` | both keys in the `web` service's `environment` mapping |
| `tests/MyRestaurant.WebApplication.Tests/Displays/DisplaysWiringTests.cs` | the limiter fact removed (−1), two `using`s dropped, summary records where the claim went |
| `tests/MyRestaurant.WebApplication.Tests/RestaurantOptionsTests.cs` | `Build()` gains two parameters; two facts and two theories for the budget (+8 tests) |
| `tests/MyRestaurant.WebApplication.Tests/Documentation/TestingSectionContractTests.cs` | `MinimumCountedClasses` 34 → 35 |
| `docs/TECHNICAL_SPECIFICATION.md` | **v1.47** — §4.2, §11.8, §13, §16.4, §17, §18, Appendix A (F-115 + Stage 6 prerequisite row, F-37's row discharged in part), changelog |
| `docs/DOCUMENTATION_REVIEW.md` | F-115's ledger row, and a *Going forward* paragraph on the third instance of F-35/F-37's pattern |
| `docs/MENU_AND_HANDHELD_PLAN.md` | **Stage 6a** added and marked landed; Stage 6's prerequisite 1 struck through; the stage itself is **not** struck through |
| `docs/BUILD_PROGRESS.md` | the Slice 62 entry, appended |
| `_CHANGES.md` | this file |

## Two decisions worth your veto, with reversion instructions

**1 — the budget: 60 attempts per 10 minutes, floors 10 and 1.** The reasoning is that the partition is a
client address and over the tunnel a client address is *the whole dining room*, so the floor exists to
protect **guests** rather than the server — the inverse of every other bound in §13.

To revert: delete both properties, both defaults, both floors, both binding calls and both validation
blocks from `RestaurantOptions.cs`; delete the two keys from `.env.example`, `compose.yaml` and §13's
table; remove the `GuestRegistrationPolicy` entry from `RateLimitedSurfaces.All` and the constant beside
it; remove the `@attribute` line and the two `@using` lines from `Register.razor`; drop the eight tests
from `RestaurantOptionsTests.cs`; restore §17 and §11.8. **The mechanism survives that reversion intact,
which is deliberate** — F-115 is the mechanism, and the policy is not.

To keep the mechanism and only change the number: edit
`RestaurantOptions.DefaultGuestRegistrationAttemptsPerWindow` and
`DefaultGuestRegistrationWindowMinutes`, the two `.env.example` lines, the two `compose.yaml` defaults,
§13's two table rows, and the two literal assertions in
`FromConfiguration_GuestRegistrationBudget_UsesTheDocumentedDefault` — which exist so that halving the
number requires reading why it is what it is.

**2 — `GenericRefusal` is `static readonly`, not `const`.** That one keyword keeps the set of public
string literals on `RateLimitedSurfaces` equal to the set of policy names, which is what lets
`EveryPolicyNameConstantIsARegisteredPolicy` say *every public string constant* instead of *every one
whose name ends in a word somebody chose*. Changing it to `const` does not break the build and does not
fail any test — it silently turns that assertion into a claim about a refusal sentence, which will then
fail for the wrong reason. If it is changed, that assertion must be rewritten in the same edit.

## What was deliberately not done

**No `REQUIREMENTS.md` revision.** Revs 3–6 each added one cross-cutting §8 principle for **new intent**;
a limit on `/register` is a mechanism catching up to intent §17 had already recorded. Rev 2's reasoning,
not rev 3's. That document is not in the archive.

**Stage 6 is not struck through.** One of four prerequisites is discharged. Striking the stage would
claim it is startable.

**`0008_menu_item_reactions.sql` line 57 keeps its trailing space.** Pre-existing, and outside
`check_tree.sh` gate 2 by that gate's own explicit ruling — it fails only on lines made *entirely* of
whitespace. Repairing it here would be an unexplained edit to a migration in a slice about rate limiting.
Recorded in `BUILD_PROGRESS.md`, not fixed, and not a finding.

## Predicted test count

| Where | Tests | Running |
| --- | --- | --- |
| Baseline — carried from Slice 61, predicted, not measured | — | 1283 |
| `DisplaysWiringTests` — the limiter fact moves out | −1 | 1282 |
| `RateLimitingContractTests` (new) — six facts | +6 | 1288 |
| `RestaurantOptionsTests` — two facts, two theories at three and two cases | +8 | 1296 |

**Predicted 1296**, §16.3 unchanged at **21**.

**Five new attributes produce eight tests**, so **1293** means the theories were counted as facts rather
than that anything failed. The baseline was carried for the second consecutive slice: **1281** means the
new class did not execute and **1279** means Slice 61's two facts are also missing, either of which needs
Slice 61 reconciled before this slice is blamed.

## What was not verified

Nothing was compiled and nothing was run. Largest risk is `Register.razor` — two new `@using` directives
and an `@attribute` whose argument is a member access on a namespace that page had never imported, and
this tree's compiler errors live in `.razor` files (F-81, F-104).

**One claim no assertion in this repository makes:** that a static-SSR Razor endpoint carries
`[EnableRateLimiting]` in its endpoint metadata. If that is false, both surfaces are unlimited and every
test still passes. `/display/pair` has run on the same unasserted assumption since Slice 22.

Full account, including the two gate emulations that were wrong before they were right, in
`docs/BUILD_PROGRESS.md`.
