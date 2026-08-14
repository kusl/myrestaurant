# M6 Slice 38 — item descriptions and item positions (Stage 2, item half minus the heading), and a count written in three places (F-77)

Extract at the repository root. Every path is repo-relative and every file is complete.

```
tar -xzf m6-slice-38-item-descriptions-and-positions.tar.gz
git status
```

**Files to DELETE: none.**

**One `git add` is required**, and the gates will not see the file without it — `scripts/check_tree.sh`
walks `git ls-files`, so an untracked file is invisible to tree hygiene:

```
git add src/MyRestaurant.DataAccess/Migrations/0004_menu_item_descriptions.sql
```

**No new directory.** `Migrations/` exists, and `MyRestaurant.DataAccess.csproj` already globs
`Migrations/*.sql` as an `EmbeddedResource`, so the migration needs no project edit. Every other file in this
archive already exists and is tracked.

---

## Read this first: Slice 37 was green, exactly as predicted

```
Test summary: total: 1107, failed: 0, succeeded: 1107, skipped: 0
§16.3 end to end: 16 of 16
all local CI gates passed
```

**1107 predicted, 1107 observed**, so per §18 there was no difference to chase. All three of Slice 37's
findings are settled by that run rather than by assertion:

- **F-74 is cleared.** Gate 7 prints `has no vulnerable packages` for all seven projects. The claim was that
  a pin at `SSH.NET` 2026.0.0 clears NU1903, the evidence at authoring time was the advisory's own
  `first_patched_version`, and the restore is what settled it.
- **F-75 is cleared.** The audit exists locally and ran, as gate 7 of eight.
- **F-76 is cleared.** Gate 3 prints `LF endings and exactly one final newline` — the strengthened wording —
  and passes over 341 authored files.

One thing that run printed is **not** repaired here and is carried deliberately: `run.sh --containers-only`
emits two `Error: no container with name or ID "myrestaurant_caddy_1" found` lines from `podman-compose` and
then starts the stack successfully. It is noise from the engine's own teardown-before-up, and the only local
lever is suppressing stderr on `up`, which would hide real errors. Still a judgement rather than a slice.

## The ruling of this slice: the boundary moved a second time

Slice 37's *Still open* scoped `0004` as the whole item half — three columns `NOT NULL` from birth, the
conditional seed, the backfill, three event types, **and** three surfaces pulled forward plus a harness
`CreateMenuSectionAsync`. Counted against the real tree that is twenty-five-odd files, none of them compiled
in the authoring environment, with `menu_item.menu_section_identifier NOT NULL` breaking the create-item form
that six of the sixteen §16.3 scenarios drive.

**This slice cuts between the item's own columns instead**, which is Slice 37's own test applied one register
lower. `0004` adds `description` and `display_order` — both `NOT NULL` with a `DEFAULT` — plus the two event
types that move them. It does not add the section reference. The properties that follow are the argument:

- **No backfill runs and no existing row is rewritten.** PostgreSQL 11 and later store a non-volatile
  `ADD COLUMN … DEFAULT` in the catalogue rather than the heap.
- **No form is required to supply anything**, so `AdministrationJourneys.CreateMenuItemAsync` fills `#name`
  and `#price` and clicks, exactly as it did. The new textarea is left empty, stores `''`, and writes no
  event.
- **The new ordering is the old ordering.** `display_order` defaults to 0 and nothing assigns anything else,
  so `ORDER BY (display_order, name, menu_item_identifier)` **is** the `ORDER BY (name, menu_item_identifier)`
  this table has always been read in. The tie-break is doing all of the work. That is why
  `MenuDirectoryTests.List_ReturnsDeactivatedItemsToo_OrderedByName` still asserts the alphabet and still
  passes unedited.

So the suite stays green **by construction** rather than by inspection, and the expensive coupling — a
`NOT NULL` reference against three surfaces and a harness — is paid on its own in `0005`. The cost is one
more migration script. DbUp journals by name, so that is not a cost.

