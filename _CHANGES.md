# M6 Slice 20 — the door nobody left open

Every file below is a **full file** at its **repo-relative path**. Extract at the repository root and
the contents drop straight over your working tree — no diffs, no patches, no scripts to run against
your own documents.

```bash
tar -xzf m6-slice-20-disclosure-channel.tar.gz -C /home/kushal/src/dotnet/myrestaurant
git add SECURITY.md scripts/check_repository.sh
```

Then:

```bash
bash scripts/check_tree.sh
bash scripts/check_repository.sh
```

## Files to DELETE

**None.** Nothing here renames, supersedes or orphans anything: no migration, no schema change, no
package change, no ADR edit, no `.slnx` edit, no `Program.cs` edit, no `.csproj` edit, no C# at all.
Two files are new, nine are replaced in place.

**The `git add` above is not optional.** `ci_local.sh`, CI's `shell-scripts` job and `check_tree.sh`
all enumerate with `git ls-files`, so an untracked new file is silently unchecked by all three — and
`check_repository.sh` asks `git ls-files --error-unmatch` about `SECURITY.md` specifically, so an
unstaged policy file fails its own gate.

## What happened

Your last run was green everywhere. 996 tests, 0 failed. All fifteen §16.3 scenarios passing.
`ci_local.sh --with-all` clearing every gate. Tree hygiene clean over 310 authored files. Nothing
outdated.

So I read the tree against what a tag makes true, which is the habit F-39 established. The defect is
not in the tree. I asked the published repository what it looks like from outside:

```
has_issues                       : true
SECURITY.md                      : 404 at the root, in .github/, and in docs/
private vulnerability reporting  : disabled
has_wiki                         : true
description                      : null
open issues / pull requests      : 0
```

`CONTRIBUTING.md` had said since rev 1, in the indicative mood, that the issue tracker was switched
off. It was not. It never had been — the setting was on for the entire life of the sentence.

## Four doors, none of them working

A person who reads this source — which is what the AGPL is *for*, and which is exactly the population
that finds security defects — had these options:

- an Issues tab that was **open**, and that the only document addressing the question denied existed;
- no Security tab entry, because no policy file existed anywhere GitHub looks;
- no private reporting form, because the setting was off;
- no address, anywhere in the tree, in any file;
- and a notice that pull requests are closed unreviewed whatever their merit.

The only channel that worked was the one the documentation said did not exist, **and it was public.**
So the first thing anybody would have done with a forgeable join token is publish it — not out of
malice, but because there was nowhere else to put it.

Nothing has been lost yet, for one reason: nobody tried. Zero issues, zero pull requests, on the day
I looked. That is luck, not a channel.

## Why this is a finding rather than a chore

It is a category this ledger had not recorded, and each shape has a different guard:

| Shape | Example | What was wrong |
| --- | --- | --- |
| a capability a requirement stated and no milestone claimed | F-35, F-37, F-39 | the build order |
| a rule four documents agreed on and no code honoured | F-38 | intent recorded in a column reserved for fact |
| the transport between a correct spec and correct code | F-40 | twenty-one files damaged in delivery |
| **the repository disagreeing with its own documents** | **F-42** | **a layer nothing in the tree can see** |

`check_tree.sh` reads `git ls-files`. A test process cannot see a settings page. And the one document
that made a claim about that page made it in a mood that cannot be checked from inside the
repository. Every gate you have built was green, correctly, about the wrong thing.

**The rule that came out of it**, narrower and more useful than *check your settings*:

> A document in this tree states policy, never platform state.

"Nothing filed here is triaged" is a commitment — true wherever it is read, surviving a checkbox
toggle, and yours to keep. "The issue tracker is off" is a claim about a checkbox, and it went wrong
in the one direction that mattered.

## The files

