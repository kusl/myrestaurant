### M6 Slice 9 — two guests at one table (landed)

§16.3 scenario **5**: *"Second guest joins via fresh token → sees first guest's order live; first guest sees
roster update."* It is the first scenario with two guests in the restaurant at once, and the only one so far
where **every** interesting event is raised by a browser other than the one being asserted on. Scenario 6
already watched a fulfillment cross from the kitchen's circuit to a guest's; this watches a join and a send
cross from one guest's circuit to another's, in the opposite direction, twice.

`MYRESTAURANT_E2E=1` goes from **8 passed / 7 skipped** to **9 passed / 6 skipped**. `dotnet test` stays at
**971 total / 0 failed** — no unit or integration test moved, and the scenario is opt-in like every other.

---

#### What the scenario does

An administrator bootstraps, puts soup and a steak pie on the menu, and creates a table. Then:

1. **The first guest** scans, self-registers with a passkey, joins, and sends one soup. Their roster is
   asserted to hold exactly one person wearing the *"you"* chip, and *"the rest of the table"* to be empty.
2. **The second guest** scans the code the table is showing now — their own browser context, their own
   virtual authenticator, their own account — registers, and joins.
3. **The first guest's roster grows to two, with nobody touching their phone.** `TableJoin.razor` publishes
   `SittingMemberJoined` after the membership row commits (§9: *"fired on: membership insert"*), the first
   guest's circuit re-reads, and the second guest appears without the *"you"* chip.
4. **The first guest's party list stays empty**, which is the assertion that makes §5.2's *"who is here"*
   and §11.1's *"the rest of the table"* two different lists rather than two renderings of one. §6.1 creates
   the `guest_order` row lazily inside a first send and `sitting_bill` is grouped from those rows, so a guest
   who has joined and ordered nothing is on the roster and nowhere near the bill.
5. **The second guest sees the first guest's soup on arrival.** That half comes from the read model, not
   from a notification — their circuit started after the send — and would still hold with §9 switched off.
   It is asserted separately for exactly that reason.
6. **The first guest sends a steak pie ×2, and the second guest's screen grows a line.** No reload, no
   click, no navigation on the second browser: `OrderLinesChanged` to the sitting's members, and the surface
   re-reads. The quantity is asserted as well as the name, because a party list that showed the line and
   lost the number would be a bill nobody could read.
7. **One open sitting, two members, in join order** — read from the rows. From a seat, *"both joined the
   sitting"* and *"a second sitting was opened on the same table and the unique index did not stop it"* look
   identical, and only `table_sitting` plus `table_sitting_member` say which happened.

---

#### "Fresh token" at an hour-long window

§16.3's word is *fresh*, and this scenario deliberately runs at the harness default of 3600 s rather than at
§13's ten-second floor. At an hour the second guest's token is very often the same string as the first
guest's, and that is the right reading: *fresh* means **the code the table is showing at the moment they
scan**, not a code the first guest's has aged out of. Scenario 3 already owns the expiry half — it is built
on the ten-second floor precisely so that the token it scanned is provably dead before the guest joins — and
duplicating that here would only add a clock to race against two registrations and four form posts.

---

#### The one product change: naming a span

`TableOrderSurface.razor`, §11.1's *"the rest of the table"*, rendered another guest's line as:

```razor
<span>@theirLine.Quantity × @theirLine.MenuItemName</span>
```

It is now `<span class="order-party-line-name">`. That was the only text on the ordering surface a reader
had no way to address — the guest's own lines carry `.order-line-name`, the kitchen's carry
`.kitchen-line-name`, and this one was a gap rather than a decision. A **distinct** class rather than
reusing `.order-line-name`, because that one is `font-weight: 600` and the rest of the table is deliberately
quieter than your own order; the new name has no rule behind it and changes nothing on screen.

Nothing else under `src/` moved. No migration, no schema change, no package, no ADR, no `Program.cs`, no
`.slnx`.

