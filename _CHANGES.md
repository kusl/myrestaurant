# M6 Slice 57 — likes: the schema and the data access, and a paragraph that contradicted the DDL above it

Extract at the repository root. Every file in the archive is a **complete file**; nothing is a patch, and
there are no scripts to run.

```
tar -xzf m6-slice-57-likes-schema-and-data-access.tar.gz
```

## Files to delete

**None.** Nothing is removed by this slice.

## New files — these must be `git add`ed

Several gates in this tree enumerate their subject with `git ls-files`, so an untracked new file is a file
they do not see:

```
git add src/MyRestaurant.DataAccess/Migrations/0008_menu_item_reactions.sql
git add src/MyRestaurant.DataAccess/Menu/MenuItemReactions.cs
git add tests/MyRestaurant.DataAccess.Tests/Menu/MenuItemReactionTests.cs
```

Those are the only three.

## What is in the archive

| Path | Why |
| --- | --- |
| `src/MyRestaurant.DataAccess/Migrations/0008_menu_item_reactions.sql` | **new** — `menu_item_reaction_event`, its index, and the `menu_item_reaction_current` fold. Touches nothing existing |
| `src/MyRestaurant.DataAccess/Menu/MenuItemReactions.cs` | **new** — `IMenuItemReactionDirectory` (two reads) and `IMenuItemReactions` (the press) |
| `tests/MyRestaurant.DataAccess.Tests/Menu/MenuItemReactionTests.cs` | **new** — nine integration facts against a real PostgreSQL |
| `src/MyRestaurant.WebApplication/Orders/OrdersServiceCollectionExtensions.cs` | Registers both, and states why the write is deliberately **not** behind `IMenuWorkflow` |
| `tests/MyRestaurant.WebApplication.Tests/Menu/MenuWiringTests.cs` | The registration fact, and the standing fact narrowed to *every write that changes the menu* |
| `tests/MyRestaurant.WebApplication.Tests/Events/MenuEventVocabularyContractTests.cs` | **F-111**: a fourth fact holding §8.2's quoted DDL to the migrations, both directions |
| `tests/MyRestaurant.DataAccess.Tests/SchemaMigrationRunnerTests.cs` | Two `KeyRelations` rows — a table **and** a view — and the paragraph saying why `0008` needs both |
| `docs/TECHNICAL_SPECIFICATION.md` | **v1.42**: §7's five reaction paragraphs, §8.2's DDL, §8.3's fold, the F-111 repair, §16.4's new class and two moved counts, Appendix A, changelog |
| `docs/DOCUMENTATION_REVIEW.md` | The F-111 row, and a status line about which copy of a fact gets maintained |
| `docs/MENU_AND_HANDHELD_PLAN.md` | Stage 5 becomes Stage 5a (landed) with both of its rulings made, and Stage 5b named; Stage 6 inherits one of the rulings |
| `docs/BUILD_PROGRESS.md` | The Slice 57 narrative, shipped whole |
| `_CHANGES.md` | This file |

## Test count

Baseline **1260**, verified from your terminal log rather than assumed. Slice 56 predicted 1260 and the run
returned 1260 — two exact predictions in a row, which is a first for this project.

| Where | Facts | Running |
| --- | --- | --- |
| Baseline (verified) | — | 1260 |
| `MenuItemReactionTests` (new) | +9 | 1269 |
| `MenuWiringTests` (28 to 29) | +1 | 1270 |
| `MenuEventVocabularyContractTests` (3 to 4) | +1 | 1271 |
| `SchemaMigrationRunnerTests.KeyRelations` (15 to 17 rows) | +2 | 1273 |

**Predicted: 1273.** Recomputed from the edited tree rather than carried: 921 `[Fact]` methods + 329
`[InlineData]` rows + 17 + 6 `MemberData` rows. The last two come from theory **rows**, so §16.4's count for
`SchemaMigrationRunnerTests` stays at 7 — that number counts methods. The §16.3 suite stays at **twenty**,
because Stage 5a builds no surface for a browser to visit. Anything other than 1273 is the first thing to
investigate.

*(I told you 1272 in the previous message. That estimate omitted one increment; the arithmetic above is the
corrected one, stated plainly rather than quietly fixed, since §18's whole point is that a number which
moves without explanation is the thing to chase.)*

## Both of Stage 5's rulings, made rather than carried

The plan had said since it was written that likes needed two rulings before shipping. Neither took an hour,
which is the argument for deciding at the moment it is cheap.

**Who sees the count: staff.** A count of 3 on a menu of sixty items is noise that makes a restaurant look
empty. So *which of these is popular* is §11.4's question and *which of these do I like* is §11.1's — two
reads over one fold. The guest gets their own press back, or the control is an affordance with no feedback;
what they do not get is everybody else's.

**Whether a like requires having ordered the item: no**, and this is the one Stage 6 inherits.
`order_current_line` records what somebody **ordered**, not what they ate, and a table shares — so the
requirement refuses the case it most wants to admit (*I ate my partner's dessert and it was the best thing
on the menu*) while admitting the one it wants to refuse. It would also make a menu write read order
history, inverting §6.5.4's direction. What bounds the number instead is the door: §4.3 authenticates every
person at a table. **And it stays reversible additively** — if the restriction is ever wanted it belongs on
the *read*, as a narrower second count.

## The one fact that could not have been written any other way

