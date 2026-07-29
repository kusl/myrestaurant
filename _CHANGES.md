# M6 Slice 1 — continuous integration: the machine that compiles what I cannot

Every file below is a **full file** at its **repo-relative path**. Extract this archive at the repo root
and the contents drop straight over your working tree. `git status` will show exactly these 10 files as
modified/added, and **no deletions**.

```bash
tar -xzf m6-slice1-continuous-integration.tar.gz -C /home/kushal/src/dotnet/myrestaurant
```

## Files to DELETE

**None.** Nothing is renamed and nothing is superseded. No migration ships, no package changes, no schema
change, and nothing in `src/` is touched at all.

## Before anything else: two corrections to my notes

**M5 is closed and the tree is green.** Your last sweep reported `total: 934, failed: 0, succeeded: 919,
skipped: 15`, a clean `run.sh --smoke`, and a healthy `--containers-only`. §19's M5 line is fully
discharged.

**The `/account/enroll-totp` 500 appears to be resolved.** `EnrollTotp.razor` is in the tree, the suite is
green, and nothing in the terminal log reproduces it. I have treated it as closed rather than re-opening a
diagnosis with no symptom. If it is still there, say so and it becomes the next slice.

## What this closes

§19's M6 line reads "full E2E suite (§16.3), backups + restore drill, cloudflared production profile +
tunnel docs, quick-tunnel demo script with warning, OPERATIONS runbooks, **CI pipeline**". This is the
emphasised phrase. §16.4 states it in one clause: "GitHub Actions — build, unit, integration (service
container PostgreSQL), E2E (compose), publish image on tag."

Three of that line's six items — the cloudflared production profile, the quick-tunnel script, and the
OPERATIONS runbooks — were delivered inside earlier milestones. After this slice, M6 is down to **two**:
the E2E harness and an executable backup/restore drill.

## Why CI first, and not the E2E suite

§19 lists the E2E matrix first, and every milestone so far has walked its line roughly in order. This one
does not, for a reason specific to how this repository gets built.

Every slice from M1 to M5 was written without a compiler. Each went out on a "where to look if the build
breaks" note and was compiled for the first time on your workstation. That loop works — 934 facts say so —
but it has exactly one participant, it runs only when you run it, and its results live in a terminal
scrollback that has to be pasted into a conversation before anyone else can see them.

The E2E matrix does not change that. It adds fifteen facts to the same loop. CI moves the loop off one
machine: a push compiles the tree, runs all 934 facts against real PostgreSQL, and boots the actual
production image — on every commit, without anybody remembering to ask.

The narrower reason: `boot-smoke` below is the gate that catches "the app starts but one route throws",
which is precisely what cost a slice's worth of back-and-forth at the end of M2, diagnosed by reading
container logs by hand. That gate is worth more, sooner, than any single E2E scenario.

## New files (5)

### Pipelines (3)

- `.github/workflows/ci.yml` — three jobs, described below. Also declares `workflow_call:` so the release
  pipeline can reuse it.
- `.github/workflows/release.yml` — on a `v*` tag: calls `ci.yml`, then publishes to
  `ghcr.io/kusl/myrestaurant`.
- `.github/dependabot.yml` — weekly NuGet (via `Directory.Packages.props`) and `github-actions` updates,
  with OpenTelemetry, xunit and Npgsql grouped so lockstep families arrive as one pull request.

### Tooling (1)

- `scripts/ci_local.sh` — the same gates, locally, in the same order and with the same flags.
  `--with-smoke` appends `./run.sh --smoke`. Clean under `bash -n` and `shellcheck --severity=style` as
  delivered.

### Docs (1, append-then-keep)

`docs/BUILD_PROGRESS.md` is large and is not regenerated. The new section ships as
`docs/_append/BUILD_PROGRESS-m6-slice-1.md`, matching the sections already in that folder.

## Edited (5)

- `Directory.Build.props` — `TreatWarningsAsErrors` becomes `true` under `ContinuousIntegrationBuild`, with
  `WarningsNotAsErrors` exempting NU1901-NU1904. See "the note finally discharged" below.
