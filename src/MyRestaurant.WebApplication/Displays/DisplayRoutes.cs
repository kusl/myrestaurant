namespace MyRestaurant.WebApplication.Displays;

public static class DisplayRoutes
{
    public const string Prefix = "/display";

    public const string Pair = "/display/pair";

    public const string BlazorCircuit = "/_blazor";

    public const int PairingAttemptsPerWindow = 5;

    public static readonly TimeSpan PairingRateLimitWindow = TimeSpan.FromMinutes(1);

    public static string ForTable(Guid tableIdentifier) => $"{Prefix}/{tableIdentifier:D}";
}
