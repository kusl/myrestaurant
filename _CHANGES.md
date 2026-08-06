# M6 Slice 22 — what leaves this machine

Every file below is a **full file** at its **repo-relative path**. Extract at the repository root and
the contents drop straight over your working tree — no diffs, no patches, no scripts that edit your
code or your documents. **`docs/BUILD_PROGRESS.md` is included whole**, with Slice 21 merged into it,
so there is nothing to append this time and nothing to remember afterwards.

```bash
tar -xzf m6-slice-22-build-context-and-package-visibility.tar.gz -C /home/kushal/src/dotnet/myrestaurant
cd /home/kushal/src/dotnet/myrestaurant
git rm docs/_append/BUILD_PROGRESS-M6-Slice-21.md
git add .dockerignore
```

Then:

```bash
bash scripts/check_tree.sh
bash scripts/check_repository.sh
```

## Files to DELETE

| Path | Why |
| --- | --- |
| `docs/_append/BUILD_PROGRESS-M6-Slice-21.md` | its contents are now inside `docs/BUILD_PROGRESS.md`, byte for byte and unedited. The directory `docs/_append/` goes with it — nothing will be delivered there again |

Nothing else is renamed, superseded or orphaned. No migration, no schema change, no package change,
no ADR edit, no `.slnx` edit, no `Program.cs` edit, no `.csproj` edit, **no C# and no Razor at all**.

**The two `git` commands above are not optional.** `git rm` because the archive cannot delete a file,
and an unmerged append file left in the tree is the exact defect this slice is closing. `git add`
because `ci_local.sh`, CI's `shell-scripts` job, `check_tree.sh` and `check_repository.sh` all
enumerate with `git ls-files`, so an untracked new file is silently unchecked by every one of them —
and `.dockerignore` untracked would also be missing from a fresh clone, which is where CI builds.

## What happened

Slice 21 fixed two red CI jobs and shipped no ledger. `docs/_append/BUILD_PROGRESS-M6-Slice-21.md` is
still sitting in your tree unmerged, and **F-43 and F-44 appear nowhere in `docs/DOCUMENTATION_REVIEW.md`
or in the specification's Appendix A**. S§18 says a behaviour change lands in one commit with its ledger
and specification edits; that delivery quoted the rule in its own notes and did not follow it. Both rows
are written now, dated to Slice 21, and the delivery mechanism that allowed it is retired.

Then I read the tree against what a tag makes true — F-39's habit, third time — one layer further out
than Slice 20 went. F-39 asked what a tag makes true about the program. F-42 asked what it makes true
about the repository. This slice asked about the **artefact**: what goes into the image, and who can
get the image back out. Neither question had ever been asked.

## F-45 — the build context was the whole working tree

There has never been a `.dockerignore` in this tree. `Containerfile` has said `COPY . .` since M1 with
the repository root as the context. Measured against a fresh clone with nothing built:

```
458 files, 31,148,997 bytes handed to the builder
    docs/llm/   16 MB
    .git/       11 MB
    src/         1.6 MB   <- the only part dotnet publish opens
```

87% is material the build cannot use. That is the harmless half.

**A build context is not a commit.** `.gitignore` names `.env`, `.dataprotection/`, every `*.dump` and
every `*-dataprotection.tar` — and it is correct about all of them; its own comment on the last one says
never to commit one. It protected nothing here, because neither engine reads it. Every one of those paths
was copied into the build stage on every local `--build`.

The ordering is not hypothetical, it is your documented upgrade:

```
OPERATIONS §12
  1. scripts/backup.sh                                     writes the key ring, in the clear (§8)
  2. podman-compose --profile production up -d --build     hands it to the image builder
```

CI escaped by accident of step ordering alone — `boot-smoke` builds before `backup.sh` writes anything.
And no gate could have caught it: every gate you have enumerates with `git ls-files`, and every file at
issue is git-ignored **on purpose**.

`.dockerignore` is an **allow-list**, and that is the ruling rather than a style choice: a deny-list is
what failed here. `.gitignore` is a well-kept deny-list, it was already right, and being right did not
help — a deny-list has to be extended for tomorrow's secret by somebody who remembers to. Context falls
to **169 files, 1.6 MB**, and the list has to be extended for tomorrow's *source directory* instead, by a
build that stops the moment it is not.

**And the row names something executable.** `Containerfile` carries a guard immediately after `COPY . .`
that fails unless the context root is exactly the allowed set, every required path is present, and no
`bin`/`obj` survives under `src`. An ignore-file can be renamed, shadowed by a `.containerignore` (Podman
prefers that name when both exist), or overridden with `--ignorefile`, and every one of those failures is
silent — the build still succeeds, just slower. This guard runs **wherever a build runs**, which is your
workstation and not only CI, and that is the point: the machine likely to have a key ring in the tree is
yours, not the runner's.