- `tests/MyRestaurant.DataAccess.Tests/Sittings/SittingRecordReadsTests.cs` — **one line**, at 354.
- `README.md` — status (M1-M5 closed, M6 in progress), a new *Continuous integration* section, a CI badge,
  the SDK pin corrected, and four stale caveats retired.
- `docs/OPERATIONS.md` — new §14: what CI checks, how to cut a release, how to deploy from the registry
  instead of building on the box, and how to verify what is actually running.
- `_CHANGES.md` (this file)

No `docs/TECHNICAL_SPECIFICATION.md`, `docs/REQUIREMENTS.md`, `docs/DOCUMENTATION_REVIEW.md`, or ADR edit:
this realizes behaviour §16.4 already specifies, in the words it already uses.

One deviation from the spec's phrasing, recorded in the BUILD_PROGRESS section rather than by editing §16.4:
that clause says "integration (service container PostgreSQL)", written before Testcontainers landed in M1.
The integration tests bring up their own PostgreSQL 17 container and need no service container. `boot-smoke`
is where a service container actually earns its place.

## The three gates

**`shell-scripts`** — every tracked `*.sh` parses under `bash -n` and passes shellcheck. Not a new standard:
this is the project's existing pre-delivery discipline, moved out of an instruction and into the tree. All
five existing scripts plus this slice's new one are clean at shellcheck's strictest practical level,
`--severity=style`.

The gate nonetheless *blocks* at `--severity=warning`, one notch lower, with `style` running right after as
`continue-on-error: true`. That is about drift, not doubt: `ubuntu-latest`'s shellcheck moves on its own
schedule, and a new style check would turn the build red on a day nobody touched the repository. A red build
with no commit behind it is how people learn to stop reading the build.

**`build-and-test`** — restore, a Release build with warnings as errors, then the whole suite. Two details.

The SDK is requested as `10.0.x` rather than `global.json`'s exact `10.0.100`, because that pin carries
`rollForward: latestMinor` — any 10.0 SDK satisfies it, and asking a runner for an exact patch that has
since been delisted is a way to fail that has nothing to do with the code.

And the data-access tests *run* here. On your Fedora box, Testcontainers needs the rootless Podman user
socket activated by hand or roughly 190 integration facts skip — quietly, with a green summary. A GitHub
runner has `/var/run/docker.sock`, so `ContainerEngineDiscovery` returns early and those facts execute on
every push whether or not anybody remembered `systemctl --user enable --now podman.socket`. That is a real
change in what "the tests pass" means.

**`boot-smoke`** — the `Containerfile` is built and the resulting image is started against a real
PostgreSQL, and `/healthz/ready` must answer 200 within 180 seconds.

Nothing else in the suite covers this. `/healthz/ready` returns 200 only after DbUp has applied every
migration and reported the schema current, and it can only be *reached* if the composition root resolved —
meaning every `Add…()` in `Program.cs` produced a satisfiable graph. A missing registration, a migration
that parses in a test but conflicts against a fresh database, a `RestaurantOptions.Validate()` rejection
from a new setting with no default: each is invisible to unit tests, each kills a deployment, and each now
fails a pull request instead of a service.

## Why CI does not use `compose.yaml`

The obvious implementation of `boot-smoke` is `docker compose --profile dev up --build`, and it does not
work. `compose.yaml` mounts the data-protection volume as `…/dataprotection:U` — the `:U` asks Podman to
chown the volume to the container user, correct for the canonical rootless engine (ADR-0004) and rejected by
Docker Compose as an invalid mount mode. The file's own comment already says so.

Editing `compose.yaml` to suit CI would mean changing the canonical dev stack to accommodate the one
environment that is not the target; shipping a `compose.ci.yaml` would mean a second file that can drift
from the first. A job-level `services:` PostgreSQL plus one `docker run --network host` is the same topology
— same image, same environment variables, same readiness probe — and leaves the canonical stack the only
compose file in the tree.

`--network host` is what keeps it to one command: the container reaches the service database on the
runner's published `127.0.0.1:5432` and publishes its own `8080` back for the probe.

## `TreatWarningsAsErrors` — the note finally discharged

`Directory.Build.props` has carried this since M1:

