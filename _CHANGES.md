# M6 Slice 5 — guests can register

Every file below is a **full file** at its **repo-relative path**. Extract this archive at the repo root
and the contents drop straight over your working tree. `git status` will show exactly these 16 files as
modified/added, and **no deletions**.

```bash
tar -xzf m6-slice5-guest-registration.tar.gz -C /home/kushal/src/dotnet/myrestaurant
```

## Files to DELETE

**None.** Nothing is renamed or superseded. No migration, no schema change, no
`Directory.Packages.props` edit, no new package, no `Program.cs` edit, no `.slnx` edit.

## The state I found

Slice 4 landed clean. `bash scripts/ci_local.sh --with-all` green end to end: `dotnet test`
**950 total / 0 failed / 935 succeeded / 15 skipped**, `MYRESTAURANT_E2E=1` **5 passed / 10 skipped**,
`run.sh --smoke` healthy, quick tunnel up. Exactly the numbers Slice 4 predicted.

## What this slice is, and why it is not what Slice 4 said it would be

Slice 4 closed with "scenario **3** is next and the last with plumbing left in it." That was wrong in an
instructive way. Scenario 3 needed no plumbing. It needed a **page that does not exist**.

There is no `/register`. Thirty-six routable pages, and none of them is a registration surface.
`SignIn.razor` said so in its own footnote — *"Guests register at the table by scanning its display —
there is no stand-alone registration page"* — and `TableJoin.razor` sends an anonymous grant-holder to
`/sign-in`, which for a first-time guest is a door with nothing behind it. `BUILD_PROGRESS.md` had it
flagged twice already (M2's limitations; the M3 join slice's *"Guest self-registration on the join path
is still to come"*).

Meanwhile the specification mandates it in three places:

- **R§4.3** — "Guests self-register at the moment of joining a table: username, optional display name,
  and at least one credential — passkey offered first, password accepted."
- **S§4.4** — "…continue to sign-in/**registration** if anonymous… Registration mid-flow: the grant
  cookie survives the passkey ceremony; **that is its purpose**."
- **S§11.1** — "Anonymous with valid token → grant → sign-in/registration (passkey-first, password
  offered) → join."

So this is a product slice that happens to unblock a test. It also unblocks §16.3 scenarios **4**
through **11**, every one of which needs a guest with an account — and until now the only way to get a
non-administrator account was for an administrator to create a *staff* account with
`must_change_password` set, which is not the thing those scenarios are about.

**No specification edit.** Nothing here changes what §4.3, §4.4 or §11.1 say; three of their sentences
become true. The route names are new, but §11 has never enumerated routes at that grain —
`/account/passkeys` and `/account/change-password` are described, not named, either.

## New files (7)

- `src/MyRestaurant.DataAccess/Identity/GuestRegistration.cs` — `NewGuestAccount`,
  `GuestRegistrationStatus`, `IGuestRegistration`, `DapperGuestRegistration`.
- `src/MyRestaurant.WebApplication/Identity/RegistrationState.cs` — `RegistrationTicket`,
  `RegistrationTicketProtector`, `RegistrationCookie`.
- `src/MyRestaurant.WebApplication/Components/Account/Pages/Register.razor` — the surface.
- `tests/MyRestaurant.DataAccess.Tests/Identity/GuestRegistrationTests.cs` — 7 integration tests.
- `tests/MyRestaurant.WebApplication.Tests/Identity/RegistrationTicketTests.cs` — 11 cases.
- `tests/MyRestaurant.EndToEnd.Tests/Harness/TableJourneys.cs` — the guest-side join journeys.
- `docs/_append/BUILD_PROGRESS-m6-slice-5.md`

## Edited (9)

- `src/MyRestaurant.WebApplication/Identity/ObligationsEnforcement.cs` — two constants on
  `AccountRoutes`: `Register` and `RegistrationPasskeyCreationOptions`. Nothing else; the obligations
  pipeline needs no exemption, because it only acts on authenticated principals and `/register` is
  anonymous by definition.
- `src/MyRestaurant.WebApplication/Identity/AccountEndpoints.cs` — the anonymous
  `/register/passkey/creation-options` endpoint, gated on the registration cookie.
