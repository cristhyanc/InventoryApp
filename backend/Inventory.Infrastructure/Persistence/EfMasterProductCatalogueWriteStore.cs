using Inventory.Application.PriceCatalogue;
using Inventory.Domain.Exceptions;
using Inventory.Domain.PriceCatalogue;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="IMasterProductCatalogueWriteStore"/> (issue #1024, parent
/// #817): adding one master product to the shared catalogue, and correcting the brand, name or size of
/// one that exists.
///
/// <para><strong>One table written, one read, and nothing else at all.</strong>
/// <c>MasterProducts</c> is written - this and <see cref="EfPriceCatalogueReviewStore"/> are its two
/// writers, and there is no third. <c>SupplierListings</c> is read for exactly one question: how many
/// of this product's listings are confirmed, which is what decides whether its size may change. No
/// observation, suggestion or collector row is touched, and <strong>nothing tenant-owned is read or
/// written</strong>: no <c>Product</c>, no <c>ProductPriceTracking</c>, no <c>Business</c>. So a
/// business's own product names, sizes, stock, costs, purchases and mappings are exactly as they were
/// after a create or an edit, and there is no <c>IgnoreQueryFilters</c> statement here and no write
/// lease. A platform-admin request carries a <em>denied</em> <c>BusinessScope</c>, and the write
/// passes <c>BusinessOwnershipEnforcer</c> because <see cref="MasterProduct"/> is not
/// <c>IBusinessOwned</c> - not because anything was relaxed. See AGENTS.md § Tenant ownership and data
/// isolation and docs/architecture.md § Super Admin master products.</para>
///
/// <para><strong>An edit updates the row, and that is the whole of why its key survives.</strong>
/// Correcting a description is an <c>UPDATE</c> of the row every tracking mapping, supplier listing,
/// match suggestion and collector work item already names - never an insert and a copy - so none of
/// those rows is rewritten, re-pointed or re-decided by a correction.</para>
///
/// <para><strong>Each call is one <c>BEGIN IMMEDIATE</c> transaction, and the write lock is taken
/// before the reads</strong> (<see cref="SqliteWriteLockTransaction"/>, the shape every decision of
/// #928 uses). All three questions this adapter asks - does another row already describe this, has
/// this row changed since the page read it, is any listing of it confirmed - are comparisons against
/// the state a person was looking at, and they are authoritative only while nothing can commit
/// between the question and the write. The confirmed-listing count in particular is read <em>inside</em>
/// the lock the Supplier Price Review's own confirmation takes, so a listing confirmed while an
/// incompatible size edit was being prepared refuses that edit rather than being silently invalidated
/// by it.</para>
///
/// <para><strong>Uniqueness is the database's, and this adapter only reports it.</strong> The unique
/// index over <c>MasterProduct.NormalisedKey</c> is the rule; the pre-check inside the transaction
/// turns the ordinary case into the documented conflict, and
/// <see cref="IsDuplicateNormalisedKey"/> recognises that one violation and nothing else - so any
/// other database failure stays a database failure instead of being reported to a caller as a
/// duplicate, and no SQLite message reaches the response.</para>
///
/// <para><strong>Every rule is the Domain's.</strong> <see cref="MasterProductPolicy"/> decided what
/// may be stored before the description reached here, <see cref="MasterProductEditPolicy"/> decides
/// whether an edit is current and whether its size may change, and
/// <see cref="ProductMatchReviewPolicy.DuplicateMasterProductMessage"/> is what a duplicate is told -
/// the same sentence the review's own create answers with, because it is the same rule.</para>
/// </summary>
public sealed class EfMasterProductCatalogueWriteStore : IMasterProductCatalogueWriteStore
{
    /// <summary>SQLITE_CONSTRAINT, the primary result code a unique-index violation reports.</summary>
    private const int SqliteConstraintViolation = 19;

    /// <summary>
    /// What SQLite names in the message of the one constraint this adapter translates: it reports the
    /// offending column rather than the index, and this table has exactly one unique index, so naming
    /// the column is unambiguous here.
    /// </summary>
    private const string NormalisedKeyIndexColumn = "MasterProducts.NormalisedKey";

    private readonly AppDbContext _db;

    public EfMasterProductCatalogueWriteStore(AppDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc />
    public async Task<CataloguedMasterProduct> CreateAsync(
        NormalisedMasterProduct description,
        DateTime asOfUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(description);

        await using var transaction = await SqliteWriteLockTransaction
            .BeginAsync(_db, cancellationToken)
            .ConfigureAwait(false);

        await RefuseDuplicateAsync(description.NormalisedKey, exceptId: null, cancellationToken)
            .ConfigureAwait(false);

        var created = new MasterProduct
        {
            Brand = description.Brand,
            Name = description.Name,
            SizeValue = description.SizeValue,
            SizeUnit = description.SizeUnit,
            NormalisedKey = description.NormalisedKey,
            CreatedAtUtc = asOfUtc,
            // Equal to the creation instant until the first edit, so "never edited" is readable
            // without a nullable column every reader would have to interpret.
            UpdatedAtUtc = asOfUtc,
        };

        _db.MasterProducts.Add(created);

        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        // Four zeros rather than an absence: a product nothing has listed yet is in the catalogue,
        // and "nothing has been found at a supplier yet" is an answer. A create reaches no supplier
        // listing at all, so this needs no count statement to be true.
        return Describe(created, new MasterProductListingCounts(0, 0, 0, 0));
    }

    /// <inheritdoc />
    public async Task<CataloguedMasterProduct?> UpdateAsync(
        long masterProductId,
        MasterProductEdit edit,
        DateTime asOfUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(edit);

        await using var transaction = await SqliteWriteLockTransaction
            .BeginAsync(_db, cancellationToken)
            .ConfigureAwait(false);

        var stored = await _db.MasterProducts
            .SingleOrDefaultAsync(master => master.Id == masterProductId, cancellationToken)
            .ConfigureAwait(false);

        if (stored is null)
        {
            return null;
        }

        // The expected-value comparison, inside the lock: the row as it is now against the row the
        // editing page read. A mismatch is somebody else's correction, and overwriting it silently
        // on a row shared by every business is the failure this refusal exists to prevent.
        MasterProductEditPolicy.RequireCurrent(edit.ExpectedUpdatedAtUtc, stored.UpdatedAtUtc);

        var description = edit.Description;
        var counts = await CountsAsync(masterProductId, cancellationToken).ConfigureAwait(false);

        // The financial safeguard, also inside the lock. Both halves are read here rather than
        // handed in: whether the size moved is a comparison with the stored row, and whether any
        // listing is confirmed is a question only this transaction can answer authoritatively.
        MasterProductEditPolicy.RequireSizeMayChange(
            MasterProductEditPolicy.IsSizeChange(
                stored.SizeValue,
                stored.SizeUnit,
                description.SizeValue,
                description.SizeUnit),
            counts.Confirmed);

        // Its own key is not a duplicate of itself: an edit that corrects only the capitalisation of
        // a brand, or nothing at all, saves the key it already carries.
        await RefuseDuplicateAsync(description.NormalisedKey, masterProductId, cancellationToken)
            .ConfigureAwait(false);

        stored.Brand = description.Brand;
        stored.Name = description.Name;
        stored.SizeValue = description.SizeValue;
        stored.SizeUnit = description.SizeUnit;
        stored.NormalisedKey = description.NormalisedKey;
        stored.UpdatedAtUtc = asOfUtc;

        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return Describe(stored, counts);
    }

    /// <summary>
    /// Refuses a description another master product already carries the key of, inside the caller's
    /// own <c>BEGIN IMMEDIATE</c> transaction so the answer cannot be overtaken.
    ///
    /// <para><paramref name="exceptId"/> is the row being edited, excluded because a row is not a
    /// duplicate of itself - which is the issue's own rule that saving an unchanged key is allowed.
    /// </para>
    /// </summary>
    private async Task RefuseDuplicateAsync(
        string normalisedKey,
        long? exceptId,
        CancellationToken cancellationToken)
    {
        var carryingTheKey = _db.MasterProducts
            .Where(master => master.NormalisedKey == normalisedKey);

        // Written as its own Where rather than as `Id != exceptId` in the predicate above: with no
        // row to exclude the comparison would be against a null parameter, which is a question about
        // SQL null semantics and not about this rule.
        if (exceptId is { } id)
        {
            carryingTheKey = carryingTheKey.Where(master => master.Id != id);
        }

        if (await carryingTheKey.AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new DomainConflictException(ProductMatchReviewPolicy.DuplicateMasterProductMessage);
        }
    }

    /// <summary>
    /// How many supplier listings this master product has in each match status, counted by the
    /// database in one statement.
    ///
    /// <para>The confirmed count is what the size safeguard is decided on; the other three are
    /// published beside it so the answer this surface returns is the same record its listing read
    /// returns, including the refused listings - a refusal is kept rather than deleted.</para>
    /// </summary>
    private async Task<MasterProductListingCounts> CountsAsync(
        long masterProductId,
        CancellationToken cancellationToken)
    {
        var counted = await _db.SupplierListings
            .AsNoTracking()
            .Where(listing => listing.MasterProductId == masterProductId)
            .GroupBy(listing => listing.MatchStatus)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var byStatus = counted.ToDictionary(row => row.Status, row => row.Count);

        return new MasterProductListingCounts(
            byStatus.GetValueOrDefault(SupplierListingMatchStatus.Proposed),
            byStatus.GetValueOrDefault(SupplierListingMatchStatus.Confirmed),
            byStatus.GetValueOrDefault(SupplierListingMatchStatus.NeedsReview),
            byStatus.GetValueOrDefault(SupplierListingMatchStatus.Rejected));
    }

    /// <summary>
    /// Saves, translating the one constraint this surface owns into the conflict it documents and
    /// leaving every other database failure alone.
    /// </summary>
    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException failure) when (IsDuplicateNormalisedKey(failure))
        {
            // The index's own answer, reported as the pre-check's: two concurrent writes of one
            // description end with one row and one documented 409 rather than a 500 that names a
            // database object.
            throw new DomainConflictException(ProductMatchReviewPolicy.DuplicateMasterProductMessage);
        }
    }

    private static bool IsDuplicateNormalisedKey(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqliteException sqlite
                && sqlite.SqliteErrorCode == SqliteConstraintViolation
                && sqlite.Message.Contains(NormalisedKeyIndexColumn, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// One stored row as the catalogue reports it. The normalised key is deliberately not published,
    /// for the reason the listing read does not publish it: whether two descriptions are the same
    /// product is the database's answer and not a decision any client makes.
    /// </summary>
    private static CataloguedMasterProduct Describe(
        MasterProduct master,
        MasterProductListingCounts counts) =>
        new(
            master.Id,
            master.Brand,
            master.Name,
            master.SizeValue,
            master.SizeUnit,
            master.CreatedAtUtc,
            master.UpdatedAtUtc,
            counts);
}
