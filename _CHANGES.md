# M6 Slice 16 — the restore drill, and the four defects it found before it could run

Every file below is a **full file** at its **repo-relative path**. Extract at the repository root and the
contents drop straight over your working tree — no diffs, no patches, no scripts to run.

```bash
tar -xzf m6-slice16-restore-drill.tar.gz -C /home/kushal/src/dotnet/myrestaurant
```

## Files to DELETE

**None.** Nothing here renames, supersedes or orphans anything: no migration, no schema change, no
package change, no ADR edit, no `Program.cs` edit, no `.slnx` edit, and **no C# at all** — so no new
test folder either.

## Two things to do by hand after extracting

```bash
git add scripts/restore_drill.sh          # NOT optional — see below
ls -l scripts/*.sh                        # all three should be -rwxr-xr-x already
```

`scripts/restore_drill.sh` is a **new** file, and both `scripts/ci_local.sh` and CI's `shell-scripts`
job enumerate what they check with `git ls-files '*.sh'`. Until it is tracked, it is silently
unchecked — the one gate in this slice that would notice a broken new script is the one that cannot
see it yet.

All three scripts carry mode 755 in the archive, so no `chmod` should be needed. If your extract
dropped the bit anyway, `chmod +x scripts/*.sh` fixes it; nothing in the tree invokes them as
`./scripts/…` (CI and `ci_local.sh` both go through `bash`), so a missing bit is cosmetic rather than
fatal.

## The files

| File | Change |
| --- | --- |
| `scripts/restore_drill.sh` | **new** — non-destructive rehearsal of a recovery set against a scratch database |
| `scripts/backup.sh` | rewritten: atomic dump, key-ring capture, discovery that refuses on ambiguity, three-valued exit |
| `scripts/restore.sh` | rewritten: archive verified first, `web` restarted from an `EXIT` trap, key ring put back, `--yes` |
| `.github/workflows/ci.yml` | `boot-smoke` gains three steps — render a page, back the instance up, drill the backup |
| `.gitignore` | backup artefacts, so O§6's "git-ignored" is true and a key ring cannot be committed |
| `.env.example` | the §15 block documents the two-file set and the discovery overrides |
| `docs/TECHNICAL_SPECIFICATION.md` | **§15 rewritten**, §16.4 amended, Appendix A gains F-38, changelog **v1.2** |
| `docs/OPERATIONS.md` | **§6 rewritten**, §8 bullet updated, §14 corrected and extended |
| `docs/DOCUMENTATION_REVIEW.md` | **F-38** entered, status line and "Going forward" updated |
| `docs/BUILD_PROGRESS.md` | Slice 16 appended (**complete file**, 5,756 lines — I did the appending) |
| `_CHANGES.md` | this file |

`docs/REQUIREMENTS.md` is **deliberately untouched** — see "One documentation decision" below.

## Why this slice is a drill and not a document

§19's M6 line has read "full E2E suite (§16.3), backups + restore drill, …" since v1.0. Slice 15
closed the first clause. The second was described in `OPERATIONS.md` §6 as five manual steps to
perform once, against a scratch host, before you need them — and nobody had performed them.

The moment something did, it found four defects. This is the whole argument for the slice.

### 1. `scripts/restore.sh` could not have completed a restore

It ran `pg_restore --clean --if-exists` under `set -euo pipefail`, with `web` already stopped, **one
line before** the `up -d web` that would bring it back.

`pg_restore` exits **1 whenever it ignored any error at all.** That is its documented contract, and it
is `exit_code = AH->n_errors ? 1 : 0` in `pg_restore.c` — I read it out of the PostgreSQL 17 source
rather than recalling it. `--clean --if-exists` ignores errors as a matter of course, because that is
what `IF EXISTS` is *for*.

So `set -e` killed the script one line early. **The single most likely outcome of the documented
recovery procedure was a database that came back and an application that stayed down, with nothing
printed to say so.** That is worse than a crash; a crash is attributable.

