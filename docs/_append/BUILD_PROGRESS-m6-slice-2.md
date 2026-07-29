### M6 Slice 2 — the end-to-end harness, and the first three scenarios (landed)

§16.3 has listed fifteen required end-to-end scenarios since M1, and since M1 all fifteen have been
`[Fact(Skip = ...)]` — a version-controlled promise with nothing behind it. The skip reason said what was
missing: "needs Playwright browsers and a live instance". This slice supplies both, plus the two pieces
the reason did not mention (a WebAuthn virtual authenticator and a way to reason about token windows),
and then spends them on scenarios **1**, **13** and **14**.

No migration, no schema change, no `Directory.Packages.props` edit, nothing deleted, and nothing in
`src/` touched at all.

---

#### What a "live instance" turned out to mean

Six things, none of which existed:

1. **A database per scenario.** A shared PostgreSQL 17 container (Testcontainers, as the data-access
   tests already use) with a `CREATE DATABASE` per scenario.
2. **The application, actually running.** Booted from its own build output as a child process, on a
   loopback port reserved by binding port 0.
3. **A browser.** Chromium, launched once for the class, with a fresh context per scenario so cookies
   never leak between them.
4. **A software authenticator.** A CDP virtual authenticator, which is what §16.3 means by "passkey via
   virtual authenticator".
5. **An origin the app will accept and the browser will trust.** See below — this was the subtle one.
6. **A way to see why a scenario failed.** The browser only ever sees a 500; the server has the stack.
   The harness captures the child process's stdout and stderr and puts the tail in the exception message.

---

#### Why a child process rather than `WebApplicationFactory`

`WebApplicationFactory` hosts the app on an in-memory `TestServer`, and a real browser cannot connect to
an in-memory anything. The documented workaround — subclass it and start a second, Kestrel-backed host
alongside — additionally needs the entry point as a type argument, and `Program.cs` here is top-level
statements that `return 1` on invalid configuration, so its generated `Program` is internal.

Booting the built binary avoids both problems and is the more honest test besides. It runs the same
composition root, the same DbUp pass, and the same fail-fast `RestaurantOptions.Validate()` a deployment
runs. A scenario that reaches its first assertion has already proved what `boot-smoke` proves.

Two details make it work. `ASPNETCORE_CONTENTROOT` points at the web application's **source** directory,
because `Program.cs` serves assets with `UseStaticFiles()` and `wwwroot` is not copied into `bin` —
without it, `js/passkey.js` 404s and every passkey ceremony fails with nothing in the browser to say
why. And the configuration and target framework are read from the *test assembly's own* output path, so
a Debug run boots a Debug application and a Release run boots a Release one; a hard-coded either would
have silently tested a stale binary on one of the two.

---

#### The origin, which is where this could have quietly not worked

The app is served at `http://localhost:{port}` while `RESTAURANT_PUBLIC_ORIGIN` is set to
`https://localhost:{port}`. That mismatch is deliberate and it is doing three jobs at once:

- §13 **refuses to start** on a non-https public origin, so the configured value has to say https.
- Chromium treats `localhost` as a **secure context regardless of scheme**, so `navigator.credentials`
  is available and the §3.1 authentication cookie — `CookieSecurePolicy.Always`, i.e. `Secure` — is
  accepted over plain HTTP. Neither is true of `http://192.168.x.x`, which is why the harness insists on
  the hostname rather than an address.
- Only the **host** is ever compared. `WebAuthnOriginPolicy` matches `localhost:{port}` exactly and
  separately treats loopback as trusted for either scheme; the §3.3 relying-party ID is
  `ServerDomain ?? Request.Host.Host` and `ServerDomain` is null by design (ADR-0005), so it comes out
  `localhost` at registration and `localhost` again at sign-in. A passkey registered in scenario 13's
  setup pass is therefore usable by its sign-in pass, on the same instance, same port.

`PublicOriginMiddleware` leaves the host alone here, because it already matches the configured origin —
which is the branch that says "keep it".

---

#### Scenario 14 needs no clock control, and that is the point

