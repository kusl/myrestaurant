# M6 Slice 53 — the picture a guest can finally see, and a plan that argued for it wrongly

Extract at the repository root. Every path is repo-relative and every file is complete.

```
tar -xzf m6-slice-53-guest-menu-pictures.tar.gz
git status
```

**Files to DELETE: none.**

**`git add` IS required — two new files, both in directories that already exist.**

```
git add src/MyRestaurant.DataAccess/Migrations/0007_menu_item_image_alt_text.sql
```

That is **one** new file. `git status` should show **thirteen modifications and one untracked path**;
anything else untracked means the archive was extracted somewhere other than the repository root.

**No new directory, no new package, no `.slnx` edit, no `.csproj` edit** — `Migrations/*.sql` is a glob, so
the migration is picked up without one — **no `compose.yaml` edit, no `REQUIREMENTS.md` edit, no
`OPERATIONS.md` edit, no ADR amended, no `export.sh` edit, no `SchemaMigrationRunnerTests` edit, and no
§16.3 scenario added or extended.**

---

## Read this first: I arrived believing this project was at Slice 47

It is at Slice 52, landed and green. `ResequenceMenuSectionsAsync` shipped in Slice 47 and the item-level one
in Slice 48; the menu index, the kitchen's "86" panel, the image schema and the administrator's upload form
all followed. **Six slices of drift**, from a summary of prior work rather than from the tree.

The reconstruction from `dump.txt` is what caught it, as it did for the session that authored Slice 51 —
which arrived believing the tree was at Slice 46. **363 file records parsed, 361 verified against their
SHA-256 exactly**; the two exceptions are by design (`export.sh` contains the dump delimiter and is excluded,
`LICENSE` is elided to metadata and hash).

Your last run was `total: 1233, failed: 0` against a prediction of **1233** — §18's arithmetic matching to
the digit for the fifth consecutive slice.

This is **Stage 4c**, plus the finding found while reading the paragraph that specified it.

---

## The stage: §11.1 renders the picture

`IMenuItemImageDirectory.ListAsync` had been a read with no caller for three slices. It has its caller: one
read per menu load, in the same pass as the menu and re-read on the same `MenuChanged`, because a picture
dictionary loaded once would be a second place for the menu to be stale.

**A 4rem square thumbnail beside the name, cropped under `object-fit: cover`.** The placement is what the
plan asked for; the size and the crop are this slice's, and the obvious alternative is wrong. Nothing in this
stack can resize an image, so at `height: auto` a portrait photograph renders twice as tall as it is wide and
the card height is straight back where the re-layout started. The crop is paid back in the **detail panel**,
which renders the same picture uncropped — something §11.1 has owed since Slice 39, when it named that panel
the surface images are read on.

**`loading` is per heading, not per card.** How many cards sit above the fold depends on description length,
viewport width and the reader's text size, none of which the server knows. A heading is the coarsest unit
certainly right at one end. A card count would be a number invented here and wrong on somebody's phone.

## The caption: a verb rather than a field, and the attach signature did not change

The plan called `alt_text` *"a field on the item's picture form"*. **The upload form requires a file**, so a
caption settable only there makes correcting a typo cost a re-upload: a new `menu_item_image_identifier`,
every cached copy of an *unchanged* photograph invalidated for a year, and a `replaced` event recording a
replacement that replaced nothing.

So `0007` adds `alt_text_changed`, `SetMenuItemImageAltTextAsync` writes one `text` column, and **the attach
carries the caption forward** from the row it deletes onto the row it writes with no event for the carry.
Both halves have a plausible wrong implementation: resetting to `''` would strip alternative text off a menu
as a side effect of improving a photograph, and an event for the carry would claim a caption moved when it
did not.

`0007` widened the vocabulary **by name** and **widened neither existing biconditional**, which is the
surprising half — a caption is not a fact about the file, so `alt_text_changed` sits outside both right-hand
sides and passes each with NULL.

## The finding (F-103): the plan's justification for the column is false

It read: *"an `<img>` with no alternative text on a menu is a card a screen reader renders as nothing."*

That conflates a **missing** `alt` attribute — a screen reader announces the URL, here a bare UUIDv7 — with
`alt=""`, which marks an image decorative and is correctly **skipped**. §11.1's card is a `<button>` holding
the dish's name and price as text, so its accessible name is already *"Grilled salmon £24.00"*. **`""` is the
right value for most pictures on this menu**, and the column earns its place only for the ones that say
something a name does not — which is the true claim the same paragraph makes two clauses earlier.

