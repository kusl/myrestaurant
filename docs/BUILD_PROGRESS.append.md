
### M3 Slice 4 — display devices: pairing, device authentication, and the `/display` surfaces (landed)

The table screen is real (§4.2, §11.5, §3.7). An administrator generates a one-time code for a table; a
tablet redeems it at the anonymous, rate-limited `/display/pair` and receives a long-lived credential;
`/display/{table}` then shows that table's rotating join QR full-screen, refreshing on the window
boundary, with a party-size chip and an offline state that makes a frozen code impossible to mistake for
a live one. This closes M3's "display pairing + device auth + `/display`" line.

**DataAccess — three services in a new `Displays/` folder.** `IDisplayDeviceDirectory`/
`DapperDisplayDeviceDirectory` lists a table's devices for the administration page, resolving both person
references (pairer, revoker) to usernames and never selecting `device_secret_hash`; live devices sort
before revoked ones. `IDisplayDevicePairing`/`DapperDisplayDevicePairing` is the write path — issue a code
(CSPRNG 8 characters from the §4.2 unambiguous alphabet, stored as SHA-256 with `expires_at`, plaintext
returned exactly once), redeem it, revoke a device. Redeem takes `FOR UPDATE` on the code row so two
tablets racing the same code cannot both pair, re-checks the table is active *under that lock* (§4.1 — a
table deactivated between issue and redeem takes no new screens), mints a 32-byte Base64Url secret,
stores only `sha256(secret)`, and burns the code. `IDisplayDeviceAuthenticator`/
`DapperDisplayDeviceAuthenticator` is the read side of the credential: `AuthenticateAsync` selects the
non-revoked row by identifier and compares the hash with `FixedTimeEquals`; `RevalidateAsync` does the
same by identifier alone, which is what §4.2's "or circuit revalidation" needs once the cookie is out of
reach. Both then run one conditional `UPDATE` that moves `last_seen_at` only when the last sighting is
older than a minute — the whole "at most once per minute" rule in a single race-free statement. All three
follow the established shape: one connection (and, for writes, one transaction) per operation, one
`IClock.UtcNow` instant, UUIDv7 keys from `IIdentifierFactory`, and `DateTime` row types projected to
`DateTimeOffset` records (the Npgsql/Dapper constructor-binding fix `TableDirectory`, `PersonDirectory`,
and `SittingDirectory` all carry). Display devices are not in the person-scoped `security_event`
vocabulary (§8.2), so nothing is audited here.

**Domain.** `PairingCode` gains `Normalize` — upper case, with spaces, hyphens, underscores, dots, and the
Unicode dashes a keyboard autocorrects into removed, bounded at 128 characters so the `stackalloc` is
fixed. It reshapes only; the result still has to pass `IsWellFormed`. The code is read off one screen and
typed into another by a person standing in a restaurant, so `abcd-efgh` has to be `ABCDEFGH`.

**The device principal, and why it is middleware.** `DisplayDeviceCookie` implements §4.2's value verbatim
(`device:{device_identifier}:{secret}`, Secure/HttpOnly/SameSite=Lax, 365 days) with a `TryParse` that
refuses anything else. It is deliberately **not** Data-Protection-encrypted, unlike the join grant: the
grant is a self-describing capability the server must trust on sight, whereas this is a bearer secret
checked against a row every request — sealing it would add nothing and would couple a year-long
credential to the key ring, so a rotation would silently unpair every screen in the building.
`DisplayDevicePrincipal` builds the claims (`NameIdentifier` = device, `Name` = label, the
`myrestaurant:principal_kind` = `table_display` marker, the table id and label) and reads them back,
refusing to answer for anything that is not a display device. It emits **no role claim** — §0/§3.7 say
`table_display` is never a `person_role`, so the four `RequireRole` area policies fail for a screen by
construction — and **no obligation claim**, so the §3.5 pipeline decides "none" and waves it through.

`DisplayDeviceAuthenticationMiddleware` installs that principal. The obvious .NET shape is a second
authentication scheme, and it does not work here: the display surface is interactive, and a circuit takes
its principal from the `/_blazor` request, which authenticates with the **default** scheme — a device
scheme would populate the initial GET and hand the circuit an anonymous principal. Plain middleware runs
on both. It is scoped to `/display*` and `/_blazor`, skips when a person is already signed in (staff on a
paired tablet are themselves), only touches the database when a cookie was actually sent, and clears a
credential it cannot honour so the pairing page can greet a revoked screen with §11.5's "this display was
disconnected".

**Rate limiting.** §4.2's "5 attempts/minute/IP" is a named fixed-window policy registered by
`AddRestaurantDisplays()` and opted into with `@attribute [EnableRateLimiting(...)]` on the pairing page —
component attributes become endpoint metadata, which is exactly what `RateLimitingMiddleware` reads. There
is no global limiter, so nothing else in the app acquires a budget. `RejectionStatusCode` is 429 rather
than the framework's 503 default, and `OnRejected` writes one plain sentence for whoever is holding the
tablet. `app.UseRateLimiter()` **must** sit after `UseForwardedHeaders()`: the partition key is the
connection's remote address, and before the forwarded headers are applied that is the proxy, so the whole
restaurant would share one bucket. Nothing new is installed — `Microsoft.AspNetCore.RateLimiting` and
`System.Threading.RateLimiting` are both in the shared framework.

