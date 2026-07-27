# M4 Slice 4 — the kitchen board: the queue, fulfillment, the "86" panel, and the reminder service

Every file below is a **full file** at its **repo-relative path**. Extract this archive at the repo root
and the contents drop straight over your working tree. `git status` will show exactly these 19 files as
modified/added (20 counting this one).

```bash
tar -xzf m4-slice4-kitchen-board.tar.gz -C /home/kushal/src/dotnet/myrestaurant
```

## Files to DELETE

**None.** `Program.cs` is untouched, and no file is superseded.

## What this closes

§19's M4 line ends "…fulfillment/reversal, projections + fold + equivalence tests, **kitchen surface +
alerts + reminder service**". Slice 1 landed the engine, Slice 2 the guest half, the close-out the time
convention. This is the clause that was outstanding. With it, M4 is complete.

## New files (15)

### Code — DataAccess (3)

- `src/MyRestaurant.DataAccess/Orders/KitchenBoardReads.cs`
  `KitchenFulfilledLineView`, `IKitchenBoardReads`/`DapperKitchenBoardReads` — the recently-fulfilled
  query behind §11.2's Undo. Deliberately not a sixth method on `IOrderReadModel`: "when did this flip"
  is not a question the four §8.3 views can answer.
- `src/MyRestaurant.DataAccess/Orders/KitchenNotifications.cs`
  `IKitchenNotifications`/`DapperKitchenNotifications` — §8.4's scan plus the guarded insert.
- `src/MyRestaurant.DataAccess/Menu/MenuAvailability.cs`
  `IMenuAvailability`/`DapperMenuAvailability` — the "86" write, availability only.

### Code — WebApplication (6)

- `src/MyRestaurant.WebApplication/Menu/MenuAvailabilityWorkflow.cs` — post-commit shell, publishes `MenuChanged`.
- `src/MyRestaurant.WebApplication/Orders/KitchenQueue.cs` — the pure §11.2 grouping.
- `src/MyRestaurant.WebApplication/Orders/KitchenAlertState.cs` — the pure §10.3 arm/unseen state.
- `src/MyRestaurant.WebApplication/Orders/KitchenReminderService.cs` — the §10.2 `BackgroundService`.
- `src/MyRestaurant.WebApplication/Components/Pages/Kitchen/KitchenBoard.razor` — `/kitchen`.
- `src/MyRestaurant.WebApplication/wwwroot/js/kitchen.js` — alert sound and wake lock.

### Tests (6)

- `tests/MyRestaurant.DataAccess.Tests/Orders/KitchenNotificationsTests.cs`   (Testcontainers, 9 facts)
- `tests/MyRestaurant.DataAccess.Tests/Orders/KitchenBoardReadsTests.cs`      (Testcontainers, 9 facts)
- `tests/MyRestaurant.DataAccess.Tests/Menu/MenuAvailabilityTests.cs`         (Testcontainers, 7 facts)
- `tests/MyRestaurant.WebApplication.Tests/Orders/KitchenQueueTests.cs`       (12 facts/theories, no container)
- `tests/MyRestaurant.WebApplication.Tests/Orders/KitchenAlertStateTests.cs`  (13 facts, no container)
- `tests/MyRestaurant.WebApplication.Tests/Orders/KitchenWiringTests.cs`      (6 facts, no container)

## Edited — code (4)

- `src/MyRestaurant.WebApplication/Orders/OrdersServiceCollectionExtensions.cs`
  Four new services and `AddHostedService<KitchenReminderService>()`.
- `src/MyRestaurant.WebApplication/Components/App.razor`
  Adds one `<script src="js/kitchen.js" defer>` alongside passkey/display/clock. Nothing else changes.
- `src/MyRestaurant.WebApplication/Components/Layout/MainLayout.razor`
  Adds a Kitchen link for the `kitchen` role. Nothing else changes.
- `src/MyRestaurant.WebApplication/Components/Pages/Home.razor`
  The lede said Milestone 2 was under way and the kitchen board was a later milestone; both stopped
  being true. Now accurate, plus role-gated area links.

## Docs (1, append-then-keep)

`docs/BUILD_PROGRESS.md` is large and is not regenerated. The new section ships as
`docs/_append/BUILD_PROGRESS-m4-slice-4.md`, matching the two M4 sections already in that folder — append
it or leave it there, whichever you have been doing:

```bash
cat docs/_append/BUILD_PROGRESS-m4-slice-4.md >> docs/BUILD_PROGRESS.md
```

No `docs/TECHNICAL_SPECIFICATION.md`, `docs/REQUIREMENTS.md`, or ADR edit: this realizes behaviour §7,
§8.4, §9, §10, §11.2, and §12 already specify. No migration — `kitchen_notification`, `menu_item_event`,
and every view involved ship in `0001_initial_schema.sql`. No new packages.

## Four decisions worth knowing before you read the diff

**§8.4's `now()` becomes `@DueBefore`, computed from `IClock`.** The spec's SQL compares `occurred_at`
against the *database's* clock, but `occurred_at` was stamped by the *application's*. Same host today,
wrong the day they are not — and untestable either way, since against `now()` there is no way to place a
send precisely either side of the threshold. Everything else in that query is §8.4 verbatim. Veto this
and the seven §10.2 facts go with it.

**A hosted service is registered from `AddRestaurantOrders()`, not `Program.cs`.** §10 is one rule with
two halves: §10.1's alert is already inside the order transaction, because a committed alert must never
point at an event that rolled back. Wiring §10.2 anywhere else would make it possible to compose ordering
into a host and get a system that alerts but never reminds. `Program.cs` is untouched as a result, which
also means nothing to merge by hand.

**The alert sound is synthesised, not a file.** Two square-wave beeps from Web Audio: no binary asset to
ship or license, cannot 404, zero network latency, and the two patterns are distinguishable — a rising
chime for a new send, a flat insistent triple for a reminder.

**The kitchen CSS is a component-local `<style>` block, like `TableDisplay.razor`'s.** Every `.kitchen-*`
class is used only by that file. The `.table-*` vocabulary moved to `app.css` in Slice 2 precisely
because a *second* component had started reading it; nothing reads these. This also means `app.css` — all
1,162 lines of it — is not in this archive and cannot be clobbered by it.

## The one-line why

A send that nobody sees is not an order, and a send that nobody sees *and nobody is told about* is the
only failure in this system that is completely silent — so this slice is a screen a cook stands at, and,
behind it, a five-second scan whose "exactly once" is a unique constraint rather than a promise.

## Where to look if the build breaks

`KitchenBoard.razor`. It is the first component in the tree with lambda-bound `@onclick` handlers inside
nested `@foreach` loops, the first to call `IJSRuntime.InvokeAsync<bool>` from `OnAfterRenderAsync`, and
the first with a component-local `<style>` block since `TableDisplay.razor`. Everything else is ordinary
C# in the same shapes the surrounding files already use.
