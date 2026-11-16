Gemini 4 Hydrogen 

thinking: 

**Analyzing the Prompt's Context**

I'm now carefully examining the prompt alongside the extensive repository context provided. The user seems to have submitted a complete dump of `myrestaurant`, encompassing the entire codebase, including commit `ebdac47`, which points to a `.NET 11` application with PostgreSQL, Blazor Server, Dapper, Podman Compose, and Playwright E2E. My focus is on understanding the nuances of the request in light of this detailed information.


**Initiating Code Review**

I've initiated a deep dive into the codebase, focusing on Slice 76 changes and the related E2E tests, Blazor components, and .NET 11 specifics. I'm carefully examining the `DisplayJourneys.cs` file, particularly the `PairAsync` method modifications, to understand the implications of the timeout and wait conditions introduced in this slice and how those changes have affected the E2E tests. My review now will encompass architecture, potential bugs, security, documentation, tests, and contracts.


**Clarifying SSR Form Behavior**

I'm now clarifying the static server-side rendering (SSR) form behavior within the Blazor context. Specifically, I am digging into how `EditForm` interacts with `blazor.web.js` when `Enhance` is used, and more importantly, when it is *not* used in the codebase. I am clarifying how the default HTML form behavior changes and when it intercepts and enables the enhanced form processing. The analysis also covers how redirects are handled depending on these settings.


**Investigating Potential Timeout Causes**

I'm now investigating a possible root cause for the timeout observed in Slice 76. The issue could stem from `WaitForURLAsync` being called after `ClickAsync`, where the page's load event might have already fired. Alternatively, enhanced navigation or a persistent WebSocket connection might be holding things open. Also, the `WaitForAsync` on `SurfaceSelector` checks `Attached` state with a 30-second timeout. If `PairAsync` lands on a page where `_stage` is `TableOutOfService` or `NotPaired`, the selector won't attach, leading to an exception which is now caught. My analysis is ongoing.


**Identifying Timeout Handling Issues**

I'm now identifying instances of incorrect exception handling related to `WaitForURLAsync` within the `tests/MyRestaurant.EndToEnd.Tests/Harness/` directory. Specifically, I'm finding that `catch (PlaywrightException)` blocks are wrapping `page.WaitForURLAsync(...)` calls, but Playwright .NET throws `System.TimeoutException` on timeout, meaning these specific catches are not doing their job. I'm investigating the implications of this oversight across several journey files. I am now checking all the instances.


**Uncovering Exception Handling**

I've just uncovered a significant issue. I've found a critical error in how we are handling exceptions, specifically related to `WaitForURLAsync` calls wrapped in `catch (PlaywrightException)` blocks. The `IsStillOnSignInPageAsync` method within `AccountJourneys.cs` utilizes this pattern, expecting `PlaywrightException` to catch timeout issues. However, `WaitForURLAsync` throws a `System.TimeoutException` upon timeout, not `PlaywrightException`! This oversight means a timeout in `SignInWithPasskeyAsync` will result in an unhandled exception, causing the method to fail. My immediate priority is to examine other instances of this error.


**Pinpointing Timeout Discrepancies**

I'm now investigating scenarios 12 and 13 to understand why they passed, even though conditional mediation might not have fired. My initial hypothesis is that the virtual authenticator's `automaticPresenceSimulation = true` swiftly triggered `tryAutofillPasskey` within the 4-second window. This allowed `WaitForURLAsync` to return `false` before a timeout could occur. I'm now checking `HiddenRecordJourneys.cs`, specifically the `FilterByUsernameAsync` function, which incorrectly catches `PlaywrightException` around a `WaitForURLAsync` call, and other instances of `WaitForAsync` and `WaitForFunctionAsync` to confirm this potential issue is widespread.


**Examining E2E Test Failures**

