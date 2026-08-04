# M6 Slice 11 — the CS4007 fix, and §16.3 scenario 8

Every file below is a **full file** at its **repo-relative path**. Extract at the repo root and the
contents drop straight over your working tree — no diffs, no patches, no scripts to run.

```bash
tar -xzf m6-slice11-reminder.tar.gz -C /home/kushal/src/dotnet/myrestaurant
```

## Files to DELETE

**Eighteen, all documentation, all in `docs/_append/`.** Slice 10 asked for these and they are still on
disk, so the request stands. Nothing in `src/` or `tests/` is renamed, superseded or orphaned; no
migration, no schema change, no package change, no ADR edit, no `Program.cs` edit, no `.slnx` edit.

```
docs/_append/BUILD_PROGRESS-m4-slice-2.md
docs/_append/BUILD_PROGRESS-m4-slice-3.md
docs/_append/BUILD_PROGRESS-m4-slice-4.md
docs/_append/BUILD_PROGRESS-m5-slice-1.md
docs/_append/BUILD_PROGRESS-m5-slice-2.md
docs/_append/BUILD_PROGRESS-m5-slice-3.md
docs/_append/BUILD_PROGRESS-m5-slice-4.md
docs/_append/BUILD_PROGRESS-m5-slice-5.md
docs/_append/BUILD_PROGRESS-m6-slice-1.md
docs/_append/BUILD_PROGRESS-m6-slice-2.md
docs/_append/BUILD_PROGRESS-m6-slice-3.md
docs/_append/BUILD_PROGRESS-m6-slice-4.md
docs/_append/BUILD_PROGRESS-m6-slice-5.md
docs/_append/BUILD_PROGRESS-m6-slice-6.md
docs/_append/BUILD_PROGRESS-m6-slice-7.md
docs/_append/BUILD_PROGRESS-m6-slice-8.md
docs/_append/BUILD_PROGRESS-m6-slice-9.md
docs/_append/M6-SLICE-9-CHANGES.md
```

The archive does not contain `docs/_append/`, so after extracting, the whole directory can go:

```bash
git rm -r docs/_append
```

I re-verified all seventeen `BUILD_PROGRESS-*.md` files against the `docs/BUILD_PROGRESS.md` in this
archive — first line, middle line and last line of each, by exact substring. **All seventeen are already
merged**; deleting them loses nothing. The eighteenth, `M6-SLICE-9-CHANGES.md`, is a stray copy of Slice
9's delivery note rather than a ledger entry, and its content is not in `BUILD_PROGRESS.md` because it
was never meant to be.

## The seven files

| File | Change |
| --- | --- |
| `tests/MyRestaurant.EndToEnd.Tests/Harness/TableOrderJourneys.cs` | **CS4007 fixed** |
| `tests/MyRestaurant.EndToEnd.Tests/EndToEndScenarios.cs` | §16.3 scenario **8** implemented |
| `tests/MyRestaurant.EndToEnd.Tests/Harness/KitchenJourneys.cs` | reminder count on the snapshot; acknowledge; watch-for-silence |
| `tests/MyRestaurant.EndToEnd.Tests/Harness/RestaurantInstance.cs` | `KITCHEN_SUBMISSION_REMINDER_SECONDS` parameterised; `kitchen_notification` read |
| `tests/MyRestaurant.EndToEnd.Tests/Harness/RestaurantHarness.cs` | the new parameter, defaulted to 60 |
| `src/MyRestaurant.WebApplication/Components/Pages/Kitchen/KitchenBoard.razor` | one added data attribute |
| `docs/BUILD_PROGRESS.md` | Slice 11 appended; Slice 10's glued heading separated |
| `_CHANGES.md` | this file |

## The build error

`Harness/TableOrderJourneys.cs`, `LineOffersRemovalAsync`. The compiler pointed at **line 412** — the
method's opening brace, which is always where CS4007 is reported — and the cause was at **line 433**:

```csharp
+ $" It holds: {Describe(await ReadCommittedLinesAsync(page))}."
```

The `await` sits inside an interpolation hole of a string that binds to
`DefaultInterpolatedStringHandler`. That handler is a `ref struct` and cannot be held across a suspension
point. Hoisting the read into a local first is the fix, and it is the pattern this file already uses in
four other places with a comment explaining it each time — this one was written inline.

I re-scanned all 317 files for `await` inside an interpolation hole. **One occurrence, this one.**

## "Some tests are now failing"

They aren't, and this is worth being precise about rather than fixing something that is not broken.
`claude-terminal.txt` shows `dotnet test` at **956 total / 0 failed**. Slice 9's note put the total at
**971**. The fifteen-test gap is exactly the end-to-end project's fifteen scenarios — the project failed
to build, so they were never *discovered*, let alone run. There is no failing assertion anywhere in that
output. Fix the CS4007 and the total returns to 971.

## What scenario 8 asserts, and why it took three things

§16.3 asks for *"a send sits unfulfilled 60 s → exactly one reminder alert."* The word carrying the
weight is **exactly**, and no single observation carries it:

- **The badge only ever rises.** Two is two whether the second alert landed a second ago or a minute ago.
  So the scenario clears it first (§10.3's *"tap to clear"*), which turns any further alert into a rise
  from zero — something that can be watched for rather than inferred.
- **A sleep-then-read would miss the interesting failure.** An alert that arrived and was cleared again
  inside the window is precisely the bug being looked for. `WatchBoardAsync` polls for the whole duration
  and returns the **high-water mark**, so asserting zero is an assertion about the stretch, not its last
  instant.
- **The board cannot settle it at all.** Its count is circuit state. A quiet board is consistent with a
  second reminder row having been written and broadcast to nobody. `UNIQUE (order_event_identifier,
  kind)` is what makes a reminder singular, so the scenario reads `kitchen_notification` grouped by kind
  — `initial: 1, reminder: 1`, before and after the quiet watch.

## Five seconds, and why every other scenario keeps sixty

`KITCHEN_SUBMISSION_REMINDER_SECONDS` was the literal `"60"` in `RestaurantInstance.CreateProcess`. It now
threads through the harness exactly as `TABLE_JOIN_TOKEN_ROTATION_SECONDS` does, defaulting to 60.

Scenario 8 asks for 5. §8.4's scan compares `occurred_at` against a threshold it is handed, so the rule is
identical at either value — the number is a duration to wait, not a parameter under test. Going below 5
would be pointless, because `KitchenReminderService.ScanInterval` is a fixed five seconds and the scan's
own resolution would then dominate.

**The 60 default is load-bearing.** §8.4 is the only thing in the system that writes because nobody acted.
At a short global setting, any scenario that sends and then spends thirty seconds asserting on something
else would pick up a reminder alert it never asked for — scenario 4's *"still one alert"* re-read being
the first casualty. Documented on the constant so it is not "tidied" later.

## The one product change

`KitchenBoard.razor` gains `data-unseen-reminders="@_alerts.UnseenReminderCount"` beside the existing
`data-unseen-alerts`. The number was already on screen inside the badge as `" (1 overdue)"`, but that
parenthetical renders only when non-zero, so its absence is ambiguous between *no reminders* and *no badge
at all*. Purely additive — same value the badge already shows, no CSS behind it, nothing changes visually.
Same reasoning as Slice 9's `.order-party-line-name` and Slice 10's `.order-prune-notice`.

## One stale doc reference, corrected in passing

`KitchenJourneys.cs` referenced `<see cref="TableOrderJourneys.BasketWarningCountAsync"/>`, which has not
existed since Slice 10 folded it into `BasketContents.UnavailableMarks`. Silent today only because
`GenerateDocumentationFile` is `false` — it would be CS1574, and therefore an **error** under CI's
`ContinuousIntegrationBuild=true`, the day that changes.

## Also in `docs/BUILD_PROGRESS.md`

Slice 10's heading was glued to the previous paragraph (`…M6 is done.` immediately followed by
`### M6 Slice 10`), so it did not render as a heading. Separated with a blank line and a rule, matching
every other slice boundary in the file. That is the only edit to existing content; Slice 11 is appended.

