# M6 Slice 36 — the suite that did not build (F-71), the register that was not a table (F-72), and a stale count inside the gate against stale counts (F-73)

Extract at the repository root. Every path is repo-relative and every file is complete.

```
tar -xzf m6-slice-36-the-suite-that-did-not-build.tar.gz
git status
```

**Files to DELETE: none.**

**One `git add` IS required**, and the gates will not see the file without it:

```
git add tests/MyRestaurant.WebApplication.Tests/Documentation/MarkdownTableContractTests.cs
```

The test SDK globs `**/*.cs`, so no `.csproj` edit is needed — but `scripts/check_tree.sh` walks
`git ls-files`, so an untracked file is invisible to tree hygiene and the authored-text count will not move.
Everything else in this archive already exists and is tracked. No new directory.

---

## Read this first

**Slice 35 did not fail a test. It never ran one.**

```
Test summary: total: 497, failed: 0, succeeded: 497, skipped: 0
Build failed with 3 error(s)
```

Those 497 are Domain, DataAccess and all sixteen §16.3 scenarios, and every one passed.
`MyRestaurant.WebApplication.Tests` **did not compile**, so its roughly five hundred and eighty assertions
did not run — including both facts Slice 35 had just written, which were the whole point of that slice.
Everything else on that run was green: tree hygiene, governance, shellcheck, `run.sh --smoke`, the
compose-substitution preflight, the E2E suite at 16 of 16, and the quick tunnel.

---

## The three findings

**F-71 — an overload that exists for `string` and not for `char`.** `DeclarationBlocksIn` called
`css.IndexOf('{', open + 1, StringComparison.Ordinal)` at three sites. The overload set was **read from
`dotnet/runtime` at `release/10.0`** rather than recalled: for a `char`, `System.String` declares
`IndexOf(char)`, `IndexOf(char, int)`, `IndexOf(char, StringComparison)` and `IndexOf(char, int, int)` — and
no three-argument-with-comparison form, though `string` has one and a four-argument one besides.
`LastIndexOf(char, StringComparison)` does not exist at all. Argument three bound to `count`, and CS1503
names a type mismatch on an argument rather than a missing member, which is why it reads like a wrong value.
The fix drops the argument and is behaviour-identical: in that same framework file
`IndexOf(char, StringComparison.Ordinal)` is a `switch` whose `Ordinal` arm returns `IndexOf(value)`, and
`IndexOf(char, int)` returns `IndexOf(value, startIndex, Length - startIndex)`.

**F-72 — both registers had stopped being tables.** Found by opening Appendix A to add F-71's row and
discovering there was no way to add one without first deciding how many columns a row has. Three defects, in
both documents:

| Shape | Appendix A | `DOCUMENTATION_REVIEW.md` Group E |
|---|---|---|
| Header narrower than its rows | declared **3** columns; every row F-38 to F-70 carried **4** | F-38's row held **5** against a 4-column header |
| Rows outside any table | F-63 to F-70 after a horizontal rule with **no header, no delimiter** — the whole of Slices 33–35 | thirty-one rows F-40 to F-70 in **fourteen fragments** |
| A row swallowing its neighbour | **F-65 had no row**, fused onto F-64's by a stray pipe pair | F-38's fifth cell, from an unescaped pipe in a code span |

A renderer truncates a row to its header's width and discards the rest silently, so the *Embodied in*
column — the second half of what *ruling to embodiment* means — was being dropped on thirty rows of the
register that heading names. **Nothing was ever wrong in the source.** It was wrong only once *rendered*,
which is how these two files are read, and it accumulated in the one direction nothing could catch: each
slice appended a row shaped like the previous slice's rows, so the drift was invisible **because** it was
consistent.

**F-73 — the gate against stale counts shipped with a stale count.** `TestingSectionContractTests`'s summary
said §16.4 *"carries eight of them"*; §16.4 as delivered carried nine, the ninth being the paragraph that
same slice wrote about that same test. Its `MinimumCountedClasses` said nine, so two numbers in one file
disagreed. F-69's mechanism, inside the repair for F-70.

---

## The decisions to veto, if you want to

**1. No new gate for F-71, and that is a ruling rather than an omission.** The compiler is the gate. It ran,
it blocked, CI would have blocked identically. A test asserting what CSC already rejects is the monument F-47
says to delete. What failed is the authoring-side verification, which walked brace balance and scanned CS1620
and CS4007 and cannot see an overload — so **§18 gains the habit instead: an archive that has not been
compiled is a prediction, and the first thing to do with one is build it.** The mechanical half of the trap
is now scanned by name in the authoring pass: three hits against Slice 35's tree, zero against this one.

**2. Appendix A goes to four columns rather than its rows going to three.** The newer rows were right — the
ledger has used `| ID | Finding | Ruling | Embodied in |` since Group A and Appendix A's rows have followed
it since F-38 while the header stayed compressed. Collapsing thirty rows to three columns would have merged
each F-number into its narrative and lost the scannable left column.

**3. The seventeen older compressed rows gain an em-dash, not a story.** `| F-20 | Hand-written fakes… |
§16.1 |` becomes `| F-20 | — | Hand-written fakes… | §16.1 |`. Writing a narrative for a 2026-07 ruling now
would be inventing history inside the register whose job is to hold it.

**4. F-63 to F-70 join the table after F-62** — numeric order, ahead of the four summary rows that have
always been an out-of-sequence tail. **The horizontal rule that stood inside the table moves above the next
heading**, which is where this document puts one before every other section; the likeliest reading is that it
was aimed there and landed one paragraph early.

