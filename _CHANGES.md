# Slice 70 — the gate that named an API the pinned package does not have

Extract at the repository root. Spec goes to **v1.55**. Findings floor **F-126**.

## Files in this archive

| Path | State |
|---|---|
| `tests/MyRestaurant.WebApplication.Tests/Deployment/ContainerLoggingContractTests.cs` | changed — one fact instead of two |
| `tests/MyRestaurant.DataAccess.Tests/PostgreSqlFixture.cs` | changed — unused import dropped |
| `tests/MyRestaurant.EndToEnd.Tests/Harness/RestaurantHarness.cs` | changed — unused import dropped |
| `Directory.Packages.props` | changed |
| `docs/TECHNICAL_SPECIFICATION.md` | changed |
| `docs/DOCUMENTATION_REVIEW.md` | changed |
| `docs/BUILD_PROGRESS.md` | changed |
| `_CHANGES.md` | changed (this file) |

**Nothing is deleted in this slice.** No file is added and no file is removed, so nothing needs `git add` and nothing needs `git rm`. `docs/MENU_AND_HANDHELD_PLAN.md` is deliberately untouched: no stage moved and no menu ruling moved.

Your two fixture edits are kept exactly as you made them — `.WithLogger(NullLogger.Instance)` on the builder chain — with one further change each: `using DotNet.Testcontainers.Configurations;` is dropped, because `TestcontainersSettings` was the only thing either file took from that namespace.

## Expected test count

**1321.** 1322 observed on your machine, one of them failing. `TheLoggerIsSilencedBeforeTheFirstContainerIsBuilt` is deleted and nothing is added, so one `[Fact]` leaves and none arrives. §16.4's paragraph for the class moves from 2 assertions to 1 in the same slice, which is the gate your removal tripped.

Any other number is worth investigating before reading assertion text.

## What the failure was

`Testcontainers` moved logger configuration off the static `TestcontainersSettings.Logger` and onto the builder before 4.14.0, the version `Directory.Packages.props` pins. Your diagnosis and your fix were both right. What was left was the gate, which asserted the assignment your fix had to delete — and its second fact asserted the *position* of that assignment, which has no meaning once the logger is a per-builder value rather than a global setting.

So the ordering fact is **deleted, not reworded**: a rule whose subject has ceased to exist is deleted rather than weakened into something that cannot fail (F-41). That was your instinct too; what stopped it being the fix was the §16.4 arithmetic, which this slice moves in the same commit.

## What the surviving fact now says

One `[Fact]`. Every `new PostgreSqlBuilder(` under `tests/` is walked forward to its `.Build()`, and a `.WithLogger(` must appear on that chain. Three consequences:

- A fixture with two builders and one silencer now **fails**. The old file-level `Contains` passed it.
- A builder with no `.Build()` after it is **reported** rather than treated as satisfied — the scan says it cannot decide instead of deciding permissively.
- It keys on the call, not on the value, so Slice 69's veto point 1 survives: swap `NullLogger.Instance` for a warning-threshold `ILogger` and this stays green.

## What was verified, and how

| Claim | How |
|---|---|
| `TestcontainersSettings` has no `Logger` at 4.14.0 | Read `src/Testcontainers/Configurations/TestcontainersSettings.cs` at tag `4.14.0`. Also absent at 4.13.0 and 4.12.0. |
| `WithLogger` is callable on `PostgreSqlBuilder` | `public TBuilderEntity WithLogger(ILogger logger)` on `AbstractBuilder`, implicit implementation of `IAbstractBuilder`, so no using directive beyond what the fixtures already carry. |
| An explicit `WithLogger` actually displaces something | `AbstractBuilder.Init()` seeds every builder with `ConsoleLogger.Instance`. That is the noise. |
| The rewritten gate passes the delivered tree | Emulated in Python over the same walk and the same markers. |
| It is sensitive | Five planted defects, all reported: silencer deleted from the fixture; deleted from the harness instead; a second unsilenced builder added beside the silenced one; `.Build()` renamed away; and the pre-slice tree with the static assignment in both files, which reproduces your failure. |
| The second-builder defect is one the old shape missed | Checked directly: a file-level `Contains` passes that tree. |

**Not verified:** nothing was compiled — no .NET SDK here, so 1321 is arithmetic off your observed 1322 and the rewritten gate has not run as C#. The API facts were read from the library's source at tag 4.14.0, not by compiling against the restored package.

## Gates emulated against the edited tree

| Gate | Result |
|---|---|
| §16.4 counted-class census | 47 counted, floor 37, no disagreement, no ambiguity, no unresolved citation |
| `SpecificationVersionTests` | header 1.55 = newest entry 1.55, history descends |
| `MarkdownTableContractTests` | no malformed run of table lines in any edited document |
| `ContextDumpExclusionContractTests` | every archived document still linked by exact path |
| `SourceCommentContractTests` | no comment in any of the three C# files |
| Byte hygiene | LF only, one final newline, no trailing whitespace, all eight files |
| `Directory.Packages.props` | parses as XML; no doubled hyphen inside a comment |

## Veto points

**1. The ordering fact is gone rather than reformulated.** A per-chain formulation of it does exist — `.WithLogger(` must precede `.Build()` — but that is now *inside* the surviving fact rather than beside it, because the two cannot fail independently: a chain with the call after `Build` is the same string as a chain without the call before it. To have two facts again you would need a defect shape that satisfies one and not the other, and there isn't one. To reverse, split the walk into presence and position and take §16.4 back to 2 assertions and the count to 1322.

**2. `Directory.Packages.props` loses its resolution date.** The file opened by claiming every pin was the latest stable release as resolved on 2026-08-02, and three annotations further down already contradicted it. Deleted on F-77 rather than re-dated, since the next bump falsifies any date written there. To keep a date instead, restore the sentence and add the bump date to the release procedure in `docs/OPERATIONS.md` §14 so something moves it.

**3. The gate's marker is still a string the compiler does not hold.** Referencing `Testcontainers.PostgreSql` from `MyRestaurant.WebApplication.Tests` would let the marker be `nameof(PostgreSqlBuilder.WithLogger)`, and this exact failure could then not recur. Rejected: it fails in the same build as the fixtures already do, so it buys a redundant check rather than an earlier one, and it puts Docker.DotNet and SSH.NET into an assembly that starts no container. Recorded as an open residual instead, with the §18 habit in its place. To reverse, add the package reference and build the marker from `nameof`.