## Build/test checklist

```bash
cd /home/kushal/src/dotnet/myrestaurant

# 1. The build error is gone. This is the one that has been red.
dotnet build
#    expect: all seven projects succeed, 0 errors

# 2. Back to the pre-Slice-10 total.
dotnet test
#    expect: total 971, failed 0, succeeded 956, skipped 15
#    Scenario 8 moves from [Fact(Skip = …)] to [Fact] + Assert.SkipUnless; xUnit counts both as
#    skipped, so with MYRESTAURANT_E2E unset every number is unchanged from Slice 9's baseline.

# 3. The strict build. One .razor hunk (two, counting the doc comment) and five C# files.
bash scripts/ci_local.sh --with-all

# 4. The point of the slice.
MYRESTAURANT_E2E=1 dotnet test tests/MyRestaurant.EndToEnd.Tests
#    expect: total 15, failed 0, 11 passed, 4 skipped
#    Scenario 8 adds roughly 30s of wall clock: ~10s waiting for the reminder, 15s watching silence.
```

**11 passed / 4 skipped is arithmetic, not observation.** It assumes Slice 10's scenario 7 goes green now
that the project compiles — nothing in this slice touches scenario 7's assertions, and if it is red the
number is 10 and scenario 8 is unaffected either way. There is no .NET SDK in my sandbox; I have run none
of this. What I did run: brace/paren/bracket balance on every edited file compared against the original,
a razor tag-balance check, and the CS4007 scan described above.

## If it goes red

| Message begins | What it means |
| --- | --- |
| `The kitchen board published data-unseen-reminders='absent'…` | The `.razor` attribute did not land. Check the `<section id="kitchen-board-surface">` opening tag has all three attributes. |
| `The kitchen board never showed the overdue send's reminder counted on the badge…` | The reminder never arrived. Either `KitchenReminderService` is not running (its startup line — *"scanning every 5s for guest submissions older than 5s"* — is in `RestaurantInstance.DiagnosticOutput`), or the threshold did not reach the child process. |
| `Assert.Equal() Failure: Expected 1, Actual 2` on `UnseenReminderCount` | Two reminders for one send. That is the `UNIQUE (order_event_identifier, kind)` constraint or §8.4's `RETURNING` guard, and it is a real defect. |
| `Assert.Equal() Failure` on `KitchenNotificationTally { Initial = 1, Reminder = 1 }` after the quiet watch | Same defect, seen at the row rather than at the badge — and this is the one that matters, because it holds even when the broadcast went nowhere. |
| `There is no alert badge to clear…` | The board had nothing unseen at the acknowledge step, which means step (b)'s reminder never actually reached the badge despite the wait passing. Shouldn't be reachable; if it is, tell me. |
| `kitchen_notification.kind held '…'` | A migration widened §8.2's CHECK constraint and `ReadKitchenNotificationsAsync` needs a third case. |

`RestaurantInstance.DiagnosticOutput` carries the web application's console tail, including every
`"Kitchen reminder issued for order event …"` line the service logged.

## What is next

Scenario **9** — a counter adjusts a price with a reason and the guest's screen reads old → new. It is the
first scenario to need a counter, which means `/administration/people/new`, a forced password change
(§3.2) and a second sign-in before the interesting part starts — the staff-account journey scenarios 4
and 6 deliberately skipped by putting an administrator at the pass. Then 10 through 12, and the
backup/restore drill.

## The one-line why

The failing build was one `await` in the wrong place; the scenario it was blocking turned out to need
three separate observations to say the single word §16.3 asks for.
