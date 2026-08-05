# M6 Slice 19 — the gate that could not pass

Every file below is a **full file** at its **repo-relative path**. Extract at the repository root and
the contents drop straight over your working tree — no diffs, no patches, nothing to run.

```bash
tar -xzf m6-slice-19-gate-scope.tar.gz -C /home/kushal/src/dotnet/myrestaurant
```

Then:

```bash
bash scripts/check_tree.sh
```

## Files to DELETE

**None.** Nothing here renames, supersedes or orphans anything: no migration, no schema change, no
package change, no ADR edit, no `.slnx` edit, no `Program.cs` edit, no `.csproj` edit, no new file. Five
existing files are replaced in place.

## What happened

Slice 18's gate landed, and the next full run said this:

```
tree hygiene FAILED: 1321 problem(s). Nothing was modified.
```

On a tree in which every file was correct. Tree hygiene is gate 1, so **the four gates behind it never
ran** — shell lint, strict Release build, the full test suite, the end-to-end scenarios. CI's `tree` job
is red right now for the same reason, on the same files.

Everything else in that run was green, and that is what locates the defect rather than being scene-setting:

| | Result |
| --- | --- |
| `dotnet build` | all seven projects, 0 errors |
| `dotnet test` | **996 total, 0 failed, 981 succeeded, 15 skipped** |
| `MYRESTAURANT_E2E=1` | **15 passed, 0 skipped** — the whole §16.3 matrix |
| `run.sh --smoke` | `/healthz/ready` 200 |
| `run.sh --containers-only` | stack up and healthy |
| `dotnet list package --outdated` | nothing outdated |
| `scripts/quick_tunnel.sh` | tunnel up, prechecks pass |
| `scripts/ci_local.sh --with-all` | **failed at gate 1** |

The only thing wrong with the repository was the gate inspecting it.

## The 1321, which resolve exactly

| Count | Files | What the gate said | What was true |
| --- | --- | --- | --- |
| 638 | `docs/llm/dump.txt` | gate 1: separator, not content | it is a context dump; the separator **is** its structure |
| 638 | `docs/llm/vendor/claude-output.txt` | gate 1: separator, not content | same |
| 45 | every `.tar.gz` / `.zip` under `docs/llm/vendor/` | gate 3: "no final newline (truncated…)" | a gzip stream ends where it ends; a trailing `0x0A` would corrupt it |

1276 + 45 = 1321. **Two independent bugs, not one** — and each one alone would have left a real hole.

### Bug 1 — the exemption was half a rule

Gate 1 exempted `export.sh`, because writing separators is that script's job. It did not exempt
`docs/llm/` — the directory `export.sh` writes them **into**, and which `export.sh` itself has always
excluded from its own output as `EXCLUDED_DIRECTORY="docs/llm"`. The gate knew about the producer and not
about the product.

The second reason is stronger and generalises: a dump is a *copy* of the authored files, so every property
the gate asserts is asserted twice over the same content. A real finding is reported twice; a correct
separator is reported as a defect.

### Bug 2 — three gates, two beliefs about what a file is

Gates 1 and 2 are `grep -I`, and `-I` makes grep report no match in a binary file. They were binary-safe
**by accident**. Gate 3's final-newline half is `tail -c 1 | wc -l`, which has no such notion — so it
failed every archive in the tree, and its message, *"truncated, or an editor that does not add one"*, is
exactly backwards about a file that is intact.

The fix is not a third guard. It is **one predicate, `is_authored_text`, that all three gates consult**, so
they cannot disagree about a file. Binary-ness is asked of `grep -I` rather than read off an extension
list, because an extension list is a list somebody has to remember to update — and it would have been
wrong about the `.zip` files on the day they were added.

## The files

| File | Change |
| --- | --- |
| `scripts/check_tree.sh` | **the fix.** Scope decided once; gates 1–3 all consult it; skip counts reported |
| `docs/TECHNICAL_SPECIFICATION.md` | **v1.5**: §16.4 gains the scope rule; Appendix A gains **F-41** |
| `docs/DOCUMENTATION_REVIEW.md` | **F-41** — status line and the closing prose |
| `docs/BUILD_PROGRESS.md` | Slice 19 section appended. **Full file**, not an append block |
| `README.md` | the `tree` row of the gate table says what the gate skips |
| `_CHANGES.md` | this file |

Five tracked files and this one. **I compared all 310 files in the delivered tree against yours: 305 are
byte-identical and the 5 above are the only ones that differ.**

`docs/BUILD_PROGRESS.md` is a complete 421 KB file this time rather than a `docs/_append/` block and a
`cat >>` line — you asked not to be handed scripts that edit documentation, and that was the last of them.
There is nothing in `docs/_append/` to merge and nothing to run.

## What the gate says now

```
checking 310 authored text file(s) of 327 tracked
  skipped: 17 generated (docs/llm), 0 binary, 0 empty
```

"Checking 412 tracked file(s)" was true on the run that failed 1321 times, and the least useful true
sentence available. A gate whose silence is meant to mean something has to say what it looked at.

## Three decisions worth being able to veto

