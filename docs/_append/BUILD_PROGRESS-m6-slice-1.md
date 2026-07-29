### M6 Slice 1 — continuous integration: the machine that compiles what I cannot (landed)

M6's build-order line (§19) reads "full E2E suite (§16.3), backups + restore drill, cloudflared production
profile + tunnel docs, quick-tunnel demo script with warning, OPERATIONS runbooks, **CI pipeline**". This
slice is the emphasised phrase, taken out of order on purpose — see below. §16.4 states it in one clause:
"GitHub Actions — build, unit, integration (service container PostgreSQL), E2E (compose), publish image on
tag."

No migration, no packages, no schema change, no new C# file, and nothing deleted. One analyzer warning is
fixed and `Directory.Build.props` finally answers a question it has carried since M1.

---

#### Why this is M6 Slice 1 rather than M6 Slice 4

§19 lists the E2E suite first, and every previous milestone has walked its line roughly in order. This one
does not, for a reason that is specific to how this repository gets built.

Every slice from M1 to M5 was written without a compiler. The authoring environment has no .NET SDK and no
package feed, so each slice's Razor pages and SQL went out on a "where to look if the build breaks" note
and were compiled for the first time on the owner's workstation. That loop worked — 934 facts and a green
sweep say so — but it has exactly one participant, it runs only when he runs it, and its results live in a
terminal scrollback that has to be copied into a conversation before anyone else can see them.

The E2E matrix does not fix that. It adds fifteen more facts to the same loop. CI moves the loop off one
workstation: a push compiles the tree, runs all 934 facts against real PostgreSQL, and boots the actual
production image — and says so in public, on every commit, without anybody remembering to ask. Sequencing
the pipeline ahead of the suite it will eventually run is the difference between hardening the project and
hardening one person's habits.

The second reason is narrower and concrete. `boot-smoke` (below) is the gate that catches "the app starts
but one route throws" — which is precisely the failure mode that cost this project a slice's worth of
back-and-forth at the end of M2, diagnosed by reading container logs by hand. That gate is worth more,
sooner, than any single E2E scenario.

---

#### Three gates, and what each one can prove that the others cannot

**`shell-scripts`.** Every tracked `*.sh` must parse under `bash -n` and pass shellcheck. This is not a new
standard — it is the project's existing pre-delivery discipline, which until now lived in an instruction
rather than in the tree. Five scripts (`export.sh`, `run.sh`, `scripts/backup.sh`, `scripts/quick_tunnel.sh`,
`scripts/restore.sh`) plus this slice's `scripts/ci_local.sh` are clean at shellcheck's *strictest* practical
level, `--severity=style`.

The gate nonetheless blocks at `--severity=warning`, one notch lower, with `style` running immediately after
as `continue-on-error: true`. The reason is drift rather than doubt: `ubuntu-latest`'s shellcheck moves on
its own schedule, and a new style check would turn the build red on a day nobody touched the repository. A
red build with no commit behind it is how people learn to stop reading the build.

**`build-and-test`.** Restore, a **Release** build with warnings escalated to errors, then the whole suite.
Two details matter.

The SDK is requested as `10.0.x`, not as `global.json`'s exact `10.0.100`. `global.json` pins that version
with `rollForward: latestMinor`, so any 10.0 SDK satisfies it; asking a runner for an exact patch that has
since been delisted is a way to fail that has nothing to do with the code.

And the data-access tests *run* here. On the owner's Fedora box, Testcontainers needs the rootless Podman
user socket activated by hand (`systemctl --user enable --now podman.socket`) or roughly 190 integration
facts skip — quietly, with a green summary. A GitHub runner has `/var/run/docker.sock`, so
`ContainerEngineDiscovery` returns early, Testcontainers uses the default endpoint, and those facts execute
on every push whether or not anybody remembered the socket. That is a real change in what "the tests pass"
means.

