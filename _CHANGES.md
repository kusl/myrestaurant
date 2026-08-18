# M6 Slice 47 — the runner that was a default, and the verb that could finally be written

Extract at the repository root. Every path is repo-relative and every file is complete.

```
tar -xzf m6-slice-47-test-runner-and-resequence.tar.gz
git add tests/MyRestaurant.WebApplication.Tests/Deployment/TestRunnerContractTests.cs
git add tests/MyRestaurant.DataAccess.Tests/Menu/MenuSectionResequenceTests.cs
git status
```

**Files to DELETE: none.**

**`git add` IS required, for two new files, and it is not optional.** Every gate and CI job in this
repository enumerates with `git ls-files`. An untracked file is invisible to `scripts/check_tree.sh`, to
`scripts/check_repository.sh`, to the shell-scripts job and to `export.sh` — and one of the two new files is
itself a gate that walks the tree, so an untracked copy of it would be a gate that never runs.

**No schema change, no migration, no ADR edit, no `compose.yaml` edit, no `.slnx` edit, no
`REQUIREMENTS.md` edit, and no CSS.**

---

## Two changes, and why they ride together

You asked for both, so here is the reasoning rather than an apology. Slices 45 and 46 each deferred the menu
verb on one rule — **one change, one green run, then the feature** — because a red run beside two changes has
two candidate causes. That rule is about *indistinguishable* symptoms, and these two cannot be confused:

- A **runner** defect fails before this project's own code runs: an MSBuild error out of a `.targets` file,
  exit code 5 for an unrecognised argument, or a summary reporting no tests at all.
- A **verb** defect fails as a named assertion in one of two files.

So the first question a red run raises answers itself: *did the suite run?* If it did not, nothing under
`src/` is implicated. If it did, nothing about the runner is.

---

## 1. The runner (F-97) — this is the fix for the error you pasted

`xunit.v3` 4.0.0 (published 2026-08-14) installs `xunit.v3.mtp-v2`, which pins Microsoft.Testing Platform 2,
and **MTP 2 has removed the VSTest target for the .NET 10 SDK**. That is exactly the message you got, four
times, from a `.targets` file in your NuGet cache. It cannot be fixed by staying on 3.2.2 forever, and it is
not fixed by `TestingPlatformDotnetTestSupport` either — that property is the .NET 8/9 mechanism and is the
legacy path the migration guidance points away from.

**The .NET 10 mechanism is one stanza in `global.json`:**

```json
{ "test": { "runner": "Microsoft.Testing.Platform" } }
```

With it, `dotnet test` runs the test applications directly. Three consequences you will notice:

| Before (VSTest) | Now (MTP) |
| --- | --- |
| `dotnet test MyRestaurant.slnx` | `dotnet test --solution MyRestaurant.slnx` |
| `dotnet test tests/X/X.csproj` | `dotnet test --project tests/X/X.csproj` |
| `--logger "trx"` and `--logger "console;verbosity=normal"` | `-- --report-xunit-trx` and `--output Detailed` |

**Both adapter packages are deleted rather than pinned back** — `Microsoft.NET.Test.Sdk` and
`xunit.runner.visualstudio`, from all four test projects and from `Directory.Packages.props`. `xunit.v3`
carries MTP support natively; the adapters are the VSTest half, and a version standing ready for a package
that cannot be used is an invitation with a comment on it.

**The finding is not the build failure.** It is that this tree carried *both* runners for eight milestones,
so which one ran was the SDK's default rather than anybody's decision — and that the choice is spelled in
**four independent places** that each move on their own: the stanza in `global.json`, a package reference per
project, a version pin, and the command line in every script and workflow. Half-migrated means `dotnet test`
does different things depending on which file was edited last, and the two mechanisms disagree about the most
ordinary argument there is: VSTest reads a bare path as the thing to run, MTP reads it as a directory to
search.

So the row names something executable: **`TestRunnerContractTests`, four assertions**, subject computed over
every project file, every `*.Tests.csproj`, every tracked script and every workflow — not over the two files
that hold an invocation today. **All four were proven to fail on the pre-fix state** (stanza removed, adapters
restored, `OutputType` dropped, old command lines back).

**One detail worth your time: the gate caught itself before it shipped.** Its package scan first matched the
bare package name and reported two findings on a correct tree — the comments this slice added to the four
projects name both banned packages in order to explain their deletion. It now requires the `Include`
attribute, on the standard F-67 arrived at (*declared, not merely mentioned*), and those comments are now the
proof it does not fire on prose.

