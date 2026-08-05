
## M6 Slice 21 — two green gates that were never green

Slice 20 closed on a table of results in which everything passed. Two of those rows were reporting on a
workstation, and the two CI jobs that have no workstation equivalent — `boot-smoke`, which boots the
production image, and `end-to-end`, which drives Chromium — were red on the same push. They stayed red.
Neither failure was in the thing its message named.

| Job | What it said | Where the defect was |
| --- | --- | --- |
| `boot smoke (container image)` | the database did not answer `pg_isready` | which container engine the script chose (**F-43**) |
| `end to end (Playwright)` | `Assert.Single()`: the collection was empty | when the harness was allowed to read the screen (**F-44**) |

Both are the same shape at one remove: a check that passed for years on one machine and could not pass on
another, because what it actually asserted was narrower than what it appeared to assert.

### F-43 — the engine the container did not belong to

`scripts/backup.sh` and `scripts/restore_drill.sh` both opened with the same four lines:

```bash
if command -v podman >/dev/null 2>&1; then
    ENGINE="podman"
elif command -v docker >/dev/null 2>&1; then
    ENGINE="docker"
```

podman first because ADR-0004 makes rootless Podman canonical, which is right — and on a host with one
engine the block is also correct, which is why it survived. A GitHub Actions `ubuntu-24.04` runner is not
that host. Its image runs `scripts/build/install-container-tools.sh`, which installs a pinned static
podman bundle into `/usr/local/bin` alongside buildah and skopeo, next to a working Docker daemon. So
`command -v podman` succeeds there, and every container in the `boot-smoke` job — the service database,
the image under test — belongs to Docker.

What the job printed:

```
[backup] using POSTGRES_CONTAINER='f35d0ec9fc2d3f…'.
[backup] error: 'f35d0ec9fc2d3f…' did not answer pg_isready for user 'myrestaurant' database 'myrestaurant'.
```

`podman exec <a docker container id>` fails with "no such container". The script had one message for
every way that command could fail, so a fault entirely in engine selection was reported as a database
that would not answer for its own credentials — and reported it two steps after `/healthz/ready` had
returned 200 from an application talking to that very database. **A diagnostic that names the wrong
subsystem is worse than no diagnostic**, because it is followed.

Three changes, and the order matters:

1. **`CONTAINER_ENGINE`** is honoured by both scripts and set to `docker` on the two `boot-smoke` steps.
   Explicit, visible in the job, and the half a reader will find first.
2. **The container chooses the engine.** When `POSTGRES_CONTAINER` is set, `backup.sh` asks each
   available engine `container inspect` and uses the one that answers. This is not a guess dressed up as
   a heuristic: the only reason the script needs an engine is to reach one named container, and whether a
   given engine can see that container is a fact. `container inspect` rather than `ps --filter name=`
   because CI passes an id, and `ps` filters do not match ids. Discovery without a name works the same
   way, per engine, and still **refuses** on ambiguity rather than picking — F-38's rule, unchanged.
3. **The two conditions are said separately.** "Knows it but it is not running" and "did not answer
   `pg_isready` for these credentials" are fixed in different places, so they are now different lines,
   and both name the engine they were asked of.

`restore_drill.sh` gets the variable but not the inference, and the asymmetry is the point: the drill
creates its own scratch container, so there is nothing to infer from. On the runner that also costs a
second pull of `postgres:17-alpine` into podman's store while Docker already holds it — slow rather than
wrong, and one more reason the job pins the engine.

### F-44 — a barrier that was satisfied by the first paint

§16.3 scenario 10 closes a sitting, walks back to `/counter`, and asserts the table left the floor and
appeared under "Settled today". It failed on the second half:

```
Assert.Single() Failure: The collection did not contain any matching items
Collection: []
```

`ListRecentlyClosedSittingsAsync` filters `closed_at >= now − 12h` against a sitting closed seconds
earlier, so the query was never in question. The harness was reading the board before either list
existed.

`WaitForBoardAsync` waited on `section.counter-board` becoming visible. **That element is present in
every state of the component**, including:

```razor
@if (!_loaded)
{
    <p class="lede">Loading the floor…</p>
}
```

So the wait was satisfied by the first paint and asserted nothing at all. And the state it failed to
wait past is not the prerender — prerendering runs the whole lifecycle before it emits, so the first HTML
a browser receives is fully loaded, which is exactly why this passed locally for as long as it did. It is
the **hand-over**. `blazor.web.js` opens the circuit, the component is constructed again from nothing,
`ComponentBase` renders the moment `OnInitializedAsync` yields, and the DOM returns to "Loading the
floor…" for as long as two queries take. Milliseconds on a workstation. Long enough on a loaded runner.

