# M6 Slice 18 — the tree must parse before anything tries to build it

Every file below is a **full file** at its **repo-relative path**. Extract at the repository root and
the contents drop straight over your working tree — no diffs, no patches, no scripts to run.

```bash
tar -xzf m6-slice-18-tree-hygiene.tar.gz -C /home/kushal/src/dotnet/myrestaurant
```

Then, before anything else:

```bash
bash scripts/check_tree.sh
```

## Files to DELETE

**None.** Nothing here renames, supersedes or orphans anything: no migration, no schema change, no
package change, no ADR edit, no `.slnx` edit, no `Program.cs` edit. One new file lands in an existing
folder, so no `.csproj` edit either.

## What happened

Your `dotnet build` was not failing a test. It was failing to load:

```
error MSB4024: The imported project file
"/home/kushal/src/dotnet/myrestaurant/Directory.Build.props" could not be loaded.
Data at the root level is invalid. Line 86, position 1.
```

Seven of those, one per project, and the same fault from `clean`, `restore`, `test` and the container
build — where it appeared as `NETSDK1013: The TargetFramework value '' was not recognized`, which is the
same problem wearing a different hat, because the `TargetFramework` that file supplies never arrived.
MSBuild imports `Directory.Build.props` before it evaluates anything, so one malformed character there
fails every verb in the repository.

Line 86 was a line of eighty `#` characters appended after `</Project>`. That string is the section
separator `export.sh` writes *between* files in a context dump.

**Twenty-one tracked files carried the identical 82-byte suffix** — a newline, eighty `#`, a newline.
That was established by arithmetic rather than by looking: `export.sh` publishes an exact byte count for
every file it emits (`Size: 4.3 KiB (4447 bytes)`), so reading each file's content as exactly that many
bytes and comparing the tail against the separator gives an answer that does not depend on judgement.

The twenty-one were exactly the **modified** files of the close-out slice. The five **new** files of that
slice were clean. That asymmetry names the cause with no room left for a theory: the modified files were
reconstructed by reading the previous dump back, and the reader took the decoration between files for
the end of a file. The authoritative terminator in that format is the byte count. It was always there;
nothing in the dump format needed fixing, and this delivery was built by reading it correctly.

`docs/BUILD_PROGRESS.md` had **two**: the trailing one, and a second buried at line 5760 between the
Slice 16 section and the close-out section, left over from an earlier cycle that appended text after it.
That is how a stray separator stops being at the end of a file and becomes something no amount of
inspecting the end of a file will find.

## Why fifteen of the twenty-one said nothing

| Files | What the line means there | Consequence |
| --- | --- | --- |
| `Directory.Build.props` | XML content after the root element closed | `MSB4024` on every MSBuild verb — the outage |
| 5 × `.cs` | a preprocessor directive with a garbage name | `CS1024`, on a compile that never got to run |
| `ci.yml`, `release.yml`, `Containerfile`, `.env.example` | a comment | nothing. All four parsed perfectly while damaged |
| 4 × `.md` | a heading rule | renders as a horizontal line |
| 3 × `.razor` | literal markup text | eighty `#` on the page |
| `app.css` | a dangling selector | discards itself **and the rule after it** |

A class of damage that is catastrophic in one file and invisible in fifteen is a class of damage that
belongs to something running on every push. The cost of finding it turned out to be one `grep`.

## The files

**Restored — the 82-byte suffix removed, and otherwise byte-identical to what you had.** I verified
that: after stripping the suffix, every one of these matches your on-disk file exactly, including its
original trailing-newline count. Eight of them also carry the documentation and CI edits below.

| File | Also edited? |
| --- | --- |
| `Directory.Build.props` | no — restoration only. **This is the one that unblocks the build.** |
| `src/…/Program.cs` | no — restoration only |
| `src/…/Configuration/RestaurantOptions.cs` | no |
| `src/…/Identity/ObligationsEnforcement.cs` | no |
| `tests/…/RestaurantOptionsTests.cs` | no |
| `tests/…/Identity/ObligationsEnforcementTests.cs` | no |
| `src/…/Components/Layout/MainLayout.razor` | no |
| `src/…/Components/Layout/DisplayLayout.razor` | no |
| `src/…/Components/Pages/Home.razor` | no |
| `src/…/wwwroot/app.css` | no |
| `Containerfile` | no |
| `.env.example` | no |
| `.github/workflows/release.yml` | no |
| `docs/REQUIREMENTS.md` | no — deliberately unchanged, see below |
| `.github/workflows/ci.yml` | **yes** — new `tree` job |
| `README.md` | **yes** — the gate table, the layout section, the first-build checklist |
| `docs/TECHNICAL_SPECIFICATION.md` | **yes** — **v1.4**: §16.4, Appendix A |
| `docs/OPERATIONS.md` | **yes** — §14 |
| `docs/DOCUMENTATION_REVIEW.md` | **yes** — **F-40** |
| `docs/BUILD_PROGRESS.md` | **yes** — buried separator removed at 5760; Slice 18 appended |
| `_CHANGES.md` | **yes** — this file |