**`boot-smoke`.** The `Containerfile` is built and the resulting image is started against a real
PostgreSQL, and `/healthz/ready` must answer 200 within 180 seconds.

Nothing else in the suite covers this. `/healthz/ready` returns 200 only after DbUp has applied every
migration and reported the schema current, and it can only be *reached* if the composition root resolved —
which means every `Add…()` extension in `Program.cs` produced a satisfiable graph. A missing registration,
a migration that parses in a test but conflicts against a fresh database, a `RestaurantOptions.Validate()`
rejection from a bad default: each is invisible to unit tests, each kills a production deployment, and each
now fails a pull request instead of a service.

---

#### Why `compose.yaml` is not used in CI

The obvious implementation of `boot-smoke` is `docker compose --profile dev up --build`, and it does not
work. `compose.yaml` mounts the data-protection volume as
`dataprotection-keys:/var/lib/myrestaurant/dataprotection:U` — the `:U` asks Podman to chown the volume to
the container user, which is correct for the canonical rootless engine (ADR-0004) and which Docker Compose
rejects as an invalid mount mode. The file's own comment already says so: "if you are on Docker and it
complains, drop the ':U'".

Editing `compose.yaml` to suit CI would mean changing the canonical dev stack to accommodate the one
environment that is not the target, and shipping a second `compose.ci.yaml` would mean a second file that
can drift from the first. A job-level `services:` PostgreSQL plus one `docker run --network host` is the
same topology — same image, same environment variables, same readiness probe — with none of that argument,
and it leaves the canonical stack the only compose file in the tree.

`--network host` is what makes it one command: the container reaches the service database on the runner's
published `127.0.0.1:5432` and publishes its own `8080` back for the probe, so no user-defined network,
no container-to-container DNS, and no port-mapping arithmetic.

---

#### `TreatWarningsAsErrors`, and a note finally discharged

`Directory.Build.props` has carried this since M1:

> Warnings are NOT errors yet: a fresh clone must build through analyzer drift on a newer SDK. Flip to
> true once the first green build is established (BUILD_PROGRESS).

The first green build was established four milestones ago; the note stayed anyway, because both of its
halves are true at once. Warnings *should* be errors — the alternative is a build log nobody reads. And a
fresh clone on tomorrow's SDK *should not* refuse at the door over an analyzer that did not exist when the
code was written, because a clone that will not build is worse than a warning that was not read.

Conditioning on `ContinuousIntegrationBuild` resolves it without choosing a side. CI passes
`-p:ContinuousIntegrationBuild=true`, which is where strictness belongs — a machine, with a commit to point
at, on a tree somebody just changed. A `dotnet build` on a workstation stays lenient. The property is not
invented for this: it is MSBuild's own, and setting it also makes the build deterministic, which is what it
is for.

`NU1901`–`NU1904` are exempted through `WarningsNotAsErrors` even under CI. Those are NuGet audit findings,
raised when an advisory is published against a package this tree already depends on. That is real news and
worth surfacing, but it arrives without a commit — so it surfaces in a `continue-on-error` step running
`dotnet list package --vulnerable --include-transitive`, and it does not turn a pull request red for
something the pull request did not do.

The one warning in the tree had to go first: `SittingRecordReadsTests.cs:354` used
`Assert.Single(record.Events.Where(…))`, which xUnit's own analyzer flags as **xUnit2031** ("do not use a
Where clause to filter before calling Assert.Single"). It is now
`Assert.Single(record.Events, stored => stored.EventType == "fulfillment")` — the overload the analyzer
recommends, `Assert.Single<T>(IEnumerable<T>, Predicate<T>)`, verified against `xunit/assert.xunit`. Same
assertion, better failure message (the predicate overload reports how many matched and where), and the only
line in the repository standing between the tree and a strict build.

---

#### `release.yml` calls `ci.yml` rather than repeating it

