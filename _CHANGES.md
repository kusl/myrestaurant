# M6 Slice 48 — the rule that caught its own documentation, and the ordering verb one register down

Extract at the repository root. Every path is repo-relative and every file is complete.

```
tar -xzf m6-slice-48-governance-fix-and-item-resequence.tar.gz
git add tests/MyRestaurant.DataAccess.Tests/Menu/MenuItemResequenceTests.cs
git status
```

**Files to DELETE: none.**

**`git add` IS required, for one new file, and it is not optional.** Every gate and CI job in this repository
enumerates with `git ls-files`. An untracked file is invisible to `scripts/check_tree.sh`, to
`scripts/check_repository.sh`, to the shell-scripts job and to `export.sh`.

**No schema change, no migration, no new read, no ADR edit, no `compose.yaml` edit, no `.slnx` edit, no
`REQUIREMENTS.md` edit, no `export.sh` edit, and no CSS.**

---

## Read this first: your last run was green on the suite and red on a gate

`total: 1180, failed: 0, succeeded: 1180`, and the prediction was 1180. §18's arithmetic had nothing to
chase. But `scripts/check_repository.sh` failed:

```
3. no document asserts a repository setting
   FAIL: docs/TECHNICAL_SPECIFICATION.md:1335 asserts a repository setting it cannot check
repository governance FAILED: 1 problem(s) in the tree. Nothing was modified.
```

That is fixed here, first, because it was red on arrival rather than caused by anything in this archive.

## Two changes, and why they ride together

Slice 47's ruling: the rule is about *indistinguishable* symptoms, not about counting changes. These two
cannot be confused.

- A **prose** defect fails as a named line number out of a grep gate. No build, no test, no suite.
- A **verb** defect fails as a named assertion in one of two files.

So a red run answers its own first question — *which gate?* — and the answer partitions the archive.

---

## 1. The gate (F-98)

Gate 3 of `scripts/check_repository.sh` is the F-42 rule made unrepeatable: a document may state **policy**,
which is true wherever it is read, and must not state **platform state**, which nothing in this repository
can verify.

Slice 46 added §16.4's paragraph on `ContextDumpExclusionContractTests`, and that paragraph had to say why
the archived build log needs the record-file exemption. It said it by **reproducing the forbidden claim
verbatim** — a short quotation, in service of an accurate point, inside the one document that is normative
about the rule being quoted.

So the specification asserted a repository setting it cannot check, and the governance gate failed for a
sentence whose subject was that very gate. **The gate was right on every count. The finding is entirely in
the prose.**

The general shape is worth more than the fix:

> A paragraph documenting why a forbidden string is permitted somewhere is the paragraph most likely to
> contain that string. A gate over authored text is tripped by its own documentation before it is tripped by
> a defect.

**The repair is to describe the claim rather than quote it.** One clause, in §16.4.

**Adding `docs/TECHNICAL_SPECIFICATION.md` to `RECORD_FILES` was considered and rejected** — and this is the
one decision in the archive you might want to veto, so here is the argument in full. It is the largest
non-record file in the tree, 449 KiB of normative prose. Widening an exemption to accommodate one clause is
exactly F-46's argument: a rule stated as a rule and enforced as a list of exceptions is enforced as a list
of exceptions. The exempt list is for files whose *job* is recording what this tree used to say; the
specification's job is saying what it does say. **To reverse:** add `"docs/TECHNICAL_SPECIFICATION.md"` to
`RECORD_FILES` in `scripts/check_repository.sh` and restore the quoted clause. Note that
`ContextDumpExclusionContractTests` asserts things about the relationship between that list and `export.sh`'s
archived set, so check that gate if you do.

**No new gate**, on F-47 and F-71: the existing one caught this on the first run after the sentence landed,
and a gate asserting that gate 3's subject does not describe itself is a monument.

---

## 2. The verb (Stage 3b) — this is the menu progress

Slice 47 shipped whole-list reordering for **headings** and named the item-level mirror as out of scope and
as the next ordering slice, in the same paragraph. This is that slice, and with it **no ordering hole remains
in the menu enhancement**.

`ResequenceMenuItemsAsync(menuSectionIdentifier, orderedMenuItemIdentifiers, actorPersonIdentifier, …)`
assigns `0…n-1` within one heading, writes one `reordered` event per item whose position actually changed,
refuses a non-permutation whole, and returns `Resequenced` / `NoChange` / `MenuItemSetChanged`.

### Three things Slice 47's design did not settle

