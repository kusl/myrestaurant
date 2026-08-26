using MyRestaurant.DataAccess.Menu;
using MyRestaurant.DataAccess.Orders;
using MyRestaurant.WebApplication.Menu;

namespace MyRestaurant.WebApplication.Orders;

public static class OrdersServiceCollectionExtensions
{
    public static IServiceCollection AddRestaurantOrders(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IMenuDirectory, DapperMenuDirectory>();
        services.AddScoped<IMenuAvailability, DapperMenuAvailability>();
        services.AddScoped<IMenuAdministration, DapperMenuAdministration>();
        services.AddScoped<IMenuEventLog, DapperMenuEventLog>();
        services.AddScoped<IMenuWorkflow, MenuWorkflow>();

        services.AddScoped<IMenuSectionDirectory, DapperMenuSectionDirectory>();
        services.AddScoped<IMenuSectionAdministration, DapperMenuSectionAdministration>();
        services.AddScoped<IMenuSectionEventLog, DapperMenuSectionEventLog>();

        services.AddScoped<IMenuItemImageDirectory, DapperMenuItemImageDirectory>();
        services.AddScoped<IMenuItemImageAdministration, DapperMenuItemImageAdministration>();
        services.AddScoped<IMenuItemImageEventLog, DapperMenuItemImageEventLog>();

        services.AddScoped<IMenuItemReactionDirectory, DapperMenuItemReactionDirectory>();
        services.AddScoped<IMenuItemReactions, DapperMenuItemReactions>();

        services.AddScoped<IOrderMutations, DapperOrderMutations>();
        services.AddScoped<IOrderReadModel, DapperOrderReadModel>();
        services.AddScoped<IOrderEventLog, DapperOrderEventLog>();
        services.AddScoped<IKitchenBoardReads, DapperKitchenBoardReads>();

        services.AddScoped<IOrderWorkflow, OrderWorkflow>();

        services.AddScoped<IOrderHistoryReads, DapperOrderHistoryReads>();
        services.AddScoped<IOrderVisibility, DapperOrderVisibility>();
        services.AddScoped<IOrderVisibilityWorkflow, OrderVisibilityWorkflow>();

        services.AddScoped<IKitchenNotifications, DapperKitchenNotifications>();
        services.AddHostedService<KitchenReminderService>();

        return services;
    }
}
