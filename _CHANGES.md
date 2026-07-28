# M5 Slice 4 — hide and unhide: the guest's own history, and the only way back

Every file below is a **full file** at its **repo-relative path**. Extract this archive at the repo root
and the contents drop straight over your working tree. `git status` will show exactly these 20 files as
modified/added, and **no deletions**.

```bash
tar -xzf m5-slice4-hide-unhide.tar.gz -C /home/kushal/src/dotnet/myrestaurant
```

## Files to DELETE

**None.** Nothing is renamed and nothing is superseded. No migration ships, no package changes,
`Program.cs` is untouched.

## The tree you exported was green

`dotnet test` reported **840 total, 0 failed, 825 passed, 15 skipped**; `run.sh --smoke` got a 200 from
`/healthz/ready`; the container stack and the quick tunnel both came up. Nothing in here is a fix — this is
the next slice.

One pre-existing warning, untouched and unrelated:
`tests/MyRestaurant.DataAccess.Tests/Sittings/SittingRecordReadsTests.cs(354,9): warning xUnit2031` — a
`.Where(…)` before `Assert.Single`. Both new test files avoid that shape.

Two housekeeping notes, neither blocking:

**`docs/BUILD_PROGRESS.md` still jumps from "M4 Slice 1" to "M5 Slice 2".** Five appends are unmerged in
`docs/_append/`, including this slice's:

```bash
cat docs/_append/BUILD_PROGRESS-m4-slice-2.md >> docs/BUILD_PROGRESS.md
cat docs/_append/BUILD_PROGRESS-m4-slice-3.md >> docs/BUILD_PROGRESS.md
cat docs/_append/BUILD_PROGRESS-m4-slice-4.md >> docs/BUILD_PROGRESS.md
cat docs/_append/BUILD_PROGRESS-m5-slice-1.md >> docs/BUILD_PROGRESS.md
cat docs/_append/BUILD_PROGRESS-m5-slice-3.md >> docs/BUILD_PROGRESS.md
cat docs/_append/BUILD_PROGRESS-m5-slice-4.md >> docs/BUILD_PROGRESS.md
```

**`/account/enroll-totp` no longer 500s.** The route was the open blocker in the previous session's notes
and the log you exported shows it clean — nothing in this slice touched it.

## What this closes

§19's M5 line reads "bills, price adjustment, close & settle, end-of-day, counter fallback QR, menu
management + events, event explorer, **hide/unhide**, post-close corrections". This is the emphasised word,
both halves: §11.1's guest history with its per-order Hide, and §11.4's hidden-records view with the
per-record Unhide that is the only undo for it.

M4 Slice 2 recorded the deferral in as many words: "§11.1's per-order **Hide** control and the guest's own
**history** of past orders need `order_visibility_event` writes and a closed-sitting query that reads
across sittings; both are §6.8 work and belong with the §11.4 hidden-records view that is their only
unhide path (M5)." `order_visibility_event` and the `order_visibility_current` view have been in
`0001_initial_schema.sql` since M1 with no writer and no reader. They have both now.

## New files (9)

### Code — DataAccess (2)

- `src/MyRestaurant.DataAccess/Orders/OrderVisibility.cs`
  `HideOrderOutcome`, `UnhideOrderOutcome`, `HideOrderResult`, `UnhideOrderResult`,
  `IOrderVisibility`/`DapperOrderVisibility` — the owner hide and the administrator unhide, one
  transaction each, `FOR UPDATE OF guest_order` and nothing else.
- `src/MyRestaurant.DataAccess/Orders/OrderHistoryReads.cs`
  `PersonOrderHistoryEntry`, `HiddenOrderFilter`, `HiddenOrderSummary`, `OrderVisibilityEntry`,
  `IOrderHistoryReads`/`DapperOrderHistoryReads` — the guest's own visible history (two queries), the
  filtered hidden-records list (one), and the visibility log.

### Code — WebApplication (1)

- `src/MyRestaurant.WebApplication/Orders/OrderVisibilityWorkflow.cs`
  `IOrderVisibilityWorkflow`/`OrderVisibilityWorkflow` — the post-commit shell that publishes §9's
  `VisibilityChanged`. No metric: §12's meter list is closed and correctly contains no visibility counter.

### Surfaces (2)

- `src/MyRestaurant.WebApplication/Components/Pages/Table/TableHistory.razor` — `/table/history`
- `src/MyRestaurant.WebApplication/Components/Pages/Administration/HiddenRecords.razor` —
  `/administration/hidden-records`

### Tests (3)