> Warnings are NOT errors yet: a fresh clone must build through analyzer drift on a newer SDK. Flip to true
> once the first green build is established (BUILD_PROGRESS).

The first green build was established four milestones ago; the note stayed because both halves are true at
once. Warnings *should* be errors — the alternative is a log nobody reads. And a fresh clone on tomorrow's
SDK *should not* refuse at the door over an analyzer that did not exist when the code was written.

Conditioning on `ContinuousIntegrationBuild` resolves it without picking a side. CI passes
`-p:ContinuousIntegrationBuild=true`, which is where strictness belongs — a machine, with a commit to point
at, on a tree somebody just changed. `dotnet build` on your workstation stays lenient. The property is
MSBuild's own, and setting it also makes the build deterministic, which is what it is for.

`NU1901`-`NU1904` stay warnings even under CI. Those are NuGet audit findings, raised when an advisory is
published against a package this tree already depends on — real news, but news that arrives without a
commit. It surfaces in a `continue-on-error` step running `dotnet list package --vulnerable
--include-transitive` instead of turning a pull request red for something the pull request did not do.

**The one warning in the tree had to go first.** `SittingRecordReadsTests.cs:354` was:

```csharp
Assert.Single(record.Events.Where(stored => stored.EventType == "fulfillment"));
```

which xUnit's own analyzer flags as **xUnit2031** — "do not use a Where clause to filter before calling
`Assert.Single`". It is now:

```csharp
Assert.Single(record.Events, stored => stored.EventType == "fulfillment");
```

the overload the analyzer recommends. I verified the signature against `xunit/assert.xunit`:
`public static T Single<T>(IEnumerable<T> collection, Predicate<T> predicate)`. Same assertion, better
failure message (the predicate overload reports how many matched and where), and it was the only line in
the repository standing between the tree and a strict build.

## `release.yml` calls `ci.yml` rather than repeating it

A publish pipeline that does not verify ships an image nobody tested; one that verifies with its own copy of
the gates is two sets of gates to keep identical. So `ci.yml` declares `workflow_call:` alongside its push
and pull-request triggers, and `release.yml`'s first job is `uses: ./.github/workflows/ci.yml`.

Tags land as `:<version>`, `:<major>.<minor>` and `:sha-<commit>`. There is no `latest`, on purpose — a tag
that silently changes what it points at is the reason people cannot answer "what is running".
`concurrency.cancel-in-progress` is `false` here, the opposite of `ci.yml`: a superseded push should be
abandoned, but a half-pushed manifest is worse than a slow one.

**`linux/amd64` only, deliberately.** The `Containerfile` runs a full `dotnet publish` in its build stage,
and doing that for arm64 through QEMU is slow enough to risk the job timeout. The right fix is not a longer
timeout — it is a cross-compiled publish (`-r linux-arm64` from an amd64 SDK), which is a `Containerfile`
change rather than a workflow line. Noted in OPERATIONS §14 for an SBC deployment.

## What I verified rather than guessed

Since a wrong action version fails the whole pipeline on the first run with "Unable to resolve action", none
of these were recalled from memory:

- **Action majors**, resolved through `github.com/<repo>/releases/latest` redirects at authoring time:
  `actions/checkout@v7` (v7.0.1), `actions/setup-dotnet@v6` (v6.0.0), `actions/upload-artifact@v7` (v7.0.1),
  `actions/cache@v6` (v6.1.0), `docker/metadata-action@v6` (v6.2.0), `docker/setup-buildx-action@v4`
  (v4.2.0), `docker/login-action@v4` (v4.5.2), `docker/build-push-action@v7` (v7.3.0).
- **`setup-dotnet` v6's inputs**, read from its `action.yml` — `dotnet-version` and `global-json-file` are
  both still there, and its `cache` input needs `packages.lock.json` files this repo does not have, which is
  why NuGet caching goes through `actions/cache` keyed on `Directory.Packages.props` + `global.json` +
  `**/*.csproj` instead.
- **shellcheck 0.11.0 against all five existing scripts** — clean at `--severity=style`, which is what made
  a `warning`-level blocking gate a safe thing to ship rather than a hope.
