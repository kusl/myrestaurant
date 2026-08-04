# M6 Slice 14 — §16.3 scenario 11, and a red test that was reading the stylesheet

Every file below is a **full file** at its **repo-relative path**. Extract at the repo root and the
contents drop straight over your working tree — no diffs, no patches, no scripts to run.

```bash
tar -xzf m6-slice14-hide-and-unhide.tar.gz -C /home/kushal/src/dotnet/myrestaurant
```

## Files to DELETE

**None.** Nothing here renames, supersedes or orphans anything: no migration, no schema change, no
package change, no ADR edit, no `Program.cs` edit, no `.slnx` edit, no new test folder.

## About the failing test — this time it is real

Slice 13's `_CHANGES.md` argued that "some tests are now failing" was mistaken, and for that dump it was.
It is not for this one. `claude-terminal.txt` on commit `aee8e40` shows, twice:

- `dotnet test` — **971 total / 0 failed** (still green; the E2E scenarios skip when `MYRESTAURANT_E2E` is unset)
- `MYRESTAURANT_E2E=1 dotnet test tests/MyRestaurant.EndToEnd.Tests` — **15 total / 1 failed / 12 passed / 2 skipped**

```
MyRestaurant.EndToEnd.Tests.EndToEndScenarios.Counter_ClosesSitting_TableFlipsToSettledAndTotalsMatch [FAIL]
  Assert.Equal() Failure: Strings differ
              ↓ (pos 1)
  Expected: "Settled total"
  Actual:   "SETTLED TOTAL"
```

## The files

| File | Change |
| --- | --- |
| `tests/MyRestaurant.EndToEnd.Tests/Harness/ScreenText.cs` | **new** — reads declared text rather than rendered text |
| `tests/MyRestaurant.EndToEnd.Tests/Harness/HistoryJourneys.cs` | **new** — the guest's own history and §6.8's Hide |
| `tests/MyRestaurant.EndToEnd.Tests/Harness/HiddenRecordJourneys.cs` | **new** — §11.4's list, its filter, its expanded record, its Unhide |
| `tests/MyRestaurant.EndToEnd.Tests/EndToEndScenarios.cs` | §16.3 scenario **11** implemented; its placeholder removed |
| `tests/MyRestaurant.EndToEnd.Tests/Harness/CounterJourneys.cs` | the label fix; `OpenSettledSittingAsync` + `PathFor` |
| `tests/MyRestaurant.EndToEnd.Tests/Harness/TableOrderJourneys.cs` | the totals-term fix (one line) |
| `src/MyRestaurant.WebApplication/Components/Pages/Table/TableHistory.razor` | two additive markup names |
| `src/MyRestaurant.WebApplication/Components/Pages/Administration/HiddenRecords.razor` | two additive markup names |
| `docs/BUILD_PROGRESS.md` | Slice 14 appended (**complete file**, 5,441 lines — I did the appending) |
| `_CHANGES.md` | this file |

Before editing, all six pre-existing files were checked against the SHA-256 hashes `export.sh` recorded
in `dump.txt`. All six matched, so every byte I did not touch is known identical to your working tree.

## Part one: the red test, and the second one behind it

`InnerTextAsync` returns the browser's own `innerText`, which is **defined in terms of layout** and
therefore has `text-transform` already applied. `CounterSitting.razor` upcases
`.counter-detail-total-label` for the eyebrow treatment. The label the component wrote really is
`Settled total`; the harness was reading the presentation layer.

**Forty lines further on, the same mistake was waiting.** `TableOrderJourneys.ReadTotalsAsync` reads each
`<dt>` of §11.1's totals list with `InnerTextAsync` and looks the result up in a dictionary keyed on
`"Your total"` and `"Table total"` — and `app.css` line 1120 upcases `.order-totals dt`. Both lookups
would have missed, and the method would have thrown

> §11.1's totals list does not carry both 'Your total' and 'Table total'

about a totals list that was entirely correct. It is reached from `WaitForSettledViewAsync` at
`EndToEndScenarios.cs:1528` — forty-four lines past the assertion that failed first. **Fixing only line
1484 would have moved the red rather than cleared it**, and the second failure would have looked like a
product defect in §11.1.