## F-46 — the F-42 rule, enforced as a list of examples

Slice 20's gate 3 exists so that no tracked file can assert a GitHub setting. It landed green, it has been
green since, and it was already wrong: `docs/OPERATIONS.md` §14 asserted, in the indicative, the
visibility of a published **package** and what that implied for pulling one. Three things wrong at once —
it is the *package* settings page and gate 3 enumerated the *repository* page; the package did not exist,
there being no tags on this repository; and GitHub's own documentation contradicts itself about which way
that switch falls for a `GITHUB_TOKEN` publish, so nobody could say whether the sentence was true.

§14 now states the intention and names where the switch lives for an operator who meets a 401. That is
true whichever way the checkbox falls, and useful in the case where it is wrong — which the old sentence
was not.

**Second half of the same blind spot:** the gate's advisory report on the published settings never ran on
a release. A called workflow sees only the secrets it is handed, and `release.yml` handed `ci.yml` none —
so the one run that *creates* a package produced no report about packages. It now passes
`ADMIN_READ_TOKEN` **by name** rather than `secrets: inherit`, and `ci.yml` declares it `required: false`
under `workflow_call`. A fork's pull request still skips that half and still goes green.

## The files

| File | Change |
| --- | --- |
| `.dockerignore` | **NEW.** The allow-list. Read by both engines from the context root |
| `Containerfile` | the guard after `COPY . .`, and the comment that says why the list is stated twice |
| `scripts/check_repository.sh` | gate 3's forbidden list gains the package-settings group, with the reasoning beside it |
| `.github/workflows/ci.yml` | `workflow_call` declares `ADMIN_READ_TOKEN` as an optional secret |
| `.github/workflows/release.yml` | `verify` passes that one secret by name |
| `docs/OPERATIONS.md` | §12 gains the ordering note; §14 gains the build-context section and replaces the visibility claim with intent plus the location of the switch |
| `docs/TECHNICAL_SPECIFICATION.md` | **v1.7**: §2 layout, **§14.1a** new and normative, §16.4 on the list belonging to the rule and on reaching the release run, Appendix A gains **F-43, F-44, F-45, F-46** |
| `docs/DOCUMENTATION_REVIEW.md` | Group E gains the same four rows, and a *Going forward* section including the procedural finding against Slice 21 |
| `README.md` | the `governance` and `boot-smoke` gate rows |
| `docs/BUILD_PROGRESS.md` | **Full file, 451 KiB.** Slice 21 merged in unedited, Slice 22 appended. Nothing to run |
| `_CHANGES.md` | this file |

`docs/REQUIREMENTS.md` is deliberately untouched, on the v1.2 and v1.4 reasoning: §15 has called the key
ring a secret since v1.0 and §18 has forbidden platform-state claims since v1.6, so both findings are
mechanisms catching up with contracts this tree already carried, not new intent.

## Build and test

```bash
bash scripts/check_tree.sh
#    expect: 5 gates, "tree hygiene passed.", exit 0, and the header reading
#    "checking 313 authored text file(s) of 423 tracked". That count is unchanged from Slice 21:
#    .dockerignore joins the scope and the _append file leaves it. If it says 312, the `git add`
#    did not happen; if it says 314, the `git rm` did not.

bash scripts/check_repository.sh --offline
#    expect: 3 gates plus a SKIP, exit 0. Gate 3 must report "none". Before the OPERATIONS §14 edit
#    it reports docs/OPERATIONS.md by file and line — that is this slice's second half proving
#    itself, and you can see it by reverting that one paragraph.

bash scripts/check_repository.sh
#    expect: 4 gates, exit 0, "passed, with 1 advisory warning" — the wiki. Description and private
#    vulnerability reporting both read clean now; Slice 20's two operator actions are done.

bash scripts/ci_local.sh --with-all
#    expect: 8 numbered gates, same number and same order as Slice 21.

dotnet test
#    expect: 996 total, 0 failed. UNCHANGED. No C#, no Razor, no .csproj, no migration and no
#    Program.cs is touched here. If this number moves, the cause is not this slice.

MYRESTAURANT_E2E=1 dotnet test tests/MyRestaurant.EndToEnd.Tests
#    expect: 15 passed, 0 skipped. Unchanged.

podman build --file Containerfile --tag myrestaurant_web:local .
#    expect: an early line reading "build context accepted: 169 file(s)", then the usual publish.
#    THIS IS THE ONE WORTH RUNNING BY HAND — it is the only thing here that exercises the new file,
#    and no other gate in the tree does.
```

`dotnet build` is not in that list on purpose: no compiled file is touched.

## What was actually verified here

No .NET SDK and no container engine in the sandbox — so nothing was reasoned about that could be
executed instead:

