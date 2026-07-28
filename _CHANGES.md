# M5 Slice 3 — end-of-day, the complete stored record, and the corrections that live beside a settled total

Every file below is a **full file** at its **repo-relative path**. Extract this archive at the repo root and
the contents drop straight over your working tree. `git status` will show exactly these 12 files as
modified/added, and **no deletions**.

```bash
tar -xzf m5-slice3-sittings.tar.gz -C /home/kushal/src/dotnet/myrestaurant
```

## Files to DELETE

**None.** Nothing is renamed and nothing is superseded. No migration ships, no package changes,
`Program.cs` is untouched.

## The tree you exported was green

`dotnet test` reported **819 total, 0 failed, 804 passed, 15 skipped**; `run.sh --smoke` got a 200 from
`/healthz/ready`; the container stack and the quick tunnel both came up. Nothing in here is a fix — this is
the next slice.

One housekeeping note, unrelated to this change: `docs/BUILD_PROGRESS.md` has the M5 Slice 2 section
appended, but the appends for **M4 slices 2–4 and M5 slice 1** are still sitting unmerged in
`docs/_append/`. The headings jump from "M4 Slice 1" straight to "M5 Slice 2". Four `cat` commands fix it
whenever you want; nothing depends on it.

```bash
cat docs/_append/BUILD_PROGRESS-m4-slice-2.md >> docs/BUILD_PROGRESS.md
cat docs/_append/BUILD_PROGRESS-m4-slice-3.md >> docs/BUILD_PROGRESS.md
cat docs/_append/BUILD_PROGRESS-m4-slice-4.md >> docs/BUILD_PROGRESS.md
cat docs/_append/BUILD_PROGRESS-m5-slice-1.md >> docs/BUILD_PROGRESS.md
cat docs/_append/BUILD_PROGRESS-m5-slice-3.md >> docs/BUILD_PROGRESS.md
```

## What this closes

§19's M5 line reads "bills, price adjustment, close & settle, **end-of-day**, counter fallback QR, menu
management + events, event explorer, hide/unhide, **post-close corrections**". This is the emphasised part,
plus the §11.4 Sittings section that houses both.

Three holes, all the same shape — the engine existed, the screen did not:

- **§5.4's end-of-day pass.** `ICounterBoardReads` has carried `LastEventAt` ("§5.4's last-activity
  timestamps") since Slice 1 and nothing read it.
- **§6.7's post-close corrections.** `OrderMutationValidator` has admitted an administrator's corrective
  event on a closed sitting since M4, and `SittingSettlementTests` asserted it there. There was no way to
  author one outside a test.
- **§11.4's "complete stored record".** `IOrderEventLog` reads one order's log for the fold and the
  validator — domain enums, no names. Right for its two callers, unusable on a screen.

## New files (6)

### Code — DataAccess (1)

- `src/MyRestaurant.DataAccess/Sittings/SittingRecordReads.cs`
  `StoredOrderOperation`, `StoredOrderEvent`, `SittingOrderRecord`,
  `ISittingRecordReads`/`DapperSittingRecordReads` — every order in a sitting with its complete,
  uncapped event log, actors named, operations legible. Three queries regardless of party size.

### Surfaces (2)

- `src/MyRestaurant.WebApplication/Components/Pages/Administration/AdministrationSittings.razor` —
  `/administration/sittings`
- `src/MyRestaurant.WebApplication/Components/Pages/Administration/ManageSitting.razor` —
  `/administration/sittings/{SittingId:guid}`

### Tests (2)

- `tests/MyRestaurant.DataAccess.Tests/Sittings/SittingRecordReadsTests.cs` (Testcontainers, 13 facts)
- `tests/MyRestaurant.WebApplication.Tests/Sittings/EndOfDayTests.cs` (7 facts, no container)

### Docs (1, append-then-keep)

`docs/BUILD_PROGRESS.md` is large and is not regenerated. The new section ships as
`docs/_append/BUILD_PROGRESS-m5-slice-3.md`, matching the sections already in that folder.

## Edited (5)

- `src/MyRestaurant.WebApplication/Sittings/SittingWorkflow.cs`
  `EndOfDayResult` and `ISittingWorkflow.CloseManyAsync`. `CloseAndSettleAsync` is byte-for-byte
  unchanged.
- `src/MyRestaurant.WebApplication/Tables/TablesServiceCollectionExtensions.cs`
  One registration (`ISittingRecordReads`) and two doc-comment paragraphs. Chosen over a new
  `AddRestaurantSittings()` so `Program.cs` needs no edit — this extension has owned §5 since M3.
- `src/MyRestaurant.WebApplication/Components/Pages/Administration/AdministrationHome.razor`
- `src/MyRestaurant.WebApplication/Components/Pages/Administration/AdministrationMenu.razor`
- `src/MyRestaurant.WebApplication/Components/Pages/Administration/AdministrationTables.razor`
  One `<a>` each: a Sittings link in the header actions. Nothing else changes in any of the three.

No `docs/TECHNICAL_SPECIFICATION.md`, `docs/REQUIREMENTS.md`, or ADR edit: this realizes behaviour §5.4,
§6.7, and §11.4 already specify.

## Six decisions worth knowing before you read the diff

