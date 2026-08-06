# M6 Slice 23 — what leaves this machine

Every file below is a **full file** at its **repo-relative path**. Extract at the repository root and
the contents drop straight over your working tree — no diffs, no patches, no scripts that edit your
code or your documents. **`docs/BUILD_PROGRESS.md` is included whole**, with Slice 23 appended, so
there is nothing to merge afterwards and nothing to remember.

```bash
tar -xzf m6-slice-23-live-surface-contract.tar.gz -C /home/kushal/src/dotnet/myrestaurant
cd /home/kushal/src/dotnet/myrestaurant
git add tests/MyRestaurant.WebApplication.Tests/Components \
        tests/MyRestaurant.WebApplication.Tests/Documentation
```

Then:

```bash
bash scripts/check_tree.sh
bash scripts/check_repository.sh
```

## Files to DELETE

**None.** Nothing is renamed, superseded or orphaned.

No migration, no schema change, no package change, no ADR edit, no `.slnx` edit, no `Program.cs` edit,
no `.csproj` edit, no shell script, no workflow, no `Containerfile`, no `.dockerignore`.

**The `git add` is not optional.** Two new files land in a directory structure that does not exist yet,
and `check_tree.sh`, `check_repository.sh`, `ci_local.sh` and CI's `shell-scripts` job all enumerate
with `git ls-files` — an untracked new file is silently unchecked by every one of them. `dotnet test`
will still compile and run them (the test SDK globs `**/*.cs`), so the symptom of forgetting is a test
count that moves while the gates report an unchanged file count.

## What happened

Slice 21's F-44 row said the fix was not applied further because *"the other four live surfaces"*
carried the same latent race. This slice went to fix those four. **There are not four**, and the two
things that turned up on the way are the finding.

## F-47 — the enumeration, and the surface it never contained

`App.razor` decides interactivity in eleven lines:

```razor
private IComponentRenderMode? RenderModeForPage
    => HttpContext.AcceptsInteractiveRouting() ? InteractiveServer : null;
```

Computed rather than remembered, that is **six routable pages** plus `TableOrderSurface`, which carries
no `@page` and no `@rendermode` of its own and is interactive because `/table/{id}` hosts it that way.
Seven components. The tree said *five*, in five separate doc comments, one of which was
`CounterBoard`'s claim to have been "the only one of the five live surfaces that did not say".

**`/table` had been a live surface since M3 and published nothing at all** — no id, no `data-live`, no
`data-loaded`. It has a `_loaded` field and a "Looking up your tables…" branch, and the state the
hand-over renders before its query answers is an empty list, which is character-for-character the state
that means *you are not seated at a table right now*. Nothing was failing, because no §16.3 scenario
reads that page.

**The second half is the one worth reading twice.** The obvious way to close this slice was to copy
`CounterBoard`'s property into four files. On `/display/{table}` that would have published an attribute
that was `true` whenever its element existed. `TableDisplay` renders `id="table-display-surface"` from
two branches — the QR, and the "Preparing the join code…" card for when §4.3 returned nothing — and the
second is transient rather than fatal, so it is fully resolved, fully interactive, and has no code on
it. `[data-live='true']` already matched it, which is why scenario 2's failure mode when a join secret
is wrong is sixty seconds inside `ReadJoinQrPathAsync` and a message about a missing `d` attribute.

So §11.10 defines the bit at the level where six surfaces actually agree — **the question, not the
expression**: `data-loaded` answers *does this markup have what the surface renders itself for*. Five
answer it with `_loaded`. The display answers it with `_loaded && _qr is not null`, and that is the rule
applied rather than an exception to it.

Both halves are F-46's finding again: a rule stated as a rule and enforced as a list of examples is
enforced as a list of examples. F-43, F-44, F-46 and now F-47 are four checks in three slices whose
names were true and whose contents were narrower than their names, all green throughout.

