# M6 Slice 13 — §16.3 scenario 10, and the write that cannot be undone

Every file below is a **full file** at its **repo-relative path**. Extract at the repo root and the
contents drop straight over your working tree — no diffs, no patches, no scripts to run.

```bash
tar -xzf m6-slice13-close-and-settle.tar.gz -C /home/kushal/src/dotnet/myrestaurant
```

## Files to DELETE

**None.** Nothing here renames, supersedes or orphans anything: no migration, no schema change, no
package change, no ADR edit, no `Program.cs` edit, no `.slnx` edit, no new test folder.

## About "some tests are now failing"

**They aren't.** `claude-terminal.txt` is from the same session as `dump.txt` — same commit, `b6a2892` —
and it is green from top to bottom:

- `dotnet test` — **971 total / 0 failed / 956 succeeded / 15 skipped**
- `bash scripts/ci_local.sh --with-all` — all six gates passed
- `MYRESTAURANT_E2E=1 dotnet test tests/MyRestaurant.EndToEnd.Tests` — **15 total / 0 failed / 12 passed / 3 skipped**
- `run.sh --smoke`, `run.sh --containers-only`, `scripts/quick_tunnel.sh` — all fine

There is no failing assertion anywhere in that output. The 15 skips under a plain `dotnet test` are the
E2E scenarios opting out because `MYRESTAURANT_E2E` is unset; the 3 under `MYRESTAURANT_E2E=1` are the
three unimplemented §16.3 scenarios, each carrying `PendingHarnessExtension`. This slice takes one of
them, leaving two.

## The files

| File | Change |
| --- | --- |
| `tests/MyRestaurant.EndToEnd.Tests/EndToEndScenarios.cs` | §16.3 scenario **10** implemented; its placeholder removed |
| `tests/MyRestaurant.EndToEnd.Tests/Harness/CounterJourneys.cs` | the close: `BeginCloseAsync`, `ConfirmCloseAsync`, `ReadPendingWarningAsync`, `ReadSettledTillAsync`, `ReadFloorAsync` |
| `tests/MyRestaurant.EndToEnd.Tests/Harness/TableOrderJourneys.cs` | `ReadTotalsAsync`, `ReadSettledViewAsync`, `WaitForSettledViewAsync`, `DescribeSettledView` |
| `tests/MyRestaurant.EndToEnd.Tests/Harness/RestaurantInstance.cs` | `ReadSettledSittingAsync` + the `SettledSitting` record |
| `src/MyRestaurant.WebApplication/Components/Pages/Counter/CounterSitting.razor` | two ids on the close buttons |
| `src/MyRestaurant.WebApplication/Components/Pages/Table/TableOrderSurface.razor` | one class on the settled heading |
| `docs/BUILD_PROGRESS.md` | Slice 13 appended (**complete file**, 5,325 lines — I did the appending) |
| `_CHANGES.md` | this file |

Before editing, I checked all seven pre-existing files against the SHA-256 hashes `export.sh` recorded in
`dump.txt`. All seven matched, so every byte I did not touch is known to be identical to your working
tree.

## Seven readings of one number

"Totals match" is the half of §16.3's sentence that could most easily be satisfied by nothing at all. Six
of these are computed at render time from the same `sitting_bill` view, so all six could agree perfectly
on a close that stamped **no total whatsoever**. They are still worth comparing — different code, two
languages, three circuits — but only the seventh makes the claim §5.3 actually promises.

| # | Where | How it is computed |
| --- | --- | --- |
| 1 | The till header, before the close | SQL sum over `sitting_bill` |
| 2 | §11.3's confirmation prompt | `CurrentTotalAmount`, quoted directly |
| 3 | The till header, after the close | the stamped `settled_total_amount` |
| 4 | The till's settle panel | C# sum over the per-person entries |
| 5 | The guest's "Table total" | C# sum over `sitting_bill`, another circuit |
| 6 | The guest's "Your total" | C# sum filtered to one person, another circuit |
| 7 | `table_sitting.settled_total_amount` | the column, read past every surface |

Reading **2** earns its place alone: it is the last number a person reads before an irreversible write,
and it comes from a third expression rather than either sum beside it.

Reading **6** is asserted rather than assumed. One guest is at the table, so their own total *is* the
table's — which means a filter that had stopped filtering shows the right number for the wrong reason,
and only in a party of one. That is exactly this scenario.

Every figure is derived from the prices the scenario created. `soup + 2 × pie`, never `34.50`.

## One soup delivered, two pies that never arrive

