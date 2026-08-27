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

## Slice 67 — the reading that was two instants

| Slice | What landed | Findings closed |
|---|---|---|
| 67 | `KitchenJourneys.ReadBoardAsync` and `TableOrderJourneys.ReadBasketAsync` rewritten to one `EvaluateAsync` each; `HarnessSnapshotContractTests` added over a computed subject set; the changelog's stale entry count deleted | F-121 |

**What is in this slice.** A flake was reported against §16.3 scenario 8 — `Expected: 2, Actual: 1` on the unseen-alert count — and could not be reproduced. It is a torn read in the harness rather than a defect in the product. `KitchenBoard.razor` renders `data-unseen-alerts` and `data-unseen-reminders` from one `KitchenAlertState` on one element; the reader took them in two round trips, so a reminder arriving in between produced one alert beside one reminder. `WaitForBoardAsync`'s predicate asks only about reminders, so the torn reading satisfied it and the assertion on the next line failed against a board that had been right the whole time. `TableOrderJourneys.ReadBasketAsync` had the same shape over three `CountAsync` calls and is polled by a predicate on a surface that is actively changing, so it is repaired in the same slice: the two fail in ways nothing could confuse — one names a kitchen board and one names a basket — and the gate's subject set covers both, so repairing one would leave it red.

**The rule and its subject.** `HarnessSnapshotContractTests` computes the composites that a `Func<T, bool>` is evaluated against and requires every method returning one to contain at most one browser read. Twenty harness files, three subjects, six readers. Collection-valued predicates are excluded and the exclusion is recorded as a residual rather than left implicit.

**Test count arithmetic.** 1302 + 2 = **1304**. Two `[Fact]` in one new class in `MyRestaurant.WebApplication.Tests`; no test was deleted and no existing count moved. §16.4's counted-class census goes 37 → 38, over a floor of 37.

**Sensitivity.** The scan is proven against three composed fixtures — a torn reading is reported, a whole one is not, and one that no predicate is ever asked about is not — and against the pre-repair tree, where it reports `ReadBoardAsync` at five reads and `ReadBasketAsync` at three. The floor on total body bytes is the guard on the brace-matching extraction: a truncated body contains no read and would otherwise pass.

**What was NOT verified.** Nothing was compiled. No test was run. No browser was opened, so the two rewritten JavaScript readers have not executed against a real page — the claim that they are behaviour-preserving rests on reading: `innerText` for `innerText`, the same relative selectors, and the same `.Trim()` and `.TrimEnd('×')` applied in C# afterwards rather than in the script. The new gate was emulated mechanically against the edited tree rather than executed. The flake itself cannot be shown fixed by a passing run, because it did not fail on demand before.

## Slice 68 — the stage two documents said could not start

| Slice | What landed | Findings closed |
|---|---|---|
| 68 | `0009_menu_item_comments.sql` and `Menu/MenuItemComments.cs` — Stage 6c, the comment schema and its data access; `MenuItemCommentTests` over a real database; §17's discharged promise and §19's *not startable* clause deleted | F-122 |

**What is in this slice.** Stage 6 is the last stage `docs/MENU_AND_HANDHELD_PLAN.md` carried and its two prerequisites landed in Slices 62 and 63. This is 6c: one event table, one fold, one directory and one write service with two verbs. No surface, no endpoint, no rate-limit policy — those are 6d and 6e, and nothing a guest can reach exists yet.

**Three of the four open questions are now ruled**, and each ruling is in §7 with its reason. A comment is filed against the **item and never an order line**, because an opinion needs no purchase — the argument the like already settled — and because an order line's log carries §6.7's correction rules a comment has no business inheriting. A comment is **staff-facing and a guest sees only their own**, which is §11.4's like-count ruling applied to text and is what makes the moderation question not arise rather than answered: nothing a guest writes is rendered to another guest, so there is nobody to moderate on behalf of. **One standing comment per person per dish**, editing is resubmission, withdrawal is an event, and every version is kept. Staff replies are the fourth question and are **not built and named as not built**.

**The finding, and how it was found.** **F-122** was found by reading §19 to decide what to build. §19's M7 bullet said Stage 6 was *not startable* until §17's rate-limit ruling was revisited — the ruling was revisited in Slice 62. §17 was worse: it carried the corrected account of the limiter and then, four paragraphs below it, the pre-fix account in future tense, promising §13 gains variables §13 has had since v1.47. And §17 said the habit F-115 produced *is stated in §18* — §18 did not state it. All three are deletions or replacements on **F-114**'s ruling, and §18 now carries the habit: an accepted risk that names its own remedy is a deferral, and when the remedy lands the promise is deleted rather than left beneath the landing.

