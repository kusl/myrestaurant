### M4 Slice 4 — the kitchen board: the queue, fulfillment, the "86" panel, and the reminder that nobody triggers (landed)

M4's build-order line (§19) reads "living order + locking protocol, staging UI, batch send + validation,
staff edits, fulfillment/reversal, projections + fold + equivalence tests, **kitchen surface + alerts +
reminder service**". Slice 1 landed the engine, Slice 2 the guest half, the close-out the time
convention. This is the clause that was still outstanding, and with it M4 is complete: a send now
reaches a screen a cook is standing at, and a send that is ignored says so by itself.

No migration, no packages, no schema change. Every table, view, and constraint this slice needs shipped
in `0001_initial_schema.sql` — including, crucially, `kitchen_notification`'s
`UNIQUE (order_event_identifier, kind)`, which is what makes the whole reminder mechanism safe rather
than merely careful.

---

#### The reminder is the load-bearing part, and it is the only thing here whose bug is silence

Everything else on this screen fails loudly. A queue in the wrong order looks wrong; a fulfil button that
does not work is pressed twice. The reminder is different: if it never fires, the application starts
cleanly, serves every page, alerts correctly on every send, and simply never mentions the ticket that
has been sitting for four minutes. Nothing in the logs, nothing on a dashboard, nothing a test that
checks "does it work" would catch — because it does work, right up until the moment nobody did anything.

So the design puts as little of it as possible in C#. §8.4's scan is a single SELECT, run every five
seconds; the "exactly one reminder per send" guarantee is the unique constraint, not a flag in memory;
and "broadcast only if the insert took" is a `RETURNING` clause, not a rowcount the caller interprets. A
restart mid-scan, two overlapping ticks, or (if there is ever one) a second web replica cannot
double-alert, and none of that depends on this process remembering anything between ticks.

**One documented deviation from §8.4's literal SQL.** The specification writes
`submission.occurred_at < now() - make_interval(secs => :reminder_seconds)`.
`DapperKitchenNotifications` computes the same threshold from `IClock.UtcNow` and binds it as
`@DueBefore` instead. `occurred_at` was stamped by the *application's* clock, so comparing it against the
*database's* `now()` compares two clocks — invisible while both containers share a host clock, wrong the
first time they do not. It also makes the rule testable at all: against `now()` there is no way to place
a send precisely either side of the threshold, and every clause of §10.2 would go unasserted. The four
EXISTS/NOT EXISTS clauses, the open-sitting filter, and the ordering are §8.4 verbatim.

`KitchenReminderService` is a `BackgroundService` on a `PeriodicTimer`, one DI scope per tick, and a
deliberately broad `catch` around each tick: a transient database blip must not stop the loop for the
life of the process. It is registered by `AddRestaurantOrders()` rather than by `Program.cs` — see below.

#### Why a hosted service is registered from `AddRestaurantOrders()`

Because §10 is one rule with two halves and they must not be separable. §10.1's initial alert is written
*inside* the order transaction (it has to be — a committed alert must never point at an event that rolled
back), so it already lives in `DapperOrderMutations`. If §10.2's half were wired somewhere else, it would
be possible to compose ordering into a host and get a system that alerts but never reminds. Registering
both from the same call means you cannot have one without the other. The extension's doc comment says so
at length, because a hosted service appearing from an `AddX()` is otherwise spooky.

#### The queue: `KitchenQueue`, pure, and ordered oldest-first at both levels

§11.2: "grouped by (table label → person display name → order), ordered by the group's oldest
`added_at`". The grouping is a pure function over the `kitchen_pending_line` read, outside the component
for the same reason `OrderStaging` and `OrderNarrative` are (§16.1 — no bUnit): the ordering rule *is*
the behaviour of the screen, and a rule that can only be checked by rendering a Razor component is a rule
nobody checks.

The word doing the work is **oldest**. Sorting a board by its most recent send is the obvious mistake and
looks fine in a demo; what it does in service is push a forgotten order further down the screen every
time somebody at that table asks for another drink — which is precisely the failure §10.2's reminder
exists to catch, arriving by a second route.

Every comparison falls through to a label and then to an identifier, so a re-read of unchanged data
produces a byte-identical board. Lines added by one send share an `occurred_at` to the microsecond and
two tables can be sent to in the same instant; a queue whose rows shuffle under a cook's hand on every
live update is worse than one in a slightly wrong order.

