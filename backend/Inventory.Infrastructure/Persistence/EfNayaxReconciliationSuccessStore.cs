using Inventory.Application.NayaxReconciliation;
using Inventory.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="INayaxReconciliationSuccessStore"/> (issue #1104): it
/// stamps "this business's hourly reconciliation succeeded in full" on the business's own Nayax SQS
/// sender row.
///
/// <para><strong>It is an ordinary tenant-owned update, and that is the point.</strong> It is
/// <see cref="EfNayaxSqsSenderActivityStore"/>'s shape exactly: it runs inside the service scope
/// resolved to the one business being reconciled, so the central query filter is what finds the row
/// and <see cref="BusinessOwnershipEnforcer"/> is what checks the write. There is no
/// <c>IgnoreQueryFilters</c>, no business id parameter and no write lease - a business id nobody
/// passed in cannot be the wrong one.</para>
///
/// <para><strong>One column, and nothing else about the mapping.</strong> It never writes the sender
/// id, the enabled flag, the last-message instant or the revision - those belong to the Super Admin's
/// surface (issue #1101) and to the queue reader (issue #1103) - and it never creates a row: a sender
/// mapping exists because a platform administrator recorded it, and a reconciliation inventing one
/// would be a business attributing its own sales.</para>
/// </summary>
public sealed class EfNayaxReconciliationSuccessStore : INayaxReconciliationSuccessStore
{
    private readonly AppDbContext _db;

    public EfNayaxReconciliationSuccessStore(AppDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc />
    public async Task RecordSucceededAsync(DateTime runStartedAtUtc, CancellationToken cancellationToken)
    {
        // Tracked, because the update goes through SaveChanges - where the ownership enforcer and
        // every other central enforcer runs. A set-based ExecuteUpdate would write straight past all
        // of them.
        var sender = await _db.BusinessNayaxSqsSenders
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (sender is null)
        {
            // Nothing to stamp and deliberately nothing to create. Unreachable in practice: a
            // business is reconciled because the eligibility read found its enabled sender row.
            return;
        }

        sender.LastReconciliationSucceededAtUtc = runStartedAtUtc;

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
