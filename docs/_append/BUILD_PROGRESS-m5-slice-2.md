
---

## M5 Slice 2 — the menu: create, rename, reprice, and the history that explains a price

§19's M5 line reads "bills, price adjustment, close & settle, end-of-day, counter fallback QR, **menu
management + events**, event explorer, hide/unhide, post-close corrections". Slice 1 took the counter's
half; this takes the emphasised one. What is left after it is end-of-day batch close (§5.4) with the
administration sittings list, the event explorer, hide/unhide (§6.8) with the hidden-records view, and the
administrator's post-close corrective surface.

Until now the only menu **write** in the system was the kitchen's 86 toggle. An administrator could not
put a dish on the menu at all: the two items every demo has were inserted by hand, which is a fine way to
run a test and no way to run a restaurant.

### Three verbs, one log, two write services

`IMenuAdministration` is create, rename, and reprice. `IMenuAvailability` keeps activate/deactivate, where
it has been since M4, and the split is by **audience** rather than by table: §7 gives the 86 to kitchen and
counter as well as to administrators, because the kitchen is the surface that knows the salmon has run out,
and §11.2 puts the toggle on the kitchen board. Everything on the new interface is administrator-only
(§11.4). Two interfaces, two audiences, one `menu_item_event` log — which is the entire point of having a
log rather than four columns.

Rename and reprice are separate calls, and the manage page gives each its own form, because §7's event
vocabulary has `name_changed` and `price_changed` as distinct types whose payload columns are mutually
exclusive — §8.2 enforces that with paired CHECKs (`(new_name IS NOT NULL) = (event_type IN ('created',
'name_changed'))` and its price twin). A combined "Save" would have to write two events anyway, and would
then need a policy for what to do when one half is a no-op. Two forms make the log read the way somebody
settling a price argument needs it to.

Each write takes the row `FOR UPDATE` before comparing, for the reason `DapperMenuAvailability` does:
without the lock, two administrators repricing the same item at once could both read 4.50, both write 5.00,
and log two `price_changed` events for one change. The price would still be right and the history would be
a lie, which is the worse of the two failures in an append-only system (ADR-0002).

### Rounding is not a detail here

`menu_item.price_amount` and `menu_item_event.new_price_amount` are both `numeric(10,2)`, and they are
written by two separate INSERT/UPDATE statements. Hand PostgreSQL 4.567 and it rounds — quietly, and
independently for each statement. So the price is rounded **once**, in `NormalizePrice`, away from zero to
match `numeric`'s own rule, before either statement runs; the value returned to the caller is then the same
number as the row and as the event. The no-op comparison happens after rounding too, which is what stops a
form that helpfully posts a third decimal from writing a `price_changed` event recording that nothing
changed.

The same method refuses a negative price and anything that would not fit eight integer digits. Both would
otherwise surface as an opaque `PostgresException` (23514 and 22003 respectively) well after the form that
caused them, from inside a transaction that then has to be unwound.

### `IMenuEventLog`, uncapped on purpose

§11.4 is explicit: administration renders "the complete stored record everywhere — full event streams,
visibility logs, security events — never projected or truncated for the administrator; filters narrow only
on explicit request". So `ListForItemAsync` has no page size and no filter. It is the answer to "why does
this cost what it costs", and a truncated answer to that question is worse than none. It reads oldest
first, because that is the direction the answer is assembled.

`ListRecentAsync` is the one capped read, and its cap **is** the explicit request: it fills a twenty-row
activity panel on the menu index so an administrator opening the page can see that somebody 86'd two things
an hour ago without opening either.

`EventType` comes back as the stored string rather than an enum. An enum here is a projection with a
failure mode — a type this build did not know about would either throw or be silently mapped to something
wrong, and the one reader whose job is to show what is actually in the table is the last place that should
happen. Both surfaces render a friendly label for the five types §8.2 admits and fall back to the raw
string, so a future type shows up as itself.

The actor is rendered `COALESCE(NULLIF(btrim(display_name), ''), username)`, the same rule
`DapperCounterBoardReads` uses for whoever closed a sitting. An audit line that says who did it and then
leaves the name blank is not an audit line.

### `MenuAvailabilityWorkflow` is now `MenuWorkflow`

The class was named when availability was all it could do, and its own doc comment already said M5 would
grow methods on it rather than add a second workflow. It now has four. One workflow over two write services
because there is one notification: §9 fires `MenuChanged` on "a menu item or `menu_item_event` commit"
without distinguishing the verb, and every subscriber responds identically — re-read the menu. A second
workflow would only make it possible to wire an application that announces 86s and not repricings.

