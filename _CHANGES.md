# M5 Slice 2 — the menu: create, rename, reprice, and the history that explains a price

Every file below is a **full file** at its **repo-relative path**. Extract this archive at the repo root
and the contents drop straight over your working tree. `git status` will show exactly these 17 files as
modified/added, plus one deletion.

```bash
tar -xzf m5-slice2-menu.tar.gz -C /home/kushal/src/dotnet/myrestaurant
```

## Files to DELETE

**One.** `MenuAvailabilityWorkflow` is renamed to `MenuWorkflow`, so its old file must go or the build will
see two classes registering for the same interface:

```bash
git rm src/MyRestaurant.WebApplication/Menu/MenuAvailabilityWorkflow.cs
```

`Program.cs` is untouched, no migration ships, and nothing else is superseded.

## Slice 1's three failing tests are fixed in here

`dotnet test` on the tree as exported reported **787 total, 3 failed, 769 passed, 15 skipped**. All three
were in Slice 1's new tests, not in the code they cover, so per the fix-vs-continue policy they are folded
in rather than shipped separately:

| Test | Was | Now |
|---|---|---|
| `CounterBoardReadsTests.ListOpenSittings_RollsUpTheTableTheGuestsTheLinesAndTheMoney` | `AddPersonAsync("bo", …)` — 2 chars, against `person.username`'s `CHECK (char_length BETWEEN 3 AND 64)` | `"bode"` |
| `SittingSettlementTests.CloseAndSettle_TotalsEveryMembersOrderNotJustTheFirst` | same `"bo"` | `"bode"` |
| `SittingSettlementTests.CloseAndSettle_HonoursPriceAdjustmentsAndDropsRemovedLines` | asserts `PendingLineCountAtClose == 0` | asserts `1` |

That third one is worth a sentence, because the implementation is right and the test was wrong. The removed
steak leaves `order_current_line` entirely, so it is neither charged for nor counted. The soup was only
**repriced** — nothing fulfilled it — so it was still with the kitchen at the instant the total was stamped.
Adjusting a price is not the same act as passing the plate, and `PendingLineCountAtClose` reports what was
actually outstanding. The assertion moved; `DapperSittingSettlement` did not.

## What this closes

§19's M5 line reads "bills, price adjustment, close & settle, end-of-day, counter fallback QR, **menu
management + events**, event explorer, hide/unhide, post-close corrections". This is the emphasised part.

Until now the only menu *write* in the system was the kitchen's 86 toggle — an administrator could not put
a dish on the menu at all, and every demo's two items were inserted by hand.

## New files (10)

### Code — DataAccess (2)

- `src/MyRestaurant.DataAccess/Menu/MenuAdministration.cs`
  `RenameMenuItemOutcome`, `RepriceMenuItemOutcome`, `CreateMenuItemResult`, `RenameMenuItemResult`,
  `RepriceMenuItemResult`, `IMenuAdministration`/`DapperMenuAdministration` — §7's create, rename, and
  reprice. One connection and transaction per operation, the row taken `FOR UPDATE` before it is compared,
  the `menu_item` change and its `menu_item_event` written together.
- `src/MyRestaurant.DataAccess/Menu/MenuEventLog.cs`
  `MenuItemEventEntry`, `IMenuEventLog`/`DapperMenuEventLog` — the complete per-item history (uncapped,
  oldest first) and a capped cross-item activity feed.

### Code — WebApplication (1)

- `src/MyRestaurant.WebApplication/Menu/MenuWorkflow.cs`
  `IMenuWorkflow` grows create/rename/reprice beside the existing 86; `MenuWorkflow` replaces
  `MenuAvailabilityWorkflow` (delete that file). One post-commit shell over both write services.

### Surfaces (3)

- `src/MyRestaurant.WebApplication/Components/Pages/Administration/AdministrationMenu.razor` — `/administration/menu`
- `src/MyRestaurant.WebApplication/Components/Pages/Administration/CreateMenuItem.razor` — `/administration/menu/new`
- `src/MyRestaurant.WebApplication/Components/Pages/Administration/ManageMenuItem.razor` — `/administration/menu/{MenuItemId:guid}`

### Tests (3)

- `tests/MyRestaurant.DataAccess.Tests/Menu/MenuAdministrationTests.cs`  (Testcontainers, 15 facts)
- `tests/MyRestaurant.DataAccess.Tests/Menu/MenuEventLogTests.cs`        (Testcontainers, 9 facts)
- `tests/MyRestaurant.WebApplication.Tests/Menu/MenuWiringTests.cs`      (8 facts, no container)

### Docs (1, append-then-keep)

`docs/BUILD_PROGRESS.md` is large and is not regenerated. The new section ships as
`docs/_append/BUILD_PROGRESS-m5-slice-2.md`, matching the sections already in that folder:

