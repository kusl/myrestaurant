using MyRestaurant.DataAccess.Sittings;
using MyRestaurant.DataAccess.Tables;
using MyRestaurant.WebApplication.Sittings;

namespace MyRestaurant.WebApplication.Tables;

public static class TablesServiceCollectionExtensions
{
    public static IServiceCollection AddRestaurantTables(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<ITableDirectory, DapperTableDirectory>();
        services.AddScoped<ITableAdministration, DapperTableAdministration>();

        services.AddScoped<ITableJoinSecretReader, DapperTableJoinSecretReader>();
        services.AddScoped<ITableJoinTokens, TableJoinTokens>();

        services.AddScoped<ISittingDirectory, DapperSittingDirectory>();
        services.AddScoped<ISittingMembership, DapperSittingMembership>();

        services.AddScoped<ICounterBoardReads, DapperCounterBoardReads>();
        services.AddScoped<ISittingSettlement, DapperSittingSettlement>();
        services.AddScoped<ISittingWorkflow, SittingWorkflow>();

        services.AddScoped<ISittingRecordReads, DapperSittingRecordReads>();

        services.AddSingleton<JoinGrantProtector>();

        return services;
    }
}
