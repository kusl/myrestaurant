# M6 Slice 3 — §16.3 scenarios 2 and 15: the display's rotating code, watched

Every file below is a **full file** at its **repo-relative path**. Extract this archive at the repo root
and the contents drop straight over your working tree. `git status` will show exactly these 11 files as
modified/added, and **no deletions**.

```bash
tar -xzf m6-slice3-display-rotating-qr.tar.gz -C /home/kushal/src/dotnet/myrestaurant
```

## Files to DELETE

**None.** Nothing is renamed and nothing is superseded. No migration, no schema change, nothing in `src/`
touched at all, and no `Directory.Packages.props` edit — the one package this slice references was already
pinned there.

## The state I found

Green on every gate that ran: `total: 934, failed: 0, succeeded: 919, skipped: 15`, a clean
`run.sh --smoke`, a healthy `--containers-only`, `dotnet list package --outdated` empty, and the quick
tunnel up at `picks-garcia-survive-kruger.trycloudflare.com`. M6 Slice 2's checklist is fully discharged.

**One gate did not run**, and it has not been running:

```
6. boot smoke (./run.sh --smoke)
scripts/ci_local.sh: line 153: ./run.sh: Permission denied
```

`run.sh` carries no execute bit in the working tree — you invoke it as `bash run.sh` everywhere else — and
under `set -euo pipefail` that ends the script rather than reporting a fixable detail. So the boot-smoke
step of `ci_local.sh --with-all` has been silently unreachable since it was added. Fixed here, and folded
into this slice rather than held, per the small-fix policy.

## New files (4)

Three harness files under `tests/MyRestaurant.EndToEnd.Tests/Harness/`:

- `AdministrationJourneys.cs` — create a table, issue a display pairing code, rotate a join secret. All
  three through the real static-SSR administration surfaces, because "admin creates table" in §16.3 means
  the form, the antiforgery token, the endpoint authorization and the redirect, not an `INSERT`.
- `DisplayJourneys.cs` — redeem a pairing code as an unpaired screen, read the QR's `d` attribute, and
  poll it until a predicate holds. Refusals are quoted verbatim into the exception, because §4.2's
  deliberately vague one-sentence rejection is as unhelpful to whoever reads the failure as it is to a
  prober.
- `JoinQrCodes.cs` — the expected SVG path for a given `(secret, table, origin, window)`, and a
  classification of what is on screen: the current window's code, the previous window's code, a code N
  windows out of date, or one this table's secret does not produce.

Plus one docs append: `docs/_append/BUILD_PROGRESS-m6-slice-3.md`.

## Edited (7)

- `tests/MyRestaurant.EndToEnd.Tests/EndToEndScenarios.cs` — scenarios **2** and **15** implemented; the
  remaining ten stay as named placeholders, with scenario 3's skip reason now naming the one piece of
  plumbing it still wants.
- `tests/MyRestaurant.EndToEnd.Tests/Harness/RestaurantInstance.cs` — `OpenIsolatedPageAsync()` for
  additional browser contexts (closed in reverse on disposal), `PublicOrigin` and
  `TableJoinTokenRotationSeconds` as properties instead of facts buried in the process environment, and
  `ReadJoinSecretAsync()`.
- `tests/MyRestaurant.EndToEnd.Tests/Harness/RestaurantHarness.cs` — documentation only. Its
  `tableJoinTokenRotationSeconds` parameter said "scenario 2 *will* want a short one"; scenario 2 exists
  now.
- `tests/MyRestaurant.EndToEnd.Tests/MyRestaurant.EndToEnd.Tests.csproj` —
  `Net.Codecrete.QrCodeGenerator`, versionless as always.
- `scripts/ci_local.sh` — `bash run.sh` rather than `./run.sh`, everywhere, with the reason in the header.
  Clean under `bash -n`, `shellcheck --severity=warning` **and** `--severity=style` as delivered.
- `README.md` — five of fifteen scenarios, the multiple-contexts note, the per-instance rotation window,
  and the roadmap line.
- `_CHANGES.md` (this file)

No `docs/TECHNICAL_SPECIFICATION.md`, `docs/REQUIREMENTS.md`, `docs/DOCUMENTATION_REVIEW.md` or ADR edit:
this realizes behaviour §4.1, §4.2, §4.3 and §11.5 already specify, in the words they already use.

## The one decision worth arguing about

**"The QR changed" is close to a worthless assertion.** It is also the obvious one, and it is what §16.3
scenario 2 literally asks for — so it is worth saying why this slice does more than that.

A display frozen on a stale code satisfies "changed" the moment anything else on the page moves. A display
signed by the wrong table's secret satisfies it perfectly. A display three windows behind satisfies it
every time it falls one further behind. Those are precisely the failures §11.5 exists to prevent; its own
comment says it out loud — *"a frozen QR looks exactly like a live one"*. So the assertion made here is
that **the artefact on screen is the code the server would accept right now**, at both ends of the
boundary.

Getting there means recomputing the QR: the secret from the row, the token from the domain's own
`JoinTokenService`, the URL from its own `BuildJoinUrl`, the geometry from the same
`Net.Codecrete.QrCodeGenerator` call the renderer makes. The alternative — decoding the SVG on screen —
means a rasteriser and a computer-vision dependency to answer a question about HMAC arithmetic.

That restates three private facts about `TableJoinTokens.RenderJoinQrSvg`: Ecc.Medium, a four-module quiet
zone, `ToGraphicsPath`. They should stay private; nothing in the product needs them, and widening their
visibility for a test is the worse trade. If one of them moves, both scenarios fail immediately and say
so, which is what a duplicated constant is supposed to do.