**Two changes in one slice, and the distinguishable-failure rule governs both.** The schema fails as an integration assertion naming `menu_item_comment_event` against a real PostgreSQL; F-122 is a documentation deletion with no gate at all and therefore cannot fail. They are also not separable under the atomic-documentation rule — §19's stale clause is a claim *about this stage*, so the slice that starts the stage is the slice that owes the edit.

**Test count arithmetic.** 1304 + 13 + 1 + 2 = **1320**. Thirteen `[Fact]` in the new `MenuItemCommentTests` in `MyRestaurant.DataAccess.Tests`; one `[Fact]` added to `MenuWiringTests` (29 → 30); two rows added to `SchemaMigrationRunnerTests.KeyRelations`, which is `[Theory]` `[MemberData]` and expands to two further test cases without changing that file's attribute count, so §16.4's seven for that class does not move. No test was deleted. §16.4's counted-class census goes 45 → 46 over a floor of 37.

**Sensitivity — reasoned, not run, and corrected here on F-124.** This paragraph originally described four mutations of `0009` as executed. Nothing in this slice was executed: the paragraph below says so, and a planted-defect proof presupposes a green baseline this slice did not have. What was actually done is reasoning about what each assertion would catch — vocabulary widened to admit `loved`, the payload biconditional weakened to a single implication, the blank guard dropped, the cap constraint renamed, `ReadDeclaredBodyCapAsync` hardcoded against a moved cap, the writer's `NoChange` branch removed, the author read taking `display_name` bare. The reasoning was sound for six of the seven and wrong about the schema fact itself, which was red on a correct schema for a reason no amount of reading the DDL produces (**F-123**, Slice 69).

**Two things about the schema that are rulings rather than shape.** The fold has **no `WHERE` clause**, deliberately: `DISTINCT ON` has to see the withdrawal to know it is the latest row, so filtering inside the view would return the submission *before* a withdrawal and report a withdrawn comment as standing. The caller filters on `body IS NOT NULL`, which the payload biconditional makes an exact test of *the last event was a submission*. And there are **four named constraints over one nullable column** rather than one conjunction, because the writer recognises the cap refusal by `conname` and a conjunction would report the whole expression — over-cap and blank would be indistinguishable to the code that has to tell a guest which one happened.

**What was NOT verified.** Nothing was compiled: no .NET SDK was available in the authoring environment, so `dotnet build` and `dotnet test` have not run and 1320 is arithmetic rather than an observation. No container engine ran, so `MenuItemCommentTests` has never executed against a real PostgreSQL — every claim about what the four constraints refuse, what `pg_get_constraintdef` returns for the cap, and how `COALESCE(NULLIF(btrim(display_name), ''), username)` types against a `citext` username rests on reading the DDL and on the identical expression already working in `DapperMenuSectionEventLog`. The migration has not been applied, so its idempotence and its interaction with `TRUNCATE ... CASCADE` in `OrderTestWorld` are unverified. No browser ran; no §16.3 scenario touches comments, because no surface exists to touch.

**Still open.** Stage 6d (the guest's control in §11.1's detail panel) and Stage 6e (the staff read). Neither has a surface ruling written yet. The fourth open question — staff replies — stays open and is recorded in the plan as the row that would have to be reopened first if any later stage shows one guest another guest's words.

## Slice 69 — the assertion that decided a sort order, and the proof that was written rather than run

| Slice | What landed | Findings closed |
|---|---|---|
| 69 | `MenuItemCommentTests` repaired — one CHECK per probe, proven by a control row, with the constraint inventory read out of the catalogue; `ContainerLoggingContractTests` and the two fixtures it holds; §18 gains the two rules the failure produced | F-123, F-124, F-125 |

**What is in this slice.** The first real execution of Slice 68's work, on a machine with a container engine, and the three things it found. No production code changes: `0009_menu_item_comments.sql` and `Menu/MenuItemComments.cs` are untouched and the schema they declare is correct. Stage 6d and 6e are still not started.

**F-123, and how it was found.** By running the suite. `TheSchemaRefusesEveryForbiddenShapeOfAnEventRow` offered `('loved', 'Anything')` to the vocabulary CHECK and got `menu_item_comment_event_body_payload` back, because that row breaks the payload biconditional too — `('loved' = 'submitted') = ('Anything' IS NOT NULL)` is `false = true` — and PostgreSQL sorts a relation's CHECKs by name and reports the first one that refuses. `body_payload` sorts before `type_vocabulary`. The assertion was deciding a fact about `qsort` and reporting it as a fact about the schema.

