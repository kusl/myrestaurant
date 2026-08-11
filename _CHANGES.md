# M6 Slice 31 — the rule that was true of two files (F-60), and the helper that said it twice (F-61)

Extract at the repository root. Every path is repo-relative and every file is complete.

```
tar -xzf m6-slice-31-image-references-and-single-fire-cleanup.tar.gz
git add tests/MyRestaurant.WebApplication.Tests/Deployment/ContainerImageReferenceContractTests.cs
git status
```

**Files to DELETE: none.**

**`git add` is required** for the one new file above — `scripts/check_tree.sh` enumerates with
`git ls-files`, so an untracked file is a file no gate looks at.

---

## Both findings came out of the logs from a run in which everything passed

No user reported either of these. They are in the three terminal logs from Slice 30's verification: 1070
tests green on two hosts, fifteen §16.3 scenarios green, `ci_local.sh --with-all --with-e2e` clear,
`dev_instance.sh` up on virginia in 76 seconds, and a hundred thousand requests through a quick tunnel at
737 RPS.

---

## F-60 — four image references, and a green suite that ran nothing

§14.1 has required a fully qualified image reference since v1.11. `compose.yaml` obeys it.
`scripts/restore_drill.sh` has obeyed it since Slice 16. Four references did not:

```
tests/MyRestaurant.DataAccess.Tests/PostgreSqlFixture.cs:36        postgres:17-alpine
tests/MyRestaurant.EndToEnd.Tests/Harness/RestaurantHarness.cs:30  postgres:17-alpine
.github/workflows/ci.yml:265                                       postgres:17-alpine
.github/workflows/ci.yml:396                                       postgres:17-alpine
```

**The claim this rests on was verified from source rather than assumed.** Testcontainers does not
normalise the reference before the engine sees it. `MatchImage.Match` records a registry only when the
first slash-separated segment carries a `.` or a `:`, and the comment above that line says the
implementation "does not resolve or set the default domain and repository prefix". So the short name
reaches the engine as a short name, and resolution goes through `unqualified-search-registries` — which
Fedora populates and a stock Debian ships commented out. F-51's mechanism, one layer over.

**Why this is worse than F-51 rather than merely wider.** F-51 was loud: three errors, nothing started.
This is silent, because both fixtures catch every startup failure and turn it into a skip — deliberately,
documented, and correctly, since a missing engine is not a broken product. So on the canonical host
`dotnet test` exits 0 with the data-access integration tests and all fifteen §16.3 scenarios declining to
run. And the skip reason says **"A container engine (Podman/Docker) was not reachable"** and prescribes
activating a socket that is already active; the engine's own sentence naming the real cause sits three
clauses further along, contradicting its own headline.

Both fixtures now split that diagnosis, and both name the image — which neither message did, and which is
the first thing you want when a pull is what failed.

**Two of the references were invisible to any audit that could be written.** One spelled into a
`podman run` command line in `scripts/quick_tunnel.sh`, one passed inline to `new PostgreSqlBuilder(…)`.
They move into `CLOUDFLARED_IMAGE` and a `PostgreSqlImage` constant, and the reason is recorded beside
each: **naming them is what puts them in scope.** `scripts/dev_instance.sh` has read a `CLOUDFLARED_IMAGE`
variable since Slice 27, so `quick_tunnel.sh` also gains an override it should always have had.

**This is F-46's shape for the third time, and the sharpest yet.** F-46 was a rule stated generally and
enforced as six phrasings about one settings page. F-58 was a rule stated generally and enforced against
one file. F-60 is a rule stated generally *in the same commit that applied it to one file*, by the person
who chose the scope.

### The gate — and this is not a reversal of F-51's ruling

F-51's row declined to make §14.1 executable, on the grounds that a check for a missing registry component
is a text assertion about a file whose real contract is behavioural. **That stands.** The CI job on the
canonical engine is still the open item, and this test is not offered instead of it.

What the new test asserts is a different proposition and one that is wholly a property of the tree: that a
rule stated for the repository is applied everywhere in the repository it applies to. Three facts:

1. **The scan found a reference in each position it reads, and at least ten in total** — first and on its
   own, because both facts below pass against an empty set (F-41).
2. **Every reference names a registry.** This is F-60.
3. **Every image name resolves to exactly one reference.** The fact the other two cannot reach: a
   reference that is fully qualified and has drifted to a different *version* than the canonical stack
   runs breaks no gate, prints nothing, and means the suite passed against a database you do not deploy.

Each proven sensitive by its own regression. Reintroducing F-60 fails facts 2 **and** 3; changing one
fixture to `postgres:18-alpine` — fully qualified, no short name anywhere — fails **only** fact 3;
renaming the `*_IMAGE` variables fails fact 1.

The scan skips `docs/` entirely: those files quote both spellings on purpose, because F-51's whole ledger
row is about the difference.

---

## F-61 — two closing lines for one Ctrl+C

```
^C[quick-tunnel] closing the tunnel (the stack keeps running; stop it with 'podman-compose down').
[quick-tunnel] closing the tunnel (the stack keeps running; stop it with 'podman-compose down').
```

