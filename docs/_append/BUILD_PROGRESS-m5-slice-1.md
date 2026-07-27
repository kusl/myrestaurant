### M5 Slice 1 — the counter: the bill, the corrections, and the one number that is never rewritten (landed)

M5's build-order line (§19) reads "**bills, price adjustment, close & settle, end-of-day, counter
fallback QR**, menu management + events, event explorer, hide/unhide, post-close corrections". This slice
is the emphasised half — everything §11.3 puts on the counter's two screens — and it closes the last
place in the system where a guest could order something and nobody could take payment for it.

No migration, no packages, no schema change, and `Program.cs` is untouched. Every table, view, and CHECK
this needs shipped in `0001_initial_schema.sql`: `table_sitting`'s three paired columns, the
`sitting_bill` view, and the two `CHECK ((closed_at IS NULL) = (…))` biconditionals that make a partial
close impossible to write.

---

#### The lock is the feature

§5.3 is four sentences and one of them is load-bearing: "`SELECT … FOR UPDATE` the sitting row … compute
the settled total as the sum over `sitting_bill` for the sitting **under that lock**". §6.6 already has
every order-mutating transaction take `FOR SHARE` on the same row first. Those two modes conflict, and
that conflict is the entire guarantee — no event slips in after the total is computed, and no total is
computed over a half-written order.

`DapperSittingSettlement` is therefore the narrowest service in the data layer: one method, one
transaction, no identifier factory (closing completes a row rather than creating one). It locks, checks
`closed_at IS NULL`, counts what is still pending, sums `sitting_bill`, and stamps all three columns
together. Take a weaker lock and the failure mode is a bill that is quietly wrong, which is the worst
shape of bug this system can have — it does not throw, it does not log, and the person it happens to has
already left.

`COALESCE(sum(...), 0)::numeric(10,2)` rather than a bare `sum`: `sitting_bill` is built *from*
`guest_order`, so a table where everybody joined and nobody ordered has no rows at all, and `sum()` over
no rows is NULL. The column is NOT NULL whenever `closed_at` is set, so a party that ordered nothing would
otherwise fail its own close.

**A losing race reports the winner's close, not a failure.** Two counters pressing Close together produce
one `Closed` and one `AlreadyClosed`, and the second carries the stamped total, instant, and actor of the
first. The person at the till wants to know the table is settled and for how much; "that didn't work" is
both untrue and unhelpful.

#### `PendingLineCountAtClose` is a record, not a warning

§5.3 puts the warning *before* the button — "the counter UI must surface still-pending lines prominently
before offering Close (remove with reason, or knowingly charge)" — and the surface does exactly that,
with a bordered panel that names the count. By the time the transaction returns, the decision has been
made and committed. The count comes back anyway so the confirmation can say "settled at $41.50 — with 2
lines still with the kitchen" instead of implying a clean close. §8.3 is explicit that the bill *includes*
pending lines by design; this is the sentence that admits it out loud.

#### Both totals, forever

§5.3: `settled_total_amount` "is **never rewritten**; post-close corrections (§6.7) live beside it, and
the UI shows both the stamped settled total and, when corrective events exist, the current corrected
total". `CounterSittingSummary` carries both and computes `HasPostCloseCorrections` once, so no surface
has to remember to make that comparison. `AmountToShow` is the stamped total once closed and the live one
while open — what was charged, versus what is owed, and the two are different questions.

#### The counter reads are not a §8.3 view, and not `ISittingDirectory` either

All four projection views are scoped to an order or a sitting the caller already knows. The counter's
questions are "which tables are open right now" and "which have just been settled", which no view
answers. `ICounterBoardReads` rolls `order_current_state` across a whole sitting through a LATERAL —
money, order count, pending and fulfilled line counts, and §5.4's last-activity instant — so twenty open
tables are one query rather than twenty-one.

It is kept apart from `ISittingDirectory` on the pattern `IKitchenBoardReads` set: the directory answers
the join flow's membership question and is consumed by the *guest* surface, and widening its record would
put a billing projection into the type a phone renders a roster from.

Every aggregate is cast in SQL rather than converted in C#. `count(*)` is `bigint`, and `sum()` over a
`bigint` widens to `numeric`; Dapper's constructor binding will feed neither into an `int` parameter. The
casts sit where the intent is visible. The LATERAL is an aggregate with no `GROUP BY`, which returns one
row even over nothing — that is what makes a table where nobody has ordered appear with zeroes rather
than vanish, and a table missing from the counter's list is a table nobody bills.

#### `AppendStaffEventToLivingOrderAsync`, and why `IOrderWorkflow` grew a third method

§11.3 wants the counter to be able to add a line. Most guests order by talking to a person, so the common
case is adding for somebody who joined the table and never pressed Send — and that person has no
`guest_order` row yet (§6.1 creates it lazily inside the first write). `AppendStaffEventAsync` takes an
order identifier, so it cannot serve that case, and minting one on the surface would put §6.1's lazy
creation race outside the transaction that owns it.

