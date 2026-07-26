### M4 Slice 1 — the order engine: the §6.6 transaction, the projections, and the menu read side (landed)

M4 opens with the part that has no user interface at all. Every screen in §11.1–§11.3 — the guest staging
area, the kitchen queue, the counter's bill — is a different rendering of one transaction and two
projections, and building any of them on top of a shaky write path would mean debugging Razor and
row-level locking at the same time. So this slice is DataAccess plus a thin post-commit shell, no
components, and it leaves M4's remaining work as presentation over a tested engine.

**The write path — one method, one transaction, §6.6 verbatim.** New `IOrderMutations`/
`DapperOrderMutations` (DataAccess/Orders) is the single way an order ever changes. There is deliberately
no "add a line" or "fulfill a line" method: §6.5.9 is all-or-nothing at the granularity of the *event*,
and a per-operation API would make honouring that impossible. Two entry points funnel into one core.
`AppendToLivingOrderAsync(sitting, owner, proposed)` is the guest send path and creates the order lazily
(§6.1) with `INSERT … ON CONFLICT (table_sitting_identifier, person_identifier) DO NOTHING` followed by
the locking re-select, which is exactly §6.1's "a lost creation race is re-read and proceeds".
`AppendToOrderAsync(order, proposed)` is the kitchen and counter path, acting on an order they are
looking at. It reads the order's sitting *without* a lock first — the column is immutable once the row
exists — because §6.6 puts the sitting lock before the order lock, and taking them the other way round on
this one path would invert the lock order and invent a deadlock.

