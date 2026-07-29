### M6 Slice 3 — the display's rotating code, watched (landed)

Two §16.3 scenarios, chosen together because they are the same scenario asked twice:

- **2** — Admin creates table → pairing code → device pairs at `/display/pair` → `/display/{table}`
  shows a rotating QR that **changes across a window boundary**.
- **15** — Admin rotates a table's join secret → the in-flight token dies; the **display's next window
  works**.

Both are about the rotating code as a *screen* rather than as a URL, both need an administrator driving
real administration forms, and both need a second browser that is a tablet rather than a person. Scenario
14 already proved the token arithmetic from the guest's side; these two prove that the thing on the table
is showing it.

Nothing in `src/` is touched. No migration, no schema change, no ADR, no specification edit — §4.1, §4.2,
§4.3 and §11.5 already say all of this. One `Directory.Packages.props` *reference* is added (the version
was already pinned), and nothing is deleted.

---

#### The problem with "the QR changed"

§16.3 scenario 2 says the code must change across a window boundary, and the obvious test is to
screenshot-compare, or diff the SVG, and assert inequality. That assertion is close to worthless. A
display frozen on a stale code satisfies "changed" the moment anything else on the page moves; a display
signed by the wrong table's secret satisfies it perfectly; a display that has drifted three windows behind
satisfies it every time it drifts one more. All three are exactly the failures §11.5 exists to prevent —
its own comment says it out loud: *"a frozen QR looks exactly like a live one"*.

So what needed proving is not that the artefact changed but that **the artefact on screen is the code the
server would accept right now**. The display renders nothing else: no token text, no URL, just an inline
SVG. There are precisely two ways to get from that SVG back to a token — decode it, or recompute it — and
decoding means a rasteriser and a computer-vision dependency to answer a question about HMAC arithmetic.

`Harness/JoinQrCodes.cs` recomputes it. The secret comes out of the row, the token from the domain's own
`JoinTokenService`, the URL from its own `BuildJoinUrl`, and the module geometry from the same
`Net.Codecrete.QrCodeGenerator` call the renderer makes. Then `Classify` answers a *sentence*: the current
window's code, the previous window's code, a code N windows out of date, or one this table's join secret
does not produce. A failure therefore reads

```
Assert.Contains() Failure: Item not found in collection
Collection: ["the current window's code", "the previous window's code"]
Not found:  "a code 3 windows out of date"
```

rather than two thousand characters of SVG path against two thousand characters of SVG path.

**The duplication, named.** Three private facts about `TableJoinTokens.RenderJoinQrSvg` are restated in the
harness: error-correction level Medium, a four-module quiet zone, `ToGraphicsPath` as the source of the `d`
attribute. They should stay private — nothing in the product needs them, and widening their visibility to
satisfy a test is the worse trade. If any of the three moves, both scenarios fail immediately and say so,
which is the behaviour a duplicated constant is supposed to have.

---

#### Reading the join secret, which is the one rule bent on purpose

§4.1 is emphatic that the join secret never leaves the server: no page renders it, `ITableDirectory`
refuses to select it, `ITableJoinSecretReader` exists as a deliberately narrow keyhole for the token
service alone, and rotation replaces it without showing anyone either value.
`RestaurantInstance.ReadJoinSecretAsync` reads it straight out of the row.

That is not a hole in the rule; it is the reason the rule is testable. The harness is not the application
— it owns the database it created — and the only reason it needs the secret is to check the application's
arithmetic from outside. Unlike `ITableJoinSecretReader` it is **not** gated on `is_active`, because a
scenario about a deactivated table still needs to know what it would have signed with.

---

#### Why the tablet needs its own browser, and why that is not tidiness

`DisplayDeviceAuthenticationMiddleware` resolves the §4.2 device credential only when nothing has
already authenticated the request — *"a signed-in person always wins"*, so that a member of staff who
opens the display URL on a paired tablet is themselves rather than the screen. Pair a display inside the
administrator's browser and the surface resolves to `DisplayStage.NotPaired` and bounces to
`/display/pair`, for a reason that looks nothing whatsoever like the cause.