`TwoPressesAtOneInstantFoldToTheLater`. Nobody hides an order twice in one millisecond, so
`order_visibility_current`'s identifier tie-break has never had to work. **Everybody taps a heart twice**,
and one transaction stamps its rows with one `IClock.UtcNow`, so two presses genuinely share an
`occurred_at`. Without the tie-break `DISTINCT ON` returns the *oldest* row and a double-tap reads back as
the state before it. It is an answer only because **F-95** made `IIdentifierFactory` ascend inside a
millisecond. The fact asserts its own premise first — two rows, one distinct instant — because a fixture
whose clock had started moving would make it pass while testing nothing (F-41).

## F-111, in one paragraph

§8.2's note beneath `menu_item_event`'s DDL described a table with **five** event types and **two** payload
columns — twelve lines below a DDL block correctly showing eight and five — and required *"integration tests
must assert all ten combinations"*. Two things make it a finding rather than a typo. The sentence is
**normative**, and its arithmetic was wrong even for the schema it was written against: five types over two
nullable columns is twenty states, not ten. And the obligation it states is one **§16.4 separately rules
against writing** — a payload the CHECK refuses is refused loudly and by name, and re-asserting a constraint
is a monument (F-47). One section required tests another forbade; the tree obeyed the second; nothing could
see it. The counts are deleted, the obligation is reversed, and the *quotation* is made executable.

## Veto points

Three decisions are worth reversing if you disagree, and each is reversible on its own.

**1. `SetLikedAsync` takes `menu_item` `FOR UPDATE`, which is wider than the conflict it guards.** The
conflict is one person against themselves; the lock serialises every press on one dish against every other,
and against a rename of it. It is the standing idiom in that file's neighbours and it answers
`MenuItemNotFound` from the same statement. *To reverse:* drop `FOR UPDATE` from `LockMenuItemSql`. You keep
the existence check and the outcome, and you lose the serialisation — the fold stays correct under a race
and the **log** can gain a second `'liked'` row for one opinion, which is the failure ADR-0002 cares about.

**2. `IMenuItemReactions` is registered outside `IMenuWorkflow`, and it is the first menu write that is.**
*To reverse:* add a forwarding verb to `MenuWorkflow`, register it, move the `Assert.IsType` line into
`MenuWorkflow_IsResolvableInAScope_AndCoversEveryWriteServiceThatChangesTheMenu`, and rename that method
back. Note what you are buying: every heart-tap becomes a `MenuChanged` broadcast, and this is the one write
in the application that can fire many times a minute at one table.

**3. F-111's gate reads a 609 KiB document with a regex on every unit run.** That is a real cost in the fast
suite for a defect that arrives once every few migrations. *To reverse:* delete
`EveryVocabularyTheSpecificationQuotes_IsTheOneTheMigrationsDeclare` and its two helpers, move §16.4's count
for that class back to 3, and drop the fourth-fact sentence from its paragraph. The prose repair stands on
its own; what you lose is the reason to believe the *next* note will not drift.

## What was verified before packaging, and what was not

**Verified mechanically.** The tree was reconstructed from `dump.txt` and SHA-256 checked file by file: 370
files, the only differences being `export.sh` (it embeds its own file marker) and `LICENSE` (elided by
design). **Session drift was caught** — this session opened two slices behind, believing the tree was at
Slice 54 with an unverified 1242 baseline, and the tree said otherwise before anything was authored. The
test-count arithmetic was rebuilt from the tree and reproduces your terminal exactly: 910 + 329 + 21 = 1260.
The §16.4 counted-class gate was emulated over the edited specification — **31** counted classes, no
disagreements — and it caught the one regression this slice created (`MenuWiringTests` stated 28 against a
file holding 29) before the number was corrected. The Markdown table gate was emulated with the real
unescaped-pipe splitter over every tracked document: no problems. The version gate was emulated on both
versioned documents: headers matching their newest entries, entries descending. **The new vocabulary gate
was emulated in both directions and proven sensitive three ways** — a widened list planted in §8.2's quoted
`menu_item_event` DDL is reported as a value mismatch, deleting `menu_item_image_event_type_vocabulary`'s
quoted CHECK is reported as a key mismatch, and narrowing the quoted reaction list to one word is reported.
The standing authoring scans were run over every changed file: no `IndexOf(char, int, StringComparison)`, no
`await` in an interpolated hole, no `string.Create` chain, no `stackalloc` in a loop, no `@section` outside
an escaped `@@section` in a comment, no collection expression on a `Dictionary`. Byte hygiene — no CR,
exactly one final newline, no whitespace-only line, no context-dump separator — was checked on every file in
the archive.

**Not verified.** Nothing was compiled and nothing was run; there is no .NET SDK reachable from where this
slice was authored, so the C# is reviewed rather than built (F-71's standing caveat). The specific risks, in
order. `Dapper.ExecuteScalarAsync<Guid?>` against a `SELECT … FOR UPDATE` is the one call shape in the new
write service with no exact precedent in this tree — `DapperMenuAvailability` uses
`QuerySingleOrDefaultAsync<T>` over a row type for the same job, and the scalar form was chosen because the
lock statement selects one column; if it misbehaves it does so as a null where an identifier belongs, which
`AnUnknownItemReportsNotFoundAndWritesNothing` reaches from the wrong side. `count(*)::integer` is cast in
SQL rather than widened in C# so the column and `MenuItemLikeCount.LikeCount` agree at the boundary; a
reader expecting `long` would fail as an `InvalidCastException` from Dapper rather than as a wrong number.
And the two new `KeyRelations` rows assert a **view** through `to_regclass`, which resolves any relation and
is therefore correct — but it is the one thing in the migration gate this slice did not exercise against a
database.