The obvious reading of "expired token URL; token from the previous window" is that it wants a
controllable server clock. It does not. §4.3's token is a pure function of `(join_secret, table_uuid,
window_index)`, and `JoinTokenService` is in the Domain, so the harness can compute a token for *any*
window from the secret it chose. Scenario 14 inserts a table with a known 32-byte secret, asks for
`window − 4` (inside the bounded lookback, so `Expired` rather than `Invalid`) and then `window − 1`.

The one real hazard is the boundary rolling over between computing a token and the server validating it,
which at the app's 60-second default would be a one-in-sixty flake. The harness therefore takes
`TABLE_JOIN_TOKEN_ROTATION_SECONDS` per instance and defaults it to **3600**. §4.3 accepts the current
and previous window whatever their width, so nothing the assertion depends on changes — the race simply
stops existing. Scenario 2, when it lands, will ask the same harness for a *short* window, because
crossing a boundary is the thing it is testing.

The table is inserted by direct SQL rather than through `/administration/tables/new`, also deliberately:
a token-window assertion that first has to sign in, pass a role check and submit a form can fail for six
reasons that have nothing to do with token windows. Scenario 2 *is* about that flow and will use the UI.

And the acceptance assertion is the redirect. An anonymous scanner presenting a valid token gets a grant
cookie and is sent to `/sign-in?ReturnUrl=/table/{id}` — §4.4 step (3). That redirect is what "accepted"
looks like from the guest's side, and it needs no account, no password, and no second page object.

---

#### Two things about driving the passkey UI that are not obvious

**Conditional mediation races the button.** `passkey.js` starts a `mediation: 'conditional'` request the
moment the sign-in page loads, and a virtual authenticator with `automaticPresenceSimulation` can satisfy
it with no gesture at all — at which point the form has already been submitted and there is no button
left to click. `SignInWithPasskeyAsync` therefore waits a few seconds to see whether the page leaves on
its own and only drives the button when it has not. Clicking during an in-flight conditional request is
safe: the element aborts its own pending request before starting a new one.

**The "left the sign-in page" predicate compares the path exactly**, not by prefix. `/sign-in/two-factor`
has to count as *left*, so that scenario 13 fails with "landed on /sign-in/two-factor" rather than with
an unexplained thirty-second timeout. The failure message is the whole reason that scenario exists.

Two smaller notes. `defaultBackupEligibility` and `defaultBackupState` arrived in Chromium's 13x line and
became **required** in the same change, so a single fixed argument list to
`WebAuthn.addVirtualAuthenticator` is wrong on one side of that boundary; the call is attempted with them
and retried without. They are not merely tolerated either — the §3.3 store persists `is_backup_eligible`
and `is_backed_up` and the assertion handler reads them back, so exercising credentials that carry the
bits set is the more faithful test. And the wizard's every step posts to `/setup` and redirects to
`/setup`, so the URL never changes and cannot be waited on; each step is identified by the element that
only exists on it.

---

#### Opt-in, and why that is not a cop-out

The scenarios skip unless `MYRESTAURANT_E2E` is set. The first run downloads roughly 150 MB of Chromium
into `~/.cache/ms-playwright`, and the rest of this suite is entirely offline once packages are restored;
a `dotnet test` that silently starts a 150 MB download is a `dotnet test` people stop trusting.

The switch is not where the coverage lives, though. CI's new **`end-to-end`** job sets it on every push,
and `scripts/ci_local.sh --with-e2e` sets it locally. It is its own job rather than part of
`build-and-test` for two reasons: `build-and-test` would otherwise pay for a browser on every run to
answer a different question, and a browser-driven flake should be attributable at a glance instead of
hiding inside a nine-hundred-fact summary. The job also runs
`playwright install --with-deps chromium` through the generated `playwright.ps1`, because `--with-deps`
needs apt and therefore root, which a test process has no business asking for.

Everything else that can be absent is also a skip with the fix in its message: no container engine, no
browser, no build output, no `MyRestaurant.slnx` above the test assembly. A missing tool is not a broken
product, and a suite that cannot tell the two apart is a suite nobody reads.

`ContainerEngineDiscovery` is duplicated from `MyRestaurant.DataAccess.Tests` rather than shared. A
`[ModuleInitializer]` runs once per *assembly* load and Testcontainers snapshots its environment into
static singletons on first touch, so a helper living in the other test project cannot run in this one's
process. The alternative was a fourth project in the solution for thirty lines that must not have a
public API.

---

#### Where to look if this breaks

1. **Everything skips with "opt-in".** Working as intended. `scripts/ci_local.sh --with-e2e`.
2. **"The built web application could not be found."** The message names the exact path it wanted. Build
   the solution in the same configuration you are testing in.
3. **"Chromium could not be started."** Almost always missing shared libraries on a minimal host. Run
   `pwsh tests/MyRestaurant.EndToEnd.Tests/bin/Debug/net10.0/playwright.ps1 install --with-deps chromium`
   once.
4. **"exited with code 1 before it became ready."** The captured output is in the message and it will be
   a `Configuration error:` line — `RestaurantOptions.Validate()` refusing something the harness set.
5. **A passkey step times out waiting for `p.totp-secret`.** The attestation failed. Check the captured
   output for the ceremony, and check that `js/passkey.js` was served — a wrong content root turns this
   into a silent no-op.
6. **Scenario 14's second navigation shows the expired page.** The token was computed for a window the
   server no longer accepts. Confirm the instance really got `TABLE_JOIN_TOKEN_ROTATION_SECONDS=3600`.

---

#### Build/test checklist for this slice

1. `dotnet restore` — two package references are added to the end-to-end project (`Npgsql`,
   `Testcontainers.PostgreSql`), both already pinned centrally, so no version resolution is new.
2. `dotnet build` — the new code is seven C# files in one test project. Nothing in `src/` changed, so a
   break here is in the harness, not the product.
3. `dotnet test` — **still 934 total, 919 passing, 15 skipped.** Three facts changed from
   `[Fact(Skip)]` to `[Fact]` and now skip at runtime instead of at discovery, which the summary counts
   the same way. If the total moved, something else moved with it.
4. `MYRESTAURANT_E2E=1 dotnet test tests/MyRestaurant.EndToEnd.Tests` — the real check. Expect
   **12 skipped, 3 passed**, and roughly a minute per scenario the first time (Chromium download aside).
5. `bash scripts/ci_local.sh` unchanged, then `bash scripts/ci_local.sh --with-e2e` once.
6. Push, and watch the new `end-to-end` job.

---

#### What is left in M6

The other twelve §16.3 scenarios, and the backup/restore drill as something executable rather than a
runbook prose section. The scenarios are now incremental work rather than a plumbing project: 2 wants a
short rotation window and a second context for the display device's principal, 3–11 want the guest
registration journey and two live circuits at once, 12 wants the obligations pipeline walked end to end.