`web` now comes back from an `EXIT` trap, so it comes back on every path out of the script including
the ones that got there by failing. Ignored errors are reported and downgrade the exit code to 2.

### 2. Nothing captured the Data Protection key ring

§15 has required it alongside the database since v1.0. `OPERATIONS.md` §6 listed it as step 3. §8
explains exactly what losing it costs. And **F-16's own row in `DOCUMENTATION_REVIEW.md` lists it under
*Embodied in*.**

Both scripts printed a reminder.

Four documents in agreement about a thing no code did — which means every backup ever taken from this
tree is a set that restores every account and **no enrolled authenticator** (§3.4). `backup.sh` now
captures it as a sibling tar; `restore.sh` puts it back; the drill fails a set that does not have one.

### 3. A failed dump could evict a good one

The dump went straight through a redirect, so a truncated file survived as the newest `.dump`. `set -e`
skipped *that* run's pruning — which is exactly what F-16's "prunes only after a successful new dump"
promises — but the **next** successful run counted the poison file toward `BACKUP_RETENTION_COUNT` and
pruned a real backup to make room for it. The guarantee held within one run and broke on the following
one. Fixed with a hidden `.partial` write, a header check, and an atomic rename.

### 4. Container discovery took the first match

`ps --format '{{.Names}}' | grep -m1 postgres`. Harmless with one postgres container — and a restore
drill needs a second one. **A backup that dumps the scratch database succeeds, comes out roughly the
right size, and is worthless**, which is precisely the failure a backup script must not be capable of.
It now refuses on ambiguity and names what it found; `POSTGRES_CONTAINER` / `WEB_CONTAINER` settle it.

All four land as one ledger row, **F-38**.

## What the drill asserts, and the two gates worth defending

`scripts/restore_drill.sh` starts its own PostgreSQL container — distinct name, **no published port**
(so it cannot collide with the live `127.0.0.1:5432`), **no volume** (so its data dies with it) —
restores a real set into it, and tears it down in a trap.

It never writes to the live database, and that argument is structural rather than a promise: there is
exactly one connection target in the file, `scratch_query`, and it names `$SCRATCH` and nothing else.
The only thing that goes near the live instance is `--from-live`, which delegates to `backup.sh`,
which only reads.

**Gate C reads its expectations out of the migrations** — anchored `^CREATE TABLE x` / `^CREATE VIEW x`
over `src/MyRestaurant.DataAccess/Migrations/*.sql`, which is 22 tables and 5 views today. A
hard-coded list would have been easier and would rot on the first migration nobody remembered to
extend it with. The parse also has the failure mode a hard-coded list does not: if the DDL is ever
reformatted past those patterns, the gate reports **that** rather than silently passing on an empty
expectation. Anything in the dump that no migration declares is reported too.

**Gate D reads DbUp's journal, because structural completeness is not the question this code asks.**
At startup `SchemaMigrationRunner.IsUpToDate()` asks DbUp whether every embedded script has been
applied and `/healthz/ready` answers from it, so a restored schema with a short `schemaversions` is one
this code will try to migrate. Table name and shape verified against `dbup-postgresql`'s
`PostgresqlTableJournal` — `schemaversions`, columns `schemaversionsid` / `scriptname` / `applied`,
unqualified so it lands in `public`. The journal stores the embedded **resource** name, so a
migration's file name is a *suffix* of its row rather than equal to it; the gate matches on the suffix
for that reason.

Gate E queries every §8.3 view — the one place in the schema where an object's correctness depends on
nine others, and therefore what `--clean` is most plausibly able to break. Gate F is a row census,
reported and never asserted: the only sensible count for a fresh instance is almost all zeros, and the
only way to notice you have been faithfully backing up an empty database for a month is to be shown
the numbers. Gate G is the key ring, and it is why a drill of a database-only set is not allowed to
look like a pass.

## The key ring's write direction, and why it is safe

