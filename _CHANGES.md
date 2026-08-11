# M6 Slice 30 — the screen it is read on (F-59), the gate that named one file (F-58), and a plan for the menu

Extract at the repository root. Every path is repo-relative and every file is complete.

```
tar -xzf m6-slice-30-handheld-contract-and-menu-plan.tar.gz
git add docs/MENU_AND_HANDHELD_PLAN.md
git add docs/adr/0014-menu-sections-and-item-descriptions.md
git add src/MyRestaurant.WebApplication/Components/Pages/Administration/AdministrationArea.cs
git add src/MyRestaurant.WebApplication/Components/Pages/Administration/AdministrationAreaLinks.razor
git add tests/MyRestaurant.WebApplication.Tests/Components/HandheldLayoutContractTests.cs
git status
```

**Files to DELETE: none.**

**`git add` is required** for the five new files above — `scripts/check_tree.sh` enumerates with
`git ls-files`, so an untracked file is a file no gate looks at.

---

## Your user found something in ninety seconds that 1066 tests could not

> the manage button was on the right side as the user was trying to manage a table

Exactly reproducible, and the mechanism is four lines of CSS repeated four times. `AdministrationHome`,
`AdministrationTables`, `AdministrationMenu` and `AdministrationSittings` each declared their own inline
copy of the same eighty lines of table rules, and each copy ended in:

```css
.admin-people .admin-row-actions {
    white-space: nowrap;
    text-align: right;
}
```

inside a wrapper carrying `overflow-x: auto`. A five-to-eight column table in a 375px viewport scrolls
sideways; the action column is the last thing in it; so the only way into a row was reachable only by
scrolling past every other column of that row. Nobody decided that — it was one paste, four times. The same
four pastes had also invented the chip vocabulary **five** times over (four inline, plus `app.css`, which
carried a comment apologising for the duplication and inviting somebody to fold it in) and
`.visually-hidden` **seven** times.

**Nothing here could have caught it, and the reason is precise rather than embarrassing.** `REQUIREMENTS.md`
§1 has said since rev 1 that guests order from their own phones. §11.7 budgets the footer clock for a
handset in real detail. But **no section said a staff surface has to be operable on one** — so there was no
rule to enforce, no gate that could have been written, and every gate was green while four pages an
operator uses standing at a table were unusable on the device they would be holding.

That is the first finding in the ledger this project did not find itself, and it is recorded as its own
lesson: **a project can only find the defects its own premises admit.** The instrument that finds the rest
is a person who has not read the documents.

---

## Why the layout landed and the menu did not

Your enhancement request came first and is second in this slice. Two reasons, and the second decided it.

The menu work adds four surfaces — a section index, a section editor, a rewritten item form, and a guest
menu that is a grouped list of described items instead of one `<select>`. All four are read from a phone.
Written before the responsive vocabulary exists, they are written against the shape F-59 was found in, and
then all four need touching again.

And: **F-59 blocks user testing, the menu does not.** A menu without sections is a menu somebody can still
order from.

So the menu is shipped **decided and planned** rather than half-built. `docs/MENU_AND_HANDHELD_PLAN.md` is
the plan, `docs/adr/0014-*.md` is the ruling, §7 and §19 point at both, and Stage 2 is a migration and a
file list rather than a design exercise. If you disagree with the ordering, Stage 2 is self-contained and
can be taken next without re-reading any of this.

---

## What §11.12 says, in the four sentences worth reading

**The direction is the rule, not a preference.** `app.css` states the narrow layout unconditionally and
contains exactly one `@media (min-width: 48rem)` query — the only place a width appears in the file. A
`max-width` query would make the wide layout the default and the handset the exception, and it fails in the
worst available direction: whatever is forgotten lands on the screen the software is actually used from.
That was the previous arrangement.

**Every record-list cell states its own label, and this is not decoration.** Overriding `display` on a
table's parts drops table semantics in every engine, so below the breakpoint `<thead>` stops being what
associates a cell with a column. An unlabelled card is a column of bare values — `Table 4`, `2`, `19:04`,
`$18.50` — with nothing on screen or in the accessibility tree saying which is which. `data-label` is the
replacement for the header; a self-describing cell opts out with `data-label=""`, which is a decision
written down rather than an omission.

**A row's action is the full width of the foot of its card, and its primary cell is also a link.** Both,
not either: the link alone leaves a card with no visible affordance, and the button alone puts the target
at the bottom of a card whose top is what somebody taps.