§16.4 asks for "publish image on tag". A publish pipeline that does not first verify is a way to ship an
image nobody tested, and a publish pipeline that verifies with *its own copy* of the gates is two sets of
gates to keep identical. So `ci.yml` declares `workflow_call:` alongside its push and pull-request
triggers, and `release.yml`'s first job is `uses: ./.github/workflows/ci.yml`. A tag runs exactly the gates
a push runs, because they are the same file.

Tags land as `ghcr.io/<owner>/<repository>:<version>`, `:<major>.<minor>`, and `:sha-<commit>`, from
`docker/metadata-action`'s semver patterns. `concurrency.cancel-in-progress` is `false` here — the opposite
of `ci.yml`, where a superseded push should be abandoned. A half-pushed manifest is worse than a slow one.

**`linux/amd64` only, deliberately.** The `Containerfile` runs a full `dotnet publish` inside its build
stage. Doing that for arm64 through QEMU emulation is slow enough to risk the job timeout, and the right
fix is not a longer timeout — it is a cross-compiled publish (`-r linux-arm64` from an amd64 SDK), which is
a `Containerfile` change rather than a workflow line. Noted in `docs/OPERATIONS.md` §14 for whoever wants
to run this on an SBC.

---

#### `scripts/ci_local.sh`

CI's build is strict; a workstation's is not. That makes "it builds here" and "it builds in CI" two
different questions, and nothing in the tree asked the second one. This script asks it: the same shell
lint, the same `dotnet restore`, the same `--configuration Release -p:ContinuousIntegrationBuild=true`
build, the same test invocation, in the same order, with each gate announcing itself so a failure is
attributable at a glance.

`--with-smoke` appends `./run.sh --smoke`. That is the closest local equivalent of the `boot-smoke` job and
not the same thing: same migrations and same readiness probe, but the app runs on the host rather than
inside the image, so it does not prove the `Containerfile` builds. `./run.sh --containers-only` is the real
local analogue; the script says so rather than implying otherwise.

`--help` prints the header comment block by walking it with awk until the first line of code, rather than
by a hard-coded `sed` range — a range is a second place to maintain, and the kind that goes stale silently.

---

#### `dependabot.yml`

Weekly, two ecosystems. NuGet reads `Directory.Packages.props`, so every bump lands in the one file that
pins versions (REQUIREMENTS §2) rather than scattered across csproj files. OpenTelemetry, xunit and Npgsql
are grouped, because those families move in lockstep and six pull requests that only build together are six
red pull requests.

Worth stating because it is a project rule that needs no configuration: Dependabot does not propose a
prerelease for a dependency currently on a stable version. "No prerelease packages" is satisfied by
default.

---

#### Files

**New (5)**

| Path | What |
| --- | --- |
| `.github/workflows/ci.yml` | `shell-scripts`, `build-and-test`, `boot-smoke`; also `workflow_call`-able |
| `.github/workflows/release.yml` | calls `ci.yml`, then pushes to GHCR on `v*` |
| `.github/dependabot.yml` | weekly NuGet + github-actions updates, grouped |
| `scripts/ci_local.sh` | the same gates, locally |
| `docs/_append/BUILD_PROGRESS-m6-slice-1.md` | this section |

**Edited (5)**

| Path | What |
| --- | --- |
| `Directory.Build.props` | `TreatWarningsAsErrors` under `ContinuousIntegrationBuild`; `WarningsNotAsErrors` for NU1901-1904 |
| `tests/…/Sittings/SittingRecordReadsTests.cs` | one line — the xUnit2031 fix at 354 |
| `README.md` | status (M1-M5 closed), a CI section and badge, SDK pin corrected, four stale caveats retired |
| `docs/OPERATIONS.md` | new §14 — CI, cutting a release, deploying from the registry |
| `_CHANGES.md` | this slice's delivery note |

