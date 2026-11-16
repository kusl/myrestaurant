# Slice 75 — the platform moves, and the suite learns to give up

Extract at the repository root. Spec goes to **v1.60**. Requirements go to **rev 7**. Findings floor moves to **F-131**.

## Files in this archive

| Path | State |
|---|---|
| `global.json` | changed — SDK floor `11.0.100-rc.3.26522.148`, `rollForward: latestMinor`, `allowPrerelease` |
| `Directory.Build.props` | changed — `net11.0` |
| `Directory.Packages.props` | changed — `OpenTelemetry.Instrumentation.AspNetCore` pin removed; two notes |
| `Containerfile` | changed — `sdk:11.0` and `aspnet:11.0` |
| `.github/workflows/ci.yml` | changed — `dotnet-version: "11.0.x"` twice, the `net11.0` Playwright path, three comments |
| `src/MyRestaurant.WebApplication/MyRestaurant.WebApplication.csproj` | changed — one package reference removed |
| `src/MyRestaurant.WebApplication/Program.cs` | changed — the framework's activity source and meters, by name |
| `src/MyRestaurant.WebApplication/Observability/FrameworkTelemetry.cs` | **new** |
| `src/MyRestaurant.WebApplication/Time/RestaurantTime.cs` | changed — `Lock` instead of `object` |
| `src/MyRestaurant.Domain/Orders/OrderEvents.cs` | changed — `closed record class OrderOperation`; two fallback arms removed |
| `src/MyRestaurant.Domain/Orders/OrderMutationValidator.cs` | changed — one fallback arm removed |
| `src/MyRestaurant.DataAccess/Orders/OrderMutations.cs` | changed — one fallback arm removed |
| `tests/Shared/PostgreSqlTestServer.cs` | **new** — the one container builder, host-side readiness, the start deadline |
| `tests/Shared/ContainerEngineDiscovery.cs` | **new** — moved here from the two projects, namespace `MyRestaurant.Testing` |
| `tests/MyRestaurant.DataAccess.Tests/PostgreSqlServerFixture.cs` | **new** — assembly fixture: one server, a database per class |
| `tests/MyRestaurant.DataAccess.Tests/PostgreSqlFixture.cs` | changed — leases a database; same public surface |
| `tests/MyRestaurant.DataAccess.Tests/MyRestaurant.DataAccess.Tests.csproj` | changed — compiles `../Shared` |
| `tests/MyRestaurant.EndToEnd.Tests/MyRestaurant.EndToEnd.Tests.csproj` | changed — compiles `../Shared` |
| `tests/MyRestaurant.EndToEnd.Tests/Harness/RestaurantHarness.cs` | changed — starts the shared server; its own builder and failure prose removed |
| `tests/MyRestaurant.WebApplication.Tests/Deployment/ContainerLoggingContractTests.cs` | changed — floor of one builder; message |
| `tests/MyRestaurant.WebApplication.Tests/Deployment/ContainerImageReferenceContractTests.cs` | changed — one message loses a stale count |
| `README.md` | changed — prerequisites, testing, one Playwright path |
| `docs/REQUIREMENTS.md` | changed — rev 7, §2 |
| `docs/TECHNICAL_SPECIFICATION.md` | changed — header, §1, §2, §12, §16.2, §16.4, Appendix A, changelog |
| `docs/DOCUMENTATION_REVIEW.md` | changed — F-130, F-131, two residuals |
| `docs/OPERATIONS.md` | changed — §12 |
| `docs/BUILD_PROGRESS.md` | changed — the Slice 75 row and narrative |
| `_CHANGES.md` | changed (this file) |

**Files to DELETE: two.**

```bash
git rm tests/MyRestaurant.DataAccess.Tests/ContainerEngineDiscovery.cs
git rm tests/MyRestaurant.EndToEnd.Tests/Harness/ContainerEngineDiscovery.cs
```

Both declare the class that `tests/Shared/ContainerEngineDiscovery.cs` now declares, and each test project compiles `../Shared`, so leaving either in place is a duplicate-type build error rather than a silent leftover.

