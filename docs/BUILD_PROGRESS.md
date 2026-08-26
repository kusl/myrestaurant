# Build progress

**This file is an index. The narrative is archived.** One row per slice: what landed and which findings it closed. The full account of every slice — the rulings inside it, what was and was not verified, the test-count arithmetic, and what was left open — is in the two archives below. Both are withheld from the context dump by `export.sh` and both are still tracked and still hygiene-checked.

- M1 through M6 Slice 39, with the original *How this was produced*, *Staged plan* and *Known caveats* preamble: [`docs/progress/BUILD_PROGRESS_THROUGH_M6_SLICE_39.md`](progress/BUILD_PROGRESS_THROUGH_M6_SLICE_39.md)
- M6 Slice 40 through Slice 65: [`docs/progress/BUILD_PROGRESS_THROUGH_M6_SLICE_65.md`](progress/BUILD_PROGRESS_THROUGH_M6_SLICE_65.md)

**A citation resolves by slice number.** `BUILD_PROGRESS M6 Slice 30` means slice 30 wherever it lives; the row below says which archive to open. New slices are appended to the table here and, when the narrative is worth keeping, to a new archive rather than to this file.

**Where the standing methodology lives.** The rules a slice is expected to follow — atomic documentation, computed subjects, sensitive gates, one change per green run — are in `docs/TECHNICAL_SPECIFICATION.md` §18 and in the standing-rules table at the top of `docs/DOCUMENTATION_REVIEW.md`. They are not restated per slice.

## Slice index — M6 Slice 40 onward

| Slice | What landed | Findings closed |
|---|---|---|
| 40 | the heading every item has, and a vocabulary nobody could check | F-80 |
| 41 | the section editor, a reserved word two files were named after (F-81), and the gate that never ran (F-82) | F-81, F-82 |
| 42 | seven defects behind one build failure, and a ruling reversed | F-83, F-87, F-86, F-88, F-89 |
| 43 | the last verb gets its surface, and three numbers nothing could check | F-92 |
| 44 | the index becomes the menu, and a barrier that measures by list | F-93, F-94 |
| 45 | the tie-break that was a coin flip | F-95 |
| 46 | the dump that had become mostly history | — |
| 47 | the runner that was a default, and the verb that could finally be written | — |
| 48 | the rule that caught its own documentation, and the ordering verb one register down | — |
| 49 | the arithmetic a test got wrong about a tree that was right, and the sentence a guest could finally read | — |
| 50 | the last surface that read the menu flat, and the rule that had nowhere to be tested | — |
| 51 | the picture a menu could not carry, and three columns a plan had already got wrong | — |
| 52 | the transport question that had a third answer, and a count of seven that said six | — |
| 53 | the picture a guest can finally see, and a plan that argued for it wrongly | — |
| 54 | the transition that broke the build, and the history a picture had never had | — |
| 55 | the 500 an operator found, and the picture a phone can finally upload | — |
| 56 | the bytes decide the format, and a build that was red only where it mattered | F-108, F-110 |
| 57 | the first stage of the menu that is not about a dish's own columns | F-111 |
| 58 | the half of the like a guest can see, and a number in its third generation | F-112 |
| 59 | likes: §11.4's count, and the end of the menu enhancement's open list | — |
| 60 | likes: a dish that is off tonight, and a read that had no reader | — |
| 61 | the controls the barrier had been measuring, and a comment that described somebody else | — |
| 62 | the wall that was documented for eleven slices, and the refusal the endpoint now decides | — |
| 63 | a gate that could not tell a use from a mention, and the menu plan's rendering rule stops being a sentence | F-116, F-117 |
| 64 | the surface the contract was written for, measured last, and the control no gate could reach | F-118 |
| 65 | the picture the barrier had never seen, and the element that is present with no area at all | — |

## Slice 66 — the comments, the four documents, and the dump that had outgrown its reader

| Slice | What landed | Findings closed |
|---|---|---|
| 66 | Every comment removed from authored `.cs`, `.razor`, `.sql`, `.css`, `.js` and `.sh`; the four largest documents archived to `docs/progress/` and rewritten as registers; `SourceCommentContractTests` added; `DocumentationCommentContractTests` deleted | F-119, F-120 |

**What is in this slice.** 2,087,175 bytes of comment removed across 334 files — 42.1% of all authored code. 1,655,921 bytes of documentation archived verbatim and replaced by 230-odd KB of register. `docs/TECHNICAL_SPECIFICATION.md` goes to v1.51, with §7, §11, §14, §16, §18, Appendix A and the changelog rewritten and §8's DDL, §13's variable list and §16.3's scenarios kept verbatim because gates read them. No behaviour changed and no production code path changed: the only edits under `src/` are removals of comment bytes.

**Two findings, and the order they were found in.** **F-120** is the measurement nobody had taken: comments were 42% of authored source and four documents were 21% of the tree, and the consequence was a context dump at 96% of the budget a session has to read it in. **F-119** was found while proving F-120's change safe rather than while looking for it — `ConfigurationSurfaceTests` terminated its scan of `RestaurantOptions.cs` on the string `/// <summary>Returns a human-readable reason`, so the boundary of the configuration surface was a sentence. Stripping comments would have moved that boundary silently. It now ends at the declaration that comment described.

**The gate that was deleted rather than weakened.** `DocumentationCommentContractTests` asserted that no documentation comment holds two `<summary>` elements, over a floor of 1,500 `///` blocks. With no `///` blocks in the tree the floor cannot be met and the assertion cannot fail — which is the vacuous gate F-41 exists to forbid. §16.4 sat at exactly thirty-seven counted classes, so `SourceCommentContractTests` takes the slot; the section now carries forty-four.

**What was verified.** Every stripper was proven against a real checker rather than against its own output: `bash -n` on all eleven scripts, `node --check` on all five JavaScript files, and for C# a comparison of the complete string-literal stream before and after, which is byte-identical in all 258 files. Bracket balance, idempotence, and the survival of every line containing no comment marker were checked per file. Every gate floor was recomputed on the stripped tree and compared against the original. Every gate's string literals were checked for disappearance from the files it reads, with test sources excluded from the corpus so a gate's own copy of its marker could not mask a vanished target — which is how F-119 was found.

**What was NOT verified.** Nothing was compiled: no .NET SDK was available in the authoring environment, so `dotnet build` and `dotnet test` have not run, the predicted test count is arithmetic rather than an observation, and the two new assertions in `SourceCommentContractTests` have never executed. No browser ran, so the §16.3 scenarios are unverified. No container engine ran, so the integration suite and the restore drill are unverified. The Razor and shell strippers were proven by lexical invariants and `bash -n`; a Razor file's *rendered* output was not compared.

**Test count.** 1302 before. `DocumentationCommentContractTests` removes 2; `SourceCommentContractTests` adds 2. **Predicted: 1302, unchanged.** A deviation from that number is the first thing to investigate, and the likeliest cause is an analyzer diagnostic promoted to an error by `ContinuousIntegrationBuild=true` rather than a failed assertion.

**Still open.** `docs/OPERATIONS.md` (57 KB), `README.md` (38 KB), `docs/REQUIREMENTS.md` (35 KB) and the fifteen ADRs (76 KB) are unchanged. Together they are under 3% of the tree, and each is either a live runbook, the requirement this specification implements, or the rationale record — so condensing them buys little and risks the documents an operator actually follows. They are a separate slice if they are wanted at all.