- **`Assert.Single`'s predicate overload**, read from `xunit/assert.xunit` source.
- **All three YAML files parse**, and `scripts/ci_local.sh` passes `bash -n` plus shellcheck at every
  severity; I also exercised its `--help` and bad-argument paths.

One thing I could not verify: whether a Release build with warnings-as-errors on the runner's SDK surfaces
warnings your Debug builds never showed. That is the gate doing its job rather than a defect — see the
checklist.

## Where to look if this breaks

Unusually for this project: **not the code**. Nothing in `src/` changed, and the only test-project change is
one line inside one assertion.

1. **`build-and-test` fails on a warning.** That is the gate working; the log names the file, line and code.
   Fix it, or add the code to `WarningsNotAsErrors` with a comment saying why. This can happen on a commit
   that touched nothing relevant, because the runner's SDK is newer than your 10.0.110 — the tradeoff the
   CI-only condition accepts.
2. **`boot-smoke` fails at "the web container exited before it became ready".** Read the `container logs`
   step, which runs on failure. Three candidates in order of likelihood: a migration that conflicts against
   a genuinely empty database, a `RestaurantOptions.Validate()` rejection, a DI registration added without
   its dependency.
3. **`shell-scripts` fails.** Reproduce exactly with `scripts/ci_local.sh` — same two passes, same two
   severities.
4. **`release.yml` cannot push.** `packages: write` is declared on the `image` job. The first push to a new
   GHCR package also needs the repository allowed to create it — a one-time settings step, not a workflow
   bug.

## Build/test checklist for this slice

1. `dotnet restore` — no new packages, no migration, no schema change.
2. `dotnet build` — unchanged from the last green sweep; no Razor page was touched.
3. `dotnet test` — **still 934 total, 919 passing, 15 skipped.** The xUnit2031 fix changes an assertion's
   form, not its meaning or its count. If the total moved, something else moved with it.
4. `bash scripts/ci_local.sh` — the real check for this slice, because it is the first thing in this
   repository's history to run the strict build. Then `bash scripts/ci_local.sh --with-smoke` once.
5. `git add .github scripts/ci_local.sh && git commit && git push` — then watch the Actions tab, which is
   the actual deliverable. Expect roughly: `shell-scripts` under a minute, `build-and-test` five to eight
   (the first run has no NuGet cache), `boot-smoke` four to seven.
6. Worth doing once while the pipeline is fresh, to prove the release path before it matters:
   `git tag --annotate v0.6.0 --message 'M6 slice 1' && git push origin v0.6.0`.

## Housekeeping carried over

`docs/BUILD_PROGRESS.md` still jumps from "M4 Slice 1" to "M5 Slice 2". Seven appends are now unmerged in
`docs/_append/`, including this slice's:

```bash
cat docs/_append/BUILD_PROGRESS-m4-slice-2.md >> docs/BUILD_PROGRESS.md
cat docs/_append/BUILD_PROGRESS-m4-slice-3.md >> docs/BUILD_PROGRESS.md
cat docs/_append/BUILD_PROGRESS-m4-slice-4.md >> docs/BUILD_PROGRESS.md
cat docs/_append/BUILD_PROGRESS-m5-slice-1.md >> docs/BUILD_PROGRESS.md
cat docs/_append/BUILD_PROGRESS-m5-slice-3.md >> docs/BUILD_PROGRESS.md
cat docs/_append/BUILD_PROGRESS-m5-slice-4.md >> docs/BUILD_PROGRESS.md
cat docs/_append/BUILD_PROGRESS-m5-slice-5.md >> docs/BUILD_PROGRESS.md
cat docs/_append/BUILD_PROGRESS-m6-slice-1.md >> docs/BUILD_PROGRESS.md
```

## What is next

The E2E harness. The fifteen §16.3 scenarios have been version-controlled as skipped placeholders since M1
and none of them can run, because there is no fixture that brings up a stack, no browser context, no
virtual authenticator and no controllable clock. That plumbing is one slice; scenarios 1, 13 and 14 come
with it, because the passkey and token-window machinery they need is what every other scenario is waiting
on.

## The one-line why

For five milestones the only thing standing between a slice I could not compile and a working restaurant was
you, at a terminal, at night — and that was never a system, it was a person being reliable.
