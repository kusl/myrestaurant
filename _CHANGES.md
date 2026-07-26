# M4 Slice 1 — the order engine: the §6.6 transaction, the projections, and the menu read side

Every file below is a **full file** at its **repo-relative path**. Extract this archive at the repo root
and the contents drop straight over your working tree. `git status` will show exactly these 14 files as
modified/added (15 counting this one).

```bash
tar -xzf m4-slice1-order-engine.tar.gz -C /home/kushal/src/dotnet/myrestaurant
```

## Files to DELETE

**None.** One file is an in-place edit (`Program.cs`); everything else is new.

The one file that does not belong in the tree afterwards is `docs/BUILD_PROGRESS.append.md`, which exists
only to be appended and then removed — see the last section.

## Before you extract: two files this archive deliberately does *not* touch

- **`tests/MyRestaurant.DataAccess.Tests/Displays/DisplayDevicePairingTests.cs`** — you already fixed the
  `Assert.DoesNotContain(':', …)` build break yourself. Overwriting your fix with mine would be a
  needless risk, so it is not in the archive.
- **`src/MyRestaurant.WebApplication/Program.cs`** — this *is* in the archive, rebuilt from the dump you
  gave me plus two additions (a `using` and one `AddRestaurantOrders();` call with its comment). If you
  have edited `Program.cs` since that dump, extract everything else and apply those two changes by hand
  instead; they are quoted in full at the end of this file.

## New files (13)

### Code — DataAccess (5)

- `src/MyRestaurant.DataAccess/Menu/MenuDirectory.cs`
  `MenuItemSummary`, `IMenuDirectory`/`DapperMenuDirectory` (§7). Read side only; returns inactive items.
- `src/MyRestaurant.DataAccess/Orders/OrderEventVocabulary.cs`
  The §6.2 enum ↔ SQL-string mapping, both directions, in one place. `internal`.
- `src/MyRestaurant.DataAccess/Orders/OrderEventLog.cs`
  `IOrderEventLog`/`DapperOrderEventLog` plus the shared `OrderEventReader` the transaction also uses.
- `src/MyRestaurant.DataAccess/Orders/OrderReadModel.cs`
  The four §8.3 views behind `IOrderReadModel`/`DapperOrderReadModel`, with their view records.
- `src/MyRestaurant.DataAccess/Orders/OrderMutations.cs`
  `IOrderMutations`/`DapperOrderMutations` — the §6.6 locking protocol, and the largest file here.

### Code — WebApplication (2)

- `src/MyRestaurant.WebApplication/Orders/OrderWorkflow.cs`
  `IOrderWorkflow`/`OrderWorkflow` — §12 counters and §9 broadcasts, after commit.
- `src/MyRestaurant.WebApplication/Orders/OrdersServiceCollectionExtensions.cs`
  `AddRestaurantOrders()` — the five services.

### Tests (6)

- `tests/MyRestaurant.DataAccess.Tests/Orders/OrderTestWorld.cs`        (shared seeding helper, no facts)
- `tests/MyRestaurant.DataAccess.Tests/Orders/OrderMutationsTests.cs`   (Testcontainers, 11 facts)
- `tests/MyRestaurant.DataAccess.Tests/Orders/OrderReadModelTests.cs`   (Testcontainers, 5 facts — §8.5 lives here)
- `tests/MyRestaurant.DataAccess.Tests/Menu/MenuDirectoryTests.cs`      (Testcontainers, 3 facts)
- `tests/MyRestaurant.WebApplication.Tests/Orders/OrderWorkflowTests.cs` (7 facts/theories, no container)
- `tests/MyRestaurant.WebApplication.Tests/Orders/OrdersWiringTests.cs`  (5 facts, no container)

## Edited — code (1)

- `src/MyRestaurant.WebApplication/Program.cs`
  Adds `using MyRestaurant.WebApplication.Orders;` and `builder.Services.AddRestaurantOrders();` after
  `AddRestaurantDisplays()`. Nothing else changes — no pipeline change, no new middleware.

## Docs (1, append-then-delete)

`docs/BUILD_PROGRESS.md` is large, so it is not regenerated here. The new section ships separately:

```bash
cat docs/BUILD_PROGRESS.append.md >> docs/BUILD_PROGRESS.md && rm docs/BUILD_PROGRESS.append.md
```

No `docs/TECHNICAL_SPECIFICATION.md`, `docs/REQUIREMENTS.md`, or ADR edit: this slice realizes behaviour
§6, §7, §8.3, §8.5, §9, §10.1, and §12 already specify. No migration — every table and view involved
ships in `0001_initial_schema.sql`. No new packages.

## Three decisions worth knowing before you read the diff

**No Razor at all.** M4's visible half is three surfaces, and all three are renderings of one transaction
and two projections. Building a staging area on an untested write path means debugging components and
row-level locking simultaneously; this slice lands the engine green so the next one is only presentation.

**The server prices every line-add, staff edits included.** §6.5.4 only says it for guest submissions,
but the menu is the price authority for an *add*, and a counter who means to charge something else has
`price_adjustment` — which demands a reason and shows old → new on the bill. Veto this if you disagree;
it is one branch in `ApplyServerSideValues`.

**§8.5 equivalence is asserted on the line *set*, not the row order.** Lines added in one send share an
`occurred_at` to the microsecond, and the two tie-breakers cannot agree: the fold's `ThenBy(Guid)` uses
.NET's `Guid.CompareTo` (Data1 as an `int`, then two `short`s, then bytes) while the view's `ORDER BY`
uses PostgreSQL's bytewise `uuid` collation. Both are stable, neither is wrong, and §8.5's wording — "the
line set, prices, and fulfillment flags" — never claimed an ordering. Same reasoning applies to
operations within one event, which is why `OrderEventLog`'s doc comment now says its deterministic read
order is not an insertion order.

## The one-line why

An order is not a row that gets edited — it is a log that gets appended to under two locks, and this is
that append: one transaction that takes the sitting `FOR SHARE` and the order `FOR UPDATE`, prices every
line from the menu rather than from the client, validates all nine §6.5 invariants against the log it
just read, writes the event and the kitchen's alert together or writes neither, and hands back both the
committed projection and — when it refuses — a reason per operation and a fresh projection to restage
from.

## If you need to patch `Program.cs` by hand

Add to the `using` block, in alphabetical position:

```csharp
using MyRestaurant.WebApplication.Orders;
```

And immediately after the `builder.Services.AddRestaurantDisplays();` line:

```csharp
// Menu (read side) and orders (§6, §7, §8.3, §9, §12): the IMenuDirectory the staging area and the "86"
// panel read; IOrderMutations, the single transaction implementing the §6.6 locking protocol;
// IOrderReadModel over the §8.3 projection views and IOrderEventLog over the raw event log; and
// IOrderWorkflow, the post-commit shell that records the §12 counters and publishes the §9 notifications
// — surfaces call that, never IOrderMutations directly, or a send would never reach the kitchen. Last of
// the four groups because an order hangs off a sitting, which AddRestaurantTables registered above.
builder.Services.AddRestaurantOrders();
```