- `src/MyRestaurant.WebApplication/Identity/IdentityServiceCollectionExtensions.cs` — registers
  `IGuestRegistration`; one line added to the summary list.
- `src/MyRestaurant.WebApplication/Components/Account/Pages/SignIn.razor` — the footnote becomes a
  **"Create an account"** link carrying `ReturnUrl`, plus the `RegisterUrl` helper.
- `tests/MyRestaurant.WebApplication.Tests/Identity/IdentityWiringTests.cs` — one resolvability test.
- `tests/MyRestaurant.EndToEnd.Tests/Harness/RestaurantInstance.cs` —
  `OpenIsolatedPageAsync(withVirtualAuthenticator:)`, `ReadOpenSittingAsync`, the `OpenSitting` record,
  and authenticator teardown ahead of context close.
- `tests/MyRestaurant.EndToEnd.Tests/Harness/AccountJourneys.cs` — `GuestAccount` and
  `RegisterGuestWithPasskeyAsync`.
- `tests/MyRestaurant.EndToEnd.Tests/EndToEndScenarios.cs` — scenario **3** implemented; the
  `[Fact(Skip)]` placeholder removed.
- `_CHANGES.md` (this file)

## The surface

Anonymous, static SSR, two steps over a Data-Protection-protected cookie:

1. **Details** — username, optional display name, **optional** password (hashed at once; the ticket
   carries a PHC string, never a plaintext).
2. **Sign-in method** — the real attestation ceremony. Registering commits the account; *"Not now — use
   my password"* commits on the password alone, **and renders only when a password was set**.

That asymmetry is §3.3's rule made structural rather than advisory. A passkey is "always offered, never
required, never a gate for guests" — so declining is offered exactly when there is something to fall
back on. `RegistrationTicket.CanDeclineThePasskey` is the single predicate the markup and the POST
handler both consult, and an account with neither credential is refused twice: the button is absent, and
`DapperGuestRegistration` throws before any SQL runs.

**Why a ticket at all, for a two-step form.** A WebAuthn attestation needs a user handle *before* the
account exists, and that handle must equal the eventual `person` row's id or a later
discoverable-credential sign-in presents a handle matching nobody. Same problem `/setup` has, same
solution (§3.6). Unlike `SetupTicket` there is **no step enum**: the wizard has three ordered steps that
must each be unskippable, registration has one, and the ticket's existence *is* the state.

No TOTP step (§3.4 pairs the authenticator with the password path for staff) and no role — a guest is
the *absence* of a grant (§3.7), so nothing touches `person_role`.

## The decision worth arguing about

**`/register` is not rate-limited, and that was a decision rather than an omission** — you asked to leave
it out, and here is what "out" costs so it stays visible.

`/display/pair` is the only limited endpoint (§4.2: 5/min/IP). `/register` is now a second anonymous
surface that writes a row, and the more consequential one, since a `person` row outlives the request.

The reason it is not a two-line addition: the limiter is configured inside `AddRestaurantDisplays`, and
`RateLimiterOptions.OnRejected` and `RejectionStatusCode` are single-valued. A second `AddRateLimiter`
call adding a registration policy would silently take over the rejection handler, and a refused
registration would answer with *"Too many pairing attempts from this device"* — worse than no limit,
because it is wrong and looks deliberate. Doing it properly means `OnRejected` dispatching on the
endpoint, which is an edit to a file this slice otherwise does not touch, and a policy §13 does not
specify.

What stands meanwhile: registration is a **two-request** flow behind an antiforgery token and a
protected ticket cookie, so it is not a scriptable single POST; the password is capped at 256 characters
so an anonymous caller cannot ask for unbounded Argon2id work; and §3.2's semaphore bounds concurrent
hashes process-wide regardless. Recorded as a named follow-up in the BUILD_PROGRESS append.

## Scenario 3, and the parenthetical nobody had tested

§16.3's full text is *"Guest scans (simulated URL from current token) → registers with passkey
**(slowly — grant outlives token)** → joins; sitting created."* The parenthetical is the whole reason
§4.4's grant exists, and it had never been tested — there was nothing to be slow at.

The instance runs at `TABLE_JOIN_TOKEN_ROTATION_SECONDS = 10`, §13's floor and the only scenario that
wants it: §4.3 accepts the current *and* previous window, so a scanned token is provably dead about
twenty seconds later. At the harness default of an hour the assertion would be vacuous.

