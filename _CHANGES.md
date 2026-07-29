# M6 Slice 2 — the end-to-end harness, and the first three scenarios

Every file below is a **full file** at its **repo-relative path**. Extract this archive at the repo root
and the contents drop straight over your working tree. `git status` will show exactly these 12 files as
modified/added, and **no deletions**.

```bash
tar -xzf m6-slice2-end-to-end-harness.tar.gz -C /home/kushal/src/dotnet/myrestaurant
```

## Files to DELETE

**None.** Nothing is renamed and nothing is superseded. No migration ships, no schema change, no
`Directory.Packages.props` edit, and nothing in `src/` is touched at all.

## The state I found

Green, on every gate: `total: 934, failed: 0, succeeded: 919, skipped: 15`, a clean `run.sh --smoke`, a
healthy `--containers-only`, and `scripts/ci_local.sh` passing all local gates including the strict
Release build. M6 Slice 1's own checklist is fully discharged. So this is the slice its closing note
promised: the E2E harness plus §16.3 scenarios 1, 13 and 14.

## New files (6)

All under `tests/MyRestaurant.EndToEnd.Tests/Harness/`:

- `RestaurantHarness.cs` — the class fixture. The opt-in gate, Chromium, the shared PostgreSQL 17
  container, and the factory that hands out instances. Every unavailability sets a `SkipReason`.
- `RestaurantInstance.cs` — one scenario's private stack: its own database, its own Data Protection key
  directory, the built application as a child process on its own loopback port, one browser context, one
  page, one virtual authenticator. Captures the app's stdout/stderr so a failure has the server's side of
  the story.
- `VirtualAuthenticator.cs` — the CDP `WebAuthn` domain, wrapped.
- `AccountJourneys.cs` — the page objects more than one scenario walks: the four-step `/setup` wizard
  (real attestation, real TOTP), sign-out, passkey sign-in.
- `WebApplicationLocator.cs` — finds the built application by walking up to `MyRestaurant.slnx` and
  mirroring this test assembly's own configuration and target framework.
- `ContainerEngineDiscovery.cs` — the rootless-Podman module initializer. Duplicated from
  `MyRestaurant.DataAccess.Tests` on purpose; the file says why.

Plus one docs append: `docs/_append/BUILD_PROGRESS-m6-slice-2.md`.

## Edited (6)

- `tests/MyRestaurant.EndToEnd.Tests/EndToEndScenarios.cs` — scenarios 1, 13 and 14 implemented; the
  other twelve stay as named placeholders with a skip reason that now says what each is waiting on.
- `tests/MyRestaurant.EndToEnd.Tests/MyRestaurant.EndToEnd.Tests.csproj` — `Npgsql` and
  `Testcontainers.PostgreSql` (both already pinned centrally), a `Microsoft.AspNetCore.App` framework
  reference, and project references to `MyRestaurant.WebApplication` (for `AccountRoutes`, and to make
  the app a build dependency) and `MyRestaurant.Domain` (for the real `JoinTokenService`,
  `Rfc6238Totp` and `Base32Text`).
- `.github/workflows/ci.yml` — a fourth gate, `end-to-end`.
- `scripts/ci_local.sh` — `--with-e2e`, and `--with-all`. Clean under `bash -n` as delivered.
- `README.md` — a new *End-to-end scenarios* section, the CI table's fourth row, status and roadmap.
- `_CHANGES.md` (this file)

No `docs/TECHNICAL_SPECIFICATION.md`, `docs/REQUIREMENTS.md`, `docs/DOCUMENTATION_REVIEW.md`, or ADR
edit: this realizes behaviour §16.3 already specifies, in the words it already uses.

## The four decisions worth arguing about

**A child process, not `WebApplicationFactory`.** A real browser cannot connect to an in-memory
`TestServer`, and `Program.cs` is top-level statements returning `int`, so its generated `Program` is
internal and not available as a `TEntryPoint`. Booting the built binary sidesteps both and is the more
honest test: same composition root, same DbUp pass, same fail-fast `Validate()`. A scenario that reaches
its first assertion has already proved what `boot-smoke` proves.

