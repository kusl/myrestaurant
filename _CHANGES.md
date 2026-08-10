# M6 Slice 29 — the engine that does not read its own defaults (F-57)

Extract at the repository root. Every path is repo-relative and every file is complete.

```
tar -xzf m6-slice-29-compose-substitution.tar.gz
git add scripts/check_compose_substitution.sh
git add tests/MyRestaurant.WebApplication.Tests/Deployment/ComposeSubstitutionContractTests.cs
git status
```

**Files to DELETE: none.**

**`git add` is required** for the two new files above — `scripts/check_tree.sh` enumerates with
`git ls-files`, so an untracked file is a file no gate looks at.

---

## Your log answered it, and the answer was not a variable

```
web:      Configuration error: RESTAURANT_TIME_ZONE '${RESTAURANT_TIME_ZONE:-America/New_York}' …
postgres: FATAL: invalid character in extension owner: must not contain any of ""$'\
          STATEMENT: CREATE EXTENSION plpgsql;
          initdb: removing contents of data directory "/var/lib/postgresql/data"
```

**Your compose engine does not apply the branch after `:-`.** Every value in `compose.yaml` is written
`${NAME:-default}`, and every variable that was not already set in your environment reached its
container as the placeholder text. That single fact killed both containers:

- **web** validates five of them, so it printed five errors and returned 1 — the exit code Slice 28
  reasoned from before any log existed.
- **postgres** got `POSTGRES_USER=${POSTGRES_USER:-myrestaurant}`, so initdb's bootstrap
  `CREATE EXTENSION plpgsql` failed on the punctuation in the owner name, initdb wiped the data
  directory, the container exited, the engine restarted it — forever. **The crash loop was never about
  your volume**, and `reset` would not have helped: initdb was already erasing that directory on every
  attempt. Both `podman system prune -a` runs were beside the point too.

**Proof of the mechanism, from your own run:** `RESTAURANT_PUBLIC_ORIGIN` was the one value that
arrived correct, and it is the one `dev_instance.sh` exports. So substitution works when the variable is
**set**; it is the *default* branch that is unapplied. `${RESTAURANT_CURRENCY_CODE:-USD}` failed too,
which rules out an escaping problem — `USD` is as plain as a default gets.

### The five errors were the good case

Eleven more variables were wrong and said nothing:

| Variable | What the placeholder text does |
|---|---|
| `RESTAURANT_NAME` | renders `${RESTAURANT_NAME:-My Restaurant}` as the restaurant's name, on every page |
| `ARGON2_*`, `KITCHEN_…`, `TABLE_…` (seven) | unparseable as an integer, therefore indistinguishable from absent: compiled-in values are used silently |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | **its emptiness is the off switch.** The literal is not empty, so the exporter attaches and points at a hostname made of braces |
| `RESTAURANT_DATABASE_CONNECTION_STRING` | three nested placeholders in one folded scalar; non-empty, so it passes validation and fails at connect |

---

## The fix: ask the engine, refuse early, make `.env` sufficient

**New `scripts/check_compose_substitution.sh`.** It does not guess from a version number:

1. enumerate the `${NAME…}` placeholders in `compose.yaml`;
2. subtract the ones that do not need a default — set in the environment, or assigned in `.env` —
   because the *set* branch is the half your log proves works. Nothing left → exit 0, nobody asked;
3. otherwise render with `compose config` under a deadline and look for a surviving `${`.

Exit **3** the engine does not apply defaults · **2** could not be determined here (no usable `config`)
· **0** fine. The middle value matters: a missing subcommand is not a broken engine, and conflating
them would either block correct hosts or pass broken ones.

**Three helpers run it before doing work.** `dev_instance.sh` refuses **before** the twenty-minute
build. `quick_tunnel.sh` refuses before publishing a hostname. `run.sh` refuses before `compose up`.

**`dev_instance.sh` asks again after `up -d`, from the containers' own environment** — one `inspect`
each, `{{range .Config.Env}}`, grep for `${`. Ground truth: no subcommand needed, cannot be fooled by a
`config` that renders differently from what it runs, and it is the only thing that can settle whether an
*empty* assignment satisfies your engine. It runs before any waiting, because a placeholder in
`POSTGRES_USER` means the database question is already settled.

**`.env.example` now assigns every variable the stack interpolates.** It was assigning 19 of 22 —
`OTEL_EXPORTER_OTLP_ENDPOINT`, `OTEL_EXPORTER_OTLP_HEADERS` and `CLOUDFLARE_TUNNEL_TOKEN` were commented
out, and a commented-out line supplies nothing. So the documented remediation was incomplete in exactly
the place that hurts most: an empty OTLP endpoint switches the exporter off, the literal switches it on.

**One thing I changed that you did not ask for, and it is F-50 again.** `.env.example` spelled
`RESTAURANT_SOURCE_URL=https://github.com/kusl/myrestaurant`. F-50 ruled that `compose.yaml` must pass
that variable with an *empty* default so `RestaurantOptions.DefaultSourceUrl` stays the one place the
fallback lives — and then left the upstream URL in the file operators are told to copy. A fork whose
first edit is that constant would have had it silently overridden by their own `.env`. It is assigned
empty now, with the fork example in a comment beside it. This is what fact 3 of the new test caught
while I was proving it sensitive; I did not go looking for it.

