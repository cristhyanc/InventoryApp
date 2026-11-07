using Inventory.Application.NayaxSqs;
using Inventory.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="INayaxSqsLiveStatusStore"/> (issue #1106): it reads the
/// current business's own Nayax SQS sender row for the three stored facts the Dashboard's live
/// decision needs.
///
/// <para><strong>It is an ordinary tenant-owned read, and that is the point.</strong> It is
/// <see cref="EfNayaxSqsSenderActivityStore"/>'s and
/// <see cref="EfNayaxReconciliationSuccessStore"/>'s shape with the write taken away: it runs inside
/// the service scope the request resolved to the caller's own business, so the central query filter
/// is what finds the row. There is no <c>IgnoreQueryFilters</c>, no business id parameter, no
/// caller-supplied predicate, page or sort and no write lease - a business id nobody passed in cannot
/// be the wrong one - so this is not one of the reviewed filter-ignoring paths and must never become
/// one.</para>
///
/// <para><strong>The projection carries no sender id, deliberately.</strong> Which AWS IAM user sends
/// a business's sales is the Super Admin's record and is readable on no member-facing surface (issue
/// #1101, AGENTS.md § Nayax integration), so what crosses this boundary is <em>whether</em> one is
/// recorded, the enabled flag, and the two observability instants the response reports. Not the id,
/// not the revision and not the row's own instants. A business that cannot learn its own sender id
/// cannot claim another business's sales by recording it.</para>
///
/// <para>It writes nothing at all: the last-message instant is the queue reader's column (issue
/// #1103) and the reconciliation instant is the hourly run's (issue #1104).</para>
/// </summary>
public sealed class EfNayaxSqsLiveStatusStore : INayaxSqsLiveStatusStore
{
    private readonly AppDbContext _db;

    public EfNayaxSqsLiveStatusStore(AppDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc />
    public Task<NayaxSqsLiveSenderStatus?> FindCurrentAsync(CancellationToken cancellationToken) =>
        _db.BusinessNayaxSqsSenders
            .AsNoTracking()
            .Select(row => new NayaxSqsLiveSenderStatus(
                // Whether an id is recorded, never the id. A row a "Remove" cleared answers false,
                // exactly as a business with no row at all does.
                row.AwsSenderId != null,
                row.IsEnabled,
                row.LastMessageReceivedAtUtc,
                row.LastReconciliationSucceededAtUtc))
            .SingleOrDefaultAsync(cancellationToken);
}
