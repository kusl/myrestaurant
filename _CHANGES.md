# Slice 67 — the reading that was two instants

Extract at the repository root. Every path in this archive is repository-relative and every file is
complete; nothing here is a patch.

**No production code changed.** Every edit is under `tests/` or `docs/`.

## What landed

| Change | Measure |
|---|---|
| `KitchenJourneys.ReadBoardAsync` reads the whole board in one `EvaluateAsync` | 5 round trips → 1 |
| `TableOrderJourneys.ReadBasketAsync` reads the whole basket in one `EvaluateAsync` | 3 round trips → 1 |
| `HarnessSnapshotContractTests` added — a composite a predicate is asked about is read in one evaluation | 2 assertions |
| `TECHNICAL_SPECIFICATION.md` to v1.52 | §16.4, changelog |
| The changelog's "ten most recent" deleted — it had drifted to eleven and nothing checks it | F-77 |

**Predicted test count: 1302 + 2 = 1304.** Any other number is the first thing to investigate.

## Files to delete

```
(none)
```

Nothing is removed or renamed.

## Files that need `git add`

Untracked files are invisible to every gate that uses `git ls-files`.

```
tests/MyRestaurant.WebApplication.Tests/HarnessSnapshotContractTests.cs
```

## Veto points

**The basket repair rides along.** It is the same defect in the same shape, and the gate's subject set
covers it, so repairing only the kitchen board would leave the gate red. To take the kitchen board
alone: restore the three-`CountAsync` body of `ReadBasketAsync`, drop `UnavailableMarkSelector` and
`CountingScript`, and narrow `HarnessSnapshotContractTests` — but the flake would remain latent in
scenario 7.

**Collection-valued predicates are out of scope.** `Func<IReadOnlyList<T>, bool>` is excluded by the
regex `Func<(\w+)\??,\s*bool>` in `HarnessSnapshotContractTests`, which cannot match a generic
argument. To bring them in, widen that pattern — and expect `ReadMenuAsync`, `ReadOwnLinesAsync`,
`ReadRosterAsync` and `ReadPartyAsync` to be reported.

**The `ScreenText.DeclaredAsync(` entry in `BrowserReads`.** It counts an indirect read. No subject
method uses it today; remove the entry if that is judged too broad.

## What was NOT verified

Nothing was compiled, no test was run, and no browser was opened. The two rewritten readers have not
executed against a real page. The new gate was emulated mechanically against the edited tree, not
executed. The flake cannot be shown fixed by a green run, because it never failed on demand.
