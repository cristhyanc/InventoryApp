using System.Linq.Expressions;

using Inventory.Application.PriceCatalogue;
using Inventory.Domain.PriceCatalogue;
using Inventory.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="IProductPriceTrackingStore"/> (issue #923, parent #817),
/// with the switch's two writes and its status read added by issue #938.
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
/// <para><strong>The one platform table it reads is <c>MasterProducts</c>, and it reads two columns
/// of it.</strong> A matched mapping's status is meaningless without saying what the product was
/// matched to, so <see cref="FindForProductAsync"/> projects the matched master product's name and
/// size - and nothing else, under issue #938's own reviewed limit. It is reached through the
/// mapping's own navigation, so a master product no mapping of this business names is unreachable;
/// there is no method here that takes a master product id. Nothing is ever written to that table, or
/// to the supplier listings or match suggestions beside it.</para>
///
/// <para><strong><see cref="TrackAsync"/> is one atomic operation, and that is what makes it
/// authoritative.</strong> It opens the <c>BEGIN IMMEDIATE</c> transaction of
/// <see cref="SqliteWriteLockTransaction"/> - so the write lock is held <em>before</em> it reads the
/// product's name and unit label and the existing mapping - and inserts inside the same transaction,
/// exactly as <see cref="EfAvailableSupplierPresetStore.CopyAsync"/> does (issue #935). Nothing can
/// commit in that window, so a rename, a unit-label edit or another tab's switch either lands before
/// the lock and is seen, or waits behind it. A deferred transaction would not be enough: it takes the
/// write lock at the first write, which is after the reads the capture was made from - and a captured
/// name that is no longer the product's name is a search for something else.</para>
///
/// <para><strong>Two things the database decides rather than this adapter.</strong> A mapping's
/// product is a foreign key on <c>(BusinessId, ProductId)</c>, so a product of another business could
/// not be stored even if the read above had somehow found one; and one mapping per product is the
/// unique index on <c>(BusinessId, ProductId)</c>, which is the backstop under the in-lock read for a
/// writer this process did not serialise with - <see cref="IsDuplicateMapping"/> recognises exactly
/// that violation and nothing else.</para>
/// </summary>
public sealed class EfProductPriceTrackingStore : IProductPriceTrackingStore
{
    /// <summary>SQLITE_CONSTRAINT, the primary result code a unique-index violation reports.</summary>
    private const int SqliteConstraintViolation = 19;

    /// <summary>
    /// What SQLite names in the message of the one constraint this adapter translates: it reports the
    /// offending columns rather than the index. Both are named, because only this pair means "this
    /// business already tracks that product".
    /// </summary>
    private const string MappingIndexColumns =
        "ProductPriceTrackings.BusinessId, ProductPriceTrackings.ProductId";

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
    public async Task<ProductPriceTrackingWrite> TrackAsync(
        long productId,
        DateTime requestedAtUtc,
        CancellationToken cancellationToken)
    {
        // BEGIN IMMEDIATE: the write lock is taken here, before the two reads the mapping is decided
        // and captured from, so neither can be overtaken by a commit before the insert.
        await using var transaction = await SqliteWriteLockTransaction
            .BeginAsync(_db, cancellationToken)
            .ConfigureAwait(false);

        // Read rather than taken from the caller: the search text is the product's own name and unit
        // label (the owner's decision of 27 October 2026), so nothing a request carried decides what
        // is stored - and whether that label may be tracked is decided here rather than before, which
        // is what makes the answer authoritative.
        var product = await _db.Products
            .AsNoTracking()
            .Where(candidate => candidate.Id == productId)
            .Select(candidate => new { candidate.Name, candidate.Unit })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (product is null)
        {
            return ProductPriceTrackingWrite.ProductNotFound();
        }

        if (ProductPriceTrackingPolicy.TrackingRefusal(product.Name, product.Unit) is { } refusal)
        {
            return ProductPriceTrackingWrite.Refused(refusal);
        }

        if (await _db.ProductPriceTrackings
            .AnyAsync(tracking => tracking.ProductId == productId, cancellationToken)
            .ConfigureAwait(false))
        {
            return ProductPriceTrackingWrite.AlreadyTracked();
        }

        var captured = ProductPriceTrackingPolicy.Capture(product.Name, product.Unit);

        var row = new Models.ProductPriceTracking
        {
            // BusinessId is deliberately absent: BusinessOwnershipEnforcer stamps it from the
            // resolved scope on SaveChanges, and a denied scope is refused there rather than here.
            ProductId = productId,
            // A new mapping is a request and nothing more. Which master product the product is gets
            // decided by the matcher (#941) or the Super Admin (#928), never by the business asking
            // for a price - which is why there is no master product or match source to carry.
            Status = ProductPriceTrackingStatus.Requested,
            MasterProductId = null,
            MatchSource = null,
            WaitingKey = captured.WaitingKey,
            SearchName = captured.SearchName,
            UnitLabel = captured.UnitLabel,
            CreatedAtUtc = requestedAtUtc,
            UpdatedAtUtc = requestedAtUtc,
        };

        _db.ProductPriceTrackings.Add(row);

        try
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Detached before the exception leaves, so a caller that translates the index violation
            // is not left with a failed insert tracked on the context that the next SaveChanges
            // would retry.
            _db.Entry(row).State = EntityState.Detached;
            throw;
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return ProductPriceTrackingWrite.Tracked();
    }

    /// <inheritdoc />
    public async Task<ProductPriceTrackingWrite> UntrackAsync(
        long productId,
        CancellationToken cancellationToken)
    {
        await using var transaction = await SqliteWriteLockTransaction
            .BeginAsync(_db, cancellationToken)
            .ConfigureAwait(false);

        // The product is looked up so that "no such product" and "that product is not tracked" stay
        // different answers: a client asking about a product that is not this business's must be told
        // the product is not there, not that its tracking is off.
        if (!await _db.Products
            .AnyAsync(candidate => candidate.Id == productId, cancellationToken)
            .ConfigureAwait(false))
        {
            return ProductPriceTrackingWrite.ProductNotFound();
        }

        var mapping = await _db.ProductPriceTrackings
            .SingleOrDefaultAsync(tracking => tracking.ProductId == productId, cancellationToken)
            .ConfigureAwait(false);

        if (mapping is null)
        {
            return ProductPriceTrackingWrite.NotTracked();
        }

        // One row of one tenant-owned table, and nothing else: the master product this mapping may
        // have named, the supplier listings under it and every other business's mapping are all left
        // exactly as they are (issue #938's sixth criterion). The reference to the master product
        // restricts rather than cascades, so removing a mapping cannot reach it even in principle.
        _db.ProductPriceTrackings.Remove(mapping);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return ProductPriceTrackingWrite.Removed();
    }

    /// <inheritdoc />
    public async Task<ProductPriceTrackingView?> FindForProductAsync(
        long productId,
        CancellationToken cancellationToken)
    {
        // The product first, so "no such product in this business" and "that product is not tracked"
        // stay different answers. Two statements rather than one: this is a read, so there is nothing
        // for a transaction to make atomic, and one shape each is what keeps both translatable.
        var product = await _db.Products
            .AsNoTracking()
            .Where(candidate => candidate.Id == productId)
            .Select(candidate => new { candidate.Id, candidate.Name, candidate.Unit })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (product is null)
        {
            return null;
        }

        var mapping = await _db.ProductPriceTrackings
            .AsNoTracking()
            .Where(tracking => tracking.ProductId == productId)
            .Select(tracking => new MappedProduct(
                tracking.Id,
                tracking.ProductId,
                tracking.MasterProductId,
                tracking.Status,
                tracking.MatchSource,
                tracking.WaitingKey,
                tracking.SearchName,
                tracking.UnitLabel,
                tracking.CreatedAtUtc,
                tracking.UpdatedAtUtc,
                // Two columns of one platform table, projected beside the mapping that names it:
                // issue #938's own reviewed limit on what a business endpoint publishes about the
                // shared catalogue. The brand, the row key, the normalised key and the instants are
                // deliberately not here, and neither is anything about a supplier listing.
                tracking.MasterProduct == null ? null : tracking.MasterProduct.Name,
                tracking.MasterProduct == null ? null : (decimal?)tracking.MasterProduct.SizeValue,
                tracking.MasterProduct == null
                    ? null
                    : (MasterProductSizeUnit?)tracking.MasterProduct.SizeUnit))
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return new ProductPriceTrackingView(
            product.Id,
            product.Name,
            product.Unit,
            mapping?.ToRecord(),
            mapping?.ToMasterProduct());
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

    /// <inheritdoc />
    public bool IsDuplicateMapping(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqliteException sqlite
                && sqlite.SqliteErrorCode == SqliteConstraintViolation
                && sqlite.Message.Contains(MappingIndexColumns, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// One mapping with the two columns of the matched master product beside it, as a SQL projection
    /// can produce them: flat, with each master-product value nullable because a mapping that is
    /// still waiting names none.
    ///
    /// <para>The master product is published only while
    /// <c>ProductPriceTrackingPolicy.IsMatched</c> says the mapping is matched, which is asked here in
    /// memory rather than in the <c>WHERE</c> clause: the consistency rule that a mapping names a
    /// master product in exactly the two matched states is the Domain's, so a stored row that somehow
    /// broke it is reported as unmatched rather than as a match with a description.</para>
    /// </summary>
    private sealed record MappedProduct(
        long Id,
        long ProductId,
        long? MasterProductId,
        ProductPriceTrackingStatus Status,
        ProductPriceTrackingMatchSource? MatchSource,
        string WaitingKey,
        string SearchName,
        string UnitLabel,
        DateTime CreatedAtUtc,
        DateTime UpdatedAtUtc,
        string? MasterProductName,
        decimal? MasterProductSizeValue,
        MasterProductSizeUnit? MasterProductSizeUnit)
    {
        public ProductPriceTrackingRecord ToRecord() =>
            new(
                Id,
                ProductId,
                MasterProductId,
                Status,
                MatchSource,
                WaitingKey,
                SearchName,
                UnitLabel,
                CreatedAtUtc,
                UpdatedAtUtc);

        public MatchedMasterProduct? ToMasterProduct() =>
            ProductPriceTrackingPolicy.IsMatched(Status)
                && MasterProductName is not null
                && MasterProductSizeValue is { } size
                && MasterProductSizeUnit is { } unit
                ? new MatchedMasterProduct(MasterProductName, size, unit)
                : null;
    }
}