So `RestaurantInstance.OpenIsolatedPageAsync()` hands out additional contexts — own cookie jar, same
origin — and both scenarios use one for the tablet. Scenario 15 opens a third for the guest, because the
join flow writes a grant cookie and a browser that had been refused must not be carrying one when it is
later accepted. Contexts are closed in reverse on disposal.

No virtual authenticator is attached to them. The journeys that need one run on the instance's own page;
a guest who registers a passkey will want one on a context of their own, which is scenario 3's business.

---

#### Scenario 2, step by step

1. `/setup` — the only way an administrator exists, and it signs them in on the same response.
2. `/administration/tables/new` — the label is typed, and the table identifier is read back out of the
   success panel's *Manage this table* link. Deliberately: the identifier is minted server-side, so
   recovering it this way tests the surface instead of reimplementing it.
3. `/administration/tables/{table}/displays` — Generate, then the plaintext code is read off the panel.
   That surface renders it in place rather than through a redirect, because that response is the only
   moment the plaintext exists; only its SHA-256 hash is stored.
4. The tablet asks for `/display/{table}` while still unpaired, and §11.5's first rule sends it to the
   pairing surface — not to a sign-in page a tablet could never satisfy.
5. It pairs, and the table it lands on is read out of the redirect rather than assumed, so *"the code
   paired this device to that table"* is an assertion rather than a premise.
6. The QR on screen is the current or previous window's code for this table's secret.
7. Within one rotation it is a **different** code — and that one is live too.

Steps 6 and 7 sample the clock *after* reading the browser, never before. The server rendered at or
before the read, so the window sampled afterwards is the newest one the screen could possibly be showing;
accepting the previous window as well is §4.3's own tolerance, and it is what makes a boundary landing
mid-assertion a non-event instead of a flake.

---

#### Scenario 15, and what "works" has to mean

Same setup, then: read the secret, read the screen, compute the token a guest holding a freshly scanned
code would have, rotate, read the new secret.

- **The in-flight token dies** — a guest presenting it gets §4.4's friendly page, at HTTP 200, with no
  detail about which thing failed. This half runs *first*, before anything is accepted, so no grant cookie
  exists that could carry the browser past a refusal and quietly turn a failure into a pass.
- **The display's next window works** — the wait predicate is not "a different code" but "a code the
  **new** secret signs, live at this instant". The weaker predicate would also be satisfied by a display
  that had merely drifted onto some other window of the old secret, which is the opposite of the claim.
  Nobody touches the tablet and nothing re-pairs it; §4.1's "paired displays pick up the new one
  automatically" is the whole assertion.
- ...and then a guest presenting the new window's token is accepted, because a code no guest can use is
  not a code that works.

Rotation is a post/redirect/get, so `RotateJoinSecretAsync` waits for the confirmation flash before
returning — and matches its *text*, since a rename and an activation change flash through the same
element. Without that wait, a scenario could read the old secret back and then spend its remaining minute
failing to explain why.

---

#### The rotation window is a parameter, not a constant

Twenty seconds for these two; the harness's default of an hour stays for scenario 14. The scenarios want
opposite things from the same knob — 14 needs "the previous window" not to roll over mid-assertion, 2 and
15 need a boundary to actually arrive inside a test's patience — and §4.3 accepts the current and previous
window whatever their width, so nothing an assertion depends on moves with it. Waits are two rotations
plus twenty seconds: one window because the refresh fires at the *next* boundary and the wait may have
begun just after the last one, a second because a loaded container can lose one, and the slack because a
timeout that fires while the thing was about to happen is the worst kind of flake.

---

#### The `--with-all` gate that never ran

The last full local run ended at step 6 with

```
scripts/ci_local.sh: line 153: ./run.sh: Permission denied
```

`run.sh` has no execute bit in the working tree, and under `set -euo pipefail` that ends the run rather
than reporting a fixable detail — so the boot-smoke gate has been silently unreachable through
`ci_local.sh` since `--with-all` was added. Every `run.sh` invocation in the script now goes through
`bash`, which works either way, and the header says why. Worth doing on your side as well, so the
README's own `./run.sh` is true:

```bash
chmod +x run.sh && git update-index --chmod=+x run.sh
```

---

#### What I verified rather than guessed