So the whole surface was swept before either fix: **24** `text-transform` declarations in `src/` against
**66** `InnerTextAsync` calls in the harness. Those two are the only collisions. `.eyebrow`,
`.chip-role`, `.manage-label`, `.hidden-facts dt`, `.event-stream-badge`, `.restaurant-clock-label`,
`.kitchen-menu-state`, `.display-eyebrow` and the rest are never read; `p.pairing-code`, `p.totp-secret`
and `p.staff-temporary-password` carry no transform.

`ScreenText.DeclaredAsync` reads `TextContentAsync` and collapses whitespace runs, which makes it exactly
`InnerTextAsync` minus the transform. **It is deliberately not a blanket replacement.** The distinction is
what the comparison is about: a *label* read — holding the phrase the component was expected to choose,
"Running total" against "Settled total" — is a claim about which branch it took, and presentation casing
is noise in it. A read of *content* — a table's label, a person's name, an amount through
`MoneyText.Format` — is data that no rule in this application transforms, and `InnerTextAsync`'s
whitespace normalisation is genuinely convenient there. Two sites changed; sixty-odd left alone.

## Part two: scenario 11

### Why there are two guests

With a single guest, *"their history is empty afterwards"* is satisfied equally well by:

- a page that stopped rendering its list,
- a reader that started returning nothing for everybody,
- a hide that hid the whole **sitting** rather than one order.

All three are catastrophic and all three pass. A bystander whose own history is unchanged across the same
write separates *this order was hidden* from *history broke* — and costs one registration rather than the
second sitting a per-order claim would otherwise need.

### Four numbers, none of which can be confused with another

| Figure | Value from the prices this scenario created |
| --- | --- |
| The hider's own share | `soup + 3 × pie` |
| The bystander's share | one `soup` |
| The table's stamped total | their sum |
| The pie's unit price | neither of the above |

A history page showing the **table's** total where a **person's** belongs cannot pass by coincidence, and
a page showing a unit price where an extension belongs reads `14.00` against a quantity of three.

### The identifiers

Both come off links the surfaces rendered — `?hide=` on the guest's Hide link, `?record=` on
administration's expand link — which is the same recovery `AdministrationJourneys.CreateTableAsync` already
does from a "Manage this table" link. Nothing is read out of the database and no `data-` attribute was
added for the harness.

*"A row appeared"* is satisfied by any hidden order in the restaurant. That the row administration found
**is** the order this guest hid is a claim about those two identifiers agreeing, and it is the reason the
apparatus exists.

### The filter, in both directions

§16.3 names the positive case. The negative one is what stops it being vacuous: a filter that had quietly
stopped filtering would return this row for every username there is, and would satisfy the positive case
perfectly. The two usernames are chosen so neither is a substring of the other — §6.8's match is a literal
substring (`DapperOrderHistoryReads.SubstringPattern`), and `.one` against `.one.b` would make the
assertion pass or fail for reasons of spelling.

The filter is **typed into the form and submitted**, not appended to the URL. §16.3 says the administrator
*filters*; a query string assembled by the harness would exercise `[SupplyParameterFromQuery]` while
skipping the form, the labels and the round trip.

### Three things re-read from the server rather than from a DOM already on screen

A stale document agrees with *"nothing changed"* without having been asked. So:

- **the bystander's history** — nothing broadcasts a hide to another guest's circuit, and the page is
  static SSR anyway;
- **the till's bill** — through §11.3's closed-sitting lookup, because the administrator's browser has
  been on that page since the close;
- **`table_sitting.settled_total_amount`** — past every surface, because §6.8 changes a visibility flag
  and §5.3 promises the stamped total is never rewritten. A hide that had reached the money would be a
  defect no screen above could distinguish from correct behaviour, because all of them would agree.

### Why no counter account, when scenarios 9 and 10 both made one

There the role was load-bearing: §6.2 records who adjusted a price and §11.1 renders it, and §11.3 makes
read-only unconditional for a counter and conditional for an administrator. Here the close is
*arrangement* rather than subject — §6.8 refuses a hide on an open sitting, so this scenario needs a
settled one and does not care who settled it. §3.7 admits administrators to `/counter`, and a staff account
would add a sign-in and a forced password change this scenario never looks at.

