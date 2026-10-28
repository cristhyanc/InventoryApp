using System.Linq.Expressions;

using Inventory.Application.PriceCatalogue;
using Inventory.Domain.PriceCatalogue;
using Inventory.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="IProductPriceTrackingStore"/> (issue #923, parent #817).
///
/// <para><strong>An ordinary tenant-owned adapter, and deliberately nothing more.</strong> Every
/// statement here goes through the filtered <c>DbSet</c>, so a read sees the caller's business and
/// nothing else; the insert leaves <c>BusinessId</c> unset and <c>BusinessOwnershipEnforcer</c> stamps
/// it from the resolved scope, which is why no method on the port takes an owner. There is no
/// <c>IgnoreQueryFilters</c> here, no raw SQL, no unscoped context and no hand-written
/// <c>WHERE BusinessId = …</c> - the central mechanism is the boundary, and adding a local filter
/// beside it would make the real one look optional (AGENTS.md § Tenant ownership and data
/// isolation).</para>
///
/// <para><strong>Two things the database decides rather than this adapter.</strong> A mapping's
/// product is a foreign key on <c>(BusinessId, ProductId)</c>, so a product of another business is
/// refused by the constraint rather than by a check here; and one mapping per product is the unique
/// index on <c>(BusinessId, ProductId)</c>, because a read-then-insert is a check two concurrent
/// callers can both pass.</para>
/// </summary>
public sealed class EfProductPriceTrackingStore : IProductPriceTrackingStore
{
    /// <summary>
    /// The one projection every read uses, so a column added to the entity cannot reach one read and
    /// not another. It deliberately omits <c>BusinessId</c>: the owner is resolved from the
    /// authenticated actor and is not a value any caller of this port reads.
    /// </summary>
    private static readonly Expression<Func<Models.ProductPriceTracking, ProductPriceTrackingRecord>> Projection =
        tracking => new ProductPriceTrackingRecord(
            tracking.Id,
            tracking.ProductId,
            tracking.MasterProductId,
            tracking.Status,
            tracking.MatchSource,
            tracking.WaitingKey,
            tracking.SearchName,
            tracking.UnitLabel,
            tracking.CreatedAtUtc,
            tracking.UpdatedAtUtc);

    private readonly AppDbContext _db;

    public EfProductPriceTrackingStore(AppDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc />
    public async Task<ProductPriceTrackingRecord> AddAsync(
        NewProductPriceTracking tracking,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tracking);

        var row = new Models.ProductPriceTracking
        {
            // BusinessId is deliberately absent: BusinessOwnershipEnforcer stamps it from the
            // resolved scope on SaveChanges, and a denied scope is refused there rather than here.
            ProductId = tracking.ProductId,
            // A new mapping is a request and nothing more. Which master product the product is gets
            // decided by the matcher (#941) or the Super Admin (#928), never by the business asking
            // for a price - which is why there is no master product or match source to carry.
            Status = ProductPriceTrackingStatus.Requested,
            MasterProductId = null,
            MatchSource = null,
            WaitingKey = tracking.WaitingKey,
            SearchName = tracking.SearchName,
            UnitLabel = tracking.UnitLabel,
            CreatedAtUtc = tracking.CreatedAtUtc,
            UpdatedAtUtc = tracking.CreatedAtUtc,
        };

        _db.ProductPriceTrackings.Add(row);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return ToRecord(row);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ProductPriceTrackingRecord>> ListAsync(
        CancellationToken cancellationToken) =>
        await _db.ProductPriceTrackings
            .AsNoTracking()
            // By the search text and then the row key, so the order does not depend on the database's
            // collation for a product name and two mappings whose text matches cannot swap places
            // between reads.
            .OrderBy(tracking => tracking.SearchName)
            .ThenBy(tracking => tracking.Id)
            .Select(Projection)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public Task<ProductPriceTrackingRecord?> FindAsync(long id, CancellationToken cancellationToken) =>
        _db.ProductPriceTrackings
            .AsNoTracking()
            .Where(tracking => tracking.Id == id)
            .Select(Projection)
            .SingleOrDefaultAsync(cancellationToken);

    /// <summary>
    /// The tracked entity a write just saved, as the same record a read projects, so the two paths
    /// cannot report one row differently.
    /// </summary>
    private static ProductPriceTrackingRecord ToRecord(Models.ProductPriceTracking row) =>
        new(
            row.Id,
            row.ProductId,
            row.MasterProductId,
            row.Status,
            row.MatchSource,
            row.WaitingKey,
            row.SearchName,
            row.UnitLabel,
            row.CreatedAtUtc,
            row.UpdatedAtUtc);
}