**`compose.yaml` is not edited.** The file is correct; the engine reading it is not.

---

## The build break — fixed properly, and it will not come back

`DevInstanceLoopbackContractTests` wrapped one assertion message in
`string.Create(CultureInfo.InvariantCulture, …)`, and the concatenation fed to it ended with a **plain**
string literal instead of an interpolated one. An additive expression converts to an interpolated string
handler only when *every* operand is itself `$"…"`, so the call bound to no overload. I checked the tree
afterwards: **every** `string.Create` concatenation in it prefixes every operand with `$`, including
operands with no holes. That file broke a uniform idiom.

The repair is not to add the missing `$`. Every hole in that message is already a `string`, so there was
nothing culture-sensitive to format and `string.Create` was never earning anything — it is a plain
interpolated string now, `using System.Globalization;` is gone with it (an unused using is an error
under CI's `TreatWarningsAsErrors`), and the reasoning sits above the assertion so nobody reaches for
the habit again. Two more idioms hardened while the file was open: `Assert.Equal` on a collection size
replaced with `Assert.True` (xUnit's analyzer has opinions), and `Assert.False(string.IsNullOrEmpty(x))`
replaced with a length comparison.

The file shipped here is the authority — it supersedes your local edit and contains the same fix.

---

## Every file, and why

| File | Change |
|---|---|
| `scripts/check_compose_substitution.sh` | **new.** The decisive check, standalone and runnable by hand, with the whole account in its header |
| `scripts/dev_instance.sh` | preflight before the build; container-environment gate after `up -d`; the `.env` warning corrected (it said an absent `.env` was "fine"; on your engine it is fatal); two new reading-key entries for the placeholder and initdb symptoms |
| `scripts/quick_tunnel.sh` | refuses before opening a tunnel over a stack that cannot start |
| `run.sh` | refuses before `compose up` |
| `.env.example` | all 22 interpolated variables assigned; OTEL pair and tunnel token empty rather than commented; `RESTAURANT_SOURCE_URL` empty per F-50 |
| `docs/TECHNICAL_SPECIFICATION.md` | **v1.14** — §14.1 third normative rule + the `.env.example` requirement + what is deliberately not claimed; §16.4 the test and why the script is a preflight and not a CI gate; Appendix A F-57; header and changelog moved together |
| `docs/OPERATIONS.md` | §10a opens with the substitution check; §2's `.env` step upgraded from advice to near-prerequisite |
| `README.md` | the third-run paragraph, and the script in the layout list |
| `docs/DOCUMENTATION_REVIEW.md` | F-57 row, status line, and two closing paragraphs |
| `docs/BUILD_PROGRESS.md` | Slice 29, whole |
| `tests/…/ComposeSubstitutionContractTests.cs` | **new**, 3 facts |
| `tests/…/DevInstanceLoopbackContractTests.cs` | build break fixed, idioms hardened |

---

## Verification

- **The check run against three simulated engines** (`config` substitutes / leaves `${…}` literal /
  no `config` at all) plus a fourth case with a complete `.env`: exit 0, exit 3 listing all 23
  variables, exit 2 with the *undetermined* message, exit 0 without asking the engine. Each intended.
- **`bash -n` + `shellcheck`** on all four scripts, clean at `--severity=warning` and `--severity=style`;
  nine pre-existing scripts baselined style-clean first. One suppression with its reason on the line
  above: `SC2016`, where `'${'` is the literal being searched for.
- **New test ported to Python and proven sensitive** one regression at a time — six mutations across the
  three facts, a throw on a broken `services:` marker, and a comment-only control that changes nothing.
- **`DevInstanceLoopbackContractTests`, `ConfigurationSurfaceTests`, `ComposeDependencyContractTests`
  and `SpecificationVersionTests` re-run** against the edited tree. All pass. Header 1.14, entries
  descending.
- **Balance walk** over both C# files with two controls; **byte hygiene** on all eleven files.

**Test count 1063 → 1066.** Three `[Fact]` methods, none removed.

**Not verified:** nothing compiled, and no real engine ran the check — the shims imitate the two
behaviours, and if your `podman-compose` has no `config` subcommand the preflight exits 2 and the
post-`up` container check is what fires. That is why both exist.

---

## On virginia

```bash
cd ~/src/dotnet/myrestaurant && git pull
bash scripts/check_compose_substitution.sh ; echo "exit=$?"
```

If it exits 3 (or 2):

```bash
cp .env.example .env
bash scripts/check_compose_substitution.sh ; echo "exit=$?"    # should be 0 now
time bash scripts/dev_instance.sh
```

`.env` carries `POSTGRES_PASSWORD=myrestaurant` — a development credential, fine on a Tailscale-only
box, worth changing on anything else. Old volumes are harmless: initdb never finished, so there is no
half-built database to clear. If the check still refuses after copying `.env`, your engine is not
honouring an empty assignment either — send me the output of
`podman-compose config | head -40` and `podman-compose --version` and I will take the second
remediation properly rather than pointing you at three options.
