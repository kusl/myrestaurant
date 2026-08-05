# M6 close-out — the release: what the program says about itself, and to whom

Every file below is a **full file** at its **repo-relative path**. Extract at the repository root and
the contents drop straight over your working tree — no diffs, no patches, no scripts to run.

```bash
tar -xzf m6-closeout-release.tar.gz -C /home/kushal/src/dotnet/myrestaurant
```

## Files to DELETE

**None.** Nothing here renames, supersedes or orphans anything: no migration, no schema change, no
package change, no ADR edit, no `.slnx` edit. `Program.cs` changes by **one statement** (noted below,
since that is rare). Four new files land in existing folders, so no `.csproj` edit either.

## The files

| File | Change |
| --- | --- |
| `src/…/Configuration/BuildInformation.cs` | **new** — parses the assembly's informational version into a version and a source revision |
| `src/…/Configuration/SourceRoutes.cs` | **new** — the `/source` route constant, in one place |
| `src/…/Components/Pages/Source.razor` | **new** — the AGPL §13 offer, naming this build's revision |
| `src/…/Components/Layout/AppColophon.razor` | **new** — the footer line, every page, both layouts |
| `src/…/Configuration/RestaurantOptions.cs` | `RESTAURANT_SOURCE_URL` + its validation |
| `src/…/Identity/ObligationsEnforcement.cs` | `/source` added to the §3.5 exemption list |
| `src/…/Components/Layout/MainLayout.razor` | renders `<AppColophon />` |
| `src/…/Components/Layout/DisplayLayout.razor` | renders `<AppColophon />`, plus its own smaller styling |
| `src/…/Components/Pages/Home.razor` | the stale "the event explorer arrives next" lede, rewritten |
| `src/…/wwwroot/app.css` | colophon and `/source` styles; `.app-footer`'s bottom padding moves |
| `src/…/Program.cs` | **one statement**: `serviceVersion` on the OpenTelemetry resource |
| `Directory.Build.props` | `VersionPrefix` and assembly metadata |
| `Containerfile` | `VERSION` / `SOURCE_REVISION` build arguments → `InformationalVersion` |
| `.github/workflows/ci.yml` | stamps the image; new gate: `/source` must name the built commit |
| `.github/workflows/release.yml` | version from the tag → the image; a GitHub release on the tag |
| `.env.example` | `RESTAURANT_SOURCE_URL`, documented for forks |
| `tests/…/BuildInformationTests.cs` | **new** — 16 facts on the parse |
| `tests/…/RestaurantOptionsTests.cs` | +8 facts: the source URL, and the five shapes refused |
| `tests/…/Identity/ObligationsEnforcementTests.cs` | +1: `/source` is exempt |
| `docs/REQUIREMENTS.md` | **rev 3** — one new §8 principle |
| `docs/TECHNICAL_SPECIFICATION.md` | **v1.3** — new §11.9; §12, §13, §16.4, §19, Appendix A |
| `docs/DOCUMENTATION_REVIEW.md` | **F-39** entered; status line and "Going forward" extended |
| `docs/OPERATIONS.md` | §14 release procedure rewritten; **new §15** on fork obligations |
| `docs/BUILD_PROGRESS.md` | stage checkboxes ticked; close-out appended (**complete file**, 5,946 lines) |
| `README.md` | the status paragraph, the scenario table, CI, backups and the roadmap, all corrected |
| `_CHANGES.md` | this file |

## Why there is a slice here at all

Slice 16 ended with *"What is left in M6: **Nothing**. The next move is not a slice, it is a release."*
That was true of the feature list. It was not true of the tree, and the gap only became visible by
reading the repository against **what a tag would make true** rather than against §19.

Publishing changes who the audience is. Two questions that had obvious answers while one person ran one
instance stop having them the moment somebody else can `podman pull` the thing.

### 1. Nothing could say which build it was

`Directory.Build.props` set no `VersionPrefix`, so every assembly reported the SDK's default `1.0.0`.
`Program.cs` called `AddService(serviceName: "myrestaurant")` with **no `serviceVersion`**, so every
trace and metric leaving the process was unversioned. No surface reported a build at all. The only
available answer to "which build is on that box?" was *whatever the person who deployed it typed* —
which is not an answer, it is a memory.

### 2. Nothing offered anybody the source

R§1 says the project is published *"so anyone may run their own copy under the AGPL"*.
`CONTRIBUTING.md` has told forks since rev 1 that *"your fork owes its users the same"*. AGPL-3.0-only
§13 — read out of this repository's own `LICENSE` rather than recalled — asks a **modified** version to
prominently offer its users the corresponding source. Nothing in the application made that
dischargeable, so a fork operator complied by writing a page from scratch or, far more likely, not at
all.

