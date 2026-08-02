### M6 Slice 8 — three scenarios that had never once passed (landed)

Slice 5 recorded `MYRESTAURANT_E2E=1` going to **6 passed / 9 skipped**. Slice 6 recorded **8 passed / 7
skipped**. Neither was ever observed. The first honest run of the suite reports **5 passed / 7 skipped / 3
failed**, and the three failures are §16.3 scenarios **3**, **4** and **6** — every scenario that registers
a guest, all stopping on the same line of the same harness method with the same message:

```
System.TimeoutException : Timeout 30000ms exceeded.
Call log:
  - waiting for Locator("button[name='__passkeySubmit']") to be visible
  at AccountJourneys.RegisterGuestWithPasskeyAsync … AccountJourneys.cs:line 149
```

Nothing in the application is wrong. `/register` works exactly as §4.3 specifies, and a real guest has
never been able to hit this. The harness was reading the address bar and believing it.

---

#### The URL changes before the page does

`RegisterGuestWithPasskeyAsync` is the only journey in the harness that navigates by **clicking a link in
the application** rather than by `page.GotoAsync`. That is deliberate and stays: §4.4's whole mechanism for
returning a guest to the table they scanned is the return URL riding on the sign-in page's *"Create an
account"* link, and a scenario that typed `/register` itself would be asserting on a path no guest can
take. But it is also what exposed the journey to a Blazor behaviour nothing else in the suite touches.

With `blazor.web.js` loaded and no interactive `Router` on the page — which is every static-SSR surface
here, so every account page, the join page and the display — an in-app link click is intercepted by
*enhanced navigation*. `NavigationEnhancement.ts`, `onDocumentClick`:

```ts
history.pushState(null, /* ignored title */ '', absoluteInternalHref);
...
performEnhancedPageLoad(absoluteInternalHref, /* interceptedLink */ true);
```

The URL is pushed **first**; the `fetch` and the `synchronizeDomContent` that patches the new markup in
happen after. And Playwright resolves `WaitForURLAsync` on a same-document navigation the moment the URL
matches — there is no `load` event coming, so there is nothing else it could wait for.

So the journey ran like this:

1. Click *Create an account*. `pushState` fires. `WaitForURLAsync(IsRegistrationUrl)` returns **while the
   sign-in document is still on screen**.
2. `FillAsync("#username", "e2e.guest")` succeeds instantly — because **`/sign-in` has a `#username` too**.
3. `FillAsync("#display-name", …)` has to wait; that field exists only on `/register`. While it waits, the
   fetch lands and `synchronizeDomContent` runs. `DomSync.ts`'s `ensureEditableValueSynchronized` assigns
   every input the value the server rendered, and the fresh registration markup carries `value=""`. **The
   username is erased.**
4. Continue posts with an empty username. `[Required]` fails, `OnValidSubmit` never fires, and the details
   step re-renders with *"Choose a username."*
5. There is no credential step, so there is no `__passkeySubmit`, so thirty seconds later the scenario
   times out on an element three states away from the problem.

It failed on every run rather than intermittently because the fill takes about two milliseconds and the
fetch about twenty; step 2 always won.

Form posts are unaffected, and that is worth stating rather than assuming: `enhancedNavigationIsEnabledForForm`
requires `data-enhance` on the form element itself and nothing in this application sets it, so the passkey
step, the join POST and every administration form are ordinary browser navigations where Playwright's waits
mean what they say. Only link clicks come through here — which is why `CompleteSetupAsync`, which does the
same four-step cookie dance over the same kind of surface, has worked since Slice 2.

---

#### `Harness/EnhancedNavigation.cs`

One method: click the link, then wait for **an element the destination has and the current page does
not**. `synchronizeDomContent` is a single synchronous call on the main thread and a Playwright query
cannot interleave with it, so the instant any part of the new markup is observable, all of it is —
including the reset of every field the two surfaces share. That makes the wait an exact barrier rather
than a hopeful delay.

It is a file of its own rather than four lines inlined into the registration journey because the hazard is
general and the next scenario to meet it is already named: §16.3 scenario **11** has an administrator
filtering the hidden-records view, which is a link click on a static-SSR page with a form on the other
side of it.

The registration journey uses `#display-name` as its barrier — a field, not the heading, because copy
changes and a barrier that breaks on a reworded sentence is a barrier somebody deletes. The URL is still
checked, but *after* arrival, where it is a fact rather than an intention.

---

#### The guard, and why a second one was worth two round trips

`AssertFieldHoldsAsync` reads both fields back with `InputValueAsync` immediately before the form goes,
and refuses to submit if either has lost what was typed into it.

That is not belt-and-braces around the fix above. It is the answer to the shape of this bug: a value reset
by a DOM patch produces a completely ordinary validation refusal on the next screen, and the scenario then
times out waiting for the screen after *that*. The distance between the cause and the symptom is what cost
the time here, and it is a distance any future self-patching surface can reintroduce. Two round trips buy a
message naming the field, what it holds and what it should have held.

The wait for `__passkeySubmit` also gained the diagnostic it never had. `DescribeRefusalAsync` became
`DescribeSurfaceAsync` and now reports the heading and **every** `p.status-error` and `.validation-message`
on the page rather than the first — a details step can refuse in more than one field at once, and
*"Choose a username."* on its own would have explained all three scenarios the day they were written.

---

#### What this does not change

- **No product file.** Not one `.cs` or `.razor` under `src/`. `/register` is correct: a guest clicks the
  link, watches the page arrive, and then types. Disabling enhanced navigation on the account surfaces to
  make a test pass would have been fixing the wrong thing.
- **No new scenario.** Scenario 5 was next on the list and is deliberately held back one slice. Two
  consecutive slices shipped scenarios reported as passing that had never run, and stacking a third on top
  of an unverified harness fix is how that happens a third time.
- `dotnet test` stays at **971 total / 0 failed** (956 succeeded, 15 skipped) — nothing outside the
  end-to-end project moved.

Expected after this: `MYRESTAURANT_E2E=1` reports **8 passed / 7 skipped**, which is what Slice 6 claimed
and this is the first slice entitled to.