| File | Change |
| --- | --- |
| `SECURITY.md` | **NEW.** The policy: private channel, no bounty said up front, scope both ways, single-maintainer timelines as targets, newest-tag-only support, and §17 as required reading |
| `scripts/check_repository.sh` | **NEW.** The sixth gate. Blocking tree half, advisory platform half |
| `CONTRIBUTING.md` | the false sentence replaced with policy; the security carve-out and its reason |
| `README.md` | a *Reporting a security problem* section, the `governance` gate row, the checklist |
| `scripts/ci_local.sh` | governance as gate 2; the four gates behind it renumbered |
| `.github/workflows/ci.yml` | a `governance` job with `administration: read` scoped to it |
| `docs/REQUIREMENTS.md` | **rev 4**: one new §8 principle, §10's carve-out, a revision-history row |
| `docs/TECHNICAL_SPECIFICATION.md` | **v1.6**: §16.4 the gate, §17 disclosure, §18 the rule, Appendix A **F-42** |
| `docs/DOCUMENTATION_REVIEW.md` | **F-42** and a *Going forward* section |
| `docs/OPERATIONS.md` | new **§16** — the runbook for receiving a report — plus §13 and §14 rows |
| `docs/BUILD_PROGRESS.md` | Slice 20 appended. **Full file**, 431 KB, nothing to run |
| `_CHANGES.md` | this file |

`docs/OPERATIONS.md`'s new section is **§16, at the end**, deliberately: its section numbers are
referenced from the specification and the ADRs, so inserting one anywhere else would have been a
silent break. I read the numbering back after the edit rather than assuming it.

## What you have to do that I cannot

Two things in this finding are not files, and no archive can contain them:

1. **Enable private vulnerability reporting** — Settings → Advanced Security. `SECURITY.md` sends
   reporters there, so until this is on, the policy names a door that is locked. This is the one item
   here that leaves a real gap rather than an untidy one.
2. **Set the repository description.** It is the first line anybody reads, and your release note
   tells people to `podman pull` from it.

Optional, reported with reasons and left to you: the wiki is on, and every document in this project
is in the tree under the atomic-documentation rule. And the Issues tab can stay open or be closed —
**the documents are now true either way**, which is the whole point of the policy-not-platform rule.

The gate will WARN about the first two on every CI run until you click them, and will never fail on
them.

## Three decisions worth being able to veto

**The platform half is advisory, not blocking.** A WARN nobody clears is a WARN people learn to
ignore — you have argued exactly that twice, in Slices 18 and 19. The argument does not transfer:
those gates reported on files, so a commit could always clear them. This one reports on something
outside the tree, where no commit can, and a *fork* cannot satisfy an assertion about your settings
at all. Failing their build over your disclosure preferences would be wrong about the licence this
project ships under.

**A separate gate rather than a sixth check in `check_tree.sh`.** That script's five gates are all
offline, all blocking, and all assertions that a file somebody wrote is machine-readable. Half of
this one is none of those, and a gate whose halves carry different authority should not answer to one
exit code.

**The exemption list has three entries.** `docs/DOCUMENTATION_REVIEW.md`, `docs/BUILD_PROGRESS.md`
and the gate script itself may contain the forbidden sentence, because quoting a defect is what a
ledger does and the gate has to hold the pattern list. Exempted by literal path, the way `export.sh`
is exempt from the separator gate. I kept `_CHANGES.md` off the list and paraphrased instead, so the
hole stays three files wide.

## Build and test

```bash
bash scripts/check_repository.sh
#    expect: 4 gates, exit 0, and "passed, with 3 advisory warning(s)". The warnings are the
#    real state of your repository, and the assertion that matters in this slice is that they
#    exit 0 — a finding about a settings page must not be able to fail a build.

bash scripts/check_repository.sh --offline
#    expect: 3 gates plus a SKIP, exit 0, no token needed. This is the half that blocks.

bash scripts/check_tree.sh
#    expect: 5 gates, "tree hygiene passed.", exit 0. The count rises from 310 to 312 — the two
#    new authored files. Under two seconds, no SDK.

bash scripts/ci_local.sh --with-all
#    expect: 8 numbered gates; governance is the new second one and gates 3-8 are unchanged.

dotnet test
#    expect: 996 total, 0 failed, 981 succeeded, 15 skipped. UNCHANGED. No C#, no .csproj, no
#    migration, no Program.cs is touched here. If this number moves, the cause is not this slice.

MYRESTAURANT_E2E=1 dotnet test tests/MyRestaurant.EndToEnd.Tests
#    expect: 15 passed, 0 skipped. Unchanged.
```