Both land together because they are one thing: §13 offers the source *of the version being interacted
with*, so an offer that cannot name a revision is approximate. Both land **before** the tag, because
the first tag is the version people cite.

This is **F-39**, and it is the same shape as F-35 and F-37 — a capability the surrounding documents
assumed and §19's build order never claimed.

## The SourceLink trap, which is why the Containerfile does this and not MSBuild

The obvious implementation is `-p:SourceRevisionId=$SHA` and let the SDK append it to
`InformationalVersion`. **That silently does nothing here.**
`AddSourceRevisionToInformationalVersion` in `Microsoft.NET.GenerateAssemblyInfo.targets` is
conditioned on `SourceControlInformationFeatureSupported`, and a code search of `dotnet/sdk` finds that
property in exactly two files — that target, and its own test. **SourceLink sets it. Nothing else in
the SDK does.**

Read out of the SDK source rather than recalled, because the failure mode is a build that succeeds and
a page that quietly reports "Not recorded" forever.

So the `Containerfile` passes it explicitly, and a package dependency to obtain one string was the
worse trade:

```dockerfile
ARG VERSION=1.0.0
ARG SOURCE_REVISION=
RUN INFORMATIONAL_VERSION="${VERSION}${SOURCE_REVISION:++${SOURCE_REVISION}}" \
    && dotnet publish … /p:Version="${VERSION}" /p:InformationalVersion="${INFORMATIONAL_VERSION}"
```

`${SOURCE_REVISION:+…}` expands to `+<revision>` only when the argument is set and non-empty, so an
unstamped build produces a clean `1.0.0` rather than a trailing `+` the parser would have to treat as a
revision it does not have.

## The gate, which is the part that will still be true in a year

F-38's lesson was *a row in the embodiment column should name something executable*. This is the first
chance to apply it without being asked, so `boot-smoke` gained:

```yaml
- name: the source offer names this commit
  run: |
    page=$(curl --fail --silent http://127.0.0.1:8080/source)
    grep --quiet --fixed-strings "${{ github.sha }}" <<<"$page"
```

The stamp travels from a build argument through an MSBuild property, an assembly attribute, a parse and
a component. **Every link in that chain fails silently** — the page still renders, and it renders "Not
recorded", which reads as a configuration choice rather than a defect. The commit appearing in the
response is the one assertion a broken chain cannot satisfy. It doubles as a reachability check: no
cookie is sent, so a regression that put the licence offer behind authentication fails here too.

## Four design decisions worth being able to veto

**The colophon is a sibling of the clock's `<footer>`, not a child.** `RestaurantClockFooter` owns that
element and pins `ShouldRender() => false` because a script owns its text node after first paint.
The two are rendered as a pair by both layouts and by nothing else, so they are *styled* as one bar —
which is why `.app-footer`'s `padding-bottom` moved to the colophon along with the
`env(safe-area-inset-bottom)`.

**`/source` is on §3.5's exemption list.** The obligations pipeline stops a flagged principal *acting*
until they have changed a password or enrolled an authenticator. That is not a reason to withhold the
licence under which they are being shown a page — and the footer they are looking at links there, so
the alternative is a visible dead link on the one page they can see.

**There is no off switch, and the version is not hidden.** An offer with an off switch is not one. As
for the version: the source is public, the tags are public, the digests are public, so concealing the
number would protect nothing and break an offer that is supposed to name what it is offering.

**The revision is text, not a link.** `{url}/tree/{revision}` would be the page guessing at the URL
layout of a forge it has never been told the identity of. GitHub, GitLab, Gitea, cgit and Sourcehut do
not agree, and a link that 404s is worse than a hash somebody can paste into `git checkout`.

## Three things fixed in passing

**The specification's header said v1.1** while its own changelog already carried a v1.2 entry — Slice 16
bumped one and not the other.

**Every stage checkbox from 2 to 6 was unticked**, with Stage 2 still marked "in progress" through four
completed milestones.

**`Home.razor` told every visitor the event explorer "arrives next"** — it shipped in M5. A landing page
is the worst place in an application to carry a roadmap: it is the one text nobody re-reads and
everybody sees.

## One documentation decision, stated so you can veto it

`REQUIREMENTS.md` moves to **rev 3**. This is the opposite call from Slice 16, which deliberately left
the requirements untouched, and the difference is real. §15's key-ring sentence was **already the
contract** and the code had failed to honour it — a defect fix at the mechanism level. Here, nothing
previously said the running program must name itself or offer its source. `CONTRIBUTING.md` said a fork
*owes* it and R§1 said the project is published under the AGPL, but neither is a requirement on the
program's behaviour. **This is new intent, so the requirements move.**