**A 16px floor on every text control**, because iOS Safari zooms the whole viewport when a focused field's
text is smaller and does not zoom back — so one undersized field breaks the layout of the page around it,
on the platform most of your guests are holding.

---

## Two decisions I made that you might veto

**1. `.page-head`, not `.admin-header`.** The obvious move was to hoist the existing class name into
`app.css`. That is wrong for a mechanical reason: three pages this slice does not restructure still declare
`.admin-header` inline, and **an inline copy of a shared name wins on source order at equal specificity** —
so those pages would silently keep the old behaviour while the stylesheet claimed otherwise. A new name
cannot lose that argument. The two coexist for exactly as long as Stage 1b takes.

*To revert:* rename `.page-head*` back to `.admin-header*` in `app.css` and the four pages, and accept
that the three unconverted pages override it.

**2. `AdministrationAreaLinks` is a new component.** The six area links were copy-pasted into six pages and
each copy omitted a different one — **its own** — so the row of links was a different row on every page and
no page was reachable from every other. It is rendered once now, self-link included and marked
`aria-current="page"`, because on a handset it is a horizontally scrolled strip and a strip whose contents
shift between pages cannot be navigated from memory.

*To revert:* inline the `<ul class="page-head-areas">` back into each page and delete
`AdministrationAreaLinks.razor` and `AdministrationArea.cs`.

---

## F-58, found by accident

`REQUIREMENTS.md` said **"Revision 4 — 2026-08-05"** in its header while its own revision history's newest
entry said **"Rev 5 — 2026-08-06"**. Six slices, green on every `dotnet test`.

`SpecificationVersionTests` exists precisely to stop that — it was F-48's fix, two slices after the
specification did the same thing. It asserts header-matches-newest and entries-descend, and both are true.
What nobody noticed is that the file it asserts them *about* is a `const string`, and **a `const string`
naming one path reads as configuration rather than as a scope decision.** F-46 established that a rule
enforced as a list of examples is enforced as a list of examples. This is the sharper corner: a list of one
does not look like a list.

Its subject is computed now — every `docs/*.md` with both a header version and a history section, both
vocabularies read by one pattern, and a half-versioned document reported as a finding rather than skipped.
Ported to Python and run against the tree **before** the fix: it fails on `REQUIREMENTS.md`, header 4
against newest entry 5. That is F-58 reproduced by the gate that should have had it.

---

## Every file, and why