Reading it out is uncontroversial: `podman cp <web>:<dir>/. -` streams a tar through the engine's own
archive API, so it works regardless of what is installed in the runtime image — no `tar` in the
container, no helper image, no mount.

Writing it back crosses the volume-ownership question that `:U` exists to answer, and that is why I
nearly deferred it. It is safe for a checkable reason rather than a hopeful one:
`mcr.microsoft.com/dotnet/aspnet:10.0` resolves to the Ubuntu-based variant, whose `runtime-deps`
Dockerfile creates the `app` user at UID 1654 and **never issues `USER app`** — read out of
`dotnet/dotnet-docker` — and this repository's `Containerfile` does not set `USER` either. **The
application runs as root.** Root-owned key files in that directory are exactly what it writes there
itself, so `podman cp` in either direction and on either engine cannot get the ownership wrong.
`compose.yaml`'s `:U` is belt-and-braces, not load-bearing.

The restore happens while `web` is stopped, which is not tidiness: Data Protection mints a fresh ring
the first time it protects anything, so a ring dropped in after startup would sit beside one the
application had already begun using.

## Why the drill went into `boot-smoke` rather than its own job

Everything a drill needs is already standing up there: a built image, a database DbUp has migrated,
and a running application. A separate job would build the image a second time to answer one question.
The job's **display name is unchanged**, so nothing keyed on the check name moves.

The `curl http://127.0.0.1:8080/setup` step is load-bearing, and the reason is a detail that would
otherwise have made Gate G meaningless: **Data Protection creates its first key the first time it
protects anything, and `/healthz/ready` protects nothing.** On a freshly booted instance the key ring
is an empty directory. `/setup` renders a form, which mints an antiforgery token, which mints the key.
Without that step every CI run would report an empty ring and the gate would become noise.

`POSTGRES_CONTAINER` and `WEB_CONTAINER` are set explicitly rather than discovered, because the runner
generates the service container's name and because `backup.sh` now refuses to guess when more than one
container matches — which is exactly what the drill's own scratch container causes seconds later.

The set is deliberately **not** uploaded as an artifact: the `-dataprotection.tar` is key material in
the clear. Throwaway in CI, but publishing one is not a habit worth forming, and `.gitignore` refuses
it for the same reason.

**CI runs the drill without `--strict` on this first landing.** Every FAIL gate still blocks; what
`--strict` adds is that ignored `pg_restore` errors and an empty ring also fail, and neither number has
been observed on a real run. Once a few runs report "pg_restore completed with no errors", tightening
it is a one-word edit — and I would rather hand you a gate that goes green than one I guessed at.

## One documentation decision, stated so you can veto it

`docs/REQUIREMENTS.md` is **untouched**, and that is a judgement call rather than an oversight.

The atomic-documentation rule (R§10 · S§18) wants a behaviour change to land with its requirement
edit. But §15's sentence *"the Data Protection keys volume must be backed up alongside the database"*
was **already the contract**. Nothing here is new intent; the documents were right and the code did
not do what they said. So this lands as a defect fix at the mechanism level — S§15 rewritten, S§16.4
amended, O§6 rewritten with O§8 and O§14 following, F-38 entered — and no revision bump on the
requirements. If you would rather see a rev 3 with an explicit recoverability bullet in R§8, that is a
one-bullet edit and I will do it.

One thing fixed in passing: O§14's first paragraph said CI "runs three gates" and listed three of
four. `end-to-end` had been missing from it since Slice 2.

## Build and test