## 2. The menu (Stage 3a) — `ResequenceMenuSectionsAsync`

The cut §7 has recorded for three slices is closed. The verb takes the **whole ordering** and assigns
`0…n-1` from it, which is what makes "move this heading up" expressible at all: `display_order` positions are
permitted to be equal, so no single absolute write distinguishes two headings sharing one, and a pairwise
swap would have to decide what happens when they do.

- Locks every row `FOR UPDATE` **ordered by identifier**, so two concurrent resequences cannot deadlock
  against each other.
- Writes **one `reordered` event per heading that actually moved** — three headings reversed leaves the
  middle one alone and writes two events, not three.
- **Refuses** a list that is not a permutation of the stored set, whole, with nothing written: short,
  repeating, and naming an unknown heading are one outcome, because from the write's side they are one fact.
- All of one call's events share an instant, so they read in the order the rows were written **only because**
  Slice 45 made the identifier factory ascend inside a millisecond. That is why this verb waited for F-95
  rather than shipping beside it — its own ordering test would otherwise have been the first test of the fix.

**The surface is `/administration/menu`.** Up and Down at the foot of each heading's group, each its own
static-SSR form named from the heading's identifier, posting the list the page already rendered with two
entries exchanged. The ends are **disabled rather than omitted**, because a control that vanishes at the edge
of a list moves every other control up a row on the next render and scenario 16 measures where controls are.
The section editor keeps its absolute-position field: that is a different question, not a duplicate.

**F-93 is obeyed on the way in for the first time.** The barrier gains `.menu-group-actions button` in the
same slice as the buttons, rather than in the slice after somebody notices.

## 3. The dump — measured, and deferred by name

**Your numbers are behind the tree.** Slice 46's cut has landed: `dump.txt` is **5.48 MB / 102,253 lines**,
not 116,400. I reconstructed all 351 files from it and **SHA-256 matched 350 of 350 non-elided files**, so
this archive was built on a byte-exact tree.

Where the remaining bytes are:

| Path | Size | What it is |
| --- | --- | --- |
| `docs/TECHNICAL_SPECIFICATION.md` | 445 KiB | of which Appendix A 139 KiB, changelog 64 KiB |
| `docs/DOCUMENTATION_REVIEW.md` | 227 KiB | the long-form twin of Appendix A |
| `docs/BUILD_PROGRESS.md` | 124 KiB | Slice 40 onward |
| everything else | about 4.6 MB | authored source, tests, scripts, CSS |

**`export.sh` is not in this archive, because nothing in it needed to change.** Every cut still available is
a *split of a history register*, and a split needs no exporter edit at all — `docs/progress/` is already
withheld by path. What a split does need is care, because each of those documents is read by four gates
(`SpecificationVersionTests`, `MarkdownTableContractTests`, `TestingSectionContractTests`,
`ContextDumpExclusionContractTests`), and Slice 46's own entry records that its first split attempt failed
tree hygiene on a trailing blank line.

So, specified for the next slice rather than done here, on exactly the reasoning Slice 46 used to defer this
verb:

1. **`docs/DOCUMENTATION_REVIEW.md` splits at a finding boundary**, the older tranche to
   `docs/progress/`, the recent tranche staying so a slice can still append its row. **About 200 KiB**, and
   it is the same operation Slice 46 performed on the build log, so the pattern and its hazards are already
   written down.
2. **The specification's Appendix A moves whole** to `docs/progress/`, with the section becoming a pointer
   paragraph so the roughly one hundred *Appendix A* citations stay valid. **139 KiB.**

**Together about 6% of the dump.** Worth saying plainly: at roughly 30–60 KiB of new prose per slice, that
buys several slices and no more. The register split is the next slice; after it, the honest answer is that
this tree is 4.6 MB of source and prose a session actually reads, and the way to shrink that is to write less
of it.

## Files in this archive

