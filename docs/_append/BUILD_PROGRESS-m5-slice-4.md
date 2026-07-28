### M5 Slice 4 — hide and unhide: the guest's own history, and the only way back (landed)

M5's build-order line (§19) reads "bills, price adjustment, close & settle, end-of-day, counter fallback
QR, menu management + events, event explorer, **hide/unhide**, post-close corrections". This slice is the
emphasised word, both halves of it: §11.1's guest history with its per-order Hide, and §11.4's
hidden-records view with the per-record Unhide that is the system's only undo for it.

No migration, no packages, no schema change, `Program.cs` untouched, and nothing deleted. Every table and
view this needs shipped in `0001_initial_schema.sql` — `order_visibility_event` and the
`order_visibility_current` view have been sitting there since M1 with no writer and no reader.

---

#### What was actually missing

M4 Slice 2 recorded the deferral in as many words: "§11.1's per-order **Hide** control and the guest's own
**history** of past orders need `order_visibility_event` writes and a closed-sitting query that reads
across sittings; both are §6.8 work and belong with the §11.4 hidden-records view that is their only
unhide path (M5)." That is exactly what this is.

Until now a guest could see the meal they were eating and nothing else. Every reader in the tree is scoped
to an order or a sitting the caller already names — `IOrderReadModel` for the projection views,
`IOrderEventLog` for the fold, `ISittingRecordReads` for one sitting's complete record — and none of them
can answer "which of *this person's* orders, across every sitting they have ever been in, may they still
see". That question is the whole of §11.1's history section, and its second clause ("may they still see")
is §6.8.

---

#### Hiding is enforced in SQL, once

§6.8's guarantee is that a hidden order is gone from "the owner's own views". Both person-scoped queries in
`IOrderHistoryReads` therefore exclude hidden orders in the statement itself:

```sql
LEFT JOIN order_visibility_current AS visibility
        ON visibility.guest_order_identifier = guest_order.guest_order_identifier
WHERE guest_order.person_identifier = @PersonIdentifier
  AND sitting.closed_at IS NOT NULL
  AND NOT COALESCE(visibility.is_hidden, false)
```

A guarantee that depends on every future page remembering a `Where` clause is not one. The `COALESCE` is
load-bearing in its own small way: "never had a visibility event" and "explicitly unhidden" must read the
same, because §6.8 defines the current flag as the latest event and no events means not hidden.

`ListHiddenOrdersAsync` is the deliberate inverse, and it is the only reader in the tree that goes looking
for hidden rows. It is reached from one page, behind `area.administration` (§3.7).

---

#### Six decisions worth knowing before you read the diff

**The lock is on `guest_order` and nothing else.** Both writes take `SELECT … FOR UPDATE OF guest_order`
before reading the current flag, so two taps on Hide cannot both see "not hidden" and both append. They
deliberately do *not* lock `table_sitting`: §6.6's order-mutating transaction locks the sitting first and
the order second, and a transaction that only ever waits on the order can never be the other half of a
deadlock with it. The sitting's `closed_at` is read in the same statement without a lock, which is sound
because a close is one-way — §5.3 stamps it and nothing in the system clears it, so a value read here
cannot go stale in the direction that would matter.

**A visibility event is not an order event, so it does not go through `IOrderMutations`.** It has no
`sequence_number`, no operations, changes no line and no total, and appears nowhere in §8.5's fold.
Routing it through the §6.6 transaction would take a `FOR SHARE` on a sitting that is closed by definition
and imply, wrongly, that a bill could move because somebody tidied their history.

**Append-only, three rows for a round trip.** Hide writes `hidden`, unhide writes `unhidden`, and the
current flag is the latest of them. A boolean on `guest_order` would have been shorter and would have
thrown away the two questions the log answers: who hid it, and had it been hidden before. §6.8's prose
calls the administrator's row `unhidden_by_administrator`; the stored word is `unhidden` and who did it is
`actor_person_identifier`, because there is no guest unhide to distinguish it from.

**The confirmation is a step, not a `confirm()`.** §6.8 requires it to state "plainly that this cannot be
undone from the guest's account". A browser dialog cannot be read, cannot be styled, and does not exist
without JavaScript — while this page works with none. Tapping Hide navigates to `?hide={order}`, which
renders the warning inline above the row with the only two things to do next.

**Expansion in the hidden-records view is one row at a time, by URL.** §11.4 requires the complete
record — "never projected or truncated" — and a complete record is three queries. Rendering a hundred of
them to draw a list would be three hundred round trips for a page somebody is skimming. `?record={order}`
*is* the "expandable" in §6.8: the list is always complete, and the record of the row an administrator
actually opened is fetched in full.

