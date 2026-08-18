# M6 Slice 51 — the picture a menu could not carry, and three columns a plan had already got wrong

Extract at the repository root. Every path is repo-relative and every file is complete.

```
tar -xzf m6-slice-51-menu-images-schema.tar.gz
git status
```

**Files to DELETE: none.**

**`git add` IS required — five new files, one of them in a new directory.**

```
git add src/MyRestaurant.Domain/Menu/ImageFormat.cs
git add src/MyRestaurant.DataAccess/Menu/MenuItemImages.cs
git add src/MyRestaurant.DataAccess/Migrations/0006_menu_item_images.sql
git add tests/MyRestaurant.Domain.Tests/ImageFormatTests.cs
git add tests/MyRestaurant.DataAccess.Tests/Menu/MenuItemImageTests.cs
git add docs/adr/0015-menu-item-images-in-the-database.md
```

`src/MyRestaurant.Domain/Menu/` **is a new directory** — `Domain` has had `Orders/`, `Security/`, `Time/`,
`Identifiers/`, `Authentication/` and `LiveUpdates/`, and never a `Menu/`. Every other directory above
already exists. `git status` should show **seven modifications and six untracked paths**; anything else
untracked means the archive was extracted somewhere other than the repository root.

**One thing to know about the new migration.** `Migrations/*.sql` is globbed as an `EmbeddedResource` in
`MyRestaurant.DataAccess.csproj`, so `0006` needs no project-file edit — but it **does** need `git add`, and
an untracked file is invisible to `git ls-files` and therefore to every gate and to CI.

**No `REQUIREMENTS.md` edit, no `OPERATIONS.md` edit, no `compose.yaml` edit, no `.slnx` edit, no `.csproj`
edit, no new package, no `export.sh` edit, no existing ADR amended, no read changed, no existing event type,
no workflow verb, and no CSS at all.** Nothing renders a picture yet.

---

## Read this first: your last run was clean, and a correction about where this project is

`total: 1202, failed: 0, succeeded: 1202, skipped: 0` — against a prediction of **1202**. §18's arithmetic
matched to the digit for the third consecutive slice. Seventeen scenarios passed against real browsers three
times over, and `ci_local.sh --with-all --with-e2e` reported every gate passing.

**The correction is mine and it is worth a line.** I arrived believing this project was at Slice 46 and
predicting 1166 tests. It is at Slice 50 and 1202: Slices 47 through 50 shipped the section resequencing
verb, the item resequencing verb, the heading description on the guest menu, and `MenuGrouping` with the
kitchen's grouped 86 panel. Stage 3 and Stages 3a–3c of the plan are closed. Nothing was lost — reconstructing
the tree from `dump.txt` settled it, 353 of 355 records matching their SHA-256 — but the authoritative account
of where this stands is `BUILD_PROGRESS.md` and your terminal log, not a session's recollection.

So there was nothing to chase. **This slice is Stage 4a**, plus the defect found while reading the plan that
specifies it.

---

## The finding: a schema about to be created out of an unchecked sketch (F-101)

Stage 4 of `docs/MENU_AND_HANDHELD_PLAN.md` has carried a DDL sketch for `menu_item_image` since Slice 30.
Three of its columns were wrong, in two different ways.

`byte_length integer NOT NULL CHECK (byte_length BETWEEN 1 AND 524288)` **is** `octet_length(bytes)` — one
fact written twice, in a table where one `UPDATE` can separate them, and where the copy every read would
select is the one that can be false. **F-65's mechanism, seventh occurrence.**

`pixel_width` and `pixel_height` are worse, and the argument is in the same stage's own prose: it says there
is no free-libre .NET imaging library available for this use and that the server therefore *validates and
stores what it is given*. **Nothing in this tree can open an image.** So neither number could ever have come
from anywhere but the uploading browser's word — recorded as a column, in the indicative, beside columns the
database actually knows.

### What makes it a finding rather than a refinement

