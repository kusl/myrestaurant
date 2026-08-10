# M6 Slice 28 — the command that came back and said nothing was wrong (F-55, F-56)

Extract at the repository root. Every path below is repo-relative and every file is complete.

```
tar -xzf m6-slice-28-bring-up-evidence-and-diagnosis.tar.gz
git add tests/MyRestaurant.WebApplication.Tests/Deployment/DevInstanceLoopbackContractTests.cs
git status
```

**Files to DELETE: none.** Nothing is renamed and nothing is superseded.

**`git add` is required** for the one new file above. `scripts/check_tree.sh` enumerates with
`git ls-files`, so an untracked file is a file no gate looks at.

---

## What this slice is about

`time bash scripts/dev_instance.sh` on virginia took six minutes and fifty-five seconds to report
success over an instance that had never served a request. It did not hang — Slice 27 fixed that. It
waited out a 300-second readiness deadline against a container that had already exited, printed the
`DEV INSTANCE — DETACHED` banner with a public URL in it, spent twenty more seconds probing that URL,
warned that it had not answered, and **exited 0**.

The reason had been available since the tenth second, in a log nothing ever printed.

---

## The diagnosis, and the one inference it rests on

From the transcript: `postgres` was created six minutes before `podman ps` reported `Up 1 second`,
which is the engine restarting it in a loop; `web` had **`Exited (1)`**.

**Exit code 1 is reachable from exactly one place in this program.** `Program.cs` binds and validates
configuration before a host exists, and on failure prints each error to stderr and `return 1`s.
Nothing else in that file returns 1 — a `SchemaMigrationRunner` failure throws, and an unhandled
exception on .NET aborts with 134. So the application refused its own environment, said which
variable and with what value, and exited, all within the first second.

That message is still on virginia. `podman logs myrestaurant_web_1 | tail -40` prints it. This slice
does not need to know what it says to fix the defect, because the defect is that nothing printed it —
but it is worth reading, and if you send it I will put the specific variable in F-55's row rather than
the general case.

---

## Two findings

**F-55 — a wait with a deadline and no evidence.** F-53 was a wait with no deadline; Slice 27 put
deadlines on every compose call and, in the same script, left a 300-second HTTP poll that never asked
whether the container it was polling still existed. The two failures look alike and need opposite
fixes: a deadline stops a wait that cannot *end*, and only a liveness check stops a wait that cannot
*succeed*. Alongside it: the success banner printed unconditionally, the settle phase probed a public
URL for an application that was not answering on loopback, `status` described a container that had
exited 1 as `(stopped, health: starting)`, `logs` could only show the tunnel's log, and `up` exited 0.

**F-56 — three helpers dial one port and only `run.sh` names the address that exists.**
`compose.yaml` publishes `127.0.0.1:8080:8080`; `run.sh` has probed the literal since M1; both tunnel
helpers defaulted `TUNNEL_TARGET` to `http://localhost:8080`. curl and GNU wget fall back to the
second address; **BusyBox wget does not**, and it is the second entry in the probe chain of a script
whose premise is a host that may not have curl. The visible cost is worse than the risk: cloudflared
reports the address it failed on, so the F-55 tunnel log is full of `dial tcp [::1]:8080: connect:
connection refused` and sends an operator after an IPv6 problem that is not there. F-51's shape for
the third time — a rule reasoned through once, applied to one file, never stated.

---

## Every file, and why it changed

### `scripts/dev_instance.sh` — rewritten

