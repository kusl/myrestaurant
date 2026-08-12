# M6 Slice 33 — Stage 1b's first half, and two rules that were true and unenforced (F-63, F-64)

Extract at the repository root. Every path is repo-relative and every file is complete.

```
tar -xzf m6-slice-33-explorers-breakpoint-and-palette.tar.gz
git status
```

**Files to DELETE: none.**

**No `git add` is required.** Every file in this archive already exists and is tracked — there are no new
files in this slice, which is the first time that has been true since Slice 24.

---

## What this slice is

`EventExplorer.razor` and `HiddenRecords.razor` join §11.12's shared vocabulary. They were the last two
pages carrying a hand-rolled copy of §11.4's row of area links, and the only two carrying a filter form, so
they went together rather than in size order. §16.3 scenario 16 goes from four surfaces to six.

Two findings came out of doing it, and neither was in the work:

- **F-63** — §11.12's *exactly one breakpoint* is a rule about the tree and was asserted about `app.css`
  alone, while the same section grants twenty-one components an inline `<style>`. No component had a
  second breakpoint, so the rule was true and unenforced. Found by needing to write one.
- **F-64** — five CSS custom properties (`--muted-foreground`, `--rule`, `--surface-sunken`,
  `--chip-background`, `--chip-foreground`) are read **fifty-five times across eight components** and
  declared nowhere. An undeclared property in CSS renders its fallback in silence, so eight surfaces have
  been drawing `#666` greys and `#e5e5e5` hairlines while the rest of the application draws `--ink-soft`
  and `--hairline`.

---

## The decisions to veto, if you want to

**1. F-64 is fixed across all eight files, not just the two this slice was already rewriting.**

The alternative was an F-47-style expected-holders list and a fix next slice. The argument against it: the
repair is a name substitution inside `<style>` blocks — no markup moves, so it cannot break a Razor compile
— and a list whose only purpose is to defer a substitution is a list this project has ruled against
writing. It was applied programmatically and diff-verified: **110 changed lines, every one a `var(--…)`
line**.

The cost is that `ManageSitting.razor` and `ManagePerson.razor` are Stage 1b's next conversion targets and
get touched twice. Under full-file delivery that costs nothing but your review time.

**To revert:** restore those six files from `HEAD` and add a `StillExpectedToReadUndeclaredProperties` list
to the sixth fact in `HandheldLayoutContractTests`.

**2. `/administration/hidden-records` is in the barrier and is measured empty.**

Putting a row on it needs a guest, a token, a join, an order and a close — scenario 11's arrangement.
Sittings has been measured on the same terms since the barrier was written, and the scenario states it
rather than glossing it. Its filter submit *is* measured, so the surface is not contributing nothing.

**To revert:** delete one line from `HandheldAdministrationPaths` and drop `MinimumControlsMeasured` from 8
to 7.

**3. Eight pages will look slightly different.** `--ink-soft` `#55636f` is cooler and darker than `#666`;
`--hairline` `#e2e6ec` is lighter and cooler than `#e5e5e5`. That is the palette those pages were always
supposed to be drawing, but it is a visible change and it is a judgement, so it is yours.

---

## Files in this archive

| File | Why |
|---|---|
| `src/…/wwwroot/app.css` | shared `.filter-*` vocabulary; `--chip-surface` declared; F-63 rule in the header |
| `src/…/Administration/EventExplorer.razor` | `.page-head` + `AdministrationAreaLinks`, shared filter, `margin-left: auto` gone |
| `src/…/Administration/HiddenRecords.razor` | same, plus the money-alignment ruling recorded at the rule |
| `src/…/Administration/ManageMenuItem.razor` | F-64 only — `var()` names, no markup change |
| `src/…/Administration/ManagePerson.razor` | F-64 only |
| `src/…/Administration/ManageSitting.razor` | F-64 only |
| `src/…/Administration/ManageTable.razor` | F-64 only |
| `src/…/Administration/TableJoinCode.razor` | F-64 only |
| `src/…/Counter/CounterJoinCode.razor` | F-64 only |
| `tests/…/Components/HandheldLayoutContractTests.cs` | four facts → six; F-63 and F-64 made executable |
| `tests/…/EndToEnd.Tests/Harness/HandheldReach.cs` | reach selector covers `.filter-actions` |
| `tests/…/EndToEnd.Tests/Harness/HiddenRecordJourneys.cs` | **selectors repointed — without this, scenario 11 fails** |
| `tests/…/EndToEnd.Tests/EndToEndScenarios.cs` | six surfaces, floor 8, comments reconciled |
| `docs/TECHNICAL_SPECIFICATION.md` | v1.18 — §11.12, §16.3, §16.4, Appendix A F-63 + F-64 |
| `docs/DOCUMENTATION_REVIEW.md` | F-63 and F-64 rows, status line, closing note on adjacency |
| `docs/BUILD_PROGRESS.md` | Slice 33 entry (complete file) |
| `docs/MENU_AND_HANDHELD_PLAN.md` | Stage 1b half struck through; 1c's numbers reconciled |
| `README.md` | scenario 16 row, M6 and M7 paragraphs |
| `_CHANGES.md` | this file |

---

## The red suite this nearly shipped

`HiddenRecordJourneys.cs` pins `form.hidden-filter #filter-username`, `form.hidden-filter
button[type='submit']`, `form.hidden-filter .hidden-filter-actions a` and `p.hidden-count`. Renaming those
classes without that file is scenario 11 failing on a page that is correct. Repointed. `p.hidden-none` is
kept exactly as it was, because it is the harness's handle rather than a style, and that reason is now
written where the class is.

---

## What to run

```
bash scripts/check_tree.sh
bash scripts/ci_local.sh --with-all --with-e2e
```

**Expect:** 8 numbered gates, same order as Slice 32. **1076 tests, 0 failed** (was 1074 — two new
`[Fact]` methods; arithmetic, not an observation). **16 of 16** §16.3 scenarios, with scenario 16 now
walking six surfaces and measuring nine controls.

The authored-text file count in gate 1 does not move: no new files.

If scenario 16 fails, the message names the widest element outside a scroll container on the surface that
overflowed — that is the diagnosis, not just the symptom.

---

## Verified from here, and what was not

**All six contract facts were ported to Python and executed.** Against this tree: six pass. Against the
tree as it was: five fail. **Eight planted regressions, eight caught** — one per assertion, including a
`max-width` query planted in a component (F-63's own regression) and `var(--text-quiet, #666)` planted in a
converted page (F-64's).

Balance, CS1620, CS4007, Razor tag-tree and byte hygiene: clean on all thirteen files, each check proven
sensitive, with untouched siblings as controls. `SpecificationVersionTests` ported and run — header 1.18
against newest entry 1.18.

**Nothing compiled and no browser rendered anything.** Thirteen files edited, `dotnet build` run on none.
The Razor markup changes are small and structural; the CSS claims rest on reading `app.css` line by line.
The first run on the workstation is what proves them.
