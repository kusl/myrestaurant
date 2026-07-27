### M4 close-out — restaurant time everywhere, and a clock that says so (landed)

Two things landed together because they are one decision. The first is a build fix: `TableOrderSurface.razor`
called `LinesFor(...)` in its render tree and the method was never written — `_partyLines` was loaded in
`LoadAsync` and then read by nothing, so the sitting-wide line query had no consumer and the web project did
not compile. The second is **F-36**, the open row in `DOCUMENTATION_REVIEW.md`: S§8.1 has always said instants
are "stored `timestamptz` UTC; rendered in `RESTAURANT_TIME_ZONE`", and no code did that. Eighteen call sites
across ten Razor files called `.ToLocalTime()`, which reads the **server process's** zone;
`RestaurantOptions.ResolveTimeZone()` existed with no caller; the runtime container sets no `TZ`. A deployed
instance rendered UTC and said nothing about it.

---

#### The ruling, and why it is stronger than the specification was

The owner's ruling settles a question S§8.1 had left implicit: **the reader's zone is irrelevant.** Not "prefer
the restaurant's zone" — *always* the restaurant's, for every viewer, wherever they are. A restaurant is a
physical place in one IANA zone. A guest in New York opening the history of a meal they ate in Tokyo wants the
times the meal actually happened at, not the times it would have been on their own wristwatch; and inside the
building, a kitchen ticket, the counter's bill, and the tablet on table four must agree to the minute or the
staff cannot talk to each other. Rendering in the viewer's zone would make all four screens disagree about the
same event, which is the one thing an append-only history is supposed to prevent (ADR-0002).

So S§8.1 is now normative about it rather than merely descriptive, R§8 states the rule and its reason, and one
type is the only thing allowed to perform the conversion.

#### `RestaurantTime` — one type, invariant formats, and no `ToLocalTime()` anywhere

`src/MyRestaurant.WebApplication/Time/RestaurantTime.cs` is a singleton over the configured
`TimeZoneInfo`, exposing `Time`, `TimeWithSeconds`, `Date`, `DateAndTime`, `DateAndTimeWithSeconds`,
`MachineReadable`, and `Snapshot`. Every call site moved onto it; the only surviving mention of `ToLocalTime`
in the tree is the doc comment forbidding it.

