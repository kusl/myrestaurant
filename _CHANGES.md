# M6 Slice 21 — two green gates that were never green

Every file below is a **full file** at its **repo-relative path**. Extract at the repository root and
the contents drop straight over your working tree — no diffs, no patches, no scripts that edit your
code or your documents.

```bash
tar -xzf m6-slice-21-ci-engine-and-board-barrier.tar.gz -C /home/kushal/src/dotnet/myrestaurant
```

## Files in this delivery

| Path | Why |
| --- | --- |
| `scripts/backup.sh` | F-43: engine chosen by the container, `CONTAINER_ENGINE` honoured, the two failure conditions split |
| `scripts/restore_drill.sh` | F-43: `CONTAINER_ENGINE` honoured; the chosen engine reported in the preamble |
| `.github/workflows/ci.yml` | F-43: `CONTAINER_ENGINE: docker` on the backup and drill steps |
| `src/MyRestaurant.WebApplication/Components/Pages/Counter/CounterBoard.razor` | F-44: publishes `id`, `data-live`, `data-loaded` |
| `tests/MyRestaurant.EndToEnd.Tests/Harness/CounterJourneys.cs` | F-44: board barrier demands both bits; new describer for the failure message |
| `.env.example` | documents `CONTAINER_ENGINE` |
| `docs/OPERATIONS.md` | §6 documents engine selection and the message change |
| `docs/_append/BUILD_PROGRESS-M6-Slice-21.md` | the ledger entry, to append |

## Files to DELETE

**None.** Nothing here renames, supersedes or orphans anything. No migration, no schema change, no
package change, no ADR edit, no `.slnx` edit, no `Program.cs` edit, no `.csproj` edit, no new test
project, no new folder under `tests/`.

`docs/_append/BUILD_PROGRESS-M6-Slice-21.md` is the one file that is meant to stop existing — after
it has been appended, exactly as the eleven before it did. It is a delivery vehicle, not part of the
tree.

## The one append

`docs/BUILD_PROGRESS.md` is 434 KiB and is never regenerated as a whole file, so the ledger entry
arrives as its own file. Merging it is a **plain concatenation of one whole file onto the end of
another** — nothing is rewritten, substituted or matched inside either document:

```bash
cat docs/_append/BUILD_PROGRESS-M6-Slice-21.md >> docs/BUILD_PROGRESS.md
rm docs/_append/BUILD_PROGRESS-M6-Slice-21.md
```

The append file opens with a blank line so the heading lands with a blank line above it whatever the
last byte of `BUILD_PROGRESS.md` is.

## Build and test checklist

```bash
bash scripts/check_tree.sh
#    5 gates, "tree hygiene passed.", exit 0. The two edited scripts and the edited Razor file are
#    already in scope; no new authored file joins it. Run this BEFORE the append and again after —
#    a mid-document separator is exactly what this gate exists for, and appending is when one
#    would arrive.

bash scripts/ci_local.sh
#    8 numbered gates, green. Gate 3 is the one this slice can break: both edited scripts must pass
#    bash -n and shellcheck --severity=warning. Both were checked at --severity=style here and are
#    clean, which is one notch stricter than the blocking gate.

dotnet build MyRestaurant.slnx -c Release -p:ContinuousIntegrationBuild=true
#    all seven projects, 0 errors.

dotnet test
#    996 total, 0 failed, 981 succeeded, 15 skipped — UNCHANGED from Slice 20.

MYRESTAURANT_E2E=1 dotnet test tests/MyRestaurant.EndToEnd.Tests
#    15 passed, 0 skipped.
```

Then push, because the two failures this slice fixes are only observable on a runner.

## What each fix actually turns on

**F-43, `boot-smoke`.** The runner image installs a static podman bundle next to Docker, so
`command -v podman` succeeds on it, and every container in that job belongs to the Docker daemon.
`podman exec <a docker container id>` fails with "no such container", and `backup.sh` had one message
for every way that command could fail — so it reported a database that would not answer for its own
credentials, two steps after `/healthz/ready` had answered 200 from an application talking to that
database.

The job now pins `CONTAINER_ENGINE: docker` (the obvious half) and the script now asks each available
engine whether it can see the container it was told to dump (the half that helps a future host with
both engines). Ambiguity on discovery is still fatal — F-38's rule is untouched.

**F-44, `end-to-end`.** `WaitForBoardAsync` waited on `section.counter-board`, which is present in
every state of the component including "Loading the floor…", so the wait was satisfied by the first
paint. Prerendering emits fully loaded markup, which is why this passed locally; the window is the
circuit hand-over, when the component is rebuilt from nothing and the DOM goes back to loading for as
long as two queries take. `ReadFloorAsync` landed there and read two empty lists — which also made
the `Assert.DoesNotContain` on the line above pass, for the wrong reason, off the same empty screen.

## Where to look if this breaks

| Symptom | Look at |
| --- | --- |
| `boot-smoke` still fails at the backup step | the step's `env:` block in `ci.yml` — `CONTAINER_ENGINE: docker` must be present alongside `POSTGRES_CONTAINER`. The new log line names the engine it used; if it says `podman`, the variable did not arrive. |
| `[backup] error: no available engine has a container '…'` | the id in `POSTGRES_CONTAINER` is stale or the service container is gone. This message replaced the misleading `pg_isready` one; it means engine selection worked and found nothing to select. |
| `[backup] error: <engine> knows '…' but it is not running` | the container exited. Previously indistinguishable from wrong credentials. |
| the drill pulls `postgres:17-alpine` on every run | `CONTAINER_ENGINE` is missing from the *drill* step too; it creates its own container and cannot infer an engine. |
| `The counter board was not live and loaded within 30s (data-live='false', …)` | the circuit never started. Check `/_framework/blazor.web.js` — this is M6 Slice 4's static-web-assets manifest failure, not this one. |
| `… (data-loaded='false')` | `ListOpenSittingsAsync` / `ListRecentlyClosedSittingsAsync` are not returning. A database problem, reported as one. |
| `… (there is no counter board on the page at all)` | §3.7 refused the principal, or the route did not resolve. The surface is absent rather than inert. |
| `CS0103: RendererInfo` in `CounterBoard.razor` | it is a `ComponentBase` property, no `@using` needed — the same call exists in `KitchenBoard.razor`, `CounterSitting.razor`, `TableOrderSurface.razor` and `TableDisplay.razor`. If it fails here it would fail in all five. |
| any scenario that used to find a table on the board now cannot | `OpenSittingSelector` and `SettledRowSelector` still key on `section.counter-board`, and that class is unchanged — only an `id` and two `data-` attributes were added. |

## Deliberately not done

`data-loaded` on `KitchenBoard`, `CounterSitting`, `TableOrderSurface` and `TableDisplay`. All four
publish `data-live` and none publishes a loaded bit, so all four carry the same latent race; they pass
because their callers go on to wait for specific content, which waits out the reload incidentally. That
is recorded in the ledger entry rather than fixed here — four surfaces is roughly 4,000 lines of Razor
edited against a race none of them is currently losing, in the same delivery as two that are.

`scripts/check_repository.sh` is untouched, `docs/llm/` stays exempt.
