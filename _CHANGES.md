# M6 Slice 4 — the display refreshes

Every file below is a **full file** at its **repo-relative path**. Extract this archive at the repo root
and the contents drop straight over your working tree. `git status` will show exactly these 9 files as
modified/added, and **no deletions**.

```bash
tar -xzf m6-slice4-display-refreshes.tar.gz -C /home/kushal/src/dotnet/myrestaurant
```

## Files to DELETE

**None.** Nothing is renamed or superseded. No migration, no schema change, no
`Directory.Packages.props` edit, no new package.

## The state I found

`dotnet test` green: `total: 934, failed: 0, succeeded: 919, skipped: 15`. Clean `run.sh --smoke`, healthy
`--containers-only`, `dotnet list package --outdated` empty, quick tunnel up, and step 6 of
`ci_local.sh --with-all` running for the first time (the `bash run.sh` fix landed).

`MYRESTAURANT_E2E=1` — **3 passed, 2 failed, 10 skipped.** Both new scenarios from Slice 3:

```
Display_PairsAndShowsRotatingQrAcrossWindowBoundary
  The table display did not show a join code different from the one it started on within 60s.
Admin_RotatesJoinSecret_InFlightTokenDiesNextWindowWorks
  The table display did not show a join code signed by the rotated secret within 60s.
```

Everything before the wait passed in both — including `AssertShowingLiveJoinCode` on the *first* code. So
the display rendered a correct, live code exactly once and then never again.

## The diagnosis

**Two causes, and a third hazard found on the way.**

### 1. The harness never had a Blazor circuit (harness/product boundary)

`RestaurantInstance` boots the **build** output with `ASPNETCORE_ENVIRONMENT=Production`.
`_framework/blazor.web.js` is a framework **static web asset**: `dotnet publish` copies those into
`wwwroot/`, a plain `dotnet build` leaves them in the NuGet cache and describes them in a build-time
manifest — and that manifest is loaded by `WebHost.ConfigureWebDefaults` **only in Development**
(`dotnet/aspnetcore`, `release/10.0`):

```csharp
if (ctx.HostingEnvironment.IsDevelopment())
{
    StaticWebAssetsLoader.UseStaticWebAssets(ctx.HostingEnvironment, ctx.Configuration);
}
```

`Program.cs` serves assets with `UseStaticFiles()`, so build-output-as-Production has neither. The script
tag 404s, no circuit is established, and every interactive page silently degrades to prerendered HTML.

Silently is the difficulty. Prerendering renders the whole surface server-side — table label, party-size
chip, and a genuinely valid current join code. Nothing errors, nothing looks wrong, the page just never
changes again. The container is fine (it publishes) and `run.sh` is fine (it is Development). **No
end-to-end scenario had ever exercised an interactive surface** — the five that pass are all static SSR —
so there was nowhere for this to show up until one watched a screen for sixty seconds.

### 2. The refresh loop lost a race with itself (real product bug)

Independent of the above, and it would bite in production:

```csharp
protected override void OnAfterRender(bool firstRender)
{
    if (!firstRender || _stage is DisplayStage.NotPaired or DisplayStage.WrongTable) return;
    _subscription = Broadcaster.Subscribe(OnDomainNotification);
    _ = RunRefreshLoopAsync();
}
```

`ComponentBase.RunInitAndSetParametersAsync` calls `StateHasChanged()` the moment `OnInitializedAsync`
yields, and it yields on the first of **four** database round trips inside `LoadAsync`. That render goes
out with `_stage` still at its default `NotPaired`. The client's acknowledgement is one loopback WebSocket
message and routinely beats four queries — at which point `OnAfterRender(firstRender: true)` is rejected
by the guard, `firstRender` is never true again, and **the refresh loop never starts.** Intermittent by
construction, invisible when it happens, and the common case on a busy database.

### 3. A loop that could die once, permanently (latent)

`RunRefreshLoopAsync` caught only `OperationCanceledException` and `ObjectDisposedException`. Anything
else escaped a fire-and-forget task — unobserved, unlogged, terminal. One dropped connection and a screen
freezes for the rest of the day, looking exactly like a healthy one.

## New files (3)

- `src/MyRestaurant.WebApplication/Displays/DisplayRefreshSchedule.cs` — the boundary arithmetic and the
  staleness deadline, pure and clock-free, out of the `.razor` where nothing but Playwright could reach it.