I'm now zeroing in on instances where `PlaywrightException` is incorrectly caught, specifically in relation to `TimeoutException` within E2E tests, across files such as `Journeys`, `DisplayJourneys`, and other related journeys. I'm expanding my analysis to encompass security, authentication, and edge cases in the `MyRestaurant.WebApplication` project, and this includes `SignIn.razor` and `Register.razor`. I'm going through `RestaurantSignInManager.cs` to double check the logic in `PasskeySignInAsync`.


**Analyzing Sign-in Persistence**

I'm now analyzing the `PasskeySignInAsync` logic, specifically focusing on the `isPersistent` parameter within `SignInOrTwoFactorAsync`. I'm noting that it's set to `false` for passkey sign-in, while password sign-in and guest registration pass `isPersistent: true`. I am exploring whether this is an intentional design choice, or a potential minor inconsistency in authentication cookie persistence. The current implementation sets the session cookie with `ExpireTimeSpan = 24h` anyway, even with `isPersistent: false`.


**Investigating Sign-in Persistence**

I'm revisiting the `PasskeySignInAsync` logic again and checking if `base.PasskeySignInAsync` has different behavior in .NET 11. I am confirming that `DapperUserStore.FindPasskeyAsync` and `AddOrUpdatePasskeyAsync` are still consistent regarding the `SignatureCounter`. The `bigint` PostgreSQL type correctly casts to `uint` on read, which aligns with the WebAuthn specification. Finally, I'm now looking at `PublicOriginMiddleware.cs` and `ForwardedHeaders` to ensure I have a complete understanding.


**Uncovering Trust Misconfigurations**

I'm now diving into the implications of `PublicOriginMiddleware` and its interaction with `SecurityHeadersMiddleware`. I've found a critical misconfiguration: if the `SecurityHeadersMiddleware` runs after the `PublicOriginMiddleware`, and a malicious actor sends a crafted `Origin` header, the trusted origin check might be bypassed, exposing the application to potential security vulnerabilities. I'm now testing different scenarios, and checking all the affected configuration options.



**Unveiling Origin Handling**

I'm now investigating the potential security implications of the `PublicOriginMiddleware` and `SecurityHeadersMiddleware`. If `SecurityHeadersMiddleware` runs after `PublicOriginMiddleware`, a malicious actor could bypass the trusted origin check, resulting in security vulnerabilities. I'm focusing on the interaction of `Origin` headers and the `Request.Host`, and am now checking `RestaurantOptions.cs`.



**Examining `.env` Configuration**

I'm now investigating a crucial aspect: the relationship between `.env.example` configurations and `RestaurantOptions.cs`, specifically focusing on how origin patterns are handled. I've noted a specific instruction in `.env.example` regarding Cloudflare quick tunnels, which defaults to including them. My analysis now expands to the code handling this configuration, verifying if the default behavior is correctly enforced and how to restrict passkey usage to only `RESTAURANT_PUBLIC_ORIGIN`.


**Uncovering Critical Origin Vulnerability**

I've just uncovered a significant vulnerability related to trusted origins. Setting `RESTAURANT_TRUSTED_ORIGIN_PATTERNS` to a space or empty string *does not* result in an empty list as intended. Instead, the default pattern `https://*.trycloudflare.com` is used, contradicting the explicit instructions in `.env.example`. This critical issue makes restricting passkey usage to only `RESTAURANT_PUBLIC_ORIGIN` impossible via configuration alone, even with `compose.yaml` defaults.


**Assessing Authorization Flow**

I'm now analyzing the authorization flow, focusing on `TableDisplay.razor` and `DisplayDeviceAuthenticationMiddleware.cs`, specifically the `ShouldResolveDevice` method. I'm verifying the SignalR circuit behavior and how it authenticates the `context.User` as `DisplayDevicePrincipal`. I am clarifying how the SignalR connection affects the authentication process.



**Clarifying Authentication Lifecycle**

I am now clarifying the exact authentication lifecycle when a display device's session is revoked while its SignalR circuit is open, or during a `RefreshAsync` run. I am pinpointing how `RevalidateAsync` returning `null` impacts state, navigation, and the `DisplayDeviceAuthenticationMiddleware`. The timing of cookie deletion and state transitions is now under intense examination.

