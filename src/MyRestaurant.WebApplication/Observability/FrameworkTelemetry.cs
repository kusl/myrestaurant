namespace MyRestaurant.WebApplication.Observability;

internal static class FrameworkTelemetry
{
    internal const string ActivitySourceName = "Microsoft.AspNetCore";

    internal static readonly string[] MeterNames =
    [
        "Microsoft.AspNetCore.Hosting",
        "Microsoft.AspNetCore.Server.Kestrel",
        "Microsoft.AspNetCore.Http.Connections",
        "Microsoft.AspNetCore.Routing",
        "Microsoft.AspNetCore.Diagnostics",
        "Microsoft.AspNetCore.RateLimiting",
    ];
}