---

#### Harness: three new reads, and why each has a wait

`Harness/TableOrderJourneys.cs` gains `ReadRosterAsync` / `WaitForRosterAsync`, `ReadPartyAsync` /
`WaitForPartyAsync`, two records (`TableRosterMember`, `PartyOrder`), and one extracted helper
(`ReadBadgeAsync`) now shared by the guest's own lines and everybody else's.

Both new lists change because of a §9 broadcast started by **another** browser, and there is no click on
this page to await and no navigation to settle. A scenario that read them once would be sampling a race it
cannot see. So both re-read until the predicate holds and then report, in one sentence, what was on screen
when it never did — the same discipline `WaitForBoardAsync` and `WaitForCommittedLinesAsync` already follow,
and for the same reason: *"the roster did not grow"* and *"the broadcast never left the other circuit"* are
indistinguishable from a bare timeout.

`TableRosterMember.IsYou` is read from the presence of the roster row's chip rather than from its word. That
chip is the only thing on the surface that makes the list this reader's view of the table rather than a list
of strings, and step 3 above turns on exactly that distinction.

`PartyOrder.TotalText` is kept as **text**. It is rendered through `MoneyText.Format(amount, CurrencyCode)`,
so parsing it back into a decimal would mean reimplementing a currency formatter inside a test in order to
compare against a number the test already knew. A scenario that cares can assert containment; this one
asserts on the lines, which is what §16.3 scenario 5 is actually about.

`GuestOrderLine` is reused for a party line rather than duplicated. The two lists come from different
sources — your own from the event fold, everybody else's from `order_current_line` — and word the badge
differently (*"At your table"* against *"At the table"*), but both publish the same `chip-ok`, which is the
state rather than the copy. The one asymmetry is that `GuestLineBadge.Removed` cannot occur on a party line
at all: `order_current_line` filters removals out in SQL (`WHERE removed.order_line_identifier IS NULL`), so
a line that was taken off simply is not there.

---

#### `SeatGuestAsync`, extracted

Scan → register with a passkey → join → wait for the circuit, in the guest's own browser context with its
own virtual authenticator. `ArrangeServiceAsync` (scenarios 4 and 6) now calls it instead of inlining the
same five steps; the call sequence is byte-for-byte what it was, so those two scenarios are unchanged in
behaviour. Scenario 5 needs two of these alive at once, which is what turned four lines inside one
arrangement into a method.

Cookies are per-context and a WebAuthn credential belongs to the authenticator that minted it, so a second
guest sharing the first's context would be the first guest with a second passkey — which is a different
scenario, and not one §16.3 asks for.

---

#### What this does not prove

The two guests are seated **sequentially**, not concurrently. §5.1's advisory lock over sitting creation and
§6.6's `FOR SHARE`/`FOR UPDATE` ordering are what make a genuine race safe, and neither is exercised here —
those belong to `MyRestaurant.DataAccess.Tests`, which already drives concurrent sends and a concurrent
close against a real PostgreSQL. A browser scenario that tried to race two joins would be asserting on
Playwright's scheduling rather than on the lock.

---

#### Where scenario 5 sits in the matrix

Live after this slice: **1, 2, 3, 4, 5, 6, 13, 14, 15**. Remaining: **7** (a removal of a fulfilled line
sinks the whole batch with a per-operation reason), **8** (one reminder and exactly one, which wants a short
`KITCHEN_SUBMISSION_REMINDER_SECONDS` rather than sixty seconds of waiting), **9** (a counter price
adjustment read old → new on the guest's screen), **10** (a close, the pending-line warning, and the flip to
a settled read-only view), **11** (hide, the hidden-records filter, unhide — the one that will meet
`EnhancedNavigation` again, on an administrator following a filter link), and **12** (a TOTP reset driving
the obligations pipeline through a forced password change and a forced re-enrollment).

Then the backup/restore drill, and M6 is done.
