### M5 Slice 3 — administration sittings: end-of-day, the complete stored record, and the corrections that live beside a settled total (landed)

M5's build-order line (§19) reads "bills, price adjustment, close & settle, **end-of-day**, counter
fallback QR, menu management + events, event explorer, hide/unhide, **post-close corrections**". This
slice is the emphasised part, plus the §11.4 Sittings section that houses both: `/administration/sittings`
and `/administration/sittings/{id}`.

No migration, no packages, no schema change, `Program.cs` untouched, and nothing deleted. Every table and
view this needs shipped in `0001_initial_schema.sql`.

---

#### What was actually missing

Slice 1 built the close transaction and the counter's two screens. It left three holes, all of them the
same shape: the *engine* existed and had no *screen*.

- §5.4's end-of-day pass. `ICounterBoardReads.ListOpenSittingsAsync` has carried `LastEventAt` — "§5.4's
  last-activity timestamps" — since Slice 1, and nothing read it.
- §6.7's post-close corrections. `OrderMutationValidator` has admitted an administrator's corrective
  event on a closed sitting since M4, and `SittingSettlementTests` asserted it there; there was no way to
  author one outside a test.
- §11.4's "complete stored record". `IOrderEventLog` reads one order's log, but for the fold and the
  validator — it maps to domain enums and carries no names, which is right for its two callers and unusable
  on a screen.

#### `ISittingRecordReads` — the third reader of the same tables, on purpose

`DapperSittingRecordReads` is the answer to "what has ever happened at this table", and it is deliberately
not `IOrderEventLog` with extra columns.

§11.4: "Administration renders the **complete stored record** everywhere — full event streams, visibility
logs, security events — never projected or truncated for the administrator; filters narrow only on explicit
request." Two consequences.

**`EventType`, `ActorRole`, and `OperationKind` are the stored strings, not enums.** `IOrderEventLog` maps
to `OrderEventType` and throws on a word it does not recognise, and that is correct there: its callers are
the §6.5 validator and the §8.5 fold, and both must refuse to proceed rather than guess. A screen must do
the opposite. An enum on this path is a projection with a failure mode — a value this build does not know
would either throw and blank the one page whose job is to show what is stored, or be silently mapped to
something wrong. Both surfaces label the values §8.2's CHECKs admit and fall back to the raw string. Same
decision `MenuItemEventEntry` made in Slice 2, for the same reason.

**Nothing is capped or paged.** A removed line, an undone fulfillment, and a superseded price are all in
the answer, because the history outliving the state is the entire point of ADR-0002. A sitting holds a
party's worth of orders and a service's worth of events; the honest read is the complete one.

#### The join that makes the record legible

`order_operation_line_removed`, `_price_adjusted`, `_fulfilled`, and `_fulfillment_reverted` store an
`order_line_identifier` and nothing else about the dish. Rendered as stored, a removal reads "removed
0192f0…" — technically complete and useless to the one person the page exists for.

So every branch of the reader's `UNION ALL` joins back to `order_operation_line_added` on
`order_line_identifier` and on to `menu_item`, and carries the name and quantity on all five operation
kinds. **That join is exact and total rather than a guess**: the column is `NOT NULL UNIQUE` on the adding
table ("the line's identity", §8.2) and is the declared FK target of all four other tables, so exactly one
origin row exists for every operation PostgreSQL will accept. `INNER JOIN`, not `LEFT` — a `LEFT` would only
invite a nullable member for a row that cannot exist.

What is **not** joined is the price. `UnitPriceAmount` stays null off `line_added` and
`NewUnitPriceAmount` stays null off `line_price_adjusted`, because a price is the thing arguments are about
and this record must not synthesise one. An adjustment says what it set; the capture it superseded is on the
`line_added` operation above it, which is where somebody settling the argument reads it from anyway.

Three queries — the orders, then every event across all of them, then every operation across all of them —
grouped in memory. A party of six with a long service is three round trips rather than thirteen, and the
query count does not move with the size of the party.

#### `CloseManyAsync`: one transaction per sitting, and that is normative

§5.4 says "close each via the same §5.3 transaction". That is not phrasing, it is the design.

§5.3's guarantee is that a sitting's total is summed under a `FOR UPDATE` that conflicts with the
`FOR SHARE` every order writer holds (§6.6). One long transaction spanning twelve tables would hold twelve
of those locks until the last one committed — so a guest still ordering at table 1 would block the closing
of table 12, and an error on table 12 would roll back eleven closures that were correct. Twelve short
transactions can do neither.

The loop therefore goes through `CloseAndSettleAsync`, not through `ISittingSettlement` directly, so **each
close is counted and announced at the moment it happens**. Batching the §9 broadcasts to the end of the pass
would leave a settled table taking orders on every phone that had it open for as long as the rest of the
pass took — and those sends would then be refused one by one with nobody able to say why.

**Repeated identifiers are collapsed before anything is attempted.** Not hypothetical: a form can post one,
and the first attempt would then close the sitting while the second found it closed, reporting one table as
both settled by this pass and previously settled by somebody else.

**`EndOfDayResult.SettledTotalAmount` deliberately excludes `AlreadyClosed`.** That total belongs to
somebody else's close; adding it here would report a day that took more than it did.