#### Undo needs a question the §8.3 views cannot answer

§11.2 wants "an Undo affordance on recently-fulfilled lines". `order_current_line.is_fulfilled` is the
latest flip's *direction* with its instant thrown away, so "fulfilled in the last quarter of an hour" is
not a question the projection views can answer. Rather than add a timestamp column to a schema-of-record
view to serve one button, `IKitchenBoardReads` asks the operation tables directly — a lateral pick of the
highest-sequence `order_operation_line_fulfilled` for each currently-fulfilled line. Hence a separate
interface rather than a sixth method on `IOrderReadModel`: that type is "the four §8.3 views", and this
is honestly a different question.

A line fulfilled, undone, and fulfilled again reports the second fulfillment. A line whose latest flip is
a reversal is absent — it is pending again and belongs in the queue, and offering a second Undo for it
would produce a refusal (§6.5.6) with no way for the cook to know why. Window: fifteen minutes, a
constant rather than configuration, since §13 does not name it and a setting nobody would change is only
a new way to be wrong.

#### `IMenuAvailability` — the one piece of M5 that could not wait

§11.2 puts the "86" toggle on the kitchen board, and the kitchen is the surface that knows the salmon has
run out. So availability, and only availability, ships now: `SetActiveAsync` flips `menu_item.is_active`
and appends the matching `menu_item_event` in one transaction, under `FOR UPDATE`. Create, rename,
reprice, and §11.4's per-item history stay in M5 and will grow from this rather than replace it — keeping
the write this narrow is what stops the kitchen board becoming an accidental menu editor.

Two behaviours are deliberate. **A no-op flip writes no event**: an append-only log of "somebody pressed
a button that changed nothing" is noise, and §11.4's history is meant to be read by a person. **The row
is locked before it is compared**: without that, two staff toggling at once could both read "active",
both write "inactive", and log two `deactivated` events for one deactivation — the flag would be right
and the history would be a lie, which is the worse failure in an append-only system (ADR-0002).

Surfaces take `IMenuWorkflow`, never the raw write, so the §9 `MenuChanged` always goes out. An 86 that
skipped the broadcast would leave the item selectable in every open guest picker until that page happened
to reload, and the guest would then have a whole send refused for it (§6.5.9).

#### §10.3's alert, and why the sound is synthesised

"Browsers block autoplay: the kitchen surface shows a one-tap 'enable sound' arm control per session;
until armed (and whenever playback fails) a persistent, high-contrast visual badge with unseen-alert
count is the fallback." Three facts are load-bearing there — armed, playback-failed, and the count — and
`KitchenAlertState` states the sentence that combines them once, in a pure type, rather than
re-deriving it in markup.

Arming is circuit state, deliberately. It is a browser-audio permission that lives exactly as long as the
page does, so "per session" means per circuit; persisting it would be a lie, since a fresh tab has not
been armed no matter what a database says.

`wwwroot/js/kitchen.js` owns the noise, because an `AudioContext` will only start inside a real user
gesture and a Blazor circuit is not one. **It synthesises two square-wave beeps rather than shipping an
audio file.** An `.mp3` would be a binary asset to ship, cache, license, and get wrong at 3 kHz on a
cheap tablet speaker; Web Audio needs no file, cannot 404, and starts with zero network latency. The two
patterns differ on purpose — a rising two-note chime for a new send (§10.1), a flatter insistent triple
for a reminder (§10.2) — because "somebody just ordered" and "you have not touched this in a minute" are
different news. The gain ramps are not decoration: an oscillator started at full amplitude clicks, and on
a small speaker the click is most of what you hear.

`arm()` proves itself with a short quiet tone rather than trusting `state === 'running'`, and returns a
boolean. `alert()` returns false rather than throwing when it cannot play, and the component treats that
as §10.3's "whenever playback fails" and raises the badge. The wake lock follows `display.js`'s dance,
keyed on `#kitchen-board-surface`, on a two-second tick — half as busy as `display.js`, which has an
actual per-second job.

#### One threading detail worth naming