**The hidden list is not filtered to closed sittings.** `HideAsync` refuses an open one, so a row for a
live sitting cannot arise from the application. If one ever does, this is the one screen that must show it
rather than hide the anomaly (§11.4) — hence a nullable `ClosedAt` on the summary and markup that says
"this sitting is still open" in bold.

---

#### The date filter, and why `RestaurantTime` grew two methods

§6.8's filter is "username, date range, and table". A date range needs a boundary, and §8.1 is
unambiguous about whose calendar day it is: an administrator typing 26 July means the restaurant's 26
July, not UTC's and not their own. §8.1 is equally unambiguous that exactly one type performs that
conversion, so `StartOfDay(DateOnly)` and `StartOfNextDay(DateOnly)` are on `RestaurantTime` rather than
in a `… AT TIME ZONE …` clause — a second place the configured zone is honoured is the place the two
drift.

Both return UTC-normalised instants, and that is not cosmetic: Npgsql refuses to write a `DateTimeOffset`
whose offset is not zero to a `timestamptz` parameter, so a boundary handed straight to a query must
already be UTC.

The range is half-open — the start of the lower day to the start of the day after the upper — so no caller
has to decide whether 23:59:59.999999 is inside a day. In a zone whose clocks move at midnight (Cuba's do)
the local midnight of a spring-forward day does not exist, and `TimeZoneInfo.GetUtcOffset` answers with
the standard offset; the boundary is then out by the size of the shift for one day a year. That is
deliberately preferred over `ConvertTimeToUtc`, which throws on an invalid local time: a filter an hour
wide at one edge is a filter, and a filter that throws is a blank page.

The username filter matches a substring case-insensitively, with `%`, `_` and `\` escaped so a pasted
wildcard cannot silently widen the search to everything. It is written `owner.username::text ILIKE …`
rather than as a bare `LIKE` on the column: `person.username` is `citext` (§8.2) and compares
case-insensitively under equality, but which of the extension's pattern operators a mixed
`citext`/`text` comparison resolves to is not something a query should quietly depend on.

---

#### `ISittingRecordReads` grew a second question

`GetOrderRecordAsync(guestOrderIdentifier)` returns one order's complete record, or `null`. The
hidden-records view lists orders from many different sittings and expands one of them; reading the whole
sitting to render one order would show an administrator the rest of that party's orders as a side effect
of opening a row about one person's hidden meal, and §11.4 is explicit that filters narrow "only on
explicit request" in *both* directions.

The three statements are now built from templates parameterised by one `const` WHERE fragment
(`table_sitting_identifier = @SittingIdentifier` or `guest_order_identifier = @GuestOrderIdentifier`),
composed once into six `static readonly` strings. Nothing is derived from input and every placeholder
stays a parameter; the point is not dynamic SQL but stopping the same 180-line union from existing twice,
which is how a reader ends up fixed in one copy and wrong in the other. `ListOrderRecordsForSittingAsync`
answers exactly as it did — its SQL is character-for-character the same once the fragment is
substituted — and both public methods now sit over one private `ReadRecordsAsync`.

---

#### No new metric

§12's meter list is closed and contains no visibility counter, correctly. The instruments there are the
ones an operator watches a service by — sends, lines, reminders, closes, token validations, sign-ins — and
how often guests tidy their own history is not one of them. The same judgement §11.6 recorded about
profile edits and `security_event`: the vocabulary is closed on purpose, and the honest answer to "should
this be in it" was no.

The §9 broadcast is not optional, though. `VisibilityChanged(orderId)` goes out after each committed
write, and §9 routes it to "table members (history views)" — a guest with their history open on one phone
and the order surface on another would otherwise keep seeing the row they had just asked to have gone.
`OrderVisibilityWorkflow` is the post-commit shell, in the same relationship to `IOrderVisibility` that
`OrderWorkflow` has to `IOrderMutations`; surfaces take the workflow and never the write service.

---

#### Tests

- `OrderVisibilityTests` (Testcontainers, 11 facts) — a hide on a settled sitting by the owner appends one
  row and flips the view; a hide by somebody else, on an open sitting, on an already-hidden order, and on
  an order that does not exist each refuse and write nothing, and the "not the owner" refusal still reports
  whose order it was; an unhide appends the second row and flips back, reporting the owner rather than the
  actor; an unhide of a visible order and of an unknown one write nothing; hide → unhide → hide leaves
  three rows and the order hidden, which is only true if this writer and `order_visibility_current` agree
  on what "latest" means; hiding one order on a two-person sitting leaves the other alone; and an
  administrator can unhide an order whose sitting is somehow still open — the deliberate asymmetry with the
  hide path, arranged directly because the writer will not produce that state.
- `OrderHistoryReadsTests` (Testcontainers, 16 facts) — settled sittings only, newest settled first, with
  the table and the person's own share, and the still-open one absent; a hidden order excluded and an
  unhidden one back; only that person's own orders even when two orders share a sitting; the lines as the
  projection (removal gone, adjustment applied, note carried) rather than the record; empty for a member
  who never ordered; the cap, and a non-positive cap asking for nothing; the hidden list system-wide with
  the username fallback on both the owner's name and the hider's, newest-hidden first; both totals and the
  sitting context on the row; an unhidden order gone from the list; the hide currently in force reported
  after a round trip; the username filter as a case-insensitive substring with wildcards taken literally;
  the table filter; the date range on the sitting's `opened_at`, half-open at both ends; the three filters
  composing; a row whose sitting is somehow open still reported with a null `ClosedAt`; and the visibility
  log oldest-first with the stored word, excluding other orders' events.
- `OrderVisibilityWorkflowTests` (10 facts, no container) — a committed hide announces once and passes its
  arguments through unchanged; the announcement carries the order the *service* reported; each of the four
  hide refusals and both unhide refusals announce nothing; a committed unhide announces once and is handed
  the administrator's identifier while reporting the owner's; hide-then-unhide announces twice; and the
  constructor rejects nulls.
- `OrdersWiringTests` gains two facts: `IOrderHistoryReads` resolves, and `IOrderVisibilityWorkflow`
  resolves *and* drags in a real `IOrderVisibility` — the same shape as the existing `IOrderWorkflow` fact
  and for the same reason.

`OrderTestWorld` gains `AddVisibilityEventAsync`, which writes a visibility row in plain SQL. That is the
established pattern in that class and the right one here twice over: it keeps a bug in the writer from
looking like a bug in the reader, and it reaches the state the writer refuses to create.

---

#### Where to look if the build breaks

**`HiddenRecords.razor`**, and specifically its two `<text>` blocks and the `class="hidden-record @(…)"`
ternary. Both idioms already exist in the tree — `<text>` in nineteen places, the nested-quote class
ternary in `CounterSitting.razor` and `KitchenBoard.razor` — so neither is new ground, but this file uses
them in the same markup. It deliberately declares no locals inside a markup `@foreach`: nothing else in
the tree does, so `IsExpanded(summary)` and `CountSentence(count)` are methods instead.

Then `SittingRecordReads.cs`'s template refactor. `OrdersTemplate`, `EventsTemplate` and
`OperationsTemplate` are `private static string` methods returning raw interpolated strings, consumed by
six `static readonly` fields declared after them; both scope fragments are `const`, so there is no
initialisation-order hazard. `ListOrderRecordsForSittingAsync` is now an expression-bodied `async` method
(`=> await ReadRecordsAsync(…).ConfigureAwait(false)`), which is legal but is the one shape in this slice
that has no precedent in the tree.

Three things I could not check without a compiler, each deliberate:

1. `form[OrderField]` is read out to a `string?` local before `Guid.TryParse` in both new surfaces.
   `StringValues` has an implicit conversion to `string?`, and `Guid.TryParse` has both a `string?` and a
   `ReadOnlySpan<char>` overload; being explicit keeps overload resolution off that question entirely.
2. `= ANY(@GuestOrderIdentifiers)` binds a `Guid[]` as one `uuid[]` parameter. `DapperMenuDirectory`
   already does this, so the shape is proven — but this is the first use of it in `Orders`.
3. `ESCAPE '\'` in a C# raw string literal is a single literal backslash on both sides: raw strings do not
   process escapes, and `standard_conforming_strings` (on by default since PostgreSQL 9.1) means the SQL
   string is one backslash rather than the start of an escape sequence.

Everything else is ordinary C# and SQL in the shapes the surrounding files already use — the readers are
`DapperCounterBoardReads`'s aliased-column, internal-row-type pattern with a `CROSS JOIN LATERAL … LIMIT
1` for the latest visibility event, and both surfaces are the static-SSR post/redirect/get shape
`ManageTable` established and `AdministrationSittings` repeated, including its `ReadFormAsync` note.

---

#### What is left in M5

The cross-cutting **event explorer** (§11.4: security, order, and menu events filtered by subject, actor,
type, and time). All three engines exist — `ISittingRecordReads` reads the order half, `IMenuEventLog` the
menu half, `ISecurityEventLog` the third — and what is missing is one screen that queries all three and a
shared filter over them. After that, M5 is closed and M6 is hardening: the Playwright matrix (fifteen
skips today, two of which — `Guest_HidesClosedOrder_AdminCanUnhide` and the join/token pair — this slice
finally makes writable), the restore drill, and CI.