Two details carry it. `ASPNETCORE_CONTENTROOT` points at the **source** directory, because `Program.cs`
serves assets with `UseStaticFiles()` and `wwwroot` is not copied into `bin` — get this wrong and
`js/passkey.js` 404s and every passkey ceremony fails with nothing in the browser to explain it. And the
configuration and TFM come from the *test assembly's own* output path, so a Debug run boots a Debug app
and a Release run boots a Release one.

**`http://localhost:{port}` served, `https://localhost:{port}` configured.** The mismatch is load-bearing
in three directions at once: §13 refuses to start on a non-https public origin; Chromium treats
`localhost` as a secure context regardless of scheme, so WebAuthn works *and* the `Secure` §3.1
authentication cookie is accepted over plain HTTP; and only the host is ever compared, so
`WebAuthnOriginPolicy` matches and the §3.3 RP ID comes out `localhost` at both registration and sign-in
(ADR-0005 — `ServerDomain` is null by design). None of that is true of an IP address, which is why the
harness insists on the hostname.

**Scenario 14 needs no clock control.** §4.3's token is a pure function of `(secret, table, window)`, and
`JoinTokenService` lives in the Domain — so the harness inserts a table with a secret it chose and
computes tokens for `window − 4` and `window − 1` directly. The only hazard is the boundary rolling over
mid-assertion, a one-in-sixty flake at the app's default, so `TABLE_JOIN_TOKEN_ROTATION_SECONDS` is
per-instance and defaults to 3600. §4.3 accepts current-or-previous whatever their width, so nothing the
assertion depends on changes. Scenario 2 will ask the same knob for a *short* window, because crossing a
boundary is what it tests.

The acceptance assertion is the redirect: an anonymous scanner with a valid token gets a grant and is sent
to `/sign-in?ReturnUrl=/table/{id}` (§4.4 step 3). That is what "accepted" looks like from the guest's
side, and it needs no account at all.

**Opt-in, gated in CI rather than by hope.** The scenarios skip unless `MYRESTAURANT_E2E` is set, because
the first run downloads ~150 MB of Chromium and the rest of this suite is offline once packages are
restored. The coverage does not live behind the switch: the new `end-to-end` CI job sets it on every push,
and `--with-e2e` sets it locally. It is its own job so `build-and-test` does not pay for a browser to
answer a different question, and so a browser flake is attributable at a glance rather than buried in a
nine-hundred-fact summary. The job runs `playwright install --with-deps chromium` through the generated
`playwright.ps1`, because `--with-deps` needs root and a test process should never ask for that.

## Two things about driving the passkey UI

`passkey.js` starts a conditional-mediation request on page load, and a virtual authenticator that
simulates presence can satisfy it with no gesture — so by the time you look for the button, the form may
already have been submitted. `SignInWithPasskeyAsync` waits to see whether the page leaves on its own and
only drives the button when it has not. Clicking mid-flight is safe; the element aborts its own pending
request first.

And "left the sign-in page" compares the path **exactly**, not by prefix, so `/sign-in/two-factor` counts
as *left*. That is what makes scenario 13 fail with "landed on /sign-in/two-factor" instead of an
unexplained timeout — which is the entire value of that scenario.

## What I verified rather than guessed

The authoring environment has no SDK, so anything I could get wrong silently was checked against source:

- **Playwright 1.61.0** (`microsoft/playwright-dotnet` at `v1.61.0`): `ICDPSession.SendAsync(string,
  Dictionary<string, object>?) → Task<JsonElement?>` and that `args` is forwarded verbatim as CDP
  `params`; `IBrowserContext.NewCDPSessionAsync(IPage)`; `ICDPSession : IAsyncDisposable`;
  `IPlaywright : IDisposable`; `Program.Main(string[]) → int`; and every `IPage` / `ILocator` /
  `BrowserTypeLaunchOptions` / `BrowserNewContextOptions` member used here. There is **no**
  `TimeoutException` type in that tree, only `PlaywrightException`, which is what the waits catch.