I'm now diving into the interplay of session revocation, `RevalidateAsync`, and the SignalR circuit's role to confirm.


**Pinpointing Device Authentication**

I'm now zeroing in on the display device authentication flow, specifically how cookies are written and deleted within `DisplayPair.razor`. I've confirmed that the `DisplayDeviceCookie.Write` method sets secure, HTTP-only, and lax same-site attributes on the cookie. The subsequent navigation to `DisplayRoutes.ForTable(table)` is now being reviewed in detail to see how this interacts with the SignalR circuit.



**Confirming State Transitions**

I'm now confirming the state transitions and user experience for guest users when a sitting is closed. I've re-examined the scenarios: active circuit vs. page reload. My focus remains on how the state of the guest table is correctly shown via existing connections or after a refresh. I'm focusing on clarifying how SignalR impacts the application.



**Analyzing Menu Loading**

I'm now carefully examining the menu loading process within `TableOrderSurface.razor`, specifically lines 608-617. I'm focusing on the `LoadMenuAsync` method's interaction with the `MenuDirectory`, `MenuItemPictures`, `MenuItemReactionReads`, and `MenuItemCommentReads` services. My aim is to understand potential performance bottlenecks and their impact on user experience, and how `_stopping` is used.



**Investigating Menu Updates**

I'm now carefully examining the menu change handling. I am focused on the guest 86'd item scenarios and confirming the `TableOrderSurface`'s state transitions, specifically during prerendering. I am digging into `OnInitializedAsync`, `OnAfterRender`, and the potential race between them. I'm clarifying state updates and potential race conditions in circuit startup.



**Examining Menu Updates**

I'm now carefully examining the menu change handling, with a focus on the timing between `LoadAsync()` and `Broadcaster.Subscribe(...)`. I'm verifying the potential for missed notifications and race conditions during prerendering in Blazor Server, particularly in `TableOrderSurface.razor` and `KitchenBoard.razor`. I'm focused on how `OnInitializedAsync` and `OnAfterRender` interact.


**Examining Synchronization Timing**

I'm now carefully examining the timing synchronization between the server's render batch sent over SignalR, and the browser's response. I've found a crucial issue, specifically in Blazor Server. The `WaitForSelectorAsync` in the E2E test might resolve *before* the server's `Broadcaster.Subscribe` in `OnAfterRenderAsync` is triggered. This occurs because the browser's `OnAfterRenderCompleted` runs after the DOM updates from SignalR, causing a race condition and a potential bug.



**Eliminating SignalR Race Conditions**

I'm focusing now on fixing a critical race condition. It looks like `WaitForSelectorAsync` in the E2E tests might complete before the server's `Broadcaster.Subscribe` in `OnAfterRenderAsync` triggers. This issue occurs because the browser's `OnAfterRenderCompleted` runs after the SignalR DOM updates, creating a bug. Subscribing in `OnInitializedAsync`, rather than `OnAfterRenderAsync`, should fix this issue.


**Verifying Notification Handling**

I'm now cross-referencing notification handling across multiple components. I'm noting the robust error handling in `KitchenBoard.razor`'s `OnDomainNotification`, which contrasts with the missing `_disposed` check within the `InvokeAsync` in `TableOrderSurface.razor`. I am now making sure that the same pattern is used in all cases.


**Verifying Notification Consistency**

I'm now cross-referencing notification handling and focusing on making all the notifications consistent. I've noted a robust approach with error handling in `KitchenBoard.razor`. I'm confirming this is present across the entire application, specifically checking if the checks in `KitchenBoard.razor` are also present in `TableOrderSurface.razor`.


**Validating Alert Logic**

I'm now validating the alert logic, confirming that `_announcedAlertToken` prevents repeated sound alerts. I've confirmed `ArmSoundAsync`'s behavior, and how it correctly defers sounds when there are already unseen alerts. The alert token is used as designed. I am now confirming `MenuWorkflow` & `ManageMenuItem.razor` & `MenuItemImageEndpoints.cs`.