Create publishes unconditionally (a create commits or throws). The other three publish **only** when
something moved. Both halves of that matter and both fail silently: a reprice that committed without
announcing itself leaves every open guest picker quoting yesterday's price until that page happens to
reload, and the guest is then surprised at the till by a number nobody showed them; announcing a rename
that changed nothing tells every phone, kitchen board, and display in the building to re-query because
somebody pressed a button and nothing happened.

No metrics. §12's meter list has no menu counter, correctly — the menu changes a handful of times a
service, and `menu_item_event` is a better record of it than a counter would be. So unlike `OrderWorkflow`
and `SittingWorkflow` this takes no `RestaurantMetrics`.

### `Program.cs` is untouched again

The five registrations went into `AddRestaurantOrders()`, which has wired the menu since M4. The menu is not
an ordering concern, but it is not a table or an identity one either, and an order prices itself from the
menu (§6.5.4) — so nothing that can take an order can be wired without it. A fifth `AddRestaurantMenu()`
would mean a host could register ordering and get a system whose staging area cannot list anything: the same
class of half-wired failure as leaving the reminder loop out.

### Surfaces

Three static-SSR pages, on the `CreateTable`/`ManageTable` pattern exactly — post/redirect/get with a
one-word `?done=` outcome so a refresh cannot re-post, one named form per action so static-SSR form matching
stays unambiguous, `PersonPrincipal.IdentifierFor(HttpContext.User)` for the actor with a belt-and-braces
refusal if the principal carries no usable id claim (`actor_person_identifier` is NOT NULL and §7 requires
every change to name somebody).

`/administration/menu` lists every item — **including** the unavailable ones. §7 requires an 86'd item to
stay on the guest's menu marked "currently unavailable" rather than vanish, and a page that hid them from the
administrator would hide exactly the rows somebody came looking for. Ordered by name, which is also why
duplicate names end up adjacent: `menu_item.name` carries no UNIQUE constraint, unlike
`restaurant_table.label`, and nothing here invents one. A kitchen running a rotating special really does
want two rows called the same thing, and this layer does not get to overrule the schema of record.

`/administration/menu/{id}` carries the two editors, the availability toggle, and the complete history. Its
availability toggle handles `AlreadyInThatState` by simply re-rendering: somebody in the kitchen got there
first between the page rendering and the button being pressed, nothing changed, and nothing is wrong.

Both pages say, in prose, the thing that causes arguments if it is left implicit: a new price applies to
lines added from now on, and anything already on an order keeps the price it was added at (§6.5.4). A
rename, by contrast, shows through everywhere immediately including on closed bills, because the name is a
read-time join in §8.3's views and not captured.

A Menu link joins the header actions on the people and tables index pages, so the three administration
sections reach each other without going home first.

### Tests

- `MenuAdministrationTests` (Testcontainers, 15 facts) — the row and its event land together with both
  payload columns for `created`; the actor and instant are recorded; rounding reaches both rows and the
  returned value; the name is trimmed; two items may share a name; rename writes `name_changed` with a NULL
  price and leaves the price alone; renaming to the same name (or the same name with padding) writes
  nothing; renaming only the case *is* a change, because `name` is `text` and not `citext`; reprice writes
  `price_changed` with a NULL name and leaves the name alone; 4.500 against a stored 4.50 is a no-op; zero
  is a legal price; an unknown item is reported and untouched; a negative price, an eight-digit overflow,
  and a blank name are each refused before anything is written; and the log keeps all four changes when both
  write services have been at the same item.
- `MenuEventLogTests` (Testcontainers, 9 facts) — the stream is complete and oldest-first across both write
  services; each of the five types carries exactly the payload §8.2 allows it; the actor is named with the
  username fallback; instants read back as UTC; one item's stream excludes another's; an unknown item is
  empty rather than an error; the activity feed is newest-first, capped, and returns nothing for a
  non-positive cap; and a renamed item's history reads under its current name while each entry still says
  what it was set to then.
- `MenuWiringTests` (8 facts, no container) — the five registrations resolve, and the workflow announces
  exactly the commits that happened: always for a create, only-when-moved for rename, reprice, and the 86,
  never for an unknown item.

### Three failing tests from Slice 1, fixed here

The Slice 1 tree was not green. All three failures were in the new tests rather than in the code they
cover:

1. `CounterBoardReadsTests` and `SittingSettlementTests` both created a second guest as `"bo"`, which is two
   characters, against `person.username`'s `CHECK (char_length BETWEEN 3 AND 64)`. Now `"bode"`.
2. `CloseAndSettle_HonoursPriceAdjustmentsAndDropsRemovedLines` asserted `PendingLineCountAtClose == 0`. It
   is 1, and 1 is right: the removed steak leaves `order_current_line` entirely, so it is neither charged
   for nor counted, but the soup was only **repriced** and nothing fulfilled it, so it was still with the
   kitchen when the total was stamped. Adjusting a price is not the same act as passing the plate. The
   assertion moved, not the implementation.
