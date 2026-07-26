# M3 Slice 4 — display devices: pairing, device authentication, and the `/display` surfaces

Every file below is a **full file** at its **repo-relative path**. Extract this archive at the repo root
and the contents drop straight over your working tree. `git status` will show exactly these 20 files as
modified/added (21 counting this one).

```bash
tar -xzf m3-slice4-display-devices.tar.gz -C /home/kushal/src/dotnet/myrestaurant
```

## Files to DELETE

**None.** Every change is an in-place edit or a new file. Nothing is removed or renamed.

The one file that does not belong in the tree afterwards is `docs/BUILD_PROGRESS.append.md`, which exists
only to be appended and then removed — see the last section.

## New files (13)

### Code — DataAccess (3)

- `src/MyRestaurant.DataAccess/Displays/DisplayDeviceDirectory.cs`
  `TableDisplayDeviceSummary`, `IDisplayDeviceDirectory`/`DapperDisplayDeviceDirectory` (§4.2, §11.4).
- `src/MyRestaurant.DataAccess/Displays/DisplayDevicePairing.cs`
  Issue / redeem / revoke, with all their outcome enums and results (§4.2).
- `src/MyRestaurant.DataAccess/Displays/DisplayDeviceAuthenticator.cs`
  `DisplayDeviceSession`, `IDisplayDeviceAuthenticator`/`DapperDisplayDeviceAuthenticator` (§4.2).

### Code — WebApplication (5)

- `src/MyRestaurant.WebApplication/Displays/DisplayRoutes.cs`
  Paths, the rate-limit policy name, and the §4.2 budget in one place.
- `src/MyRestaurant.WebApplication/Displays/DisplayDeviceCookie.cs`
  §4.2's `device:{id}:{secret}` value, parsed and written.
- `src/MyRestaurant.WebApplication/Displays/DisplayDevicePrincipal.cs`
  The device principal's claims — no role, no obligations (§0, §3.7).
- `src/MyRestaurant.WebApplication/Displays/DisplayDeviceAuthenticationMiddleware.cs`
  Installs that principal on `/display*` and `/_blazor`.
- `src/MyRestaurant.WebApplication/Displays/DisplaysServiceCollectionExtensions.cs`
  `AddRestaurantDisplays()` — the three services plus the pairing rate limiter.

### Razor + assets (4)

- `src/MyRestaurant.WebApplication/Components/Layout/DisplayLayout.razor`
- `src/MyRestaurant.WebApplication/Components/Pages/Display/DisplayPair.razor` — `/display/pair`
- `src/MyRestaurant.WebApplication/Components/Pages/Display/TableDisplay.razor` — `/display/{TableId:guid}`
- `src/MyRestaurant.WebApplication/Components/Pages/Administration/TableDisplays.razor`
  `/administration/tables/{TableId:guid}/displays`
- `src/MyRestaurant.WebApplication/wwwroot/js/display.js` — wake lock + stale-code detection

### Tests (5)

- `tests/MyRestaurant.DataAccess.Tests/Displays/DisplayDevicePairingTests.cs`      (Testcontainers, 14 facts)
- `tests/MyRestaurant.DataAccess.Tests/Displays/DisplayDeviceAuthenticatorTests.cs` (Testcontainers, 8 facts)
- `tests/MyRestaurant.WebApplication.Tests/Displays/DisplayDeviceCookieTests.cs`    (12 facts, no container)
- `tests/MyRestaurant.WebApplication.Tests/Displays/DisplayDevicePrincipalTests.cs` (7 facts, no container)
- `tests/MyRestaurant.WebApplication.Tests/Displays/DisplaysWiringTests.cs`         (6 facts, no container)

## Edited — code (5)

- `src/MyRestaurant.Domain/Security/PairingCode.cs`
  **Additive.** Adds `Normalize` (case + separators, bounded). Nothing existing changed.
- `src/MyRestaurant.WebApplication/Program.cs`
  Adds `AddRestaurantDisplays()`, `app.UseRateLimiter()` (after `UseForwardedHeaders`, before
  `UseStaticFiles`), and `app.UseMiddleware<DisplayDeviceAuthenticationMiddleware>()` (after
  `UseAuthentication`, before `UseAuthorization`).
- `src/MyRestaurant.WebApplication/Components/App.razor`
  One `<script src="js/display.js" defer>` beside `passkey.js`.
- `src/MyRestaurant.WebApplication/Components/Pages/Administration/ManageTable.razor`
  A **Display devices** section linking to the new admin page.
- *(no `_Imports.razor` change — the new pages carry their own `@using` lines.)*

## Docs (1, append-then-delete)

`docs/BUILD_PROGRESS.md` is ~71 KB, so it is not regenerated here. The new section ships separately:

```bash
cat docs/BUILD_PROGRESS.append.md >> docs/BUILD_PROGRESS.md && rm docs/BUILD_PROGRESS.append.md
```

No `docs/TECHNICAL_SPECIFICATION.md`, `docs/REQUIREMENTS.md`, or ADR edit: this slice realizes behaviour
§4.2, §3.7, and §11.5 already specify, and `docs/OPERATIONS.md` §5 already describes the flow that now
exists. No migration (`table_display_device` and `table_display_pairing_code` both ship in
`0001_initial_schema.sql`). No new packages — `Microsoft.AspNetCore.RateLimiting` and
`System.Threading.RateLimiting` are in the shared framework.

## Two decisions worth knowing before you read the diff

**The device principal is middleware, not an authentication scheme.** A custom `AuthenticationHandler`
is the obvious shape and it silently breaks the display: `/display/{table}` is interactive, and a Blazor
circuit takes its principal from the `/_blazor` request, which authenticates with the *default* scheme.
A device scheme would populate the initial GET and hand the circuit an anonymous principal. Middleware
runs on both, so the device reaches the circuit.

**`/display/{table}` is anonymous at the endpoint and gated in the component.** §11.5 requires an
unpaired device to be *redirected* to `/display/pair`; `[Authorize]` cannot do that, because a failed
policy challenges the default scheme and lands a tablet on `/sign-in`. §3.7's "table claim matches
`{table}`" is a route-value comparison anyway, which is the same reason §3.7 already leaves `/table`'s
per-sitting membership check outside the policy.

## The one-line why

A table needs a screen that shows a code no one has to be handed: an administrator issues a hashed,
single-use, ten-minute code; the tablet trades it once for a revocable year-long credential; and from then
on the screen re-renders the rotating QR on every window boundary, announces its party size from the live
broadcast, and — the part that actually matters — raises a red curtain over the code the moment it can no
longer prove the code is fresh, because a frozen QR looks exactly like a working one.