**Files to ADD: four.** `git add tests/Shared src/MyRestaurant.WebApplication/Observability/FrameworkTelemetry.cs tests/MyRestaurant.DataAccess.Tests/PostgreSqlServerFixture.cs` — `check_tree.sh` reads `git ls-files`, so an unadded file is an unchecked one.

## Expected test count

**1332 — unchanged.** No `[Fact]` or `[Theory]` was added, removed or moved. §16.4's counted-class census stays at **49** over a floor of 37. End-to-end scenarios stay at **21**. Any other number is worth investigating before reading assertion text.

## What landed

**.NET 11 on two SDKs at once.** `global.json` names a floor, not a pin. The workstation has `11.0.100-rc.3.26522.148` and nothing newer; CI's `setup-dotnet` is asked for `11.0.x` and installs the stable `11.0.100`, which is above the floor; the image builds on `sdk:11.0`. When Fedora packages the stable SDK the workstation moves to it with no edit here.

**The suite cannot hang.** `terminal.txt` shows `dotnet test` cancelled at forty-two minutes with both container-backed assemblies at zero tests, on a host upgraded to Fedora 45 and Podman 6 — containers created and running, nothing ready. Readiness was `pg_isready` through the engine's exec endpoint, with no deadline and a silenced logger. It is now a `SELECT 1` from the host, bounded at five minutes; a timeout **fails** with the container's state and log tail. No engine at all is still a skip.

**One server, not one per class.** The data-access assembly starts one container; each test class gets its own database, so no test class changed. `max_connections` is raised to 300 on that container because the classes now share it.

**Deleted because .NET 11 does it.** The ASP.NET Core OpenTelemetry instrumentation package, and four `_ =>` arms over `OrderOperation` that a `closed` hierarchy lets the compiler replace.

**Looked at, already current, not touched.** Every NuGet pin (`dotnet list package --outdated` in `terminal.txt`: no updates) and every GitHub Actions major. PostgreSQL stays on 17 — that is your data, and a new major is a dump-and-restore.

**User data.** No migration, no schema change, no change to any route, cookie, stored secret or backup format. Nothing is rebuilt or cleared on upgrade, because nothing in this slice gives a reason to.

## Not verified

Nothing was compiled — no SDK here and no route to the package feed — so every claim below the file table is read, not run. The Testcontainers calls were checked against the 4.16.0 source except `AddCustomWaitStrategy`, taken from the module's own use of it. `closed` comes from the C# 15 reference. Whether Podman 6's exec endpoint is the call that stalled is inferred, not observed. Whether native ASP.NET Core spans keep the instrumentation package's display names is not known.

## Veto points

1. **`closed record class OrderOperation`.** If the release-candidate compiler refuses it, change `closed record class` back to `abstract record` in `OrderEvents.cs` and restore the removed arms: `_ => throw new ArgumentOutOfRangeException(nameof(operation), operation.GetType().Name, "Unknown operation type.")` in `LineIdentifierOf` and at the end of the insert switch in `OrderMutations.cs`, `_ => false` in `OperationIsAllowedFor`, `_ => "operation"` in `OrderMutationValidator.DescribeOperation`.
2. **The instrumentation package is gone.** To reverse: re-add the `PackageVersion` (1.21.0) and the `PackageReference`, and put `AddAspNetCoreInstrumentation()` back on both builders in `Program.cs` in place of the `FrameworkTelemetry` lines. Worth doing only if a collector's dashboards key on span names and those turn out to differ.
3. **One server per assembly.** If shared-server load shows up as a flake, the smallest reversal is in `PostgreSqlFixture.InitializeAsync`: start a `PostgreSqlTestServer` of its own there and dispose it in `DisposeAsync`. The deadline and the host-side probe stay either way.
4. **The five-minute deadline** is `StartupDeadline` in `PostgreSqlTestServer.cs`, with its text beside it. A cold image pull on a slow link is the only honest reason to raise it.
5. **PostgreSQL 17 was held on purpose.** Moving it is a slice of its own with a restore drill in it.