| File | Change |
|---|---|
| `docs/MENU_AND_HANDHELD_PLAN.md` | **new.** Six stages, with the schema, the migration order, the file list, and the two stages that are recorded as *not startable* and why |
| `docs/adr/0014-menu-sections-and-item-descriptions.md` | **new.** Seven rulings on the menu model, each with the argument and the accepted cost |
| `src/…/Administration/AdministrationArea.cs` | **new.** The six-member enum behind the shared nav |
| `src/…/Administration/AdministrationAreaLinks.razor` | **new.** The area links, rendered once |
| `tests/…/Components/HandheldLayoutContractTests.cs` | **new.** Four facts, each proven sensitive |
| `src/…/wwwroot/app.css` | rewritten handheld-first; one `min-width: 48rem` query; the `.record-*` and `.page-head*` vocabulary; `--touch-target`; the 16px input floor; `select` and `textarea` styled for the first time (the `textarea` is Stage 3's description field, added while the file was open); `.chip`, `.muted` and `.visually-hidden` declared once, with `clip-path` rather than the deprecated `clip` |
| `src/…/Administration/AdministrationHome.razor` | record list; username is the link; action at the foot of the card; **inline `<style>` removed entirely** |
| `src/…/Administration/AdministrationTables.razor` | same, and this is the page F-59 was reported against |
| `src/…/Administration/AdministrationMenu.razor` | same. Restructured and **deliberately not given sections** — it says so in its own header, and its `Describe` fallback already renders an unknown event type as itself, so Stage 2's two new types read correctly here before this page learns their names |
| `src/…/Administration/AdministrationSittings.razor` | same, plus the batch-close tick moved to the **first** thing in each card with the table's label as its accessible name — a bare checkbox at the top of a card is a control with nothing saying what it closes, and this is the one control on an administration index that charges somebody money |
| `tests/…/Documentation/SpecificationVersionTests.cs` | subject computed rather than named; both version vocabularies; half-versioned documents are findings; two facts, renamed, not added to |
| `docs/TECHNICAL_SPECIFICATION.md` | **v1.15** — new §11.12; §7 forward-references ADR-0014 and restates the two rules easiest to lose while rewriting a menu; §16.4 gains both gates and what the handheld one cannot assert; §19 gains M7 and why its stages run in that order; Appendix A gains F-58, F-59 and one row that is not a finding; header and changelog moved together |
| `docs/REQUIREMENTS.md` | **rev 6** — one new §8 principle; §6.8 gains sections, descriptions and ordering, plus what stays out of scope and why; the F-58 header correction recorded in its own rev 6 entry; the stale specification-version citation dropped rather than corrected |
| `docs/DOCUMENTATION_REVIEW.md` | F-58 and F-59 rows, the status line, and two closing paragraphs — one of which is the only lesson in this file that came from outside it |
| `docs/BUILD_PROGRESS.md` | Slice 30, whole file |
| `README.md` | M7 in the roadmap and in the opening; `docs/` added to the layout list, which it had never been in |

---

## Verification

- **`HandheldLayoutContractTests` ported to Python, run, then attacked.** Four facts pass on the tree.
  Nine mutations, one at a time — a second breakpoint; the breakpoint inverted to `max-width`; the block
  emptied; a page re-declaring `.record-actions` inline; one `<td>` losing its `data-label`; the wrapper
  renamed on one page; a page not on the expected list acquiring the retired vocabulary; and a page on that
  list half-converted, which is caught by the label-parity fact rather than the list fact because a
  half-converted page has cells and no labels. Both non-vacuity guards then attacked directly by deleting
  every `.page-head*` and then every `.record-*` **selector** from `app.css` while leaving the comments
  that name them — the guard asserts a selector begins a line, so both fire. A comment-only edit changes
  nothing, as a control.
- **The generalised version gate run against the tree before the fix**: fails on `REQUIREMENTS.md`, 4
  against 5. After: two documents versioned, four skipped, zero half-versioned, both facts passing.
- **Razor tag-tree and `@code` brace balance** over all five new and rewritten components: clean. Three
  untouched components as controls, of which `TableOrderSurface.razor` fails — and the failure is the
  checker's, not the file's (`IReadOnlyList<OrderLineView>` inside an `@{ }` block is a generic argument
  that looks like a tag). Recorded rather than suppressed, because a checker that passes on everything is
  not looking.
- **`<td>` / `data-label` parity**: 5/5, 5/5, 9/9, 14/14.
- **Byte hygiene on all sixteen files**: LF, one final newline, no CR, no trailing whitespace, no
  whitespace-only lines, no context-dump separator.
- **DbUp's statement splitter read from source** before the plan committed to a `DO` block:
  `PostgresqlQueryParser.ParseRawQuery`'s `DollarQuoted` state consumes a whole tagged block, so a `;`
  inside `DO $$ … $$` does not split the statement.

**Test count 1066 → 1072.** Four new `[Fact]` methods; the two in `SpecificationVersionTests` are rewritten
and renamed, not added to. Arithmetic prediction — nothing was compiled or run.

**Not verified, and it is the honest limit of this slice:** no browser rendered any of this. §11.12 is a
claim about what a stylesheet does at two viewport widths, and the strongest thing asserted here is its
*structure*. The four pages have not been seen at 375px by anything. That is exactly the gap Stage 1c
exists to close, which is why the plan names the Playwright barrier as its first open item instead of
leaving it implied.

---

## On virginia

```bash
cd ~/src/dotnet/myrestaurant && git pull
bash scripts/ci_local.sh          # check_tree first, then the unit gates
```

Then look at it on the phone, which is the only thing that can actually check this slice:

```
/administration            → the People index
/administration/tables     → the page the report was about
/administration/sittings    → the batch-close tick, which is now the first thing in each card
```

What to look for: each row is a bordered card, every value has a small caps label above it, and the
**Manage** button is the full width of the bottom of its card. The row of area links across the top is one
horizontally scrolling strip with the current page filled in — that strip is the same six links in the same
order on all four pages now, which it was not before. Rotate to landscape or open it on a laptop and the
cards should become the table you had, header row and all.

If a card looks like a stack of unlabelled values, the `data-label` rules did not apply and I want to know
the browser. If the strip has wrapped into a pile instead of scrolling, `.page-head-areas` lost its
`overflow-x` and the same applies.