| Change | Why |
|---|---|
| both waits bounded **and** watched | F-55. The readiness wait polls container state; the new database wait uses `pg_isready` inside the container. Each ends early — crash loop, or a container that will not stay started — instead of running the deadline down. |
| database wait separated from readiness wait | "the app did not answer" is equally true of a crash-looping database, a rejected configuration and an image that never started. One message cannot diagnose three failures. |
| a stopped container is started again, up to `DEV_INSTANCE_START_ATTEMPTS` (3) | `SchemaMigrationRunner` gives up after sixty seconds (ADR-0012). A slower first `postgres` boot outlives it and leaves a correct image stopped with nothing wrong. The engine's restart policy usually covers this; *usually* is not worth 300 seconds. |
| failure prints both log tails, with state, **exit code** and restart count | The whole finding. `podman logs` held the answer for seven minutes. |
| a reading key for six real symptoms | `Configuration error:` → it refused its environment. `Database not ready (attempt n/30)` → the cause is in the *other* log. Four PostgreSQL data-directory failures → `reset`. `address already in use`. Both containers up and the probe still failing → check the address. |
| `logs [web\|postgres\|database\|tunnel]`, default **web**, `--tail N` | The one command an operator reaches for could only show cloudflared. |
| new `diagnose` | The failure report, on demand, later, without re-running `up`. |
| new `reset` | The one failure the helper cannot repair: a data directory that will not start survives `down` (which keeps volumes deliberately) and `podman system prune -a` (which does not touch them). Removes this project's volumes **enumerated from the engine**, after saying what that destroys, and refuses rather than assumes consent when stdin is not a terminal. |
| a stopped container reports its exit code, never a health status | `(stopped, health: starting)` reads as a container on its way up. It had exited 1 six minutes earlier. |
| settle phase skipped when readiness failed | Probing a public URL for an application that does not answer on loopback cannot produce information. It produced a warning. |
| `up` exits 0 only if the application answered | `time bash scripts/dev_instance.sh` is how this is invoked, and a `&&` chain believes an exit code. The stack is **left running** on failure: the containers are the evidence. |
| state file written **before** readiness is decided | The tunnel and its hostname are real whatever the application is doing, so `url` and `down` must work on a failed bring-up. A URL nobody recorded is a tunnel nobody can close. |
| `TUNNEL_TARGET` defaults to `http://127.0.0.1:8080` | F-56. |
| `wait_for_http` deleted | It became unreachable once readiness moved into `wait_for_application`. shellcheck's SC2329 said so; acted on rather than suppressed. |

### `scripts/quick_tunnel.sh`

`TUNNEL_TARGET` defaults to the literal, with the reasoning beside the assignment. Three lines,
comments aside. Nothing else about that script changes — it is still the foreground demo helper.

### `.env.example`

`TUNNEL_TARGET`'s documented default corrected, and the new knobs listed with what they are ceilings
*for*: `DEV_INSTANCE_DATABASE_WAIT`, `DEV_INSTANCE_START_ATTEMPTS`,
`DEV_INSTANCE_CRASH_LOOP_RESTARTS`, `DEV_INSTANCE_LOG_TAIL`. **All fourteen added lines are
comments**, so `ConfigurationSurfaceTests`'s key set is untouched — verified, not assumed.

### `docs/TECHNICAL_SPECIFICATION.md` → **v1.13**

§14.3a gains four normative paragraphs (no wait outlives its evidence; a failure prints the log and
`logs` reaches the application; `up`'s exit status is a claim about the instance; `reset` exists and
what it must ask before acting) plus the address-literal rule. §16.4 gains the loopback contract test
*and what it deliberately does not assert*. Appendix A gains F-55 and F-56. Header and changelog both
moved, together — F-48 is what happens when only one does.

**No `REQUIREMENTS.md` edit**, on the v1.11 and v1.12 reasoning: R§8 asks for an operable instance and
ADR-0004 has named podman-compose canonical since M1, so a helper reporting success over a dead
instance is a mechanism failing a contract this tree already carried, not new intent. No schema
change, no ADR edit, no `.slnx` edit, and **`compose.yaml` is not touched by this slice**.

### `docs/OPERATIONS.md`

§10a's command table gains `logs`, `diagnose` and `reset` and records the exit-code contract. A new
**"When it does not come up"** subsection: what `up` prints on failure, the two log lines that point
in opposite directions, and the data-directory case with `reset` as its answer. §15's verification
`curl` uses the literal, for F-56's reason.

### `README.md`

The command block gains the three new subcommands; the F-53 paragraph gains the F-55 sequel, because
a reader who is told the script once hung forever should also be told what the fix to that produced.

### `docs/DOCUMENTATION_REVIEW.md`

