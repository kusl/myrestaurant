# M6 Slice 46 — the dump that had become mostly history

Extract at the repository root. Every path is repo-relative and every file is complete.

```
tar -xzf m6-slice-46-context-dump-reduction.tar.gz
git add docs/progress/BUILD_PROGRESS_THROUGH_M6_SLICE_39.md
git add tests/MyRestaurant.WebApplication.Tests/Documentation/ContextDumpExclusionContractTests.cs
git status
```

**Files to DELETE: none.**

**`git add` IS required, for two new files, and it is not optional.** Every gate and CI job in this
repository enumerates with `git ls-files`. An untracked file is invisible to `scripts/check_tree.sh`, to
`scripts/check_repository.sh`, to the shell-scripts job — and, in this slice specifically, to `export.sh`
itself, which means an untracked archive would be missing from `dump.txt` for a reason that has nothing to
do with the exclusion this slice adds.

**No behaviour change. Nothing under `src/` is in this archive at all.** No schema change, no migration,
no read changed, no surface changed, no Razor component, no CSS, no new package, no `compose.yaml` edit,
no `.slnx` edit, no `REQUIREMENTS.md` edit, no ADR edit.

---

## What this is

Your dump had reached **6.08 MiB and 87% of the project capacity it loads into**. That is a dump which
will refuse a session partway through the next feature, and the cost of hitting that ceiling mid-slice is
a slice delivered from a partial tree. So this slice fixes the container before more goes into it.

**Measured on the produced file, not predicted.** I ran the rewritten `export.sh` against a `git init`ed
copy of your tree and read the output back:

**6,377,323 bytes before, 5,483,728 after — roughly 894 KiB smaller, about 14%, taking 87% of capacity
to about 75%.**

Round numbers on purpose. The dump contains `docs/BUILD_PROGRESS.md`, which contains this slice's own
entry, which contains this measurement — so the artefact measures itself and a figure to the byte would
be false precision. The exact before-and-after above is what the two runs reported; the saving is stated
loosely because your tree has commits mine does not.

Three changes get there, and they are worth different amounts:

| Change | Saves | Why it is safe |
| --- | --- | --- |
| `docs/BUILD_PROGRESS.md` splits at Slice 40 | 730 KiB | the archive is a real tracked file, byte-exact, linked by path from the retained half |
| metadata block: twelve fields to three | 164 KiB | the three kept are the three that make a reconstruction checkable |
| `LICENSE` body elided | 34 KiB | the SHA-256 is still dumped, so a modified licence is still detectable |

## The menu is next, and why it is not here

**Slice 45 was green twice** — `total: 1162, failed: 0` on both `dotnet test` and
`ci_local.sh --with-all --with-e2e`, plus the five-times-repeated `MenuEventLogTests` you ran, 9 of 9 each
time. That is the evidence F-95 needed, since a 50% property cannot be cleared by one green run.

**So Stage 3a — `ResequenceMenuSectionsAsync` — is unblocked, and it is the next slice.** It is deferred
here by name rather than dropped, which is the discipline that let six earlier verbs be reported as
discharged rather than noticed later. The reason it is not in this archive is the ruling Slice 45 itself
made and this is the second application of: **one change, one green run, then the feature.** This slice
moves 730 KiB of documentation that four gates read and changes what a fifth script enumerates. If a new
write rode along and the run came back red, there would be two candidate causes, and §18's habit of
chasing a count deviation before the slice closes gets expensive with two.

## Two defects this caught before packaging

**`scripts/check_repository.sh` gate 3 would have failed on the archive.** That gate forbids a document
from asserting platform state (F-42) and exempts the files whose job is to *quote* such a claim. The
archived log quotes F-42's own sentence — *"Issues are disabled. There is no bug…"* — at what is now line
6252 of `docs/progress/`. Moving history out of an exempt file into a new file carries the exemption with
it, or the first run after the split is red for a reason unrelated to anything anybody changed.
`docs/progress/*` joins `RECORD_FILES`, and the new gate's fourth assertion asserts it is there so the
next tranche cannot arrive without it.

**The first split attempt failed tree hygiene.** Taking the archive through line 11095 captured the blank
line between the two slices, leaving the file ending in two newlines — which gate 3 of
`scripts/check_tree.sh` refuses. The split now consumes that line and asserts its own boundary line
numbers rather than trusting them.

## One claim went false rather than stale

`scripts/check_tree.sh` said its `GENERATED_DIRECTORIES` was *"kept in step with export.sh's
EXCLUDED_DIRECTORY by hand."* After this slice the exporter withholds **two** directories and only one of
them is generated — so that sentence is not merely out of date, it points at the wrong conclusion.
Hygiene-exempting the archive because it happens to be absent from a dump would stop checking 749 KiB of
authored prose for exactly the appended-separator defect that reached twenty-one files before anybody
noticed (F-40).

**Withheld from the dump and exempt from hygiene are different properties, and only one of them belongs to
the file.** So `export.sh` now names three kinds of held-out path instead of one:

- `GENERATED_DIRECTORIES` — tool output. `docs/llm`. Skipped by tree hygiene, because a dump's own
  structure is the separator that gate forbids.
- `ARCHIVED_DIRECTORIES` — authored history, withheld only for size. `docs/progress`. **Still**
  hygiene-checked, **still** a record file for the platform-state rule.
