# M6 Slice 60 — likes: a dish that is off tonight, and a read that had no reader

**Apply this to a tree at Slice 59.** It edits five files that Slices 58 and 59 created or last touched, so
extracting it over an older tree will leave a Razor component and a scenario referencing things that do not
exist.

Extract at the repository root. Every file in the archive is a **complete file**; nothing is a patch, and
there are no scripts to run.

```
tar -xzf m6-slice-60-like-an-unavailable-dish.tar.gz
```

## Files to delete

**None.** Nothing is removed by this slice.

## New files — these must be `git add`ed

**None.** Every file in the archive already exists in the tree.

## What is in the archive

| Path | Why |
| --- | --- |
| `src/MyRestaurant.WebApplication/Components/Pages/Table/TableOrderSurface.razor` | The way into the panel for a dish that cannot be staged, and `ChooseItem`'s corrected paragraph |
| `src/MyRestaurant.WebApplication/wwwroot/app.css` | `.order-menu-inspect`, and `.order-menu-item` becomes a column |
| `tests/MyRestaurant.WebApplication.Tests/Menu/MenuItemReactionSurfaceContractTests.cs` | The sixth and seventh facts |
| `tests/MyRestaurant.EndToEnd.Tests/Harness/TableOrderJourneys.cs` | `InspectAsync`, and F-113's repair |
| `tests/MyRestaurant.EndToEnd.Tests/MenuReactionScenarios.cs` | Scenario 21 extended; still one `[Fact]` |
| `docs/TECHNICAL_SPECIFICATION.md` | **v1.45**: §7, §11.1, §16.3 scenario 21, §16.4 five → seven, two Appendix A rows, changelog |
| `docs/MENU_AND_HANDHELD_PLAN.md` | Stage 5c, landed; the carried open item struck through in both stages; the header's stale date deleted |
| `docs/DOCUMENTATION_REVIEW.md` | **F-113** |
| `docs/BUILD_PROGRESS.md` | The Slice 60 narrative, shipped whole |
| `_CHANGES.md` | This file |

## Test count

| Where | Facts | Running |
| --- | --- | --- |
| Baseline — **measured**, from your last run | — | 1279 |
| `MenuItemReactionSurfaceContractTests` (5 → 7) | +2 | 1281 |

**Predicted 1281**, and the §16.3 suite stays at **21**.

This is the first slice in four whose baseline was measured rather than carried forward, so a deviation is
attributable to this slice alone rather than to a chain of predictions.

## The drift, said first

The session opened believing this tree was at **Slice 54** — picture history, roughly 1250 tests, ledger
through F-105. The SHA-256 reconstruction said **Slice 59**, v1.44, 1279, F-112. Five slices, which is the
largest gap the reconstruction has caught. 374 of 375 files verified byte-exact; the two that did not are
`export.sh`, whose dump rewrites its own header into the copy it embeds, and `LICENSE`, which the dump
elides to metadata deliberately. Nothing was reconstructed on a guess.

## What this slice closes

**A guest could not like an unavailable dish.** §11.1 puts the like in the detail panel, the panel opens
only for a *chosen* item, and §7 renders a deactivated item's card `disabled` — so *the salmon is off
tonight and it is still the best thing here* was an opinion this surface could not record. Stage 5b-i wrote
the gap down and named the repair; this is the repair.

## The one ruling worth reading twice

**The card stays `disabled`.** The obvious change is to drop it so one control does both jobs, and it
works: §7's "cannot be added to a send" is enforced by `OrderStaging.Stage`, which refuses an inactive item
**by name**, and again by the send transaction under the lock. The markup gets smaller. Every existing test
stays green.

It is refused because §7's rule is about *staging* and the card is the staging control. A card that
answered a tap on a dish the surface already knows is off would be inviting somebody to press *Add to
basket* and be told no. What was missing was a **path**, not a looser refusal — so the card keeps
`disabled` and gains a sibling inside the same `<li>`.

A sibling and not a child, which is the parser ruling for the second time: a `<button>` inside a
`<button>` is markup the parser silently splits, taking the half that carries the dish's name out of the
staging path with nothing thrown and the Razor compiling.

## Veto points

**1. The card keeps `disabled` and gains a second control.** *To reverse:* delete the
`.order-menu-inspect` block and the guard around it, drop `disabled="@(!item.IsActive)"` from the card, and
delete the sixth contract fact. What you buy is one control per card instead of two. What you pay is a
guest tapping a dish that is off, choosing it, pressing *Add to basket* and being refused — the invitation
§7's `disabled` exists to withhold.