- `tests/MyRestaurant.DataAccess.Tests/Orders/OrderVisibilityTests.cs` (Testcontainers, 11 facts)
- `tests/MyRestaurant.DataAccess.Tests/Orders/OrderHistoryReadsTests.cs` (Testcontainers, 16 facts)
- `tests/MyRestaurant.WebApplication.Tests/Orders/OrderVisibilityWorkflowTests.cs` (10 facts, no container)

### Docs (1, append-then-keep)

`docs/BUILD_PROGRESS.md` is large and is not regenerated. The new section ships as
`docs/_append/BUILD_PROGRESS-m5-slice-4.md`, matching the sections already in that folder.

## Edited (11)

- `src/MyRestaurant.DataAccess/Orders/OrderEventVocabulary.cs`
  Two constants — `hidden` and `unhidden`, §8.2's second closed vocabulary. Nothing else moves.
- `src/MyRestaurant.DataAccess/Sittings/SittingRecordReads.cs`
  `GetOrderRecordAsync(guestOrderIdentifier)`, and the three statements refactored into templates
  parameterised by one `const` WHERE fragment. `ListOrderRecordsForSittingAsync` answers exactly as it did;
  its SQL is character-for-character the same once the fragment is substituted.
- `src/MyRestaurant.WebApplication/Time/RestaurantTime.cs`
  `StartOfDay(DateOnly)` and `StartOfNextDay(DateOnly)`, both UTC-normalised. Nothing existing changes.
- `src/MyRestaurant.WebApplication/Orders/OrdersServiceCollectionExtensions.cs`
  Three registrations and one doc-comment bullet. Chosen over a new `AddRestaurantVisibility()` so
  `Program.cs` needs no edit — this extension has owned §6 since M4.
- `src/MyRestaurant.WebApplication/Components/Pages/Home.razor`
  A history link in the area list, and the lede's stale "Menu management … arrives next" corrected —
  menu management landed in M5 Slice 2.
- `src/MyRestaurant.WebApplication/Components/Pages/Table/TableArea.razor`
  A history link in the footer and in the "not seated" branch, which until now offered somebody with no
  open table nothing to do.
- `src/MyRestaurant.WebApplication/Components/Pages/Table/TableOrderSurface.razor`
  One `<a>`: the settled view already said the order "stays on your history" and can now point at it.
  Nothing else in this file changes.
- `src/MyRestaurant.WebApplication/Components/Pages/Administration/AdministrationHome.razor`
- `src/MyRestaurant.WebApplication/Components/Pages/Administration/AdministrationTables.razor`
- `src/MyRestaurant.WebApplication/Components/Pages/Administration/AdministrationMenu.razor`
- `src/MyRestaurant.WebApplication/Components/Pages/Administration/AdministrationSittings.razor`
  One `<a>` each: a Hidden records link in the header actions. Nothing else changes in any of the four.
- `tests/MyRestaurant.DataAccess.Tests/Orders/OrderTestWorld.cs`
  `AddVisibilityEventAsync` plus its statement.
- `tests/MyRestaurant.WebApplication.Tests/Orders/OrdersWiringTests.cs`
  Two facts and three doc-comment sentences.

No `docs/TECHNICAL_SPECIFICATION.md`, `docs/REQUIREMENTS.md`, or ADR edit: this realizes behaviour §6.8,
§11.1 and §11.4 already specify, in the words they already use.

## Six decisions worth knowing before you read the diff

**Hiding is enforced in SQL, once.** §6.8's guarantee is that a hidden order is gone from "the owner's own
views", and a guarantee that depends on every future page remembering a `Where` clause is not one. Both
person-scoped queries carry `AND NOT COALESCE(visibility.is_hidden, false)`. The `COALESCE` is
load-bearing: "never had a visibility event" and "explicitly unhidden" have to read the same, because §6.8
defines the current flag as the latest event and no events means not hidden.

**The lock is on `guest_order` and nothing else.** Both writes take `FOR UPDATE OF guest_order` before
reading the current flag, so two taps on Hide cannot both see "not hidden" and both append. They
deliberately do not lock `table_sitting`: §6.6 locks the sitting first and the order second, and a
transaction that only ever waits on the order can never be the other half of a deadlock with it. The
sitting's `closed_at` is read unlocked in the same statement, which is sound because a close is one-way —
§5.3 stamps it and nothing clears it.

**A visibility event is not an order event, so it does not go through `IOrderMutations`.** No
`sequence_number`, no operations, no line, no total, nothing in §8.5's fold. Routing it through the §6.6
transaction would take a `FOR SHARE` on a sitting that is closed by definition and imply, wrongly, that a
bill could move because somebody tidied their history.

