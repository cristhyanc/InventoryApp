using Inventory.Application.PriceCatalogue;
using Inventory.Domain.PriceCatalogue;
using Inventory.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="IPriceCatalogueWorkListStore"/> (issue #923, parent #817):
/// the two reads the collector work list of #924 is built from.
///
/// <para><strong>It widens no boundary.</strong> The mappings are read through the ordinary filtered
/// <c>DbSet</c> - there is no <c>IgnoreQueryFilters</c> here, no raw SQL, no unscoped context and no
/// second cross-business read path - so what this adapter deduplicates is whatever rows the context it
/// was given may already see. The three catalogue tables it joins to are platform tables, which carry
/// no owner and are not filtered because there is no owner for a filter to compare against. Which
/// caller may read the work list across businesses, and how it authenticates, is #924's own reviewed
/// decision; this issue adds no such caller.</para>
///
/// <para><strong>Nothing a business owns leaves through it.</strong> The projections carry a product
/// description, a waiting key and a supplier's listing - never a business id, a product id, a
/// mapping id or a count of how many businesses are waiting on something. That is issue #923's third
/// known constraint written into the queries themselves.</para>
///
/// <para><strong>The tracked-products read deliberately does not join.</strong> It takes the distinct
/// master product ids the mappings name and then reads those products and their listings, rather than
/// joining mappings to listings - which is what makes two businesses tracking one product produce one
/// entry instead of the same listing twice.</para>
/// </summary>
public sealed class EfPriceCatalogueWorkListStore : IPriceCatalogueWorkListStore
{
    private readonly AppDbContext _db;

    public EfPriceCatalogueWorkListStore(AppDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ProductAwaitingMatch>> ListProductsAwaitingMatchAsync(
        CancellationToken cancellationToken)
    {
        // Requested and nothing else: NotFound is a recorded answer, and re-sending it every run is
        // exactly what recording it prevents (IPriceCatalogueWorkListStore states the rule).
        //
        // DISTINCT in SQL over the three columns, so rows that agree exactly - the common case, five
        // businesses holding the same product name - collapse before they are materialised.
        var waiting = await _db.ProductPriceTrackings
            .AsNoTracking()
            .Where(tracking => tracking.Status == ProductPriceTrackingStatus.Requested)
            .Select(tracking => new WaitingSearchText(
                tracking.WaitingKey,
                tracking.SearchName,
                tracking.UnitLabel))
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // And the fold itself in memory, because it is the Domain's rule rather than the database's:
        // two spellings that differ only in case or spacing are one search, and SQLite's collations
        // could not be asked that question without a second answer to it existing. The representative
        // spelling is the ordinally first one, so two reads of the work list send the same text.
        return waiting
            .GroupBy(text => text.WaitingKey, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group =>
            {
                var representative = group
                    .OrderBy(text => text.SearchName, StringComparer.Ordinal)
                    .ThenBy(text => text.UnitLabel, StringComparer.Ordinal)
                    .First();

                return new ProductAwaitingMatch(
                    group.Key,
                    representative.SearchName,
                    representative.UnitLabel);
            })
            .ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TrackedMasterProduct>> ListTrackedMasterProductsAsync(
        CancellationToken cancellationToken)
    {
        var trackedMasterProductIds = await _db.ProductPriceTrackings
            .AsNoTracking()
            .Where(tracking => tracking.MasterProductId != null)
            .Select(tracking => tracking.MasterProductId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (trackedMasterProductIds.Count == 0)
        {
            return [];
        }

        var masterProducts = await _db.MasterProducts
            .AsNoTracking()
            .Where(master => trackedMasterProductIds.Contains(master.Id))
            // By the comparison key and then the row key, so the order does not depend on the
            // database's collation for a brand or a name.
            .OrderBy(master => master.NormalisedKey)
            .ThenBy(master => master.Id)
            .Select(master => new
            {
                master.Id,
                master.Brand,
                master.Name,
                master.SizeValue,
                master.SizeUnit,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // One statement for every tracked product's listings rather than one per product: a read that
        // went back to the database per master product would be the per-row pattern AGENTS.md refuses
        // for Nayax, for the same reason.
        //
        // One listing per (supplier master record, supplier product id) needs no DISTINCT: that pair
        // is a unique index, and this read names the master products rather than joining through the
        // mappings, so a product five businesses track still has each of its listings once.
        var listings = await _db.SupplierListings
            .AsNoTracking()
            .Where(listing => listing.MasterProductId != null
                && trackedMasterProductIds.Contains(listing.MasterProductId.Value))
            .OrderBy(listing => listing.SupplierPresetId)
            .ThenBy(listing => listing.SupplierProductId)
            .Select(listing => new
            {
                MasterProductId = listing.MasterProductId!.Value,
                Listing = new TrackedSupplierListing(
                    listing.Id,
                    listing.SupplierPresetId,
                    listing.SupplierProductId,
                    listing.Url,
                    listing.Title,
                    listing.UnitsPerPack,
                    listing.Barcode,
                    listing.BarcodeScope,
                    listing.MatchStatus),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var listingsByMasterProduct = listings
            .GroupBy(entry => entry.MasterProductId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<TrackedSupplierListing>)group
                    .Select(entry => entry.Listing)
                    .ToList());

        return masterProducts.ConvertAll(master => new TrackedMasterProduct(
            master.Id,
            master.Brand,
            master.Name,
            master.SizeValue,
            master.SizeUnit,
            listingsByMasterProduct.TryGetValue(master.Id, out var own) ? own : []));
    }

    /// <summary>
    /// One waiting mapping's search text exactly as its columns hold it, which is what a SQL
    /// <c>DISTINCT</c> can be taken over. It becomes a <see cref="ProductAwaitingMatch"/> only after
    /// the Domain's fold has grouped it.
    /// </summary>
    private sealed record WaitingSearchText(string WaitingKey, string SearchName, string UnitLabel);
}
