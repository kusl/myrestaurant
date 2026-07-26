using System.Data.Common;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using MyRestaurant.DataAccess;
using MyRestaurant.Domain.Identifiers;
using MyRestaurant.Domain.LiveUpdates;
using MyRestaurant.Domain.Time;
using MyRestaurant.WebApplication.Components;
using MyRestaurant.WebApplication.Configuration;
using MyRestaurant.WebApplication.Displays;
using MyRestaurant.WebApplication.Identity;
using MyRestaurant.WebApplication.LiveUpdates;
using MyRestaurant.WebApplication.Observability;
using MyRestaurant.WebApplication.Orders;
using MyRestaurant.WebApplication.Tables;
using Npgsql;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

// The composition root (TECHNICAL_SPECIFICATION §14, BUILD_PROGRESS). Startup order is deliberate:
//   1. bind + validate configuration and fail fast on bad security-relevant settings;
//   2. wire OpenTelemetry (exporters only when an OTLP endpoint is configured);
//   3. register services;
//   4. apply database migrations BEFORE binding HTTP (never serve on a half-applied schema, §17);
//   5. forwarded headers → public-origin host normalization → rate limiting → auth → display-device
//      principal → obligations pipeline → health endpoints → Blazor components.

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddEnvironmentVariables();

// (1) Configuration is environment-only (§13). Validate before a host exists so a misconfigured
// deployment exits with a clear message instead of half-starting.
RestaurantOptions options = RestaurantOptions.FromConfiguration(builder.Configuration);
IReadOnlyList<string> configurationErrors = options.Validate();
if (configurationErrors.Count > 0)
{
    foreach (string error in configurationErrors)
    {
        Console.Error.WriteLine($"Configuration error: {error}");
    }

    return 1;
}

// (2) OpenTelemetry (§12). The OTLP exporters are attached only when OTEL_EXPORTER_OTLP_ENDPOINT is
// set, so a plain `dotnet run` with no collector does not spam connection-refused logs. The meter
// and instrumentation are always registered — they are cheap and keep the custom meter live.
bool otlpExporterConfigured = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(serviceName: "myrestaurant"))
    .WithTracing(tracing =>
    {
        tracing.AddAspNetCoreInstrumentation();
        tracing.AddNpgsql();
        if (otlpExporterConfigured)
        {
            tracing.AddOtlpExporter();
        }
    })
    .WithMetrics(metrics =>
    {
        metrics.AddAspNetCoreInstrumentation();
        metrics.AddNpgsqlInstrumentation();
        metrics.AddMeter(RestaurantMetrics.MeterName);
        if (otlpExporterConfigured)
        {
            metrics.AddOtlpExporter();
        }
    });

if (otlpExporterConfigured)
{
    builder.Logging.AddOpenTelemetry(logging =>
    {
        logging.IncludeScopes = true;
        logging.IncludeFormattedMessage = true;
        logging.AddOtlpExporter();
    });
}

// (3) Services. Everything the domain needs is behind an interface so tests can substitute it.
builder.Services.AddMetrics();
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<IIdentifierFactory, UuidV7IdentifierFactory>();
builder.Services.AddSingleton<IDatabaseConnectionFactory>(
    _ => new NpgsqlDatabaseConnectionFactory(options.DatabaseConnectionString));
builder.Services.AddSingleton<RestaurantMetrics>();
builder.Services.AddSingleton<IDomainEventBroadcaster, InProcessDomainEventBroadcaster>();
builder.Services.AddSingleton(serviceProvider =>
{
    ILogger<SchemaMigrationRunner> logger = serviceProvider.GetRequiredService<ILogger<SchemaMigrationRunner>>();
    return new SchemaMigrationRunner(
        options.DatabaseConnectionString,
        message => logger.LogWarning("{MigrationStatus}", message));
});

// Data-protection keys live on a mounted volume so cookies/tokens survive restarts (§3.4).
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(options.DataProtectionKeysDirectory))
    .SetApplicationName("myrestaurant");

// ASP.NET Core Identity core services over the custom Dapper store, with the Argon2id hasher, plus
// sign-in, hardened cookie auth, the claims factory (roles + §3.5 obligation claims), security-stamp
// revalidation, the area authorization policies, cascading authentication state, and the
// security-event log (§3.1–§3.7, ADR-0003/ADR-0008). Registered after Data Protection because the
// store encrypts the TOTP secret and the auth cookie is protected with it, and after RestaurantMetrics
// because the hasher and sign-in manager report there.
builder.Services.AddRestaurantIdentity(options);

// Table and sitting services (§4, §5): the read-only ITableDirectory the administration tables pages
// read from and the transactional ITableAdministration they write through; the server-only join-secret
// reader and the ITableJoinTokens service that renders and validates the rotating QR (§4.3–§4.5); the
// ISittingDirectory/ISittingMembership pair behind the §4.4 join flow and §5.1 sitting open; and the
// JoinGrantProtector for the short-lived join-grant cookie. A §4/§5 concern, kept separate from
// AddRestaurantIdentity; all of it resolves the same connection factory, clock, identifier factory,
// metrics, and Data Protection provider registered above — hence the position after them.
builder.Services.AddRestaurantTables();