**5. The new gate is named for the property, not for the register.** `MarkdownTableContractTests`, not
`DefectRegisterContractTests`. Naming it after the two files that prompted it would enforce a general rule
against its own examples — F-46's lesson, and the reason F-63 had to be written at all.

**6. F-73's number is kept rather than deleted, unlike F-69's.** F-69's count was the argument for a rule
being a *should*, so deleting it removed the argument. This one is the argument for a floor, and a floor is a
deliberate refusal to accept whatever the tree currently says; there is nothing to derive it from. What is
added is the habit of moving it.

---

## Files in this archive

| Path | Change |
|---|---|
| `tests/MyRestaurant.WebApplication.Tests/Components/HandheldLayoutContractTests.cs` | three call sites and one initialiser lose an argument that does not exist; the helper's summary records F-71 |
| `tests/MyRestaurant.WebApplication.Tests/Documentation/MarkdownTableContractTests.cs` | **NEW.** Two facts: a run of table lines opens with a header and its delimiter; a row carries its header's column count |
| `tests/MyRestaurant.WebApplication.Tests/Documentation/TestingSectionContractTests.cs` | F-73: the summary's count and `MinimumCountedClasses` both move to ten |
| `docs/TECHNICAL_SPECIFICATION.md` | **v1.21.** Appendix A restructured, F-63 – F-70 brought inside, F-65 restored; §16.4 two paragraphs; §18 the build-it-first habit; F-71, F-72, F-73; changelog |
| `docs/DOCUMENTATION_REVIEW.md` | Group E rebuilt as one table; F-38's pipe escaped; three rows; status line; *Going forward* |
| `docs/BUILD_PROGRESS.md` | Slice 36 section |
| `_CHANGES.md` | this file |

**Nothing under `src/` is touched.** No application code, no stylesheet, no Razor. The only behaviour that
changes is which assertions run.

---

## What was verified

No .NET SDK in the authoring sandbox, so all of this is text-level and says so. The point of F-71 is that
this is not sufficient.

- **Tree reconstructed from `dump.txt` and verified by SHA-256: 334 of 334 files match.** The one mismatch is
  `export.sh`, whose dump embeds a nested copy of the script that writes it. Incidentally the eleven files
  that needed a second trailing newline to hash correctly are **exactly** the eleven Slice 35 recorded as
  ending in two or more LFs — that open item reproduces from the dump alone.
- **All nine handheld facts and the testing-section fact ported to Python and run: ten facts, thirty-five
  assertions, zero failures.** They pass against Slice 35's tree too, so the compile error was the only thing
  between that tree and a green suite. A measurement, not a hope.
- **The two new facts proven sensitive by the tree**: forty-one findings against Slice 35's tree (fourteen
  structural, twenty-seven width), zero against this one.
- **Proven NOT to fire on two shapes the first draft got wrong**, both found in the tree and neither planted:
  `docs/OPERATIONS.md` writes a shell pipeline inside a table cell as an escaped pipe, correctly, and a scan
  splitting on every pipe called that row too wide; and `BUILD_PROGRESS.md` quotes eighteen lines of
  `dev_instance.sh`'s failure diagnosis inside two code fences, every line opening with the pipe that helper
  indents a container's log with, which without fence tracking is two runs of table lines with no delimiter.
- **The new gate caught this slice's own rows.** F-72's ledger row wrote a pipe pair and a `ps` pipeline
  unescaped inside a cell, and both rows came back seven cells wide against a four-column header. Fixed by
  escaping, which is the repair the failure message names.
- **§16.4's ten counted paragraphs each compared to their file: ten pairs, ten agree.**
- **`SpecificationVersionTests` ported:** header 1.21 against newest entry 1.21, twenty-two entries
  descending, two documents qualifying.
- **Bracket balance** on all three C# files, string- and comment-aware, with an untouched sibling as control
  and proven sensitive by deleting one brace. **CS1620** and **CS4007** clean. **The overload scan needed
  comment-stripping** to stay off a correct tree — the doc comment added to `DeclarationBlocksIn` quotes the
  bad call in prose, and that literal is left in place on purpose, so a future version of the scan that
  forgot to strip comments fails on arrival rather than quietly bounding its reach.
- **Byte hygiene** on every delivered file: no CR, one final LF, no whitespace-only lines.

## What was NOT verified

**Nothing compiled.** `MarkdownTableContractTests` is the only new C# file and the likeliest site of a
complaint: two `sealed record` declarations nested in the class, which nothing else in this test project
does; a `List<string>` inside a record; and `UnreadDirectoryNames.Contains(segment, StringComparer.Ordinal)`,
the LINQ overload rather than the array one.

**Nothing here confirms either register now renders as intended in a browser.** The gate asserts the
structure a renderer requires; whether a four-column table is *readable* at that row length is a judgement
for whoever opens the file, and the rows are long.

## Test count

Slice 35's predicted 1080 was never observed. The last observed figure is **1078**, from Slice 34.

Predicted: **1078 + 2** (Slice 35's two facts, now able to run) **+ 2** (this slice's two facts) = **1082**.
Arithmetic on the last observed count, not an observation. §16.3 stays at **16**.

Per §18: if the run returns anything other than 1082, that difference is the next thing to chase.

## One thing found and deliberately not fixed

**F-41 has no row in `DOCUMENTATION_REVIEW.md`.** It is cited fifteen times there and appears only in
Appendix A. Found by the same read that found F-72. Whether the repair is to write the row or to accept that
Appendix A is the register of record for gate-scope rulings is a decision, not a repair, and it does not
belong in a slice about table structure. The gate written here deliberately does not assert that every cited
finding has a row, because that assertion would have to be right about grouped rows like `F-21 – F-24` and
would report findings on a correct tree.