**The confirmation is a step, not a `confirm()`.** §6.8 requires it to state "plainly that this cannot be
undone from the guest's account". A browser dialog cannot be read, cannot be styled, and does not exist
without JavaScript, while this page works with none. Tapping Hide navigates to `?hide={order}` and the
warning renders inline above the row with two things to do next: confirm, or go back.

**Expansion in the hidden-records view is one row at a time, by URL.** §11.4 wants the complete record
"never projected or truncated", and a complete record is three queries. A hundred of them to draw a list
would be three hundred round trips for a page somebody is skimming. `?record={order}` *is* §6.8's
"expandable": the list is always complete, and the row an administrator actually opened is fetched in full.

**The hidden list is not filtered to closed sittings.** `HideAsync` refuses an open one, so such a row
cannot arise from the application — and if one ever does, this is the one screen that must show it rather
than tidy the anomaly away (§11.4). Hence a nullable `ClosedAt` on the summary and markup that says "this
sitting is still open" in bold.

## The one-line why

A guest who wants last Tuesday off their phone should be able to take it off their phone without anybody's
permission — and because the restaurant's record is not theirs to edit, the honest way to do that is a row
saying they hid it, which a manager can answer with a row saying they put it back.

## Where to look if the build breaks

**`HiddenRecords.razor`**, and specifically its two `<text>` blocks and the `class="hidden-record @(…)"`
ternary. Both idioms already exist in the tree — `<text>` in nineteen places, the nested-quote class
ternary in `CounterSitting.razor` and `KitchenBoard.razor` — so neither is new ground, but this is the
first file to use them in the same markup. It deliberately declares no locals inside a markup `@foreach`:
nothing else in the tree does that, so `IsExpanded(summary)` and `CountSentence(count)` are methods.

Then `SittingRecordReads.cs`. `OrdersTemplate`, `EventsTemplate` and `OperationsTemplate` are
`private static string` methods returning raw interpolated strings, consumed by six `static readonly`
fields declared after them; both scope fragments are `const`, so there is no initialisation-order hazard.
`ListOrderRecordsForSittingAsync` is now an expression-bodied `async` method
(`=> await ReadRecordsAsync(…).ConfigureAwait(false)`) — legal, and the one shape here with no precedent
in the tree.

Three things I could not check without a compiler, each deliberate:

1. `form[OrderField]` is read out to a `string?` local before `Guid.TryParse` in both new surfaces.
   `StringValues` converts implicitly to `string?` and `Guid.TryParse` has both a `string?` and a
   `ReadOnlySpan<char>` overload; being explicit keeps overload resolution off that question entirely.
2. `= ANY(@GuestOrderIdentifiers)` binds a `Guid[]` as one `uuid[]` parameter. `DapperMenuDirectory`
   already does this, so the shape is proven — but this is its first use in `Orders`.
3. `ESCAPE '\'` inside a C# raw string literal is one literal backslash on both sides: raw strings do not
   process escapes, and `standard_conforming_strings` (on by default since PostgreSQL 9.1) makes the SQL
   string a single backslash rather than the start of an escape sequence.

Everything else is ordinary C# and SQL in shapes the surrounding files already use — the readers are
`DapperCounterBoardReads`'s aliased-column, internal-row-type pattern plus a
`CROSS JOIN LATERAL … LIMIT 1` for the latest visibility event, and both surfaces are the static-SSR
post/redirect/get shape `ManageTable` established and `AdministrationSittings` repeated, `ReadFormAsync`
note included.

## Build/test checklist for this slice

1. `dotnet restore` — **no new packages**, no migration, no schema change, no `Program.cs` edit.
2. `dotnet build` — the two Razor pages are the likely compiler-catch home, as always.
3. `dotnet test` — expect **+37 facts** (11 + 16 + 10, plus 2 wiring, less nothing removed): 877 total,
   825 + 37 passing, 15 still skipped.
4. `bash run.sh --smoke` — should be unaffected; nothing touched startup, config, or migrations.
5. `bash run.sh --containers-only`, then:
   - `/table/history` as a guest with a settled meal — the row, its lines, its total.
   - Hide it: the confirmation step, then the row gone and the flash message.
   - `/administration/hidden-records` — the row, the four filters, "Open the complete record", the
     visibility log, the event log, then Unhide.
   - `/table/history` again — the row is back.

## What is left in M5

The cross-cutting **event explorer** (§11.4: security, order, and menu events filtered by subject, actor,
type, and time). All three engines exist — `ISittingRecordReads`, `IMenuEventLog`, `ISecurityEventLog` —
and what is missing is one screen over them with a shared filter. After that M5 is closed and M6 is
hardening: the Playwright matrix, the restore drill, and CI.
