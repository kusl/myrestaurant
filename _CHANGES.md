# M6 Slice 27 — the command that started everything and never came back

**Findings closed:** F-53 (the documented bring-up command never returns), F-54 (a runbook step
asserting behaviour no script has).

Extract this archive at the repository root. Every file in it is complete; nothing is a patch.

```bash
cd ~/src/dotnet/myrestaurant
tar -xzf m6-slice-27-compose-dependency-hang.tar.gz
git add tests/MyRestaurant.WebApplication.Tests/Deployment
git status
```

**Before anything else, on virginia**, because the killed run left containers behind:

```bash
bash scripts/dev_instance.sh down
```

---

## Files in this archive

| Path | New? | Why |
|---|---|---|
| `compose.yaml` | | The fix. `web`'s dependency on `postgres` is `service_started`, not `service_healthy` |
| `scripts/dev_instance.sh` | | Every compose call under a deadline; health reported; created-but-not-started repaired |
| `tests/MyRestaurant.WebApplication.Tests/Deployment/ComposeDependencyContractTests.cs` | **new** | Three facts asserting the rule on every `dotnet test` |
| `docs/TECHNICAL_SPECIFICATION.md` | | v1.12: §14.1 prohibition, §14.3a deadline, §16.4 test, Appendix A F-53/F-54, changelog |
| `docs/DOCUMENTATION_REVIEW.md` | | F-53 and F-54 rows, status line, two closing paragraphs |
| `docs/OPERATIONS.md` | | §2 `.env` correction, §10a deadline + troubleshooting + `--no-build` row |
| `docs/BUILD_PROGRESS.md` | | Slice 27 entry (ships whole) |
| `README.md` | | The two `compose.yaml` rules, the deadline, the `.env` correction |
| `_CHANGES.md` | | This file |

**Files to DELETE: none.**

**`git add` reminder:** `tests/MyRestaurant.WebApplication.Tests/Deployment/` is a new directory. The
gate scripts enumerate with `git ls-files`, so an untracked file is invisible to `check_tree.sh` and to
`ComposeDependencyContractTests`' own hygiene checks. `git add` it before running the gates.

---

## What was actually wrong

The hang is not in the script. It is in `podman-compose up -d`.

podman-compose 1.3.0 — Debian trixie's version, and podman-compose is the canonical engine (ADR-0004) —
implements `up -d` as `podman run -d` for **every** container, followed by a wait on each dependency's
`depends_on` condition, in an unbounded `while True:` loop whose only two exits are logged at *debug*
level. `compose.yaml` asked `web` to wait for `postgres` to be `service_healthy`; the health status
never advanced past `starting`; the loop ran once a second forever.

Your four output lines map exactly onto four calls:

| Line | Call |
|---|---|
| `331b32fc…` | `podman pod create` |
| pull, then `c7ba751d…` | `podman run -d` (postgres) |
| `myrestaurant_postgres_1` | `podman start`, echoing the name |
| `6fe37290…` | `podman run -d` (web) |
| *nothing* | `check_dep_conditions()` → `podman wait --condition=healthy` |

Upstream: issues **#1178** (reported from Debian, with a `Ctrl+C` traceback landing on precisely those
frames) and **#1183**, which names the design error — dependents are started first and the conditions
checked afterwards, so the wait can only delay a return.

**Your instance was serving the whole time.** Both containers were started before the wait began. The
only thing that was broken was the terminal coming back.

## Decisions, and why