No `docs/TECHNICAL_SPECIFICATION.md`, `docs/REQUIREMENTS.md`, `docs/DOCUMENTATION_REVIEW.md`, or ADR edit:
this realizes behaviour §16.4 already specifies, in the words it already uses. The one substantive
deviation from the spec's phrasing is recorded here rather than there — §16.4 says "integration (service
container PostgreSQL)", written before Testcontainers landed in M1; the integration tests bring up their
own PostgreSQL 17 container and need no service container, and `boot-smoke` is where a service container
actually earns its place.

---

#### Where to look if this breaks

Unusually for this project: **not the code**. Nothing in `src/` changed, and the only test-project change is
one line inside one assertion.

1. **`build-and-test` fails on a warning.** That is the gate working. The build log names the file, the
   line and the code. Fix it, or — if it is genuinely not actionable — add the code to
   `WarningsNotAsErrors` in `Directory.Build.props` with a comment saying why. Note this can happen on a
   commit that touched nothing relevant, because the runner's SDK is newer than 10.0.110; that is the
   tradeoff the CI-only condition accepts.
2. **`boot-smoke` fails at "the web container exited before it became ready".** Read the `container logs`
   step that runs on failure. Three candidates, in order of likelihood: a migration that conflicts against
   a genuinely empty database, a `RestaurantOptions.Validate()` rejection (the job sets `RESTAURANT_*`
   explicitly, so a new required setting with no default would land here), and a DI registration added
   without its dependency.
3. **`shell-scripts` fails.** Reproduce exactly with `scripts/ci_local.sh`, which runs the same two
   shellcheck passes at the same two severities.
4. **`release.yml` cannot push.** `packages: write` is declared on the `image` job. The first push to a new
   GHCR package also needs the repository to be allowed to create it — a one-time settings step, not a
   workflow bug.
5. **An action version is rejected** ("Unable to resolve action"). The pinned majors were resolved at
   authoring time: `checkout@v7`, `setup-dotnet@v6`, `upload-artifact@v7`, `cache@v6`,
   `docker/metadata-action@v6`, `docker/setup-buildx-action@v4`, `docker/login-action@v4`,
   `docker/build-push-action@v7`. Dependabot's `github-actions` ecosystem now keeps them current.

---

#### Build/test checklist for this slice

1. `dotnet restore` — no new packages, no migration, no schema change.
2. `dotnet build` — unchanged from the last green sweep; no Razor page was touched.
3. `dotnet test` — **still 934 total, 919 passing, 15 skipped.** The xUnit2031 fix changes an assertion's
   form, not its meaning or its count. If the total moved, something else moved with it.
4. `bash -n scripts/ci_local.sh && shellcheck --severity=style scripts/ci_local.sh` — both clean as
   delivered.
5. `bash scripts/ci_local.sh` — the real check for this slice. It must pass, including the strict build.
   Then `bash scripts/ci_local.sh --with-smoke` once.
6. `git add .github scripts/ci_local.sh && git commit && git push` — and then watch the Actions tab, which
   is the actual deliverable. Expect roughly: `shell-scripts` under a minute, `build-and-test` five to
   eight (first run has no NuGet cache), `boot-smoke` four to seven.
7. Optional, and worth doing once while the pipeline is fresh: `git tag --annotate v0.6.0 --message 'M6
   slice 1'` and push it, to prove the release path end to end before it matters.

---

#### What is left in M6

The E2E matrix (§16.3 — fifteen skipped facts and no harness), and the backup/restore drill as something
executable rather than a runbook §6 describes in prose. The cloudflared production profile, the
quick-tunnel script and the OPERATIONS runbooks were all delivered inside earlier milestones, so §19's M6
line is down to two items.

Next slice is the E2E harness: a fixture that brings up a stack, a Playwright browser context, and the
first scenarios from §16.3 — starting with 1, 13 and 14, because the virtual authenticator and the
controllable-clock plumbing they need are what every other scenario is waiting on.
