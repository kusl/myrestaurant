### M6 Slice 4 — the display refreshes (landed)

M6 Slice 3 delivered §16.3 scenarios **2** and **15**, and both failed on their first real run:

```
Display_PairsAndShowsRotatingQrAcrossWindowBoundary
  The table display did not show a join code different from the one it started on within 60s.
Admin_RotatesJoinSecret_InFlightTokenDiesNextWindowWorks
  The table display did not show a join code signed by the rotated secret within 60s.
```

Every assertion before the wait passed in both, including the one that the *first* code on screen is one
the server would accept. So the display rendered a correct, live code exactly once and then never again —
which is precisely the failure §11.5 exists to prevent, arriving as a test failure rather than as a
restaurant full of dead QR codes.

Two distinct causes, one in the harness and one in the product, and a third latent hazard found on the way
to them. All three are fixed here. `dotnet test` goes from 934 to **950** (sixteen new cases, still zero
failures); `MYRESTAURANT_E2E=1` goes from 3 passed / 2 failed / 10 skipped to **5 passed / 10 skipped**.

No migration, no schema change, no package change, no ADR edit, nothing deleted.

---

#### Cause one: the harness never had a Blazor circuit

`RestaurantInstance` boots the **build** output — `src/MyRestaurant.WebApplication/bin/<Config>/net10.0` —
with `ASPNETCORE_ENVIRONMENT=Production`. That pairing has a consequence nobody had reason to expect.

The framework's own JavaScript, `_framework/blazor.web.js` among it, is a **static web asset**.
`dotnet publish` copies those into `wwwroot/`; a plain `dotnet build` leaves them in the NuGet cache and
describes them in a build-time manifest, `MyRestaurant.WebApplication.staticwebassets.runtime.json`. That
manifest is loaded by `WebHost.ConfigureWebDefaults` (`dotnet/aspnetcore`, `release/10.0`):

```csharp
builder.ConfigureAppConfiguration((ctx, cb) =>
{
    if (ctx.HostingEnvironment.IsDevelopment())
    {
        StaticWebAssetsLoader.UseStaticWebAssets(ctx.HostingEnvironment, ctx.Configuration);
    }
});
```

**Only in Development.** `Program.cs` serves assets with `UseStaticFiles()`, so a build output run as
Production has neither the published copies nor the manifest: `GET /_framework/blazor.web.js` returns 404,
no circuit is ever established, and every interactive page in the application silently degrades to the
prerendered HTML it was born with.

Silently is the whole difficulty. Prerendering renders the *entire* surface server-side — the table label,
the party-size chip, and a genuinely current, genuinely valid join code. Nothing errors. Nothing looks
wrong. The page simply never changes again. The container is unaffected (it publishes) and so is `run.sh`
(it is Development), which is why five earlier scenarios passed: **every one of them is a static-SSR page.**
No end-to-end scenario had ever exercised an interactive surface, so this had no way to be noticed until a
scenario watched one for sixty seconds.

**Fixed in `Program.cs`**, not in the harness:

```csharp
if (!app.Environment.IsDevelopment())
{
    StaticWebAssetsLoader.UseStaticWebAssets(app.Environment, app.Configuration);
}
```

Chosen over flipping the harness to Development because "build output + `ASPNETCORE_ENVIRONMENT=Production`"
is a configuration a real operator will reach for when reproducing a production setting locally, and losing
all interactivity with no diagnostic is a trap worth closing for them too. It costs a deployment nothing:
`StaticWebAssetsLoader.ResolveManifest` returns `null` when the file is absent, and publish emits no runtime
manifest — so in the container this call finds nothing and does nothing, while `UseStaticFiles` serves the
published copies exactly as before.

---

#### Cause two: the refresh loop lost a race with itself

Fixing the harness is not enough, because the surface had a live bug of its own:

```csharp
protected override void OnAfterRender(bool firstRender)
{
    if (!firstRender || _stage is DisplayStage.NotPaired or DisplayStage.WrongTable)
    {
        return;
    }

    _subscription = Broadcaster.Subscribe(OnDomainNotification);
    _ = RunRefreshLoopAsync();
}
```