**New:**

| File | Change |
| --- | --- |
| `scripts/check_tree.sh` | the gate — five properties of the checkout, run first in CI and locally |

**Edited, not corrupted:**

| File | Change |
| --- | --- |
| `scripts/ci_local.sh` | tree hygiene inserted as gate 1; sections renumbered; help text |
| `compose.yaml` | line 109 was a blank line carrying three spaces |
| `tests/…/Identity/DapperUserStorePasskeyTests.cs` | line 228 was a blank line carrying eight spaces |

Twenty-five files. Every one is in the archive for a reason stated above.

## The gate

`scripts/check_tree.sh`, first in both CI (its own `tree` job) and `scripts/ci_local.sh`. Five
properties of the checkout, asserted before any tool that would report their absence as something else:

1. **No context-dump separator** in any tracked file. `export.sh` is exempt **by path**, because writing
   that string is its job; the exemption is a literal path comparison rather than a cleverer rule, so it
   is obvious and cannot widen by accident. The threshold is **twenty** `#` rather than eighty:
   Markdown's deepest heading is six and nothing in this tree has a use for twenty consecutive ones, so a
   separator that was re-wrapped or truncated on the way in is caught too, where an exact-length match
   would wave it through.
2. **No whitespace-only lines.** Narrower than `.editorconfig`'s `trim_trailing_whitespace` on purpose:
   it fails only on lines made *entirely* of spaces or tabs, never on trailing whitespace after real
   content — two spaces at the end of a Markdown line are a hard break, and a gate that forbade those
   would be wrong about Markdown rather than right about whitespace. A line with nothing but indentation
   has no such defence. This is the check the two whitespace fixes above exist to satisfy.
3. **LF endings and a final newline.** Both load-bearing. A CRLF in a shell script reports as
   `bad interpreter: /usr/bin/env bash^M`, which names the wrong problem; a missing final newline is what
   a truncated transfer looks like, which makes this the cheapest available detector of the *other* way a
   delivered tree arrives damaged.
4. **Every `.props`, `.targets`, `.csproj`, `.slnx` is well-formed XML.** The gate that would have turned
   this incident into thirty seconds. `xml.etree` is standard library — no package, no network.
   Well-formedness only: MSBuild stays the authority on whether a project *means* anything; this asserts
   MSBuild will get far enough to have an opinion.
5. **Every `.yml` / `.yaml` parses.** Blocking where a parser exists, a reported skip where none does —
   the shape your shellcheck gate already uses. Worth being explicit that this gate could **not** have
   caught the incident: a trailing `#` line is valid YAML, and both workflows parsed while damaged.
   Gate 1 finds that. This one is for a workflow that was truncated or re-indented, which nothing else in
   the pipeline reads early enough to blame correctly.

Gates 1–4 need only git, grep and the Python standard library, so they block everywhere — including a
workstation with no SDK. On Fedora, `sudo dnf install python3-pyyaml` turns gate 5 from a skip into a
check; GitHub's runners already have it.

## Four decisions worth being able to veto

**A `tree` job rather than a step on `shell-scripts`.** It is not about shell — the file it exists to
protect is XML. And a distinct check name means a failure here is attributable from the commit list
without opening anything, which is the whole point of a gate whose failure mode is a message that blames
the wrong tool. Costs one runner slot for about ten seconds. If you would rather it were a step inside
`shell-scripts`, that is a four-line move; I went this way because renaming an existing job could break a
required status check, and adding one cannot.

**The two whitespace fixes, rather than making gate 2 advisory.** A gate that reports a finding on every
run is a gate people learn to ignore — the argument this repository already makes about `NU19xx`. Both
edits are a blank line losing its leftover indentation, so the behavioural risk is zero.

**`REQUIREMENTS.md` is untouched.** This is the v1.2 call, not the v1.3 one. `.editorconfig` has asked
for LF endings, a final newline and trimmed whitespace since M1, and §16.4 is the section that records
which of the project's own rules are enforced instead of remembered. Nothing new is being asked of the
program, so nothing changes in the requirements.

**`export.sh` is untouched.** The tempting fix is an explicit end-of-content marker so a reader cannot
mistake the separator for content. It would be redundant: the format already publishes an exact byte
count per file, which is an unambiguous terminator, and the failure was in the consumer rather than the
producer. Changing the dump format would also invalidate every tool that already reads it correctly.

## Build and test

