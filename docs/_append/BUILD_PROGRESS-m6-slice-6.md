### M6 Slice 6 — the kitchen hears the guest (landed)

Two more of §16.3's fifteen: **4** (a guest stages two adds and a note, presses Send, and the kitchen
gets exactly one alert with both lines pending) and **6** (the kitchen marks one line away and the
guest's own screen re-badges it). They are the first scenarios in which a commit made by one browser has
to be observed by a *second live circuit in another browser context* — everything before this watched a
timer, a redirect, or a row.

`dotnet test` stays at **971 total / 0 failed** (956 succeeded, 15 skipped) because no unit test was
added; `MYRESTAURANT_E2E=1` goes from **6 passed / 9 skipped** to **8 passed / 7 skipped**. No migration,
no schema change, no package change, no ADR edit, no `Program.cs` edit, nothing deleted.

---

#### The build was red, and the reason is worth writing down

Slice 5 shipped `Harness/TableJourneys.cs` with this in it:

```csharp
string.Create(
    CultureInfo.InvariantCulture,
    $"Joining did not confirm; the table page is now showing {stage}. A grant is"
    + " single-use and is cleared whatever the outcome (§4.4), so if this was a"
    + " refusal the grant is already spent and a retry will not help."),
```

`error CS1620: Argument 2 must be passed with the 'ref' keyword`. The overload being bound is
`string.Create(IFormatProvider?, ref DefaultInterpolatedStringHandler)`, and C# converts an addition to a
handler only when the additive expression is composed **entirely** of interpolated strings. Roslyn's
`Binder_Operators.cs` says so literally:

```csharp
&& left  is BoundUnconvertedInterpolatedString or BoundBinaryOperator { IsUnconvertedInterpolatedStringAddition: true }
&& right is BoundUnconvertedInterpolatedString or BoundBinaryOperator { IsUnconvertedInterpolatedStringAddition: true }
```

A bare `" single-use…"` binds as a `BoundLiteral`, the whole expression collapses to a plain `string`, and
the call no longer matches an overload it can bind by value. Prefixing every continuation with `$` fixes
it, and a hole-less `$"…"` still qualifies — `BindInterpolatedString` returns a
`BoundUnconvertedInterpolatedString` carrying a constant, not a literal node. The rule is now written into
the file as a comment beside the call, because this is the second time it has been reintroduced.

The same review found a quieter one three files over. `DisplayJourneys.WaitForLiveSurfaceAsync` wrapped its
diagnostic in a **raw** string literal with trailing backslashes for line continuation — and raw string
literals process no escape sequences at all, so every one of those backslashes and every newline it was
meant to hide was being printed into the message. It compiled and no test failed on it; it just produced a
mangled sentence at the exact moment somebody most needed to read one. Both are now concatenated
interpolated strings, which is the shape the rest of the harness already uses.

---

#### One new attribute on two surfaces: `data-live`

`TableDisplay.razor` has published `data-live` since Slice 4, for a reason that turned out not to be
specific to displays: **a surface that never became interactive is indistinguishable from a live one**.
Prerendering produces the whole document server-side, so the page looks completely correct and then never
changes again.

That hazard is worse, not better, on the other two live surfaces:

- **The ordering island.** Every control on it is a click handler. On a dead island "Add to basket" lands
  on nothing, and the first thing anybody learns is that the basket stayed empty — thirty seconds later,
  with no mention of circuits. `TableOrderSurface.razor` now renders its whole tree inside
  `<div class="order-surface" id="table-order-surface" data-live="…">`. That wrapper is also what
  disambiguates the island's own `p.status-success` from the parent page's *"You have joined Table Four"*,
  which is the same element on the same document and is on screen from the moment a join redirects back.
- **The kitchen board.** A prerendered board lists what was outstanding when the page was requested, in the
  right order, with the right waiting times, and then never alerts. A kitchen that has genuinely had no
  orders for ten minutes looks exactly the same. This is §10's worst failure and the one nobody notices
  while it is happening. `KitchenBoard.razor` already had `id="kitchen-board-surface"` for `js/kitchen.js`;
  it gains `data-live` on the same element.

The board also gains **`data-unseen-alerts`**, the §10.3 count as a number. The badge says it in English
already, but that string carries pluralisation and an optional *"(n overdue)"* parenthetical, and the count
is the one piece of state on that screen that exists **only in circuit memory** — everything else can be
re-derived from the database. Publishing it means an operator can read it in dev tools and a scenario can
assert on it without parsing prose to learn a fact the component already knows.

No CSS changed. Nothing in `app.css` reaches into the ordering surface with a child combinator, so the
wrapper is inert; `.order-totals > div` is internal to the tree it wraps.

---

#### Scenario 4 — "one loud alert" is a claim about the number one

The scenario stages 1 × *Soup of the day* with the note *"No onions, extra hot"*, then 2 × *Steak pie*, and
sends. Three things are asserted in an order that matters.

**Before the send, the board is live and showing nothing.** That is what turns §11.1's *"nothing reaches
the kitchen until you press Send"* from documentation into a test: a surface that wrote as it staged would
have alerted already, and the board was subscribed and watching.

**After the send, one predicate over both facts.** §9 publishes `OrderLinesChanged` **before**
`KitchenAlert`, and `KitchenBoard.OnDomainNotification` handles each as it arrives — so there is a real
window in which the queue has re-read and the alert has not yet been counted. A scenario that waited for
two lines and *then* read the count would sample that window and report a silent kitchen. So the wait is
`PendingLines.Count == 2 && UnseenAlertCount >= 1`, and the equality (`== 1`) is asserted on the snapshot
that satisfied it — then re-read once at the end, after the guest surface has finished reacting, which
turns "one alert so far" into "one alert, full stop".

**One alert, not two.** Two adds in one send is one `order_event`, therefore one `kitchen_notification`
row, therefore one `KitchenAlert` (§10.1). A count of two would mean the alert had gone per-line, which is
how a busy service becomes a siren nobody can hear over. The whole of `AfterCommit`'s
`if (result.KitchenNotificationWritten)` is what this asserts.

`Sound is not armed and not asserted.` §10.3's arm control has to run inside a real user gesture to unlock
browser audio, and what it unlocks is an `AudioContext` on a headless browser with no output device — "did
it beep" is a question about Chromium's audio stack, not about this application. §10.3 itself names the
visual badge as the fallback whenever sound is not working and makes the unseen count the record of what
arrived; that count is the number the sound is played *from*, so asserting on it asserts on the alert.

---

#### Scenario 6 — the badge crosses a browser boundary

One tap on one line (§11.2's *"tap a line → one fulfillment event"*, not *fulfill all*, because the half
worth getting wrong is the **other** line staying where it was), and then nobody touches the guest's phone.
§9 sends `LineFulfillmentChanged` to the sitting's members, the ordering island re-reads, and the chip goes
from *With the kitchen* to *At your table*.

The badge is read from the chip's **class** rather than its words: `chip-ok` is the state, "At your table"
is copy, and a scenario that matched the copy would fail on a wording change and pass on a styling bug. The
pass losing the line is asserted too — `kitchen_pending_line` excludes a fulfilled line (§8.3), so that is
the same fact from the writing side rather than a second opinion.

---

#### The arrangement both scenarios share

`ArrangeServiceAsync` stands up: an administrator (the §3.6 wizard), two menu items, a table, a guest who
scans, self-registers with a passkey and joins, a live ordering island, and a live kitchen board. Two
decisions in it are worth recording.

**The kitchen board is the administrator's own browser.** §3.7 admits both `kitchen` and `administrator` to
`/kitchen`, and an administrator covering the pass is a case the application deliberately supports —
`KitchenBoard.razor` reads the actor role from the principal precisely so that one is not recorded as the
other. Creating a separate kitchen account would mean `/administration/people/new`, a forced password
change (§3.2), and a second sign-in, none of which either scenario asserts on. It is also not free to
avoid: the administrator has TOTP from the wizard, so a *password* sign-in in a fresh context would hit the
§3.5 challenge, and a *passkey* sign-in would need the credential, which belongs to the authenticator on the
administrator's own context. Scenarios that are about a staff account's own journey will create one.

**The board is opened before anything is sent.** An alert is a §9 broadcast to subscribers, and
`KitchenBoard.razor` subscribes in `OnAfterRender(firstRender)` — which only runs on a circuit. A board
opened after the send would show the queue perfectly well and would have heard nothing.

The join secret is read out of the row rather than decoded off a paired display. These scenarios are about
what happens after the guest sits down, and pairing a tablet to obtain a QR would put the whole of scenario
2's apparatus in front of them; scenario 2 already proves that a real screen encodes exactly the code the
table's secret produces, so nothing is assumed by computing one here.

---

#### Harness changes

- `Harness/TableOrderJourneys.cs` (new) — `WaitForLiveSurfaceAsync`, `StageAsync`, `SendAsync`,
  `BasketLineCountAsync`, `ReadCommittedLinesAsync`, `WaitForCommittedLinesAsync`, and the
  `GuestOrderLine` / `GuestLineBadge` vocabulary. Every selector is scoped to `#table-order-surface`.
- `Harness/KitchenJourneys.cs` (new) — `OpenAsync`, `WaitForLiveBoardAsync`, `ReadBoardAsync`,
  `WaitForBoardAsync`, `FulfillLineAsync`, and the `KitchenBoardSnapshot` / `KitchenBoardLine` vocabulary.
- `AdministrationJourneys.CreateMenuItemAsync` and the `MenuItemOnTheMenu` record.

Two small habits worth keeping. **Items are selected by identifier, not by label**: the picker's label is
the name, the formatted price, and possibly §7's *"(currently unavailable)"*, so matching it would make a
scenario fail for a currency setting. **A line is found by reading the queue and comparing names in C#**,
not by interpolating a name into a `:text-is('…')` selector — menu items are free text, and an apostrophe
in *"Chef's soup"* is not something a scenario should have to know about.

---

#### What this slice does not do

- **It does not arm the alert sound.** Recorded above; the badge count is the assertion §10.3 itself names.
- **It does not assert the §10.2 reminder.** That is scenario 8, and it wants a short
  `KITCHEN_SUBMISSION_REMINDER_SECONDS` rather than sixty seconds of waiting — a harness change, not a
  product one.
- **It does not touch `TableJoin.razor`.** The island's wrapper lives inside the component, so the static
  parent is unchanged.
- **It leaves scenario 5 for its own slice.** Two guests with two live circuits watching each other's
  roster and totals is a different axis from this one, and folding it in here would have made a single
  arrangement serve three scenarios badly.
