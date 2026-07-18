using System.Data.Common;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using MyRestaurant.DataAccess;
using MyRestaurant.Domain.Identifiers;
using MyRestaurant.Domain.LiveUpdates;
using MyRestaurant.Domain.Time;
using MyRestaurant.WebApplication.Components;
using MyRestaurant.WebApplication.Configuration;
using MyRestaurant.WebApplication.Identity;
using MyRestaurant.WebApplication.LiveUpdates;
using MyRestaurant.WebApplication.Observability;
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
//   5. forwarded headers → auth → health endpoints → Blazor interactive-server components.

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
// sign-in, hardened cookie auth, security-stamp revalidation, the area authorization policies, and the
// security-event log (§3.1–§3.7, ADR-0003/ADR-0008). Registered after Data Protection because the
// store encrypts the TOTP secret and the auth cookie is protected with it, and after RestaurantMetrics
// because the hasher and sign-in manager report there.
builder.Services.AddRestaurantIdentity(options);

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
// HttpContext.User from the Identity cookie; authorization enforces the area policies (§3.7) once the
// area pages carry [Authorize]. Both sit after static files and before antiforgery/endpoints.
app.UseForwardedHeaders();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
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

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

return 0;
