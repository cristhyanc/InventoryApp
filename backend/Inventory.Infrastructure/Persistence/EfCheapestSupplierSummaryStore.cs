using Inventory.Application.PriceCatalogue;
using Inventory.Domain.PriceCatalogue;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="ICheapestSupplierSummaryStore"/> (issue #930, parent
/// #817): the one read behind the Cheapest supplier column and its popup.
///
/// <para><strong>Five statements, and each one is bounded by the one before it.</strong> The
/// business's own GST registration (by primary key, because <c>Businesses</c> carries no tenant
/// filter of its own, exactly as <see cref="EfAvailableSupplierPresetStore"/> reads it); the
/// caller's own linked, not-hidden suppliers; the caller's own products among the ids asked for;
/// those products' own price-tracking mappings; and finally the shared catalogue's listings - only
/// of the master products those mappings name, and only from the supplier records the caller holds
/// a copy of. There is no statement here that a caller could point at a master product, a listing
/// or a supplier record of its own choosing.</para>
///
/// <para><strong>The tenant boundary is the central one.</strong> The products, the mappings and the
/// suppliers go through the filtered <c>DbSet</c>s, so a product id of another business matches
/// nothing and is absent from the answer; there is no <c>IgnoreQueryFilters</c> here, no raw SQL, no
/// unscoped context and no hand-written <c>WHERE BusinessId = …</c>. The catalogue tables are
/// platform tables and carry no owner to filter on - which is exactly why the way in has to be one
/// of this business's own matched mappings, and is (AGENTS.md § Tenant ownership and data
/// isolation).</para>
///
/// <para><strong>Nothing it reads identifies another business, and nothing it reads is a cost.</strong>
/// A listing and an observation are facts about a supplier's shelf; there is no count of who else
/// tracks a master product and nowhere for one to travel. An observed price is purchasing guidance
/// and never becomes a purchase, a unit cost, an inventory valuation, a GST accounting figure or a
/// line of any report (AGENTS.md § Observed supplier prices).</para>
///
/// <para><strong>It compares nothing.</strong> Every figure here is stored exactly as it was
/// published - a pack price, a basis, a rate, a pack size - and the per-unit arithmetic, the GST
/// conversion and the choice of cheapest are <c>CheapestUnitOfferPolicy</c>'s alone.</para>
/// </summary>
public sealed class EfCheapestSupplierSummaryStore : ICheapestSupplierSummaryStore
{
    private readonly AppDbContext _db;

    public EfCheapestSupplierSummaryStore(AppDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc />
    public async Task<CheapestSupplierFacts> ReadAsync(
        BusinessId businessId,
        IReadOnlyList<long> productIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(productIds);

        var ids = productIds.ToList();

        // A business that has recorded no answer is treated as not registered, which shows it the
        // price it actually hands over. Inventing "registered" would show every price a tenth low.
        var gstRegistered = await _db.Businesses
            .AsNoTracking()
            .Where(business => business.Id == businessId.Value)
            .Select(business => business.GstRegistered)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false) ?? false;

        // The business's own comparison suppliers: a linked copy of a platform supplier record
        // (issue #934) that it has not switched out of use. A supplier it typed in itself has no
        // catalogue listings to compare, and a hidden one is a supplier it has said it does not buy
        // from - neither takes part, and neither appears.
        var suppliers = await _db.Suppliers
            .AsNoTracking()
            .Where(supplier => supplier.SupplierPresetId != null && !supplier.IsHidden)
            .OrderBy(supplier => supplier.Name)
            .ThenBy(supplier => supplier.Id)
            .Select(supplier => new LinkedSupplier(
                supplier.Id,
                supplier.Name,
                supplier.SupplierPresetId!.Value))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var products = await _db.Products
            .AsNoTracking()
            .Where(product => ids.Contains(product.Id))
            .Select(product => new ProductRow(product.Id))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var mappings = await _db.ProductPriceTrackings
            .AsNoTracking()
            .Where(tracking => ids.Contains(tracking.ProductId))
            .Select(tracking => new MappingRow(
                tracking.ProductId,
                tracking.Status,
                tracking.MasterProductId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var mappingsByProduct = mappings.ToDictionary(mapping => mapping.ProductId);

        var listingsByMasterProduct = await ListingsAsync(
            MatchedMasterProductIds(mappings),
            [.. suppliers.Select(supplier => supplier.SupplierPresetId)],
            cancellationToken).ConfigureAwait(false);

        var known = products.ToDictionary(product => product.ProductId);

        // In the order they were asked for, and only the ones that are this business's: an id of
        // another business is simply not here.
        var answered = ids
            .Where(known.ContainsKey)
            .Select(id => Product(known[id], mappingsByProduct, listingsByMasterProduct))
            .ToList();

        return new CheapestSupplierFacts(gstRegistered, suppliers, answered);
    }

    /// <summary>
    /// The master products the caller's own matched mappings name. A mapping that is waiting, not
    /// found or suspended names none - <c>ProductPriceTrackingPolicy.IsMatched</c> is the one rule -
    /// so nothing it could point at is read.
    /// </summary>
    private static List<long> MatchedMasterProductIds(IReadOnlyList<MappingRow> mappings) =>
        [.. mappings
            .Where(mapping => ProductPriceTrackingPolicy.IsMatched(mapping.Status))
            .Select(mapping => mapping.MasterProductId)
            .OfType<long>()
            .Distinct()];

    /// <summary>
    /// The listings of those master products published by the supplier records the business holds a
    /// copy of, each with the latest accepted observation of its price when it has one.
    ///
    /// <para>The observation is a correlated read of the listing's own <c>LatestObservationId</c>
    /// cursor rather than a scan of the append-only table: a listing points at the one observation
    /// that is current, and a failed or missing check never clears that pointer (issue #925). The
    /// freshness limit comes from the supplier master record the listing belongs to, because the
    /// limit is per supplier and is stored rather than decided by code that names a supplier.</para>
    /// </summary>
    private async Task<ILookup<long, ListingRow>> ListingsAsync(
        List<long> masterProductIds,
        List<int> supplierPresetIds,
        CancellationToken cancellationToken)
    {
        if (masterProductIds.Count == 0 || supplierPresetIds.Count == 0)
        {
            return Array.Empty<ListingRow>().ToLookup(listing => listing.MasterProductId);
        }

        var listings = await _db.SupplierListings
            .AsNoTracking()
            .Where(listing => listing.MasterProductId != null
                && masterProductIds.Contains(listing.MasterProductId.Value)
                && supplierPresetIds.Contains(listing.SupplierPresetId))
            .Select(listing => new ListingRow(
                listing.Id,
                listing.MasterProductId!.Value,
                listing.SupplierPresetId,
                listing.MatchStatus,
                listing.UnitsPerPack,
                listing.Url,
                listing.LastCheckedAtUtc,
                listing.SupplierPreset!.PriceCheckFreshnessHours,
                _db.SupplierPriceObservations
                    .Where(observation => observation.Id == listing.LatestObservationId)
                    .Select(observation => new ObservationRow(
                        observation.PackPrice,
                        observation.PriceBasis,
                        observation.SupplierGstRate,
                        observation.IsPromotion,
                        observation.PromotionEndsAtUtc,
                        observation.StockStatus,
                        observation.ObservedAtUtc))
                    .FirstOrDefault()))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return listings.ToLookup(listing => listing.MasterProductId);
    }

    private static TrackedProductListings Product(
        ProductRow product,
        IReadOnlyDictionary<long, MappingRow> mappingsByProduct,
        ILookup<long, ListingRow> listingsByMasterProduct)
    {
        var mapping = mappingsByProduct.GetValueOrDefault(product.ProductId);

        var listings = mapping?.MasterProductId is { } masterProductId
            && ProductPriceTrackingPolicy.IsMatched(mapping.Status)
            ? listingsByMasterProduct[masterProductId].Select(Listing).ToList()
            : [];

        return new TrackedProductListings(
            product.ProductId,
            mapping?.Status,
            listings);
    }

    private static TrackedListingPrice Listing(ListingRow listing) =>
        new(
            listing.SupplierListingId,
            listing.SupplierPresetId,
            // Confirmed and nothing else: a proposed or flagged listing may be a different size or
            // another product entirely, and the comparison reports that as its own reason rather
            // than pricing a product from somebody's guess.
            listing.MatchStatus == SupplierListingMatchStatus.Confirmed,
            listing.UnitsPerPack,
            listing.Url,
            listing.LastCheckedAtUtc,
            listing.Observation is { } observation
                ? new ObservedPackPrice(
                    observation.PackPrice,
                    observation.PriceBasis,
                    observation.SupplierGstRate,
                    observation.IsPromotion,
                    observation.PromotionEndsAtUtc,
                    observation.StockStatus,
                    observation.ObservedAtUtc,
                    listing.PriceCheckFreshnessHours)
                : null);

    /// <summary>One of the caller's products, as a SQL projection can produce it.</summary>
    private sealed record ProductRow(long ProductId);

    /// <summary>One of the caller's own price-tracking mappings.</summary>
    private sealed record MappingRow(
        long ProductId,
        ProductPriceTrackingStatus Status,
        long? MasterProductId);

    /// <summary>One supplier listing with its supplier's freshness limit and its current price.</summary>
    private sealed record ListingRow(
        long SupplierListingId,
        long MasterProductId,
        int SupplierPresetId,
        SupplierListingMatchStatus MatchStatus,
        int? UnitsPerPack,
        string? Url,
        DateTime? LastCheckedAtUtc,
        int PriceCheckFreshnessHours,
        ObservationRow? Observation);

    /// <summary>The latest accepted observation of one listing's price, exactly as it is stored.</summary>
    private sealed record ObservationRow(
        decimal PackPrice,
        SupplierPriceBasis PriceBasis,
        decimal? SupplierGstRate,
        bool IsPromotion,
        DateTime? PromotionEndsAtUtc,
        SupplierStockStatus StockStatus,
        DateTime ObservedAtUtc);
}