`KitchenAlertState` is a plain counter and `IDomainEventBroadcaster` fans out from whichever thread
committed the event, so two sends landing together could lose an increment. The board therefore records
the alert *inside* `InvokeAsync`, on the renderer's dispatcher, which serializes every mutation of it —
and records it **before** the re-read, so an alert still counts if the queries fail. A board that missed
a query is recoverable; a board that missed an alert is silent.

The alert token is a monotonic sequence, not a count, and acknowledging does not rewind it. Resetting it
would make the next alert's token equal to one already announced, and the board would go quiet with no
error anywhere. `KitchenAlertStateTests` pins both.

#### Two small things that came with it

`Pages/Home.razor`'s lede still said Milestone 2 was under way and that "the kitchen and counter boards
arrive in later milestones", which stopped being true two slices ago. It now describes what actually
works and carries role-gated area links — `<AuthorizeView Roles="…">` matching the §3.7 policies, because
showing somebody a door that answers "access denied" is worse than not showing it.

`MainLayout` gains a Kitchen link for the **kitchen role only**, not for administrators, even though
`area.kitchen` admits both. An administrator already carries an Administration link, and a fifth item
would put the header back into the three-row wrap at 375px that the single-Account-link change fixed.
Administrators reach the board from the landing page instead.

---

#### Files

**New (13)**

- `src/MyRestaurant.DataAccess/Orders/KitchenBoardReads.cs`
- `src/MyRestaurant.DataAccess/Orders/KitchenNotifications.cs`
- `src/MyRestaurant.DataAccess/Menu/MenuAvailability.cs`
- `src/MyRestaurant.WebApplication/Menu/MenuAvailabilityWorkflow.cs`
- `src/MyRestaurant.WebApplication/Orders/KitchenQueue.cs`
- `src/MyRestaurant.WebApplication/Orders/KitchenAlertState.cs`
- `src/MyRestaurant.WebApplication/Orders/KitchenReminderService.cs`
- `src/MyRestaurant.WebApplication/Components/Pages/Kitchen/KitchenBoard.razor`
- `src/MyRestaurant.WebApplication/wwwroot/js/kitchen.js`
- `tests/MyRestaurant.DataAccess.Tests/Orders/KitchenNotificationsTests.cs` (Testcontainers, 9 facts)
- `tests/MyRestaurant.DataAccess.Tests/Orders/KitchenBoardReadsTests.cs` (Testcontainers, 9 facts)
- `tests/MyRestaurant.DataAccess.Tests/Menu/MenuAvailabilityTests.cs` (Testcontainers, 7 facts)
- `tests/MyRestaurant.WebApplication.Tests/Orders/KitchenQueueTests.cs` (12 facts/theories, no container)
- `tests/MyRestaurant.WebApplication.Tests/Orders/KitchenAlertStateTests.cs` (13 facts, no container)
- `tests/MyRestaurant.WebApplication.Tests/Orders/KitchenWiringTests.cs` (6 facts, no container)

**Changed (4)**

- `src/MyRestaurant.WebApplication/Orders/OrdersServiceCollectionExtensions.cs` — four services and the hosted reminder loop
- `src/MyRestaurant.WebApplication/Components/App.razor` — loads `js/kitchen.js`
- `src/MyRestaurant.WebApplication/Components/Layout/MainLayout.razor` — Kitchen link for the kitchen role
- `src/MyRestaurant.WebApplication/Components/Pages/Home.razor` — accurate lede, role-gated area links

**Deleted** — none. `Program.cs` is untouched.

No `docs/TECHNICAL_SPECIFICATION.md`, `docs/REQUIREMENTS.md`, or ADR edit: this realizes behaviour §7,
§8.4, §9, §10, §11.2, and §12 already specify.

---

#### Build/test checklist for this slice

1. `dotnet restore` — no new packages, no migration, no schema change.
2. `dotnet build` — `KitchenBoard.razor` is where a compiler catch would live: it is the first component
   with lambda-bound `@onclick` handlers inside nested `@foreach` loops, the first to call
   `IJSRuntime.InvokeAsync<bool>` from `OnAfterRenderAsync`, and the first with a component-local
   `<style>` block since `TableDisplay.razor`.
3. `dotnet test` — the previous green set plus six suites. Three need a container engine and skip
   without one; three do not.
4. `./run.sh --smoke` — boots once, verifies `/healthz/ready`, exits. Watch the log for
   `Kitchen reminder service started`, which is the registration this slice most wants confirmed.
