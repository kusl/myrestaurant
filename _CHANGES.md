# M6 Slice 35 — the palette written twice (F-68), the count that was already wrong (F-69), and the gate nobody wrote down (F-70)

Extract at the repository root. Every path is repo-relative and every file is complete.

```
tar -xzf m6-slice-35-palette-and-the-undocumented-gate.tar.gz
git status
```

**Files to DELETE: none.**

**One `git add` IS required**, and the gates will not see the file without it:

```
git add tests/MyRestaurant.WebApplication.Tests/Documentation/TestingSectionContractTests.cs
```

The test SDK globs `**/*.cs`, so no `.csproj` edit is needed — but `scripts/check_tree.sh` walks
`git ls-files`, so an untracked file is invisible to tree hygiene and the authored-text count will not move.
Everything else in this archive already exists and is tracked.

---

## Read this first

**Slice 34 is green.** The terminal logs in this repository are Slice 34's and they are clean: **1078 tests,
0 failed, 0 skipped**, 16/16 §16.3 scenarios, all eight `ci_local.sh` gates, boot smoke passing. Slices 33
and 34 both landed and both work. Nothing in this slice is a repair of either.

**Slice 34 predicted 1077 and the run returned 1078.** That one test is F-70, and reconciling it is where
this slice started.

---

## What this slice is

Stage 1b's last item was recorded as tidying — strip the redundant `var(--declared, #literal)` fallbacks out
of the seven components that still have a `<style>` block. Opening them produced three findings.

- **F-69 — the number that justified calling it tidying was already wrong.** §11.12 left the no-fallback
  rule as a *should* on one stated ground: *over a hundred references across sixteen components* still
  carried one. §16.4 repeated it, F-64's ledger row repeated it, `MENU_AND_HANDHELD_PLAN.md` repeated it —
  and Slice 34 had emptied nine of the sixteen blocks, so the truth was **fifty, across seven**. Both halves
  wrong; the component count wrong by more than half. That number was the *entire* argument for the rule
  being a *should*, and fifty is an afternoon.

- **F-68 — the palette is written back into the tree as literals, and seven copies had drifted.** The first
  file opened, `TableHistory.razor`, has the guest's irreversible-hide warning in `#fdecea` on `#f5c2c0`
  against the palette's `--danger-surface` `#fbeaea` on `--danger-hairline` `#f0c7c7`. **That is the same
  pair Slice 34 removed from four `.chip-warn` copies** — F-66 in a fifth place, one area over from where
  that sweep was looking. The class: **95 colour values outside `:root`**, of which **20 are byte-identical
  to a property declared there** (`#ffffff` six times against `--surface-raised`, *three inside `app.css`
  itself*; `#b45309` five times against `--caution-ink`), three are `rgba(22, 32, 43, α)` — which is `--ink`
  in decimal, the one form no scan for `#hex` could find — and five are near-copies on
  `/administration/events`'s badges. **F-64's gate cannot see any of it**: it asks whether a name a rule
  *reads* is declared, and a rule that reads nothing and writes `#b45309` is invisible to it.

- **F-70 — a blocking gate was in the tree and no document in this repository knew.**
  `HandheldLayoutContractTests` holds **eight** `[Fact]` methods; §16.4 says *"Seven assertions"*; the
  class's own summary says *"three of the seven facts"*; Slice 34's write-up says *"all seven facts…all
  seven pass"*. No ledger row, no changelog entry, no `_CHANGES.md` line — every artefact §18's
  atomic-documentation rule requires, absent. The assertion is
  `OverflowWrapIsDeclaredExactlyOnceOnTheBodyElement`, and **it is a good rule**, kept. What was missing was
  the paperwork. §16.4 states an assertion count for nine test classes and nothing had ever compared any of
  them to a file.

---

## The decisions to veto, if you want to

**1. Seven rules render a different colour than they did yesterday.**

Twenty substitutions are byte-identical and change nothing. These seven are near-copies collapsed onto a
property that already existed — F-66's precedent, where four chip reds were moved onto the palette — and
each carries a comment beside the rule saying what moved:

| Surface | Rule | Was | Now |
|---|---|---|---|
| `/table/history` | `.history-confirm` background / border | `#fdecea` / `#f5c2c0` | `--danger-surface` / `--danger-hairline` |
| `/kitchen` | `.kitchen-menu-item.is-off` background / border | `#fef2f2` / `#fca5a5` | `--danger-surface` / `--danger-hairline` |
| `/counter/sittings/{id}` | `.counter-settle-confirm` background | `#fef2f2` | `--danger-surface` |
| `/administration/events` | `.event-stream-badge` ink / background | `#1f2937` / `#e5e7eb` | `--ink` / `--chip-surface` |
| `/administration/events` | the three stream tints | `#fde8e8`, `#e0f2f1`, `#fef3c7` | `--danger-surface`, `--accent-surface`, `--caution-surface` |

