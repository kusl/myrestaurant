### M6 Slice 5 — guests can register (landed)

M6 Slice 4 closed with "scenario **3** is next and the last with plumbing left in it". That was wrong in an
instructive way. Scenario 3 needed no plumbing at all; it needed a **page that did not exist**.

`/register` was never built. The route list had thirty-six entries and none of them was a registration
surface, `SignIn.razor` said so in its own footnote — *"Guests register at the table by scanning its
display — there is no stand-alone registration page"* — and `TableJoin.razor` sent an anonymous
grant-holder to `/sign-in`, which for a first-time guest is a door with nothing behind it. The gap was
recorded twice in this document (M2's known limitations, and the M3 join slice: *"Guest self-registration
on the join path is still to come"*) and mandated in three places in the specification:

- **R§4.3** — "Guests self-register at the moment of joining a table: username, optional display name, and
  at least one credential — passkey offered first, password accepted."
- **S§4.4** — "…and continue to sign-in/**registration** if anonymous… Registration mid-flow: the grant
  cookie survives the passkey ceremony; **that is its purpose**."
- **S§11.1** — "Anonymous with valid token → grant → sign-in/registration (passkey-first, password
  offered) → join."

So this slice is a product slice that happens to unblock a test. Nothing in the specification changes; three
sentences of it become true. It also unblocks §16.3 scenarios **4** through **11**, every one of which needs
a guest with an account, and until now the only way to obtain a non-administrator account was for an
administrator to create one from `/administration/people/new` — which is a *staff* account with
`must_change_password` set, and not the thing those scenarios are about.

`dotnet test` goes from 950 to **969**; `MYRESTAURANT_E2E=1` from 5 passed / 10 skipped to **6 passed / 9
skipped**. No migration, no schema change, no package change, no ADR edit, nothing deleted.

---

#### The surface: `/register`, two steps over a protected cookie

Anonymous and static SSR, like every account surface, because it writes cookies on the response — the
accumulating registration ticket, and the authentication cookie once the account exists.

1. **Details** — username, optional display name, **optional** password. The password is hashed
   immediately, so the ticket carries an Argon2id PHC string and never a plaintext.
2. **Sign-in method** — the real WebAuthn attestation ceremony. Registering commits the account; *"Not
   now — use my password"* commits it on the password alone, **and is rendered only when a password was
   set**.

That asymmetry is §3.3's rule made structural rather than advisory. A passkey is "always offered, never
required, never a gate for guests" — so declining is offered exactly when there is something to fall back
on, and `RegistrationTicket.CanDeclineThePasskey` is the single predicate both the markup and the POST
handler consult. An account with neither credential is refused twice: the button is absent, and
`DapperGuestRegistration` throws before any SQL runs.

**Why a ticket at all, for a two-step form.** A WebAuthn attestation needs a user handle *before* the
account exists — the browser is told who the credential is for, and the authenticator stores it. So the
person's UUIDv7 is minted at step one and must equal the `person` row's eventual id, or a later
discoverable-credential sign-in would present a handle matching nobody. This is exactly the problem `/setup`
solves with `SetupTicket` (§3.6), and it is solved the same way: a Data-Protection-protected, 30-minute,
Secure/HttpOnly/SameSite=Lax cookie, with `/register/passkey/creation-options` anonymous and gated on it.

Unlike `SetupTicket` there is **no step enum**. The wizard has three ordered steps that must each be
unskippable; registration has one, and the ticket's existence *is* the state. A single-valued enum would be
ceremony pretending to be a state machine.

**No TOTP step and no role.** §3.4 pairs the authenticator with the password path for staff and
administrators; a guest is never required to hold one, and may enroll voluntarily later from
`/account/enroll-totp`. And a guest is the *absence* of a role grant (§3.7) — not a role named `guest` — so
nothing is written to `person_role` and no `role_granted` event is recorded.

---

#### The commit: `IGuestRegistration`, and why it is not on `IAccountAdministration`

One transaction: the `person` row, the `passkey_credential` row when there is one, and the
`security_event` rows — `account_created`, plus `passkey_registered` when a passkey was registered — with
a **NULL actor**, matching the shape `/setup` writes for its own self-actions (§3.6).

Every method on `IAccountAdministration` takes a `grantedBy` / `changedBy` / `resetBy` identifier and
records that administrator as the actor, because §3.7 is about one person acting on another's account. A
guest registering has no actor: the subject is doing it to themselves. Sharing the interface would mean a
required parameter this path has nothing to put in.

**No advisory lock**, unlike `IFirstAdministratorBootstrap`. That one serializes on
`pg_advisory_xact_lock` because its precondition is global ("zero administrators exist") and cannot be
expressed as a constraint. Registration has no such invariant. The only race is two people claiming one
username at once, and `person.username`'s UNIQUE constraint over `citext` decides it correctly,
case-insensitively, and for free; the loser gets `UsernameTaken` and nothing is written.

The integration tests pin the passkey-only shape hardest, because it is the one with somewhere to go wrong:
`person.password_hash` must accept NULL (§3.2 makes it nullable precisely so a passkey can stand alone), and
the account must still be findable and usable through `DapperUserStore` afterwards — which is the seam where
a column written differently from the store's expectation would surface. For a passkey-only guest that store
is the only way in, so `RegisterAsync_ProducesAnAccountTheIdentityStoreCanSignIn` asserts the round trip
rather than trusting it.

---

#### §16.3 scenario 3, and the parenthetical nobody had tested