**And the row names something executable** (F-38's lesson, fifth application).
`LiveSurfaceContractTests` **derives** the interactive set from `[ExcludeFromInteractiveRouting]` and
from `@rendermode="InteractiveServer"` in any file's markup, rather than from a list. Seven assertions,
including that the two bits are published the same number of times per file — which is the one that
catches a surface rendering its element from two branches, and the only one that would have caught
`TableDisplay` before this change.

## F-48 — the specification's header, for the second time

While bumping the specification the header read **v1.6** and the newest changelog entry read **v1.7**.
The v1.3 entry corrects the identical drift from Slice 16, in its own words. Found, corrected,
explained in the correcting document, and repeated seven versions later.

Recorded at full weight for the second half rather than the first: a stale version number is not worth
a paragraph, but a correction that left nothing behind that runs is. `SpecificationVersionTests` is two
assertions — header matches newest entry, entries descend — and a deliberate refusal to grow a third.

## The files

| File | Change |
| --- | --- |
| `src/…/Components/Pages/Table/TableArea.razor` | **the sixth surface.** `id="table-area-surface"`, both bits, both properties |
| `src/…/Components/Pages/Display/TableDisplay.razor` | new `_loaded`, latched on all four paths out of `OnInitializedAsync`; `data-loaded` on both surface branches, predicate `_loaded && _qr is not null` |
| `src/…/Components/Pages/Kitchen/KitchenBoard.razor` | `data-loaded` beside the live bit and the two §10.3 counts |
| `src/…/Components/Pages/Counter/CounterSitting.razor` | `data-loaded` |
| `src/…/Components/Pages/Table/TableOrderSurface.razor` | `data-loaded` |
| `src/…/Components/Pages/Counter/CounterBoard.razor` | **doc comment only.** The "only one of the five" sentence is retired and its wrongness recorded where it stood |
| `tests/…/Harness/DisplayJourneys.cs` | selector demands both; the failure message distinguishes *no circuit* from *"Preparing the join code…"* |
| `tests/…/Harness/KitchenJourneys.cs` | selector demands both; describe and message updated |
| `tests/…/Harness/CounterJourneys.cs` | the sitting selector demands both; type-level remarks record what F-44 deferred |
| `tests/…/Harness/TableOrderJourneys.cs` | selector demands both; describe and message updated |
| `tests/MyRestaurant.WebApplication.Tests/Components/LiveSurfaceContractTests.cs` | **NEW.** Seven assertions, subject derived from the routing rule |
| `tests/MyRestaurant.WebApplication.Tests/Documentation/SpecificationVersionTests.cs` | **NEW.** Two assertions — F-48 |
| `docs/TECHNICAL_SPECIFICATION.md` | **v1.8**: **§11.10** new and normative, §16.4 gains both tests, Appendix A gains **F-47** and **F-48**, and the header now says what the changelog says |
| `docs/DOCUMENTATION_REVIEW.md` | Group E gains both rows; *Going forward* gains the habit both of them earned |
| `docs/BUILD_PROGRESS.md` | **Full file, 465 KiB.** Slice 23 appended. Nothing to run |
| `_CHANGES.md` | this file |

`docs/REQUIREMENTS.md` is deliberately untouched, on the v1.2 reasoning: §11.5 has said since v1.0 that
a frozen screen must not masquerade as a live one, so this is that contract stated once for every
surface instead of five times for four of them — a mechanism catching up with intent, not new intent.

`Home.razor` is deliberately untouched: it is interactive and it has no render in which it is
incomplete, so `data-loaded` would be `true` from the first paint and would assert nothing. It is in the
contract test's expected set anyway, because the set is what the rule produces; whether a member owes
the bits is a separate question.

`wwwroot/js/display.js` is deliberately untouched: its staleness curtain keys on `data-refresh-token`
and covers a circuit that died *later*. These bits cover one that never lived. Two mechanisms, two
failure modes, no overlap.

## Build and test

```bash
bash scripts/check_tree.sh
#    expect: 5 gates, "tree hygiene passed.", exit 0, and the header reading
#    "checking 315 authored text file(s) of 425 tracked". Two new .cs files enter scope. If it says
#    313, the `git add` did not happen.

bash scripts/check_repository.sh --offline
#    expect: 3 gates plus a SKIP, exit 0. Unchanged — no tracked file gained a platform-state claim.

bash scripts/check_repository.sh
#    expect: 4 gates, exit 0, "passed, with 1 advisory warning" — the wiki, as before.

bash scripts/ci_local.sh --with-all
#    expect: 8 numbered gates, same number and same order as Slice 22.

dotnet test
#    expect: 1005 total, 0 failed. Was 996. Nine new tests: seven in LiveSurfaceContractTests, two in
#    SpecificationVersionTests. No existing test is edited, so any other movement is not this slice.

MYRESTAURANT_E2E=1 dotnet test tests/MyRestaurant.EndToEnd.Tests
#    expect: 15 passed, 0 skipped. THIS IS THE ONE WORTH WATCHING — the four barriers are strictly
#    stricter than they were, and a barrier that now waits longer is the intended change. A barrier
#    that now times out is a real finding; see below.
```

`dotnet build` on its own is not in the list, but the two new files are the first C# this slice adds, so
a plain `dotnet build` is a cheap first check that they compile before the test run.

`podman build` is not in the list: nothing in the build context changed.

## Where to look if this breaks

**`LiveSurfaceContractTests` fails at `TheScanReadsTheTree…` with a set mismatch.** Somebody added a
routable page, or removed `[ExcludeFromInteractiveRouting]` from one. That is the speed bump working:
add the name to `ExpectedInteractiveComponents` and, in the same edit, decide whether the new surface
has a loading state and therefore owes the two bits. The list is not the rule — it is compared against
what the rule produces, so it can only pass by agreeing with it.

**`LiveSurfaceContractTests` throws "walked up … without finding MyRestaurant.slnx".** The test reads
the Razor sources off disk and locates them the way `WebApplicationLocator` does. It fails rather than
skips on purpose. If you are running tests from somewhere detached from the tree, that is the cause.

**An E2E scenario now times out on a barrier.** Read the message: every one of the four now names which
bit was missing. `data-live='false'` is the old meaning, no circuit. `data-loaded='false'` is new and
means the circuit is up and the surface has not finished — on `/display/{table}` specifically it means
§4.3 returned no code for that table, so look at the join secret and at whether the row is still active.
A barrier that hangs where nothing hung before is worth reporting rather than loosening; these
selectors are strictly stricter, so they cannot fail on anything that was previously correct.

**`SpecificationVersionTests` fails.** Something bumped one of the two version fields in
`docs/TECHNICAL_SPECIFICATION.md` and not the other. That is the whole point of it.

**Test count is not 1005.** Nine tests were added and none removed or edited. If the total moved by some
other amount, look at what changed between your last run and this one rather than at this slice.

**A Razor file will not compile.** The likeliest site by far, as always. Five of the six edits are one
attribute in the markup and one property in the `@code` block; `TableDisplay` is the only one with new
statements, four `_loaded = true;` assignments on the paths out of `OnInitializedAsync`. Balance was
checked before and after on every file and is structurally identical, but no SDK ran here.

## What was actually verified here

No .NET SDK in the sandbox, so nothing was reasoned about that could be executed instead:

- **The interactive set was computed rather than read.** A faithful implementation of the test's own
  scan was run over the tree: 48 `.razor` files, 32 statically routed pages, one island
  (`TableOrderSurface`), and an interactive set of exactly the seven expected names.
- **All nine new assertions were executed against the delivered tree** and pass.
- **Both gates were proven sensitive against the Slice 22 tree.** The contract test fails there on
  `TableArea` (no live bit); on `CounterSitting`, `KitchenBoard`, `TableOrderSurface` and `TableArea`
  (no loaded bit); and on `TableDisplay` by counts — `2` live against `0` loaded, which is the only
  assertion that catches a surface with no `_loaded` field at all. The version test fails there with
  1.6 against 1.7.
- **Splitter fidelity was checked against the dump's own byte counts** before anything was edited — the
  F-40 lesson, applied to the transport rather than assumed. Thirteen files compared; two
  (`CounterBoard.razor`, `CounterJourneys.cs`) came back one byte short because their trailing blank
  line had been collapsed, and both were restored before packaging. Every delivered file that existed
  before now matches its dump size plus exactly the edits described above.
- **Every edit applied by exact-match replacement with an assertion that the anchor occurs exactly
  once**, so nothing was edited by position. `docs/BUILD_PROGRESS.md` is its existing bytes plus one
  appended section; it was not regenerated.
- **Markup balance checked before and after on every edited Razor file.** The two files a crude
  tag-matcher reports on report identically *before* the slice — `ILogger<TableDisplay>` and
  `IReadOnlyList<OrderLineView>` read as tags — which is what makes them generic-type false positives
  rather than findings.
- **`.editorconfig` hygiene checked on every delivered file**: LF endings, final newline, no
  whitespace-only lines, no trailing whitespace, no context-dump separator.
- No shell script, workflow or project file changed, so `bash -n`, shellcheck and the XML/YAML gates
  have nothing new to look at.

## Still not a file, and still yours

The tag, unchanged from Slice 22's note. `release.yml` has still never executed; a `workflow_dispatch`
off `main` publishes only `ghcr.io/kusl/myrestaurant:sha-<commit>` and skips the release-notes job, so
it rehearses the path without spending a version number. Still a suggestion, still not in this archive.
