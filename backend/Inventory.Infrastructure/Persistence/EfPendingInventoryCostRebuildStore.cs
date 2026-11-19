using Inventory.Application.Costing;
using Inventory.Application.Time;
using Inventory.Infrastructure.Data;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="IPendingInventoryCostRebuildStore"/> (issue #1304).
///
/// <para>It is a two-line adapter on purpose: the rules are
/// <see cref="PendingInventoryCostRebuildRecorder"/>'s, which is the one place a pending rebuild is
/// recorded for every writer that stores a sale (issues #1103, #1105 and #1261) - one row per
/// <c>(business, product)</c>, an instant that only ever moves earlier, a revision that moves on at
/// every enqueue, and the read answered by the database inside the caller's own write-locked
/// transaction. Nothing is saved here: the caller's save and commit are what make the sales and the
/// record of their replay one change.</para>
///
/// <para>The row's owner is not set either - <c>BusinessOwnershipEnforcer</c> stamps it from the
/// resolved scope on save, exactly as it does for every other tenant-owned insert.</para>
/// </summary>
public sealed class EfPendingInventoryCostRebuildStore : IPendingInventoryCostRebuildStore
{
    private readonly AppDbContext _db;
    private readonly IClock _clock;

    public EfPendingInventoryCostRebuildStore(AppDbContext db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    /// <inheritdoc />
    public Task StageAsync(
        IReadOnlyDictionary<long, DateTime> affected, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(affected);

        return PendingInventoryCostRebuildRecorder.StageAsync(
            _db, affected, _clock.UtcNow, cancellationToken);
    }
}