- `tests/MyRestaurant.WebApplication.Tests/Displays/DisplayRefreshScheduleTests.cs` — sixteen cases.
- `docs/_append/BUILD_PROGRESS-m6-slice-4.md`

## Edited (6)

- `src/MyRestaurant.WebApplication/Program.cs` — load the build-time static web assets manifest outside
  Development. **No-op in the container**: `ResolveManifest` returns `null` when the file is absent and
  publish emits no runtime manifest, so this finds nothing there and `UseStaticFiles` serves the published
  copies exactly as before.
- `src/MyRestaurant.WebApplication/Components/Pages/Display/TableDisplay.razor` — start the live work via
  an idempotent `StartLiveWorkIfNeeded()` gated on `RendererInfo.IsInteractive` instead of `firstRender`,
  called from the end of `OnInitializedAsync` (which needs no render at all); absorb, log, and retry
  unexpected refresh failures; publish `data-live`; delegate the arithmetic to `DisplayRefreshSchedule`.
- `tests/MyRestaurant.EndToEnd.Tests/Harness/RestaurantInstance.cs` — `VerifyInteractivityAsync` probes
  `/_framework/blazor.web.js` at startup and refuses to hand back an instance that cannot be interactive,
  naming the cause.
- `tests/MyRestaurant.EndToEnd.Tests/Harness/DisplayJourneys.cs` — `WaitForLiveSurfaceAsync`, plus
  `DescribeSurfaceAsync` for the failure message.
- `tests/MyRestaurant.EndToEnd.Tests/EndToEndScenarios.cs` — scenarios 2 and 15 wait for interactivity
  before they start watching the QR; `InteractivityPatience` beside `RefreshPatience`.
- `_CHANGES.md` (this file)

No `docs/TECHNICAL_SPECIFICATION.md`, `docs/REQUIREMENTS.md`, `docs/DOCUMENTATION_REVIEW.md` or ADR edit:
every line here makes §4.3 and §11.5 true as written, and changes nothing they say.

## The decision worth arguing about

**The static-web-assets fix went in `Program.cs`, not in the harness.**

The purist move is to leave product code alone and change the harness — flip it to `Development`, or have
it boot a `dotnet publish` output. I did not, for one reason: *build output + `ASPNETCORE_ENVIRONMENT=Production`*
is a thing a real operator does, on the day they are reproducing a production configuration locally before
publishing. What they get today is an application where every interactive surface renders once and then
stops, with no error anywhere and no clue as to why. That is worth three lines to close for them, not just
for the test.

It is also the cheapest of the three options and the only one with no downside in the container, where it
provably does nothing.

If you would rather the harness published instead, the change is contained: `WebApplicationLocator` grows
a publish step and `WebApplicationLaunch.ContentRoot` stops needing to point at the source tree. It is a
slower loop and a bigger diff, and it buys only the container's exact asset layout — which nothing
currently asserts.

## Three smaller things, in case they look arbitrary

**The ceiling was off by the overshoot.** `DelayUntilNextWindow` clamped to `rotation` flat. A code minted
at the very start of its window reports a `NextRotationAt` one full rotation away and legitimately wants
`rotation + 250 ms`; clamping woke the loop 250 ms *before* the boundary, re-rendered the window already on
screen, and reached the new code only on the pass after. Every healthy display, every window. The ceiling
is now `rotation + overshoot` — it exists to stop a backwards clock jump parking the loop for hours, not to
second-guess an ordinary full-window wait. Found by writing the test, which is the argument for the file.

**`data-live` is not only for tests.** §11.5's staleness curtain covers a circuit that dies *later* —
`data-refresh-token` stops changing and `js/display.js` notices. It cannot cover a circuit that never
lived, because the prerendered surface is byte-identical to a healthy one. One attribute makes that
readable in dev tools and waitable in a scenario.

**The interactivity probe stays even though `Program.cs` now makes it pass.** It is one request per
instance, and it guards a failure that is invisible by construction: if a future change breaks
interactivity, this fails at instance startup with the reason instead of surfacing as an unrelated
sixty-second timeout in whichever scenario happened to need a circuit.

## What I verified rather than guessed

- **`WebHost.ConfigureWebDefaults`** (`src/DefaultBuilder/src/WebHost.cs`, `release/10.0`) — the
  `IsDevelopment()` gate on `StaticWebAssetsLoader.UseStaticWebAssets`, quoted above.
