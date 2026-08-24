# M6 Slice 56 — the bytes decide the format, and the build CI runs

Extract at the repository root. Every file in the archive is a **complete file**; nothing is a patch, and
there are no scripts to run.

```
tar -xzf m6-slice-56-bytes-decide-the-format.tar.gz
```

## Files to delete

**None.** Nothing is removed by this slice.

## New files — these must be `git add`ed

Several gates in this tree enumerate their subject with `git ls-files`, so an untracked new file is a file
they do not see:

```
git add tests/MyRestaurant.WebApplication.Tests/Menu/MenuItemImageContentTypeContractTests.cs
```

That is the only new file.

## What is in the archive

| Path | Why |
| --- | --- |
| `tests/MyRestaurant.EndToEnd.Tests/Harness/PictureFixtures.cs` | **F-108**: the `stackalloc` moves above the loop. This is the build break |
| `src/MyRestaurant.WebApplication/Components/Pages/Administration/ManageMenuItem.razor` | **F-109**: identifies the media type from the bytes. **F-110**: both format censuses become derived. Also gains `@using MyRestaurant.Domain.Menu` |
| `src/MyRestaurant.WebApplication/Menu/MenuItemImageEndpoints.cs` | **F-110**: `MenuItemImageUpload.RecognisedTypesForOperators`, derived from the same census `AcceptAttribute` is |
| `tests/MyRestaurant.WebApplication.Tests/Menu/MenuItemImageContentTypeContractTests.cs` | **new** — three facts, all proven sensitive against the tree as it shipped |
| `tests/MyRestaurant.EndToEnd.Tests/MenuPictureScenarios.cs` | §16.3 scenario **20** |
| `docs/TECHNICAL_SPECIFICATION.md` | **v1.41**: §7 rewritten on what decides a media type, §16.3 scenario 20, §16.4 the new class, Appendix A F-108/F-109/F-110, changelog |
| `docs/DOCUMENTATION_REVIEW.md` | Three ledger rows, and a status line about deferrals rather than about media types |
| `docs/MENU_AND_HANDHELD_PLAN.md` | Stage 4f, and Stage 4e's carried item struck through and closed |
| `docs/BUILD_PROGRESS.md` | The Slice 56 narrative, shipped whole |
| `_CHANGES.md` | This file |

## Test count

Baseline **1256**, verified from your terminal log. Slice 55 predicted 1256 and the run returned 1256.

| Where | Facts | Running |
| --- | --- | --- |
| Baseline (verified) | — | 1256 |
| `MenuItemImageContentTypeContractTests` (new) | +3 | 1259 |
| `MenuPictureScenarios` (2 to 3) | +1 | 1260 |

**Predicted: 1260.** The §16.3 suite moves from nineteen to **twenty**. Anything other than 1260 is the
first thing to investigate.

## The build break, in one paragraph

`ci_local.sh` stopped at step 5 and everything after it never ran. `PictureFixtures.Deflate` declared a
four-byte `stackalloc` inside its loop; stack memory is released when the *method* returns rather than when
the iteration ends, so the frame grows once per pass. In fact it cost seventy-six bytes. What matters is
that `Directory.Build.props` deliberately leaves warnings non-fatal for a plain `dotnet build` and makes
them errors under the flag CI passes — so the same defect is one line of scrollback locally and a halt in
the pipeline. Your session shows three green signals and then the one that counts. **No gate is added:**
CA2014 is the gate, it ran, it decided correctly and it blocked.

## The six-slice item, and why it took six slices

Stage 4b diagnosed F-109 correctly, wrote the fix out in full — *identify from the bytes and pass that* —
and declined it on one sentence: doing so *"would make two of the write's outcomes unreachable from the only
surface that can reach them"*. That sentence was copied forward into four consecutive open-item lists
without being re-read as a claim.

It is true, and it is not a cost. Those two outcomes belong to a **library**, and `MenuItemImageTests`
reaches both directly on every integration run; an outcome no *form* can produce is not an outcome nothing
tests. The real worry underneath — that a surface deciding what an image is would be a second authority on
what may be stored — is answered rather than dismissed: what the surface passes is the answer of the same
pure function the write consults, so there is one decision procedure called twice, and the write's two
checks are now true by construction rather than by luck.

**F-62 said a reason for not doing something is a claim about the tree, checked before it is written down.
This adds the other half: a claim used to defer is re-checked each time it is used again.**

## Veto points

Three decisions are worth reversing if you disagree, and each is reversible on its own.

**1. `ContentTypeContradictedByBytes`'s arm is kept although nothing can now reach it from that page.** The
alternative is deleting it, which is tidier and which answers a future refusal with a redirect and silence —
the worst failure available on an upload surface. *To reverse:* delete the `case` and its comment from
`ManageMenuItem.razor`. Nothing else changes; the enum member and its write-side test stay where they are.

**2. The refusal renders media types (`image/jpeg, image/png, image/webp`) rather than English names.** It
reads a little more mechanically. The alternative needs a map from type to article-and-name, which would be
the fourth copy of the vocabulary this change exists to remove. *To reverse:* change
`MenuItemImageUpload.RecognisedTypesForOperators`; both the lede paragraph and the refusal read it, and the
third gate keeps working as long as no format name is spelled in the page itself.

**3. Scenario 20 asserts that the downscaler leaves a small file's declared type untouched.** That is
argued from `menu-picture.js`'s early return rather than observed, and it is the one assertion in the new
scenario that could fail for a reason having nothing to do with F-109. *To reverse:* delete the
`Assert.Equal(UnnamedContentType, held)` block. The scenario still proves the fix; it just stops proving
that it is testing what it thinks it is.

## What was verified before packaging, and what was not

**Verified mechanically.** The tree was reconstructed from `dump.txt` and SHA-256 checked file by file: 370
files, the only differences being `export.sh` (it embeds its own file marker) and `LICENSE` (elided by
design). **No session drift.** All three new gates were emulated twice — against the repaired tree, where
each passes, and against `ManageMenuItem.razor` exactly as it shipped in your dump, where each fails: the
first naming `file.ContentType`, the second reporting no `IdentifyContentType` call, the third naming all
three format words and the missing census. 185 files under `src/` were walked and exactly one binds an
`IFormFile`. The §16.4 counted-class gate was emulated over the edited specification: 31 counted classes
against a floor of 29, no disagreements, no ambiguous paragraphs, no uncited names. The Markdown table gate
was emulated with the real unescaped-pipe splitter across every tracked document: 415 rows, no problems.
The version gate was emulated: two versioned documents, headers matching their newest entries, entries
descending. Byte hygiene was checked on every file in the archive.

**Not verified.** Nothing was compiled and nothing was run — and since this slice exists partly because of
that gap, it is worth stating plainly. The specific risks, in order: the `@using MyRestaurant.Domain.Menu`
added to `ManageMenuItem.razor` is a new import on a Razor page and would fail as `CS0246` if `ImageFormat`
did not resolve from there (it is `public static` in `MyRestaurant.Domain`, which `MyRestaurant.WebApplication`
references transitively through `MyRestaurant.DataAccess`, and `MenuItemImageEndpoints.cs` already imports
that namespace in the same assembly); the second gate's argument walk assumes the attach call's first `);`
is its own, which is true of the shipped formatting and would break if the call were reformatted onto one
line; and scenario 20's `type` assertion is argued rather than observed, which is veto point 3.