- **The warning must name one line, not two portions.** §11.3 renders `PendingLineCount`, a count of
  unfulfilled rows in `order_current_line`. The pie is *one* row at quantity two, so a warning that had
  started counting portions says "2" and this asserts **1**. In production that is a counter being told
  the wrong thing at the moment they decide whether to charge somebody.
- **The stamped total must include food nobody ate.** §5.3's "knowingly charge" is the whole point: told,
  settled anyway, charged. A close that quietly dropped the undelivered line would be smaller on all
  seven readings and perfectly self-consistent.
- **At one of each item, wrong arithmetic gives the right answer.** A total summing *unit* prices instead
  of extensions, or double-counting the delivered line, is indistinguishable from the correct total at
  quantity one. At one soup and two pies each is a different number.

The pie is asserted still-undelivered *after* the close, on both the till and the phone. A surface that
re-badged it at settlement would be telling the guest their food arrived — the one fact on that bill they
might want to argue about.

## Why a counter account, when the screen does not branch on role

Scenario 9 needed one because §6.2 records the actor and §11.1 renders it, so the assertion would
otherwise have been about the wrong role **and would have passed**. This is the mirror image, and worth
stating plainly: `CounterSitting.razor` gates every control on `_sitting.IsOpen` and never consults the
principal, so an administrator sees the identical read-only screen and the assertion passes either way
**today**.

It is the direction of the *next* failure that decides it. §6.5.8 admits nothing but an administrator's
corrective events after a close, and §5.3 says corrections "are an administrator's". The day this surface
grows the correction panel those sections describe, an administrator at a settled till will *correctly*
see controls a counter must not. Asserting "read-only" as an administrator is asserting it for the one
role permitted to act after a close; the counter is the role for which read-only is unconditional, and
that is the claim §11.3 makes.

The administrator still covers the pass for the single fulfillment, on scenarios 4, 6, 7 and 8's
reasoning. A second staff account for one tap would be a sign-in and a forced password change nothing
here asserts on.

## The two product changes

Both additive, no CSS behind either new name, and both `.razor` tag trees verified **identical** to the
pristine files from `dump.txt`. Nothing changes on screen.

**`CounterSitting.razor` — `#counter-close` and `#counter-close-confirm`.** The two buttons are otherwise
the same selector: "the primary button in the settle section" is *Close & settle* before the prompt and
*Yes — close & settle* after it. Nothing outside the markup could tell "I opened the confirmation" from
"I settled the table", and settling cannot be undone (§5.3) — which makes this the one place in the
application where that distinction is most worth having in the markup rather than inferred from which
panel happens to be on screen. They live in exclusive branches, so each is unique in the document.

**`TableOrderSurface.razor` — `.order-settled-heading`.** Everything else identifying §11.1's settled view
is an *absence* — no picker, no Send row, no removal ticks — and a surface whose circuit died has none of
those either, so a wait keyed on the picker leaving is satisfied by a dead page while proving nothing.
This heading is the one positive marker. "The second `h2` on the surface" was never a way to name it:
that is also the heading of the live ordering view. Fourth time now, after `.order-line-adjustment`,
`.order-prune-notice` and `.order-party-line-name`.

## Two harness decisions worth knowing about

**The close is two calls.** `BeginCloseAsync` returns the prompt, `ConfirmCloseAsync` accepts it. A
composite would settle the table before the scenario could read the prompt, and a settled sitting renders
no prompt to go back for. Same reasoning that kept `SignInWithPasswordAsync` and
`CompleteForcedPasswordChangeAsync` apart in Slice 12.

**`AlreadyClosed` returns normally.** The sitting really is settled and the view really does flip; only
`SittingNotFound` — a problem with no flip — is a failure. So `ConfirmCloseAsync` polls for the read-only
note **before** the refusal, or a losing race would be reported as a fault.

## Build/test checklist

```bash
cd /home/kushal/src/dotnet/myrestaurant

# 1. Two .razor files and four .cs files.
dotnet build
#    expect: all seven projects succeed, 0 errors

# 2. Unchanged from Slice 12's baseline.
dotnet test
#    expect: total 971, failed 0, succeeded 956, skipped 15
#    Scenario 10 moves from [Fact(Skip = ...)] to [Fact] + Assert.SkipUnless; xUnit counts both as
#    skipped, so with MYRESTAURANT_E2E unset every number is identical.

# 3. The strict build — warnings are errors under ContinuousIntegrationBuild.
bash scripts/ci_local.sh --with-all

# 4. The point of the slice.
MYRESTAURANT_E2E=1 dotnet test tests/MyRestaurant.EndToEnd.Tests
#    expect: total 15, failed 0, 13 passed, 2 skipped
#    Scenario 10 adds roughly 25-30s and waits on no timer.
```

