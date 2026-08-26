using MyRestaurant.DataAccess.Displays;

namespace MyRestaurant.WebApplication.Displays;

public static class DisplaysServiceCollectionExtensions
{
    public static IServiceCollection AddRestaurantDisplays(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IDisplayDeviceDirectory, DapperDisplayDeviceDirectory>();
        services.AddScoped<IDisplayDevicePairing, DapperDisplayDevicePairing>();
        services.AddScoped<IDisplayDeviceAuthenticator, DapperDisplayDeviceAuthenticator>();

        return services;
    }
}