And the comparison is reported as a **phrase**, not as two thousand characters of path against two
thousand characters of path, so a failure reads:

```
Collection: ["the current window's code", "the previous window's code"]
Not found:  "a code 3 windows out of date"
```

## Three smaller things, in case they look arbitrary

**The tablet needs its own browser context, and it is not tidiness.**
`DisplayDeviceAuthenticationMiddleware` ignores the §4.2 device credential on any request the Identity
cookie already authenticated — *"a signed-in person always wins"*, so staff opening a display URL on a
paired tablet are themselves. Pair inside the administrator's browser and the surface resolves to
`NotPaired` and bounces to `/display/pair`, for a reason that looks nothing like the cause. Scenario 15
opens a third context for the guest, because a browser that was refused must not be carrying a grant
cookie when it is later accepted.

**The clock is sampled after the browser is read, never before.** The server rendered at or before the
read, so the window sampled afterwards is the newest one the screen could be showing — and accepting the
previous window too is §4.3's own tolerance. That is what turns a boundary landing mid-assertion from a
flake into a non-event.

**Rotation stays a per-instance parameter.** Twenty seconds for these two, the existing hour for scenario
14. They want opposite things from the same knob, and §4.3 accepts the current and previous window
whatever their width, so nothing an assertion depends on moves with it. Waits are two rotations plus
twenty seconds.

## What I verified rather than guessed

- **Playwright 1.61.0** (`microsoft/playwright-dotnet` at `v1.61.0`): `ILocator.GetAttributeAsync`,
  `CountAsync`, `InnerTextAsync`, and `LocatorWaitForOptions` carrying `WaitForSelectorState? State`
  beside `float? Timeout`. The QR path is waited for as **attached** rather than visible, because §11.5's
  offline curtain sits on top of that element and a scenario diagnosing a frozen display must still be
  able to read what it froze on.
- **`Net.Codecrete.QrCodeGenerator` 3.0.0**: `QrCode.EncodeText(string, QrCode.Ecc)` and
  `ToGraphicsPath(int)` — the two calls `TableJoinTokens` already makes, so verified by code that compiles
  today rather than by a document.
- **Every selector, against the Razor in the tree**: `#label` / *Create table*; `p.pairing-code` /
  *Generate pairing code*; *Rotate join secret* and the `secret-rotated` flash text; `#pairing-code`,
  `#device-label` / *Pair this display*; `#table-display-surface svg.join-qr-svg path`;
  `p.status-success`; `p.status-error`. Each is a selector a Razor edit could break, which is why they
  live in three journey files rather than scattered through the scenarios.
- **`scripts/ci_local.sh`**: `bash -n` clean, `shellcheck --severity=warning` clean, `--severity=style`
  clean. The `--help` path prints its header by scanning contiguous `#` lines, so the new paragraph is
  `#`-prefixed throughout — a bare blank line there would have truncated the help.

One thing I could not verify without an SDK: whether xUnit's analyzers have anything to say about
`Assert.Contains(phrase, collection)`. It is the recommended form for collection membership (the rule that
exists, xUnit2017, fires on `Assert.True(collection.Contains(x))`, which this deliberately avoids), and
overload resolution is unambiguous because the second argument is an `IReadOnlyList<string>` rather than a
`string`. If CI disagrees under warnings-as-errors, the fix is local to one helper.

## Build/test checklist for this slice

1. `dotnet restore` — one new package *reference*, already pinned centrally and already arriving
   transitively. No version resolution is new.
2. `dotnet build` — three new files and four edited ones, all in the end-to-end test project.
3. `dotnet test` — **still 934 total, 919 passing, 15 skipped.** Two facts moved from a discovery-time
   skip to a runtime one, which the summary counts identically.
4. `MYRESTAURANT_E2E=1 dotnet test tests/MyRestaurant.EndToEnd.Tests` — the real check for this slice.
   Expect **5 passed, 10 skipped**. Scenarios 2 and 15 each wait for rotation boundaries on purpose, so
   this run is meaningfully longer than the last.
5. `bash scripts/ci_local.sh --with-all` — and this time watch step 6 actually run.
6. Push, and watch the `end-to-end` job.

## Also worth doing on your side

```bash
chmod +x run.sh && git update-index --chmod=+x run.sh
```

The script fix makes `ci_local.sh` work regardless, but the README tells people to type `./run.sh`, and
right now that is not true of a fresh clone.

## Housekeeping carried over

`docs/BUILD_PROGRESS.md` still jumps from "M4 Slice 1" to "M5 Slice 2". Eleven appends are now unmerged in
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
cat docs/_append/BUILD_PROGRESS-m6-slice-3.md >> docs/BUILD_PROGRESS.md
```

## What is next

Ten §16.3 scenarios, and the backup/restore drill as something executable. Scenario **3** is the next one
and the last with any plumbing left in it: the guest registration journey (not the same page as `/setup`)
and a virtual authenticator on a context that is not the administrator's. After that, 4 through 11 are two
live circuits and a shopping list, and 12 walks the obligations pipeline end to end.

## The one-line why

The single worst thing this product can do is show a table a QR code that stopped working ten minutes ago,
because a dead code and a live one look identical from every seat in the restaurant — and there is now a
machine that pairs a screen, waits for the boundary, rotates the secret out from under it, and says
whether the code on the glass is one the server would honour.
