# M6 Slice 42 — seven defects behind one build failure, and a ruling reversed

Extract at the repository root. Every path is repo-relative and every file is complete.

```
tar -xzf m6-slice-42-defects.tar.gz
git status
```

**Files to DELETE: none.**

**`git add` is NOT required.** Every file in this archive already exists in the tree and is tracked. No
file is new, no directory is new, and `scripts/check_tree.sh` walks `git ls-files`, so there is nothing
here it cannot already see.

**No schema change.** No migration is added and `0005` is untouched. No new package, no `compose.yaml`
edit, no `.slnx` edit, no ADR edit, no `REQUIREMENTS.md` edit.

**Nothing under `src/` changes behaviour.** The two files touched there are comments that asserted the
opposite of the `switch` arms twenty lines beneath them.

---

## Read this first

The five build errors you reported were **two** defects. Behind them sat fourteen failing integration and
end-to-end facts that could not have run while the build was red — so this archive is red-to-green on seven
findings, five of which nothing had reported yet.

Six of the seven are one mechanism, and it is worth more than any individual repair:

> **A schema widened by a migration reaches the test arrangement last**, because arrangement is the code
> nobody is looking at while implementing the thing being arranged for.

`0004` and `0005` added three columns to `menu_item`, three payload columns to `menu_item_event`, three
members to `MenuItemSummary`, and three event types. All of that landed correctly in `src/`. What did not
land was the INSERT the test world writes events with, a positional stand-in in the unit suite, six
assertions counting a create, and one harness read.

**The seventh is the one to read.** `MenuEventVocabularyContractTests` — the gate that repairs F-80 — named
`EventTypeVocabulary`. There is no such type; it is `EventTypeCatalogue`, and has been since the explorer
was written. The wrong name came from **F-80's own ledger rows**, in `TECHNICAL_SPECIFICATION.md` and in
`DOCUMENTATION_REVIEW.md`, plus Slice 40's delivery note — five occurrences, three documents, all written
in the same slice, all agreeing with each other and none with the tree. The test was written from the
ledger rather than from the file the ledger describes, which is how a name gets copied wrong five times:
the right spelling and the wrong spelling were never in one view.

So for one slice the menu vocabulary was **correct in the source and guarded by nothing** — the exact state
F-80 exists to prevent, reached through the fix for it.

---

## The seven findings

| ID | What was wrong | Where |
|---|---|---|
| **F-83** | The F-80 gate named a class that has never existed, taking the name from F-80's own ledger rows | `MenuEventVocabularyContractTests.cs` + 3 documents |
| **F-84** | `MenuItemSummary` grew seven members to ten; a positional stand-in stayed at seven | `OrderStagingTests.cs` |
| **F-85** | A two-character username against a three-character CHECK took six facts down in `InitializeAsync` | `MenuSectionEventLogTests.cs` |
| **F-86** | The test world's event INSERT named two payload columns of five, so three of eight types were unwritable | `OrderTestWorld.cs`, `EventExplorerReadsTests.cs` |
| **F-87** | Six assertions described a create one row smaller than `0005` performs; two read the wrong row entirely | `MenuAdministrationTests.cs` |
| **F-88** | A harness read a heading with `InnerText` where `app.css` uppercases it, comparing a rendering to a name | `TableOrderJourneys.cs` |
| **F-89** | A census kept in prose "and moved by habit" was left behind by both moves that followed | `TestingSectionContractTests.cs`, spec §16.4 |

**No gate is added for any of the seven**, and that is a ruling rather than economy. The compiler refused
two of them and PostgreSQL's CHECK constraints refused two more, loudly, on the first run — a test
re-asserting what CSC or a CHECK already rejects is a monument (F-47, F-71).

---

## Two things worth your veto

**F-89 reverses F-73's ruling.** F-73 found a census count in prose that was stale on arrival and ruled it
should be *kept*, because it was the argument for `MinimumCountedClasses`, with the habit of moving it
added beside it. Three slices later the floor had moved twice and neither prose copy had moved once —
§16.4 said *ten* while the floor said *eighteen*, and the gate's own summary said *sixteen* in a sentence
whose next clause said *eighteen*. I deleted both prose copies rather than correcting them, leaving the
enforced floor as the only place the census is written. **To revert**: restore the two sentences with the
number nineteen in them, and restore F-73's wording in the class summary.

**`CreatedEventScalarAsync` is a new private helper in `MenuAdministrationTests`.** It reads a payload by
`event_type = 'created'` instead of by recency, because after `0005` the newest event of a create is the
section one and its payload columns are all null by CHECK. That query already existed inline in
`CreateWritesTheItemAndItsCreatedEventTogether`; this hoists it so a third caller need not rediscover why.
**To revert**: inline it at both call sites. It adds no `[Fact]`, so §16.4's count of 26 is unaffected
either way.

---

## What I verified, without an SDK

- **Tree reconstruction**: 347 of 348 files byte-identical to their recorded SHA-256. The exception is
  `export.sh`, which contains the dump's own `# FILE:` banner as literal text and cannot be round-tripped
  by a parser using that banner as a delimiter. It is **not in this archive** and was not touched.
- **`TestingSectionContractTests`**, ported and run before and after. Before: 18 counted classes, 0
  disagreements — the tree as delivered was already correct here, which is F-82's repair holding. After:
  **19 counted, 0 disagreements, 0 ambiguous, 0 uncited.**
- **`MarkdownTableContractTests`**, ported, fence-aware and escaped-pipe-aware, over every `.md` in the
  repository: **60 table runs, 0 findings**, including the fourteen new four-cell ledger rows.
- **`SpecificationVersionTests`**, ported: header 1.27, newest entry 1.27, 28 entries descending.
- **The vocabulary gate's extraction by hand** against `0005`: the regex matches across the newline, yields
  eight quoted words, set-equal to `EventTypeCatalogue.MenuEventTypes`. The gate passes *once it compiles*,
  which had never been established.
- **Byte hygiene** on every changed file: no CR, exactly one final newline, no whitespace-only line.

## What I did not verify

**Nothing compiled and no test ran.** The likeliest site of a complaint, named rather than left to be
found: the three new optional parameters on `AddMenuItemEventAsync` sit **after** a `CancellationToken`,
which is legal and which no other method in `OrderTestWorld` does. Every existing call site passes the
token positionally and stops, so they bind correctly.

**No database confirmed the six repaired counts.** They are read off
`DapperMenuAdministration.CreateMenuItemAsync`, and corroborated by two facts in the same file that Slice
40 *did* update and that consequently passed — one asserts 2 events for a create, the other 5 for a create
plus three verbs. Both are only consistent with a two-row create.

**`TextContentAsync` returns `string?`** where `InnerTextAsync` returned `string`. The null-coalesce is
present.

---

## Test count

Predicted: **1149** — unchanged from Slice 41, because this slice adds no `[Fact]` and removes none. Every
repair changes what an existing assertion expects, or how it reads what it expects. §16.3 stays at 17.

**Slice 41's 1149 never met a run**, and neither did Slice 40's 1136. The last observed count is 1124, from
Slice 39. Per §18, if this run returns anything other than 1149, that difference is the first thing to
chase — and this is the first slice in three where the check is expected to be possible at all.

---

## On the menu

This slice deliberately adds nothing to Stage 3. What it does is give the remaining work a green tree, and
it is worth noting that everything left on that list touches the arrangement that kept getting missed:
`MoveMenuItemToSectionAsync` will write a second `section_changed`, so any fact counting an item's events
must know a create already contributes one (F-87) — and `OrderTestWorld` could not write that event type at
all until this archive (F-86).