The new method routes to `IOrderMutations.AppendToLivingOrderAsync` with the *actor* and the *order owner*
as different people, which is precisely the difference between a counter's staff edit and a guest's own
submission. It is on the bill either way; the log says who put it there, and §10.1 alerts the kitchen
because a counter's line-changing staff edit is one of the two things that does.

#### The surfaces

`/counter` lists open sittings oldest-first — the table that has been sitting longest is the one most
likely to be asking for its bill — with the money, the chips, and a per-table "Show join code". Below it,
tables settled in the last twelve hours, read-only, so a receipt can be checked against the record. It
subscribes to the broadcaster because §9 lists the counter as a consumer of `OrderLinesChanged`: a total
moving while somebody is standing at the till reading it is the point.

`/counter/sittings/{id}` is the bill. Per-person totals from `sitting_bill`, every current line with its
state badge and note, price adjustment (new price + required reason), staff remove (optional reason),
staff add, the pending warning, and Close & settle behind a confirm step.

**A closed sitting is the same page with the controls gone, and that is §11.3's "closed-sitting lookup
(read-only)" rather than a second page.** §6.5.8 admits nothing after a close but an administrator's
corrective events, so a counter's Adjust button there would be a door that only ever answers no.

**One picker at the bottom rather than a panel per person.** The obvious layout puts "Add an item for
Ada" under Ada's lines, which needs a `RenderFragment`-returning method to avoid duplicating the markup
five times. One picker with a "For" select is fewer moving parts, and it is the only control on the page
that can name somebody who has no lines to sit under.

Nothing on either surface enforces §6.5 or §5.3. Every button goes through `IOrderWorkflow` or
`ISittingWorkflow` to the one transaction, which re-decides under the lock — so a guest sending while the
counter presses Close produces one winner and one plainly-reported refusal. What the surfaces refuse on
their own they refuse only to make the answer immediate.

`/counter/tables/{id}/join-code` is §4.5's fallback, deliberately a sibling of the administration page
rather than a shared component: they differ in policy (§3.7 gives the counter its own), in where "back"
goes, and in how large the code is drawn — a counter holds this up across a pass. What they must not
differ in is the code itself, and they do not: both call `ITableJoinTokens.DescribeCurrentAsync` and
render what comes back. Static SSR, so it is a snapshot; §4.3's two-window acceptance means a scanned code
cannot die in a guest's hand, and "Show a fresh code" is the manual refresh. The window-aligned automatic
refresh belongs to the paired display, which has a circuit to run a timer on.

#### `sittings_closed_total` finally has a caller

The meter has existed since M1 and `RecordSittingClosed()` was, until now, dead code — there was nothing
in the system that could close a sitting. `SittingWorkflow` calls it, and only on the call that actually
closed: a losing race would otherwise double-count one close and tell every subscriber to re-query for a
change it did not make. Metrics before the broadcast, matching `OrderWorkflow`, so a subscriber that
re-queries synchronously cannot observe a state change that has not been counted.

The broadcast is not cosmetic. §11.1 flips the guest's table surface to a read-only settled-bill view
**on** `SittingClosed`, and the kitchen drops the table from its queue on the same notification. A close
that committed without announcing itself would leave a settled table still taking orders on every phone
that already had the page open — and those sends would then be refused one by one, with nobody able to
say why. `TableOrderSurface` and `KitchenBoard` already handle the notification; this is the first thing
that publishes it.

#### Header arithmetic

`MainLayout` now shows a Counter link to the `counter` role only, on the same reasoning as the Kitchen
link: an administrator already carries Administration, and six items in a header that wrapped onto three
rows at 375px with four is not a trade worth making. Administrators reach both boards from the landing
page's role-gated area links, which is the one place every door is listed.

#### Tests

- `SittingSettlementTests` (Testcontainers, 9 facts) — the stamp lands as three columns or none; pending
  lines are charged for and counted; adjustments and removals reach the total; every member's order is in
  it; a sitting nobody ordered in settles at zero; a second close writes nothing and reports the first;
  an unknown sitting is untouched; a guest send after close is rejected and the stamp does not move; an
  administrator's §6.7 correction leaves it alone.
- `CounterBoardReadsTests` (Testcontainers, 10 facts) — the roll-up, the all-zeroes case, pending versus
  fulfilled counted separately, closed sittings excluded and open ones oldest-first, the stamped total
  and closer's name (with the username fallback), the window and the cap, a non-positive cap, an unknown
  identifier, and both totals after a post-close correction.
- `CounterWiringTests` (5 facts, no container) — the three registrations resolve, and the workflow
  announces exactly the closes that happened: once for a close, never for a losing race, never for an
  unknown sitting, and yes for a sitting that settles at zero.

#### What is left in M5

End-of-day batch close (§5.4) and the administration sittings list; menu management with its event log
(§7, §11.4); the event explorer; hide/unhide (§6.8) and the hidden-records view; and the administrator's
post-close corrective surface. The engine for that last one already exists and is tested here — what is
missing is the screen.