**F-101's mechanism one register up:** there a DDL sketch in a design document became a migration; here an
*argument* became markup. Three gates read that file and none reads a sentence for whether it is true.
Corrected rather than deleted (F-77's habit), and the gate is shaped by the defect: the *correct* value is
usually empty, so the *incorrect* markup renders identically everywhere and exists only in the source.

**One consequence nothing anticipated:** the `<img>` is **last in the document and first on the screen**. A
button's accessible name is computed in document order, so a captioned picture placed first would announce
its own description before the dish it describes.

---

## Veto points

Each is flagged because it is a decision rather than an implementation, with how to reverse it.

**1. The caption is a separate verb rather than a parameter on the attach.** This is the largest scope call in
the slice: it costs a migration, an enum, an interface method, a workflow method, a form and five tests. To
reverse: drop `SetMenuItemImageAltTextAsync` and its outcome enum, add `altText` as a sixth parameter to
`AttachMenuItemImageAsync`, delete `alt_text_changed` from `0007` along with the third biconditional, and
accept that fixing a typo re-uploads the photograph.

**2. The thumbnail crops.** `object-fit: cover` on a fixed 4rem square. To reverse: in `app.css`, change
`height: 4rem` to `height: auto` and delete `object-fit: cover` from
`.order-menu-item.has-picture .order-menu-thumbnail`. Card heights then vary with each photograph's aspect
ratio, which is what the re-layout exists to prevent — the trade is stated beside the rule.

**3. The detail panel renders the picture too.** Not asked for by Stage 4c; §11.1's own text has promised it
since Slice 39. To reverse: delete the `@if (PictureFor(chosenItem) is { } chosenPicture)` block from
`TableOrderSurface.razor` and the `.order-menu-detail-picture` rule from `app.css`, and correct §11.1.

**4. F-102 has no row in `DOCUMENTATION_REVIEW.md` and I did not backfill one.** Slice 52 put it in the
status paragraph and in Appendix A but not in the ledger's table. F-103 has a row. Reversing a prior slice's
filing decision is a decision, so it is **named as carried** rather than quietly fixed. If you want it, it is
one row.

**5. `alt` on the administrator's own thumbnail stays `""`.** The caption renders as visible text beside the
file's facts instead. To reverse: change `alt=""` to `alt="@_picture.AltText"` in `ManageMenuItem.razor` and
accept that a screen reader reads the dish's name, then the caption, on a page whose `<h1>` is that name.

---

## Test count arithmetic

Uncompiled, per §18. **1233 → 1242.**

| Class | Was | Now | Why |
|---|---|---|---|
| `MenuItemImageTests` | 11 | 15 | the caption stored without moving the identifier; the no-op and the clearing; the carry across a replace with no event; the two silent refusals |
| `MenuItemImageSurfaceContractTests` | 6 | 10 | the caption form carries no file input; the guest card renders the picture and reads once; every `<img>` carries `alt`; the card's `alt` comes from the column |
| `MenuWiringTests` | 26 | 27 | a caption is announced only when it moved, and arrives verbatim |

§16.4's counted-class floor **stays at 28** — the guest surface joined the existing class rather than
founding a new one, because the route-helper claim is one claim over two files, and the census counts
classes. §16.3 stays at seventeen.

**Any deviation from 1242 is the first thing to investigate after a run.**

---

## What was verified, and what was not

**Verified.** The SHA-256 reconstruction above. A Razor tag-tree walk on both edited components **against the
pristine files re-extracted from the dump** — with `@(…)` expressions masked, `ManageMenuItem.razor` is a
clean tree before and after, and `TableOrderSurface.razor` retains exactly three pre-existing artefacts of a
generic type in markup, identical before and after. §16.4's count gate **simulated in full**: 28 counted
classes against a floor of 28, no ambiguity, no uncited class, no disagreement. The specification version
gate simulated: header `1.38`, newest entry `v1.38`, all descending. The Markdown table gate simulated across
all five edited documents. Brace and paren balance on every edited file, compared against the pristine file
where a naive count was already non-zero. Every implementer of the three changed interfaces enumerated and
updated. `Migrations/*.sql` confirmed to be a glob. The end-to-end harness confirmed to read the card's spans
as **descendants**, so the new `.order-menu-body` wrapper cannot break scenario 17. Byte hygiene on all
fourteen files. Every CSS custom property confirmed present in `:root`.

**Not verified.** **Nothing was compiled** — no SDK, no database, no browser. `0007` has never run. **The
4rem square has never been rendered**, so whether the crop treats a real photograph acceptably is a judgement
you will want to make after looking at it — that is the likeliest thing in this slice to want an opinion. **No
screen reader has read any of this**: the F-103 argument is reasoned from the accessible-name computation
rather than observed, and the gate asserts the markup, which is the checkable half.

---

## Files in this archive

| Path | New? |
|---|---|
| `src/MyRestaurant.DataAccess/Migrations/0007_menu_item_image_alt_text.sql` | **new** |
| `src/MyRestaurant.DataAccess/Menu/MenuItemImages.cs` | modified |
| `src/MyRestaurant.WebApplication/Menu/MenuWorkflow.cs` | modified |
| `src/MyRestaurant.WebApplication/Components/Pages/Table/TableOrderSurface.razor` | modified |
| `src/MyRestaurant.WebApplication/Components/Pages/Administration/ManageMenuItem.razor` | modified |
| `src/MyRestaurant.WebApplication/wwwroot/app.css` | modified |
| `tests/MyRestaurant.DataAccess.Tests/Menu/MenuItemImageTests.cs` | modified |
| `tests/MyRestaurant.WebApplication.Tests/Menu/MenuWiringTests.cs` | modified |
| `tests/MyRestaurant.WebApplication.Tests/Menu/MenuItemImageSurfaceContractTests.cs` | modified |
| `docs/TECHNICAL_SPECIFICATION.md` | modified (v1.38) |
| `docs/DOCUMENTATION_REVIEW.md` | modified (F-103) |
| `docs/MENU_AND_HANDHELD_PLAN.md` | modified (Stage 4c landed) |
| `docs/BUILD_PROGRESS.md` | modified (Slice 53) |
| `_CHANGES.md` | this file |
