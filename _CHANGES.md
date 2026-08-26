# Slice 66 — house cleaning: the comments, the four documents, and the dump that had outgrown its reader

Extract at the repository root. Every path in this archive is repository-relative and every file is
complete; nothing here is a patch.

**Nothing in this slice changes behaviour.** No production code path was altered. Every edit under
`src/` is the removal of comment bytes, and every edit under `tests/` is either the removal of comment
bytes or one of the two named repairs below.

## What landed

| Change | Measure |
|---|---|
| Comments removed from authored `.cs`, `.razor`, `.sql`, `.css`, `.js`, `.sh` | 2,087,175 bytes across 334 files — 42.1% of all code |
| Four documents archived verbatim to `docs/progress/` and rewritten as registers | 1,655,921 bytes withheld from the dump |
| `SourceCommentContractTests` added — no authored C# or Razor file carries a comment | 2 assertions |
| `DocumentationCommentContractTests` deleted — its subject no longer exists | −2 assertions |
| `ConfigurationSurfaceTests` no longer terminates its scan on a documentation comment | F-119 |
| `TECHNICAL_SPECIFICATION.md` to v1.51 | 733,597 → 171,249 bytes |

Projected context dump: **7.16 MB → about 3.4 MB.**

## Files to delete

```
tests/MyRestaurant.WebApplication.Tests/Documentation/DocumentationCommentContractTests.cs
```

That is the only deletion. Nothing else in the tree is removed or renamed.

## Files that need `git add`

Untracked files are invisible to every gate that uses `git ls-files`, which is both hygiene gates.

```
tests/MyRestaurant.WebApplication.Tests/Documentation/SourceCommentContractTests.cs
docs/progress/TECHNICAL_SPECIFICATION_THROUGH_V1_50.md
docs/progress/DOCUMENTATION_REVIEW_THROUGH_F_118.md
docs/progress/BUILD_PROGRESS_THROUGH_M6_SLICE_65.md
docs/progress/MENU_AND_HANDHELD_PLAN_THROUGH_STAGE_1D.md
```

`docs/progress/BUILD_PROGRESS_THROUGH_M6_SLICE_39.md` is already tracked and is **not** in this
archive. It is untouched.

## Test count

1302 before. `DocumentationCommentContractTests` removes two; `SourceCommentContractTests` adds two.
**Predicted: 1302, unchanged.** A deviation is the first thing to investigate, and the likeliest cause
is an analyzer diagnostic promoted to an error by `ContinuousIntegrationBuild=true` rather than a
failed assertion.

## What was verified

Every stripper was proven against a real checker rather than against its own output.

- **Shell:** `bash -n` passes on all eleven scripts. `--help` still prints usage on all eight scripts
  that implement it by echoing their own header block — those headers were rewritten by hand, not
  stripped, because they are functional.
- **JavaScript:** `node --check` passes on all five files.
- **C#:** the complete string-literal stream is byte-identical before and after in all 258 files, so no
  literal was damaged. Bracket balance holds, stripping is idempotent, every original line containing
  no comment marker survives verbatim, and `[Fact]`/`[Theory]` counts are unchanged in all 114 test
  files.
- **Razor:** markup is untouched — `<` and `>` counts outside comments are identical, and the three
  modes (markup, `@code` C#, `<style>` CSS) were checked separately.
- **SQL:** dollar-quoted bodies and named constraints survive; the three `event_type` vocabularies §8.2
  quotes still equal the three the migrations declare, in both directions.
- **Gates:** every floor was recomputed on the stripped tree and compared against the original —
  §16.4 counted classes 37 → 44, component `<style>` blocks 14 → 14, CSP `.razor` files 51 → 51,
  configured keys 19 → 19, refused names 17 → 17, unique test classes 111 → 111. Markdown: 29
  documents, 71 tables, 842 rows, no malformed run. Byte hygiene: 387 files, zero problems. All four
  withheld documents are linked by path from a document the dump contains. No file outside the record
  files asserts a platform setting.

## What was NOT verified

- **Nothing was compiled.** No .NET SDK was available, so `dotnet build` and `dotnet test` have not run.
  The test count is arithmetic, not an observation, and the two new assertions have never executed.
- **No browser ran**, so the §16.3 scenarios are unverified.
- **No container engine ran**, so the integration suite and the restore drill are unverified.
- A Razor component's **rendered output** was not compared before and after; the Razor proof is lexical.
- `scripts/check_tree.sh` and `scripts/check_repository.sh` were emulated in Python, not executed.

## Veto points

Each of these is a decision you may want to reverse, with how to reverse it.

**1. Comments are banned outright rather than capped.** `SourceCommentContractTests` asserts zero
comments in authored C# and Razor, not a density ceiling. A ceiling would need a number, and a number
in a gate is a number that gets raised. *To reverse:* delete
`tests/MyRestaurant.WebApplication.Tests/Documentation/SourceCommentContractTests.cs`, remove its
paragraph from §16.4, and remove the no-comment paragraph from §18. §16.4 then carries 43 counted
classes, still above its floor of 37.

**2. `DocumentationCommentContractTests` was deleted, not repaired.** Its floor of 1,500 `///` blocks
cannot be met by a tree with none, so the assertion could not fail. *To reverse:* restore it from git
and lower `MinimumBlocksScanned` to zero — but note that this makes it a gate that cannot fail, which
is what F-41 forbids.

**3. `OPERATIONS.md`, `README.md`, `REQUIREMENTS.md` and the fifteen ADRs are untouched.** Together
206 KB, under 3% of the tree, and each is a live runbook, the requirement this specification
implements, or the rationale record. *To reverse:* they are a separate slice.

**4. `SourceCode.WithoutComments` was reused rather than corrected.** Its stated residual — a
multi-line verbatim or raw string literal is read as code on its inner lines — means a `//` inside
multi-line SQL would be reported as a comment by the new gate. No such literal exists in this tree
today, and the failure direction is a loud false finding rather than a silent pass. *To reverse:* teach
that reader about multi-line literals and extend `SourceCodeTests` to prove it; on today's tree the
change is a no-op, which is why it was not made here.

## Findings

**F-119 — a gate's scan was terminated by a documentation comment.** `ConfigurationSurfaceTests` read
every configured key out of `RestaurantOptions.cs` between `public static RestaurantOptions
FromConfiguration` and the string `/// <summary>Returns a human-readable reason`. F-116 ruled that no
gate may depend on prose declining to quote its subject; this is the inverse and the same error. The
scan now ends at `ValidationMethodMarker`, the declaration that comment described, and two constants
become one. Verified identical before and after: 19 configured keys, 17 refused names, no orphans.

Worth naming how it was found: it did **not** appear in the first pass, because that pass searched the
whole tree for each gate's string literals and the marker survives in the gate's own source. It
appeared only once test sources were excluded from the corpus — the same shape as F-67, where a gate
could not tell a use from a mention.

**F-120 — comments were 42% of authored source and four documents were 21% of the tree, and nothing
measured either.** The consequence was a context dump at 96% of the budget a session has to read it in.
Every word of it was already in git. All comments removed; the four documents archived and rewritten;
`SourceCommentContractTests` holds the result.