The scenario's full text is *"Guest scans (simulated URL from current token) → registers with passkey
**(slowly — grant outlives token)** → joins; sitting created."* The parenthetical is the interesting half,
and it is the entire reason §4.4's join grant exists. It had never been tested, because until this slice
there was no registration to be slow at.

The instance runs at `TABLE_JOIN_TOKEN_ROTATION_SECONDS = 10` — §13's floor, and the only scenario that
wants it. §4.3 accepts the current *and* previous window, so a scanned token is provably dead about twenty
seconds later; at the harness default of an hour there would be nothing to outlive and the assertion would
be vacuous. The scenario then:

- scans a live token in the guest's **own** browser context, with its **own** virtual authenticator (a
  WebAuthn credential belongs to the authenticator that minted it, so a passkey created on the shared
  administrator context would be one this guest could never use), and asserts §4.4 sent them to sign in;
- follows the **"Create an account"** link rather than navigating to `/register` — that link carrying the
  return URL is the whole mechanism by which registering lands the guest back at the table, and a scenario
  that typed the URL would be asserting on a path no guest can take;
- registers with a passkey and **no password**;
- asserts the URL it returns to carries **no token at all**, and shows the join confirmation;
- waits until the scanned token is past §4.3's acceptance window, measured from the instant it was minted
  rather than from now, and proves it is dead by re-scanning it in a **third** context — one with no grant
  cookie that could carry the navigation past a refusal and quietly turn a failure into a pass;
- joins anyway, and checks the database for exactly one open sitting on that table with exactly this guest
  on its roster.

That last check is not redundant with the page. §5.1's "sitting created" is a claim about rows, and a second
sitting that the `table_sitting_one_open_per_table` unique index should have prevented would look identical
from a seat.

---

#### Harness changes

- `RestaurantInstance.OpenIsolatedPageAsync(withVirtualAuthenticator: true)` — an isolated context that can
  hold credentials. Default remains off: a display device has no credentials beyond the §4.2 cookie, and
  CDP setup for every tablet would be waste.
- `RestaurantInstance.ReadOpenSittingAsync` — the open sitting and its roster, by username.
- `Harness/TableJourneys.cs` (new) — `ScanAsync`, `JoinAsync`, and `JoinStageOnScreen`, which names which of
  §4.4's four outcomes is on screen so a failure reads "it offered the join button" rather than quoting a
  heading.
- `AccountJourneys.RegisterGuestWithPasskeyAsync` and the `GuestAccount` record.

`ScanAsync` navigates to a **relative** path, and the reason is worth recording: the absolute URL a display
encodes comes from `RESTAURANT_PUBLIC_ORIGIN`, which the harness deliberately sets to
`https://localhost:{port}` while Kestrel serves plain HTTP there — the mismatch is what lets §13's https
requirement and Chromium's localhost-as-secure-context rule both hold. Navigating to that absolute URL
would reach nothing. Scenario 2 separately asserts that a real screen encodes exactly the code the table's
secret produces, so between the two nothing is assumed.

---

#### Known gap carried forward: `/register` is not rate-limited

`/display/pair` is the only rate-limited endpoint in the application (§4.2: five attempts per minute per IP),
and `/register` joins it as a second anonymous surface that writes a row — the more consequential of the two,
since a `person` row outlives the request.

It is not limited here, deliberately and not for free. The limiter is configured by
`AddRestaurantDisplays`, and `RateLimiterOptions.OnRejected` plus `RejectionStatusCode` are single-valued:
a second `AddRateLimiter` call registering a registration policy would silently take over the rejection
handler, and a refused registration would answer with the display's *"Too many pairing attempts from this
device"* — which is worse than no limit, because it is wrong and looks deliberate. Doing it properly means
`OnRejected` learning to dispatch on the endpoint, which is an edit to a file this slice otherwise does not
touch and a policy §13 does not specify.

What stands in the meantime: registration is a **two-request** flow behind an antiforgery token and a
Data-Protection ticket cookie, so it is not a scriptable single POST; the password is capped at 256
characters so an anonymous caller cannot ask for unbounded Argon2id work; and §3.2's hashing semaphore
bounds concurrent hashes process-wide regardless. This is a named follow-up, not an oversight.

---

#### What this slice does not do

- **It does not change `TableJoin.razor`.** An anonymous grant-holder still goes to `/sign-in`, which now
  offers registration alongside it. §4.4 says "sign-in/registration" and a returning guest — the common case
  after the first meal — wants sign-in first. Sending everyone to `/register` and offering sign-in from
  there would be the same page count with the wrong default.
- **It adds no header link.** Registration belongs to the join flow and to the sign-in page, per §4.3's
  "at the moment of joining a table". `MainLayout`'s session nav already wrapped onto three rows at 375 px
  the last time it held four items.
- **It reuses `.setup-steps` / `.setup-step` for the two-step indicator** rather than adding CSS. The rule
  is a generic numbered-step list with no setup-specific styling; the class name is now slightly wider than
  its origin, which is cheaper than a duplicate rule.
- **It offers no post-registration passkey nudge for the password path.** §3.3 wants a dismissible nudge
  "after registration and after sign-in". A guest who takes the password branch has just declined a passkey
  on the previous screen, so nudging them on the next one would be asking twice; the durable home is the
  `/account/passkeys` page, which already exists and is linked from the profile. The *sign-in* nudge remains
  outstanding, as recorded in M2's limitations.