- **`StaticWebAssetsLoader`** (`src/Hosting/Hosting/src/StaticWebAssets/StaticWebAssetsLoader.cs`) —
  `ResolveManifest` reads `configuration[WebHostDefaults.StaticWebAssetsKey]` or
  `{ApplicationName}.staticwebassets.runtime.json` beside the assembly, and **returns `default` when the
  file does not exist**. That is what makes the call a no-op in a published deployment. Confirmed public
  shipped API in `PublicAPI.Shipped.txt`:
  `StaticWebAssetsLoader.UseStaticWebAssets(IWebHostEnvironment!, IConfiguration!) -> void`.
- **`WebApplication`** (`src/DefaultBuilder/src/WebApplication.cs`) — `Environment` is
  `IWebHostEnvironment` and `Configuration` is `IConfiguration`, so the call site needs no adapter. I used
  `StaticWebAssetsLoader` directly rather than `builder.WebHost.UseStaticWebAssets()` because the latter
  goes through `ConfigureWebHostBuilder.ConfigureAppConfiguration`, which runs eagerly and then validates
  host-configuration keys — legal here, but placing the call after `Build()` and before `UseStaticFiles()`
  is the placement whose ordering I can reason about without an SDK.
- **`ComponentBase`** (`src/Components/Components/src/ComponentBase.cs`) —
  `RunInitAndSetParametersAsync` calls `StateHasChanged()` before awaiting `OnInitializedAsync`, which is
  the whole of cause 2; and `RendererInfo => _renderHandle.RendererInfo` is `protected`, so it is reachable
  from `@code`.
- **`RendererInfo`** (`src/Components/Components/src/RenderTree/RendererInfo.cs`) — `Name` and
  `IsInteractive`. `RemoteRenderer` overrides it with `new("Server", isInteractive: true)`;
  `Web.HtmlRendering.StaticHtmlRenderer`, which `EndpointHtmlRenderer` derives from, with
  `new("Static", isInteractive: false)`. So `data-live` is exactly "a circuit rendered this".
- **Playwright 1.61.0** — `LocatorWaitForOptions.Timeout` is `float?`, hence the explicit cast;
  `Microsoft.Playwright.TimeoutException` derives from `PlaywrightException`, so the existing catch shape
  covers the wait timing out.
- **CS4007** — `await` cannot appear inside an interpolated string that binds to
  `DefaultInterpolatedStringHandler`, so `WaitForLiveSurfaceAsync` reads the page into a local *before*
  composing its message. The existing `PairAsync` avoids the same trap by concatenating instead.

## Build/test checklist for this slice

1. `dotnet build` — one new source file, one new test file, one edited `.razor`. The Razor edit is the one
   worth watching: `@inject ILogger<TableDisplay> Logger` (self-referencing generic, legal), `RendererInfo`
   used from `@code`, and two `data-live` attributes.
2. `dotnet test` — expect **950 total, 0 failed, 935 succeeded, 15 skipped** (was 934/0/919/15).
3. `MYRESTAURANT_E2E=1 dotnet test tests/MyRestaurant.EndToEnd.Tests` — the real check. Expect
   **5 passed, 10 skipped**. Scenarios 2 and 15 still wait on real boundaries, so it is still the slow one.
4. `bash scripts/ci_local.sh --with-all`.
5. Push, and watch the `end-to-end` job.

**If 2 and 15 still fail**, the failure now tells you where to look: `WaitForLiveSurfaceAsync` timing out
means the circuit never started (and `RestaurantInstance` should have refused before that);
`WaitForJoinQrPathAsync` timing out with `data-live='true'` on screen means the circuit is up and the
refresh loop is not, which is a `TableDisplay.razor` problem and nothing to do with static assets.

## Housekeeping carried over

`docs/BUILD_PROGRESS.md` still jumps from "M4 Slice 1" to "M5 Slice 2". Twelve appends are now unmerged:

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
```

`shellcheck` is still not installed locally, so `ci_local.sh` step 1 only parses:

```bash
sudo dnf install ShellCheck
```

## What is next

Ten §16.3 scenarios. Scenario **3** is next and the last with plumbing left in it: the guest registration
journey (not the same page as `/setup`) and a virtual authenticator on a context that is not the
administrator's. 4 through 11 are two live circuits and a shopping list — and they are now worth attempting,
because until this slice a scenario needing two live circuits would have had none.

## The one-line why

The display had two independent ways to freeze on a code that stopped working — one that made every
interactive page in the application inert outside Development, and one that turned on whether a WebSocket
round trip beat four database queries — and both were undetectable by design, because a dead QR code and a
live one are the same picture.
