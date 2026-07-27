using MyRestaurant.DataAccess.Sittings;
using MyRestaurant.DataAccess.Tables;
using MyRestaurant.WebApplication.Sittings;

namespace MyRestaurant.WebApplication.Tables;

/// <summary>
/// Wires the table and sitting services (TECHNICAL_SPECIFICATION §4, §5). Five groups:
///
/// <list type="bullet">
///   <item><description><b>Management (§4.1)</b> — the read-only <see cref="ITableDirectory"/> the
///   administration tables list/detail pages read from, and the transactional
///   <see cref="ITableAdministration"/> those pages write through (create, rename, rotate the join
///   secret, deactivate/reactivate).</description></item>
///   <item><description><b>Join tokens (§4.3–§4.5)</b> — the server-only
///   <see cref="ITableJoinSecretReader"/> and the <see cref="ITableJoinTokens"/> service that reads the
///   secret through it to render a table's current rotating QR (the counter/admin fallback, §4.5) and to
///   validate a presented token, recording <c>table_join_tokens_validated_total{result}</c>
///   (§12).</description></item>
///   <item><description><b>Sittings (§5.1)</b> — the read-only <see cref="ISittingDirectory"/> the table
///   surface asks "is this person already a member here, and who else is?", and the transactional
///   <see cref="ISittingMembership"/> that opens a sitting and inserts membership atomically when a
///   grant is consumed.</description></item>
///   <item><description><b>Settlement (§5.3, §11.3)</b> — <see cref="ICounterBoardReads"/>, the counter's
///   roll-up of open and recently-closed sittings; <see cref="ISittingSettlement"/>, the one transaction
///   that stamps <c>closed_at</c> and the settled total under <c>FOR UPDATE</c>; and
///   <see cref="ISittingWorkflow"/>, the post-commit shell that counts and announces the close.
///   Surfaces take the workflow, never the settlement directly — a close nobody hears about leaves a
///   settled table still taking orders on every phone that already had the page open
///   (§9, §11.1).</description></item>
///   <item><description><b>The join grant (§4.4)</b> — <see cref="JoinGrantProtector"/>, which
///   encrypts and verifies the short-lived cookie that carries proof-of-scan across the detour through
///   sign-in or registration.</description></item>
/// </list>
///
/// <para>Kept separate from <c>AddRestaurantIdentity</c> because tables and sittings are a §4/§5
/// concern, not identity; both are registered from <c>Program.cs</c>. The data services are scoped,
/// matching the identity services' lifetime — they hold no state and open their own connection per call
/// from the singleton <see cref="MyRestaurant.DataAccess.IDatabaseConnectionFactory"/>; their other
/// dependencies (<see cref="MyRestaurant.Domain.Time.IClock"/>,
/// <see cref="MyRestaurant.Domain.Identifiers.IIdentifierFactory"/>, the options, the metrics, and the
/// broadcaster) are singletons registered before this call. The grant protector is a singleton: it wraps
/// one <c>IDataProtector</c> derived once from the singleton provider, and holds nothing per
/// request.</para>
/// </summary>
public static class TablesServiceCollectionExtensions
{
    public static IServiceCollection AddRestaurantTables(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Management (§4.1).
        services.AddScoped<ITableDirectory, DapperTableDirectory>();
        services.AddScoped<ITableAdministration, DapperTableAdministration>();

        // Join tokens (§4.3–§4.5). The secret reader is the only path to the server-only join secret;
        // the token service is its sole consumer, turning the secret into the rotating QR and the
        // validation outcome.
        services.AddScoped<ITableJoinSecretReader, DapperTableJoinSecretReader>();
        services.AddScoped<ITableJoinTokens, TableJoinTokens>();

        // Sittings (§5.1). The directory answers the membership question §4.4's "members bypass tokens"
        // rule turns on; the membership service is the single write path a consumed grant flows into.
        services.AddScoped<ISittingDirectory, DapperSittingDirectory>();
        services.AddScoped<ISittingMembership, DapperSittingMembership>();

        // Settlement (§5.3, §5.4, §11.3). The reads roll money and line counts across whole sittings for
        // the counter's screens; the settlement service is the only thing in the system that writes
        // closed_at, and it does so under the FOR UPDATE that §6.6's FOR SHARE conflicts with.
        services.AddScoped<ICounterBoardReads, DapperCounterBoardReads>();
        services.AddScoped<ISittingSettlement, DapperSittingSettlement>();
        services.AddScoped<ISittingWorkflow, SittingWorkflow>();

        // The join grant (§4.4). Depends only on the Data Protection provider registered in Program.cs
        // before this call, so a singleton is safe and avoids re-deriving the protector per request.
        services.AddSingleton<JoinGrantProtector>();

        return services;
    }
}