**The rule applied, and it is the line between correction and redesign:** a literal identical to an existing
property reads it; a literal that is a *near*-copy of an existing property reads it and the change is
stated; a literal with **no** existing property gets one declared at its current value — because choosing
between two undeclared literals is a design judgement and this slice is a correction.

The alternative was declaring five more properties so nothing moved at all. That keeps every pixel and keeps
the defect: a palette with two near-identical reds in it is a palette nobody can use correctly, and
`.history-confirm` is the panel a guest reads before doing something irreversible.

**2. Ten new properties in `:root`, and two of the names look wrong at a glance.**

`--accent-ink` is `#ffffff` and so is `--surface-raised` — two names, one value, on exactly the principle
`--focus-ring` and `--accent` already were: *the ink on the accent* and *a raised surface* are different
jobs that need not move together. And `--danger-signal` (`#b91c1c`) is deliberately **not**
`--danger-ink-strong`, because it carries *less* contrast on white than `--danger-ink`; `-strong` in this
palette means more contrast, and this one is louder rather than stronger. Both reasons are written beside
the declarations.

**3. The eighth gate is adopted rather than deleted, and its author is not this project.**

The rule is right, so what was missing is paperwork rather than judgement. Two defects in it are repaired:
no non-vacuity guard on its component walk (so *"declared exactly once in the tree"* was satisfied by a scan
that read `app.css` and opened no component — F-41), and its keyword check ran against the composed report
line rather than the value (so a repository path containing the word would have satisfied it).

**Its eight-times claim is preserved and labelled as its own account**, not restated as fact: the stylesheet
it describes is two commits back and there is no git history in the authoring sandbox. Inventing
corroboration would be the F-62 error in miniature.

**4. `.sitting-meta` stays open, deferred a second time.**

`ManageSitting` and `TableArea` both declare it and the two have drifted — `ManageSitting` has
`margin: 0.2rem 0 0` and `TableArea` does not. This slice edited `TableArea` and did not fix it, because the
resolution is a choice between a shared declaration (`app.css` plus a prefix-list entry) and a rename that
touches markup, and neither belongs in a slice whose subject is colour. Say the word and it goes in now.

---

## Files in this archive

| Path | Change |
|---|---|
| `src/MyRestaurant.WebApplication/wwwroot/app.css` | `:root` rebuilt with ten new properties and the palette rule stated at the top; nine literals outside `:root` replaced |
| `src/MyRestaurant.WebApplication/Components/Pages/Kitchen/KitchenBoard.razor` | 13 literals to properties, 10 fallbacks removed, note on the 86'd row |
| `…/Components/Pages/Counter/CounterBoard.razor` | 1 literal, 6 fallbacks |
| `…/Components/Pages/Counter/CounterSitting.razor` | 10 literals, 13 fallbacks, note on the settle confirmation |
| `…/Components/Pages/Table/TableHistory.razor` | 3 literals (two drifted), 7 fallbacks, note on `.history-confirm` |
| `…/Components/Pages/Table/TableArea.razor` | 5 fallbacks |
| `…/Components/Pages/Display/TableDisplay.razor` | 4 literals including the `rgba` veil, 3 fallbacks |
| `…/Components/Pages/Administration/EventExplorer.razor` | 5 near-copy badge tints to properties, note on the set |
| `…/Components/Layout/DisplayLayout.razor` | 6 fallbacks |
| `tests/…/Components/HandheldLayoutContractTests.cs` | ninth fact added; eighth fact repaired and documented; two helpers, five regex fields |
| `tests/…/Documentation/TestingSectionContractTests.cs` | **NEW.** One assertion: §16.4's counts against the files |
| `docs/TECHNICAL_SPECIFICATION.md` | **v1.20.** §11.12 four new paragraphs, §16.4 count to nine plus three paragraphs, §18 two habits, Appendix A F-68/F-69/F-70, changelog |
| `docs/DOCUMENTATION_REVIEW.md` | three rows, status line, F-64's stale count corrected, *Going forward* extended |
| `docs/BUILD_PROGRESS.md` | Slice 35 section; Slice 34's still-open figure annotated in place |
| `docs/MENU_AND_HANDHELD_PLAN.md` | Stage 1b's fallback paragraph replaced by what actually happened |
| `_CHANGES.md` | this file |

---

## What was verified

No .NET SDK in the authoring sandbox, so all of this is text-level and says so.