```bash
cat docs/_append/BUILD_PROGRESS-m5-slice-2.md >> docs/BUILD_PROGRESS.md
```

## Edited (6)

- `src/MyRestaurant.WebApplication/Orders/OrdersServiceCollectionExtensions.cs`
  Two new registrations (`IMenuAdministration`, `IMenuEventLog`) and the workflow's new type. Chosen over a
  new `AddRestaurantMenu()` so `Program.cs` needs no edit — the extension has wired the menu since M4,
  because an order prices itself from it (§6.5.4).
- `src/MyRestaurant.WebApplication/Components/Pages/Administration/AdministrationHome.razor`
  A Menu link in the header actions. Nothing else changes.
- `src/MyRestaurant.WebApplication/Components/Pages/Administration/AdministrationTables.razor`
  The same, from the other side.
- `tests/MyRestaurant.WebApplication.Tests/Orders/KitchenWiringTests.cs`
  `Assert.IsType<MenuAvailabilityWorkflow>` → `Assert.IsType<MenuWorkflow>`, plus a line of doc explaining
  the rename. This is the only file that names the old class.
- `tests/MyRestaurant.DataAccess.Tests/Sittings/CounterBoardReadsTests.cs`  — the `"bo"` fix.
- `tests/MyRestaurant.DataAccess.Tests/Sittings/SittingSettlementTests.cs`  — the `"bo"` fix and the
  pending-count assertion.

No `docs/TECHNICAL_SPECIFICATION.md`, `docs/REQUIREMENTS.md`, or ADR edit: this realizes behaviour §7 and
§11.4 already specify. No migration, no new packages.

## Five decisions worth knowing before you read the diff

**Rename and reprice are two forms, not one Save.** §7's event vocabulary has `name_changed` and
`price_changed` as separate types whose payload columns are mutually exclusive, enforced by §8.2's paired
CHECKs. A combined save would write two events anyway and would then need a policy for what to do when one
half changed nothing. Two forms make the log read the way somebody settling a price argument needs it to.

**The price is rounded once, in C#, before either row is written.** `price_amount` and `new_price_amount`
are both `numeric(10,2)` and are written by two separate statements — hand PostgreSQL 4.567 and it rounds
quietly and independently for each. Rounding once (away from zero, matching `numeric`'s own rule) guarantees
the row, the event, and the value returned to the caller are the same number. The no-op comparison happens
after rounding too, so a form that posts 4.500 against a stored 4.50 writes nothing instead of writing an
event that records nothing.

**`EventType` is the stored string, not an enum.** §11.4 requires administration to render the complete
stored record; an enum is a projection with a failure mode, where an unknown type either throws or is
silently mapped to something wrong. Both surfaces label the five types §8.2 admits and fall back to the raw
string.

**Duplicate item names are allowed.** `menu_item.name` carries no UNIQUE constraint, unlike
`restaurant_table.label`. A kitchen running a rotating special wants two rows called the same thing, and
this layer does not get to invent a constraint the schema of record does not have. The index orders by name,
so duplicates sit adjacent where somebody will notice them.

**`Program.cs` is untouched.** Five scoped registrations went into `AddRestaurantOrders()`, whose doc comment
already claimed the menu. Nothing to merge by hand.

## The one-line why

A restaurant whose menu can only be edited with `psql` is a database with a waiter attached — and because a
price that moves is the thing guests argue about, every change has to leave a row saying who moved it, when,
and what it was before.

## Where to look if the build breaks

`ManageMenuItem.razor`, and specifically `InputNumber<decimal>`. It is the first `InputNumber` in the tree —
every other form here is `InputText` — and the generic argument is inferred from the bound property's type
rather than written out. If the inference does not go the way I expect, the fix is
`<InputNumber TValue="decimal" @bind-Value="RepriceInput.PriceAmount" … />` in both that file and
`CreateMenuItem.razor`.

After it, one thing I could not check without a compiler: the renamed `MenuWorkflow` type now shares its
simple name with `KitchenBoard.razor`'s injected `@inject IMenuWorkflow MenuWorkflow` property, and that
file has `@using MyRestaurant.WebApplication.Menu`. C# simple-name lookup finds members of the enclosing
type before types in imported namespaces, so `MenuWorkflow.SetMenuItemActiveAsync(…)` at line 733 resolves
to the property and this is fine — but it is the one place the rename could bite, and if it does the fix is
to rename that injected property rather than the class.

Then `MenuEventLog.cs`: the `COALESCE(NULLIF(btrim(actor.display_name), ''), actor.username)` actor-name
expression is copied verbatim from `DapperCounterBoardReads`, where it is already green, and the two INNER
JOINs are over NOT NULL foreign keys. Everything else is ordinary C# in the shapes the surrounding files
already use.
