using System.Data.Common;
using Dapper;
using MyRestaurant.Domain.Time;

namespace MyRestaurant.DataAccess.Sittings;

public enum CloseSittingOutcome
{
    Closed,
    SittingNotFound,
    AlreadyClosed,
}

public sealed record CloseSittingResult(
    CloseSittingOutcome Outcome,
    Guid SittingIdentifier,
    decimal? SettledTotalAmount,
    DateTimeOffset? ClosedAt,
    Guid? ClosedByPersonIdentifier,
    int PendingLineCountAtClose)
{
    public bool IsClosed => Outcome is CloseSittingOutcome.Closed;

    public bool SittingIsClosed => Outcome is CloseSittingOutcome.Closed or CloseSittingOutcome.AlreadyClosed;
}

public interface ISittingSettlement
{
    Task<CloseSittingResult> CloseAndSettleAsync(
        Guid sittingIdentifier,
        Guid closedByPersonIdentifier,
        CancellationToken cancellationToken = default);
}

public sealed class DapperSittingSettlement : ISittingSettlement
{
    private const string LockSittingSql = """
        SELECT table_sitting.table_sitting_identifier    AS SittingIdentifier,
               table_sitting.closed_at                   AS ClosedAt,
               table_sitting.closed_by_person_identifier AS ClosedByPersonIdentifier,
               table_sitting.settled_total_amount        AS SettledTotalAmount
        FROM table_sitting
        WHERE table_sitting.table_sitting_identifier = @SittingIdentifier
        FOR UPDATE;
        """;

    private const string SettledTotalSql = """
        SELECT COALESCE(sum(sitting_bill.person_total_amount), 0)::numeric(10,2)
        FROM sitting_bill
        WHERE sitting_bill.table_sitting_identifier = @SittingIdentifier;
        """;

    private const string PendingLineCountSql = """
        SELECT count(*)::int
        FROM order_current_line AS line
        INNER JOIN guest_order
                ON guest_order.guest_order_identifier = line.guest_order_identifier
        WHERE guest_order.table_sitting_identifier = @SittingIdentifier
          AND NOT line.is_fulfilled;
        """;

    private const string StampClosedSql = """
        UPDATE table_sitting
        SET closed_at = @ClosedAt,
            closed_by_person_identifier = @ClosedByPersonIdentifier,
            settled_total_amount = @SettledTotalAmount
        WHERE table_sitting_identifier = @SittingIdentifier;
        """;

    private readonly IDatabaseConnectionFactory _connectionFactory;
    private readonly IClock _clock;

    public DapperSittingSettlement(IDatabaseConnectionFactory connectionFactory, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(clock);

        _connectionFactory = connectionFactory;
        _clock = clock;
    }

    public async Task<CloseSittingResult> CloseAndSettleAsync(
        Guid sittingIdentifier,
        Guid closedByPersonIdentifier,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = _clock.UtcNow;

        await using DbConnection connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        SittingLockRow? sitting = await connection
            .QuerySingleOrDefaultAsync<SittingLockRow>(new CommandDefinition(
                LockSittingSql,
                new { SittingIdentifier = sittingIdentifier },
                transaction,
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        if (sitting is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new CloseSittingResult(
                CloseSittingOutcome.SittingNotFound,
                sittingIdentifier,
                SettledTotalAmount: null,
                ClosedAt: null,
                ClosedByPersonIdentifier: null,
                PendingLineCountAtClose: 0);
        }

        if (sitting.ClosedAt is { } alreadyClosedAt)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new CloseSittingResult(
                CloseSittingOutcome.AlreadyClosed,
                sittingIdentifier,
                sitting.SettledTotalAmount,
                AsUtc(alreadyClosedAt),
                sitting.ClosedByPersonIdentifier,
                PendingLineCountAtClose: 0);
        }

        int pendingLineCount = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            PendingLineCountSql,
            new { SittingIdentifier = sittingIdentifier },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        decimal settledTotalAmount = await connection.ExecuteScalarAsync<decimal>(new CommandDefinition(
            SettledTotalSql,
            new { SittingIdentifier = sittingIdentifier },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(
            StampClosedSql,
            new
            {
                SittingIdentifier = sittingIdentifier,
                ClosedAt = now,
                ClosedByPersonIdentifier = closedByPersonIdentifier,
                SettledTotalAmount = settledTotalAmount,
            },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new CloseSittingResult(
            CloseSittingOutcome.Closed,
            sittingIdentifier,
            settledTotalAmount,
            now,
            closedByPersonIdentifier,
            pendingLineCount);
    }

    private static DateTimeOffset AsUtc(DateTime value)
        => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private sealed record SittingLockRow(
        Guid SittingIdentifier,
        DateTime? ClosedAt,
        Guid? ClosedByPersonIdentifier,
        decimal? SettledTotalAmount);
}
