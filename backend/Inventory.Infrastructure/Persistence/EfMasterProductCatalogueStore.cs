using System.Text;
using Inventory.Application.PriceCatalogue;
using Inventory.Domain.PriceCatalogue;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="IMasterProductCatalogueStore"/> (issue #1023, parent
/// #817): the two reads the Super Admin Master products page is built from.
///
/// <para><strong>It is read-only, and there is nothing here that writes.</strong> No
/// <c>SaveChanges</c>, no transaction and no write lease: a master product is still written by exactly
/// one path in this application, the Super Admin review of issue #928
/// (<see cref="EfPriceCatalogueReviewStore"/>), and creating, editing or deleting one from this
/// surface is a separate issue.</para>
///
/// <para><strong>Four platform tables, and no tenant-owned one at all.</strong>
/// <c>MasterProducts</c>, <c>SupplierListings</c>, the <c>SupplierPresets</c> that publish those
/// listings and the <c>SupplierPriceObservations</c> row a listing points at for its current price.
/// Every one of them is about a product or a supplier's shelf and about no business, so none carries
/// a <c>BusinessId</c> or a query filter - which is why this adapter needs <strong>no</strong>
/// <c>IgnoreQueryFilters</c> statement, unlike the review store beside it. A platform-admin request
/// carries a <em>denied</em> <c>BusinessScope</c>, and under it the central filters still match none
/// of anybody's products, mappings or businesses: this adapter never reads one, and there is nowhere
/// in either answer for a business id, a business name or a business's product id to travel. No
/// mapping is read and no count of businesses tracking a product exists here (the owner's
/// clarification of 31 October 2026). See AGENTS.md § Tenant ownership and data isolation and
/// docs/architecture.md § Super Admin master products.</para>
///
/// <para><strong>The order is fixed and ends in the row's own key</strong> - brand, name, size, id for
/// the catalogue, and the supplier's name, the supplier's own product id and the row's key for one
/// product's listings - because a page that is read a slice at a time has to come out in the same
/// order on every request. No caller chooses a sort, a predicate or a column list; the only things it
/// may narrow by are a search term and the bounded slice
/// <see cref="MasterProductCataloguePaging"/> already resolved.</para>
///
/// <para><strong>It compares nothing and derives nothing.</strong> A listing's current price is the
/// pack price of the one accepted observation the listing points at, published with the currency, the
/// tax basis and the instant it was stored with; the per-unit arithmetic and the GST conversion are
/// <c>CheapestUnitOfferPolicy</c>'s alone, on its own endpoint. An observed price is purchasing
/// guidance and never becomes a purchase, a unit cost, an inventory valuation, a GST accounting figure
/// or a line of any report (AGENTS.md § Observed supplier prices).</para>
/// </summary>
public sealed class EfMasterProductCatalogueStore : IMasterProductCatalogueStore
{
    /// <summary>
    /// The character <see cref="LikePattern"/> writes before a <c>%</c>, <c>_</c> or itself, so a
    /// character <c>LIKE</c> treats specially is matched literally when a person types it into the
    /// search box rather than acting as a wildcard. It is <see cref="EfProductCatalogStore"/>'s rule,
    /// because it is the same question asked of a different catalogue.
    /// </summary>
    private const char SearchEscapeCharacter = '\\';

    private readonly AppDbContext _db;

    public EfMasterProductCatalogueStore(AppDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc />
    public async Task<MasterProductCatalogueResult> ListAsync(
        MasterProductCatalogueFilter filter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var matching = Matching(_db.MasterProducts.AsNoTracking(), filter.Search);

        // The whole match, not the page: the count is what tells the page there is more to read.
        var total = await matching.CountAsync(cancellationToken).ConfigureAwait(false);

        var products = await matching
            .OrderBy(master => master.Brand)
            .ThenBy(master => master.Name)
            .ThenBy(master => master.SizeValue)
            .ThenBy(master => master.Id)
            .Skip(filter.Skip)
            .Take(filter.Take)
            .Select(master => new ProductRow(
                master.Id,
                master.Brand,
                master.Name,
                master.SizeValue,
                master.SizeUnit,
                master.CreatedAtUtc,
                master.UpdatedAtUtc))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var counts = await CountsAsync(
                products.ConvertAll(product => product.Id),
                cancellationToken)
            .ConfigureAwait(false);

        return new MasterProductCatalogueResult(
            products.ConvertAll(product => Describe(product, counts)),
            total);
    }

    /// <inheritdoc />
    public async Task<MasterProductDetail?> FindAsync(
        long masterProductId,
        CancellationToken cancellationToken)
    {
        var product = await _db.MasterProducts
            .AsNoTracking()
            .Where(master => master.Id == masterProductId)
            .Select(master => new ProductRow(
                master.Id,
                master.Brand,
                master.Name,
                master.SizeValue,
                master.SizeUnit,
                master.CreatedAtUtc,
                master.UpdatedAtUtc))
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (product is null)
        {
            return null;
        }

        var counts = await CountsAsync([masterProductId], cancellationToken).ConfigureAwait(false);

        var listings = await _db.SupplierListings
            .AsNoTracking()
            .Where(listing => listing.MasterProductId == masterProductId)
            .OrderBy(listing => listing.SupplierPreset!.Name)
            .ThenBy(listing => listing.SupplierProductId)
            .ThenBy(listing => listing.Id)
            .Select(listing => new ListingRow(
                listing.Id,
                listing.SupplierPreset!.Name,
                listing.SupplierProductId,
                listing.Title,
                listing.SizeValue,
                listing.SizeUnit,
                listing.UnitsPerPack,
                listing.MatchStatus,
                listing.Url,
                listing.LastCheckedAtUtc,
                // The listing's own cursor into the append-only observations, read correlated rather
                // than as a scan of that table: a listing points at the one reading that is current,
                // and a failed or missing check never clears that pointer (issue #925).
                _db.SupplierPriceObservations
                    .Where(observation => observation.Id == listing.LatestObservationId)
                    .Select(observation => new ObservationRow(
                        observation.PackPrice,
                        observation.Currency,
                        observation.PriceBasis,
                        observation.ObservedAtUtc))
                    .FirstOrDefault()))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new MasterProductDetail(
            Describe(product, counts),
            listings.ConvertAll(Listing));
    }

    /// <summary>
    /// The master products whose brand or name contains <paramref name="search"/>, or all of them
    /// when nothing was typed.
    ///
    /// <para><c>LIKE</c> rather than <c>Contains</c>, which is <see cref="EfProductCatalogStore"/>'s
    /// reason exactly (issue #827): EF Core's SQLite provider translates <c>Contains</c> to
    /// <c>instr(…)</c>, which compares byte-for-byte, so a search typed in a different case than the
    /// stored brand or name would find nothing. SQLite's <c>LIKE</c> folds ASCII letter case by
    /// default - though it leaves non-ASCII letters unfolded - and the escape character keeps a
    /// literal <c>%</c> or <c>_</c> a person types from acting as a wildcard.</para>
    /// </summary>
    private static IQueryable<MasterProduct> Matching(IQueryable<MasterProduct> products, string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return products;
        }

        var pattern = $"%{LikePattern(search)}%";
        var escapeCharacter = SearchEscapeCharacter.ToString();

        return products.Where(master =>
            EF.Functions.Like(master.Brand, pattern, escapeCharacter)
                || EF.Functions.Like(master.Name, pattern, escapeCharacter));
    }

    /// <summary>
    /// <paramref name="value"/> with every character <c>LIKE</c> treats specially - <c>%</c>, <c>_</c>
    /// and <see cref="SearchEscapeCharacter"/> itself - preceded by the escape character.
    /// </summary>
    private static string LikePattern(string value)
    {
        var pattern = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (character is '%' or '_' or SearchEscapeCharacter)
            {
                pattern.Append(SearchEscapeCharacter);
            }

            pattern.Append(character);
        }

        return pattern.ToString();
    }

    /// <summary>
    /// How many supplier listings each of <paramref name="masterProductIds"/> has in each match
    /// status, counted by the database in one statement.
    ///
    /// <para>A listing that is still held under a waiting key belongs to no master product and is
    /// counted against none, which is #923's exclusive-ownership rule showing through the count. A
    /// refused listing <em>is</em> counted: a refusal is kept rather than deleted, and a product with
    /// nothing but refusals must not read as a product nobody has ever looked up.</para>
    /// </summary>
    private async Task<IReadOnlyDictionary<long, MasterProductListingCounts>> CountsAsync(
        List<long> masterProductIds,
        CancellationToken cancellationToken)
    {
        if (masterProductIds.Count == 0)
        {
            return new Dictionary<long, MasterProductListingCounts>();
        }

        var counted = await _db.SupplierListings
            .AsNoTracking()
            .Where(listing => listing.MasterProductId != null
                && masterProductIds.Contains(listing.MasterProductId.Value))
            .GroupBy(listing => new { listing.MasterProductId, listing.MatchStatus })
            .Select(group => new StatusCount(
                group.Key.MasterProductId!.Value,
                group.Key.MatchStatus,
                group.Count()))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return counted
            .GroupBy(row => row.MasterProductId)
            .ToDictionary(group => group.Key, Counts);
    }

    private static MasterProductListingCounts Counts(IEnumerable<StatusCount> rows)
    {
        var byStatus = rows.ToDictionary(row => row.MatchStatus, row => row.Count);

        return new MasterProductListingCounts(
            byStatus.GetValueOrDefault(SupplierListingMatchStatus.Proposed),
            byStatus.GetValueOrDefault(SupplierListingMatchStatus.Confirmed),
            byStatus.GetValueOrDefault(SupplierListingMatchStatus.NeedsReview),
            byStatus.GetValueOrDefault(SupplierListingMatchStatus.Rejected));
    }

    private static CataloguedMasterProduct Describe(
        ProductRow product,
        IReadOnlyDictionary<long, MasterProductListingCounts> counts) =>
        new(
            product.Id,
            product.Brand,
            product.Name,
            product.SizeValue,
            product.SizeUnit,
            product.CreatedAtUtc,
            product.UpdatedAtUtc,
            // No listings at all is four zeros rather than an absence: the product is in the
            // catalogue either way, and "nothing has been found at a supplier yet" is an answer.
            counts.GetValueOrDefault(product.Id) ?? new MasterProductListingCounts(0, 0, 0, 0));

    private static CataloguedSupplierListing Listing(ListingRow listing) =>
        new(
            listing.Id,
            listing.SupplierName,
            listing.SupplierProductId,
            listing.Title,
            listing.SizeValue,
            listing.SizeUnit,
            listing.UnitsPerPack,
            listing.MatchStatus,
            listing.Url,
            listing.LastCheckedAtUtc,
            listing.Observation is { } observation
                ? new CataloguedObservedPrice(
                    observation.PackPrice,
                    observation.Currency,
                    observation.PriceBasis,
                    observation.ObservedAtUtc)
                : null);

    /// <summary>One master product, as a SQL projection can produce it.</summary>
    private sealed record ProductRow(
        long Id,
        string Brand,
        string Name,
        decimal SizeValue,
        MasterProductSizeUnit SizeUnit,
        DateTime CreatedAtUtc,
        DateTime UpdatedAtUtc);

    /// <summary>One supplier listing of that product, with the price it currently publishes.</summary>
    private sealed record ListingRow(
        long Id,
        string SupplierName,
        string SupplierProductId,
        string? Title,
        decimal? SizeValue,
        MasterProductSizeUnit? SizeUnit,
        int? UnitsPerPack,
        SupplierListingMatchStatus MatchStatus,
        string? Url,
        DateTime? LastCheckedAtUtc,
        ObservationRow? Observation);

    /// <summary>The accepted observation a listing points at, exactly as it is stored.</summary>
    private sealed record ObservationRow(
        decimal PackPrice,
        string Currency,
        SupplierPriceBasis PriceBasis,
        DateTime ObservedAtUtc);

    /// <summary>How many listings one master product has in one match status.</summary>
    private sealed record StatusCount(
        long MasterProductId,
        SupplierListingMatchStatus MatchStatus,
        int Count);
}