There is no .NET SDK in my sandbox; I have run none of this. What I did run, on every edited file:
brace/paren/bracket balance **and a depth walk** (never negative, ends at zero) with strings and comments
stripped; a CS4007 scan (no `await` inside any interpolation hole); a CS1620 scan confirming every
additive operand inside every `string.Create(...)` is an interpolated string; the Razor tag-tree
comparison above; an existence check of all **twenty-seven** selectors the new harness code depends on
against the markup it targets; and the SHA-256 comparison of all seven pre-edit files.

## If it goes red

| Message begins | What it means |
| --- | --- |
| `The counter board has no open table labelled 'E2E Ten'…` | The guest's join never opened a sitting, or `/counter` refused the counter principal. §3.7 admits counter and administrator; a failed policy shows the access-denied panel and the message quotes the URL. |
| `The browser is not on /account/change-password-required…` | §3.5's obligation (1) did not fire for a §3.7 account. A real defect — `CreateStaffAsync` writes `must_change_password` and `ObligationsMiddleware` must intercept the sign-in's own navigation. |
| `Assert.NotNull` on `warning` | §5.3's pending-line warning is absent while a line is unfulfilled. That is the section's central requirement — a counter settling a bill without being told what has not arrived. |
| `Assert.Equal() Failure: Expected 1, Actual 2` on `warning.LineCount` | The warning is counting portions rather than unfulfilled rows. Check `CounterSittingSummary.PendingLineCount`. |
| `The till is not offering Close & settle…` | The sitting was already settled — an end-of-day pass (§5.4) or another till. Nothing else in this scenario closes anything, so this would be genuine. |
| `Pressing Close & settle did not raise §11.3's confirmation.` | `#counter-close` did not land, or there is no circuit. The button sets one field and cannot be refused. |
| `The close was refused and the sitting is still open…` | `CloseSittingOutcome.SittingNotFound`, with the till's own words quoted. |
| `The sitting neither settled nor refused within 30s…` | Either the click never dispatched, or the §6.6 `FOR SHARE`/`FOR UPDATE` pair is genuinely contended — and nothing else here is writing. |
| `Assert.Equal() Failure: Expected "Settled total", Actual "Running total"` | `closed_at` was not set, or the page did not re-read after the commit. |
| `no §6.7 correction has been made, so no corrected total should be shown` | The stamped total and the live total diverged on their own, which §5.3 says cannot happen. The most serious thing this scenario can find. |
| `Assert.Equal() Failure` on `settled.TableTotalText` but the header is right | The SQL view and the C# sum disagree — `sitting_bill` versus `_bill.Sum(…)`. |
| `a settled sitting must offer the guest nothing to order` | §11.1's flip did not remove the ordering apparatus. The message names what is still on screen. |
| `The guest's surface never flipped to §11.1's settled view…` | §9's `SittingClosed` never left the till's circuit, or the guest's is not listening. |
| `Assert.Equal() Failure` on `row.SettledTotalAmount` | The stamped column disagrees with every screen. This is reading 7 and the only one that is not another rendering of the same query. |
| `Assert.Null` on `ReadOpenSittingAsync` | `closed_at` was not set, so the table still has an open sitting and the next guest to scan would rejoin a settled one. |
| `Assert.Single() Failure` on `floor.Settled` | The table is missing from §11.3's "Settled today" list, or is on it twice. |

`RestaurantInstance.DiagnosticOutput` carries the web application's console tail if a page 500s.

## What is next

Scenario **11** — a guest hides a closed order, it leaves their own history while staff and admin views
are unchanged, an administrator filters the hidden-records view by username, and Unhide restores it. It
inherits this slice's close directly: a hideable order is a *settled* one, so scenario 11's arrangement
is scenario 10's ending. It will also meet `EnhancedNavigation` again, on an administrator following a
filter link.

Then **12** — a TOTP reset driving §3.5's pipeline through a forced password change *and* a forced
re-enrollment, which inherits `CompleteForcedPasswordChangeAsync` and needs only the second obligation
beside it.

Then the backup/restore drill, and M6 is done.

## The one-line why

Every scenario before this one asserts on something that could be done again; this is the first whose
subject is a one-way door, which is why it checks the number against the column it was stamped into
rather than against another rendering of the same query.