```bash
bash scripts/check_tree.sh
#    expect: 5 gates, "tree hygiene passed.", exit 0. Under two seconds, no SDK needed.
#    If gate 5 says SKIP, that is PyYAML missing locally and is not a failure.

dotnet build
#    expect: all seven projects succeed, 0 errors.
#    This is the assertion that matters here — the tree you have now cannot reach a compiler at all.

dotnet test
#    expect: 996 total, 0 failed, 982 succeeded, 14 skipped. UNCHANGED from the close-out slice.
#    No test is added, moved, renamed or unskipped in this slice. The 25 facts the close-out added
#    (16 BuildInformationTests, 8 RestaurantOptionsTests, 1 ObligationsEnforcementTests InlineData)
#    have still never executed, so 996 is a prediction rather than an observation. If you get a
#    different number, look there first, not here.

bash scripts/ci_local.sh --with-all
#    expect: 5 numbered gates now — tree hygiene is the new first one.

MYRESTAURANT_E2E=1 dotnet test tests/MyRestaurant.EndToEnd.Tests
#    expect: 15 passed, 0 skipped.
```

## What was actually verified here

No .NET SDK and no container engine in the sandbox, so **nothing here has been compiled or executed**
beyond the shell script. What *was* run:

- The twenty-one damaged files identified by byte arithmetic against each `METADATA` block's `Size:`
  field, not by inspection, and the corrupt suffix confirmed byte-identical across all of them.
- **A full reconciliation of the delivered tree against your on-disk tree.** Every one of the 309 files
  was compared; 284 are byte-identical, and the 25 that differ are the 25 listed above. That is how I
  know the restorations changed nothing but the suffix — four files initially drifted by a single
  trailing newline from an over-eager normalisation, and that was reverted.
- After repair: all 10 MSBuild/solution files parse as XML; all 4 YAML files parse (`ci.yml` is now five
  jobs, `tree` first with two steps, `boot-smoke` still ten; `release.yml` still three).
- The tree scanned for every other artifact the dump format could have leaked — `--- METADATA ---`,
  `--- CONTENT ---`, `# FILE: `, the `═` rule — none present anywhere.
- The whole tree checked for what gates 2 and 3 assert, so they land green rather than
  green-except-for-two: exactly two whitespace-only lines existed and are fixed; **zero** files have
  CRLF, **zero** lack a final newline, **zero** are empty.
- `scripts/check_tree.sh` run against the repaired tree (5 gates, exit 0), then against a scratch copy
  with all five damage patterns re-introduced: the separator in `Directory.Build.props`, in `Program.cs`
  and buried mid-document in `BUILD_PROGRESS.md`; a whitespace-only line in `compose.yaml`; a truncated
  flow sequence in `ci.yml`. It reported six problems, named each by file **and line** — including
  `Directory.Build.props:86`, the exact line MSBuild complained about — and exited 1.
- `bash -n` plus `shellcheck` at `--severity=warning` **and** `--severity=style` on the new script and on
  the edited `ci_local.sh`. Both clean at both, which keeps `ci.yml`'s claim that every script in this
  tree is style-clean true.
- Every documentation edit applied by exact-match replacement with an assertion that the anchor appears
  **exactly once**, so nothing was edited by position. One anchor was initially too short and pulled the
  F-38 sentence out of §16.4's first paragraph; that was caught by reading the result back and repaired.

## Where to look if this breaks

**`dotnet build` still fails on `Directory.Build.props`.** The extract did not land. Check
`sed -n '82,84p' Directory.Build.props` — the file is 84 lines and ends at `</Project>`.

**`dotnet build` now fails with `CS1024`.** A `.cs` file still has the appended line. Run
`bash scripts/check_tree.sh`; it names the file and the line number.

**`check_tree.sh` fails on a file I did not send you.** Then a file was damaged that was not in the
close-out slice's modified set, which would mean the pattern is wider than the twenty-one I found. Send
me the output and I will look; do not delete the line by hand until you know why it is there.

**Gate 5 says SKIP on your workstation.** PyYAML is not installed. `sudo dnf install python3-pyyaml`.
This is not a failure and CI checks it regardless.

**Gate 2 fails on a Markdown file after you edit one.** You wrote a line with only indentation on it.
Note that a Markdown *hard break* — two spaces after real text — is deliberately allowed; the gate only
objects to lines that are nothing but whitespace.

**The `tree` job fails in CI but the script passes locally.** The likeliest difference is gate 5, which
runs blocking there and may be skipping here. The second likeliest is that something in your working
tree is untracked: the script reads `git ls-files`, so a file you have not `git add`-ed is invisible to
it locally and present in CI's checkout only if you committed it.

**Test count is not 996.** Nothing in this slice touches a test's behaviour. Both test files in the
archive are pure restorations. Look at the close-out slice's 25 new facts, which are executing for the
first time.