If the token trips part-way the exception propagates and the sittings already closed **stay** closed. They
were separate committed transactions and there is no undo for a close (§5.3). The surface re-reads and shows
what is still open, which is the truth.

#### `/administration/sittings` — no select-all, nothing pre-ticked

Static SSR, like every administration surface. Open sittings oldest-first with §5.4's last-activity column
(and a coarse "4 hours ago" beside the instant, because the question being asked is "has this table gone
home"), a checkbox per row, and one button.

**There is no select-all and nothing starts ticked.** Closing twelve tables costs twelve deliberate ticks,
and that *is* the confirmation step rather than a second page. The counter's single close has a confirm
because it happens mid-service with a guest waiting; an end-of-day pass is deliberate by nature, and a
select-all next to an irreversible action at the end of a long shift is the wrong affordance to build.

§5.3's pending-lines warning is repeated above the button, summed across every open table. An end-of-day
pass is the most likely moment in the system for somebody to charge for a plate nobody carried.

**This is the one form in the tree that reads its own POST values** —
`await HttpContext.Request.ReadFormAsync(...)` — rather than binding a `[SupplyParameterFromForm]` model. A
checkbox set whose length is however many tables happen to be open has no static model to bind to, and the
alternative already used elsewhere (a select-one picker, as on `ManageTable` and `TableDisplays`) is not what
§5.4 describes. It is not a second read of the body: the Blazor endpoint has already parsed the form to find
which handler to invoke, and `HttpRequest` caches the parsed collection, so this returns the same instance
the framework used. Awaiting the cached path rather than touching `Request.Form` synchronously keeps the
cancellation token honoured.

Post/redirect/get afterwards, with three counts in the query string — closed, already settled, gone. The
money does **not** travel in the URL: the settled list below re-renders from the database and shows every
stamped total in the restaurant's own currency rather than one reconstructed from a query parameter.

#### `/administration/sittings/{id}` — corrections appear only once it is settled

The record renders for an open sitting too; reading history is not a write. The corrective forms do not.

§6.7 is titled "post-close corrections" and §3.7's matrix gives "post-close corrective events" to the
administrator alone. While a table is still eating, the screen built for it is the counter's — and an
administrator already holds the counter role's capabilities (§3.7), so this page links to
`/counter/sittings/{id}` instead of growing a second copy of the counter's controls. `TryBegin` refuses on an
open sitting anyway, for the replayed post the markup does not offer.

**One picker rather than a panel per line.** A per-line correction panel in static SSR needs a distinct form
name per row and a model to bind a dynamic set of them to, which is exactly the shape §5.4's checkboxes had
to escape. `CounterSitting` and `TableDisplays` already settled this: one picker at the bottom that can name
any row above it. Two forms, because §6.2's `staff_edit` and `price_adjustment` are two event types with two
operation subtypes and mutually exclusive payload columns.

The price is rounded once, in C#, away from zero — matching `numeric(10,2)`'s own rule — before it reaches
the transaction, so the operation row, the projection, and the number reported back are the same value
rather than three independent roundings of one input. The same decision `MenuAdministration` made for
reprice in Slice 2.

A refusal renders **in place** rather than redirecting: §6.5.9's per-operation reasons are the whole value of
the message and do not survive a query string. A success redirects, so a refresh cannot re-append.

Adding a line goes through `AppendStaffEventToLivingOrderAsync`, so it works for somebody who joined and
never sent anything — their order row is created inside the same transaction (§6.1), which the surface cannot
do for itself. An item currently marked unavailable is refused, by the surface immediately and by §6.5.4
under the lock regardless. That is deliberate: the alternative is a bill naming a dish the kitchen has said it
cannot make.

#### Both totals, again

`CounterSittingSummary.HasPostCloseCorrections` was built in Slice 1 and had one consumer. Now the settled
list and the record header both use it, because §5.3 requires the stamped total and the corrected total to be
shown together the moment they differ — "what was charged" and "what the record now says is owed" are two
questions, and after this slice an administrator can actually cause them to diverge.

#### Tests

- `SittingRecordReadsTests` (Testcontainers, 13 facts) — one record per order oldest-first with the owner's
  name; every event in sequence with its stored type and role; the actor-name username fallback; a
  `line_added`'s item, quantity, captured price and note; a removal's reason and the item it took off; a
  removed line **gone from the projection and still in the record**; an adjustment's new price and required
  reason with the capture untouched beside it; a fulfillment and its reversal both surviving; one event's
  several operations all staying on that event; orders in other sittings excluded; an unknown sitting empty;
  a sitting nobody ordered in empty; and an administrator's post-close correction in the record with the
  stamped total unmoved.
- `EndOfDayTests` (7 facts, no container) — the registration resolves; three closes are announced
  separately and in order; only the ones actually closed are counted and totalled while all three are still
  attempted; repeated identifiers collapse before anything is attempted; an empty selection touches nothing;
  a sitting settling at zero is still counted and announced; every individual result is carried in the order
  asked; and a null selection throws.

#### What is left in M5

Hide/unhide (§6.8) with the hidden-records view, and the cross-cutting event explorer (§11.4: security,
order, and menu events filtered by subject, actor, type, and time). `ISittingRecordReads` is the order half
of that explorer's engine; `IMenuEventLog` is the menu half; `ISecurityEventLog` reads the third. What is
missing is `order_visibility_event`'s write path and one screen that queries all three.