## Four decisions inside the migration that are rulings rather than implementation

**An item is created at position 0, not appended.** `DapperMenuSectionAdministration` reads
`COALESCE(MAX(display_order), -1) + 1` and appends, because a section's position is menu-wide and always was.
An item's is not: §7 puts it *within its section*, and "the end of the menu" is not a defined place until
`0005` gives the item a heading. Appending a menu-wide number now would hand out positions `0005` would have
to undo — and it would break the invisibility property above, which is the whole reason this slice ships
green. The two services therefore disagree, on purpose, and both say why in the file.

**`created` keeps carrying the name and the price only, so an item created with a description writes two
events.** Widening it to carry a description is the obvious alternative. It is refused for a concrete reason:
a description is optional, so the biconditional binding the payload to the type would have to be relaxed to
an implication, and every `created` row already in a database was written without one. The log reads
*"Created as “Soup” at 4.50 / Description set"* — two lines where one would do, and honest about it. A
**blank** description at creation time writes no second event at all, on the no-op rule the other verbs
already honour: an append-only log of "somebody left a field empty" is noise.

**The vocabulary is spelled `description_changed` and `reordered`, and `menu_section_event` spells the same
two verbs `described` and `reordered`.** The asymmetry is a decision. Each table's vocabulary is internally
consistent — this one has said `name_changed` and `price_changed` since `0001` — and harmonising them would
mean rewriting a vocabulary already in applied history and in rows in somebody's database, to buy nothing a
reader of either table needs.

**Every CHECK on `menu_item_event` is dropped by querying for it, not by name.** `0001` declared four inline,
so PostgreSQL generated `menu_item_event_event_type_check`, `menu_item_event_new_price_amount_check`,
`menu_item_event_check` and `menu_item_event_check1`. Those are deterministic and undocumented, and depending
on them in a script that runs at startup on somebody else's box is depending on an implementation detail of a
PostgreSQL version nobody here chose. `0004` loops `pg_constraint` in a dollar-quoted `DO` block and adds five
**named** constraints back, so `0005` can widen the vocabulary by name on a tree that knows the name.
`contype = 'c'` cannot catch a NOT NULL in either 17 (an attribute) or 18 (`contype = 'n'`).

## What descriptions actually do, today

This is not schema-only. A description is authored, stored, logged, read back and displayed **this slice**:

- `/administration/menu/new` gains an optional textarea. `.form-field textarea` has been styled in `app.css`
  since Slice 30 for exactly this field, so the block declares nothing — and `.muted` is the shared soft-ink
  class rather than a new one. The success panel echoes the stored description when there was one, from the
  result rather than a second read, because the write service returns what it actually stored (trimmed).
- `/administration/menu/{id}` gains a **Description** form and a **Position** form, bringing that page to
  four verbs. The description form uses a `.form-field` block rather than `.manage-inline-form`, deliberately:
  that class styles `input` and `select` and has never styled a `textarea`, so a three-line box inside it
  would be an unstyled control on the one page that has one. The facts grid gains Position, and the item's
  description renders under it — or a `.muted` line saying guests see the name and the price only.
- `/administration/menu` renders the description as a `.record-secondary` **under the item's name** rather
  than in a column of its own, because a sentence in a table cell at 375px is the shape F-59 was about. The
  *Created* column is replaced by *Position*; created-at moves to the item's own page, where the history it
  belongs to already lives. The count line reports how many items are described.
- Both `Describe` methods learn the two new types. The item page quotes the description; the index feed says
  only that one changed, because the feed is a glance and the sentence belongs where the uncapped history is.
  Both distinguish **cleared** from **set**, because `''` is a stored value and *"Description set to
  nothing"* is not what a reader wants.

**What guests do not get yet, and why that is correct.** The picker is a `<select>`. A description inside an
`<option>` label is the problem Stage 3's card layout exists to solve, not a smaller version of the solution
— so the guest-facing half waits, and Stage 3's scenario 17 is where it gets an end-to-end assertion.
`MenuWorkflow` still publishes `MenuChanged` on a description that moved, and that is deliberate rather than
premature: the notification means *re-read the menu* and nothing else, and a workflow that decided which
columns were worth announcing would have to be edited again the moment a surface starts reading one.

