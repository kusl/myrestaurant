# Slice 76 — the first run, and the sum it counted differently

Extract at the repository root. Spec goes to **v1.61**. Findings floor moves to **F-132**.

## Files in this archive

This archive is the delta against commit `934669a` (your tree as of the last dump, which matched what was delivered byte for byte).

| Path | State |
|---|---|
| `tests/MyRestaurant.EndToEnd.Tests/Harness/DisplayJourneys.cs` | changed — `PairAsync` waits for the URL to commit and the table surface to attach; the catch also takes `TimeoutException` |
| `docs/BUILD_PROGRESS.md` | changed — the Slice 76 narrative records both runs |
| `docs/DOCUMENTATION_REVIEW.md` | changed — one residual |
| `_CHANGES.md` | changed (this file) |

**Files to DELETE: none. Files to ADD: none.**

## Expected test count

**1332 — unchanged**, and this time expected to be 1332 passed. §16.4's census stays at **49** over a floor of 37. End-to-end scenarios stay at **21**.

## What the latest run showed

- `dotnet test`: **1332 of 1332** on rc3 and Podman 6.1.3.
- `scripts/ci_local.sh --with-all --with-e2e`: all nine gates green, strict build with no warnings, no outdated or vulnerable packages.
- The end-to-end suite passed in both of those. The last stand-alone end-to-end run failed one scenario: pairing a display timed out waiting for the browser's `load` event after the page had already arrived. That is the one code change here.

## What the Slice 75 run showed

Your tree matched the Slice 75 archive byte for byte, so nothing below is an extraction mistake.

- It compiled on `11.0.100-rc.3`, `closed record class` included.
- The hang is gone: the data-access assembly finished in 26 seconds on Podman 6, and all 21 end-to-end scenarios passed in both runs.
- **132 failures, one cause:** `53300: sorry, too many clients already`. The single shared server was my change and it was wrong — every class leaves connection pools behind, and they added up past the server's limit.
- **`ci_local.sh` stopped at the strict build:** `CS0618`, `EditContext.Validate()` is obsolete in .NET 11. A warning in a plain build, an error in CI.
- The `NETSDK1005` errors at the top are `dotnet clean` running before `dotnet restore` on a tree whose target framework had just changed. They are not a defect and will not recur.

## What landed

**One server per test class again.** That is the arrangement that ran green before; the probe from the host, the five-minute deadline and the single builder from Slice 75 all stay. A first timeout now fails every later start immediately, so a dead engine costs one deadline rather than one per class.

**`SignIn.razor`** awaits `ValidateAsync()`.

## Not verified

The pairing change has not run. Why the `load` event never came is not known; the other journeys that wait the same way have never failed and are left alone.

## Veto points

1. **The pairing wait.** To reverse, put `PageWaitForURLOptions { Timeout = 30_000 }` back and delete the surface wait; the scenario's own `WaitForLiveSurfaceAsync` is unaffected either way.
2. **Sharing a server** is now forbidden by §16.2. Reopening it means measuring the pools first, in a slice of its own.
