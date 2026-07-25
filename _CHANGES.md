# M3 Slice 3 — join grant, `/table/{id}` routing, sitting open + membership

Every file below is a **full file** at its **repo-relative path**. Extract this archive at the repo
root and the contents drop straight over your working tree. `git status` will show exactly these
12 files as modified/added (13 counting this one).

## Files to DELETE

**None.** Every change is an in-place edit or a new file. Nothing is removed or renamed.

The one file that does not belong in the tree afterwards is `docs/BUILD_PROGRESS.append.md`, which
exists only to be appended and then removed — see the last section.

## New files (7)

### Code (5)

- `src/MyRestaurant.DataAccess/Sittings/SittingDirectory.cs`
  `TableSittingSummary`, `SittingMemberSummary`, `ISittingDirectory`/`DapperSittingDirectory` (§5.1, §5.2).
- `src/MyRestaurant.DataAccess/Sittings/SittingMembership.cs`
  `JoinTableOutcome`, `JoinTableResult`, `ISittingMembership`/`DapperSittingMembership` (§4.4, §5.1).
- `src/MyRestaurant.WebApplication/Tables/JoinGrant.cs`
  `JoinGrant`, `JoinGrantProtector`, `JoinGrantCookie` (§4.4).
- `src/MyRestaurant.WebApplication/Identity/PersonPrincipal.cs`
  Reads the person identifier off a `ClaimsPrincipal` (§3.1).
- `src/MyRestaurant.WebApplication/Components/Pages/Table/TableJoin.razor`
  `/table/{TableId:guid}` — anonymous, static SSR, the whole §4.4 flow plus the member surface.

### Tests (2)

- `tests/MyRestaurant.DataAccess.Tests/Sittings/SittingMembershipTests.cs`  (Testcontainers, 9 facts)
- `tests/MyRestaurant.WebApplication.Tests/Tables/JoinGrantTests.cs`        (8 facts, no container)
- `tests/MyRestaurant.WebApplication.Tests/Identity/PersonPrincipalTests.cs` (6 facts, no container)

## Edited — code (3)

- `src/MyRestaurant.WebApplication/Tables/TablesServiceCollectionExtensions.cs`
  Registers `ISittingDirectory`, `ISittingMembership` (scoped) and `JoinGrantProtector` (singleton).
- `src/MyRestaurant.WebApplication/Components/Pages/Table/TableArea.razor`
  `/table` becomes the "your open sittings" index; still interactive-server.
- `src/MyRestaurant.WebApplication/Program.cs`
  **Comment only.** The `AddRestaurantTables()` note now covers sittings and the grant protector.

## Edited — tests (1)

- `tests/MyRestaurant.WebApplication.Tests/Tables/TablesWiringTests.cs`
  Adds an identifier factory and an ephemeral Data-Protection provider to the test container, plus
  three resolvability facts (`ISittingDirectory`, `ISittingMembership`, `JoinGrantProtector`).

## Docs (1, append-then-delete)

`docs/BUILD_PROGRESS.md` is ~65 KB, so it is not regenerated here. The new section ships separately:

```bash
cat docs/BUILD_PROGRESS.append.md >> docs/BUILD_PROGRESS.md && rm docs/BUILD_PROGRESS.append.md
```

No `docs/TECHNICAL_SPECIFICATION.md`, `docs/REQUIREMENTS.md`, or ADR edit: this slice realizes
behaviour §4.4, §5.1, and §9 already specify. No migration (`table_sitting` and `table_sitting_member`
ship in `0001_initial_schema.sql`). No new packages.

## The one-line why

Scanning a table's QR and joining its party are separated by a sign-in the guest usually has not done
yet, and by the time they come back the rotating token has rotated away. The join grant is the
short-lived, encrypted, table-scoped, single-use proof-of-scan that survives that detour — and
consuming it opens the sitting and inserts the membership in one locked transaction, so two guests
scanning the same display in the same second end up at the same table rather than racing the
one-open-sitting-per-table index.
