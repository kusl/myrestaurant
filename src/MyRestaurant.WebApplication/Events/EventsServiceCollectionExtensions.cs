using MyRestaurant.DataAccess.Events;

namespace MyRestaurant.WebApplication.Events;

public static class EventsServiceCollectionExtensions
{
    public static IServiceCollection AddRestaurantEventExplorer(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IEventExplorerReads, DapperEventExplorerReads>();

        return services;
    }
}
