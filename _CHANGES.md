# M6 Slice 12 — §16.3 scenario 9, and the first staff account

Every file below is a **full file** at its **repo-relative path**. Extract at the repo root and the
contents drop straight over your working tree — no diffs, no patches, no scripts to run.

```bash
tar -xzf m6-slice12-price-adjustment.tar.gz -C /home/kushal/src/dotnet/myrestaurant
```

## Files to DELETE

**None.** Slice 11 asked for the eighteen files under `docs/_append/`, and commit `ce95628` ("delete
append files") did it — that directory is gone from the dump, so the request is discharged. Nothing in
this slice renames, supersedes or orphans anything: no migration, no schema change, no package change, no
ADR edit, no `Program.cs` edit, no `.slnx` edit.

## About "some tests are now failing"

**They aren't.** `claude-terminal.txt` is from the same session as `dump.txt` — its cloudflared
timestamps are UTC, and 20:23 EDT is 00:23Z — and it shows `dotnet test` at **971 total / 0 failed / 956
succeeded / 15 skipped**, `scripts/ci_local.sh --with-all` green across all six gates, and
`MYRESTAURANT_E2E=1` at **15 total / 0 failed / 11 succeeded / 4 skipped**. Slice 11's CS4007 fix landed
and the fifteen-test gap it explained is closed. There is no failing assertion anywhere in that output.
The four remaining skips are the four unimplemented §16.3 scenarios, each carrying
`PendingHarnessExtension`. This slice takes one of them.

## The files

| File | Change |
| --- | --- |
| `tests/MyRestaurant.EndToEnd.Tests/EndToEndScenarios.cs` | §16.3 scenario **9** implemented |
| `tests/MyRestaurant.EndToEnd.Tests/Harness/CounterJourneys.cs` | **new** — the till: board, bill, price adjustment |
| `tests/MyRestaurant.EndToEnd.Tests/Harness/AdministrationJourneys.cs` | `CreateStaffAccountAsync`, `StaffRoles`, `StaffAccount` |
| `tests/MyRestaurant.EndToEnd.Tests/Harness/AccountJourneys.cs` | password sign-in; §3.5's forced password change; one widened selector |
| `tests/MyRestaurant.EndToEnd.Tests/Harness/TableOrderJourneys.cs` | `GuestPriceAdjustment`, `GuestOrderLineDetail`, `ReadOwnLinesAsync`, `WaitForOwnLineAsync` |
| `tests/MyRestaurant.EndToEnd.Tests/Harness/RestaurantInstance.cs` | `CurrencyCode` named rather than a literal |
| `src/MyRestaurant.WebApplication/Components/Pages/Counter/CounterSitting.razor` | `data-live`, a surface id, two input ids |
| `src/MyRestaurant.WebApplication/Components/Pages/Table/TableOrderSurface.razor` | one added class |
| `src/MyRestaurant.WebApplication/Components/Pages/Administration/CreateStaff.razor` | one added class |
| `docs/BUILD_PROGRESS.md` | Slice 12 appended (**complete file**, 5,066 lines — I did the appending) |
| `_CHANGES.md` | this file |

## Why scenario 9 needed a real staff account

Scenarios 4, 6, 7 and 8 deliberately put an administrator at the pass, and said why each time. That
reasoning does not survive here.

The thing under test is a sentence on a guest's screen, and §6.2 binds a `price_adjustment` to counter
**or** administrator and records which. `CounterSitting.razor` reads the actor role off the principal —
`counter` wins when somebody holds both, because that is the capacity they are standing at the till in —
so an administrator adjusting the price renders *"by an administrator"*. The assertion would then be
about the wrong role, **and it would pass**. Who acted is part of the claim.

So the scenario walks §3.7 → §3.5 on the way in: the create-staff form, the generated temporary password,
a sign-in that lands on `/account/change-password-required` rather than anywhere it asked for, and the
change that clears the obligation. That landing is asserted explicitly rather than absorbed into a helper
— a §3.7 account carries `must_change_password`, and a counter who could reach the till on a password an
administrator can still read off a screen is a hole, not an inconvenience.

## Quantity two, and why the number matters

The pie is ordered **two** at 14.00 and adjusted to 11.00. §6.5.7 adjusts a *unit* price and §11.1
renders the *extension* — so the unit must read 11.00 **and** the line must read 22.00, and a surface that
wrote the sentence without recomputing the money fails the second while passing the first. At quantity one
those are the same number twice.

The soup is the control: one line adjusted, not the ticket.

Both totals are **derived in the scenario** from the prices it actually created, not restated as
constants. A restated total is a second place the fixture lives, and it goes quietly wrong the day
somebody changes a price for another scenario's sake while every assertion still passes.

## The three product changes

All additive; no CSS stands behind any new class and nothing changes on screen.

**`CounterSitting.razor` — `data-live`, and this one is worth having regardless of any test.** A
prerendered till is the dangerous kind of broken because it is the kind that looks right: the bill is
correct as of the request, every total adds up, and Adjust price, Remove, Add to the bill and Close &
settle are all `@onclick` handlers with no circuit behind them. Pressing any of them does nothing —
no refusal, no flash, no error — and the screen never hears §9 either. Same attribute, same reasoning, as
`KitchenBoard`'s and `TableOrderSurface`'s.

**`CounterSitting.razor` — `id` on each price-editor input.** They were previously distinguishable only by
`inputmode` on one and `maxlength` on the other, which is not something outside the markup should have to
know. Only one editor is ever open (`StartAdjust` calls `CancelEditors`, and `_adjustingLine` holds a
single line), so an id is unique in the document; the wrapping `<label>` still associates each input
implicitly, so no accessible name changes.

**`.order-line-adjustment`** — the removal sentence directly above carries the identical
`.order-line-detail`, so "the detail paragraph under this line" was never a way to name this one, and on a
line both adjusted and removed it would have named both.

**`.staff-temporary-password`** — that element holds a password and borrowed `.totp-secret` for its
monospaced treatment. Reading a password out of something named for a TOTP secret breaks silently the day
that page grows a real authenticator panel.

## Two things I found while reviewing my own code

**A CS4007, caught and fixed before packaging.** `ReadPriceAdjustmentsAsync` had
`{(await previous.CountAsync() == 0 ? … : …)}` inside an interpolation hole of a string binding to
`DefaultInterpolatedStringHandler`. Same class of error as Slice 11's, in code written the same day I
wrote about it. Both counts are now hoisted into locals first. I re-scanned every `.cs` file in the
project afterwards: no `await` inside any interpolation hole remains.

**A selector that could never have matched.** `AccountJourneys.DescribeSurfaceAsync` looked for
`p.status-error`, but `ChangePasswordRequired.razor` renders its refusals as a `ul.status-error` of `<li>`
elements because Identity hands back a list — so the one page whose entire job is to refuse would have
described itself as reporting no error, on the exact journey this slice adds. Widened to `.status-error`;
the old match set is a subset, so no existing caller changes.

I also dropped a composite `SignInAsStaffForTheFirstTimeAsync` I had written: scenario 9 asserts on the
page in between the two halves, scenario 12 will want the same granularity, and shipping an unused
`internal` method is clutter.

## Build/test checklist

```bash
cd /home/kushal/src/dotnet/myrestaurant

# 1. Three .razor files and six .cs files.
dotnet build
#    expect: all seven projects succeed, 0 errors

# 2. Unchanged from Slice 11's baseline.
dotnet test
#    expect: total 971, failed 0, succeeded 956, skipped 15
#    Scenario 9 moves from [Fact(Skip = ...)] to [Fact] + Assert.SkipUnless; xUnit counts both as
#    skipped, so with MYRESTAURANT_E2E unset every number is identical.

# 3. The strict build - warnings are errors under ContinuousIntegrationBuild.
bash scripts/ci_local.sh --with-all

# 4. The point of the slice.
MYRESTAURANT_E2E=1 dotnet test tests/MyRestaurant.EndToEnd.Tests
#    expect: total 15, failed 0, 12 passed, 3 skipped
#    Scenario 9 adds roughly 20-25s: a /setup wizard, two menu items, a table, a staff account, a
#    guest registration, two Argon2id verifies and two hashes, and no waiting on any timer.
```

There is no .NET SDK in my sandbox; I have run none of this. What I did run, on every edited file:
brace/paren/bracket balance **and a depth walk** (never negative, ends at zero) with strings and comments
stripped; a Razor tag-structure comparison against the pristine file from `dump.txt`, confirming all three
`.razor` edits leave the tag tree **identical**; the CS4007 scan above; a CS1620 scan confirming every
additive operand inside every `string.Create(...)` is an interpolated string; and an existence check of all
sixteen selectors the new harness code depends on against the markup it targets.

## If it goes red

| Message begins | What it means |
| --- | --- |
| `The till never became interactive within 30s...` | `data-live` did not land on `CounterSitting.razor`, or `IsLiveAttributeValue` is missing from its `@code` block. Check the `<section class="panel counter-sitting-page" id="counter-sitting-surface"` opening tag has both new attributes. |
| `The counter board has no open table labelled 'E2E Nine'...` | The guest's join never opened a sitting, or `/counter` refused the principal. §3.7 admits counter and administrator; a failed policy shows the access-denied panel instead, and the message quotes the URL. |
| `The browser is not on /account/change-password-required...` | §3.5's obligation (1) did not fire for a §3.7 account. That is a real defect — `CreateStaffAsync` writes `must_change_password = true`, and `ObligationsMiddleware` must intercept the sign-in's own navigation. |
| `Pressing Adjust price on 'Steak pie' did not open the editor.` | The two input ids did not land, or the sitting is already settled (§11.3 renders no line controls on a closed sitting). |
| `Adjusting 'Steak pie' to $11.00 was refused...` | A real refusal under the §6.6 lock, with the till's own reasons quoted. Nothing else in this scenario writes concurrently, so this would be genuine. |
| `Assert.Equal() Failure: Expected $22.00, Actual $28.00` on `adjusted.PriceText` | The sentence was written but the extension was not recomputed — the exact failure quantity two exists to separate. |
| `Assert.Contains() Failure ... "the counter"` | The actor role was recorded or rendered as something other than `counter`. Check `CounterSitting.razor`'s `_actorRole` and `TableOrderSurface.razor`'s `DescribeRole`. |
| `§11.1 requires a price adjustment to be shown old -> new, and this one is missing...` | One of the two amounts stopped rendering. The message names which, and quotes the sentence on screen. |
| `Assert.Equal() Failure` on `RunningTotalText` but the guest's line is right | The fold and the view disagree — `sitting_bill` / `order_current_line` versus `OrderNarrative`. That is the §16.2 equivalence property, and it is the most serious thing this scenario can find. |

`RestaurantInstance.DiagnosticOutput` carries the web application's console tail if a page 500s.

## What is next

Scenario **10** — a counter closes with the pending-line warning shown, the table flips to settled
read-only, and the totals match. It inherits this slice's till harness and `data-live` wholesale and
needs `CloseAndSettleAsync` plus the read-only assertions. Then **11** (hide / filter / unhide, which will
meet `EnhancedNavigation` again on an administrator following a filter link) and **12** (a TOTP reset,
which inherits `CompleteForcedPasswordChangeAsync` and needs only the second obligation beside it). Then
the backup/restore drill.

## The one-line why

Every earlier scenario could let an administrator stand in for staff; this one could not, because the
thing it asserts is *who* changed a number on somebody's bill.
