# M6 Slice 55 — the 500 an operator found, and the picture a phone can finally upload

Extract at the repository root. Every file in the archive is a **complete file**; nothing is a patch, and
there are no scripts to run.

```
tar -xzf m6-slice-55-downscaling-and-the-500.tar.gz
```

## Files to delete

**None.** Nothing is removed by this slice.

## New files — these must be `git add`ed

Several gates in this tree enumerate their subject with `git ls-files`, so an untracked new file is a file
they do not see:

```
git add src/MyRestaurant.WebApplication/wwwroot/js/menu-picture.js
git add tests/MyRestaurant.WebApplication.Tests/Components/EditContextConsumerContractTests.cs
git add tests/MyRestaurant.EndToEnd.Tests/Harness/PictureFixtures.cs
git add tests/MyRestaurant.EndToEnd.Tests/MenuPictureScenarios.cs
```

## What is in the archive

| Path | Why |
| --- | --- |
| `src/MyRestaurant.WebApplication/Components/Pages/Administration/ManageMenuItem.razor` | **F-106**: the `ValidationMessage` moves inside its `EditForm`. Also reads the cap and splats the two downscaler attributes onto the file input |
| `src/MyRestaurant.WebApplication/Components/App.razor` | Loads `js/menu-picture.js` beside the other four classic scripts |
| `src/MyRestaurant.WebApplication/Menu/MenuItemImageEndpoints.cs` | `MenuItemImageUpload` gains the two attribute names, the status element id, and the longest edge |
| `src/MyRestaurant.WebApplication/wwwroot/js/menu-picture.js` | **new** — the browser-side downscaler |
| `src/MyRestaurant.DataAccess/Menu/MenuItemImages.cs` | `IMenuItemImageDirectory.ReadDeclaredByteCapAsync`, so the cap travels instead of being copied (**F-107**) |
| `tests/MyRestaurant.WebApplication.Tests/Components/EditContextConsumerContractTests.cs` | **new** — the gate for F-106, two facts |
| `tests/MyRestaurant.WebApplication.Tests/Menu/MenuItemImageSurfaceContractTests.cs` | Ten facts to twelve (Stage 4e, F-107) |
| `tests/MyRestaurant.EndToEnd.Tests/Harness/PictureFixtures.cs` | **new** — a real PNG at any size, generated rather than carried |
| `tests/MyRestaurant.EndToEnd.Tests/MenuPictureScenarios.cs` | **new** — §16.3 scenarios 18 and 19 |
| `docs/TECHNICAL_SPECIFICATION.md` | **v1.40**: §7 downscaling, §16.3 18–19, §16.4 the new gate and the widened surface contract, Appendix A F-106 and F-107, changelog |
| `docs/DOCUMENTATION_REVIEW.md` | F-106 and F-107 rows, and the status line |
| `docs/MENU_AND_HANDHELD_PLAN.md` | Stage 4e, and Stage 4d's two open items closed |
| `docs/BUILD_PROGRESS.md` | The Slice 55 narrative |
| `_CHANGES.md` | This file |

## Test count

Baseline **1250**, verified from your terminal log rather than predicted — the first slice in three whose
starting count was confirmed rather than assumed.

| Where | Facts | Running |
| --- | --- | --- |
| Baseline (verified) | — | 1250 |
| `EditContextConsumerContractTests` (new) | +2 | 1252 |
| `MenuItemImageSurfaceContractTests` (10 to 12) | +2 | 1254 |
| `MenuPictureScenarios` (new) | +2 | 1256 |

**Predicted: 1256.** The §16.3 suite moves from seventeen to **nineteen**; a run without
`MYRESTAURANT_E2E=1` reports the two new scenarios as skipped, exactly as it does the other seventeen.
Anything other than 1256 is the first thing to investigate.

## The defect, in three steps

Your report named the wrong request, which is why this was hard to find.
`ManageMenuItem.razor` carried `<ValidationMessage For="@(() => AltTextInput.AltText)" />` one line
**below** `</EditForm>`, inside the block that only renders when a picture exists:

1. The attaching POST renders the page first, while `_picture` is still `null` — the row is not written
   yet — so the block does not render. The upload **succeeds**, commits, writes its `attached` event, and
   redirects.