## F-77 — a count of the event vocabulary, written in three files, checkable in none

`MenuEventLog`'s type summary, `AdministrationMenu.razor`'s `Describe` and `ManageMenuItem.razor`'s all said
*"a friendly label for the five event types §8.2's CHECK admits"*. Five was right when `0001` shipped. This
slice makes it seven; `0005` makes it eight.

**And one of the three was already wrong about a document in the same repository.**
`AdministrationMenu.razor` went on to name *"Stage 2's two new types (`description_changed`,
`section_changed`)"*, where `MENU_AND_HANDHELD_PLAN.md` Stage 2 specifies **three** — omitting `reordered`,
which is a type this very slice implements.

The effect is nil: the `switch` fallback renders an unlabelled type as itself, which is the design and is what
made the drift survivable. The kind is not nil. It is **F-47's habit** — where a rule can be executed, a list
must not exist — applied to a census no test can read, and it is the **sixth** time this ledger has recorded
one fact written in two places disagreeing with itself (F-48, F-50, F-56, F-65, F-73, this).

**The counts are deleted rather than corrected, and that is the ruling.** A corrected count goes stale again
in `0005`; three slices of evidence say the sentence cannot be maintained by hand. Each comment now states
the *property* — a friendly label per admitted type, falling back to the stored string — which is true at
every vocabulary size, leaving the `switch` arms as the only census.

**No gate is added, deliberately.** A test counting `case` arms against a CHECK constraint in a `.sql` file
would be a monument to a sentence, and the compiler already refuses a duplicate arm. **The apparent
contradiction with F-73 is recorded rather than left to be noticed**: F-73's count was *kept* because it is
the argument for a floor a test asserts and nothing derives it, so deleting it would have removed the
argument. This one decorates a `switch` that is its own answer. §16.4 states the residual — nothing
mechanically stops the next such sentence.

## The migration gate needed a new shape of fact, and that is the interesting half

`0004` is **the first migration in this tree that creates no relation.** It is entirely `ALTER`. So every
assertion `SchemaMigrationRunnerTests` already held passes unchanged on a tree where `0004` never ran: the
census of relations that catches `0003` is structurally blind to it.

Two facts close that. Four theory rows name the columns that arrived by `ALTER` — and only those, because
`0001`'s columns are proven by their relation existing and a census of all of them would be a second copy of
the DDL (F-47). One fact names the five CHECK constraints, and asserts the two generated names **absent**.

That second fact is load-bearing for a reason worth stating: a `dbup-postgresql` splitter that broke the
dollar-quoted `DO` block would leave a `DO` with a **truncated body**, which is still valid SQL that simply
does less. The failure would present as a green migration against a table with no constraints — a state no
relation check and no column check can see. This is the first statement in this repository to depend on that
splitter's `DollarQuoted` handling, which is why it gets an assertion rather than a comment.

## What is in this slice

| Area | Change |
|---|---|
| Migration | `0004_menu_item_descriptions.sql` — **new**, two columns, two payload columns, five named CHECKs replacing four generated ones |
| Data access | `MenuDirectory.cs` (`Description`, `DisplayOrder`, position-first ordering), `MenuAdministration.cs` (create takes a description; `DescribeMenuItemAsync`, `ReorderMenuItemAsync`), `MenuEventLog.cs` (two payload columns) |
| Web layer | `MenuWorkflow.cs` — two verbs, publishing only on a real change |
| Surfaces | `CreateMenuItem.razor` (textarea), `ManageMenuItem.razor` (Description and Position forms, seven flash outcomes), `AdministrationMenu.razor` (description as subtitle, Position replaces Created) |
| Tests | `MenuAdministrationTests` +8, `MenuDirectoryTests` +2, `MenuWiringTests` +2, `SchemaMigrationRunnerTests` +2 attributes (one a four-row theory); `OrderStagingTests` and `OrderTestWorld` updated for the new shape |
| Gate arithmetic | `TestingSectionContractTests`' floor and summary move from ten to sixteen with §16.4's census — F-73's habit, second application |
| Documents | S v1.23 (§7, §8.1, §8.2, §16.4, Appendix A, changelog), ledger F-77, ADR-0014 history, plan's Stage 2 boundary |