**Assessing File Upload Limits**

I'm now assessing the implications of large file uploads without JavaScript, focusing on `menu-picture.js`. I'm verifying the interplay of .NET Core's and Kestrel's limits and the PostgreSQL constraint. I am confirming if reading files into a `MemoryStream` bypasses the design's intent to avoid hardcoding limits and correctly validates the constraints.



**Examining Static SSR Updates**

I'm now carefully examining the static server-side rendering (SSR) in `ManageMenuItem.razor`. I've identified that `AttachPictureAsync` can fail and re-render the page without altering input fields from the user. Because this is static SSR, values will reset. This causes potential data loss if an image upload fails, but other data has been typed into other fields. I'm checking how this might affect form fields.


**Validating Form Persistence**

I'm now validating a significant issue discovered in static SSR scenarios: form data is lost upon re-rendering due to POST errors because input field values are only populated on GET requests. This affects all the form-based pages in the `Manage` section, including `ManageMenuItem.razor`, `ManageMenuSection.razor`, `ManageTable.razor`, and `ManagePerson.razor`. I must now check for all the scenarios.


**Addressing Form Re-renders**

I'm now zeroing in on a critical UX bug in static SSR related to form re-renders on `ManageMenuItem`. I've determined that when a form on the page fails, all other form fields reset to their default values because data is only populated on GET requests. This creates data loss if image uploads or other POST operations fail. I must now check for all the scenarios.


**Validating Form Persistence**

I'm now validating a significant issue. A `EditForm` validation failure in one form on `ManageMenuItem.razor`, leads to a re-render. All the other form's fields get wiped to empty/0 because data only populates during GET requests. This is because values are only populated on GET requests. I'm now checking if this is also the case with other forms.


**Examining Missing Event Descriptions**

I'm now zeroing in on missing descriptions in `EventExplorer.razor` for three event types. Specifically, `"description_changed"`, `"section_changed"`, and `"reordered"` are not properly handled within `DescribeMenuEventType`. This results in raw event names appearing in the dropdown and event list, creating an unclear user experience. Also, the absence of corresponding fields projection in `EventExplorerReads.cs` has also created issues.


**Verifying Missing Descriptions**

I'm now carefully checking the missing descriptions in `EventExplorer.razor` and ensuring all event types are correctly described. I will focus on making sure all event descriptions are properly handled within `DescribeMenuEventType`, including those related to  `"description_changed"`, `"section_changed"`, and `"reordered"`. I will also check against `EventExplorerReads.cs` to ensure corresponding fields.


**Analyzing Order Handling**

I'm now carefully analyzing SQL transaction isolation levels to mitigate potential race conditions and deadlocks, particularly in order management. My review highlights a potential deadlock risk between `AppendToLivingOrderAsync` and `CloseAndSettleAsync`, and the concurrency of `CreateOrderIfAbsentSql`. I am looking for ways to improve this.


**Verifying Passkey Signature Counter**

I'm now verifying the handling of the passkey's `SignatureCounter`, specifically within `DapperUserStore.cs`. I've confirmed that the `SignatureCounter` is correctly cast as `uint` from the `bigint` PostgreSQL type. Also, I've checked the interactions of the `FindPasskeyAsync` and `GetPasskeysAsync` methods, making sure they're consistent with the WebAuthn specification.


**Examining Argon2id and Containers**

I'm now diving into password hashing and container configurations. I have checked how `Argon2idPasswordHasher.cs` handles concurrency with a `SemaphoreSlim` and proper disposal, which seems correct. I'm verifying the container builds, checking the use of `INFORMATIONAL_VERSION` and `dotnet publish` in `Containerfile` and the use of the scripts in the `scripts/` directory.



**Examining Container Orchestration**

I'm now investigating the container orchestration scripts, focusing on `restore.sh`. It unconditionally selects `podman` if `podman-compose` is available, which could cause issues on dual-engine hosts if containers are running under Docker. I am making sure this is consistent with other scripts.