// Display devices (§4.2, §11.5): the read-only IDisplayDeviceDirectory the administration devices page
// lists from, the transactional IDisplayDevicePairing behind issue/redeem/revoke, the
// IDisplayDeviceAuthenticator the request middleware and the live surface re-validate with, and the
// 5-per-minute-per-IP rate-limiter policy /display/pair opts into. A display is a device principal, not
// a person, so it is wired apart from AddRestaurantIdentity — but it renders the rotating QR through
// AddRestaurantTables' ITableJoinTokens, hence the position after it.
builder.Services.AddRestaurantDisplays();

// Menu (read side) and orders (§6, §7, §8.3, §9, §12): the IMenuDirectory the staging area and the "86"
// panel read; IOrderMutations, the single transaction implementing the §6.6 locking protocol;
// IOrderReadModel over the §8.3 projection views and IOrderEventLog over the raw event log; and
// IOrderWorkflow, the post-commit shell that records the §12 counters and publishes the §9 notifications
// — surfaces call that, never IOrderMutations directly, or a send would never reach the kitchen. Last of
// the four groups because an order hangs off a sitting, which AddRestaurantTables registered above.
builder.Services.AddRestaurantOrders();

// The app is only ever reached through a trusted proxy (Caddy in dev, Cloudflare tunnel in prod),
// so honour its X-Forwarded-* headers. KnownIPNetworks/KnownProxies are cleared deliberately — safe
// ONLY because the origin is never exposed directly (BUILD_PROGRESS: forwarded-headers trust).
builder.Services.Configure<ForwardedHeadersOptions>(forwarded =>
{
    forwarded.ForwardedHeaders =
        ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    forwarded.KnownIPNetworks.Clear();
    forwarded.KnownProxies.Clear();
});

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

WebApplication app = builder.Build();

// (4) Migrate before binding HTTP. A failure throws and the process exits non-zero without ever
// serving a request against an incomplete schema (§17: "half-applied schema").
using (IServiceScope migrationScope = app.Services.CreateScope())
{
    migrationScope.ServiceProvider.GetRequiredService<SchemaMigrationRunner>().Run();
}

// (5) HTTP pipeline. No HTTPS redirection — TLS is terminated at the proxy. Authentication populates
// HttpContext.User from the Identity cookie; authorization enforces the area policies (§3.7) on the
// pages that carry [Authorize]; the obligations middleware then makes everything except sign-out and
// the pipeline pages unreachable while a §3.5 flag is set. Static files sit before authentication so
// css/assets are never blocked; antiforgery sits after auth, before endpoints.
app.UseForwardedHeaders();
// Normalize Request.Host to the effective public origin host BEFORE anything derives from it, so the
// .NET 10 passkey handler (RP ID = ServerDomain ?? Request.Host.Host, and ServerDomain is null by
// design) sees the host the browser is actually on — including a Cloudflare quick tunnel's per-run
// *.trycloudflare.com hostname. Sits right after forwarded headers so it can see X-Forwarded-Host and
// before auth/endpoints so the ceremony sees the corrected host (§3.3, ADR-0005).
app.UseMiddleware<PublicOriginMiddleware>();
// Endpoint rate limiting (§4.2: /display/pair is anonymous and limited to 5 attempts/minute/IP). It
// MUST sit after UseForwardedHeaders, because the limiter partitions on the connection's remote
// address — before the forwarded headers are applied that address is the proxy's, and every device in
// the building would share one bucket. Only endpoints carrying [EnableRateLimiting] are affected;
// there is no global limiter, so everything else passes straight through.
app.UseRateLimiter();
app.UseStaticFiles();
app.UseAuthentication();
// A paired table display is a device principal, not a person (§0, §4.2), so it is resolved from its own
// long-lived cookie right after the Identity cookie has had its chance — a signed-in person always
// wins. Plain middleware rather than an authentication scheme on purpose: the display surface is
// interactive, and a circuit takes its principal from the /_blazor request, which authenticates with
// the default scheme; middleware runs there too, so the device reaches the circuit (see the class docs).
app.UseMiddleware<DisplayDeviceAuthenticationMiddleware>();
app.UseAuthorization();
app.UseMiddleware<ObligationsMiddleware>();
app.UseAntiforgery();

// Health endpoints (§12). Liveness is "the process answers"; readiness additionally proves the
// database is reachable and migrations are current — compose healthchecks target these.
app.MapGet("/healthz/live", () => Results.Text("live"));
app.MapGet(
    "/healthz/ready",
    async (IDatabaseConnectionFactory connectionFactory, SchemaMigrationRunner migrationRunner, CancellationToken cancellationToken) =>
    {
        try
        {
            await using DbConnection connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
            await using DbCommand command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            _ = await command.ExecuteScalarAsync(cancellationToken);

            return migrationRunner.IsUpToDate()
                ? Results.Text("ready")
                : Results.Text("migrations pending", "text/plain", statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (Exception)
        {
            return Results.Text("not ready", "text/plain", statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    });

// The POST /sign-out endpoint (antiforgery-protected; exempt from the obligations pipeline).
app.MapRestaurantAccountEndpoints();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

return 0;
