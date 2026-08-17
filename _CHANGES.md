# M6 Slice 45 — the tie-break that was a coin flip

Extract at the repository root. Every path is repo-relative and every file is complete.

```
tar -xzf m6-slice-45-monotonic-identifiers.tar.gz
git status
```

**Files to DELETE: none.**

**`git add` is NOT required.** Every file in this archive already exists in the tree and is tracked. No
file is new, no directory is new, and `scripts/check_tree.sh` walks `git ls-files`, so there is nothing here
it cannot already see.

**No schema change, no migration, no read changed, no surface changed.** `0005` is untouched and no
migration is added. No SQL text is edited — the two changes to `MenuEventLog.cs` are comments. No Razor
component is in this archive at all. No new package, no `compose.yaml` edit, no `.slnx` edit, no
`REQUIREMENTS.md` edit.

---

## What this is

Your first `dotnet test` reported one failure that the `ci_local.sh` run then passed:

```
MenuEventLogTests.ListRecent_IsNewestFirstAcrossItemsAndRespectsTheCap
Expected: "section_changed"   Actual: "created"
```

Not a flake. `Guid.CreateVersion7()` is `Guid.NewGuid()` with the 48-bit millisecond timestamp written over
the top and the version and variant nibbles set — verified against `dotnet/runtime` `release/10.0`. The other
74 bits stay random, so it is ordered *between* milliseconds and **unordered within one**: 49.8% of
same-millisecond pairs invert under PostgreSQL's `uuid` comparison.

Every mutation in §8 stamps all the rows of its transaction with one `IClock.UtcNow`, so nine reads plus
`OrderProjection` order by an instant and break the tie on an identifier — arbitrarily, until now.

- §11.4's per-item history read a three-event create in the minted order **one time in six**.
- The section history did the same one register up.
- The activity feed you shipped last slice reordered itself between page loads on unchanged data.
- **A guest's basket order did not survive being sent.** `OrderStaging.Build` mints line identifiers in a
  tight loop *in basket order*; `OrderProjection` breaks its line tie on `order_line_identifier`; every line
  of one send shares its instant. Guest surface, kitchen ticket, and bill.

Fixed in one file: monotonicity becomes a contract of `IIdentifierFactory`, kept by a 12-bit counter in
`rand_a` (RFC 9562 §6.2 method 1) advanced by compare-and-swap over one process-wide `long`.

---

## Files in this archive

| Path | What changed |
|---|---|
| `src/MyRestaurant.Domain/Identifiers/IIdentifierFactory.cs` | `Create()` gains the ordering contract, and the note that the relation is PostgreSQL's rather than `Guid.CompareTo`'s |
| `src/MyRestaurant.Domain/Identifiers/UuidV7IdentifierFactory.cs` | The counter, the compare-and-swap, and both edge cases |
| `src/MyRestaurant.DataAccess/Menu/MenuEventLog.cs` | **Comments only.** Two ordering claims repointed from the format to the contract |
| `tests/MyRestaurant.Domain.Tests/UuidV7IdentifierFactoryTests.cs` | 2 assertions → **7** |
| `tests/MyRestaurant.WebApplication.Tests/Documentation/TestingSectionContractTests.cs` | `MinimumCountedClasses` 19 → **20**, and the doc paragraph beside it |
| `docs/TECHNICAL_SPECIFICATION.md` | **v1.30.** §8.1 requirement, §16.4 paragraph, Appendix A F-95, changelog |
| `docs/adr/0011-uuidv7-application-generated.md` | Amended: the counter, both edges, and why `Guid.NewGuid()` is still right for stamps and tokens |
| `docs/DOCUMENTATION_REVIEW.md` | F-95 row, status line, and the narrative on probabilistic evidence |
| `docs/BUILD_PROGRESS.md` | Slice 45 |
| `docs/MENU_AND_HANDHELD_PLAN.md` | **Stage 3a** — the resequencing verb, fully specified, recorded as unblocked |
| `_CHANGES.md` | This file |

---

## Test count

**1157 → 1162.** One term: `UuidV7IdentifierFactoryTests` goes from 2 `[Fact]` methods to 7. Nothing else
gains or loses a test method; §16.3 stays at 17 scenarios.

If the run returns anything other than 1162, check that file's `[Fact]` count first — it is the only term in
the sum *and* the number §16.4 now claims, so a miscount there fails twice.

---

## What was NOT verified — read this before running it

**Nothing was compiled and nothing was run.** No .NET SDK in that environment and no reachable package feed.
The ordering proof is a byte-accurate model plus a transliteration of the shipped arithmetic, not
`MyRestaurant.Domain.dll`. Per §18 an uncompiled archive is a prediction.

**The original failure was 50%, so one green run is not evidence.** Please run the DataAccess suite a few
times:

```
for i in 1 2 3 4 5; do dotnet test tests/MyRestaurant.DataAccess.Tests --filter MenuEventLogTests; done
```

**`Create_MintsDistinctSortKeysUnderConcurrency` uses `Parallel.For`,** and a compare-and-swap loop's first
real test is its first real run.

**The guest-basket consequence has no test in this slice.** `OrderProjection` is now correct and nothing
asserts a multi-line send reads back in basket order. Named in *Still open* rather than quietly closed.

---

## Veto points

**The reordering verb is deliberately not here**, though it is the named open menu cut and you asked for menu
progress. A resequence writes several `reordered` events in one transaction at one instant — which *is* this
slice's property — so its ordering test would have been the first test of this fix, and a red run could not
have said which change caused it. Stage 3a carries the full design so the next slice is arrangement, not
design. **If you would rather have had both in one archive, say so and I will fold them together.**

**`UuidV7IdentifierFactory`'s state is `static`.** Deliberate: the guarantee is about the process's stream,
and two instances each ascending independently would satisfy an instance field while interleaving wrongly.
To make it per-instance instead, drop `static` from `_lastSortKey` and `NextSortKey`, and delete
`Create_AscendsAcrossTwoInstances` — the singleton registration in `Program.cs` would still be correct.

**Four comments were repointed rather than deleted**, which is a departure from F-77's delete-the-number
ruling. The reasoning: F-77 is about a *count* nothing can check, and this was a *claim* that was right about
the mechanism and wrong about who provided it — so it now names the contract, which a test can fail. If you
prefer them simply deleted, they are the two `///` and `//` blocks in `MenuEventLog.cs`.

**`AdministrationMenu.razor` is not in this archive.** Its `@key` comment claims that two same-instant events
are ordered by their identifiers, which this slice makes true, so it needed no edit — and your
`menuSectionSummary` rename stays exactly as you made it.