- **Playwright 1.61.0** (`microsoft/playwright-dotnet` at `v1.61.0`): `ILocator.GetAttributeAsync(string,
  LocatorGetAttributeOptions?) → Task<string?>`, `ILocator.CountAsync() → Task<int>`,
  `ILocator.InnerTextAsync(LocatorInnerTextOptions?)`, and `LocatorWaitForOptions` carrying
  `WaitForSelectorState? State` alongside `float? Timeout` — the QR path is waited for as **attached**
  rather than visible, because the offline curtain §11.5 raises over a stale code sits on top of that very
  element and a scenario diagnosing a frozen display must still be able to read what it froze on.
- **`Net.Codecrete.QrCodeGenerator` 3.0.0**: `QrCode.EncodeText(string, QrCode.Ecc)` and
  `ToGraphicsPath(int)` — the same two calls `TableJoinTokens` already makes, so they are verified by
  code that compiles today rather than by a document.
- **The surfaces themselves**, selector by selector, against the Razor in the tree: `#label` and
  *Create table*; `p.pairing-code` and *Generate pairing code*; *Rotate join secret* and the
  `secret-rotated` flash text; `#pairing-code`, `#device-label` and *Pair this display*;
  `#table-display-surface svg.join-qr-svg path`. Every one of them is a selector a Razor edit could
  break, which is why they live in three journey files rather than scattered through the scenarios.
- **`scripts/ci_local.sh`** under `bash -n` and `shellcheck --severity=warning` *and* `--severity=style`:
  clean at both, as delivered. The `--help` path prints its header by scanning contiguous `#` lines, so
  the new paragraph is `#`-prefixed throughout — a bare blank line there would have truncated the help.

---

#### Build/test checklist for this slice

1. `dotnet restore` — one new package *reference* (`Net.Codecrete.QrCodeGenerator`), already pinned
   centrally, and already arriving transitively; no version resolution is new.
2. `dotnet build` — three new files and three edited ones, all in the end-to-end test project. Nothing in
   `src/` changed, so a break is in the harness rather than the product.
3. `dotnet test` — **still 934 total, 919 passing, 15 skipped.** Two facts moved from a discovery-time
   skip to a runtime one, which the summary counts identically. If the total moved, something else moved
   with it.
4. `MYRESTAURANT_E2E=1 dotnet test tests/MyRestaurant.EndToEnd.Tests` — the real check. Expect
   **5 passed, 10 skipped**. Scenarios 2 and 15 each spend up to a minute waiting for rotation
   boundaries on purpose, so this run is meaningfully longer than the last.
5. `bash scripts/ci_local.sh --with-all` — and this time watch step 6 actually run.
6. Push, and watch the `end-to-end` job.

---

#### If something fails

1. **`Pairing the display did not reach a table display surface.`** The refusal text is quoted into the
   message. §4.2 gives one deliberately vague sentence for every failure — expired, used, unknown, typo —
   so check the app's captured output for which it was, and check that the code was not consumed twice.
2. **`a code this table's join secret does not produce`** on the *first* read. Either the public origin
   the harness computed is not the one the app embedded (they are now derived from one variable, so this
   should be impossible), or `RenderJoinQrSvg` changed one of its three geometry facts.
3. **`a code N windows out of date`.** The refresh loop is behind rather than wrong. Look for a paused
   container or a stopped circuit; §11.5's curtain will also be up on screen.
4. **The rotation wait times out.** The display never picked up the new secret. Check that
   `RotateJoinSecretAsync`'s confirmation really appeared, and that the tablet's circuit is still alive —
   `LoadAsync` revalidates the device on every pass, so a revoked or dead device stops the loop rather
   than showing a stale code.
5. **The unpaired-display assertion fails with a sign-in heading.** The pairing context inherited an
   Identity cookie, which means it is not actually isolated.

---

#### What is left in M6

Ten §16.3 scenarios, and the backup/restore drill as something executable rather than a runbook prose
section. Scenario 3 is the next natural one and the last with any plumbing left in it: the guest
registration journey, and a virtual authenticator on a context that is not the administrator's. From
there, 4 through 11 are two live circuits and a shopping list, and 12 walks the obligations pipeline
end to end.