- **All nine facts ported to Python and run against the delivered tree: nine pass.** Against the tree as it
  stood before this slice, **the ninth fails twice** — 50 fallbacks, 95 literals. Before and after, not a
  claim.
- **The ninth fact proven sensitive seven ways**, including a colour planted *inside* `app.css`'s one
  breakpoint query, which is what shows the nested at-rule is read.
- **And proven NOT to fire three ways** — an id selector named `#abcdef-notice` (six hex characters after a
  `#`, in a prelude), a `background-color: transparent`, and a CSS comment naming both a hex literal and a
  `var(--name, #hex)` fallback in prose. **That last one is left in the delivered tree on purpose**, inside
  `KitchenBoard`'s block comment: a future version of this fact that forgot to strip CSS comments fails on
  arrival instead of quietly bounding its own reach the way F-67 did for four slices. Measured both ways —
  stripped, 0 and 0; unstripped, 14 literals and 1 fallback, all prose.
- **`TestingSectionContractTests` proven sensitive by the tree itself, nothing planted:** against §16.4 as
  delivered before this slice, nine (class, count) pairs, **eight agree and one disagrees** — the handheld
  test. Against the edited specification: nine pairs, nine agree.
- **Brace/paren/bracket balance** on both C# files with untouched siblings as controls: clean, proven
  sensitive. **CS1620** and **CS4007** scans clean. **A brace-in-interpolation scan caught a real defect
  during authoring:** the first draft wrote a message containing `{ ... }` inside a `$"…"` string, where that
  is an interpolation hole rather than literal braces, and it would not have compiled. Rephrased.
- **Razor tag-tree balance** on every touched component: clean — **after fixing the scanner for the third
  time in three slices.** It first reported `<TableDisplay> never closed`, pointing at
  `@inject ILogger<TableDisplay> Logger`: a C# generic argument on a directive line, read as markup. Same
  class of bug as the standing false report Slice 34 recorded on `TableOrderSurface.razor`
  (`IReadOnlyList<OrderLineView>` inside an inline `@{ }` region) — the scanner was reading C# as HTML
  wherever C# appears outside a `@code` block. It now blanks directive lines and inline `@{ … }` regions,
  and the result is **zero reports across every `.razor` file in `Components/`, including the one Slice 34
  left open.** Still proven sensitive: deleting one `</div>` from `KitchenBoard` is reported twice, at the
  lines the blocks opened on.
- **`SpecificationVersionTests` ported and run:** header 1.20 against newest entry 1.20, entries descending,
  two documents qualifying.
- **Byte hygiene** on every delivered file.

## What was NOT verified

**Nothing compiled.** Fifteen files touched, one of them new. `TestingSectionContractTests` is the likeliest
site of a complaint — only new C# file, uses `Dictionary` deconstruction in a `foreach`, and calls
`Assert.True` inside a helper that returns a string, which nothing else in this tree does.

**No browser rendered anything, and this time none could.** Scenario 16 measures geometry; it is told
nothing about which red a warning panel is, and a barrier asserting a computed colour would be asserting the
palette against itself. §11.12 now says the palette rules are text assertions and only text assertions.

**The seven colour changes are corrections, not improvements.** Whether they read better belongs to whoever
is holding the phone.

## One thing found and deliberately not fixed

`check_tree.sh` gate 3 prints **`all files end with exactly one LF`** and what it asserts is
`tail -c 1 | wc -l` — *at least* one. **Eleven tracked files end with two or more**, including
`.env.example`, `.github/workflows/ci.yml`, `scripts/backup.sh`, `scripts/restore_drill.sh`,
`Components/Pages/Counter/CounterBoard.razor` and `Components/Account/Pages/SignIn.razor`. Nothing is broken
— `.editorconfig` asks for a final newline and does not forbid a second, and the gate's stated purpose,
catching a truncated transfer, is served by the check it performs. But the message asserts a property the
line beneath it does not check, which is **F-65's shape** in a shell script.

Found because this slice's own byte-hygiene pass was stricter than the gate. **Not fixed**, and
`CounterBoard.razor` ships with its two trailing newlines untouched, because the choice is between weakening
one word of a message and normalising eleven files across six directories — a decision, not a repair, and
not one to make inside a slice about colour. It is written into BUILD_PROGRESS's *Still open* with the
evidence.

## Test count

Observed **1078**. Predicted **1080** — one new `[Fact]` in each of the two test files. Arithmetic, not an
observation. §16.3 stays at **16**.

**Per §18's new habit: if the run returns anything other than 1080, that difference is the next thing to
chase rather than a rounding.** That habit exists because of F-70, which was one digit in a green summary
line.