**Assessing Key Backup/Restore**

I'm now carefully assessing the logic in `backup.sh` and `restore.sh`, focusing on potential issues in key file handling within containers. Specifically, I'm analyzing the effects of the `podman cp` command used with and without the trailing `/.` to understand how the tar archive's contents are structured during backup and restoration processes. I am also checking whether the application container needs to be running or stopped when restoring.

\n\n
I've discovered the `tar -tf` command in `backup.sh` creates "./key-....xml" (root) entries. However, extracting into `$KEYS_DIRECTORY` places `./key-....xml` inside `$KEYS_DIRECTORY`. Later, when restore is called, the application container needs to be stopped. Also, this has to work for a GH runner, since `restore_drill.sh` creates its own scratch container and DOES check `CONTAINER_ENGINE`.


**Revising Technical Documentation**

I'm now revising the technical documentation to reflect the codebase's move to .NET 11. I've noted numerous references to .NET 10 within `TECHNICAL_SPECIFICATION.md` and related documents, including §1, §3.1, §3.3, and several ADRs. This needs to be systematically updated to ensure accuracy and clarity regarding the current .NET 11 implementation.


**Updating Documentation and README**

I am now updating the documentation to reflect .NET 11 and refining the README to fix outdated and inaccurate notes, and checking README.md. I've noted a few issues, including outdated references to .NET 10, incomplete markdown formatting, and statements about anonymous `/register` endpoints. I'm focusing on ensuring accuracy and providing clear, current information.


**Correcting Security Documentation**

I'm now correcting critical inaccuracies in `README.md` and `SECURITY.md` regarding rate limiting on the `/register` endpoint. The documentation incorrectly states that the endpoint is not rate-limited, but since Slice 62, this is no longer accurate. I'm updating these documents to reflect the implemented rate limiting for enhanced security.


**Addressing Documentation Accuracy**

I'm now focusing on discrepancies between the codebase and its documentation, particularly regarding rate limiting on the `/register` endpoint and the number of end-to-end scenarios. I'm updating `README.md` and `SECURITY.md` to accurately reflect the implemented rate limiting. I am also checking the number of ADRs and end-to-end scenarios in detail.


**Verifying Documentation Accuracy**

I'm now focusing on discrepancies between the codebase and its documentation, specifically counting E2E scenarios. I'm checking existing tests and confirming the documented number of scenarios. I'm cross-referencing this against the actual implementation within the E2E tests and README. I am now updating all the discrepancies.


**Validating Test Coverage**

I'm now validating the test coverage and confirming the number of assertions matches the [Fact] attributes in the test files: `MenuItemCommentStaffReadContractTests.cs`, `MenuItemCommentTests.cs`, and `HarnessSnapshotContractTests.cs`. Also, the test runs on `SourceCode.WithoutComments` are correct. I'm focusing on the `SourceCommentContractTests` test and the `SourceCode.WithoutComments` contract.


**Validating Trusted Origin Configuration**

I'm now zeroing in on a critical misconfiguration: attempting to clear `RESTAURANT_TRUSTED_ORIGIN_PATTERNS` results in using the default Cloudflare origin pattern, contradicting documentation. The configuration file and origin parsing logic are analyzed. Setting the variable to a space, empty string, or similar values reverts to the default `https://*.trycloudflare.com`, which cannot be disabled. This impacts security, and I'm updating the docs.


**Addressing Origin Pattern Overrides**

I'm now zeroing in on a critical misconfiguration: attempting to clear `RESTAURANT_TRUSTED_ORIGIN_PATTERNS` results in using the default Cloudflare origin pattern, contradicting documentation. The configuration file and origin parsing logic are analyzed. Setting the variable to a space, empty string, or similar values reverts to the default `https://*.trycloudflare.com`, which cannot be disabled. This impacts security, and I'm updating the docs. Furthermore, EventExplorer's event descriptions are also being validated.