5. Manual verification **needs a menu** (M5 still owns menu creation) and two browser profiles:

   ```sql
   INSERT INTO menu_item (menu_item_identifier, name, price_amount, is_active, created_at)
   VALUES (gen_random_uuid(), 'Soup',   4.50, true, now()),
          (gen_random_uuid(), 'Salad',  6.00, true, now()),
          (gen_random_uuid(), 'Salmon', 18.00, true, now());
   ```

   Grant an account the `kitchen` role from `/administration`, then open `/kitchen` as that account.
6. Manual, the happy path:
   - As a guest on a phone, scan a table and send two Soups with a note and one Salad.
   - The kitchen board gains the ticket **without a refresh**, grouped under the table label, with the
     note in an orange block and the send time on the ticket header.
   - Tap **Enable sound**. A short quiet two-note confirmation plays and the chip reads **Sound on**.
   - Send again from the phone: a rising chime plays and the red badge counts one alert.
   - Tap a line. It leaves the queue, appears under **Just fulfilled**, and the guest's phone re-badges
     that line to **At your table** without a refresh.
   - Tap **Undo**. It returns to the queue and the guest's badge goes back to **With the kitchen**.
   - Tap **Fulfill all** on a ticket: one event, the whole ticket clears.
7. Manual, the reminder (§10.2) — the point of the slice:
   - `KITCHEN_SUBMISSION_REMINDER_SECONDS=20` in `.env` makes this bearable to watch.
   - Send from the phone and touch nothing. Within ~25 seconds the board plays the **three-note** pattern
     and the badge reads `2 new alerts (1 overdue)`.
   - Wait two more minutes: **nothing further happens**. One reminder per send, ever.
   - `SELECT kind, count(*) FROM kitchen_notification GROUP BY kind;` → one `initial`, one `reminder`.
   - Send again, fulfil one of its lines within the window, and wait: **no reminder** — §8.4's last
     NOT EXISTS, and the clause most likely to be got wrong.
8. Manual, the "86" panel:
   - Turn **Salmon** off on the board. The guest's picker greys it out and marks it *(currently
     unavailable)* **without a refresh** (`MenuChanged`).
   - `SELECT event_type FROM menu_item_event;` → one `deactivated`.
   - Press **Turn off** again on a reloaded board → the status line says it was already set that way and
     no second event is written.
9. Manual, the sound fallback: open `/kitchen` and do **not** arm it. Send from the phone. The
   high-contrast badge counts the alert and the orange notice explains why it is silent.
10. Manual, the wake lock: leave the board open on a tablet for longer than its screen timeout.

---

#### Known consequences and deliberate limits

**The board re-reads everything on every notification.** Three small indexed queries per event, restaurant
wide. A table surface can scope its re-read to one sitting; the kitchen board is the whole restaurant by
definition, so there is nothing to scope by. Fine at the size of restaurant this application is for
(ADR-0006); a hundred-cover service would want the queue diffed rather than re-fetched, and that is a
different program.

**Playback failure is detected per alert, not continuously.** The board learns that sound is broken the
next time it tries to play something. Continuous detection would mean a `DotNetObjectReference` callback
and a disposal path on the one surface that must never break, to gain nothing: the badge is already up by
the time anybody could act on the information.

**"Waiting 12m" is coarse and does not tick.** A per-second counter on twenty rows is a re-render per
second per row and tells a cook nothing "12m" does not. It refreshes on every live update, which in a
working service is often.

**A reminder that fires while no board is open is lost as a sound.** The row is written and the ticket is
still on the queue with its age showing, but nothing replays the alert when a board later connects.
Replaying stored alerts on connect would mean a board opened at the end of service screaming through the
whole evening's history; the queue itself is the durable record.

**Staff line-adds do not alert from this screen.** §10.1 gives an initial alert to "every `staff_edit` by
counter or administrator that adds or removes lines" — the transaction already writes that row and
`OrderWorkflow` already broadcasts it. There is simply no surface that produces such an edit yet; the
counter (§11.3) arrives in M5, at which point the behaviour is exercised without a line of new code here.

**Post-close corrections are invisible here, correctly.** §6.7 corrections belong to an administrator on
a settled sitting, and every query on this board filters `closed_at IS NULL`.