**Three gates read that document and none reads a fenced SQL block for meaning.**
`MarkdownTableContractTests` reads it for table structure, `SpecificationVersionTests` for version agreement,
`check_tree.sh` for hygiene. A DDL sketch in a plan is therefore **authored prose carrying the authority of a
schema and none of the checking** — and its destination is uniquely unforgiving, because DbUp journals by
script name and an applied migration is never edited (F-34). The slice that copies a sketch into
`Migrations/` is the last moment it is free to be wrong.

Found by reading the sketch in order to implement it, which is **F-93's timing a second time**.

### The repair, and where it stops

All three columns are dropped **before `0006` was authored**, each reason written into §7 and into the script.
`new_byte_length` **is** kept on `menu_item_image_event`, and the asymmetry is stated as the point: after a
removal the bytes are gone, so the log is the only place that number can live.

**No gate is added over the plan's SQL blocks** (F-41, F-47). A document that sketches a schema *in order to
argue about it* legitimately contains shapes the tree does not have — Stage 4's sketch also argues against two
alternatives it never built — so a gate comparing them would report findings on a correct document. The
sketch is kept in the plan, marked superseded, so F-101's row has something to point at.

---

## The menu progress: Stage 4a

`0006_menu_item_images.sql` adds `menu_item_image` and `menu_item_image_event` and touches **nothing that
already existed**, which is `0003`'s cut a second time. Every read, every write, every integration fact and
all seventeen §16.3 scenarios mean exactly what they meant before. `OrderTestWorld` needed **no edit**, because
`TRUNCATE … CASCADE` on `menu_item` reaches both new tables.

### ADR-0015: the bytes live in the database

`bytea` rather than a volume, on **F-38's** argument. §15 *defines* a recovery set as exactly two artefacts and
`restore_drill.sh` rehearses both on every push; a third means editing that definition, `backup.sh`,
`restore.sh`, the drill and the runbook — after which an operator who takes one more backup the old way holds
a set that restores, quietly, an application whose menu has no pictures in it. Object storage is refused one
register out, on R§1's self-hosted premise.

**The direction of reversibility settles it rather than the cost.** `bytea` → volume reads rows and writes
files. Volume → `bytea` cannot find the files.

**It pays out in a file that is not in this archive:** `OPERATIONS.md`, both scripts and the drill are
untouched, because the recovery set is still two artefacts.

### Four schema rulings

- **One image per item as `UNIQUE` on the referencing column**, not a `bytea` on `menu_item` — a picture is
  replaced far more often than a dish is renamed, and a column on the item would put the images inside every
  menu read. A gallery is then a later migration that drops the `UNIQUE`, not a redesign.
- **A replace mints a new identifier and deletes the old row**, because §7's route is keyed on the image so
  that `Cache-Control: public, max-age=31536000, immutable` is a **true statement**. Keying on the item buys
  an `ETag` and a revalidation round trip per image per page load, on phones, forever.
- **A removal deletes the row** — a *stated exception* to §6.8, because the history is in the log and the
  bytes are not history. Keeping them would grow the recovery set by half a megabyte per retaken photograph.
- **The log references `menu_item`, not the image**, naming the image as a bare `uuid`. The **opposite** of
  `0005`'s ruling about `new_menu_section_identifier`, and opposite because the row it names is gone by
  design: a real key could only forbid the deletion or cascade the history away.

### The one addition to the sketch

**The declared media type is checked against the bytes' own signature.** §7's route hands the stored column
back out as a response header on your origin, so a column that disagreed with its bytes would make this
program mislabel its own responses. `MyRestaurant.Domain.Menu.ImageFormat` reads the opening bytes and no
more — PNG's eight, JPEG's marker, and **both halves** of WebP's RIFF header, since `RIFF` alone is also an
AVI and a WAV.

It is in `Domain` on **F-100's** argument, one slice after that finding: a pure function of a byte span, whose
interesting cases are the malformed ones.

**The vocabulary is declared twice — F-80's shape — and the agreement is gated behaviourally.** F-80's repair
compared two lists; this attaches a real file of every recognised format and requires the database to accept
it, so the two agreeing on paper while nothing can be stored is *also* a failure.

### The cap is the database's

