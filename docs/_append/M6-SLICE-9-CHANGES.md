# M6 Slice 9 — two guests at one table (§16.3 scenario 5)

Every file below is a **full file** at its **repo-relative path**. Extract this archive at the repo root
and the contents drop straight over your working tree.

```bash
tar -xzf m6-slice9-second-guest.tar.gz -C /home/kushal/src/dotnet/myrestaurant
```

`git status` will then show 4 modified/added files and no deletions.

## Files to DELETE

**None.** Nothing in this slice renames, supersedes or orphans an existing file. No migration, no schema
change, no package change, no ADR edit, no `Program.cs` edit, no `.slnx` edit.

## The four files

| File | Change |
| --- | --- |
| `tests/MyRestaurant.EndToEnd.Tests/EndToEndScenarios.cs` | scenario **5** implemented; `SeatGuestAsync` extracted |
| `tests/MyRestaurant.EndToEnd.Tests/Harness/TableOrderJourneys.cs` | roster and party reads, with waits |
| `src/MyRestaurant.WebApplication/Components/Pages/Table/TableOrderSurface.razor` | one added CSS class (see below) |
| `docs/_append/BUILD_PROGRESS-m6-slice-9.md` | **new** — the ledger row |
| `_CHANGES.md` | this file |

## First: your tests are not failing

You asked me to fix failing tests. `claude-terminal.txt` shows every gate green, so there was nothing to
fix and I have not invented anything:

| Run | Result |
| --- | --- |
| `dotnet test` | total **971**, failed **0**, succeeded 956, skipped 15 |
| `bash scripts/ci_local.sh --with-all` | all six gates passed |
| `MYRESTAURANT_E2E=1 dotnet test tests/MyRestaurant.EndToEnd.Tests` | total 15, failed **0**, **8 passed**, 7 skipped |
| `bash run.sh --smoke` | `/healthz/ready` → 200 |
| `dotnet list package --outdated` | nothing outdated in any project |

Slice 8's enhanced-navigation fix worked. Scenarios 3, 4 and 6 are genuinely green for the first time — the
first slice in three entitled to say so. If you saw a failure in a run you did not paste, send me that
output and I will take it as the next thing.

## What this slice adds

§16.3 scenario **5**: *"Second guest joins via fresh token → sees first guest's order live; first guest sees
roster update."*

An administrator bootstraps, puts soup and a pie on the menu, creates a table. The first guest scans,
registers with a passkey, joins, sends one soup. The second guest scans the code the table is showing now —
their own context, their own authenticator, their own account — registers, and joins. Then, with nobody
touching either phone:

- the **first** guest's roster grows to two (`SittingMemberJoined`, §9);
- the first guest's *"rest of the table"* stays **empty**, because §6.1 creates a `guest_order` row only
  inside a first send — the roster and the bill are two different lists;
- the **second** guest's screen already carries the first guest's soup, from the read model;
- the first guest sends a pie ×2 and the second guest's screen grows a line (`OrderLinesChanged`, §9);
- and the database holds **one** open sitting with both usernames in join order.

The last one matters: from a seat, "both joined the sitting" and "a second sitting was opened on the same
table and the unique index did not stop it" look identical.

## The one `src/` change, and why

`TableOrderSurface.razor` rendered another guest's line as a bare `<span>`:

```razor
<span>@theirLine.Quantity × @theirLine.MenuItemName</span>
```

It is now `<span class="order-party-line-name">`. That was the only text on the ordering surface with no way
to address it — your own lines carry `.order-line-name`, the kitchen's carry `.kitchen-line-name`, and this
was a gap rather than a decision.

Deliberately a **new** class rather than reusing `.order-line-name`: that one is `font-weight: 600`, and the
rest of the table is meant to read quieter than your own order. The new name has no CSS rule behind it, so
nothing changes on screen. I checked `wwwroot/app.css` — no selector reaches that bare span today, so this
is purely additive.

The harness could have used `span:not(.chip)` and touched no product file at all. I chose the class because
this codebase has consistently added explicit hooks (`data-live`, `#table-order-surface`,
`data-unseen-alerts`, `.kitchen-line-name`) wherever a surface needed to be observable, and a negation
selector would have been the one place that guessed instead.

## Build/test checklist