**1. The heading is a parameter, not inferred from the list.** A position is a position *within* a section,
so the set is one heading's items. Deriving the heading from the first item's row admits a list spanning two
headings and answers it with a silent partial write; taking the whole menu asks the write to renumber the
puddings because somebody moved a drink.

**2. An unknown heading returns `MenuItemSetChanged`, through the ordinary permutation comparison, and there
is no fourth outcome.** It has no items under it, so any non-empty list fails the permutation test on the
same line every other refusal fails on — and the surface cannot act on the distinction, since an unknown
heading and a stale item set both mean *this page is stale, reload it*. There is a fact for it, because "no
rows came back" is also what an empty heading looks like, and the two agreeing should be a decision on the
record.

**3. The section row is deliberately not locked, and the argument is arithmetic.** This is the one
substantive difference from the section verb. A concurrent create or refile appends at
`MAX(display_order) + 1`, computed from the very positions this verb is holding `FOR UPDATE`: `n` rows with
maximum `m` give `m ≥ n - 1`, so the arrival lands at `m + 1 ≥ n` — strictly after every position a
resequence of those `n` rows can assign, which is *exactly* the append those verbs promise. The interleaving
is correct with no lock.

Taking none is worth more than the lock would be: this verb takes item locks and nothing else, where
`MoveMenuItemToSectionAsync` takes an item lock and then a section lock, so a section lock here would invert
that nesting and make the deadlock question live for the first time in that file. Item rows are locked
**ordered by identifier**, on Slice 47's rule, so two concurrent resequences cannot deadlock in each other's
set.

**Not tested, deliberately:** that interleaving. Two transactions racing is a property of a scheduler, and a
test passing on one ordering would be F-41's shape rather than evidence. The argument is in the code, in §7,
and above.

### The surface

Each item row's actions cell gains **Up** and **Down** beside **Manage** — the same three controls a
heading's group carries, one register down. Each is its own static-SSR form named from the *item's*
identifier. The ends of each heading's list are **disabled rather than omitted**, on the rule the group's row
follows: a control that vanishes at the edge of a list moves every other control up a row, and §16.3
scenario 16 measures where controls are.

The list exchanged is the one that heading's loop is already rendering. Nothing is computed from a position.

### F-93 needed no edit, and that is a finding rather than a relief

`.record-actions button` has been in the 375px barrier since the barrier was written, and it matched
**nothing** until this slice — every index's actions cell held a link and only a link. The item rows are the
first submit controls to render in one.

So the obligation was discharged by a selector added many slices early. The uncomfortable half is recorded:
**a selector matching nothing is indistinguishable from a selector matching everything it should**, and
nothing in the harness could have said which this was. What makes it safe rather than lucky is that
`.menu-group-actions button` was already asserting the same claim on the same page. The barrier's comment now
says all of this. `MinimumControlsMeasured` is a floor and does not move.

---

## 3. In passing

`MenuSectionResequenceTests.cs(249,39): xUnit2031` — a `Where` clause before `Assert.Single` where the
filtering overload belongs. Cleared, with a note that the analyzer is right about why: the overload names the
subject in the failure message where a pre-filtered empty sequence cannot.

---

## 4. The dump — deferred again, by name, and the number is better than you thought

**You are right that it is no longer 87%.** I reconstructed all 351 files from `dump.txt` and **SHA-256
matched 351 of 351 non-elided files**, so this archive was built on a byte-exact tree. The measurement:
**5.48 MB / 103,876 lines.**

Where the bytes are:

| Path | Size |
| --- | --- |
| `docs/TECHNICAL_SPECIFICATION.md` | 449 KiB — Appendix A 141 KiB, changelog 67 KiB |
| `docs/DOCUMENTATION_REVIEW.md` | 225 KiB |
| `docs/BUILD_PROGRESS.md` | 134 KiB |
| `tests/` | 1,973 KiB |
| `src/` | 1,890 KiB |
| `scripts/`, `.github/`, root | 371 KiB |

**`export.sh` is not in this archive, because nothing in it needed to change** — unchanged from Slice 47's
statement of this. Every remaining cut is a *split of a history register*, and `docs/progress/` is already
withheld by path, so a split needs no exporter edit. Deferred here for the concrete reason that **this slice
already edits two of those four-gate-read registers** (Appendix A gains two rows, the ledger gains one), and
a red gate beside a relocation of the same files would have two candidate causes.

Specified for Slice 49, unchanged:

1. **`docs/DOCUMENTATION_REVIEW.md` splits at a finding boundary**, older tranche to `docs/progress/`.
   **About 200 KiB.** Same operation Slice 46 performed on the build log, so the pattern and its hazards are
   already written down.