- **The 31 MB figure is measured**, from a fresh `git clone` of `kusl/myrestaurant`: 458 files,
  31,148,997 bytes.
- **`.dockerignore` was evaluated against your real tree** with a faithful implementation of the
  documented matching rules (last matching pattern wins; `**` spans zero or more segments; a pattern
  matches a path or any ancestor). Result: 169 files, 1,615,409 bytes, with every path in the publish
  graph individually asserted present — all three `.csproj`, `Migrations/0001_initial_schema.sql`,
  `wwwroot/app.css`, `appsettings.json`, `Components/App.razor`.
- **Sensitivity proven by planting the real hazards** — `.env`, `.dataprotection/key-abc.xml`, a
  `.dump`, a `-dataprotection.tar`, a stale `obj/project.assets.json` carrying a host `.nuget` path, a
  built `.dll` under `src/**/bin`. All six excluded with the file present; all six confirmed copied with
  it absent.
- **The `Containerfile` guard was executed in `dash`** — the SDK image's `/bin/sh`, not bash — after
  joining its line continuations the way the Dockerfile parser does. `dash -n` clean on all three `RUN`
  bodies. It accepts the real 169-file context and rejects eight separately constructed damage cases,
  each reverted before the next: a leaked `.env`; a leaked `.git`; `docs` + `tests` + `README.md` (the
  no-ignore-file case); a `backups/` holding a key-ring tar; an `obj/` under `src`; a missing `.csproj`;
  a missing `global.json`; and your tree as it stands today. Every one exits 1 and names what it found.
- **Ignore-file precedence checked against the sources rather than from memory**: Docker's build
  documentation for the context root and the `<Dockerfile>.dockerignore` override, and Podman's
  `podman-build.1.md.in` for `.containerignore`/`.dockerignore` and which wins when both exist.
- **Each new gate-3 pattern fired individually** — seven planted one at a time and reverted between
  runs — and four legitimate policy phrasings were planted to prove the list does not over-fire,
  including the replacement sentence §14 now carries and an instruction to *set* a visibility, which
  must stay sayable.
- **Both workflows parsed with PyYAML**, and the `workflow_call` secret declaration, `verify`'s
  `secrets:` block and the governance job's `env:` were read back out of the parsed documents.
- **`check_tree.sh` and `check_repository.sh` were run against the delivered tree**, staged, with both
  halves and a real token: 5 gates clean over 313 authored files, and governance clean with one advisory
  warning.
- `bash -n` over all eight tracked scripts. **shellcheck was not available in the sandbox** — the one
  thing here I could not run. `scripts/check_repository.sh` is the only script that changed and the
  change is two array literals and comment text, with no new command, expansion or control flow, so the
  exposure is small; CI runs shellcheck regardless.
- **Every documentation edit applied by exact-match replacement with an assertion that the anchor occurs
  exactly once**, so nothing was edited by position. `docs/BUILD_PROGRESS.md` was assembled from its
  existing bytes plus two whole appended sections — it was not regenerated, and Slice 21's entry is
  byte-identical to the file you already have.

## Where to look if this breaks

**`podman build` prints `BUILD CONTEXT REJECTED`.** Read the list it prints. Under `not allowed here`
it is naming top-level entries that reached the builder, which means `.dockerignore` was not read —
check it landed at the repository root, that no `.containerignore` exists beside it, and that nothing is
passing `--ignorefile`. Under `required, absent` the ignore-file went too far and the message names
exactly which path to re-include.

**`check_tree.sh` says 312 authored files.** `git add .dockerignore` did not happen. **314** — the
`git rm` did not.

**`check_repository.sh` fails at gate 3 on a file I did not touch.** Then that file already carried one
of the new phrasings and I missed it; the message names file and line. State the intention instead, or
send me the path.

**CI's `governance` job is red on a release.** It can only be the tree half — the platform half cannot
fail. `bash scripts/check_repository.sh --offline` locally gives the identical output with no network.

**The release's governance job now says `SKIP: no GitHub token`.** `ADMIN_READ_TOKEN` is not set in the
repository's secrets. That is the same skip you have always had there; it is now visible rather than
silent, which is the improvement.

**Test count is not 996.** Nothing in this slice touches a test, a project file, or any compiled source.
Look at what changed between your last run and this one.

## Still not a file, and still yours

The tag. `OPERATIONS.md` §14's release procedure is unchanged: bump `VersionPrefix`, run the gates, tag,
push. The one thing worth knowing before you do is that **`release.yml` has never executed** — every
action version in it resolves and every job is structurally sound, but the whole workflow is in the state
`scripts/restore.sh` was in before F-38. A `workflow_dispatch` off `main` publishes only
`ghcr.io/kusl/myrestaurant:sha-<commit>` and skips the release-notes job, so it rehearses the path
without spending a version number. That is a suggestion, not a finding, and it is not in this archive.