§8.2 declares `menu_item_image_bytes_within_cap`. The write catches the check violation, compares the
constraint's **name**, and answers `BytesOverCap`. **No number appears in C# anywhere.** Two constraints
rather than one bounded `BETWEEN`, so an empty file and a four-megabyte photograph are distinguishable by
name; the empty case is refused in C# first, because zero is a definition rather than a policy number — and
refused first so a zero-byte file is not reported as a PNG that is not a PNG.

---

## Files in this archive

**New (six):**

| Path | What |
| --- | --- |
| `src/MyRestaurant.Domain/Menu/ImageFormat.cs` | Signature identification, pure, BCL-only |
| `src/MyRestaurant.DataAccess/Menu/MenuItemImages.cs` | Records, both interfaces, both Dapper implementations |
| `src/MyRestaurant.DataAccess/Migrations/0006_menu_item_images.sql` | Two tables, one index, nothing existing touched |
| `tests/MyRestaurant.Domain.Tests/ImageFormatTests.cs` | 8 assertions, no container |
| `tests/MyRestaurant.DataAccess.Tests/Menu/MenuItemImageTests.cs` | 11 assertions, real PostgreSQL |
| `docs/adr/0015-menu-item-images-in-the-database.md` | The storage decision and its seven rulings |

**Modified (seven):**

| Path | What changed |
| --- | --- |
| `src/MyRestaurant.WebApplication/Orders/OrdersServiceCollectionExtensions.cs` | Two registrations, and the comment naming the re-opened obligation |
| `tests/MyRestaurant.DataAccess.Tests/SchemaMigrationRunnerTests.cs` | Two theory rows on `KeyRelations`; one doc paragraph |
| `tests/MyRestaurant.WebApplication.Tests/Documentation/TestingSectionContractTests.cs` | `MinimumCountedClasses` 25 → 27 |
| `docs/TECHNICAL_SPECIFICATION.md` | v1.36; §7 gains five paragraphs; §8.1; §8.2 gains two tables; §16.4 gains three paragraphs; Appendix A gains F-101 and the Stage 4a row; changelog |
| `docs/MENU_AND_HANDHELD_PLAN.md` | Stage 4 becomes 4a (landed, struck through) and 4b (designed); the sketch is kept and marked superseded |
| `docs/DOCUMENTATION_REVIEW.md` | Status line and F-101's row |
| `docs/BUILD_PROGRESS.md` | Slice 51's narrative, appended; shipped complete |

---

## Test count arithmetic

Uncompiled, per §18. **1202 → 1223.**

| Where | Assertions |
| --- | --- |
| `ImageFormatTests` | 8 |
| `MenuItemImageTests` | 11 |
| `SchemaMigrationRunnerTests` theory rows | 2 |
| **Total added** | **21** |

§16.3 stays at seventeen. §16.4's census and its enforced floor both move 25 → 27.
`SchemaMigrationRunnerTests`' own **assertion** count stays at 7, because a theory row is not a `[Fact]` —
exactly the distinction F-90 was about, and the reason its §16.4 count does not move while the suite total
does. **Any deviation from 1223 is the first thing to investigate.**

---

## Two things flagged for your veto, cheaper to reverse now than later

**1. This re-opens the obligation Slice 43 closed.** Two data-access services now exist with no caller
outside their integration tests, which is the state `IMenuSectionAdministration` was in from `0003` until the
section editor. It is the **weaker** form — nothing is added behind `IMenuWorkflow`, so no surface can change
a picture without announcing it for the reason that no surface can change one at all — and it is named in §7,
in the DI comment, in the plan and in `BUILD_PROGRESS.md`. **To reverse:** hold `0006` and the two new source
files until Stage 4b and ship them in one slice with the route and the form. The cost of doing that is a
slice that carries a migration, a route, an upload transport decision, a CSP assertion and a 375px layout
together, which is the shape v1.30's ruling exists to avoid.

**2. `ImageFormat` is in `Domain`, which is a new directory and a second test class.** The census moves two
rather than one. **To reverse:** fold `IdentifyContentType` into
`DapperMenuItemImageAdministration` as a private static, delete `ImageFormatTests`, and drop
`MinimumCountedClasses` to 26 — at the cost of putting the truncated-signature and not-a-WebP-RIFF cases
behind a PostgreSQL container, which is the argument F-100 made one slice ago.