**Addressing Event Descriptions**

I'm now focusing on adding correct descriptions in `EventExplorer.razor` for three specific event types: `"description_changed"`, `"section_changed"`, and `"reordered"`. The absence of descriptions currently causes raw event names, so I'll create a better user experience by fixing this. I am checking if `ExplorerEvent` projections also have missing fields.


**Validating Form Persistence**

I'm now validating a significant issue in the form persistence and re-render behavior. My analysis confirms that when a form validation fails, or throws an exception, all other input fields reset, leading to data loss in static SSR. This happens with any form on `ManageMenuItem.razor`, and other form pages. I must identify all cases.


**Examining Authentication Lifecycle**

I am now thoroughly examining the authentication lifecycle, specifically how sessions and cookies behave when SignalR circuits are involved. My analysis focuses on precisely how `RevalidateAsync` impacts the circuit and when cookie deletion occurs. I am confirming the impact of SignalR.


**Updating Documentation Accuracy**

I'm now carefully updating stale documentation in `README.md` and `SECURITY.md`, specifically regarding rate limiting on the `/register` endpoint. The docs incorrectly state it's not rate-limited, but this has been implemented. Slice 62 now applies rate limiting. I am ensuring that all discrepancies are corrected.
































Overall this is a remarkably disciplined, well-structured codebase. The dependency boundaries (`WebApplication → DataAccess → Domain`), relational append-only event schema with composite-FK subtype enforcement, row-level locking protocol (`FOR SHARE` on `table_sitting` + `FOR UPDATE` on `guest_order` / `table_sitting`), and the self-verifying contract test suite in `MyRestaurant.WebApplication.Tests` are all unusually thorough.

Below are the concrete issues and subtle edge cases found across the repository, grouped by severity.

---

### 1. Bugs & Configuration Defects

#### `RESTAURANT_TRUSTED_ORIGIN_PATTERNS` cannot actually be cleared
* **Where:** `src/MyRestaurant.WebApplication/Configuration/RestaurantOptions.cs` (`ReadOriginPatterns`), `.env.example`, `compose.yaml`
* **What happens:** `.env.example` instructs operators:
  > *clear it (set to a single space) to lock passkeys to `RESTAURANT_PUBLIC_ORIGIN` only.*
  
  However, `RestaurantOptions.ReadOriginPatterns` does:
  ```csharp
  if (string.IsNullOrWhiteSpace(raw))
  {
      return fallback;
  }
  ...
  return parts.Length == 0 ? fallback : parts;
  ```
  Both an empty string and `" "` return `DefaultTrustedOriginPatterns` (`["https://*.trycloudflare.com"]`), and any non-empty sentinel like `"none"` is rejected by `Validate()` (`IsValidOriginPattern`). As a result, there is currently no way via environment configuration to disable `https://*.trycloudflare.com` and lock WebAuthn RP derivation to `RESTAURANT_PUBLIC_ORIGIN` alone.
* **Fix:** Distinguish unset (`raw is null`) from explicitly blank/whitespace (returning `[]`), and note in `compose.yaml` how an empty/whitespace override behaves with `${VAR:-default}` on compose engines.

#### `EventExplorer.razor` does not translate the newer menu event types
* **Where:** `src/MyRestaurant.WebApplication/Components/Pages/Administration/EventExplorer.razor` (`DescribeMenuEventType`) vs `src/MyRestaurant.DataAccess/Events/EventExplorerReads.cs` (`EventTypeCatalogue.MenuEventTypes`)
* **What happens:** `EventTypeCatalogue.MenuEventTypes` was widened for migrations `0004` and `0005` to include `"description_changed"`, `"section_changed"`, and `"reordered"`. In `EventExplorer.razor`, `DescribeMenuEventType` only handles the original five (`created`, `name_changed`, `price_changed`, `activated`, `deactivated`). As a result, both the Type filter `<select>` and the event list render raw `"description_changed"`, `"section_changed"`, and `"reordered"` strings, and `ExplorerEvent` does not project `new_description`, `new_display_order`, or `new_menu_section_identifier` from `menu_item_event`.
* **Note:** `MenuEventVocabularyContractTests` checks `ManageMenuItem.razor`'s `DescribePicture` against `menu_item_image_event_type_vocabulary` and checks `EventTypeCatalogue.MenuEventTypes` against `menu_item_event_type_vocabulary`, which is why this wasn't caught by a contract test.