## The four product changes

All additive, no CSS behind any of the new names, **nothing changes on screen**. Both `.razor` tag trees
were walked and are balanced and unchanged in structure.

**An `id` on each `section.panel`** — `#table-history-surface`, `#hidden-records-surface`. Every other
surface the harness reads has one (`#table-order-surface`, `#counter-sitting-surface`,
`#kitchen-board-surface`, `#table-display-surface`), and these two pages carry `p.status-success` and
`p.status-error`, which are the same classes every surface in the application uses — a document-wide match
would read whatever the layout happened to be saying. `#table-history-surface` also gives the enhanced-nav
click on the Hide link a scoping root that is genuinely absent from every other page.

**A class on each empty-list sentence** — `.history-none`, `.hidden-none`. Each was a `p.lede` among the
page's other `p.lede`s, reachable only by position. On `HiddenRecords.razor` that one element carries two
different sentences through a ternary, and the difference is load-bearing: *"Nothing hidden matches that"*
says the filter excluded everything, while *"Nothing is hidden anywhere in the restaurant"* is the stronger
claim and the one an unhide has to produce. The scenario asserts both, separately.

Note also that `Orders.Count == 0` and *the page says it has nothing* are two claims, not one. A list that
failed to render is also empty.

## Two harness decisions worth knowing about

**Two facts were deliberately not given fields.** §11.4 renders a hidden row's table label between a
username and a timestamp in one sentence, and its line count as the second of two
`span.hidden-record-note`s — so reaching either means splitting prose or indexing siblings. Both are
asserted where they have elements of their own, on the guest's history page. A harness field that could
only be filled by counting siblings starts lying the day a third note is added.

**The two `ol.hidden-events` lists are told apart by content, not position.** An expanded record draws the
visibility log and the event log under the same class. The obvious separator is the heading above —
`h3:has-text('Visibility log') + ol` works today, and `h3:has-text('The record') + ol` already does not,
because a paragraph sits between them. The stable difference is structural: a stored event wraps its
metadata in `div.hidden-event-head` because it has a sequence number to put beside the type, and a
visibility event has no such wrapper. So every `li` is walked once and sorted by whether it contains that
element — no `:has()`, no `:not()`, no sibling combinators, nothing for a selector engine to disagree
about.

## One new method on CounterJourneys

`OpenSettledSittingAsync(page, sittingIdentifier, timeout)`. `OpenSittingAsync` finds a table on §11.3's
**open**-sittings list and a settled table has left it, so this is by identifier — which the caller holds,
because `OpenSittingAsync` returned it.

It waits on the read-only note as **part of the barrier**, not as a bonus assertion: the route renders the
identical component for an open sitting, so waiting only on the surface would return happily from a bill
that had never been settled — and every caller is re-reading one *because* it is settled, to establish that
something which happened elsewhere left it alone.

## Build/test checklist

```bash
cd /home/kushal/src/dotnet/myrestaurant

# 1. Two .razor files, three new .cs files, three edited .cs files.
dotnet build
#    expect: all seven projects succeed, 0 errors

# 2. Unchanged from Slice 13's baseline.
dotnet test
#    expect: total 971, failed 0, succeeded 956, skipped 15
#    Scenario 11 moves from [Fact(Skip = ...)] to [Fact] + Assert.SkipUnless; xUnit counts both as
#    skipped, so with MYRESTAURANT_E2E unset every number is identical.

# 3. The strict build — warnings are errors under ContinuousIntegrationBuild.
bash scripts/ci_local.sh --with-all

# 4. The point of the slice.
MYRESTAURANT_E2E=1 dotnet test tests/MyRestaurant.EndToEnd.Tests
#    expect: total 15, failed 0, 14 passed, 1 skipped
#    Scenario 10 goes green on the ScreenText fix. Scenario 11 adds roughly 35-40s and waits on no timer.
#    The one remaining skip is scenario 12.
```

There is no .NET SDK in my sandbox; I have run none of this. What I did run:

- **SHA-256** comparison of all six pre-edit files against the hashes `export.sh` recorded — all six matched;
- brace/paren/bracket **balance and a depth walk** (never negative, ends at zero) with strings and comments
  stripped, across every file in the test project;
- a **CS4007** scan — no `await` inside any interpolation hole;
- a **CS1620** scan — every additive operand inside every `string.Create(...)` is an interpolated string;
- a **Razor tag-tree walk** of both edited components;
- an **existence check of all 36 selectors** the two new journey classes depend on, against the markup they
  target;
- a check that **none of the four new names has a CSS rule anywhere**, so nothing changes on screen.

## If it goes red

| Message begins | What it means |
| --- | --- |
| `Assert.Equal() Failure: Expected "Settled total", Actual "SETTLED TOTAL"` | `ScreenText` did not take effect — check that `CounterJourneys.ReadSettledTillAsync` is calling `DeclaredAsync` for the label. |
| `§11.1's totals list does not carry both 'Your total' and 'Table total'` | The same, on the second site: `TableOrderJourneys.ReadTotalsAsync` line ~794. |
| `/table/history never rendered its own surface…` | Either the `id` did not land on `section.panel`, or the guest's cookie failed the §11.1 table policy — the message quotes the URL, and the access-denied panel is what a policy failure looks like. |
| `The order at position N offers no Hide link…` | The list was read while a confirmation panel was open. §11.1 withholds the link from exactly that row. |
| `The confirmation on screen would post order 'X' rather than Y` | §6.8's confirmation opened on the wrong row. A real defect, and the serious kind: there is no undo from the guest's account. |
| `Hiding … was refused, so nothing was written` | One of §6.8's three refusals, quoted verbatim. `SittingStillOpen` would mean the close in step (c) did not commit. |
| `Assert.Contains("Nothing here yet")` on `alphaAfter.EmptySentence` | The order is gone from the list but the page draws no sentence — which means the list failed to render rather than that the order was hidden. |
| `Assert.Single()` on `bravoAfter.Orders` | The hide reached somebody else's history. §6.8 scopes it to one order and one owner. |
| `Assert.Equal(2, afterHide.People.Count)` | The hide reached the till. §6.8: the order is "still on its sitting's bill". |
| `Assert.Equal` on `row.SettledTotalAmount` | The hide reached the money. §5.3 says the stamped total is never rewritten, and this is the only reading that can tell. |
| `Narrowing the hidden-records list to '…' never left '…'` | The GET form did not navigate. The one benign cause is two consecutive filters by the same username, which this scenario does not do. |
| `The hidden-records filter came back holding '…'` | The server disagrees about what it was asked, so the list is answering a different question. |
| `Assert.Empty` on `wrongOwner.Rows` | The username filter is not filtering — it returned another owner's hidden order. |
| `There is no hidden order … in the list to expand` | The row left the list between the read and the expand, or the filter excludes it. |
| `Assert.Equal("Hidden by the owner", onlyEvent.Description)` | The visibility log records the wrong event type, or more than one row exists for a single hide. |
| `§11.4 must show the order's stored events under a hidden record` | The hide took the event log with it. ADR-0002 says the log outlives the state. |
| `Assert.Contains("anywhere in the restaurant")` | The unfiltered list still holds something after the unhide, or the empty sentence chose the narrowed branch with no filter set. |
| `Assert.Single()` on `restored.Orders` | The unhide did not restore visibility — `order_visibility_current` should now answer false for this order. |

`RestaurantInstance.DiagnosticOutput` carries the web application's console tail if a page 500s.

## What is next

Scenario **12** — an administrator resets a TOTP-enrolled user, who then signs in with a password and is
driven through §3.5's pipeline twice: a forced password change *and* a forced TOTP re-enrollment, landing
home. It inherits `CompleteForcedPasswordChangeAsync` from Slice 12 and needs only the second obligation
beside it, plus the passkey path hitting the same pipeline.

Then the backup/restore drill, and M6 is done.

## The one-line why

Every scenario before this one asserts that something happened; this is the first whose subject is a record
that is still there, which makes its central claim a list of things that did **not** move — and the reason
it needed a second guest, three server re-reads and a column read past every screen to say anything at all.