2. The browser follows the redirect. That GET is the first render in which a picture exists. An `EditForm`
   cascades its `EditContext` to its **children**, a sibling receives none, and
   `ValidationMessage.OnParametersSet` throws `InvalidOperationException` without one. **500.**
3. Every subsequent view of that item answered 500 — **including the one carrying the Remove button** — so
   the state was not reversible from any surface in the application.

If you have an item stuck in that state from before this slice, deploying this fixes it. Nothing needs to
be undone in the database: the picture and its event were written correctly all along.

## Veto points

Three decisions are worth reversing if you disagree, and each is reversible on its own.

**1. `ReadDeclaredByteCapAsync` runs on every render of the item page.** One lookup on a catalogue table,
beside the five reads already there. The alternative was a startup-cached singleton, refused because a
process remembering a number across a migration that changed it is the staleness §17 exists to prevent one
register up. *To reverse:* delete the method from `IMenuItemImageDirectory` and its implementation in
`DapperMenuItemImageDirectory`; delete `_pictureByteCap`, `PictureBudgetAttributes` and the
`_pictureByteCap = await …` line from `ManageMenuItem.razor`; drop `@attributes="PictureBudgetAttributes"`
from the file input. The downscaler then never switches on, and `TheUploadControlIsHandedTheCapAndAPlaceToReport`
and `NoFileUnderSourceRestatesTheStoredPictureCap` must go with it — leaving `MenuItemImageSurfaceContractTests`
at ten facts and §16.4 saying **Ten assertions** again.

**2. The longest edge is 1600px, and it is a genuinely new number.** It is not a second copy of anything —
no pixel dimension has ever been written down in this repository, because §8.2 stores none (F-101) — but it
is still a number somebody chose. *To reverse:* change `MenuItemImageUpload.LongestEdgePixels`. Nothing
else reads it.

**3. Scenario 19 is the riskiest thing in this delivery.** It drives a real `<canvas>` in headless
Chromium and waits on a settled status sentence beside a re-enabled control. Nothing here was executed, so
if it is flaky the likeliest cause is timing rather than the mechanism. *To reverse:* delete
`MenuPictureScenarios.cs` and `PictureFixtures.cs`, remove §16.3's rows 18 and 19 and the fixture
paragraph, and put "nineteen" back to "seventeen" in §16.4's CI sentence. **Scenario 18 is the one that
would have caught F-106**, so if only one survives, keep that one — it needs `PictureFixtures` too.

## What was verified before packaging, and what was not

**Verified mechanically.** The working tree was reconstructed from `dump.txt` and SHA-256 checked file by
file: 364 of 365 matched, the exception being `LICENSE`, which the dump elides to metadata by design. The
PNG generator's exact arithmetic — the CRC-32 table, the Adler-32 accumulator, the stored-block framing,
the integer-division ramp — was re-implemented and its output decoded at both fixture sizes: 12px is 512
bytes and decodes as 12×12 RGB, 640px is 1,229,598 bytes and re-encodes to about 16 KB as JPEG. The new
`EditContextConsumer` walk was run against the repaired tree (51 components, 83 consumers, 0 findings)
**and** against `ManageMenuItem.razor` as it shipped in the dump, where it reported one finding at the
right line. The §16.4 counted-class gate was emulated over the edited specification: 30 counted classes
against a floor of 29, no disagreements, no ambiguous paragraphs, no uncited names. The Markdown table gate
was emulated over every edited document. Byte hygiene — no CR, final newline present, no whitespace-only
lines — was checked on every file in the archive. The cap-restatement fact was emulated over the real tree:
the bound parses out of `0006` as six digits, 178 files under `src/` were scanned, and none restates it.

**Not verified.** Nothing was compiled and nothing was run. There is no .NET SDK and no reachable NuGet
from where this slice was authored, so the C# is reviewed rather than built — in particular the Playwright
call shapes in `MenuPictureScenarios`, which are the least familiar API surface in this delivery. The
downscaler has not executed in any browser; its behaviour is argued from the specifications of
`createImageBitmap`, `canvas.toBlob` and `DataTransfer`, and from the JPEG sizes measured on the fixture
images.