```bash
bash -n scripts/backup.sh scripts/restore.sh scripts/restore_drill.sh
shellcheck --severity=warning scripts/*.sh    # blocking gate — clean
shellcheck --severity=style   scripts/*.sh    # advisory gate — also clean

dotnet build
#    expect: all seven projects succeed, 0 errors. No C# changed in this slice.

dotnet test
#    expect: 971 total, 0 failed, 957 succeeded, 14 skipped — UNCHANGED from Slice 15.
#    Nothing here is an xUnit test, deliberately: the drill asserts on pg_dump/pg_restore
#    round-tripping and on container plumbing, and Testcontainers is the wrong tool for that.

bash scripts/ci_local.sh --with-all

# the drill itself, against a live dev stack:
bash run.sh --containers-only
bash scripts/backup.sh --no-keys     # dev runs the app on the host: no container to read a ring from
bash scripts/restore_drill.sh --no-keys
#    expect: gates A-F pass, G skipped with a WARN, exit 0. Roughly 20-30s, most of it the
#    scratch container's first-boot initdb.
```

Note that `dotnet test` at Slice 15 was expected at **971 / 0 failed / 957 succeeded / 14 skipped** and
I have not seen a run since. This slice moves no test either way, so whatever Slice 15's numbers
actually were, they should be identical here.

## What was actually verified here

No .NET SDK in the sandbox and no container engine either, so **none of the three scripts has been
executed.** What was run:

- `bash -n` and `shellcheck` at both `--severity=warning` and `--severity=style` on all three scripts
  — clean at both, which keeps `ci.yml`'s claim that every script in the tree is style-clean true.
- A YAML parse of the edited `ci.yml`: four jobs, nine steps in `boot-smoke`.
- The drill's pure-bash logic exercised against the **real** migration files: 22 tables and 5 views
  parsed; `contains` verified safe on empty arrays under `set -u`; the generated census SQL inspected;
  the journal suffix match and the ignored-error regex checked on both matching and non-matching input.
- `pg_restore`'s exit-code contract read out of the PostgreSQL 17 source.
- DbUp's journal table name and column shape read out of `dbup-postgresql`.
- The container's default user established from the .NET image sources rather than assumed.
- Every doc edit applied by exact-match replacement with an assertion that the anchor appears **exactly
  once**, so nothing was edited by position. Section numbering in `OPERATIONS.md` and
  `TECHNICAL_SPECIFICATION.md` re-checked afterwards: unchanged, so no cross-reference moved.

## Where to look if this breaks

**`backup.sh` exits 2 in production.** The web container was not found, or
`DATA_PROTECTION_KEYS_DIRECTORY` does not match its mount point. The database dump is fine and is
already on disk; only the ring is missing.

**`backup.sh` refuses with "more than one running container matches".** Something else on the host has
`postgres` or `web` in its name, or a previous drill's scratch container survived (`--keep`, or a
`SIGKILL`). It lists the candidates. `POSTGRES_CONTAINER=… WEB_CONTAINER=…` settles it.

**The drill fails Gate C with "read 0 table(s) and 0 view(s) out of the migrations".** A migration was
reformatted past the anchored `CREATE TABLE x` / `CREATE VIEW x` patterns. Fix the patterns in the
script — do not hard-code a list, which is the thing this gate exists to avoid.

**The drill fails Gate D with rows missing from `schemaversions`.** The dump predates a migration this
tree now carries, which is the *expected* answer for an old dump: this code would migrate it forward at
startup. A dump from a **newer** schema is the dangerous direction and fails fast on boot instead.

**The drill fails Gate E on a view.** `pg_restore --clean` dropped or failed to recreate something a
§8.3 view depends on. Gate B's ignored-error count in the same run is where the reason will be.

**The drill warns on Gate G with an empty ring.** Nothing has been protected yet on that instance. In
CI that means the `/setup` render step did not do its job — check its output before anything else.

**CI's `back up the booted instance` step fails.** `${{ job.services.postgres.id }}` did not resolve to
something `docker exec` accepts, or `myrestaurant_web_ci` had already exited. `boot-smoke`'s existing
readiness probe runs first and would normally have caught the second.

**`restore.sh` exits 2 with "pg_restore ignored N errors".** Usually benign under `--clean --if-exists`
— a `DROP` for something that was not there. It is surfaced rather than swallowed precisely because the
count is small enough to be worth reading; `backup.sh` passes `--no-owner` at dump time to keep it that
way.
