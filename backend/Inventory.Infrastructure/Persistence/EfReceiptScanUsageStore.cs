using Inventory.Application.ReceiptScanning;
using Inventory.Application.Time;
using Inventory.Domain.ReceiptScanning;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="IReceiptScanUsageStore"/> (issue #1164): the current
/// business's receipt-scan count for one of its own calendar months, and the increment that makes a
/// scan safe to make.
///
/// <para><strong>Counting is one serialised operation.</strong> The read of the month's counter, the
/// comparison with the cap and the increment all happen inside one
/// <see cref="SqliteWriteLockTransaction"/>, so the second of two concurrent scans waits, reads the
/// first one's committed count and is refused rather than passing a check only one of them fits
/// through. A deferred transaction would take the write lock after the read the decision rests on,
/// which is the race this exists to remove - the same reason
/// <see cref="EfAttachmentQuotaStore"/> reserves attachment storage that way (issue #968).</para>
///
/// <para><strong>The counter is the count, not a derivation.</strong> Unlike attachment storage,
/// which is summed from the rows that refer to a file, there is nothing here to derive a count from:
/// a scan leaves no record of itself anywhere, by design, and must not. So the number is stored, and
/// it is only ever incremented - never decremented, released or swept. A scan that failed after the
/// reader was called still spent the money, and a month that has passed keeps its count as the
/// evidence of what was spent.</para>
///
/// <para>Every statement here runs through the ordinary tenant query filters and the central
/// ownership stamp: no business id is written, read or accepted anywhere, and another business's
/// counter is simply not there. The Super Admin's own read of one business's count is a separate
/// platform port (<see cref="EfPlatformReceiptScanUsageStore"/>), never a widening of this one.</para>
/// </summary>
public sealed class EfReceiptScanUsageStore : IReceiptScanUsageStore
{
    private readonly AppDbContext _db;
    private readonly IClock _clock;

    public EfReceiptScanUsageStore(AppDbContext db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    /// <inheritdoc />
    public async Task<int> ReadUsedAsync(int monthKey, CancellationToken cancellationToken = default) =>
        await _db.ReceiptScanUsages
            .AsNoTracking()
            .Where(row => row.MonthKey == monthKey)
            .Select(row => (int?)row.ScanCount)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false)
        // A month with no row has made no scans. Zero rather than an absence a caller interprets.
        ?? 0;

    /// <inheritdoc />
    public async Task<int?> TryCountScanAsync(
        int monthKey, int limit, CancellationToken cancellationToken = default)
    {
        await using var transaction = await SqliteWriteLockTransaction.BeginAsync(_db, cancellationToken);

        var now = _clock.UtcNow;
        var row = await _db.ReceiptScanUsages
            .FirstOrDefaultAsync(candidate => candidate.MonthKey == monthKey, cancellationToken)
            .ConfigureAwait(false);

        if (!ReceiptScanQuotaPolicy.Fits(row?.ScanCount ?? 0, limit))
        {
            // Nothing is written, and nothing is rolled back either: the comparison was the whole
            // of the operation.
            return null;
        }

        if (row is null)
        {
            row = new ReceiptScanUsage
            {
                MonthKey = monthKey,
                ScanCount = 1,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };
            _db.ReceiptScanUsages.Add(row);
        }
        else
        {
            row.ScanCount++;
            row.UpdatedAtUtc = now;
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return row.ScanCount;
    }
}
