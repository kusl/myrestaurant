# M5 Slice 1 — the counter: bills, price adjustment, staff edits, and Close & settle

Every file below is a **full file** at its **repo-relative path**. Extract this archive at the repo root
and the contents drop straight over your working tree. `git status` will show exactly these 14 files as
modified/added (15 counting this one).

```bash
tar -xzf m5-slice1-counter.tar.gz -C /home/kushal/src/dotnet/myrestaurant
```

## Files to DELETE

**None.** `Program.cs` is untouched, no migration ships, and no file is superseded.

## What this closes

§19's M5 line reads "**bills, price adjustment, close & settle, end-of-day, counter fallback QR**, menu
management + events, event explorer, hide/unhide, post-close corrections". This is the emphasised half —
everything §11.3 puts on the counter's screens — minus end-of-day batch close (§5.4), which is an
administration surface and goes with the administration slice.

It also closes the last hole in the round trip: until now a guest could order and the kitchen could cook,
and nothing in the system could take payment or stop the table ordering.

## New files (7)

### Code — DataAccess (2)

- `src/MyRestaurant.DataAccess/Sittings/SittingSettlement.cs`
  `CloseSittingOutcome`, `CloseSittingResult`, `ISittingSettlement`/`DapperSittingSettlement` — §5.3
  exactly: `FOR UPDATE` the sitting, verify it is open, total `sitting_bill` under that lock, stamp all
  three columns together.
- `src/MyRestaurant.DataAccess/Sittings/CounterBoardReads.cs`
  `CounterSittingSummary`, `ICounterBoardReads`/`DapperCounterBoardReads` — open sittings, recently
  closed sittings, and one sitting, each rolled up with money and line counts through a LATERAL over
  `order_current_state`.

### Code — WebApplication (2)

- `src/MyRestaurant.WebApplication/Sittings/SittingWorkflow.cs`
  `ISittingWorkflow`/`SittingWorkflow` — the post-commit shell: `sittings_closed_total` (§12) and
  `SittingClosed` (§9), on the call that actually closed and no other.
- `src/MyRestaurant.WebApplication/Components/Pages/Counter/CounterJoinCode.razor`
  `/counter/tables/{TableId:guid}/join-code` — §4.5's fallback QR, static SSR.

### Surfaces (2)

- `src/MyRestaurant.WebApplication/Components/Pages/Counter/CounterBoard.razor` — `/counter`.
- `src/MyRestaurant.WebApplication/Components/Pages/Counter/CounterSitting.razor` —
  `/counter/sittings/{SittingId:guid}`.

### Docs (1, append-then-keep)

`docs/BUILD_PROGRESS.md` is large and is not regenerated. The new section ships as
`docs/_append/BUILD_PROGRESS-m5-slice-1.md`, matching the M4 sections already in that folder — append it
or leave it there, whichever you have been doing:

```bash
cat docs/_append/BUILD_PROGRESS-m5-slice-1.md >> docs/BUILD_PROGRESS.md
```

## New tests (3)

- `tests/MyRestaurant.DataAccess.Tests/Sittings/SittingSettlementTests.cs`   (Testcontainers, 9 facts)
- `tests/MyRestaurant.DataAccess.Tests/Sittings/CounterBoardReadsTests.cs`   (Testcontainers, 10 facts)
- `tests/MyRestaurant.WebApplication.Tests/Sittings/CounterWiringTests.cs`   (5 facts, no container)

## Edited — code (4)

- `src/MyRestaurant.WebApplication/Orders/OrderWorkflow.cs`
  Adds `AppendStaffEventToLivingOrderAsync`. `IOrderMutations` is unchanged, so the existing
  `FakeOrderMutations` in `OrderWorkflowTests` still compiles.
- `src/MyRestaurant.WebApplication/Tables/TablesServiceCollectionExtensions.cs`
  Three registrations: `ICounterBoardReads`, `ISittingSettlement`, `ISittingWorkflow`. Chosen over a new
  `AddRestaurantSittings()` so `Program.cs` needs no edit — the extension already says it wires "table
  **and sitting** services (§4, §5)".
- `src/MyRestaurant.WebApplication/Components/Layout/MainLayout.razor`
  A Counter link for the `counter` role. Nothing else changes.
- `src/MyRestaurant.WebApplication/Components/Pages/Home.razor`
  The lede said the counter's bill and settle-up arrived next; it no longer does. Plus a role-gated
  Counter area link.

No `docs/TECHNICAL_SPECIFICATION.md`, `docs/REQUIREMENTS.md`, or ADR edit: this realizes behaviour §5.3,
§5.4, §6.3, §6.7, §8.3, §9, §11.3, and §12 already specify. No migration, no new packages.

## Four decisions worth knowing before you read the diff

**A closed sitting is the same page with the controls hidden, not a second read-only page.** §6.5.8
admits nothing after a close but an administrator's corrective events, so a counter's Adjust button on a
settled table would be a door that only ever answers no. This is §11.3's "closed-sitting lookup
(read-only)", and it is free. Both totals are shown when §6.7 corrections exist, which is §5.3's actual
requirement.

**The add-an-item picker sits once at the bottom rather than under each person.** The per-person layout
needs a `RenderFragment`-returning method to avoid duplicating markup, and Razor templates in `@code` are
the one construct here I could not verify without a compiler. One picker with a "For" select is fewer
moving parts — and it is the only control on the page that can name a guest who has joined and never
ordered, who by definition has no lines to sit under.

**`IOrderWorkflow` gained a third method rather than the surface minting an order identifier.** Most
guests order by talking to a person, so the common counter add is for somebody with no `guest_order` row
yet. `AppendStaffEventAsync` needs an identifier that does not exist; creating one on the surface would
move §6.1's lazy-creation race outside the transaction that owns it.
`AppendStaffEventToLivingOrderAsync` passes the actor and the order owner separately, which is exactly
what distinguishes a counter's staff edit from a guest's own send.

**`Program.cs` is untouched.** Three scoped registrations went into `AddRestaurantTables()`, whose doc
comment already claimed sittings. Nothing to merge by hand.

## The one-line why

A meal that cannot be paid for is a hobby, and the number that says what it cost has to be decided under
the same lock that stops anyone adding to it — so this slice is one transaction, two screens, and a
promise that the total on the receipt is the total that was owed at the instant the table stopped
ordering.

## Where to look if the build breaks

`CounterSitting.razor`. It is the longest new component, and the only one with inline editors keyed off a
line identifier inside a nested `@foreach`, `@bind:event="oninput"` text inputs, and two `<select>`
elements bound to `string` and parsed with `Guid.TryParse` (deliberately — `BindConverter` support for
`Guid` is not something I could confirm against the SDK from here). Everything else is ordinary C# in the
shapes the surrounding files already use.

After it, `CounterBoardReads.cs`: the LATERAL's casts are the second most likely thing to be wrong, and
they fail at run time in a Testcontainers test rather than at compile time. `count(*)` is `bigint`,
`sum()` over `bigint` is `numeric`, and Dapper's constructor binding will feed neither into an `int`.
