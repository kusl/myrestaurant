using System.Data.Common;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting.StaticWebAssets;
using Microsoft.AspNetCore.HttpOverrides;
using MyRestaurant.DataAccess;
using MyRestaurant.Domain.Identifiers;
using MyRestaurant.Domain.LiveUpdates;
using MyRestaurant.Domain.Time;
using MyRestaurant.WebApplication.Components;
using MyRestaurant.WebApplication.Configuration;
using MyRestaurant.WebApplication.Displays;
using MyRestaurant.WebApplication.Events;
using MyRestaurant.WebApplication.Identity;
using MyRestaurant.WebApplication.LiveUpdates;
using MyRestaurant.WebApplication.Menu;
using MyRestaurant.WebApplication.Observability;
using MyRestaurant.WebApplication.Orders;
using MyRestaurant.WebApplication.Security;
using MyRestaurant.WebApplication.Tables;
using MyRestaurant.WebApplication.Time;
using Npgsql;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddEnvironmentVariables();

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

bool otlpExporterConfigured = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(
        serviceName: "myrestaurant",
        serviceVersion: BuildInformation.Current.InformationalVersion))
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

builder.Services.AddMetrics();
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<IClock, SystemClock>();

builder.Services.AddSingleton(_ => new RestaurantTime(options));
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

builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(options.DataProtectionKeysDirectory))
    .SetApplicationName("myrestaurant");

builder.Services.AddRestaurantIdentity(options);

builder.Services.AddRestaurantTables();

builder.Services.AddRestaurantDisplays();

builder.Services.AddRestaurantRateLimiting();

builder.Services.AddRestaurantOrders();

builder.Services.AddRestaurantEventExplorer();

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

using (IServiceScope migrationScope = app.Services.CreateScope())
{
    migrationScope.ServiceProvider.GetRequiredService<SchemaMigrationRunner>().Run();
}

if (!app.Environment.IsDevelopment())
{
    StaticWebAssetsLoader.UseStaticWebAssets(app.Environment, app.Configuration);
}

app.UseForwardedHeaders();

app.UseMiddleware<PublicOriginMiddleware>();

app.UseMiddleware<SecurityHeadersMiddleware>();

app.UseRateLimiter();
app.UseStaticFiles();
app.UseAuthentication();

app.UseMiddleware<DisplayDeviceAuthenticationMiddleware>();
app.UseAuthorization();
app.UseMiddleware<ObligationsMiddleware>();
app.UseAntiforgery();

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

app.MapRestaurantClock();

app.MapRestaurantAccountEndpoints();

app.MapRestaurantMenuImages();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode(serverOptions => serverOptions.ContentSecurityFrameAncestorsPolicy = null);

app.Run();

return 0;
