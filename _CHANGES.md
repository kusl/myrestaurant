# M6 Slice 25 — F-50: the variable that never arrived

Extract at the repository root. Every file is complete; nothing is a diff, a patch or a script.

```bash
tar -xzf m6-slice-25-configuration-reaches-the-container.tar.gz -C /path/to/myrestaurant
git add tests/MyRestaurant.WebApplication.Tests/Configuration/
```

That `git add` is required: the directory is new, and `check_tree.sh` and `check_repository.sh`
enumerate via `git ls-files`, so an untracked file is invisible to both.

**No files to delete.** Nothing is renamed, moved or superseded.

---

## Files in this archive

| Path | New? | Why |
|---|---|---|
| `compose.yaml` | changed | Passes `RESTAURANT_SOURCE_URL` into the `web` service. The fix. |
| `src/MyRestaurant.WebApplication/Configuration/RestaurantOptions.cs` | changed | `DefaultSourceUrl`'s doc comment records why compose passes an *empty* default rather than repeating this constant. Doc comment only — no signature, no behaviour. |
| `tests/MyRestaurant.WebApplication.Tests/Configuration/ConfigurationSurfaceTests.cs` | **new** | The gate. Five facts. |
| `docs/TECHNICAL_SPECIFICATION.md` | changed | Header → **v1.10**; §13 gains the transport rule; §16.4 gains the test; Appendix A gains F-50; changelog gains v1.10. |
| `docs/DOCUMENTATION_REVIEW.md` | changed | F-50 row; "Going forward" names the new class. |
| `docs/OPERATIONS.md` | changed | §15 gains a verification step; §14's release examples corrected to one version. |
| `docs/BUILD_PROGRESS.md` | changed | Full file — existing bytes plus the Slice 25 section appended. |
| `.github/workflows/release.yml` | changed | Header comment's tag example corrected to match OPERATIONS §14. Comment only. |
| `_CHANGES.md` | changed | This file. |

`REQUIREMENTS.md` is deliberately untouched — see "Decisions" below.

---

## The finding

`compose.yaml`'s `web` service enumerates its environment key by key and takes no `env_file`. A key it
does not name does not reach the process. Measured against everything
`RestaurantOptions.FromConfiguration` binds — seventeen keys — **exactly one was missing**:

```
APP READS BUT COMPOSE web DOES NOT PASS: ['RESTAURANT_SOURCE_URL']
```

Of the seventeen it was the worst one it could have been. It is the variable F-39 built the AGPL §13
offer around; the one `OPERATIONS.md` §15 is *titled* after; the one §13 annotates "if you modify the
program and run it as a network service, point this at your fork". It is also the only variable in the
table whose default is a **claim about who wrote the program** rather than a formatting preference.

So the documented procedure produced the opposite of its purpose. A fork operator modified this
program, published their source, set the variable in `.env` as instructed, ran
`podman-compose --profile production up -d`, and served every one of their users a §13 offer naming
**this** repository — the one place their modifications are not.

Nothing failed. The container started, `/healthz/ready` answered 200, `/source` rendered a version and
a revision, and the link resolved — to a real repository containing the wrong program. The entire
failure is that a string was plausible.

**Why 1051 passing tests did not see it.** They passed legitimately. `RestaurantOptionsTests` covers
the binding layer thoroughly and the binding layer has no defect. Every test in the suite *constructs
its own* `RestaurantOptions`; not one receives the object a container would hand it, because doing that
means starting a container. No gate could see it either: `check_tree.sh` reads tracked files as text,
and `compose.yaml` is correct as text; `check_repository.sh` reads the platform; `boot-smoke` asserts
`/source` names the commit, which was true.

That is F-38's shape one layer further out — F-38 was four documents agreeing about something no code
did; this is four documents agreeing about something the code did correctly, where the transport
between the operator and the code discarded it.

---

## Decisions

**Empty default, not the upstream URL.** The new compose line is
`RESTAURANT_SOURCE_URL: ${RESTAURANT_SOURCE_URL:-}`, which is asymmetric with `RESTAURANT_NAME` and
`RESTAURANT_CURRENCY_CODE` beside it, and that is a ruling rather than an oversight.
`RestaurantOptions.DefaultSourceUrl` is a fork's natural first edit. A compose file spelling this
repository's URL as its own default would silently override that edit — F-50 reintroduced one layer up,
in the file that had just been fixed. An empty value is read as unset by `ReadString`
(`IsNullOrWhiteSpace` → fallback), so the fallback stays decided in exactly one place.

**`env_file: .env` rejected.** It fixes this instance and nothing else, and it hands the container the
whole file — `POSTGRES_PASSWORD`, `CLOUDFLARE_TUNNEL_TOKEN`, and whatever an operator has added. That
is F-45's mistake in a different file: a deny-list where an allow-list was already working. The
enumerated block is right; it was one line short.

**The gate is a test, not a `check_tree.sh` gate.** Same answer as Slices 23 and 24: that script
asserts properties of authored text as text and knows nothing about what a compose service means, and
F-41's rule is that gates sharing a file set must share one definition of it. This gate's subject is a
C# method.

**The rule runs in one direction.** A key in the `web` block that nothing reads is *not* a finding —
`POSTGRES_*` belongs to the database image and `OTEL_*` to the exporter under its own published
contract. Asserting it would report findings on a correct tree (F-41). Recorded in §13 as
one-directional so nobody adds it later thinking it was forgotten.