If you would rather this landed as a mechanism-level fix with no revision bump, the edit is one bullet
in R§8 and one line in the revision history; say so and I will pull it back out.

## Build and test

```bash
dotnet build
#    expect: all seven projects succeed, 0 errors.

dotnet test
#    expect: 996 total, 0 failed, 982 succeeded, 14 skipped.
#    25 more facts than Slice 16: 16 in the new BuildInformationTests, 8 in RestaurantOptionsTests,
#    1 InlineData in ObligationsEnforcementTests. No test moves in or out of the skipped column.

bash scripts/ci_local.sh --with-all

# then, before tagging, confirm the whole stamp chain on a real build:
podman build --file Containerfile --tag myrestaurant_web:stamped \
    --build-arg SOURCE_REVISION="$(git rev-parse HEAD)" .
#    boot it and open /source: it must name that commit, not "Not recorded".
```

**Cutting the tag**, once the above is green:

```bash
# 1. VersionPrefix in Directory.Build.props is already 1.0.0. Confirm that is what you want.
# 2. Tag and push:
git tag --annotate v1.0.0 --message 'M6 complete'
git push origin v1.0.0
```

`release.yml` re-runs every CI gate, derives `1.0.0` from the tag, passes it and the commit into the
image build, publishes `ghcr.io/kusl/myrestaurant:1.0.0`, `:1.0` and `:sha-<commit>`, and opens a
GitHub release. The `release-notes` job holds the only `contents: write` in either workflow.

## What was actually verified here

No .NET SDK in the sandbox and no container engine, so **nothing here has been compiled or executed.**
What *was* run:

- Brace, paren and bracket balance on every C#, Razor and CSS file touched.
- A tag-balance parse of all five touched components, with Razor comments, `<style>` bodies and
  `@code` blocks stripped first.
- A YAML parse of both workflows: `ci.yml` is four jobs with **ten** steps in `boot-smoke` (was nine);
  `release.yml` is three jobs.
- The SDK's `Microsoft.NET.GenerateAssemblyInfo.targets` and `Microsoft.NET.DefaultAssemblyInfo.targets`
  read out of `dotnet/sdk` — the SourceLink condition, and that `Version` falls back to `VersionPrefix`.
- AGPL §13 read out of this repository's own `LICENSE`.
- All eight GitHub Action majors in both workflows checked against the API: `checkout@v7`,
  `setup-dotnet@v6`, `cache@v6`, `upload-artifact@v7`, `setup-buildx-action@v4`, `login-action@v4`,
  `metadata-action@v6`, `build-push-action@v7`. All current. `release.yml` has never run; if one had
  been wrong, the first tag is where it would have surfaced.
- Every documentation edit applied by exact-match replacement with an assertion that the anchor appears
  **exactly once**, so nothing was edited by position.

## Where to look if this breaks

**The build fails on `BuildInformation`.** It uses `char.IsAsciiHexDigit` and a range expression; both
are fine on net10.0. More likely is the `required` modifier interacting with an analyzer setting — the
type mirrors `RestaurantOptions`, which already uses `required` throughout, so it should not.

**`/source` renders "Not recorded" in production.** The image was built without
`--build-arg SOURCE_REVISION=…`. The default `podman-compose --build` path does exactly that, and it is
the honest answer — a locally built image genuinely does not know its commit. Pass the argument, or
deploy the published image.

**CI's new step fails but the page looks fine in a browser.** Compare the rendered revision against
`github.sha`. A truncated one means something abbreviated it before the page did; a missing one means
the build argument did not reach `dotnet publish`, and the `echo "building MyRestaurant …"` line in the
Containerfile's `RUN` is the first place that shows.

**The footer looks wrong — a gap, or the clock crowded against the bottom.** `.app-footer`'s
`padding-bottom` moved to `.app-colophon`. Those two are always rendered as a pair; if you add a layout
that renders `<RestaurantClockFooter />` without `<AppColophon />`, that assumption breaks and the
clock will sit flush against the viewport edge.

**A person mid-obligation cannot reach `/source`.** `SourceRoutes.Source` did not make it into
`ObligationsEnforcement.IsExemptPath`. There is a fact for exactly this.

**`release-notes` fails with a permissions error.** That job carries `contents: write` at job scope
while the workflow default is `contents: read`. If the repository's Actions settings force read-only
tokens, the job-level grant cannot override it — the setting is at Settings → Actions → Workflow
permissions.

################################################################################
