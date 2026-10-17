using Inventory.Application.Providers;
using Inventory.Application.Time;
using Inventory.Domain.Providers;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="IProviderDataCoverageStore"/> (issue #730, parent #329
/// decision P9). It lives in Inventory.Infrastructure with the <see cref="AppDbContext"/> and the
/// <see cref="ProviderDataCoverage"/> model it writes, like every other adapter behind an
/// Application persistence port (issue #309).
///
/// <para>The write is one append and one save, with no read first: a coverage record is a
/// historical statement about one run, never a status that an earlier row has to be reconciled
/// with. The owning business is stamped by <c>BusinessOwnershipEnforcer</c> on save, so there is no
/// business predicate or assignment here, by design (AGENTS.md § Tenant ownership and data
/// isolation).</para>
///
/// <para><see cref="ProviderDataCoverage.RecordedAtUtc"/> is taken from the clock here rather than
/// from the caller, so every source records provenance the same way and no use case can report a
/// coverage range as having been retrieved at a time of its own choosing.</para>
/// </summary>
public sealed class EfProviderDataCoverageStore : IProviderDataCoverageStore
{
    private readonly AppDbContext _db;
    private readonly IClock _clock;

    public EfProviderDataCoverageStore(AppDbContext db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    /// <inheritdoc />
    public async Task RecordAsync(
        ExternalProvider provider,
        ProviderDataSource source,
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken cancellationToken)
    {
        // An empty or inverted range accounts for no instant at all. Storing it would add a row
        // that every reader has to know to ignore, so the port refuses it here instead.
        if (toUtc <= fromUtc)
            return;

        _db.ProviderDataCoverages.Add(new ProviderDataCoverage
        {
            Provider = provider,
            Source = source,
            FromUtc = fromUtc,
            ToUtc = toUtc,
            RecordedAtUtc = _clock.UtcNow,
        });

        await _db.SaveChangesAsync(cancellationToken);
    }
}