**2. *Add to basket* is asserted never `disabled`.** *To reverse:* delete the seventh fact and bind the
button to the chosen item's availability. Note what you are buying: a dead control with no reason on it,
where `OrderStaging` would have named the dish, and a second opinion about availability inside a component
whose staging area already holds one (F-65).

**3. The accessible name is a visually hidden span rather than an `aria-label`.** *To reverse:* put
`aria-label="Read about @item.Name"` on the button and delete the span. What you pay is voice control: an
`aria-label` replaces the content, so *"click Read about"* stops matching for the population most likely to
be using it.

**4. Scenario 21 was extended instead of a scenario 22 added.** *To reverse:* copy the arrangement into a
second `[Fact]`. What you pay is a second container, a second passkey registration and a second join, for
an arrangement this scenario already has standing.

**5. F-113 adds no gate.** *To reverse:* write one, and note what it needs — a CSS selector engine resolved
against a component tree, because *which reads a `text-transform` rule reaches* is not decidable from text.
A lexical approximation reports findings on correct reads while missing transformed ones, which is F-41's
*reaching past what it can decide*.

## F-113, and why it could not be seen

`ChosenItemDetail.Facts` is keyed by the detail panel's `<dt>` terms and its own paragraph names them
*Price*, *Available* and *On the menu since*. The reader built it with `InnerTextAsync`, and `app.css`
upcases `.order-menu-facts dt` — so it produced `AVAILABLE`.

**F-88's mechanism a second time inside the file F-88 was found in.** That fix corrected two reads in one
slice and then wrote *"only this one read is affected"*, which was wrong by one at the moment of writing.
It was narrowly true only because `Facts` was a **read with no reader**: nothing had ever looked a term up,
so the disagreement between the record and its reader was unobservable rather than unobserved.

The term now goes through `ScreenText.DeclaredAsync`; the `<dd>` beside it deliberately does not, that
being `ScreenText`'s own distinction between a label and content. What closes the hole is that `Facts` now
has a caller — this slice's scenario step.

## What was verified before packaging, and what was not

**Verified mechanically.** All seven contract facts emulated against the edited tree, and the two new ones
**proven sensitive against six planted defects**: the card's `disabled` dropped, the control moved inside
the card's `<button>`, the guard removed, *Add to basket* given a `disabled` binding, and the control
deleted outright. Every one was reported, by the assertion that should report it. §16.4's counted-class
gate emulated over the edited specification — **33 classes, no ambiguity, no disagreement**. The Markdown
table gate emulated with the real unescaped-pipe splitter over all **25** tracked documents: clean. The
version gate emulated: header 1.45, newest entry 1.45, **46** entries strictly descending. Brace and
parenthesis balance checked on every changed C# file. Byte hygiene — no CR, one final newline, no tab, no
whitespace-only or trailing-space line — on every file in the archive. No `await` in an interpolated hole
(CS4007); no `string.Create` chain with a plain literal (CS1620); no `@section`.

**Not verified.** Nothing was compiled and nothing was run (F-71's standing caveat). **The layout change is
the largest risk and no gate here can see it**: `.order-menu-item` becomes a column and the card takes
`flex-grow: 1` so a row of cards stays level, and nothing in this repository measures §11.1 at any width —
§16.3 scenario 16's 375px barrier is scoped to §11.4's surfaces. If a row looks ragged, that is the line.
Second, `MenuInspectSelector` uses a `>` child combinator; the control is a direct child of the `<li>`
today, and a Razor construct between them would break the selector while the contract fact still passed.
Third, `KitchenJourneys.OpenAsync` is called on the administrator's page, which no scenario had previously
used as both the kitchen and the menu index — §3.7 permits it and that method's own summary says so.

## What is open after this slice

**Nothing about the menu enhancement.** Stage 5's list is empty for the second time, and this time nothing
was deferred into it. Stage 6 — comments — remains *not startable* for the reasons it records: a
rate-limiting slice with no menu in it, a `REQUIREMENTS.md` revision about guest privacy, and a moderation
surface.

**One new open item, named rather than carried silently:** nothing in this repository measures §11.1 at
375px. The handheld barrier is scoped to §11.4, and this slice is the first to change the guest menu's box
model. The repair is a scenario, and it needs a seated guest.