`trap cleanup INT TERM EXIT` at line 123, and a second handler on the same three at line 185. A signal
trap and the `EXIT` trap are independent registrations, so the body ran twice for one keystroke.

**Nothing it did was harmful, and that is the finding rather than a mitigation.** `kill` on a reaped
process returns immediately and `rm -f` is idempotent. What was wrong was the sentence: two identical
lines read as two tunnels, or as one that would not close, from a helper whose whole job at that moment is
to tell you what state the machine is in. Third consecutive slice where a helper's *output* was the defect
and its actions were correct — F-53 printed nothing, F-55 printed success over a dead container.

**The fix does not depend on knowing which trap fired first.** I could not reproduce the double fire in
the sandbox: different bash, no controlling terminal, and `kill -INT <pid>` is not a `^C` delivered to a
foreground process group. Your log is the evidence; the structural explanation is the mechanism; and the
handler is made correct under *every* ordering with a first-entry guard plus `trap - INT TERM EXIT` on
entry, rather than by choosing a different set of signals. The guarded handler was exercised and fires
once.

**The class was audited, not the instance.** `run.sh`'s smoke trap carries the identical registration and
is **unchanged**, because its handler is silent and idempotent by construction and a rule that called that
a defect would report findings on a correct tree (F-41). `backup.sh`, `restore.sh` and `restore_drill.sh`
register on `EXIT` alone. Deliberately not made executable, for the same reason: the assertion would have
to be *no handler on both a signal and EXIT*, which is false of `run.sh` for good reasons.

---

## Two things in those logs that are NOT findings

**The single HTTP 429 in 100,000 requests is Cloudflare's edge, not your application.** `/healthz/live`
carries no `[EnableRateLimiting]` and there is no global limiter. Recorded as a baseline in
BUILD_PROGRESS instead: 737 RPS, P50 90 ms, P99 215 ms — and `/healthz/ready`, which opens a connection,
runs `SELECT 1` and asks DbUp whether the schema is current, returned 100,000 of 100,000 at P50 91 ms. A
readiness probe that does real work being indistinguishable from a liveness probe that does none, at that
volume, is worth knowing.

**`Error: no container with name or ID "myrestaurant_caddy_1" found`** is podman-compose's own internal
`rm` of a container that does not exist yet. Engine noise at error level, not yours to fix.

---

## A correction to Slice 30's entry

It predicted 1072 tests from a baseline of 1066. Your run reports **1070**. The rewritten
`SpecificationVersionTests` has two `[Fact]` methods where it previously had **four**, not two — so the
rewrite replaced four facts with two and the count landed two lower than predicted. Corrected in
BUILD_PROGRESS rather than left standing.

Slice 31 predicts **1073**. Three new facts, arithmetic, nothing compiled or run.

---

## A correction to the menu plan, made before Stage 2 is authored

**Stage 2 as the plan wrote it cannot ship green.** `menu_section_identifier` is `NOT NULL`, so the moment
`0003` applies, `CreateMenuItem.razor` cannot create an item without naming a section — and
`AdministrationJourneys.CreateMenuItemAsync` drives that real form in five of the fifteen §16.3 scenarios.
Schema plus data access, with the surfaces left for Stage 3, is a red suite whatever the quality of the two
halves.

So Stage 2 pulls exactly three things forward: the section create page, the picker and description field on
the item form, and a harness `CreateMenuSectionAsync`. The section index, the section editor with its event
history, the rewritten guest menu and the kitchen panel's grouping all stay in Stage 3.

The alternative — nullable in `0003`, tighten in `0004` — is three migrations for two decisions and puts an
"Uncategorized" state in the schema for exactly one slice, which every surface written during that slice
then has to handle and un-handle. The ruling that an item under no heading is an item nobody decided about
is worth more than a neat stage boundary.

---

## Verified

- Scanner **ported to Python line for line** and run: 12 references, all qualified, one reference per image
  name, all four positions populated.
- **Each fact proven sensitive** by its own regression, including the fully-qualified version drift that
  only fact 3 catches.
- `bash -n` and `shellcheck --severity=style` clean — but shellcheck **0.9.0** here against your 0.11.0, so
  your run is the one that counts.
- Brace balance on all three C# files, with an untouched sibling as a control.
- `SpecificationVersionTests` **ported and run**: header 1.16 against newest entry 1.16, seventeen entries
  descending, two documents qualified, none half-versioned. My first port was too strict — it scanned whole
  files rather than the text after a history heading and flagged `BUILD_PROGRESS.md`. Corrected against the
  real regexes, which is the reason to port rather than reason.
- Byte hygiene on every delivered file. One pre-existing exception left alone: `ci.yml` ends with two
  newlines in your tree and `check_tree.sh` accepts it, so the gate is looser than my scan.
- Both workflows and `compose.yaml` parse as YAML.

## Not verified

**Nothing compiled.** **No engine resolved any reference** — the Debian failure is F-51's observation plus a
verified reading of Testcontainers' source, not a reproduction. **CI's service container was changed
without being run:** Docker normalises a short name to `docker.io/library/…`, so both references should be
a store hit rather than a second pull and the cache-sharing comment now says so, but the first push is what
proves it. If the drill step starts pulling on every run, that comment is where to look.