**`SourceUrl` not made `required`.** It would turn a silent wrong answer into a refusal to start, but
it makes an unmodified deployment set a variable to discharge an obligation it does not have (§13 binds
*modified* versions), and it does not prevent what happened: the operator here *did* set it.

**No `REQUIREMENTS.md` edit**, on the v1.2 and v1.7 reasoning. R§8 has carried the AGPL §13 obligation
since rev 3 and S§11.9 has specified the mechanism since v1.3. This is a mechanism catching up with a
contract the tree already had, not new intent.

**Two smaller corrections folded in, named as such.** `OPERATIONS.md` §14 said `git tag v1.0.0` and
then listed `ghcr.io/kusl/myrestaurant:0.6.0`, with the `compose.override.yaml` example pinning `0.6.0`
too, while `release.yml`'s header comment said `v0.6.0`. One release, three places, two versions. Not a
finding — nothing unverifiable, no gate missing — but it is the paragraph a person reads *while*
cutting the first tag. Now one number.

---

## The gate

`ConfigurationSurfaceTests` **derives** the key set from `RestaurantOptions.FromConfiguration` rather
than listing it (F-47's habit, seventh application): a key is the first string literal after a
`configuration,` argument, with the span between required to be whitespace so a differently-shaped call
is skipped rather than guessed at. Seventeen keys, no duplicates.

1. **The scan read the tree** — ≥ 12 keys, none read twice. First and alone, because every assertion
   below is satisfied by an empty set (F-41).
2. **Every variable `Validate()` refuses to start over is one the binding method reads** — a second,
   independent observation of the same set from the same file. Catches a rename applied to one half.
3. **Every key reaches the container** — the `web` service's `environment` mapping. *This one found
   F-50.*
4. **Every key is in `.env.example`** — a commented-out line counts.
5. **Every key is in §13's table** — checked against the section, not the document.

The compose scan is **bounded to the `web` service** — two-space key to next two-space key, then the
`environment:` mapping, then its six-space children — because every service in that file takes an
`environment:` block and a key set on the wrong one would satisfy an unbounded scan while reaching
nothing.

Plain string operations, no `Regex`: there is none anywhere in this tree, and warnings are errors in
CI. No YAML parser either — a package dependency to check five lines of indentation is the worse trade,
and the same choice the CSP contract test makes.

---

## Verification performed before packaging

No .NET SDK and no container engine here, so nothing was reasoned about that could be executed instead.

- **The finding was measured, not spotted** — full set-difference census of the seventeen bound keys
  against all three restatements.
- **All five assertions executed as faithful ports** against the delivered tree. They fail on the
  Slice 24 tree at assertion 3 naming `RESTAURANT_SOURCE_URL`, and pass on this one. A gate written
  today that fails on today's tree is the strongest sensitivity proof available.
- **Sensitivity proven for the other four by planting damage**: a key dropped from `.env.example`; a
  key renamed in §13's table; a key renamed in a `Validate()` message only; and the missing key added
  to **`postgres`** instead of `web`. Each caught by its own assertion and no other. The last proves
  the service-boundary parse is doing work rather than searching the file.
- **The parser was written twice and compared** — regex first, plain string walk second. Both yield the
  same seventeen keys and fifteen validated names, which is what makes a hand-rolled scanner
  trustworthy with no compiler here to run it.
- **`SpecificationVersionTests` re-run**: header 1.10, entries 1.10 … 1.0 descending, both assertions
  hold. `Version.TryParse` reads `1.10` as minor **ten**, so it sorts above 1.9 — checked, because a
  string comparison would have got it backwards.
- **Both edited YAML files parsed with a real parser**, and the `web` environment mapping re-read from
  the parsed document: 20 keys, `RESTAURANT_SOURCE_URL` present with value `${RESTAURANT_SOURCE_URL:-}`,
  and absent from `postgres`.
- **Governance gate 3 re-run over every delivered file** — no new match; the §15 addition names a
  compose file and a command, not a platform setting (F-46's form).
- **Every documentation edit applied by exact-match replacement with an assertion that the anchor
  occurs exactly once.** Nothing edited by position. `BUILD_PROGRESS.md` is its existing bytes plus one
  appended section.
- **Byte hygiene on every delivered file**: LF only, final newline, no whitespace-only lines, no
  trailing whitespace, no context-dump separator. Brace/paren/bracket and string balance on both C#
  files. CS4007 and CS1620 scans clean — the one `${...}` sequence in a failure message was removed
  rather than left in a concatenation chain somebody might later make uniformly interpolated.

---

## Expected after applying

```
dotnet test        →  1056 total, 0 failed   (was 1051; five new, no existing test edited)
check_tree.sh      →  authored-text count +1 (the new test file)
ci_local.sh --with-all → 8 gates, same order as Slice 24
podman-compose --profile production config | grep RESTAURANT_SOURCE_URL
                   →  prints a line. Before this slice it printed nothing.
```

---

## Still open

**`Permissions-Policy`** — recorded by Slice 24, deliberately not answered here. It is a judgement
about a security header's cost-benefit on a deny-list surface; folding it into a slice about an
AGPL-compliance defect would muddy the record of both. Blocks nothing.

**Two operator actions** no archive can contain: enabling private vulnerability reporting on GitHub,
and setting the repository description (F-42).