**Surfaces.** `/display/pair` (anonymous, static SSR — it writes the cookie) resolves three entry states
on GET: already paired (offer the display, do not re-pair), disconnected, or fresh. Every failure —
unknown, reused, expired, malformed, table deactivated — produces one sentence, because anything finer
turns an anonymous endpoint into an oracle for which codes exist. `/display/{TableId:guid}` is
**interactive server** (§11.5 wants a live chip, a window-aligned refresh, and a connection state) and
**anonymous at the endpoint, gated in the component**: §11.5 says an unpaired device is *redirected* to
`/display/pair`, and `[Authorize]` cannot produce that — a failed policy challenges the default scheme and
lands a tablet on `/sign-in`. §3.7's "table claim matches `{table}`" is a route-value comparison and
belongs in the surface for the same reason `/table`'s per-sitting membership check already does. Both use
a new bare `DisplayLayout` rather than `MainLayout`, whose session header and `<AuthorizeView>` would
present a device as though it were a person. `/administration/tables/{id}/displays` issues codes (rendered
in place, never post/redirect/get — the plaintext exists once, exactly like the TOTP recovery codes),
lists devices with last-seen, and revokes via a select-driven form (the shape `ManagePerson` already uses
for roles). `ManageTable` gained the link.

**The refresh loop and the offline state.** The component schedules its next refresh from the QR it just
rendered — `NextRotationAt` plus 250 ms, floored and ceilinged against a clock jump — which keeps it
aligned to §4.3's `(window_index+1) × rotation` without keeping its own notion of the window. Each pass
re-validates the device (catching revocation, and doubling as the `last_seen_at` heartbeat), re-renders
the code, and re-reads the party size. `SittingMemberJoined`/`SittingClosed` re-read it too; the sitting
id is deliberately not compared, because the notification that matters most — the first person joining —
announces a sitting the surface has never seen. Staleness is detected client-side by `wwwroot/js/display.js`
against two attributes the surface publishes (`data-refresh-token`, `data-fresh-for-ms`), with the deadline
measured from when the **script** observed the token change rather than from a server timestamp: a kiosk's
clock is frequently wrong by minutes, and a skewed clock must not be able to declare a healthy display
dead or a dead one live. The same script holds the screen wake lock and re-acquires it on
`visibilitychange` (§11.5, §10.3). It is inert on every page without the surface, so loading it from
`App.razor` alongside `passkey.js` costs one element lookup a second.

No migration (`table_display_device` and `table_display_pairing_code` ship in `0001_initial_schema.sql`),
no packages, no spec edit — this realizes behaviour §4.2, §3.7, and §11.5 already specify. Tests:
`DisplayDevicePairingTests` (Testcontainers, 14 facts — hash-only storage, the whole redeem happy path,
single-use, expiry, malformed codes as a theory, human-typed codes, the derived label, the
deactivated-after-issue refusal, revoke-once-and-stay-revoked, and the directory's view of all of it),
`DisplayDeviceAuthenticatorTests` (Testcontainers, 8 facts — correct secret, wrong secret, another
device's secret, unknown device, revoked device, deactivated table still authenticates, the once-a-minute
heartbeat, and revalidation), `DisplayDeviceCookieTests` (12 facts, no container), `DisplayDevicePrincipalTests`
(7 facts — including the two that matter most: a signed-in person is not a device even though it also has
a `NameIdentifier`, and a device holds no role), and `DisplaysWiringTests` (6 facts).

**Known consequence — a display is a device everywhere it is allowed to be, and nowhere else.** Because
the middleware is path-scoped, a kiosk browser walked to `/` or `/table` is an ordinary anonymous visitor
there. That is intended: no other surface has to defend against a principal whose `NameIdentifier` is a
device rather than a person, and `PersonPrincipal` — which reads that same claim — is never handed one.

Deferred: nothing in §4.2 or §11.5 remains. Next is M4 — the living order, the §6.6 locking protocol,
staging and batch send, fulfillment, and the kitchen surface.

### Build/test checklist for this slice

1. `dotnet restore` — **no new packages** this slice.
2. `dotnet build` — the four new Razor components are the most likely home of anything a compiler catches;
   `Components/Pages/Display/TableDisplay.razor` most of all, being the first interactive component here
   with a background loop, a broadcaster subscription, and `IDisposable`.
3. `dotnet test` — expect the previous green set plus the two Testcontainers display suites (which skip
   without a container engine) and the three pure ones (which never do).
4. `./run.sh --smoke` — boots once, verifies `/healthz/ready`, exits. No migration runs; 0001 already has
   both display tables.
5. Manual, end to end, on a quick tunnel (`bash scripts/quick_tunnel.sh`) with a second device or a second
   browser profile standing in for the tablet:
   - Administration → Tables → a table → **Manage displays** → **Generate pairing code**. The code shows
     once, with its expiry.
   - On the "tablet", open `/display/pair`, type the code (try it lower-case and hyphenated — it should
     still work), name the display, submit. Expect a redirect to the full-screen table display with a QR.
   - Watch it past a rotation boundary: the QR must visibly change and the code must keep working.
   - Scan it with a phone and join. The party-size chip should go from **Table free** to **1 person**
     without touching the display.
   - Kill the app (`podman-compose --profile dev stop web`) and watch the display: within about
     `TABLE_JOIN_TOKEN_ROTATION_SECONDS + 10`, the red **Reconnecting…** curtain must cover the QR.
6. Manual, refusals:
   - Enter the same code twice → the second attempt is refused with the same wording as a typo.
   - Enter six wrong codes inside a minute → the sixth returns **429** with the plain-text message.
   - Administration → **Manage displays** → **Revoke display**, then wait one rotation on the tablet → it
     should navigate itself to `/display/pair` and say **This display was disconnected.**
   - Deactivate the table → the display shows "out of service" rather than a code, and stays paired.
   - Open `/display/{some-other-table}` on the tablet → **Wrong table**, with a link back to its own.