## What was verified

**Every `CreateMenuItemAsync` call site was found and updated by type, not by search.** The description is
positioned after `name` to mirror `CreateMenuSectionAsync`, so `string` lands where `decimal` was expected at
every un-updated site — a compile error, never a silent mis-bind. Nine sites: the interface, the
implementation, the workflow's interface and implementation, the Razor form, the wiring fake, and four in the
data-access tests.

**`MenuItemSummary` has exactly two construction sites in the whole tree**, verified by search:
`DapperMenuDirectory.ToSummary` and one factory in `OrderStagingTests`. Members were therefore *inserted* to
mirror `MenuSectionSummary`'s member order rather than appended, and the factory passes `""` and `0` with a
comment saying `OrderStaging` reads neither.

**Structural verification on all fifteen edited or new files, string- and comment-aware**, with untouched
siblings as controls. The walker was **proven sensitive by three planted defects** in
`MenuAdministration.cs` — a deleted closing brace, an unterminated raw string, and an extra parenthesis —
each reported. It was also **proven to have been wrong first**: an earlier version mis-parsed `$"""` as an
interpolated single-quote string and reported three false unbalanced files, which is why the controls are in
the run at all.

**The three Razor pages were walked as tag trees**, quote-aware, with the `@code` block excluded and `@*…*@`
comments stripped. Both of those exclusions are corrections rather than conveniences: the first draft read
`<ValidationMessage For="@(() => …)" />` as an unclosed element because `=>` contains `>`, and read
`IReadOnlyList<MenuItemSummary>` in the `@code` block as markup — which is precisely the standing false
report Slice 36 recorded. `AdministrationTables.razor` and `ManageTable.razor` are the controls; all five
files clean.

**Selector existence was checked against the stylesheet rather than assumed**, and it caught two real
defects before packaging: the first draft of `CreateMenuItem.razor` invented `.field-optional` and
`.field-hint`, neither of which exists in `app.css`. Both are now `.muted`, and every one of the thirteen
classes that page uses is declared. `.record-secondary` was confirmed present before being used on the index.

**The 375px barrier was reasoned about rather than hoped for.** `.form-field textarea` is `width: 100%` under
a universal `box-sizing: border-box`, so neither new control can exceed the viewport; and every `<td>` in
both tables on the index carries a `data-label`, with header and cell counts balanced at 5 and 5 —
`HandheldLayoutContractTests` asserts both.

**§16.4's contract test was simulated over the edited specification, and it caught a real failure.** The
first draft described all four test classes in one paragraph with one count, which that gate reads as
unattributable and reports — the same finding Slice 37 hit. Split into four paragraphs of one class and one
count each. Final state: **sixteen counted paragraphs, every claim matching its file, zero ambiguity, zero
uncited names.** The floor moved with it.

**`SpecificationVersionTests` simulated:** header 1.23 against newest entry 1.23, twenty-four entries
descending, both versioned documents found against a floor of two.

**The Markdown table gate was simulated over all twenty-four documents**, fence-aware and escape-aware:
fifty-seven table runs, zero shape problems. F-77's Appendix A row and ledger row were both checked to carry
exactly four cells.

**A CS1587 was introduced and caught.** Splitting a `<para>` in `IMenuAdministration`'s summary left a bare
blank line inside a `///` block, which under warnings-as-errors is a build failure. Repaired before
packaging. CS4007 and CS1620 scans clean across all fifteen files.

## What was NOT verified