**The repair is not to change the expected name.** Naming `body_payload` there would pass, and would pass equally against a migration with no vocabulary CHECK at all — the probe would have stopped being about its constraint. Each probe row is instead constructed to break exactly one CHECK, and that property is **proven rather than asserted**: every probe is paired with a control row differing from it in one attribute, inserted in a transaction and rolled back. If a probe were over-determined, its control would be refused too and the fact says which one. The four names are read out of `pg_constraint` and compared against the set the probes cover, so a fifth CHECK cannot silently shadow one of them, and the cap gets a schema-level probe — the fact has been called *every forbidden shape* since it was written and covered three of four.

**Verified against a real PostgreSQL 16 in the authoring environment**, which is what makes this slice different from the last one. All five probes report their own constraint by name; all four controls insert; the row count is zero afterwards. The old probe row reproduces the reported failure exactly.

**F-124 is the reason F-123 shipped.** Slice 68's sensitivity paragraph described four mutation runs, and Slice 68's *What was NOT verified* paragraph, six paragraphs below it, says no container engine ran and that the class had never executed. A mutation proof presupposes a green baseline; this baseline was red. The rule F-41 states was satisfied on paper. §18 now carries the distinction — a proof is an execution, and a slice that cannot execute names the defect the assertion is *reasoned* to catch and says it was not run — and Slice 68's paragraph above is corrected in place rather than annotated (**F-114**).

**F-125 is why the failure was hard to find.** Testcontainers logs each container created, each `pg_isready` probe and each container deleted at Information, which across the integration classes is several hundred lines carrying the one failing assertion somewhere inside. Both fixtures now assign `TestcontainersSettings.Logger = NullLogger.Instance` as the first statement of the `try` that already turns a container failure into a skip, so the assignment cannot itself crash an assembly, and it runs after `ContainerEngineDiscovery` has set `DOCKER_HOST` — touching `TestcontainersSettings` snapshots the endpoint, which is why the order is asserted and not merely written. Nothing is lost that anybody reads: a container that will not start is diagnosed by `DescribeFailure`, which names the image, the short-name resolution rule and the socket to activate.

**Three findings in one slice, and the distinguishable-failure rule governs all three.** F-123 fails as one integration assertion in `MyRestaurant.DataAccess.Tests` naming a constraint. F-125 fails as two source-scan assertions in `MyRestaurant.WebApplication.Tests` naming a file. F-124 is a documentation correction with no gate and cannot fail. F-124 is not separable from F-123 under the atomic-documentation rule — it is a claim *about the assertion F-123 repairs*, in the slice narrative that shipped it.

**Test count arithmetic.** 1320 + 2 = **1322**. Two `[Fact]` in the new `ContainerLoggingContractTests` in `MyRestaurant.WebApplication.Tests`. `MenuItemCommentTests` still holds thirteen: the fifth probe and the catalogue comparison are assertions inside an existing fact, so §16.4's thirteen for that class does not move. No test was deleted. §16.4's counted-class census goes 46 → 47 over a floor of 37.

**Sensitivity — run, this time, and the two halves are labelled.** `ContainerLoggingContractTests` was emulated mechanically against the edited tree and against two planted defects: with the assignment deleted from `PostgreSqlFixture`, the first fact reports that file by name; with the assignment moved below `new PostgreSqlBuilder(`, the first fact passes and the second reports it, which is what makes them two facts. The repaired schema fact was executed against a real PostgreSQL carrying the exact DDL of `0009`: five probes, five distinct constraint names, four controls accepted, zero rows left. The pre-repair probe row was run in the same session and reports `body_payload`, which is the failure this slice closes.

**What was NOT verified.** Nothing was compiled: no .NET SDK was available in the authoring environment, so `dotnet build` and `dotnet test` have not run and 1322 is arithmetic rather than an observation. The PostgreSQL used to verify the schema fact was 16, not the 17 the fixture starts; CHECK constraints are sorted by name in both and have been since the behaviour existed, but the run was not against the pinned image. `ContainerLoggingContractTests` was emulated in Python against the same file contents rather than executed as C#. The claim that `NullLogger.Instance` silences Testcontainers is read from the library's use of `TestcontainersSettings.Logger` and was not observed.

**Still open.** Stage 6d (the guest's control in §11.1's detail panel) and Stage 6e (the staff read), both unchanged by this slice. The fourth Stage 6 question — staff replies — stays open.
