# Slice 69 — one CHECK per probe, and the proof that was written rather than run

Extract at the repository root. Spec goes to **v1.54**. Findings floor **F-125**.

## Files in this archive

| Path | State |
|---|---|
| `tests/MyRestaurant.WebApplication.Tests/Deployment/ContainerLoggingContractTests.cs` | **new — needs `git add`** |
| `tests/MyRestaurant.DataAccess.Tests/Menu/MenuItemCommentTests.cs` | changed |
| `tests/MyRestaurant.DataAccess.Tests/PostgreSqlFixture.cs` | changed |
| `tests/MyRestaurant.EndToEnd.Tests/Harness/RestaurantHarness.cs` | changed |
| `docs/TECHNICAL_SPECIFICATION.md` | changed |
| `docs/DOCUMENTATION_REVIEW.md` | changed |
| `docs/BUILD_PROGRESS.md` | changed |
| `docs/MENU_AND_HANDHELD_PLAN.md` | changed |
| `_CHANGES.md` | changed (this file) |

**Nothing is deleted in this slice.** No production code changed: `0009_menu_item_comments.sql` and `Menu/MenuItemComments.cs` are untouched, and the schema they declare was correct all along.

## Expected test count

**1322.** 1320 + 2 (`ContainerLoggingContractTests`). `MenuItemCommentTests` still holds thirteen `[Fact]` — the fifth probe and the catalogue comparison are assertions inside the existing schema fact, so §16.4's count for that class does not move.

Any other number is worth investigating before reading assertion text.

## What was verified, and how

| Claim | How |
|---|---|
| Each of the five probe rows breaks exactly one CHECK | Executed against a real PostgreSQL carrying `0009`'s exact DDL. Five probes, five distinct constraint names. |
| Each of the four control rows inserts | Same run, inside a transaction, rolled back; row count zero afterwards. |
| The reported failure reproduces | Same run: `('loved', 'Anything')` reports `menu_item_comment_event_body_payload`, exactly as on your machine. |
| `pg_constraint` returns the four names in that order | Same run. |
| `ContainerLoggingContractTests` is sensitive | Emulated against the edited tree and two planted defects: assignment deleted (fact 1 reports the file), assignment moved below the builder (fact 1 passes, fact 2 reports it). |

**Not verified:** nothing was compiled — no .NET SDK in the authoring environment, so 1322 is arithmetic. The PostgreSQL used was 16, not the pinned 17. The contract test was emulated in Python against the same file contents rather than run as C#.

## Gates emulated against the edited tree

| Gate | Result |
|---|---|
| §16.4 counted-class census | 47 counted, floor 37, no disagreement, no ambiguity, no unresolved citation |
| `SpecificationVersionTests` | header 1.54 = newest entry 1.54, history descends |
| `MarkdownTableContractTests` | no malformed run of table lines in any edited document |
| `ContextDumpExclusionContractTests` | every archived document still linked by exact path |
| Byte hygiene | LF only, one final newline, no trailing whitespace, all nine files |
| No comment in authored source | none in any of the four C# files |
| `ContainerImageReferenceContractTests` | unchanged: the new file declares no `*Image` constant |

## Veto points

**1. The Testcontainers logger is silenced rather than thresholded.** `NullLogger.Instance` drops Warning and Error as well as the Information chatter. The argument for it is that a container failure here is diagnosed by `DescribeFailure`, which is several sentences and better than any log line the library emits. To keep warnings instead, replace `NullLogger.Instance` in both fixtures with a small `ILogger` whose `IsEnabled` returns `logLevel >= LogLevel.Warning`; `ContainerLoggingContractTests` keys on the assignment, not on what is assigned, so it stays green either way.

**2. The catalogue comparison lists the four constraint names.** Subjects are computed elsewhere in this tree (F-47, F-58); here the *actual* set is computed and the expected set is written down, which is the shape of a census rather than a list. To reverse it, drop the `declared` array and the `Assert.Equal` beneath it; the five probes still cover the four constraints, but a fifth CHECK could then shadow one of them without anything turning red.

**3. Three findings ride in one slice.** They fail in three distinguishable places — one DataAccess integration assertion, two WebApplication source-scan assertions, and one documentation correction that cannot fail. To split, F-125 and its test are wholly separable: revert `PostgreSqlFixture.cs`, `RestaurantHarness.cs`, the new test file, and the `ContainerLoggingContractTests` paragraph in §16.4, the F-125 rows in Appendix A and the ledger, and take the census back to 46 and the count back to 1320.
