### M4 Slice 2 — the guest ordering surface: menu, staging, batch send, and the living order (landed)

M4 Slice 1 landed an order engine with no user interface at all. This slice is the first consumer: the
member view of `/table/{id}` grows a menu picker, a staging area, a Send that reports per-operation
reasons, the committed living order with its removals and price adjustments intact, the rest of the
party's orders read-only, running personal and table totals, and the §11.1 flip to a settled bill when
the counter closes the sitting. No data-access code changed and no SQL was written — every query this
surface makes already existed and, until now, had no caller.

**The structural decision: an interactive island inside a static page.** `/table/{id}` is
`[ExcludeFromInteractiveRouting]` because the join flow writes the grant cookie and issues redirects,
and a Blazor circuit can do neither. But everything §11.1 asks for below the join is live — a basket
that survives a mis-tap, lines that re-badge when the kitchen fulfills them, a party total that moves
when someone else orders, the flip on `SittingClosed`. Rather than compromise one for the other,
`TableJoin.razor` keeps the cookie work and renders the new `TableOrderSurface` with
`@rendermode="InteractiveServer"`. The parameters that cross the boundary are two `Guid`s, which the
framework protects with the data-protection key ring like any other server component marker; the person
is **not** among them, and is read from the cascading authentication state inside the island so the
component is correct on its own terms rather than merely correct because its caller was careful. The
roster moved into the island for the same reason: §9 sends `SittingMemberJoined` to table members, and a
statically rendered list cannot hear it. `TableJoin.razor`'s inline `<style>` block is gone; the
`.table-*` vocabulary moved to `app.css`, because an inline style in one component silently styling
another component's markup is a dependency nobody can see.

**Scoping every read by the sitting, not the table.** The island is handed the sitting the parent
resolved and re-derives everything else, and every query is keyed by that identifier. The one query
keyed by the table — `GetOpenSittingForMemberAsync`, which answers "am I still a member of something
open here?" — has its answer **discarded unless it names this sitting**. That is not defensive
programming; it is the difference between two behaviours. A sitting that is closed and followed by a new
one on the same table would otherwise silently swap the order under the reader's feet mid-session,
showing a guest somebody else's table as though it were theirs. Comparing the identifier makes the same
event do what §11.1 asks instead: flip to the read-only settled bill.

**Two new pure types, because a Razor component is not testable here.** `OrderNarrative` (Domain) folds
an order's event log into a per-line story that **keeps removed lines** — the opposite of
`order_current_line`, and deliberately so: §11.1 wants "removed lines struck-through with actor +
reason, price adjustments shown old → new with reason", and an append-only log exists so history
survives state (ADR-0002). The "old" side of that arrow is stored nowhere, which is precisely why it
cannot come from a view: it is the price the fold was holding when the adjustment arrived.
`OrderNarrative` also states §6.5.3's guest-removal rule once, as `GuestMayRemove`, so the surface can
grey out what the transaction would refuse rather than offering a control that always fails.
`OrderStaging` (WebApplication) is the basket: stage, unstage, change a quantity, tick a committed line
for removal, and `Build` into the operations of one `guest_submission` with a parallel description per
operation so a rejection's `OperationIndex` becomes "2 × Soup — the menu item is currently unavailable"
rather than "operation 3 failed". Both live outside the component for the reason `ProfileDetails`,
`ObligationsEnforcement`, and `PairingCode.Normalize` do (§16.1 — no bUnit).

**There are now two folds over the same log, and that is a risk with a test against it.**
`OrderProjection` answers the bill's question and §8.5 pins it against the SQL views;
`OrderNarrative` answers the guest's. Nothing structural stops them drifting, and the first person to
notice would be a customer being charged something the app never showed them. So
`OrderNarrativeTests.NonRemovedLines_AgreeWithOrderProjectionOnARandomisedSequence` closes the triangle:
on a seeded 120-event sequence — sends, removals, fulfillments, reversals, price adjustments, and one
operation aimed at a line that does not exist — the narrative's non-removed lines equal the projection's
lines field for field, in the same order, and the totals match. The sequence opens with a fixed prologue
that guarantees a removed line, a fulfilled line, and an adjusted line, so the coverage assertions hold
by construction rather than by the seed happening to be kind.

