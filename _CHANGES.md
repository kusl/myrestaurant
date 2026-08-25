# M6 Slice 63 — a gate that could not tell a use from a mention, and the menu plan's rendering rule stops being a sentence

**Apply this to a tree at Slice 62.** It edits six files and adds three. Extracting it over an older tree
will leave a §16.4 census that does not match, a counted-class floor two above what the document states, and
two test classes referring to a helper that is not there.

Extract at the repository root. Every file in the archive is a **complete file**; nothing is a patch, and
there are no scripts to run.

```
tar -xzf m6-slice-63-use-mention-and-the-closed-set.tar.gz
```

## Files to delete

**None.** Nothing is removed by this slice.

## New files — these must be `git add`ed

| Path |
| --- |
| `tests/MyRestaurant.WebApplication.Tests/SourceCode.cs` |
| `tests/MyRestaurant.WebApplication.Tests/SourceCodeTests.cs` |
| `tests/MyRestaurant.WebApplication.Tests/Security/RawHtmlContractTests.cs` |

**Three files, and they must be added rather than merely extracted.** The tree and repository gates enumerate
with `git ls-files`, so an untracked file is invisible to every one of them — and `SourceCode.cs` being
invisible would mean the two gates that depend on it fail to compile with the reason unfindable.

## What is in the archive

| Path | Why |
| --- | --- |
| `tests/MyRestaurant.WebApplication.Tests/SourceCode.cs` | **new** — `WithoutComments`, the comment-blind reader both tree scans now go through |
| `tests/MyRestaurant.WebApplication.Tests/SourceCodeTests.cs` | **new** — four facts over composed fixtures, one per comment form |
| `tests/MyRestaurant.WebApplication.Tests/Security/RawHtmlContractTests.cs` | **new** — Stage 6b's gate, two facts, the recorded set of six |
| `tests/MyRestaurant.WebApplication.Tests/Security/RateLimitingContractTests.cs` | the scan reads code rather than text; the false open-parenthesis paragraph replaced; F-116 recorded in the class summary. **Still six facts** |
| `tests/MyRestaurant.WebApplication.Tests/Documentation/TestingSectionContractTests.cs` | `MinimumCountedClasses` 35 → 37 |
| `src/MyRestaurant.WebApplication/Security/ResponseSecurityHeaders.cs` | the census deleted from its summary; the gate that now holds the set named; the *second* line of defence stated as second |
| `docs/TECHNICAL_SPECIFICATION.md` | **v1.48** — §16.4 (two paragraphs added, one count deleted), §17, §18 (two habits), Appendix A (F-116, F-117, Stage 6b row), changelog |
| `docs/DOCUMENTATION_REVIEW.md` | F-116 and F-117 ledger rows, and a *Going forward* paragraph on what an emulation is evidence about |
| `docs/MENU_AND_HANDHELD_PLAN.md` | **Stage 6b** added and marked landed; Stage 6's prerequisite 4 struck through; the stage itself is **not** struck through |
| `docs/BUILD_PROGRESS.md` | the Slice 63 entry, appended |
| `_CHANGES.md` | this file |

## The failure you reported, and what it actually was

```
RateLimitingContractTests.EveryOptInInTheTreeNamesARegisteredPolicy
  src/…/Security/RateLimitedSurfaces.cs opts in with '…', which is not a member of RateLimitedSurfaces.
```

`RateLimitedSurfaces.cs` line 23 is a documentation comment reading *"The page opts in with
`[EnableRateLimiting(…)]` naming this value."* The scan's pattern is `EnableRateLimiting\(` and its own
summary claimed the open parenthesis made it safe, on F-67's authority.

**F-67 is a ruling about an identifier, not about a form.** Keying on `Foo(` rather than `Foo` does separate a
call from a mention of a *name*. It does not separate a use of a *construct* from a documentation comment that
spells that construct — and a comment explaining an attribute spells the attribute, placeholder argument
included, because that is what an explanation is. The gate and the sentence it could not read shipped in the
same archive, and the first real run reported a finding on a correct tree.

**Not your hand-fix.** *initialize required members* is `RestaurantOptions`' two new `required` properties
reaching their construction sites — an omission of Slice 62's authoring, correctly repaired. The scan defect
was already in the tree you repaired.

## Two decisions worth your veto, with reversion instructions

**1 — the prose in `RateLimitedSurfaces.cs` is deliberately left alone.** The one-character fix is to delete
the parenthesis from that comment, and it would have made your run green in ten seconds. It is declined
because it makes the gate's correctness depend on prose never quoting its own subject, which is a promise no
future sentence is bound by — and because leaving the mention makes the new reader *load-bearing*: replacing
the reader with a no-op reports that mention immediately, so the tree carries its own permanent proof that
the fix is doing something. **To revert:** change line 23 to say `<c>[EnableRateLimiting]</c>` without the
parenthesis, and `SourceCode.cs` becomes unnecessary for that gate — but `RawHtmlContractTests` still needs
it, because `ResponseSecurityHeaders`' summary names `MarkupString` in prose and a type name has no
parenthesis to key on at all.

**2 — `RawHtmlContractTests` records six file paths rather than asserting a rule.** A gate that names its
subject is normally a gate about one file (F-58), and this one names six. It is declined as a problem because
the property that matters — *no person-authored value reaches raw HTML* — is a fact about where a value came
from several calls away and is not decidable from text, so the alternatives were an approximation that reports
findings on correct files or nothing at all. A closed set turns the undecidable question into a human one
asked in the commit that adds a site. **To revert:** delete
`tests/MyRestaurant.WebApplication.Tests/Security/RawHtmlContractTests.cs`, drop `MinimumCountedClasses` to 36,
and remove the §16.4 raw-HTML paragraph — the specification's §17 sentence and
`ResponseSecurityHeaders`' summary would then be claims with nothing behind them again, so restore their
counts to *six* if you take this option.

## Predicted test count

| Where | Tests | Running |
| --- | --- | --- |
| Baseline — carried from Slice 62, predicted, not measured | — | 1296 |
| `SourceCodeTests` — four facts | +4 | 1300 |
| `RawHtmlContractTests` — two facts | +2 | 1302 |

**Predicted 1302.** §16.3 stays at **21** — no scenario was touched, and no Razor file was touched at all.

**Reconcile the baseline first.** Your run reported one failure and no total, so 1296 is still a prediction.
**1302** confirms the baseline and this slice together. **1298** means Slice 62's two theories counted as
facts. **1296** means neither new class executed.

## What was not verified

Nothing was compiled and nothing was run. The largest risk is `SourceCode.WithoutComments` itself: it is a
hand-written state machine, and it was verified by transcribing the C# back into Python line for line and
running it over all 169 `.cs` and `.razor` files under `src/` — `EnableRateLimiting\(` 3 → 2 with both
arguments correct, `MarkupString` 7 → 6 in exactly the six recorded files, newline count preserved in every
file, and structural tokens still present in the raw-string, CSS-comment and Razor-comment-block files. That
proves the algorithm, not the C#. `docs/BUILD_PROGRESS.md` lists the rest in order.
