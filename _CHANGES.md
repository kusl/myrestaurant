# M6 Slice 59 — likes: §11.4's count, and the end of the menu enhancement's open list

**Apply this after Slice 58, and only after 58 comes back green.** This archive stacks: it extends a
scenario and a contract class that Slice 58 created, so extracting it over a tree that has not had 58
applied will leave two files referencing things that do not exist.

Extract at the repository root. Every file in the archive is a **complete file**; nothing is a patch, and
there are no scripts to run.

```
tar -xzf m6-slice-59-likes-the-count.tar.gz
```

## Files to delete

**None.** Nothing is removed by this slice.

## New files — these must be `git add`ed

**None.** Every file here already exists, or was created by Slice 58 and `git add`ed with it.

## What is in the archive

| Path | Why |
| --- | --- |
| `src/MyRestaurant.WebApplication/Components/Pages/Administration/AdministrationMenu.razor` | The chip, the third read, and `LikeCount` |
| `tests/MyRestaurant.WebApplication.Tests/Menu/MenuItemReactionSurfaceContractTests.cs` | The fifth fact — the index reads the count and never one person's presses |
| `tests/MyRestaurant.EndToEnd.Tests/Harness/AdministrationJourneys.cs` | `ReadMenuIndexLikeCountAsync` |
| `tests/MyRestaurant.EndToEnd.Tests/MenuReactionScenarios.cs` | Scenario 21 extended with the read-back; still one `[Fact]` |
| `docs/TECHNICAL_SPECIFICATION.md` | **v1.44**: §11.4's chip, §16.3's sixth claim, §16.4 four → five, the Stage 5b-ii Appendix A row, changelog |
| `docs/MENU_AND_HANDHELD_PLAN.md` | Stage 5b-ii, landed; Stage 5's open list closed |
| `docs/BUILD_PROGRESS.md` | The Slice 59 narrative, shipped whole |
| `_CHANGES.md` | This file |

**`docs/DOCUMENTATION_REVIEW.md` is not in the archive**, deliberately: this slice found no defect. An
enhancement is recorded in `BUILD_PROGRESS`, the specification changelog and Appendix A; the ledger is for
findings.

## Test count — and the honest caveat

| Where | Facts | Running |
| --- | --- | --- |
| Baseline — **predicted by Slice 58, not verified** | — | 1278 |
| `MenuItemReactionSurfaceContractTests` (4 → 5) | +1 | 1279 |

**Predicted: 1279**, and the §16.3 suite stays at **21**.

**This is weaker footing than this project normally accepts and it is said first rather than buried.**
Slice 58 has not been built or run. If its run does not return 1278, chase that before applying this
archive. The two slices touch different surfaces and different test classes, so a deviation is
attributable — but §18's method depends on knowing which number was real, and if 1279 comes back then 58's
prediction was right too, because this slice adds exactly one.

## Where a number goes, which is most of what this slice decided

The read has existed since Slice 57 and the surface is one chip. What took the thinking was **where**, and
all three answers are rulings a later slice would reverse without noticing.

**Beside the name rather than in a column of its own — Stage 4d's ruling a second time.** A column empty on
most rows puts a `data-label` reading *Liked* beside nothing on the handheld card, which is exactly the
failure §11.12's label rule exists to prevent. Stage 4d refused two columns empty on *half* their rows;
this one would be empty on most. What a column buys is sortability, which is not on offer anyway — the
index sorts by heading and position. On a menu of sixty the chips **are** the comparison.

**Neutral rather than `-ok` or `-warn`.** A count of likes is neither good news nor a warning, and both
modifiers already mean something one cell over.

**Nothing rather than a zero.** The read lists what *is* liked instead of left-joining the menu. Rendering
*0 likes* on fifty-eight rows would be this surface inventing a fact the read declined to state, and would
bury the four rows that answer the question.

## The one fact worth reading twice

**The index must call `ListLikeCountsAsync` and must never call `ListLikedByAsync`.** They are one
keystroke apart and only one of them is about the person reading the page. An index calling the wrong one
**renders perfectly** — every chip says *1 like* or is absent, because the page is showing the
administrator their own opinion presented as the restaurant's. Nothing throws, no number is malformed, no
other test goes red, and the surface answers *which of these do I like* on a page that asks *which of these
is popular*. The failure mode is that **both call sites compile**.