**`docs/llm/` is excluded by the gate, not untracked by me.** The tempting fix is
`git rm -r --cached docs/llm/`. That directory is your deliberate working record and what the repository
keeps is not the gate's business; the gate's *scope* is what was wrong. If you would rather untrack it,
the `GENERATED_DIRECTORIES` entry becomes harmless rather than wrong, so nothing here has to change either
way.

**Binary detection by `grep -I`, not by `.gitattributes`.** Marking the archives `binary` is the idiomatic
git answer and would fix bug 2. Rejected because it also changes how git diffs, merges and archives those
paths — a larger change than this needs — and because it does nothing about bug 1, a context dump being
text.

**Gate 1 stays blocking.** Slice 18 argued that a gate reporting findings on every run is a gate people
learn to ignore. That argument points here too, harder: a gate that cannot pass on a correct tree has to
be bypassed, and the four real gates behind it go with it.

## Build and test

```bash
bash scripts/check_tree.sh
#    expect: 5 gates, "tree hygiene passed.", exit 0, and a skip line reporting 17 generated files.
#    This is the assertion that matters in this slice. Under two seconds, no SDK needed.

bash scripts/ci_local.sh --with-all
#    expect: all 5 numbered gates RUN. Gates 2-5 have never executed under this script, so if
#    there is a surprise anywhere in this delivery it will be here rather than in gate 1.

dotnet test
#    expect: 996 total, 0 failed, 981 succeeded, 15 skipped. UNCHANGED — and now an observation
#    rather than a prediction, because your last run reported exactly this. No test is touched.

MYRESTAURANT_E2E=1 dotnet test tests/MyRestaurant.EndToEnd.Tests
#    expect: 15 passed, 0 skipped. Unchanged.
```

`dotnet build` is not in that list on purpose: no compiled file is touched in this slice.

## What was actually verified here

No .NET SDK in the sandbox — but this slice's subject is a shell script, and it was executed.

- The 1321 accounted for exactly, by file and by gate, from your run's own output: 638 + 638 + 45.
- **`docs/llm/` reconstructed in a scratch tree** at its real shape — both committed dumps at full size
  and seventeen archives — so the gate faced the input that produced the failure rather than a
  description of it. Result: 5 gates, exit 0, 17 skipped as generated.
- **Sensitivity re-proven, which matters more than the pass.** A gate that passes by skipping everything
  is worthless. All five damage patterns re-introduced *outside* `docs/llm/` — the separator appended to
  `Directory.Build.props`, to `Program.cs`, and buried at line 3000 of `BUILD_PROGRESS.md`; a
  whitespace-only line in `compose.yaml`; a truncated flow sequence in `ci.yml`; a CRLF in
  `scripts/backup.sh`; a stripped final newline on `README.md`. It reported **8 problems**, named each by
  file and line — including `Directory.Build.props:85` from gate 1 **and** gate 4 — and exited 1.
- **A binary file planted outside `docs/llm/`** in that same run, to prove the binary rule is general
  rather than a `docs/llm/` carve-out: reported as `1 binary` in the skip line and accused of nothing.
- `grep -I -q ''` confirmed as a binary detector against files with real NUL bytes and against a real
  gzip stream, and confirmed to agree with a NUL scan of the first 8000 bytes.
- The delivered tree — edited documents included — run through the new gate: passes, 0 findings. The new
  §16.4 paragraph and F-41 row contain no separator line and no whitespace-only line, checked rather than
  assumed.
- `bash -n` plus `shellcheck` at `--severity=warning` **and** `--severity=style`, both clean, which keeps
  `ci.yml`'s claim that every script in this tree is style-clean true.
- Every documentation edit applied by exact-match replacement with an assertion that the anchor appears
  **exactly once**, so nothing was edited by position.

## Where to look if this breaks

**`check_tree.sh` still fails on `docs/llm/`.** The extract did not land. `grep -n GENERATED_DIRECTORIES
scripts/check_tree.sh` — you should see the array set to `("docs/llm")` near the top.

**It fails on a file under some *other* generated directory.** Then there is a second such directory I
did not know about. Add it to `GENERATED_DIRECTORIES`; that is what the array is for. Send me the path and
I will record it in §16.4.

**The skip line reports more binary files than you expected.** `grep -I` calls a file binary if it finds a
NUL byte early, so a UTF-16 file would land there. Nothing in this tree is UTF-16 — `.editorconfig` sets
`charset = utf-8` — so a count above 0 outside `docs/llm/` is worth looking at rather than accepting.

**`ci_local.sh --with-all` now fails at gate 2, 3, 4 or 5.** Expected in the sense that these have never
run under this script: gate 1 blocked every previous invocation. This is a real finding about your tree
rather than about this delivery, and the gate names the file.

**Test count is not 996.** Nothing in this slice touches a test, a project file or any compiled source.
Look at what changed between your last run and now.

**The `tree` job passes locally but fails in CI.** Likeliest is gate 5, which runs blocking there and may
be skipping here — `sudo dnf install python3-pyyaml`. Second likeliest is a file present in CI's checkout
but not `git add`-ed locally, since the script reads `git ls-files`.
