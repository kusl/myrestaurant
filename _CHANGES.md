# M6 Slice 6 — the kitchen hears the guest

Every file below is a **full file** at its **repo-relative path**. Extract this archive at the repo root
and the contents drop straight over your working tree. `git status` will show exactly these 10 files as
modified/added, and **no deletions**.

```bash
tar -xzf m6-slice6-kitchen-hears-the-guest.tar.gz -C /home/kushal/src/dotnet/myrestaurant
```

## Files to DELETE

**None.** Nothing is renamed or superseded. No migration, no schema change, no
`Directory.Packages.props` edit, no new package, no `Program.cs` edit, no `.slnx` edit, no `.csproj` edit
(the test SDK globs new files in `tests/MyRestaurant.EndToEnd.Tests/Harness/` on its own).

## The state I found

`MyRestaurant.EndToEnd.Tests` did not compile:

```
tests/MyRestaurant.EndToEnd.Tests/Harness/TableJourneys.cs(113,21): error CS1620:
    Argument 2 must be passed with the 'ref' keyword
```

Everything else was green — `dotnet test` reported **956 / 0 failed** with the E2E project excluded by the
build failure. That is mine, from Slice 5, and it is the second time it has been reintroduced; the fix now
carries a comment explaining the rule so a future full-file rewrite does not undo it again. See
"Why this happens" below.

## Files (9)

### Fixes to what Slice 5 shipped

- `tests/MyRestaurant.EndToEnd.Tests/Harness/TableJourneys.cs` — the CS1620. Three characters, one comment.
- `tests/MyRestaurant.EndToEnd.Tests/Harness/DisplayJourneys.cs` — a raw string literal with trailing
  backslashes used as line continuations. Raw string literals process **no** escape sequences, so those
  backslashes and the newlines they were hiding were being printed into the diagnostic. Compiles fine,
  nothing failed on it, the message was just mangled at the moment somebody most needed to read it.

### Product (2)

- `src/MyRestaurant.WebApplication/Components/Pages/Table/TableOrderSurface.razor` — the whole render tree
  is now wrapped in `<div class="order-surface" id="table-order-surface" data-live="…">`, and a
  `IsLiveAttributeValue` property was added beside the other computed ones. Same attribute, same reason,
  as `TableDisplay.razor`'s. The body is re-indented one level by the wrapper — `git diff -w` shows the
  real change, which is seven added lines and one closing tag.
- `src/MyRestaurant.WebApplication/Components/Pages/Kitchen/KitchenBoard.razor` — `data-live` and
  `data-unseen-alerts` on the existing `#kitchen-board-surface` section, plus the same property.

**Why any product change at all.** Both surfaces prerender completely and then, without a circuit, never
change again — the ordering island's controls are all click handlers that land on nothing, and the kitchen
board lists a correct-looking queue and never alerts. That is indistinguishable from a quiet restaurant.
It is the same hazard `TableDisplay.razor` published `data-live` for in Slice 4, and it is the difference
between a scenario failing with "no circuit was established" and failing with "the basket stayed empty".

`data-unseen-alerts` is the §10.3 count as a number rather than as English inside the badge (which also
carries pluralisation and an optional "(n overdue)"). It is the only state on that screen that exists
solely in circuit memory.

No CSS changed: nothing in `app.css` reaches into the ordering surface with a child combinator.

### Harness (3)

- `tests/MyRestaurant.EndToEnd.Tests/Harness/TableOrderJourneys.cs` — **new**. Staging, sending, reading
  committed lines and their badges. All selectors scoped to `#table-order-surface`, which is load-bearing:
  the parent page renders its own `p.status-success` ("You have joined …") and an unscoped wait would
  report a send as accepted before the button was pressed.
- `tests/MyRestaurant.EndToEnd.Tests/Harness/KitchenJourneys.cs` — **new**. Opening the board, one
  snapshot of `(unseen alerts, pending lines)`, waiting on a predicate over it, fulfilling a line.
- `tests/MyRestaurant.EndToEnd.Tests/Harness/AdministrationJourneys.cs` — adds `CreateMenuItemAsync` and
  the `MenuItemOnTheMenu` record.

### Scenarios (1)

- `tests/MyRestaurant.EndToEnd.Tests/EndToEndScenarios.cs` — §16.3 scenarios **4** and **6** implemented,
  their `[Fact(Skip)]` placeholders removed, and a shared `ArrangeServiceAsync` that stands up an
  administrator, two menu items, a table, a joined guest with a live ordering island, and a live kitchen
  board.