```bash
cd /home/kushal/src/dotnet/myrestaurant

# 1. Nothing outside the end-to-end project changed behaviour.
dotnet test
#    expect: total 971, failed 0, succeeded 956, skipped 15   (UNCHANGED)
#    Scenario 5 goes from [Fact(Skip = …)] to a [Fact] that calls Assert.SkipUnless, and xUnit
#    counts both as skipped — so with MYRESTAURANT_E2E unset every number stays exactly where
#    it was. It is still one test method either way.

# 2. The strict build. The .razor edit is the only thing that can produce a compiler diagnostic;
#    it is one attribute on one element, and I balance-checked the file.
bash scripts/ci_local.sh --with-all

# 3. The point of the slice.
MYRESTAURANT_E2E=1 dotnet test tests/MyRestaurant.EndToEnd.Tests
#    expect: total 15, failed 0, 9 passed, 6 skipped
#    scenario 5 is the one that moves

# 4. Append the progress block.
cat docs/_append/BUILD_PROGRESS-m6-slice-9.md >> docs/BUILD_PROGRESS.md
```

I cannot run any of this — there is no .NET SDK in my sandbox — and Slice 8's `_CHANGES.md` had to correct
two slices' worth of numbers I had once stated as though I had. So: **9 passed / 6 skipped is an arithmetic
prediction, not an observation.** One scenario moved from skipped to implemented; if it comes back red, the
scenario is wrong and the numbers were never the point.

## If it goes red

The scenario reaches new ground in exactly two places. The new waits are built to name which:

| Message begins | What it means |
| --- | --- |
| `The table roster never showed both guests on the roster…` | The `SittingMemberJoined` broadcast did not reach the first guest's circuit, or the first guest's circuit had gone away. The message lists who the roster *does* show. Check that both pages are still on `/table/{id}` and that neither navigated. |
| `The rest of the table never showed the first guest's soup…` | The second guest's surface loaded but `ListSittingBillAsync` returned nothing for the first guest — which would mean the send did not create a `guest_order` row, or the two guests are on different sittings. Step (f)'s database read would catch the second; if it is the first, the failure will be at step (d) rather than at (f). |
| `The rest of the table never showed both of the first guest's lines…` | The arrival read worked and the live one did not: `OrderLinesChanged` reached one circuit and not the other. This is the only assertion in the scenario that has no non-live equivalent. |
| `…never advanced from the details step to the credential step…` | Registration, i.e. Slice 8's territory — but for a *second* account in the same instance. If this fires only for `e2e.guest.five.two`, suspect username uniqueness or a shared virtual authenticator rather than enhanced navigation. |
| `Assert.Equal() Failure … 'First Guest' vs 'Second Guest'` at step (f) | Join order came back reversed. `ReadOpenSittingAsync` orders by `joined_at`; two joins seconds apart cannot tie, so this would be a real ordering bug rather than a flake. |

The web application's own console tail is on `RestaurantInstance.DiagnosticOutput` for the server side of
any of these.

## Housekeeping — a correction to Slice 8's note

Slice 8's `_CHANGES.md` claimed fifteen unmerged appends and gave a loop to merge them. That was **wrong**:
you had already merged most of them. Checking `docs/BUILD_PROGRESS.md` against `docs/_append/` right now,
only two are genuinely unmerged:

```bash
cat docs/_append/BUILD_PROGRESS-m5-slice-4.md >> docs/BUILD_PROGRESS.md
cat docs/_append/BUILD_PROGRESS-m5-slice-5.md >> docs/BUILD_PROGRESS.md
```

**And there is a real defect worth your eyes.** M5 Slice 3's block is in `docs/BUILD_PROGRESS.md`
**twice** — identical `### M5 Slice 3 — administration sittings…` headings at lines **1550** and **2506**.
Something got appended twice at some point. That needs one block deleted by hand; I am not giving you a
script for it, and the file is far too large to regenerate. If you would rather I produced the whole
corrected file in a future slice, say so and I will — it is ~4,100 lines, which is a lot of tokens but not
an unreasonable ask for a document that is the project's own record.

`shellcheck` is still not installed locally, so `ci_local.sh` step 1 only parses:
`sudo dnf install ShellCheck`.

## What is next

Scenario **7** — a guest ticks a fulfilled line for removal, the whole batch is refused with a
per-operation reason, and removing their *pending* line succeeds. It is the first scenario about §6.5.9's
all-or-nothing refusal as a guest experiences it, and `TableOrderJourneys` already reads the rejection
panel, so it needs a tick-and-send journey and nothing else. Then 8 through 12, and the backup/restore
drill.

## The one-line why

Two people at one table is the first thing this application does that a single browser cannot show you.