**Three behaviours worth naming.**

*Staged lines carry no price.* §6.5.4 prices every added line server-side from the menu read inside the
send transaction, so a price captured at staging time would be a second, older authority — and the one
moment it disagreed with the charge is the moment a guest would notice. The surface renders the current
menu price beside each staged line instead, which is correct by construction and re-reads itself on
`MenuChanged`. For the same reason `Build` proposes every added line at **zero**: the transaction
overwrites it, and sending zero rather than a plausible-looking number means a regression in that
overwrite shows up as a free lunch on the first order instead of as a stale price nobody spots.

*Stale removal ticks are pruned on every re-read.* A guest ticks a pending line; the kitchen fulfills it
before they press Send. §6.5.9 refuses the **whole** event on one bad operation, so that one leftover
tick would make the Send button permanently useless with no explanation. `PruneRemovals` drops marks for
lines that are no longer the guest's to remove and the surface says so in a sentence. The transaction
re-decides all of it under the lock regardless; this only moves the answer to the moment of the tap.

*Notes are not validated, at any length.* §7 is explicit that customization notes are free text and are
"never validated against any rules engine", so `OrderStaging` trims and collapses a blank to `null` —
exactly what the transaction does — and refuses nothing. The input carries `maxlength="200"` as a
courtesy against a pasted novel, and the circuit's own message-size limit is the real backstop.

**Live updates.** The island subscribes on `OnAfterRender(firstRender)` — never during prerendering, so
no subscription is created for a render about to be thrown away — and handles the four §9 notifications
that can change what is on the screen, filtered on the sitting identifier: `OrderLinesChanged`,
`LineFulfillmentChanged`, `SittingMemberJoined`, `SittingClosed`, plus `MenuChanged`, which additionally
re-reads the menu so a staged item picks up its "currently unavailable" mark (§7). One re-read per
notification, unconditionally: the queries are small, scoped to one sitting, and a restaurant table is
not a hot path. Nothing publishes `MenuChanged` yet — menu administration is M5 — and the handler is
wired now because writing it later means remembering to.

**`MoneyText`.** New, and the reason it is not `amount.ToString("C")` is that the framework's currency
formatting reads the *server's* culture and ignores `RESTAURANT_CURRENCY_CODE` entirely, printing
dollars for a restaurant configured in euros and doing it silently. A short ISO 4217 → symbol table with
the code itself as the fallback (`ISK 1200.00`), and the digits always invariant with two decimals,
because prices are `numeric(10,2)` and a guest checking a bill against a menu board wants them to line
up.

No migration, no packages, no DI change (every service the island resolves was registered by
`AddRestaurantOrders()` in Slice 1), and no specification edit — this realizes behaviour §6, §7, §9,
and §11.1 already specify. Tests: `OrderNarrativeTests` (16 facts including the drift guard, no
container), `OrderStagingTests` (22 facts and theories, no container), `MoneyTextTests` (10 facts and
theories, no container).

**Known consequence — a basket dies with the circuit.** Staging is circuit state and is not persisted, so
a refresh, a phone locking for long enough to drop the WebSocket, or a tab restore empties it. That is
deliberate: §6 gives an order exactly one persistence mechanism, the append-only event log, and a
half-composed basket is not an order event. Persisting drafts would mean a second write path, a second
projection, and a new question — "whose draft is on this table?" — that §11.1 never asks. If it proves
annoying in real use, the cheap fix is `sessionStorage` on the client, not a table.

**Known consequence — prerendering reads the order twice.** The island prerenders inside the static page
and then loads again when the circuit starts, so the first paint costs two rounds of the same six
queries. `TableDisplay` has carried the same cost since M3. Disabling prerender would trade it for a
visibly empty panel on a phone over a tunnel, which is the worse deal.