**1. The condition is prohibited, not worked around.** `service_started` is now the only condition
`compose.yaml` may use (§14.1, beside F-51's fully-qualified-images rule).

Three reasons it had to be a prohibition:

- **There is no flag.** `--no-deps` is accepted by `up`'s parser in 1.3.0 and consulted only by
  `compose_run`. Splitting into `up -d postgres` then `up -d web` does not help either: `get_excluded`
  subtracts a named service's dependencies from the exclusion set, so `up -d web` processes `postgres`
  too and reaches the same wait.
- **Satisfiability is a property of the host.** A health status advances only if something runs the
  healthcheck, and under rootless Podman that is a transient systemd timer in your user session.
  Upstream's own fix (PR #1184) says it in a commit title: *run the healthy state validation only when
  systemd is available*.
- **It was never needed.** `SchemaMigrationRunner` retries a connection failure thirty times at
  two-second intervals, and the comment beside it, written in M1, says what for: *"at compose start the
  web container can race PostgreSQL"*. `web` losing that race is a race the code was written to lose
  safely.

The health**check** on `postgres` stays. `podman ps` reads it, `dev_instance.sh status` prints it, and
an operator debugging a sick database needs it. It simply stops standing between `up -d` and returning.

`caddy` and `cloudflared` moved from the list form to the explicit mapping form with
`condition: service_started`. Identical semantics — both engines normalize the list form to exactly
that — written out so the file states the rule everywhere it applies.

**2. Every compose call runs under a deadline, and that outlives the cause.** `compose.yaml` is fixed;
podman-compose is not, and the next blocking path will arrive by some other route. A script whose whole
purpose is to hand the terminal back must not contain a call that can keep it (§14.3a).

- `DEV_INSTANCE_COMPOSE_WAIT`, default 240s, for ordinary commands.
- `DEV_INSTANCE_BUILD_WAIT`, default 5400s, for the image build — a watchdog that cut off your
  legitimate nineteen-minute cold build would be a worse defect than the one it guards against.
- A tripped deadline is **not** treated as failure: F-53 is named, both services' state *and health*
  are printed straight from the engine, anything created-but-not-started is started, and
  `/healthz/ready` is verified independently of compose.
- `status` prints those engine-read lines *before* asking compose anything, so they arrive even when
  compose is wedged. `down` falls back to removing the containers directly.
- Killing compose is safe in the one way that matters, and it is the same property the detached tunnel
  relies on: containers it already created belong to the engine, not to this shell.

**3. `service_healthy` is grepped for in the preflight.** The file about to be handed to the engine is
the one in your checkout, so a branch or a bad merge that reintroduces the condition gets a sentence
naming the cause instead of a terminal that stops.

**4. Two smaller things found while in the file.** `compose_container` filtered on
`com.docker.compose.service` alone — the correct label, podman-compose does set it — so a `web` service
from any other compose project on the host could have matched; it now tries a project-scoped filter
first and falls back, because the project name is derived here and the engine is the authority.
`container_health` is new: reported, never waited on, which is the entire distinction this slice is
about.

**5. F-54 — the `.env` claim, ruled against the document.** `OPERATIONS.md` §2 said *"`run.sh` and the
scripts do this automatically when `.env` is absent — F-16"*. All nine scripts were grepped; none writes
`.env`. The citation is accurate: F-16's row in `DOCUMENTATION_REVIEW.md` really does say that. So this
is a decision that was recorded and never implemented, then restated in the indicative by the runbook
depending on it — F-38's shape, pointed inward.

**Ruled the other way: the document is wrong, the scripts are right, and that clause of F-16's ruling is
reversed rather than implemented.** Materialising `.env.example` would write an untracked file carrying
`POSTGRES_PASSWORD=myrestaurant` that nobody knowingly created, on a path `.gitignore` hides from every
tool that reads the tree — F-45's class of artefact, arriving by a different door. And because the stack
starts without it, auto-creation buys nothing except the removal of the one moment an operator is
supposed to decide about credentials.

**This is yours to overrule.** If you want the scripts to create `.env`, say so and it moves to the
other side in a slice of its own; F-16's row then needs a note either way.

## Verification

No SDK and no container engine available, so everything that could be executed was.

- **podman-compose 1.3.0's source fetched and read** — `compose_up`, `run_container`,
  `check_dep_conditions`, `get_excluded`, the healthcheck translation, `ServiceDependencyCondition`, and
  `Podman.run`'s contract (returns an exit code, never raises). 1.2.0, 1.3.0, 1.4.0 and `main` compared:
  `main` has the fix and honours `--no-deps`; 1.3.0 has neither.
- **`scripts/dev_instance.sh` run end to end against fake `podman` / `podman-compose` / `curl`
  stand-ins.** The `up` path completes, exit 0, banner and settle phase included. Then, individually:
  - compose starts the containers then hangs, `postgres` health pinned at `starting` — **your exact
    failure**: deadline trips, F-53 named, containers reported `running, health: starting`, readiness
    verified, terminal released in **3 seconds**.
  - compose creates but does not start, then hangs: the repair path runs —
    `starting build_postgres_1 (it is created)`.
  - compose wedged from the first call: `status` reports engine facts first; `down` removes both
    containers directly and leaves nothing behind.
- **Preflight grep proven sensitive**: silent on the delivered file, fires with all four lines when the
  old condition is planted back.
- **`bash -n` clean; `shellcheck` clean at `--severity=warning` and `--severity=style`.** Baselined
  against all nine existing scripts first, so the installed shellcheck agrees with CI's on this tree.
- **`compose.yaml` parsed with a real YAML parser**: four services, three edges, all `service_started`,
  twenty `web` environment keys.
- **`ComposeDependencyContractTests` ported to Python, run, and proven sensitive** one regression at a
  time: `service_healthy` on three edges, on one edge, `service_completed_successfully`, a dangling
  dependency target, all `depends_on` removed (fact 1's non-vacuity guard), and a broken `services:`
  marker (throws rather than passing vacuously). The **list form passes** — deliberately, since both
  engines normalize it to `service_started` and failing it would report a finding on a correct file
  (F-41).
- **`ConfigurationSurfaceTests`' compose scan re-run**: still twenty keys, no duplicates,
  `RESTAURANT_SOURCE_URL` present, `SOURCE_REVISION` not miscounted. Block boundaries moved (web
  38→98 became 56→121) because comments were added — which is why that test computes them.
- **`SpecificationVersionTests` ported and run**: header 1.12, entries descending. `Version.TryParse`
  reads `1.12` as minor twelve, so it sorts above 1.11 — checked, because a string compare gets it
  backwards.
- **Brace/paren/bracket balance walked** over the new C# file (string- and comment-aware), with
  `ConfigurationSurfaceTests` as a control. Both balanced.
- **Every documentation edit by exact-match replacement, asserting the anchor occurs exactly once.**
- **Byte hygiene on every file**: LF only, one final newline, no CR, no whitespace-only lines, no
  trailing whitespace, no context-dump separator.

**Test count: 1056 → 1059.** Three `[Fact]` methods, none removed, no `[Theory]`. That is arithmetic,
not an observation — nothing here has compiled.

## What is not verified

**The fix has not been watched working on virginia.** The claim is that `service_started` makes
`check_dep_conditions` wait on `--condition=running` against a container that is already running, which
returns immediately. That is read out of podman-compose's source and podman's documented `wait`
semantics. The deadline is why a wrong answer there is survivable rather than another silent hang.

**Nothing has compiled.** The new test file is balanced, its logic ported and exercised, and its idioms
copied from a file in this tree that compiles — but `dotnet build` has not seen it.

## After you extract

```bash
bash scripts/dev_instance.sh down          # first: clear the containers the killed run left
git add tests/MyRestaurant.WebApplication.Tests/Deployment
bash scripts/check_tree.sh
bash scripts/dev_instance.sh               # on virginia
```

On the Fedora box, `dotnet test` is the one that matters — it is the first thing to compile the new
file.

Expected on virginia, in order: the `.env` warning (four lines now), the build (cached, seconds), the
tunnel URL — **reused**, since `down` removes the tunnel, so this run mints a new one and any passkey
registered against `state-dust-pty-cfr` is gone either way — then `starting postgres and web … (deadline
240s)`, then two `containers (engine):` lines with `postgres: … running, health: healthy`, then `the app
is ready on this host`, the banner, and the terminal back.

If `postgres` reports `health: starting` and never changes, that is the root cause of F-53 showing
itself on your host: nothing is running the container's healthcheck timer. The stack works anyway now —
which is the point of the prohibition — but it is worth knowing, and `loginctl enable-linger "$USER"`
is the first thing to try.