- `ELIDED_FILES` — metadata and hash, no body. `LICENSE`.

## The hazard, stated plainly

**A withheld file is invisible to the session that would notice it was missing.** It is tracked, it is
authored, it is edited by hand, and `dump.txt` does not contain it. A document in that state is one
careless slice from being regenerated without it.

So: **`docs/BUILD_PROGRESS.md` must never be delivered as though it were the whole log.** That is written
in both files' headers, in the dump's own banner, and in §18. The checkable half of it is
`ContextDumpExclusionContractTests` fact 2 — every withheld document is linked by path from a document the
dump does contain. History that leaves the dump leaves a pointer behind, or it is gone in the only sense
that matters.

**About a hundred *BUILD_PROGRESS M6 Slice N* citations are deliberately left unchanged**, across
`DOCUMENTATION_REVIEW.md` and Appendix A. They cite the log, the log still contains every slice, and the
rule for which file holds slice N is stated once in both headers. Rewriting a hundred rows to record a
filing decision would be a hundred chances to introduce an error in service of nothing (F-47).

## The LICENSE elision, and how to undo it

Flagged as a veto candidate and cleared in the session. It is worth 34 KiB of the 940 KiB, and it is the
one item here with any downside — an AGPL project whose `/source` page is a compliance surface.

It is safe rather than merely cheap **because the hash is still dumped**: a modified licence remains
detectable from the dump alone, which is the property that matters.

**To revert:** delete `"LICENSE"` from `ELIDED_FILES` in `export.sh`. Nothing else depends on it — no
gate, no document, no test.

## Files in this archive

| Path | What changed |
| --- | --- |
| `export.sh` | three named kinds of held-out path; metadata twelve fields to three; `LICENSE` elision; `file_mime` and the three host probes removed as dead |
| `docs/BUILD_PROGRESS.md` | now Slice 40 onward, plus an orientation header and the Slice 46 entry |
| `docs/progress/BUILD_PROGRESS_THROUGH_M6_SLICE_39.md` | **new** — M1 through Slice 39, byte-exact, `git add` required |
| `tests/MyRestaurant.WebApplication.Tests/Documentation/ContextDumpExclusionContractTests.cs` | **new** — four assertions, `git add` required |
| `tests/MyRestaurant.WebApplication.Tests/Documentation/TestingSectionContractTests.cs` | census floor twenty to twenty-one, and the doc sentence recording the moves |
| `scripts/check_tree.sh` | the comment that had become false, corrected and explained |
| `scripts/check_repository.sh` | `docs/progress/*` joins `RECORD_FILES` |
| `docs/TECHNICAL_SPECIFICATION.md` | v1.31; §2 layout; §16.4 paragraph; §18 two paragraphs; Appendix A F-96; changelog |
| `docs/DOCUMENTATION_REVIEW.md` | F-96 row; the whole-file-delivery paragraph now covers both halves |
| `README.md` | points at both halves of the log |
| `_CHANGES.md` | this file |

## What to run

```
bash scripts/check_tree.sh
bash scripts/check_repository.sh
dotnet test
bash export.sh > /dev/null && wc -c docs/llm/dump.txt
```

**Expect `dotnet test` to report 1166.** That is 1162 plus four, and the arithmetic has one term: the new
`ContextDumpExclusionContractTests`. If it reports anything else, check that file's `[Fact]` count first,
because it is the only thing that moved. If it reports 1166 and a *documentation* gate is red, the cause is
§16.4's census — the floor moves twenty to twenty-one in the same slice as the paragraph that raises it, so
a mistake there fails twice, once as the floor and once as a count.

**Expect `wc -c` to report somewhere near 5.5 million.** That came from running this exporter on a
reconstruction of your tree, so it will differ by whatever you have committed since — and the dump
includes the document stating the number, so it cannot be exact in principle.

## What was NOT verified

**Nothing was compiled and no test was run.** There is no .NET SDK in the authoring environment and the
package feeds are unreachable from it. Per §18 an uncompiled archive is a prediction, so **build it before
believing anything above.**

`ContextDumpExclusionContractTests` has never executed. It parses shell arrays with regular expressions,
which is exactly the kind of code whose first honest test is its first real run — although the same parse
was run here in Python against your four actual scripts and returned the right five arrays.

**Two of the four assertions were proven against real defects; two were not.** Facts 3 and 4 were run by
hand against the unfixed state and failed there, which is this project's usual sensitivity requirement.
Fact 1's non-vacuity guard is untested, and **fact 2 — the load-bearing one — has never been shown to fail
on a tree where the link was missing.**

`shellcheck` here was `shellcheck-py` rather than your distribution binary, so a version difference could
report differently on your machine. Clean at `--severity=warning` (blocking) and `--severity=style`
(advisory) on all twelve scripts.

**Only `export.sh` was actually executed.** `check_tree.sh`, `check_repository.sh` and `ci_local.sh` were
hand-simulated against their own pattern lists, not run.

**The dump this produces has never been consumed by a session.** The claim that three metadata fields
suffice rests on nine fields never having been used — an observation about past sessions, not a guarantee
about the next one. If a future slice wants the last-commit line back, that is evidence rather than a
mistake.