---

## What to do with this

```
tar -xzf m6-slice-51-menu-images-schema.tar.gz
git add src/MyRestaurant.Domain/Menu/ImageFormat.cs \
        src/MyRestaurant.DataAccess/Menu/MenuItemImages.cs \
        src/MyRestaurant.DataAccess/Migrations/0006_menu_item_images.sql \
        tests/MyRestaurant.Domain.Tests/ImageFormatTests.cs \
        tests/MyRestaurant.DataAccess.Tests/Menu/MenuItemImageTests.cs \
        docs/adr/0015-menu-item-images-in-the-database.md
git status
bash scripts/ci_local.sh
```

The integration facts need a container engine; without one they skip rather than fail, which would leave the
new migration unexercised — so `bash run.sh --smoke` is worth running too, because it applies `0006` against
a real PostgreSQL and returns 200 or does not.

---

## What was NOT verified

**Nothing was compiled and nothing ran.** This archive is a prediction until `dotnet build` says otherwise.

**No `bytea` round trip was performed.** `TheContentReadBackIsByteIdenticalToWhatWasStored` is the fact that
proves Dapper and Npgsql hand a `byte[]` parameter and a `bytea` column back unchanged. `byte[]` was chosen
over `ReadOnlyMemory<byte>` deliberately, being the mapping Npgsql has always had.

**Whether `PostgresException.ConstraintName` is populated for a check violation.** The cap outcome depends on
it, and any *other* check violation is rethrown by design — so if that field arrives empty the symptom is a
thrown `PostgresException` inside one named fact rather than a wrong answer anywhere else. Second thing to
look at on a red run.

**Whether `regexp_match(pg_get_constraintdef(…), '([0-9]+)')` finds the cap.** It relies on the rendered
CHECK holding exactly one run of digits; `CHECK ((octet_length(bytes) <= 524288))` does, and `octet_length`
has none — but the rendering is PostgreSQL's and no server was asked. A wrong answer fails that fact's own
guard, which requires the value to exceed a twelve-byte PNG before using it.

**Whether a private nested record can be a Dapper generic argument across a class boundary.**
`MenuItemImageTests.ImageEvent` is passed to `OrderTestWorld.QueryAsync<T>`. `DapperMenuSectionEventLog`
proves private nested records bind, but not from another type.

**Whether the collection expressions targeting `ReadOnlySpan<byte>` compile as written.**
`IdentifyContentType([.. PngPrefix, 0x00])` and the `params` spread in the test's `Riff` helper are C# 12
shapes this tree has not used before. If either is refused it is a compiler error naming the line, which is
the cheapest kind of red.

**What was done instead:** the tree was reconstructed from `dump.txt` and 353 of 355 SHA-256 hashes matched
(the two exceptions are `export.sh`, excluded and reproduced only in the dump's self-documentation, and
`LICENSE`, elided by design since Slice 46). Brace, paren and bracket balance is zero on all three new C#
files and the three edited ones. `0006`'s parenthesis depth returns to zero at every `;`, and it contains **no
dollar-quoted block at all**, which is what naming every constraint bought — F-78's collision cannot recur in
a script with no `DO`. Four gates were simulated: `TestingSectionContractTests` (27 counted classes against a
floor of 27, zero ambiguous, zero unresolvable citations — and **one disagreement, which it caught**: §16.4
said *seven assertions* for `ImageFormatTests` where the file holds eight, because the declared-versus-actual
fact was written last and the paragraph was not moved. **F-70's shape, in the section written to stop it**,
found by running the gate's logic rather than by re-reading the sentence. Both the paragraph and the
arithmetic now say eight),
`MarkdownTableContractTests` (0 problems, including the two new Appendix A rows and the new ledger row), the
version gate (header 1.36 against a newest entry of 1.36), and the platform-state gate. Tree hygiene was
checked on every touched and new file.

**The dump reduction is again specified and deferred by name**, unchanged from v1.33's statement of it: every
remaining cut splits a history register that four gates read, and this slice adds a table to one of them.
