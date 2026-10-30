using Inventory.Application.ConnectedMachines;
using Inventory.Domain.ConnectedMachines;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="IConnectedMachineSnapshotStore"/> (issue #978).
///
/// <para><strong>One table, and the only write is an insert.</strong> It touches
/// <c>BusinessConnectedMachineSnapshots</c> and nothing else. Reads go through the central tenant
/// query filter and the insert is stamped by <c>BusinessOwnershipEnforcer</c>, so the business key
/// comes from the scope the caller resolved and never from a parameter: there is no
/// <c>IgnoreQueryFilters</c>, no raw SQL, no business-id argument and no <c>ExecuteUpdate</c> or
/// <c>ExecuteDelete</c> anywhere in it.</para>
///
/// <para><strong>The claim is one transaction with the database's own constraint underneath
/// it.</strong> The check for an existing snapshot and the insert run inside one <c>BEGIN
/// IMMEDIATE</c> transaction, so the write lock is taken before the read the decision is made on -
/// two complete reads cannot both find none. Underneath that, the unique index on (business, UTC
/// date, account series) refuses a second row outright, and a refusal is translated back into "the
/// other reader won" rather than raised: #978 requires that the loser discard its snapshot
/// <em>without an error and without a retry</em>, because the winner's snapshot is exactly as
/// complete as this one.</para>
/// </summary>
public sealed class EfConnectedMachineSnapshotStore : IConnectedMachineSnapshotStore
{
    private readonly AppDbContext _db;

    public EfConnectedMachineSnapshotStore(AppDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ConnectedMachineSnapshot>> FindLatestDayAsync(
        CancellationToken cancellationToken)
    {
        // The latest day first, then that day's rows: a day the business's Nayax account changed on
        // can hold one snapshot per series, and which of them is the day's count is the Domain's
        // decision rather than an ORDER BY written here.
        var latestDay = await _db.BusinessConnectedMachineSnapshots
            .AsNoTracking()
            .MaxAsync(row => (DateTime?)row.SnapshotDateUtc, cancellationToken)
            .ConfigureAwait(false);

        if (latestDay is not { } day)
        {
            return [];
        }

        var stored = await _db.BusinessConnectedMachineSnapshots
            .AsNoTracking()
            .Where(row => row.SnapshotDateUtc == day)
            .OrderBy(row => row.AccountSeries)
            .Select(row => new
            {
                row.SnapshotDateUtc,
                row.AccountSeries,
                row.MachineCount,
                row.ProviderMachineIds,
                row.TakenAtUtc,
                row.ReadWasComplete,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return stored.ConvertAll(row => new ConnectedMachineSnapshot(
            row.SnapshotDateUtc,
            row.AccountSeries,
            row.MachineCount,
            ConnectedMachineFleetRead.ParseIds(row.ProviderMachineIds),
            row.TakenAtUtc,
            row.ReadWasComplete));
    }

    /// <inheritdoc />
    public Task<bool> ExistsForAsync(
        DateTime dayUtc,
        int accountSeries,
        CancellationToken cancellationToken) =>
        _db.BusinessConnectedMachineSnapshots
            .AsNoTracking()
            .AnyAsync(
                row => row.SnapshotDateUtc == dayUtc && row.AccountSeries == accountSeries,
                cancellationToken);

    /// <inheritdoc />
    public async Task<bool> TryRecordAsync(
        ConnectedMachineSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        await using var transaction = await SqliteWriteLockTransaction
            .BeginAsync(_db, cancellationToken)
            .ConfigureAwait(false);

        var alreadyRecorded = await ExistsForAsync(
                snapshot.DateUtc,
                snapshot.AccountSeries,
                cancellationToken)
            .ConfigureAwait(false);

        if (alreadyRecorded)
        {
            // The day and series are claimed. Nothing is written - not even a different count,
            // because a snapshot is append-only evidence and a second read of the same day is not a
            // correction of the first.
            return false;
        }

        // BusinessId is left unset on purpose: BusinessOwnershipEnforcer stamps the caller's business
        // on save, which is also what refuses the write when no business is resolved.
        _db.BusinessConnectedMachineSnapshots.Add(new BusinessConnectedMachineSnapshot
        {
            SnapshotDateUtc = snapshot.DateUtc,
            AccountSeries = snapshot.AccountSeries,
            MachineCount = snapshot.MachineCount,
            ProviderMachineIds = ConnectedMachineFleetRead.FormatIds(snapshot.ProviderMachineIds),
            TakenAtUtc = snapshot.TakenAtUtc,
            ReadWasComplete = snapshot.ReadWasComplete,
        });

        try
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // The database refused the insert. The unsaved entity is detached first, so the caller's
            // context is not left holding a row the database rejected, and then the day is re-read:
            // if it is now claimed, the index refused a duplicate and this reader lost the race #978
            // describes - which is discarded without an error and without a retry, because the
            // winner's snapshot is exactly as complete as this one. Any other write failure is a
            // real failure and propagates.
            _db.ChangeTracker.Clear();

            if (await ExistsForAsync(snapshot.DateUtc, snapshot.AccountSeries, cancellationToken)
                .ConfigureAwait(false))
            {
                return false;
            }

            throw;
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return true;
    }
}