| Path | What changed |
| --- | --- |
| `global.json` | the `test` stanza — the MTP opt-in |
| `Directory.Packages.props` | xunit.v3 to 4.0.0; both adapter packages deleted, with the reason where the versions were |
| the four test `csproj` files | both adapter references deleted; the Domain project explains why |
| `.github/workflows/ci.yml` | both test steps respelled; artifact paths widened |
| `scripts/ci_local.sh` | both test invocations respelled |
| `README.md` | `--project`, and why; the filter switches `dotnet test -?` now offers |
| `tests/MyRestaurant.WebApplication.Tests/Deployment/TestRunnerContractTests.cs` | **new** — four assertions, `git add` required |
| `src/MyRestaurant.DataAccess/Menu/MenuSectionAdministration.cs` | the outcome enum, the verb, the whole-table locking read, the permutation test |
| `src/MyRestaurant.WebApplication/Menu/MenuWorkflow.cs` | the verb, one conditional publish |
| `src/MyRestaurant.WebApplication/Components/Pages/Administration/AdministrationMenu.razor` | Up and Down per heading, the handler, the flash |
| `tests/MyRestaurant.DataAccess.Tests/Menu/MenuSectionResequenceTests.cs` | **new** — eight assertions, `git add` required |
| `tests/MyRestaurant.DataAccess.Tests/Orders/OrderTestWorld.cs` | a `QueryAsync` sibling to `ScalarAsync` |
| `tests/MyRestaurant.WebApplication.Tests/Menu/MenuWiringTests.cs` | the fake learns the verb; two facts |
| `tests/MyRestaurant.EndToEnd.Tests/Harness/HandheldReach.cs` | `.menu-group-actions button` joins the barrier (F-93) |
| `tests/MyRestaurant.WebApplication.Tests/Documentation/TestingSectionContractTests.cs` | census floor 21 to 23 |
| `docs/TECHNICAL_SPECIFICATION.md` | v1.32; §7; §16.4 opening plus two paragraphs; two counts moved and one prose census deleted; Appendix A F-97 and the Stage 3a row; changelog |
| `docs/DOCUMENTATION_REVIEW.md` | F-97 row; status line |
| `docs/BUILD_PROGRESS.md` | the Slice 47 entry |
| `docs/MENU_AND_HANDHELD_PLAN.md` | Stage 3a struck through, with what landed and the three things the design did not settle |
| `_CHANGES.md` | this file |

## What to run, in this order

```
bash scripts/check_tree.sh
bash scripts/check_repository.sh
dotnet restore
dotnet test
```

**`dotnet restore` on its own first, and read it.** It is the one step that can tell you the runner migration
is wrong before anything else is at stake. If it succeeds and `dotnet test` then fails with a `.targets`
error, an unrecognised option, or "no tests ran", the cause is in section 1 and nothing under `src/` is
implicated.

**Expect `dotnet test` to report 1180.** That is 1166 plus fourteen: four in `TestRunnerContractTests`, eight
in `MenuSectionResequenceTests`, two in `MenuWiringTests`. Any other number is the first thing to investigate
(§18). If it reads 1180 and a *documentation* gate is red, look at §16.4's census first — the floor moves
21 to 23 in the same slice as the two paragraphs that raise it, so a mistake there fails twice.

Then, when you have a green run:

```
scripts/ci_local.sh --with-all
```

## What was NOT verified

**Nothing was compiled and no test was run.** No .NET SDK in the authoring environment and no reachable
package feed. Per §18 an uncompiled archive is a prediction — build it before believing any of the above.

**`xunit.v3` 4.0.0 was not restored.** Its release notes and both Microsoft references were read; what its
dependency graph resolves to on your machine was not. **If `--report-xunit-trx` is rejected with exit code 5,
delete that flag and the `--` before it from both CI steps.** The report is an artifact upload, not a gate.

**MTP's zero-tests exit code (8) was not exercised.** Skipped tests are reported tests, so a solution-wide run
where the end-to-end scenarios skip should not trip it. If it does, `--ignore-exit-code 8` on that step is the
documented remedy — but check first, because "no tests ran" on a project with 17 of them is a finding.

**No Blazor form was rendered.** Two forms per heading with per-heading `@formname` values is the documented
static-SSR pattern, and this page now has two per group. If a POST landed on the wrong handler, every heading
would move the same one. This is the single most likely thing to be wrong, and it is visible in one click.

**The 375px barrier was not run**, so `.menu-group-actions button` is asserted to be measured rather than
measured. **The resequence never ran against PostgreSQL**, so the `FOR UPDATE` ordering is read from
PostgreSQL's documented behaviour rather than demonstrated by two concurrent transactions.

**`shellcheck` here was `shellcheck-py`**, not your distribution binary, so a version difference could report
differently. Clean at `--severity=warning` (blocking) and `--severity=style` (advisory) on all twelve scripts.

**One claim was removed rather than shipped.** A comment first said NSubstitute 6.0.0 "was current on
2026-08-17". Nothing here checked that. It now says the pin is unchanged and unverified by this slice.