**Nothing compiled.** There is no .NET SDK in the authoring environment. The likeliest sites of a complaint
are named rather than left to be discovered: `ScalarAsync<int?>` and `ScalarAsync<decimal?>` on a nullable
value type read through `OrderTestWorld.ScalarAsync<T>` (precedent at `MenuAdministrationTests` line 211,
which is why it is used rather than avoided); `HashSet<string> found = [.. names];` building a collection
expression from a Dapper `IEnumerable<string>`; and `InputTextArea` with a `rows` attribute, which is a
standard component parameter passthrough but is not used anywhere else in this tree.

**No database ran it.** Unlike Slice 37, no PostgreSQL was available in the authoring environment this time,
so `0004` is **reasoned about and not executed**. The three things that would settle it are stated so a red
run is read correctly rather than chased in the wrong place:

1. **Whether DbUp's splitter survives the `DO` block.** The claim rests on `PostgresqlQueryParser`'s
   `DollarQuoted` state consuming a tagged block, which Slice 37 read from source but did not exercise —
   `0003` contains no dollar-quoting. `Run_NamesTheMenuItemEventCheckConstraints` is the assertion that
   decides it, and a failure there means the block split, not that the names are wrong.
2. **Whether `pg_constraint` returned all four generated names.** If the loop ran against a partial list, a
   leftover `menu_item_event_check` survives — which the same fact asserts absent.
3. **Whether `ADD CONSTRAINT` on an existing table with existing rows validates.** Every constraint added
   here is satisfied by every row `0001`'s CHECKs already admitted, and the two new columns are NULL on every
   existing row, which both new biconditionals require for the five old types. That is an argument, not a run.

**No browser rendered anything.** Three Razor pages changed, and the reachability barrier at 375px is
scenario 16's job on the real run. The two new forms on `ManageMenuItem.razor` are the surfaces that have
never been laid out, and a red first run there is informational.

**The description is not exercised end to end.** No §16.3 scenario fills the new textarea, and the harness is
deliberately untouched so the sixteen scenarios stay byte-identical. Stage 3's scenario 17 is where that
lands.

## Test count

Last observed: **1107**, from Slice 37, matching its prediction exactly.

Predicted here: **1107 + 8 (`MenuAdministrationTests`) + 2 (`MenuDirectoryTests`) + 2 (`MenuWiringTests`)
+ 4 (`SchemaMigrationRunnerTests` theory rows) + 1 (`SchemaMigrationRunnerTests` fact) = 1124.** Arithmetic on
the last observed count, not an observation. §16.3 stays at **16**.

Per §18: if the run returns anything other than 1124, that difference is the next thing to chase before this
slice closes.

## Still open

**`0005` and the last of Stage 2.** `menu_item.menu_section_identifier uuid NOT NULL REFERENCES
menu_section`, the conditional one-section seed and the backfill beside it, `section_changed` with its payload
column and a vocabulary CHECK now droppable **by name**, and the three surfaces the `NOT NULL` forces: the
section create page, the section picker on the item form, and a harness `CreateMenuSectionAsync` the five
ordering scenarios call before their first `CreateMenuItemAsync`. That last file decides whether the ordering
integration tests compile.

**The section writes are still not behind `IMenuWorkflow`.** Deliberate, recorded three times now, and Stage
3's obligation — it becomes a defect the moment a guest surface groups by section.

**F-41 has no row in `DOCUMENTATION_REVIEW.md`.** Carried unchanged for a third slice: cited fifteen times in
that file and present only in Appendix A. Still a decision rather than a repair.

**`.sitting-meta` is declared by two components and the two have drifted.** Deferred a fifth time.

**A CI job that runs the canonical stack on the canonical engine.** Fourteenth consecutive slice.

**`run.sh --containers-only` prints two `Error:` lines about a container that does not exist yet, then starts
it successfully.** Carried from Slice 37, still a judgement about whose fix it is.

**No authoring-environment database this slice.** Slice 37 had PostgreSQL 16 and measured its schema half;
this one did not. That is a difference in evidence quality between two consecutive slices and it is recorded
so the next one restores the practice rather than quietly dropping it.