**One transaction per sitting, and §5.4 says so on purpose.** "Close each via the same §5.3 transaction" is
the design, not the phrasing. §5.3's guarantee is a total summed under a `FOR UPDATE` that conflicts with
the `FOR SHARE` every order writer holds (§6.6). One long transaction over twelve tables would hold twelve
of those locks until the last committed — a guest still ordering at table 1 would block the close of table
12 — and an error on table 12 would roll back eleven correct closures. The loop goes through the public
`CloseAndSettleAsync`, so each close is counted (§12) and announced (§9) *when it happens*; batching the
broadcasts to the end would leave settled tables taking orders on every phone that had them open for the
rest of the pass.

**No select-all, nothing pre-ticked.** Closing twelve tables costs twelve deliberate ticks, and that is the
confirmation step rather than a second page. The counter's single close has a confirm because a guest is
standing there; an end-of-day pass is deliberate by nature, and a select-all beside an irreversible action
at the end of a long shift is the wrong affordance to build.

**The record's `EventType`, `ActorRole`, and `OperationKind` are stored strings, not enums.**
`IOrderEventLog` maps to `OrderEventType` and throws on an unknown word, which is right for the validator
and the fold — both must refuse rather than guess. A screen must do the opposite: an enum here is a
projection with a failure mode, where an unrecognised value either throws and blanks the one page whose job
is to show what is stored, or is silently mapped to something wrong. Both surfaces label the values §8.2
admits and fall back to the raw string. Same decision `MenuItemEventEntry` made in Slice 2.

**The four non-adding operation tables get their dish from a join, and the join is exact.** They store an
`order_line_identifier` and nothing else, so rendered as stored a removal reads "removed 0192f0…". Every
`UNION ALL` branch joins back to `order_operation_line_added` on `order_line_identifier` — `NOT NULL UNIQUE`
on that table and the declared FK target of all four others, so exactly one origin row exists for every
operation PostgreSQL will accept. `INNER JOIN`, not `LEFT`. What is *not* joined is the price:
`UnitPriceAmount` stays null off `line_added` and `NewUnitPriceAmount` off `line_price_adjusted`, because a
price is what arguments are about and the record must not synthesise one.

**Corrections appear only once the sitting is settled.** §6.7 is titled "post-close corrections" and §3.7's
matrix gives them to the administrator alone. While a table is still eating, the screen built for it is the
counter's — and an administrator already holds the counter capabilities (§3.7), so `ManageSitting` links to
`/counter/sittings/{id}` rather than growing a second copy of those controls. `TryBegin` refuses on an open
sitting anyway, for the replayed post the markup does not offer. The *record* renders either way; reading
history is not a write.

**`EndOfDayResult.SettledTotalAmount` excludes `AlreadyClosed`.** That total belongs to somebody else's
close. Adding it would report a day that took more than it did.

## The one-line why

A restaurant that cannot be closed at the end of the night is a restaurant with twelve tables permanently
mid-meal — and because the one number nobody may rewrite is the settled total, the only honest way to fix a
mistake on a bill is to append a row saying who fixed it, when, and what it was before.

## Where to look if the build breaks

**`AdministrationSittings.razor`, and specifically `await HttpContext.Request.ReadFormAsync(...)`.** This is
the one form in the tree that reads its own POST values instead of binding a `[SupplyParameterFromForm]`
model — a checkbox set whose length is however many tables are open has no static model to bind to, and the
select-one picker used on `ManageTable` and `TableDisplays` is not what §5.4 describes. It is not a second
read of the body: the Blazor endpoint has already parsed the form to find the handler, and `HttpRequest`
caches the parsed collection. `IFormCollection` and `HttpMethods` come from `Microsoft.AspNetCore.Http`,
which the Web SDK's implicit usings already supply — the same way `HttpContext` and `HttpMethods.IsGet`
work in `ManageMenuItem.razor` today. If the enumeration over `form[SelectedSittingField]` does not infer
`string?`, the fix is `foreach (string? posted in form[SelectedSittingField].ToArray())`.

Then `ManageSitting.razor`'s two `InputNumber` bindings — `decimal` for the price and `int` for the
quantity. `ManageMenuItem.razor` established that inference works for `decimal`; the `int` one is new. If
either misbehaves the fix is to write the generic argument out:
`<InputNumber TValue="decimal" @bind-Value="CorrectInput.NewUnitPriceAmount" … />` and
`<InputNumber TValue="int" @bind-Value="AddInput.Quantity" … />`.

Two things I could not check without a compiler, both deliberate:

1. `SittingRecordReads.cs` takes `using MyRestaurant.DataAccess.Orders;` for `OrderEventVocabulary`'s
   operation-kind discriminators (internal, same assembly). `MyRestaurant.DataAccess.Sittings` and
   `MyRestaurant.DataAccess.Orders` declare no colliding simple names today, and `CounterBoardReads.cs`
   already references the latter by prefix — but this is the first file in `Sittings` to import it
   wholesale.
2. `ManageSitting.razor` imports both `MyRestaurant.DataAccess.Orders` and `MyRestaurant.Domain.Orders`.
   `CounterSitting.razor` already does exactly that and is green, so there is no ambiguity to find; noted
   only because it is the sort of thing that bites once.

Everything else is ordinary C# and SQL in the shapes the surrounding files already use — the reader is
`OrderEventReader`'s `UNION ALL` with two more joins per branch, and both surfaces are the static-SSR
post/redirect/get shape `ManageTable` established and `ManageMenuItem` repeated.