F-55 and F-56 rows, the status line, and three closing paragraphs: F-53's fix producing F-53's shape
in reverse (**a bound on how long you will wait is not a bound on how long you will be wrong**), the
exit-code rule this project had not stated, and F-51's shape reaching its third occurrence — with the
habit that would have caught all three: *when a comment explains why a value is what it is, grep the
tree for the other places that value is written.*

### `docs/BUILD_PROGRESS.md`

Slice 28, shipped whole. The mock-engine harness, the six scenarios with their measured timings, the
sensitivity proofs, and what could not be verified from here.

### `tests/…/Deployment/DevInstanceLoopbackContractTests.cs` — new

F-50's pattern, not a grep: the published port in `compose.yaml` is authoritative, each helper's
`TUNNEL_TARGET` default is a restatement, and the test derives the first to check the second. Four
facts — the scan found everything (F-41 non-vacuity), each helper dials the published host and port,
each host is an address literal, and what is published is still loopback. The last one exists because
the rule is *argued* from there being one address: publish on `0.0.0.0` and the justification is gone,
and a test that stayed green through that change would be asserting a coincidence.

Deliberately **not** asserted: that no script says `localhost`. `run.sh` prints it in a sentence for a
human to paste into a browser, which is correct, and failing on it would report a finding on a correct
tree (F-41).

---

## Verification

No .NET SDK and no container engine here, so the script was run against a **mock engine** — `podman`,
`podman-compose` and `curl` shims driven by state files — with the production-length deadlines in
place, so the timings are the ones an operator sees:

| Scenario | Result |
|---|---|
| healthy | `DETACHED` banner, **exit 0** |
| `web` exits 1 on a `Configuration error:` line | `NOT SERVING`, both logs, reading key, **exit 1, 15s** |
| `postgres` crash-looping (`PANIC: could not locate a valid checkpoint record`) | crash loop named at three restarts, readiness skipped, **exit 1, 7s** |
| `logs` / `logs postgres` / `logs tunnel` / `logs database --tail 2` | right container every time |
| `reset --yes` | removed `myrestaurant_postgres-data` and `myrestaurant_dataprotection-keys`, left `other_project_data` alone |
| `reset` with no tty and no `--yes` | **exit 1, nothing removed** |
| five argument errors | each names itself, each exits non-zero |

**415 seconds and exit 0 became 7–15 seconds and exit 1.**

Also: `bash -n` and `shellcheck --severity=style` clean on both scripts (all nine existing scripts
baselined style-clean first with shellcheck 0.11.0); the new test ported to Python, run, and proven
sensitive one regression at a time — including two non-vacuity mutations and a comment-only control
that must change nothing; `SpecificationVersionTests`, `ConfigurationSurfaceTests` and
`ComposeDependencyContractTests` re-run against the edited tree; a string- and comment-aware balance
walk over the new C# file with two controls; every doc edit applied by exact-match replacement with a
one-occurrence assertion; byte hygiene on all nine files.

**Test count 1059 → 1063.** Four `[Fact]` methods, none removed, no `[Theory]`. Arithmetic, not an
observation.

---

## What is not verified

- **Nothing has compiled.** No `dotnet build` on the new test file.
- **Nothing ran on virginia.** Every timing is from the mock. What the mock cannot say is which
  variable the real `Configuration error:` line names.
- **`{{.RestartCount}}`** is read from documentation, not from a running engine. Absent reads as 0,
  which loses the crash-loop early exit and keeps everything else.

## Suggested run on virginia

```bash
cd ~/src/dotnet/myrestaurant
git pull
podman logs myrestaurant_web_1 | tail -40      # the answer that has been sitting there
bash scripts/dev_instance.sh diagnose          # the same thing, framed, with the reading key
time bash scripts/dev_instance.sh              # now exits 1 fast if it is still broken
```

If `logs postgres` shows a data-directory failure: `bash scripts/dev_instance.sh reset`, then `up`.
That destroys the database and the key ring on that instance, which on a tester box with no data worth
keeping is the right trade — and is exactly what `podman system prune -a` could not do for you.