### Documentation (1)

- `docs/_append/BUILD_PROGRESS-m6-slice-6.md` — **new**. Append it, do not regenerate:

```bash
cat docs/_append/BUILD_PROGRESS-m6-slice-6.md >> docs/BUILD_PROGRESS.md
```

No `TECHNICAL_SPECIFICATION.md`, `REQUIREMENTS.md` or ADR edit: nothing here changes what the system is
required to do. `data-live` / `data-unseen-alerts` are at the same grain as `data-refresh-token`, which §11
does not enumerate either.

## Why this happens (CS1620), so it stops happening

`string.Create`'s second parameter is `ref DefaultInterpolatedStringHandler`. C# converts an **addition**
to that handler only when the whole additive expression is composed of interpolated strings — Roslyn's
`Binder_Operators.cs` requires every operand to be a `BoundUnconvertedInterpolatedString`. One bare
`"…"` literal in the chain makes it a `BoundLiteral`, the expression collapses to a plain `string`, and the
call cannot bind by value.

```csharp
$"a {x}" + " b"     // ✗ CS1620 — " b" is a literal
$"a {x}" + $" b"    // ✓ a hole-less $"…" still counts
```

Two related traps the harness now comments on in place:

- An `await` **inside** an interpolated string that binds to a handler is **CS4007**. Read the page into a
  local first, then compose the message.
- Raw string literals (`$"""…"""`) process no escapes, so `\` at end of line is a backslash, not a
  continuation.

## Build and test checklist

```bash
cd /home/kushal/src/dotnet/myrestaurant

# 1. It compiles again — this is the whole of the red build.
dotnet build

# 2. Unchanged: no unit test was added or removed by this slice.
dotnet test
#    expect: total 971, failed 0, succeeded 956, skipped 15

# 3. Warnings-as-errors, the way CI builds it.
bash scripts/ci_local.sh --with-all

# 4. The scenarios themselves.
MYRESTAURANT_E2E=1 dotnet test tests/MyRestaurant.EndToEnd.Tests
#    expect: 8 passed / 7 skipped   (was 6 passed / 9 skipped)
#    newly passing: Guest_StagesAddsAndSend_KitchenGetsOneAlert
#                   Kitchen_FulfillsLine_GuestSeesFulfilledBadge

# 5. Append the progress block.
cat docs/_append/BUILD_PROGRESS-m6-slice-6.md >> docs/BUILD_PROGRESS.md
```

Remaining `[Fact(Skip)]` placeholders: **7** — scenarios 5, 7, 8, 9, 10, 11, 12.

## Where to look if this breaks

| Symptom | Where to look |
| --- | --- |
| `CS1620` again, anywhere | An operand in a `string.Create(provider, $"…" + …)` chain lost its `$`. |
| `CS4007` | An `await` inside an interpolated string bound to a handler. Hoist it into a local. |
| "The ordering surface never became interactive" | `/_framework/blazor.web.js` is not being served, or the wrapper's `data-live` is not rendering. `RestaurantInstance.VerifyInteractivityAsync` probes the first at startup. |
| "The kitchen board never became interactive" | Same cause. The board subscribes in `OnAfterRender(firstRender)`, which only runs on a circuit. |
| Scenario 4: `0 unseen alerts` but two lines arrive | The board heard `OrderLinesChanged` and not `KitchenAlert`. Check `OrderWorkflow.AfterCommit`'s `if (result.KitchenNotificationWritten)` and whether the `kitchen_notification` row went in with the event (§10.1). |
| Scenario 4: `2 unseen alerts` | The alert has gone per-line rather than per-event. One send is one `order_event` is one alert. |
| Scenario 4: send not confirmed | The failure quotes §6.5.9's whole refusal panel — every per-operation reason, because a send is refused as a whole. |
| Staging does not fill the basket | The failure quotes §11.1's staging notice. An unavailable item or a quantity outside 1–100 are the two local refusals. |
| Scenario 6: badge never flips | `LineFulfillmentChanged` did not reach the guest's circuit, or the guest surface's subscription was never made. The board's own confirmation is asserted first, so a refused fulfillment fails earlier and says so. |
| A send appears to succeed before the button is pressed | A `p.status-success` selector lost its `#table-order-surface` scope and is matching the parent page's "You have joined …". |