#### Multi-form static-SSR admin pages wipe sibling form values on POST errors
* **Where:** `ManageMenuItem.razor`, `ManageMenuSection.razor`, `ManageTable.razor`
* **What happens:** In `ManageMenuItem.razor`, `OnInitializedAsync` initializes all six `[SupplyParameterFromForm]` models (`??= new()`), and only populates their values from `_item` / `_picture` inside `if (HttpMethods.IsGet(HttpContext.Request.Method))`.
  If any one form fails validation or renders an error on POST without redirecting (e.g., `AttachPictureAsync` fails with `UnsupportedContentType`/`BytesOverCap`, or `MoveAsync` fails), the *other* forms on the page re-render with default empty/zero values (`Name = ""`, `PriceAmount = 0`, `DisplayOrder = 0`, `MenuSectionIdentifier = Guid.Empty`, `AltText = ""`).
* **Fix:** Populate any input model that wasn't the target of the submitted form (or default-populate unless a form's fields were present in `HttpContext.Request.Form`).

---

### 2. Concurrency & Test Harness Edge Cases

#### SignalR subscription timing window in interactive surfaces
* **Where:** `KitchenBoard.razor`, `TableOrderSurface.razor`, `CounterBoard.razor`, `CounterSitting.razor`
* **What happens:** These four components establish `Broadcaster.Subscribe(OnDomainNotification)` only inside `OnAfterRender` / `OnAfterRenderAsync(firstRender: true)`. However, `data-live="true"` and `data-loaded="true"` are rendered during `OnInitializedAsync` and pushed to the browser before the render-completion acknowledgment invokes `OnAfterRender(firstRender: true)`. Any domain notification published in that sub-RTT window after `LoadAsync()` finishes and before `OnAfterRender` runs is missed.
* **Contrast:** `TableDisplay.razor` already handles this cleanly by calling `StartLiveWorkIfNeeded()` (guarded by `_liveWorkStarted` and `RendererInfo.IsInteractive`) in both `OnInitializedAsync` and `OnAfterRender`. Also, `TableOrderSurface.OnDomainNotification`'s `InvokeAsync` callback doesn't check `if (_disposed) return;` at the top like `KitchenBoard` and `CounterBoard` do.

#### `AccountJourneys.IsStillOnSignInPageAsync` and other harness waits only catch `PlaywrightException`
* **Where:** `tests/MyRestaurant.EndToEnd.Tests/Harness/AccountJourneys.cs`, `HiddenRecordJourneys.cs`
* **What happens:** Slice 76 discovered that Playwright .NET throws `System.TimeoutException` (not `PlaywrightException`) when `WaitForURLAsync` times out, and updated `DisplayJourneys.PairAsync` accordingly.
  The same gap remains in `AccountJourneys.IsStillOnSignInPageAsync`:
  ```csharp
  await page.WaitForURLAsync(
      HasLeftSignInPage,
      new PageWaitForURLOptions { Timeout = (float)grace.TotalMilliseconds });
  return false;
  ```
  `SignInWithPasskeyAsync` relies on this returning `true` if conditional mediation hasn't auto-signed-in within 4 seconds so it can fall back to filling `#username` and clicking `__passkeySubmit`. If conditional mediation ever doesn't complete within 4 seconds, `TimeoutException` escapes uncaught instead of triggering the fallback. The diagnostic wrappers around `WaitForURLAsync` in `AccountJourneys` and `HiddenRecordJourneys` also miss `TimeoutException`.