That is a different shape from the fact it mirrors. Fact three forbids the count on the guest's side, where
the defect is a **disclosure**. Fact five is a **substitution** — right audience, wrong number — and a
restaurant where four dishes have one like each and the rest have none is a plausible restaurant, so
reading the page does not catch it.

## Scenario 21 was extended rather than a scenario added

Slice 58's open list predicted this, and the prediction held. **The extension is the only place in this
repository where §11.1's write and §11.4's read meet** — two different queries against the same rows, for
two different people, on two different surfaces. Three assertions, each refusing an implementation that
passes the others: one like while the press stands; **none against the other dish**, because *the count is
1* is also what a page hard-wired to report 1 would say; and **none against either once withdrawn**, which
a count over `'liked'` *events* rather than current opinions would fail and everything before it would not.

## Veto points

**1. The count is a chip beside the name rather than a column.** *To reverse:* add a sixth `<th>`, a sixth
`<td class="record-numeric" data-label="Liked">`, and render `0` where the dictionary has no key. What you
buy is a number in a fixed place on the wide table; what you pay is a `data-label` beside an empty cell on
most handheld cards, which is what §11.12's label rule exists to prevent, and fifty-eight zeros burying
four numbers.

**2. `data-like-count` is a data attribute rather than a class.** *To reverse:* name it
`.record-like-count`, declare a rule for it in `app.css`, and have the harness read the chip's text. Note
what you are buying: an assertion that depends on the wording *"3 likes"*, which is the one part of the
chip free to change, and a name in §11.12's shared `.record-` namespace.

**3. Scenario 21 was extended instead of a scenario 22 being added.** *To reverse:* copy the arrangement
into a second `[Fact]` and split the guest half from the administrator half. What you pay is a second
seated guest — a container, a passkey registration and a join — for two halves that are only interesting
together.

## What was verified before packaging, and what was not

**Verified mechanically.** All five contract facts were emulated against the edited tree and the new one
**proven sensitive both ways**: an index calling `ListLikedByAsync` instead is reported twice over (the
requirement fails *and* the prohibition fires), and an index that has lost the read entirely is reported
once. The §16.4 counted-class gate was emulated over the edited specification — **33 classes, no
disagreement between any claimed count and its file**, the census unchanged because the new assertion
joined an existing class. The Markdown table gate was emulated with the real unescaped-pipe splitter over
all **25** tracked documents: clean. The version gate was emulated: header 1.44, newest entry 1.44, **45**
entries strictly descending. A Razor tag-tree walk over the new chip block confirms its one `<span>` pair
balances and the `@code` block's braces close with nothing after them. The standing authoring scans ran
over every changed file: no `await` in an interpolated hole (CS4007), no `string.Create` chain with a plain
literal (CS1620), no `@section`, no tabs. Byte hygiene — no CR, one final newline, no whitespace-only or
trailing-space line — checked on every file in the archive.

**Not verified.** Nothing was compiled and nothing was run (F-71's standing caveat), **and the baseline is
a prediction rather than a measurement**, which is the larger gap. The specific risks, in order. **The
harness's row locator** — `tr:has(a.record-link[href*='{identifier:D}'])` — is the one selector here with
no precedent in that file; `:has()` is supported by the Chromium Playwright ships, and the fallback is to
walk rows and compare hrefs. **`int.TryParse` on an attribute Razor wrote from an `int`** cannot fail, so
the throw arm is unreachable and exists to make the reader total. And **the chip's placement inside
`td.record-primary`** puts a second element in a cell whose handheld card layout was designed around a link
and an optional sentence; `.chip` is already `inline-block` and should flow, but nothing here measured it
at 375px — §16.3 scenario 16 visits this page and asserts the document is no wider than its viewport, which
is the check that would catch it.

## What is open after this slice

**Nothing about likes.** Every read and every write `0008` introduced now has a caller, and no verb in §7
is without a surface. Stage 5's open list is empty for the first time since 5a.

**The next thing in the plan is Stage 6 — comments — and it is *not startable*.** It needs a rate-limiting
slice with no menu in it (§17's `/register` wall, where a second naive `AddRateLimiter` policy hijacks
§4.2's single-valued rejection handler), a `REQUIREMENTS.md` revision about guest privacy, and a moderation
surface. The plan's recommendation — *do Stage 5b and stop* — is now discharged rather than pending.