- Scans a live token in the guest's **own** context with its **own** virtual authenticator — a WebAuthn
  credential belongs to the authenticator that minted it, so a passkey created on the shared
  administrator context would be one this guest could never use.
- Follows the **"Create an account"** link rather than typing `/register`. That link carrying the return
  URL is the entire mechanism by which registering lands the guest back at the table; a scenario that
  navigated directly would assert on a path no guest can take.
- Registers with a passkey and no password.
- Asserts the URL it returns to carries **no token at all**, and shows the join confirmation.
- Waits until the scanned token is past §4.3's window — measured from the instant it was minted, not
  from now, because registration already spent some of it — and proves it dead by re-scanning in a
  **third** context, one with no grant cookie that could carry the navigation past a refusal and quietly
  turn a failure into a pass.
- Joins anyway, then checks the database for exactly one open sitting on that table with exactly this
  guest on its roster. Not redundant with the page: a second sitting that
  `table_sitting_one_open_per_table` should have prevented looks identical from a seat.

## The bug I caught in review, before you did

My first `TableJourneys.ScanAsync` built the scan URL with
`JoinTokenService.BuildJoinUrl(instance.PublicOrigin, …)` — which is `https://localhost:{port}` while
Kestrel serves plain **HTTP** on that port. It would have reached nothing, and the failure would have
read as a scenario problem rather than a harness one.

It navigates to a relative path now, with the reason written into the type's remarks rather than merely
patched: the origin mismatch is deliberate and load-bearing (§13 refuses a non-https public origin;
Chromium treats `localhost` as a secure context regardless of scheme), so the absolute URL a display
encodes is by construction not fetchable from the harness. Scenario 2 already asserts that a real screen
encodes exactly the code the table's secret produces, so between the two nothing is assumed.

## What I verified rather than guessed

- **`passkey.js`** — `tryAutofillPasskey` runs only for `operation === 'Request'`, so the `Create`
  element on `/register` does not self-fire on load; and `obtainAndSubmitCredential` **returns without
  submitting** when a conditional-mediation attempt fails. That second one matters here: the guest lands
  on `/sign-in` with an authenticator holding no credentials, and if a failed conditional request
  submitted the form the browser would navigate away before the "Create an account" link could be
  clicked. It does not.
- **`creation-options-url`** is read from the element's attribute and overrides the default, which is how
  `/setup` already points attestation at its own anonymous endpoint — the same mechanism, reused.
- **`App.razor`** — `RenderModeForPage` is `null` when `AcceptsInteractiveRouting()` is false, so
  `[ExcludeFromInteractiveRouting]` genuinely yields static SSR and the page can write cookies.
- **`ObligationsMiddleware`** — acts only when `user.Identity?.IsAuthenticated == true`, so an anonymous
  `/register` needs no entry in `IsExemptPath`.
- **`TableOrderSurface.razor`** — its four buttons are "Add to basket", "Send", and two `link-button`
  row actions. None contains "Join", so `JoinStageOnScreen`'s `button:has-text('Join')` discriminates
  Confirm from Member cleanly even though both render the table label as their `h1`.
- **`RestaurantHarness.StartInstanceAsync(int, CancellationToken)`** — parameter order and defaults
  unchanged; scenario 3 passes the rotation positionally like 2 and 15 do.
- **`DapperUserStore`** — `FindByNameAsync`, `FindPasskeyAsync(user, credentialId, ct)` and
  `GetTwoFactorEnabledAsync` signatures, for the round-trip test.
- **`Directory.Build.props`** — `GenerateDocumentationFile` is false, so cref resolution is not
  compiled; `TreatWarningsAsErrors` is on only under `ContinuousIntegrationBuild`, which is what the
  CI run will apply.

## Build/test checklist for this slice

1. `dotnet build` — one new `.razor` and one edited one. **The Razor is the thing to watch**, as always:
   `Register.razor` has three `@formname` forms on one page (`register-passkey`,
   `register-skip-passkey`, `register-restart`) plus an `EditForm` named `register-details`, a nested
   `DetailsInputModel : IValidatableObject` reaching the enclosing component's private
   `MinimumPasswordLength` const, and a `PasskeySubmit` with `CreationOptionsUrl` set.
