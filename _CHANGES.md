# Slice 76 — the first run, and the sum it counted differently

Extract at the repository root. Spec goes to **v1.61**. Findings floor moves to **F-132**.

## Files in this archive

| Path | State |
|---|---|
| `tests/Shared/PostgreSqlTestServer.cs` | changed — takes the database name; no shared-database method, no `max_connections`; a first timeout fails later starts at once |
| `tests/MyRestaurant.DataAccess.Tests/PostgreSqlFixture.cs` | changed — starts and disposes its own server |
| `tests/MyRestaurant.DataAccess.Tests/MyRestaurant.DataAccess.Tests.csproj` | changed — comment only |
| `tests/MyRestaurant.EndToEnd.Tests/Harness/RestaurantHarness.cs` | changed — names its database; reads `ConnectionString` |
| `src/MyRestaurant.WebApplication/Components/Account/Pages/SignIn.razor` | changed — `await _editContext.ValidateAsync()` |
| `.github/workflows/ci.yml` | changed — two comments |
| `README.md` | changed — one paragraph in *Testing* |
| `docs/TECHNICAL_SPECIFICATION.md` | changed — header, §2, §16.2, Appendix A, changelog |
| `docs/DOCUMENTATION_REVIEW.md` | changed — F-130 corrected, F-132 added, one discharged residual deleted |
| `docs/BUILD_PROGRESS.md` | changed — Slice 75 corrected, the Slice 76 row and narrative |
| `_CHANGES.md` | changed (this file) |

**Files to DELETE: one.**

```bash
git rm tests/MyRestaurant.DataAccess.Tests/PostgreSqlServerFixture.cs
```

It calls a method this slice removes from `PostgreSqlTestServer`, so leaving it is a build error rather than a leftover.

**Files to ADD: none.**

## Expected test count

**1332 — unchanged**, and this time expected to be 1332 passed. §16.4's census stays at **49** over a floor of 37. End-to-end scenarios stay at **21**.

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

Nothing was compiled here. `ValidateAsync()` is assumed to return `Task<bool>`. Thirty-four servers starting together on Podman 6 is inferred from three doing so in your run, not observed.

## Veto points

1. **`ValidateAsync()`.** If its shape differs, the build error will be on `SignIn.razor` line 120 and will name the real signature.
2. **Sharing a server** is now forbidden by §16.2. Reopening it means measuring the pools first, in a slice of its own.