The failure is quiet in a way worth naming. The assertion one line above the failing one is

```csharp
Assert.DoesNotContain(tableLabel, floor.OpenTableLabels);
```

and an empty list satisfies it. So the reading that produced the failure had already produced a pass, for
the wrong reason, out of the same empty screen.

`CounterBoard.razor` now publishes what the other four live surfaces publish, plus one more bit:

| Attribute | From | Says |
| --- | --- | --- |
| `data-live` | `RendererInfo.IsInteractive` | a circuit produced this markup |
| `data-loaded` | `_loaded` | §11.3's two queries have answered |

and `BoardSurfaceSelector` demands both. **Either alone is wrong, in opposite directions.** `data-live`
by itself steers a reader *to* the circuit's first render — the one instant when neither list is in the
document — so it would have made this worse rather than better. `data-loaded` by itself matches the
prerendered markup, which is loaded and inert: correct as of the request and never again, on the one
screen in the application whose entire purpose is a number that moves while somebody stands reading it.

Until now `/counter` was also the only one of the five live surfaces that published nothing, so nothing
anywhere asserted a circuit was behind it.

### What is deliberately not in this slice

**`data-loaded` on the other four surfaces.** `KitchenBoard`, `CounterSitting`, `TableOrderSurface` and
`TableDisplay` all publish `data-live` and none publishes a loaded bit, so all four carry the same latent
race. They pass today because their callers go on to wait for specific content — a bill line, a badge, a
menu item — and that wait incidentally waits out the reload. The board is where it bites because
`ReadFloorAsync` asks about membership of a list, and absence is indistinguishable from a list that has
not rendered.

That is a real finding, and it is recorded rather than fixed here: four surfaces is ~4,000 lines of Razor
edited against a race none of them is currently losing, in the same delivery as two failures that are
losing. Scenario 10 is the evidence that the class is real; a scenario that fails is the evidence needed
to justify the other four.

### Build and test

```bash
bash scripts/check_tree.sh
#    expect: 5 gates, "tree hygiene passed.", exit 0. No new authored file lands in its scope —
#    docs/_append/ is a delivery convenience, merged and removed — so the count is unchanged.

bash scripts/ci_local.sh
#    expect: 8 numbered gates, green. Gate 3 (shell scripts) is the one that matters here: both
#    edited scripts must pass bash -n and shellcheck --severity=warning.

dotnet build MyRestaurant.slnx -c Release -p:ContinuousIntegrationBuild=true
#    expect: all seven projects, 0 errors. CounterBoard.razor is the only compiled file that changed
#    on the src side, and Razor is where a delivery like this would break.

dotnet test
#    expect: 996 total, 0 failed, 981 succeeded, 15 skipped. UNCHANGED. No test is added, renamed or
#    moved; the harness edit changes a selector and a message. If this number moves, the cause is
#    not here.

MYRESTAURANT_E2E=1 dotnet test tests/MyRestaurant.EndToEnd.Tests
#    expect: 15 passed, 0 skipped. Counter_ClosesSitting_TableFlipsToSettledAndTotalsMatch is the
#    one that was failing in CI and has never failed locally, so a local pass proves the selector
#    still matches — not that the race is closed. The runner proves that.
```

The drill and the backup cannot be rehearsed against the CI topology from a workstation, so the two
things worth doing locally are the ones that would catch a typo:

```bash
BACKUP_DIRECTORY=/tmp/mr-backup-check bash scripts/backup.sh --no-keys
#    expect: on a dev stack, the postgres container found and named with the engine it was found
#    through — "[backup] using 'myrestaurant_postgres_1', discovered via podman." That line is new
#    and it is the whole of F-43's fix reporting itself.

CONTAINER_ENGINE=nosuchengine bash scripts/backup.sh --no-keys
#    expect: exit 1, "CONTAINER_ENGINE='nosuchengine' is not on PATH." A bad override must fail on
#    the override rather than fall back to guessing, which is the behaviour this slice removed.
```

### Ledger

| Shape | Finding | What was wrong |
| --- | --- | --- |
| a script correct on one host and wrong on another, reporting the wrong subsystem | **F-43** | engine chosen by `PATH` order rather than by the container it had to reach |
| a test barrier satisfied by markup present in every state | **F-44** | `section.counter-board` exists while loading, so the wait asserted nothing |

Both rows belong to a category this ledger has recorded twice before — a check whose *name* was true and
whose *content* was narrower than its name. F-40's separator gate and F-42's governance gate were both
about layers nothing then looked at. These two are about looking at the right layer and asking it the
wrong question, which is harder to notice because the gate is green.