`ComponentBase.RunInitAndSetParametersAsync` calls `StateHasChanged()` the moment `OnInitializedAsync`
yields — and it yields on the first of **four** database round trips inside `LoadAsync`. That first render
therefore goes out while `_stage` is still its default `NotPaired`. The client's acknowledgement of it is a
single loopback WebSocket message, and it routinely beats four queries. When it does,
`OnAfterRender(firstRender: true)` runs, the guard rejects the stage, and `firstRender` is never true
again: **the refresh loop is never started at all.**

Intermittent by construction — it turns on which of a round trip and four queries finishes first — and
invisible when it happens, because the frozen code is a valid code. On a busy database it would be the
common case.

The fix separates the two questions the old guard was conflating. "Am I interactive?" is
`RendererInfo.IsInteractive`, which is the honest form of what `firstRender` was standing in for. "Have I
already started?" is a latch, `_liveWorkStarted`, so the starter is idempotent and can be called from
anywhere. It is now called at the end of `OnInitializedAsync` — which needs no render at all — with
`OnAfterRender` kept only as a net for future edits.

---

#### The latent hazard: a loop that could die once, permanently

`RunRefreshLoopAsync` caught `OperationCanceledException` and `ObjectDisposedException`. Anything else — a
dropped connection, a moment of database unavailability, neither of them the display's fault — escaped a
fire-and-forget task: unobserved, unlogged, and terminal. One bad second and a screen the restaurant is
trusting freezes for the rest of the day, identically to a healthy one.

It now absorbs unexpected exceptions, logs them at warning, and waits for the next boundary. That is not a
swallow: `_refreshSequence` stops advancing while the trouble lasts, so `js/display.js` raises the §11.5
offline curtain if it outlasts `data-fresh-for-ms`. Cancellation and disposal still end the loop, because
those mean the circuit is genuinely gone.

---

#### `DisplayRefreshSchedule` — the arithmetic, out of the Razor and under test

The two expressions that decide whether a display ever refreshes now live in
`src/MyRestaurant.WebApplication/Displays/DisplayRefreshSchedule.cs`, pure and clock-free, covered by
sixteen cases that run in milliseconds. Both were previously private members of a `.razor` file, reachable
only by a Playwright scenario willing to watch a real boundary go past.

Moving them turned up a third, smaller mistake. The old ceiling was one rotation flat:

```csharp
return delay > rotation ? rotation : delay;   // was
```

A code minted at the very start of its window reports a `NextRotationAt` one full rotation away and
legitimately wants `rotation + 250 ms`. Clamping that to `rotation` woke the loop 250 ms *before* the
boundary, re-rendered the window already on screen, and only reached the new code on the pass after — a
visibly late QR on every healthy display, every window. The ceiling is now `rotation + overshoot`; it
exists to stop a clock that jumped backwards from parking the loop for hours, not to second-guess an
ordinary full-window wait.

The tests pin the property rather than the arithmetic: *the wake-up lands in the window after the one on
screen*, expressed through the domain's own `JoinTokenService.CurrentWindowIndex`, and *`data-fresh-for-ms`
always outlasts the longest possible delay* — the invariant that keeps a working display from raising the
offline curtain once per window.

---

#### `data-live`, so this can never be a mystery again

The surface now publishes `data-live`, set from `RendererInfo.IsInteractive`: `"true"` only when a circuit
produced the markup, `"false"` from prerendering. `js/display.js` does not need it — the staleness curtain
already covers a circuit that dies *later*, because `data-refresh-token` stops changing. This covers the
case where it never lived, which is otherwise indistinguishable from health in every pixel on the glass.

Two things now consume it, and between them they replace a sixty-second timeout two steps from its cause
with a sentence at the moment of failure:

- `RestaurantInstance` probes `/_framework/blazor.web.js` during startup and refuses to hand back an
  instance that answered anything but 200, naming the static-web-assets cause in the message. One request
  per instance, for a failure class that is invisible by nature.
- `DisplayJourneys.WaitForLiveSurfaceAsync` waits for `[data-live='true']` before either scenario starts
  watching the QR, and reports the surface's actual attribute values when it does not arrive.

---

#### What this slice does not claim

The end-to-end suite still boots a build output rather than a published one, so it still does not prove the
container's exact asset layout. What it now proves is that the application is interactive in an environment
other than Development, which is the property the two failing scenarios were unknowingly asking for. Making
the harness publish is a bigger change and a slower loop; it is worth doing the day a scenario needs
something only publishing produces.