2. `dotnet test` — expect **969 total, 0 failed, 954 succeeded, 15 skipped** (was 950/0/935/15). The
   arithmetic: +7 `GuestRegistrationTests`, +11 `RegistrationTicketTests`, +1 `IdentityWiringTests` = 19
   new; scenario 3 stops being a `[Fact(Skip)]` placeholder and becomes a real `[Fact]` that skips on the
   `MYRESTAURANT_E2E` gate instead, so the skip count is unchanged at 15.
3. `MYRESTAURANT_E2E=1 dotnet test tests/MyRestaurant.EndToEnd.Tests` — expect **6 passed, 9 skipped**.
   Scenario 3 adds roughly 30–40 s: a WebAuthn ceremony plus the deliberate wait for the token to die.
4. `bash scripts/ci_local.sh --with-all`.
5. Push, and watch the `end-to-end` job.

**Worth doing by hand once**, because no test covers it and it is the other branch of the surface: run
`bash run.sh`, open `/register`, fill in a username **and** a password, and take *"Not now — use my
password"*. You should land signed in with a password-only guest account. Then sign out and sign back in
with it.

## Where to look if this breaks

- **`RegisterGuestWithPasskeyAsync` fails at the link** ("offered no link to /register") — `SignIn.razor`
  did not render `RegisterUrl`, or the `a[href^='/register']` selector missed because the href was
  rewritten. The page, not the ceremony.
- **It reaches step 2 and stalls on `__passkeySubmit`** — the details POST did not advance the ticket.
  Check the registration cookie is being written: it is `Secure`, and everything depends on Chromium
  treating `http://localhost` as a secure context.
- **It clicks "Add a passkey" and stays on `/register` with a refusal** — the attestation was rejected.
  Most likely `/register/passkey/creation-options` returned 400, which means the cookie did not reach it
  (path or SameSite) or the ticket had expired. The harness quotes the page's `p.status-error`, which
  distinguishes "could not be added" (browser ceremony) from "could not be registered" (server
  verification).
- **Scenario 3 fails at `JoinStage.Confirm` right after registering** — the return URL did not survive.
  Either `SignIn.razor` dropped it from `RegisterUrl`, or `Register.razor`'s `SelfUrl` dropped it across
  the post/redirect/get, and the guest was sent to `/` instead of the table.
- **It fails at the bystander's `Expired`** — the token is somehow still valid, which means
  `WaitUntilTokenIsDeadAsync`'s arithmetic is off or the instance is not running at the rotation it was
  asked for. `instance.TableJoinTokenRotationSeconds` is read back rather than assumed, so print it.
- **It fails at the final database check** — the join happened but the rows are not what §5.1 promises.
  That is `SittingMembership`, not this slice.

## Housekeeping carried over

`docs/BUILD_PROGRESS.md` still jumps from "M4 Slice 1" to "M5 Slice 2". Thirteen appends are now
unmerged:

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
cat docs/_append/BUILD_PROGRESS-m6-slice-3.md >> docs/BUILD_PROGRESS.md
cat docs/_append/BUILD_PROGRESS-m6-slice-4.md >> docs/BUILD_PROGRESS.md
cat docs/_append/BUILD_PROGRESS-m6-slice-5.md >> docs/BUILD_PROGRESS.md
```

`shellcheck` is still not installed locally, so `ci_local.sh` step 1 only parses:

```bash
sudo dnf install ShellCheck
```

## What is next

Nine §16.3 scenarios. **4** is the natural next one and is now genuinely unblocked: a guest stages two
adds and a note, sends, and the kitchen gets exactly one alert. It needs a menu item (an administrator,
or a direct insert like this slice's table), a second live circuit on the kitchen board, and the
`TableOrderSurface` island — which means it is the first scenario to drive an *interactive* surface's
controls rather than watch one. Slice 4 made that possible; this one supplies the guest.

After that, 5 through 11 are variations on the same two-circuit shape and should come quickly. Then the
backup/restore drill, and M6 is done.

## The one-line why

The join grant's entire purpose is to outlive the token while a guest registers — and until this slice
there was no way for a guest to register, so the grant had nothing to outlive and the specification's
own promise had never once been executed.