The formats are explicit and `InvariantCulture`, for exactly the reason `MoneyText` already documents for
`"C"`: `ToString("t")` takes the 12-versus-24 choice, the separator, and the month names from the *server's*
culture, which in this deployment is whatever locale the base image happens to carry. That is a decision made
by nobody, and it changes silently when the image is rebuilt. F-36's row named this entanglement and left it
open; it is now settled as configuration — `RESTAURANT_CLOCK_FORMAT`, `12-hour` (default) or `24-hour`,
validated at startup so a typo fails the process instead of quietly showing the wrong clock on every screen in
the building. It is deliberately **not** a `required` property, so every existing `RestaurantOptions`
construction (including the wiring tests') keeps compiling on the documented default.

`Snapshot` additionally computes **when the offset next changes** — a day-by-day walk to the bracketing day,
then a bisection to the second, memoized until the transition it found has passed. That is what lets a page
left open across the first Sunday in November stop rendering EDT without anybody reloading it.

#### §11.7 — a footer clock, because the convention is invisible until stated

"Sent 3:04 PM" tells a reader on another continent nothing at all unless the page says whose three o'clock that
is. A new `RestaurantClockFooter` therefore appears on **every** page in **both** layouts — including
`DisplayLayout`, whose whole existing rationale is that it carries no chrome, because a clock is the one piece
of chrome that means something on a screen a whole table looks at. `DisplayLayout`'s shell became a column with
the card centred in a `.display-main`, so the footer sits below it rather than beside it.

**There is no server-side timer, on purpose.** A Blazor timer would tick only on the interactive surfaces —
half the pages here are static SSR — and would cost one render plus one circuit message per second per open
tab, indefinitely, on phones. Instead the component renders one anchor and never renders again
(`ShouldRender() => false`, since the script owns that text node afterwards and a framework re-render would
overwrite a correct ticking reading with a stale one). `wwwroot/js/clock.js` advances it: a classic script
alongside `passkey.js` and `display.js`, so it works on the static-SSR account pages too, and a no-op on any
document without the footer.

`Intl` is forbidden in that script and the comment says why: it formats in the **reader's** locale and zone,
the exact thing the ruling rules out. The invariant abbreviated day and month names are hardcoded so the
ticking text and the server-painted text it takes over from are byte-identical — otherwise the handover at
page load is visible as a flicker.

**The handheld budget.** Most readers are on a phone, and the browser and OS will both try to save battery;
this must cooperate rather than fight:

1. **Nothing runs while hidden.** `visibilitychange` *clears* the timer rather than letting it fire and be
   ignored. A backgrounded tab costs zero.
2. **One wake per visible second.** `setTimeout` aimed at the coming second boundary — not `setInterval`
   (drift accumulates into double-fires) and never `requestAnimationFrame` (sixty wakes for one visible
   change).
3. **No DOM write unless the text changed**, guarded by one string comparison. `tabular-nums` stops the digits
   changing width as they tick, and `contain: content` on the footer keeps the repaint from inviting a layout
   pass over the page around it.

**Which clock is believed.** Elapsed time comes from `performance.now()` — monotonic, so an NTP step cannot
move it, which `Date.now()` alone cannot promise. But `performance.now()` stops advancing during device
suspend on several platforms, so a phone that spends an hour in a pocket would wake an hour behind. Both are
therefore read every tick and their divergence is treated as the signal it is: past two seconds, prefer the
wall clock (only it saw the suspend) and re-anchor from the server. A wall clock stepped *backwards* under us
prefers the monotonic reading and re-anchors anyway.

#### `GET /restaurant-clock`

The markup anchor is the whole story for a short-lived page. Two surfaces here are not short-lived: a
`/display/{table}` tablet holds one URL for days on a cheap oscillator, and a guest's circuit lasts a meal.
Rather than reload either, the script re-asks — every ten minutes while visible, on returning from a minute or
more hidden, and on detected divergence. Never while hidden, never more than once a minute, and a failed
request is *ignored* rather than allowed to blank the clock: a wall clock a second off beats a blank one. Half
the measured round trip is subtracted as the usual symmetric-latency estimate; the initial markup anchor uses
Navigation Timing's `responseStart` for the same reason, since anchoring at script-run time would leave the
clock permanently a fraction of a second slow.

The endpoint is anonymous (`credentials: 'omit'` from the script), `no-store`, and **added to
`ObligationsEnforcement.IsExemptPath`** alongside `/healthz`. That last one is not incidental: the §3.5
obligation pages render this footer too, and a redirect to HTML would leave the one page a locked-out user is
allowed to see with a dead clock.

#### `LinesFor` — the build fix, done as the read it should always have been

Rather than reinstate a `_partyLines.Where(...)` scan per bill entry, `LoadAsync` now groups the one
sitting-wide read into `_partyLinesByOrder` and `LinesFor` is a dictionary lookup. §11.1 renders a row per
person at the table, so the alternative — a query inside that loop — would turn a six-person table into six
extra round trips on every §9 notification. An order with nothing on it is absent from the grouping rather
than present and empty, so the empty list is the ordinary answer and not an error.

---

#### Files

**New**

- `src/MyRestaurant.WebApplication/Time/RestaurantTime.cs`
- `src/MyRestaurant.WebApplication/Time/RestaurantClockEndpoints.cs`
- `src/MyRestaurant.WebApplication/Components/Layout/RestaurantClockFooter.razor`
- `src/MyRestaurant.WebApplication/wwwroot/js/clock.js`
- `tests/MyRestaurant.WebApplication.Tests/Time/RestaurantTimeTests.cs`

**Changed**

- `Configuration/RestaurantOptions.cs` — `ClockFormat`, `UsesTwelveHourClock`, validation
- `Program.cs` — `RestaurantTime` singleton, `MapRestaurantClock()`
- `Identity/ObligationsEnforcement.cs` — exempt `/restaurant-clock`
- `Components/_Imports.razor`, `Components/App.razor`, both layouts, `wwwroot/app.css`
- Ten Razor pages, eighteen call sites: `Account/Pages/Passkeys`, `Administration/{AdministrationHome,
  AdministrationTables, ManagePerson, ManageTable, TableDisplays, TableJoinCode}`, `Setup`,
  `Table/{TableArea, TableOrderSurface}`
- `tests/.../RestaurantOptionsTests.cs`, `tests/.../Identity/ObligationsEnforcementTests.cs`
- `.env.example`, `compose.yaml`, `run.sh`
- `docs/REQUIREMENTS.md` (R§8), `docs/TECHNICAL_SPECIFICATION.md` (S§8.1, new S§11.7, S§13, S§19,
  Appendix A), `docs/DOCUMENTATION_REVIEW.md` (F-36 closed)

**Deleted** — none.

---

#### Verification

1. `dotnet build` — the `CS0103: LinesFor` error is gone; the web project compiles.
2. `dotnet test` — `RestaurantTimeTests` covers: the same instant rendering differently for a New York and a
   Tokyo restaurant; the date rolling forward when the restaurant zone has passed midnight and the reader's
   has not; a 45-minute offset (`Asia/Kathmandu`) not rounded to the hour; midnight as `12:00 AM` not
   `0:00 AM`; both clock formats; **format stability under `de-DE` and `ja-JP` ambient cultures**, which is
   the property F-36 was really about; the November 2026 transition found at the second, cross-checked
   against `TimeZoneInfo` on both sides of it; a no-DST zone reporting no transition; and the memo expiring
   after the transition it named.
3. `grep -rn "ToLocalTime" --include=*.razor --include=*.cs src/` → only the doc comment forbidding it.
4. Manual, on a quick tunnel with a phone:
   - Set `RESTAURANT_TIME_ZONE=Asia/Tokyo` and reload. Every timestamp on `/administration/tables`,
     `/account/passkeys`, and the table surface moves together; the footer reads **Tokyo**.
   - Set `RESTAURANT_CLOCK_FORMAT=24-hour`. The footer and every page timestamp switch together — no page
     is left on the other convention.
   - Set `RESTAURANT_CLOCK_FORMAT=military`. The process refuses to start and names the variable.
   - Watch the footer tick for a minute. It advances once a second and does not visibly reflow the page.
   - Background the tab for two minutes, then return: the reading is correct immediately, not two minutes
     behind and catching up.
   - Lock the phone for ten minutes, unlock: same.
   - `curl -s http://127.0.0.1:8080/restaurant-clock` returns the snapshot JSON with
     `cache-control: no-store`.
   - Reach `/account/change-password-required` with an outstanding obligation: the footer clock still ticks
     (the exemption), and the page is otherwise unreachable as before.
5. Manual, the display: open `/display/{table}` on a tablet. The card is still centred; the clock sits under
   it at display type size; the rotating QR and the staleness curtain behave exactly as before.