2. **Appendix A moves whole** to `docs/progress/`, the section becoming a pointer paragraph so the roughly
   one hundred *Appendix A* citations stay valid. **139 KiB.**

**Together about 6%.** One thing I want to put in front of you rather than decide, because it is worth more
than the split and it is a ruling reversal: the specification describes `DOCUMENTATION_REVIEW.md` as *"the
long-form twin of Appendix A."* If that is literally true, then **366 KiB of the dump is one register written
twice**, and retiring a twin beats relocating both — a consolidation does not grow back, where a split does.
I have not touched it. Say the word and Slice 49 is that instead.

The only export-side lever left is the per-file framing: about 430 bytes × 351 files ≈ 151 KiB, reducible to
roughly 50 KiB by collapsing the metadata block to one line. That costs you the block my reconstruction
verifies against, for 1.8%. I would decline that trade, and I am recording the number so the decision is
yours rather than absent.

---

## Test count arithmetic

Uncompiled, per §18. **1180 → 1190.**

| Where | Assertions |
| --- | --- |
| `MenuItemResequenceTests` (new) | 8 |
| `MenuWiringTests` | 2 |
| **Total added** | **10** |

Any deviation from 1190 is the first thing to investigate.

---

## Files in this archive

| Path | What changed |
| --- | --- |
| `src/MyRestaurant.DataAccess/Menu/MenuAdministration.cs` | the outcome enum, the section-scoped locking read, the verb, the permutation helper, the position row |
| `src/MyRestaurant.WebApplication/Menu/MenuWorkflow.cs` | the verb, one conditional publish |
| `src/MyRestaurant.WebApplication/Components/Pages/Administration/AdministrationMenu.razor` | Up and Down per item row, the handler, two end-of-list helpers, the form-name helper, three flash sentences, header comment, lede |
| `tests/MyRestaurant.DataAccess.Tests/Menu/MenuItemResequenceTests.cs` | **new** — eight assertions, `git add` required |
| `tests/MyRestaurant.DataAccess.Tests/Menu/MenuSectionResequenceTests.cs` | the xUnit2031 fix |
| `tests/MyRestaurant.WebApplication.Tests/Menu/MenuWiringTests.cs` | the fake learns the verb; two facts |
| `tests/MyRestaurant.WebApplication.Tests/Documentation/TestingSectionContractTests.cs` | census floor 23 to 24 |
| `tests/MyRestaurant.EndToEnd.Tests/Harness/HandheldReach.cs` | comment only — records that `.record-actions button` has become live |
| `docs/TECHNICAL_SPECIFICATION.md` | v1.33; **the F-98 fix at §16.4**; §7 and §11.4; one §16.4 paragraph plus one count moved; Appendix A F-98 and the Stage 3b row; changelog |
| `docs/DOCUMENTATION_REVIEW.md` | F-98 row; status line |
| `docs/BUILD_PROGRESS.md` | the Slice 48 entry |
| `docs/MENU_AND_HANDHELD_PLAN.md` | Stage 3a's out-of-scope bullet points forward; Stage 3b struck through with what landed and what is still open |
| `_CHANGES.md` | this file |

---

## What to run, in this order

```
bash scripts/check_repository.sh --offline
dotnet build
dotnet test
bash scripts/ci_local.sh --with-all
```

The governance gate first, because it is the one that was red and it needs nothing built. If it still fails,
nothing else in this archive is implicated. Then the build, because F-71's habit is that an archive which has
not been compiled is a prediction.

---

## What was NOT verified

**Nothing was compiled or run.** What *was* done: a string-aware brace and bracket balance over every edited
C# file; a Razor tag-tree walk over `AdministrationMenu.razor` with generic type arguments and code islands
excluded; CS4007 and CS1620 scans; every constructor and helper signature the new test file calls checked
against the tree; a `SpecificationVersionTests` simulation; a `TestingSectionContractTests` simulation that
resolved every cited class and compared every stated count against the files; a `MarkdownTableContractTests`
simulation over every tracked Markdown file; and byte hygiene.

**The census delta rather than the census.** `MinimumCountedClasses` moves 23 → 24 on the arithmetic that
exactly one §16.4 paragraph citing a class with a count was added. If that arithmetic is wrong the floor is
wrong by the same amount.

**No browser saw the new controls.** §16.3's scenario 17 is not extended, so nothing asserts end to end that
an item moves within its heading. That is a real gap and the obvious next end-to-end step.

**The lock-free interleaving is argued, not tested.**

**Whether `.record-actions button` renders at the touch-target height on a handset.** The barrier will now
measure it, which is the point; nothing here can observe the answer in advance.
