using Inventory.Application.NayaxSqs;
using Inventory.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="INayaxSqsSenderActivityStore"/> (issue #1103): it stamps
/// "a message arrived just now" on the current business's own Nayax SQS sender row.
///
/// <para><strong>It is an ordinary tenant-owned update, and that is the point.</strong> Unlike
/// <see cref="EfNayaxSqsSenderDirectory"/> beside it, this adapter runs inside the service scope
/// resolved to the one business a message was attributed to, so the central query filter is what
/// finds the row and <see cref="BusinessOwnershipEnforcer"/> is what checks the write. There is no
/// <c>IgnoreQueryFilters</c>, no business id parameter and no write lease: a business id nobody
/// passed in cannot be the wrong one.</para>
///
/// <para><strong>One column, and nothing else about the mapping.</strong> It never writes the sender
/// id, the enabled flag or the revision - those are the Super Admin's surface alone (issue #1101) -
/// and it never creates a row: a sender mapping exists because a platform administrator recorded it,
/// and a reader inventing one would be a business attributing its own sales.</para>
/// </summary>
public sealed class EfNayaxSqsSenderActivityStore : INayaxSqsSenderActivityStore
{
    private readonly AppDbContext _db;

    public EfNayaxSqsSenderActivityStore(AppDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc />
    public async Task RecordMessageReceivedAsync(
        DateTime receivedAtUtc, CancellationToken cancellationToken)
    {
        // Tracked, because the update goes through SaveChanges - where the ownership enforcer and
        // every other central enforcer runs. A set-based ExecuteUpdate would write straight past all
        // of them.
        var sender = await _db.BusinessNayaxSqsSenders
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (sender is null)
        {
            // Nothing to stamp and deliberately nothing to create. Unreachable in practice: the
            // directory found a row to attribute the message by.
            return;
        }

        sender.LastMessageReceivedAtUtc = receivedAtUtc;

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
