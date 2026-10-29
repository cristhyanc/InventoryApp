using Inventory.Application.Subscriptions;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="ISubscriptionGrandfatheringStore"/> (issue #966).
///
/// <para><strong>One table, one row, and nothing else at all.</strong> It touches
/// <c>SubscriptionGrandfatherings</c> and no other table. The row is a platform row - about no
/// business - so it carries no business key and no query filter, and the writes pass
/// <c>BusinessOwnershipEnforcer</c> because the entity is not <c>IBusinessOwned</c>, not because
/// anything was relaxed. There is no <c>IgnoreQueryFilters</c> here and no statement over a
/// tenant-owned table on any path, including the per-request read of the completion marker.</para>
///
/// <para><strong>The cutoff is fixed by the primary key, not by a query.</strong> The row's key is
/// the fixed <see cref="SubscriptionGrandfathering.SingletonId"/> and the database assigns nothing,
/// so two first runs that both find no row cannot record two cutoffs: the second one's insert
/// violates the primary key, and <see cref="StartAsync"/> turns that into reading the cutoff the
/// other run fixed. Only that violation is recognised; any other database failure stays a database
/// failure. Nothing in this adapter updates <see cref="SubscriptionGrandfathering.CutoffUtc"/> -
/// there is no code path that could.</para>
/// </summary>
public sealed class EfSubscriptionGrandfatheringStore : ISubscriptionGrandfatheringStore
{
    /// <summary>SQLITE_CONSTRAINT, the primary result code a primary-key violation reports.</summary>
    private const int SqliteConstraintViolation = 19;

    private readonly AppDbContext _db;

    public EfSubscriptionGrandfatheringStore(AppDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc />
    public async Task<SubscriptionGrandfatheringState?> FindAsync(CancellationToken cancellationToken)
    {
        var stored = await _db.SubscriptionGrandfatherings
            .AsNoTracking()
            .Select(row => new
            {
                row.CutoffUtc,
                row.CompletedAtUtc,
                row.GrantedBusinessCount,
            })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        // No row is the state every installation is in today, reported as it is: what "the step has
        // never run" means for a business's entitlement is the Domain policy's decision.
        return stored is null
            ? null
            : new SubscriptionGrandfatheringState(
                stored.CutoffUtc,
                stored.CompletedAtUtc,
                stored.GrantedBusinessCount);
    }

    /// <inheritdoc />
    public async Task<SubscriptionGrandfatheringState> StartAsync(
        DateTime cutoffUtc,
        CancellationToken cancellationToken)
    {
        var existing = await ReadAsync(cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            // A cutoff is already recorded, so this run grants by that instant rather than by the
            // one it proposed. This is the whole of "later runs reuse that cutoff and never move
            // it", and it is why there is no update here to review.
            return StateOf(existing);
        }

        var row = new SubscriptionGrandfathering
        {
            Id = SubscriptionGrandfathering.SingletonId,
            CutoffUtc = cutoffUtc,
            CompletedAtUtc = null,
            GrantedBusinessCount = 0,
        };

        _db.SubscriptionGrandfatherings.Add(row);

        try
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsSingletonAlreadyInserted(exception))
        {
            // Another run recorded the cutoff between this one's read and its insert. The recorded
            // cutoff wins, exactly as it does on the ordinary rerun path above.
            _db.ChangeTracker.Clear();
            var recorded = await ReadAsync(cancellationToken).ConfigureAwait(false);
            if (recorded is null)
            {
                // The row the primary key refused is not there to be read: that is not the
                // collision this translates, so the original failure stands.
                throw;
            }

            return StateOf(recorded);
        }

        return StateOf(row);
    }

    /// <inheritdoc />
    public async Task<SubscriptionGrandfatheringState> CompleteAsync(
        DateTime completedAtUtc,
        int grantedBusinessCount,
        CancellationToken cancellationToken)
    {
        var row = await ReadAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "The grandfathering step has no recorded cutoff, so there is nothing to mark "
                    + "complete. Record the cutoff first.");

        if (row.CompletedAtUtc is not null)
        {
            // Already complete. The recorded instant and count belong to the run that finished the
            // work; a rerun that granted nothing finished nothing new, so neither is rewritten.
            return StateOf(row);
        }

        row.CompletedAtUtc = completedAtUtc;
        row.GrantedBusinessCount = grantedBusinessCount;

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return StateOf(row);
    }

    private async Task<SubscriptionGrandfathering?> ReadAsync(CancellationToken cancellationToken) =>
        await _db.SubscriptionGrandfatherings
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

    private static SubscriptionGrandfatheringState StateOf(SubscriptionGrandfathering row) =>
        new(row.CutoffUtc, row.CompletedAtUtc, row.GrantedBusinessCount);

    /// <summary>
    /// Whether <paramref name="exception"/> is the primary key refusing a second row of this
    /// single-row table, and nothing else.
    /// </summary>
    private static bool IsSingletonAlreadyInserted(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqliteException sqlite
                && sqlite.SqliteErrorCode == SqliteConstraintViolation
                && sqlite.Message.Contains("SubscriptionGrandfatherings.Id", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