Then, in order: `SELECT … FOR SHARE` the `table_sitting` row (which conflicts with the close
transaction's `FOR UPDATE`, and that conflict is the whole guarantee that no event slips past a close);
`SELECT … FOR UPDATE` the `guest_order` row; `coalesce(max(sequence_number), 0) + 1` under that lock, so
`UNIQUE (guest_order_identifier, sequence_number)` can never fire; read the prior log, the menu, the
sitting's open flag, ownership and membership; run `OrderMutationValidator`; insert the event, its
operations, and — when §10.1 says so — the `kitchen_notification` row, all in the same transaction;
commit. Step (g), the broadcast, is not here on purpose.

**Two behaviours that are not in the validator.** First, **the server prices every added line**. §6.5.4
says the unit price is "set server-side from the current menu price (client-sent prices are ignored)", so
whatever `UnitPriceAmount` arrives with is replaced by the `menu_item.price_amount` read inside this
transaction — and the rule is applied to staff edits as well as guest submissions, because the menu is
the price authority for an *add* and a counter who means to charge something else has
`price_adjustment`, which demands a reason and shows old → new on the bill. Second, **free text is
trimmed, never rejected**: customization notes and removal reasons are collapsed to `NULL` when blank,
because §7 is explicit that notes are never validated against any rules engine. A blank
price-adjustment reason is the exception and is passed through untouched, so the validator reports it
rather than the `btrim(reason) <> ''` CHECK exploding.

**The read side — two of them, and they have to agree.** `IOrderReadModel`/`DapperOrderReadModel` reads
the §8.3 views: `order_current_line` (per order, per sitting), `order_current_state`, `sitting_bill` with
the person's names joined on, and `kitchen_pending_line` with the §11.2 grouping keys and a
`COALESCE(NULLIF(btrim(display_name), ''), username)` so a freshly created account does not produce a
blank ticket header. `IOrderEventLog`/`DapperOrderEventLog` reads the raw log into domain `OrderEvent`s in
two queries — the headers, then all five typed operation tables folded into one flat set by `UNION ALL`
with every branch cast to its target type, because in a union PostgreSQL resolves the column type from
the branches and a bare `NULL` would leave it `unknown`. That log is what the validator sees under the
lock, what §8.5's equivalence test folds, and what §11.4's "complete stored record, never projected or
truncated" will render.

**§8.5, the load-bearing assertion.** `OrderReadModelTests.Views_AndTheDomainFold_AgreeOnARandomisedEventSequence`
drives 60 real events — sends, removals, fulfillments, reversals, price adjustments, counter staff edits
— through the real transaction with a **seeded** generator (a projection bug that reproduces only on
Tuesdays is worse than no test), then compares the fold against the views order by order: every line's
item, quantity, current price, note, fulfillment flag, added-at and adding event; both counts; the total;
first-submitted and last-event instants; and that sequence numbers are dense from 1. Some generated
events are rejected by §6.5 and that is left in deliberately — a rejected event must leave the log and the
views equally untouched.

The comparison is by line **set**, not by row order, and that is a finding worth recording. Lines added
in one send share an `occurred_at` to the microsecond, so the tie-breaker decides the order — and the two
tie-breakers cannot agree: the fold's `ThenBy(Guid)` uses .NET's `Guid.CompareTo` (Data1 as an `int`, then
two `short`s, then bytes) while the view's `ORDER BY` uses PostgreSQL's bytewise `uuid` collation. Both
are stable, neither is wrong, and §8.5's wording is about "the line set, prices, and fulfillment flags"
precisely because ordering was never part of the contract. The SQL ordering is in fact the better of the
two for a reader: line identifiers are UUIDv7 minted in staging order, and PostgreSQL's bytewise
comparison puts a UUIDv7's timestamp first, so the view returns a send's lines roughly in the order the
guest staged them.

**Operations within one event are a set, too.** The same UUIDv7 reasoning cuts the other way here: the
operation surrogate keys give the read a deterministic order, but not necessarily the insertion order,
since two keys minted inside one millisecond differ only in their random bits. The schema records no
ordinal within an event and nothing needs one — §6.5.5 forbids the one intra-event ordering that could
change an outcome (removing a line the same event added), and the views break same-event ties arbitrarily
too, because every operation of one event shares its sequence number.

**Menu, read side only.** New `IMenuDirectory`/`DapperMenuDirectory` (DataAccess/Menu) lists every item
ordered by name and reads one by identifier. It returns **inactive items too** — §7 requires a
deactivated item to stay on the menu marked "currently unavailable" rather than vanish, "the guest sees
that the salmon exists and is out", and a directory that filtered them would break that quietly. Menu
*administration* — create, rename, reprice, activate/deactivate, each appending a `menu_item_event` — is
M5 (§19) and will bring its own write interface; the read side lands now because ordering is unbuildable
without it and is its only consumer today.

**The post-commit shell.** New `IOrderWorkflow`/`OrderWorkflow` (WebApplication/Orders) is what surfaces
call; they never touch `IOrderMutations` directly. It records the §12 counters
(`guest_submission_batches_total`, `order_lines_added_total`, `order_lines_removed_total`,
`order_lines_fulfilled_total`) and publishes the §9 notifications after commit: `OrderLinesChanged`
unconditionally ("any order event commit"), `LineFulfillmentChanged` additionally on fulfillment and
reversal, and `KitchenAlert(initial)` **only when the transaction actually wrote the row** — the workflow
does not re-derive §10.1, because a change to one copy of that rule and not the other would leave the
sound and the stored record disagreeing. Nothing is published or counted for a rejected event. All five
services are registered by a new `AddRestaurantOrders()`, wired in `Program.cs` after
`AddRestaurantDisplays()`.

No migration (every table ships in `0001_initial_schema.sql`), no packages, no spec edit — this realizes
behaviour §6, §7, §8.3, §8.5, §9, §10.1, and §12 already specify. Tests: `OrderMutationsTests`
(Testcontainers, 11 facts — lazy creation and server pricing, one notification per send and the next
sequence number, all-or-nothing rejection leaving not even the order row behind, the non-member refusal,
guest removal of their own pending line but not a fulfilled one, fulfillment and its reversal being
silent and alternating, price adjustment with and without a reason, counter-alerts-kitchen-doesn't, the
closed-sitting matrix, unknown sitting and unknown order, and the empty-event and cross-order refusals),
`OrderReadModelTests` (Testcontainers, 5 facts including the §8.5 equivalence), `MenuDirectoryTests`
(Testcontainers, 3 facts), `OrderWorkflowTests` (7 facts and theories, no container), and
`OrdersWiringTests` (5 facts, no container).

**Known consequence — a post-close administrative correction still alerts the kitchen.** §10.1 says a
`staff_edit` by counter or administrator that adds or removes lines writes a `kitchen_notification`, with
no exception for closed sittings, and the transaction implements it literally. In practice an
administrator comping a line an hour after the bill settled will make the kitchen's tablet chime. Leaving
it is the honest reading; if it becomes a nuisance in use, the fix is one clause in `ShouldNotifyKitchen`
and a sentence in §10.1, not a special case in a surface.

**Known consequence — a rejected first send returns an identifier for a row that no longer exists.** The
lazily-created `guest_order` is rolled back with everything else, but `AppendOrderEventResult` still
carries the identifier the event targeted, which is genuinely useful when the order already existed. A
caller must not persist it; the fresh projection beside it is what §6.5.9 intends them to use.

**Deferred to the next M4 slice:** the guest staging area and `/table/{id}`'s living-order view (§11.1),
which is the first consumer of all of this, followed by the kitchen surface with its alert sound, wake
lock, and "fulfill all for this order" (§11.2, §10.3), and the §8.4 reminder scan and its background
service (§10.2). `kitchen_reminders_sent_total` stays at zero until that lands. Nothing in this slice
depends on menu administration, but the next one does in practice — until M5 there is no way to put an
item on the menu except an `INSERT`.

### Build/test checklist for this slice

1. `dotnet restore` — **no new packages**, no migration, no schema change.
2. `dotnet build` — no Razor at all this slice, which is deliberate: the compiler-catch risk in this
   codebase lives in components, and the order engine is worth landing green on its own. The likeliest
   catch is in `OrderMutations.cs`, which is the largest new file.
3. `dotnet test` — the previous green set plus three Testcontainers suites (which skip without a
   container engine; on rootless Podman, `systemctl --user enable --now podman.socket` once) and two pure
   ones. Expect `OrderReadModelTests` to be the slowest test in the suite by some margin — it commits
   dozens of transactions.
4. `./run.sh --smoke` — boots once, verifies `/healthz/ready`, exits. Nothing new runs at startup; the DI
   additions are all scoped and lazily constructed.
5. Manual verification needs a menu, and there is no surface for one until M5. Two rows are enough:

   ```sql
   INSERT INTO menu_item (menu_item_identifier, name, price_amount, is_active, created_at)
   VALUES (gen_random_uuid(), 'Soup', 4.50, true, now()),
          (gen_random_uuid(), 'Salad', 6.00, true, now());
   ```

   There is still nothing to click — the staging area is the next slice — so the honest check this time
   is `dotnet test` plus reading the `order_event` and `order_operation_*` rows the integration tests
   leave behind if you point the suite at a persistent database.