**Known consequence — times render in the server's zone, not `RESTAURANT_TIME_ZONE`.** Found while
building this surface and recorded as **F-36**, not fixed here. Every surface in the tree uses
`ToLocalTime()`, `RestaurantOptions.ResolveTimeZone()` has no caller, and the runtime container sets no
`TZ`, so a deployed instance shows a guest UTC. This slice matches the existing convention rather than
introducing a second one inside a single screen — the static header and the interactive island are one
page — and the fix, together with the locale question §13 never settles (12- versus 24-hour), lands as
its own slice before M5.

**Deferred, and why.** §11.1's per-order **Hide** control and the guest's own **history** of past orders
need `order_visibility_event` writes and a closed-sitting query that reads across sittings; both are
§6.8 work and belong with the §11.4 hidden-records view that is their only unhide path (M5). The
committed order renders removals and price adjustments the guest can actually cause today; the surfaces
that *produce* staff removals and price adjustments — the counter (§11.3) — arrive in M5, so those
branches are written and tested but cannot yet be exercised through the interface. Guest
self-registration on the join path (§11.1) is still outstanding from M2: an anonymous scanner is sent to
`/sign-in` and needs an account already.

### Build/test checklist for this slice

1. `dotnet restore` — **no new packages**, no migration, no schema change, no DI change.
2. `dotnet build` — `TableOrderSurface.razor` is where a compiler catch would live: it is the first
   interactive island in the tree (as opposed to a whole interactive page), and the first component with
   a `<select>`, lambda-bound `@onchange` handlers inside a `@foreach`, and a nested `record` in its
   `@code` block. `TableJoin.razor` is the second-likeliest, having lost its `<style>` block and gained
   a child component with `@rendermode`.
3. `dotnet test` — the previous green set plus three pure suites; none needs a container engine, so
   none skips.
4. `./run.sh --smoke` — boots once, verifies `/healthz/ready`, exits.
5. Manual verification **needs a menu**, and there is still no surface for one until M5. Two rows is
   enough, and add an inactive one to see §7's rule work:

   ```sql
   INSERT INTO menu_item (menu_item_identifier, name, price_amount, is_active, created_at)
   VALUES (gen_random_uuid(), 'Soup',   4.50, true,  now()),
          (gen_random_uuid(), 'Salad',  6.00, true,  now()),
          (gen_random_uuid(), 'Salmon', 18.00, false, now());
   ```

6. Manual, the happy path, on a quick tunnel (`bash scripts/quick_tunnel.sh`) with a phone:
   - Scan a table's display, sign in, join. The table page now shows **Who is here**, a picker, and an
     empty basket.
   - **Salmon** appears in the picker greyed out and reading *(currently unavailable)*, and cannot be
     selected — §7's "the guest sees that the salmon exists and is out".
   - Add two Soups with a note and one Salad. The basket shows both with the running amount; **Send**
     is enabled and names the count.
   - Send. The status line says what went; the basket empties; the lines appear below badged **With the
     kitchen**, with the note under each.
   - Tick **Take this off my order on the next send** on one line, then Send again. It renders
     struck-through and badged **Removed**, with "Taken off by you".
7. Manual, live updates — open the same table as a **second** guest in another browser profile:
   - The first guest's roster gains the second person **without a refresh** (`SittingMemberJoined`).
   - The second guest orders; the first guest's **the rest of the table** panel and the **table total**
     move without a refresh (`OrderLinesChanged`).
8. Manual, refusals and edges:
   - Set a quantity to `0` or `101` in the basket → refused in place, the row keeps its old quantity.
   - Deactivate an item in SQL (`UPDATE menu_item SET is_active = false WHERE name = 'Soup';`) **after**
     staging it, refresh, and Send → the whole batch is refused, the panel names the offending line by
     description, and **the basket is untouched** (§6.5.9 all-or-nothing).
   - `UPDATE table_sitting SET closed_at = now() WHERE closed_at IS NULL;` then refresh the table page →
     the surface flips to **This sitting has been settled**, the picker and Send are gone, and the bill
     renders read-only.