- **Testcontainers 4.13.0**: `PostgreSqlBuilder(string image)` is current and the parameterless ctor is
  `[Obsolete]` — which matters under warnings-as-errors, and matches what `PostgreSqlFixture` already does.
- **xunit.analyzers**: `xUnit2013` returns early unless the expected size is 0 or 1, so
  `Assert.Equal(10, recoveryCodes.Count)` is clean.
- **`.editorconfig`**: `csharp_style_namespace_declarations = file_scoped:warning` and
  `csharp_prefer_braces = true:warning` are the only severity overrides, both honoured throughout.
- **Chromium's virtual-authenticator parameters**, and the 13x change that made two of them required —
  hence the attempt-then-retry.

One thing I could not verify: whether a headless Chromium on your Fedora box has every shared library it
wants. If it does not, that is a skip with the one-line fix in its message, not a failure.

## Build/test checklist for this slice

1. `dotnet restore` — two new package *references*, no new versions.
2. `dotnet build` — seven C# files in one test project; nothing in `src/` changed.
3. `dotnet test` — **still 934 total, 919 passing, 15 skipped.** Three facts moved from a discovery-time
   skip to a runtime one, which the summary counts identically.
4. `MYRESTAURANT_E2E=1 dotnet test tests/MyRestaurant.EndToEnd.Tests` — the real check for this slice.
   Expect **3 passed, 12 skipped**. First run also downloads Chromium; if it complains about shared
   libraries, run
   `pwsh tests/MyRestaurant.EndToEnd.Tests/bin/Debug/net10.0/playwright.ps1 install --with-deps chromium`
   once and retry.
5. `bash -n scripts/ci_local.sh` (clean as delivered), then `bash scripts/ci_local.sh --with-e2e`.
6. Push, and watch the new `end-to-end` job.

## Housekeeping carried over

`docs/BUILD_PROGRESS.md` still jumps from "M4 Slice 1" to "M5 Slice 2". Nine appends are now unmerged in
`docs/_append/`, including this slice's:

```bash
cat docs/_append/BUILD_PROGRESS-m4-slice-2.md >> docs/BUILD_PROGRESS.md
cat docs/_append/BUILD_PROGRESS-m4-slice-3.md >> docs/BUILD_PROGRESS.md
cat docs/_append/BUILD_PROGRESS-m4-slice-4.md >> docs/BUILD_PROGRESS.md
cat docs/_append/BUILD_PROGRESS-m5-slice-1.md >> docs/BUILD_PROGRESS.md
cat docs/_append/BUILD_PROGRESS-m5-slice-2.md >> docs/BUILD_PROGRESS.md
cat docs/_append/BUILD_PROGRESS-m5-slice-3.md >> docs/BUILD_PROGRESS.md
cat docs/_append/BUILD_PROGRESS-m5-slice-4.md >> docs/BUILD_PROGRESS.md
cat docs/_append/BUILD_PROGRESS-m5-slice-5.md >> docs/BUILD_PROGRESS.md
cat docs/_append/BUILD_PROGRESS-m6-slice-1.md >> docs/BUILD_PROGRESS.md
cat docs/_append/BUILD_PROGRESS-m6-slice-2.md >> docs/BUILD_PROGRESS.md
```

## What is next

The other twelve §16.3 scenarios, and the backup/restore drill as something executable. The scenarios are
incremental work now rather than a plumbing project — 2 wants a short rotation window and a second
browser context for the display device's principal; 3 through 11 want the guest registration journey and
two live circuits at once; 12 wants the obligations pipeline walked end to end.

## The one-line why

For five milestones the only thing that could tell you whether a guest could actually order dinner was
you, opening the app on a phone; there is now a machine that scans the code, presses the buttons, and
says so on every push.