`dotnet build` is not in that list on purpose: no compiled file is touched.

## What was actually verified here

No .NET SDK in the sandbox — but everything in this slice is a shell script or a document, and both
were executed rather than reasoned about.

- **The finding was measured, not inferred.** Every number above came from the live GitHub API
  against `kusl/myrestaurant`: the repository object, the private-vulnerability-reporting endpoint,
  the community profile, and a 404 probe for the policy file at all three paths GitHub reads.
- **`scripts/check_repository.sh` was run against a real git tree**, both halves, against your real
  repository — and it passes with three advisory warnings and exit 0.
- **Sensitivity proven for every blocking gate individually**, because a gate that passes by
  asserting nothing is worthless. Seven damage cases, each reverted before the next: the forbidden
  sentence reintroduced into `CONTRIBUTING.md` (reported by file and line); the policy untracked and
  deleted; `§17` removed from it; the reporting-channel phrase removed; `README.md` stopped pointing
  at it; the policy stopped pointing back at `CONTRIBUTING.md`; the policy emptied. Every one exits
  1 and names the file.
- **The exemption proven narrow.** The same forbidden sentence appended to `docs/OPERATIONS.md` and
  to `docs/BUILD_PROGRESS.md` in one run: the first is reported at `docs/OPERATIONS.md:355`, the
  second is not.
- `bash -n` plus `shellcheck` at **both** `--severity=warning` and `--severity=style`, clean over all
  eight tracked scripts, which keeps `ci.yml`'s claim that every script in this tree is style-clean
  true. Your existing scripts were baselined clean first, so any finding would have been
  attributable to this slice.
- `scripts/check_tree.sh` run over the delivered tree, edited documents and new files included: 5
  gates, 0 findings, 312 authored files. The new prose was checked for separator lines and
  whitespace-only lines rather than assumed innocent.
- `.github/workflows/ci.yml` parsed with PyYAML, and the job list plus the `governance` job's
  permissions read back out of the parsed document rather than eyeballed.
- Every documentation edit applied by exact-match replacement with an assertion that the anchor
  appears **exactly once**, so nothing was edited by position. `OPERATIONS.md`'s section numbering
  read back afterwards.

## Where to look if this breaks

**`check_repository.sh` fails at gate 1.** The extract landed but the `git add` did not.
`git ls-files SECURITY.md` should print the path.

**It fails at gate 3 on a file I did not touch.** Then that file already contained one of the
forbidden phrasings and I missed it. The message names the file and line; state the policy instead,
or tell me the path and I will record it.

**Gate 4 says `private vulnerability reporting=unknown`.** The token lacks `administration:read`.
Expected on a fork's pull request and reported as unknown rather than as a finding; on your own
machine, `gh auth refresh -s admin:repo_hook` or just accept the unknown — the tree half is the half
that blocks.

**Gate 4 skips entirely on your machine.** No token in the environment. `GITHUB_TOKEN`, `GH_TOKEN`,
or an authenticated `gh` — and CI passes one, so this half runs there regardless.

**CI's `governance` job is red.** It can only be the tree half; the platform half cannot fail. Run
`bash scripts/check_repository.sh --offline` locally and you will get the identical output with no
network at all.

**`ci_local.sh` reports 8 gates and you expected 7.** Correct. Governance is the new gate 2.

**Test count is not 996.** Nothing in this slice touches a test, a project file, or any compiled
source. Look at what changed between your last run and now.