#### `scripts/restore.sh` ignores `CONTAINER_ENGINE`
* **Where:** `scripts/restore.sh`
* **What happens:** `scripts/backup.sh` and `scripts/restore_drill.sh` both respect `CONTAINER_ENGINE` (and `backup.sh` probes engines to find which daemon owns `POSTGRES_CONTAINER`). `scripts/restore.sh` ignores `CONTAINER_ENGINE` and hardcodes choosing `podman-compose` / `podman` whenever `podman-compose` is on `PATH`, even if the target containers belong to `docker`.

---

### 3. Documentation Drift

#### `README.md` and `SECURITY.md` still claim `/register` has no rate limit
* **Where:**
  * `SECURITY.md` (*Read §17 first*): `- /register has no rate limit.`
  * `README.md` (*Known caveats*): `- /register is anonymous and not rate-limited.` and `"/display/pair is the only limited endpoint"`
* **Reality:** Since Slice 62 (F-115), `Register.razor` has `[EnableRateLimiting(RateLimitedSurfaces.GuestRegistrationPolicy)]` (default 60 attempts / 10 min per client IP), and `TECHNICAL_SPECIFICATION.md` §11.8, §13, and §17 were updated to reflect it. Leaving the old claim in `SECURITY.md` and `README.md` is particularly confusing since `SECURITY.md` directs researchers to §17 for that item.

#### Stale counts and `.NET 10` references outside spec-enforced sections
* **`README.md`:**
  * Under *Layout*: mentions `fourteen decision records` (there are 15: `0001`–`0015`).
  * Under *End-to-end scenarios*: says `All sixteen are implemented` (there are 21 scenarios across `EndToEndScenarios.cs`, `MenuPictureScenarios.cs`, and `MenuReactionScenarios.cs`).
  * Under *Continuous integration*: says `~970 facts` (1332 tests as of Slice 76).
* **`docs/TECHNICAL_SPECIFICATION.md` & ADRs:** §1, §3.1, §3.3, and ADRs `0001`, `0003`, `0011` still reference `.NET 10` for Identity/WebAuthn after F-131 moved the target framework to `.NET 11`.
* **`docs/DOCUMENTATION_REVIEW.md`:** Row `F-55` has a truncated second cell (`**The command came back this time`), leaving an unclosed `**`.

---

### 4. What Looks Particularly Strong

* **Schema & transaction integrity:** Named CHECK constraints on `menu_item_image`, `menu_item_comment_event`, and `menu_item_event`, composite FKs enforcing event-type/operation-table alignment in `order_event`, and reading numeric caps back from `pg_constraint` (`ReadDeclaredByteCapAsync` / `ReadDeclaredBodyCapAsync`) rather than duplicating constants in C# are cleanly executed.
* **Security posture:** `SecurityHeadersMiddleware` runs ahead of rate limiting, static files, and obligations middleware, strips the framework's partial `frame-ancestors` policy, derives `connect-src` dynamically per host, and is guarded by `ContentSecurityPolicyContractTests` and `RawHtmlContractTests`.
* **Contract tests:** Computing subject sets from the tree rather than maintaining hardcoded lists (`ContainerLoggingContractTests`, `HarnessSnapshotContractTests`, `MenuItemCommentStaffReadContractTests`, `LiveSurfaceContractTests`, `HandheldLayoutContractTests`) plus `TestingSectionContractTests` keeping §16.4 honest is a very effective pattern.
* **Slice 76 fixes:** Returning to per-class PostgreSQL containers while keeping the host-side `SELECT 1` probe, shared `PostgreSqlTestServer` builder, and first-timeout short-circuit directly addresses both the Podman 6 hang and the `53300: too many clients already` pool exhaustion.

If you'd like, we can address any of these (such as fixing `RESTAURANT_TRUSTED_ORIGIN_PATTERNS` clearing, completing `DescribeMenuEventType` in `EventExplorer.razor`, fixing the `IsStillOnSignInPageAsync` `TimeoutException` catch, or cleaning up the `README.md`/`SECURITY.md` drift) as a focused slice.











