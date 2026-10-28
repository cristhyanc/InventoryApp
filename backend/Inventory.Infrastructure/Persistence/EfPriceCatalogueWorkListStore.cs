using Inventory.Application.PriceCatalogue;
using Inventory.Domain.PriceCatalogue;
using Inventory.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="IPriceCatalogueWorkListStore"/> (issue #923, parent #817):
/// the two reads the collector work list of #924 is built from.
///
/// <para><strong>The two mapping reads are the reviewed cross-business read of issue #924, and they
/// are the only <c>IgnoreQueryFilters</c> statements here.</strong> Issue #923 shipped them filtered
/// and recorded that the caller was #924's decision to make; that decision is made. A collector
/// request carries a <em>denied</em> <c>BusinessScope</c>, so the central filter would match none of
/// the mappings and the work list would be silently empty - which is why the statement has to ignore
/// the filter rather than be given a scope. What it is allowed to project is the point:
/// <c>WaitingKey</c>, <c>SearchName</c>, <c>UnitLabel</c> and <c>MasterProductId</c>, none of which
/// says whose product a mapping is about. No <c>BusinessId</c>, no <c>ProductId</c>, no mapping id
/// and no count of how many businesses are waiting on anything crosses this boundary, so what is read
/// across businesses is a product description and nothing else.</para>
///
/// <para>It is bounded by authorization rather than by ownership, exactly as a platform table is: the
/// only endpoint that reaches this adapter is <c>GET /api/price-collector/work</c>, which is class 5
/// of the endpoint authorization classes and reachable only by an app-only token pinned to the
/// configured collector tenant and client id. No member, no Super Admin and no support session can
/// call it. A wider projection, a second <c>IgnoreQueryFilters</c>, an unscoped context or a raw SQL
/// statement here would be a boundary change needing its own reviewed issue - see
/// AGENTS.md § Tenant ownership and data isolation and docs/architecture.md § Collector work
/// list.</para>
///
/// <para><strong>Everything else it reads carries no owner at all.</strong> The supplier master
/// records, the master products, the listings and the match refusals are platform tables, which are
/// not filtered because there is no owner for a filter to compare against.</para>
///
/// <para><strong>The tracked-products read deliberately does not join.</strong> It takes the distinct
/// master product ids the mappings name and then reads those products, rather than joining mappings
/// to listings - which is what makes two businesses tracking one product produce one entry instead of
/// the same listing twice.</para>
///
/// <para><strong>Every variable-size read is bounded, and counted before it is read.</strong> The
/// listings of a product, the proposals under a waiting key and that key's refusals are read for the
/// products one run was issued for and never for the catalogue, and each is counted per product in
/// one grouped statement first - so what comes back is at most the asked-for bound per product, and a
/// product beyond the bound is answered as "beyond the bound" rather than with a prefix. Without the
/// count the read would be the unbounded one the response bound exists to prevent: a single product
/// with ten thousand listings would be materialised in full before anything could decide not to
/// publish it.</para>
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
            // The reviewed cross-business read of issue #924; see the class remarks for what it may
            // project and why that is what makes it safe.
            .IgnoreQueryFilters()
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
        var trackedMasterProductIds = await TrackedMasterProductIdsAsync(cancellationToken);

        if (trackedMasterProductIds.Count == 0)
        {
            return [];
        }

        // By the row key, because the row key is what a bounded run's rotation continues from: the
        // cursor the next run starts after is a stored master product id, so the order products are
        // issued in has to be the order of those ids. It is as deterministic as the comparison key
        // would have been and does not depend on any collation.
        return await _db.MasterProducts
            .AsNoTracking()
            .Where(master => trackedMasterProductIds.Contains(master.Id))
            .OrderBy(master => master.Id)
            .Select(master => new TrackedMasterProduct(
                master.Id,
                master.Brand,
                master.Name,
                master.SizeValue,
                master.SizeUnit))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<BoundedMasterProductListings> ListListingsForMasterProductsAsync(
        IReadOnlyCollection<long> masterProductIds,
        int perProductLimit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(masterProductIds);

        if (masterProductIds.Count == 0 || perProductLimit <= 0)
        {
            return BoundedMasterProductListings.None;
        }

        var ids = masterProductIds.ToList();

        // How many each product has before any of them is read, which is what makes the read itself
        // bounded rather than only the answer: one grouped statement returning one row per product,
        // and then rows for the products that are inside the bound. A product beyond it is answered
        // as "beyond the bound" rather than with a prefix nobody asked for.
        var counts = await _db.SupplierListings
            .AsNoTracking()
            .Where(listing => listing.MasterProductId != null
                && ids.Contains(listing.MasterProductId.Value))
            .GroupBy(listing => listing.MasterProductId!.Value)
            .Select(group => new { MasterProductId = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var beyondLimit = counts
            .Where(entry => entry.Count > perProductLimit)
            .Select(entry => entry.MasterProductId)
            .ToHashSet();

        var withinLimit = counts
            .Where(entry => entry.Count <= perProductLimit)
            .Select(entry => entry.MasterProductId)
            .ToList();

        if (withinLimit.Count == 0)
        {
            return new BoundedMasterProductListings(
                new Dictionary<long, IReadOnlyList<TrackedSupplierListing>>(),
                beyondLimit);
        }

        // One statement for every selected product's listings rather than one per product: a read
        // that went back to the database per master product would be the per-row pattern AGENTS.md
        // refuses for Nayax, for the same reason.
        //
        // One listing per (supplier master record, supplier product id) needs no DISTINCT: that pair
        // is a unique index, and this read names the master products rather than joining through the
        // mappings, so a product five businesses track still has each of its listings once.
        var listings = await _db.SupplierListings
            .AsNoTracking()
            .Where(listing => listing.MasterProductId != null
                && withinLimit.Contains(listing.MasterProductId.Value))
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
                    listing.MatchStatus,
                    listing.LastCheckedAtUtc),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new BoundedMasterProductListings(
            listings
                .GroupBy(entry => entry.MasterProductId)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyList<TrackedSupplierListing>)group
                        .Select(entry => entry.Listing)
                        .ToList()),
            beyondLimit);
    }

    /// <summary>
    /// The distinct master products at least one mapping names, as ids.
    ///
    /// <para>The second half of the same reviewed cross-business read: the projection is one id and
    /// the <c>DISTINCT</c> is what makes five businesses' five mappings one product.</para>
    /// </summary>
    private async Task<List<long>> TrackedMasterProductIdsAsync(CancellationToken cancellationToken) =>
        await _db.ProductPriceTrackings
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(tracking => tracking.MasterProductId != null)
            .Select(tracking => tracking.MasterProductId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<CollectableSupplier>> ListCollectableSuppliersAsync(
        CancellationToken cancellationToken)
    {
        var presets = await _db.SupplierPresets
            .AsNoTracking()
            .OrderBy(preset => preset.Id)
            .Select(preset => new
            {
                preset.Id,
                preset.Name,
                preset.IsEnabled,
                preset.PriceConnector,
                preset.PriceConnectorKey,
                preset.Url,
                preset.PricesIncludeGst,
                preset.PriceCheckFreshnessHours,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // The most recent successful check of any of a supplier's listings, which is what the cadence
        // rule reads. One grouped statement rather than one read per supplier, and a separate one
        // rather than a correlated subquery, so a supplier with no listings at all simply has no
        // entry - which is the state that makes it due.
        var lastCheckedBySupplier = await _db.SupplierListings
            .AsNoTracking()
            .Where(listing => listing.LastCheckedAtUtc != null)
            .GroupBy(listing => listing.SupplierPresetId)
            .Select(group => new
            {
                SupplierPresetId = group.Key,
                LastCheckedAtUtc = group.Max(listing => listing.LastCheckedAtUtc),
            })
            .ToDictionaryAsync(
                entry => entry.SupplierPresetId,
                entry => entry.LastCheckedAtUtc,
                cancellationToken)
            .ConfigureAwait(false);

        return presets.ConvertAll(preset => new CollectableSupplier(
            preset.Id,
            preset.Name,
            preset.IsEnabled,
            preset.PriceConnector,
            preset.PriceConnectorKey,
            preset.Url,
            preset.PricesIncludeGst,
            preset.PriceCheckFreshnessHours,
            lastCheckedBySupplier.TryGetValue(preset.Id, out var lastChecked) ? lastChecked : null));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CatalogueMasterProduct>> ListMasterProductsForMatchingAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        if (limit <= 0)
        {
            return [];
        }

        return await _db.MasterProducts
            .AsNoTracking()
            // By the comparison key and then the row key, for the reason the tracked read is: the
            // order must not depend on the database's collation for a brand or a name, and a
            // catalogue larger than the limit has to yield the same prefix twice.
            .OrderBy(master => master.NormalisedKey)
            .ThenBy(master => master.Id)
            .Take(limit)
            .Select(master => new CatalogueMasterProduct(
                master.Id,
                master.Brand,
                master.Name,
                master.SizeValue,
                master.SizeUnit))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<BoundedWaitingListings> ListWaitingListingsAsync(
        IReadOnlyCollection<string> waitingKeys,
        int perKeyLimit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(waitingKeys);

        if (waitingKeys.Count == 0 || perKeyLimit <= 0)
        {
            return BoundedWaitingListings.None;
        }

        var keys = waitingKeys.ToList();

        // Counted before any of them is read, for the reason the matched products' listings are:
        // what has to be bounded is the read as well as the answer.
        var counts = await _db.SupplierListings
            .AsNoTracking()
            .Where(listing => listing.WaitingKey != null && keys.Contains(listing.WaitingKey))
            .GroupBy(listing => listing.WaitingKey!)
            .Select(group => new { WaitingKey = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var beyondLimit = counts
            .Where(entry => entry.Count > perKeyLimit)
            .Select(entry => entry.WaitingKey)
            .ToHashSet(StringComparer.Ordinal);

        var withinLimit = counts
            .Where(entry => entry.Count <= perKeyLimit)
            .Select(entry => entry.WaitingKey)
            .ToList();

        if (withinLimit.Count == 0)
        {
            return new BoundedWaitingListings(
                new Dictionary<string, IReadOnlyList<TrackedSupplierListing>>(StringComparer.Ordinal),
                beyondLimit);
        }

        var listings = await _db.SupplierListings
            .AsNoTracking()
            .Where(listing => listing.WaitingKey != null && withinLimit.Contains(listing.WaitingKey))
            .OrderBy(listing => listing.SupplierPresetId)
            .ThenBy(listing => listing.SupplierProductId)
            .Select(listing => new
            {
                WaitingKey = listing.WaitingKey!,
                Listing = new TrackedSupplierListing(
                    listing.Id,
                    listing.SupplierPresetId,
                    listing.SupplierProductId,
                    listing.Url,
                    listing.Title,
                    listing.UnitsPerPack,
                    listing.Barcode,
                    listing.BarcodeScope,
                    listing.MatchStatus,
                    listing.LastCheckedAtUtc),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new BoundedWaitingListings(
            listings
                .GroupBy(entry => entry.WaitingKey, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyList<TrackedSupplierListing>)group
                        .Select(entry => entry.Listing)
                        .ToList(),
                    StringComparer.Ordinal),
            beyondLimit);
    }

    /// <inheritdoc />
    public async Task<BoundedRefusedMasterProducts> ListRefusedMasterProductsAsync(
        IReadOnlyCollection<string> waitingKeys,
        int perKeyLimit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(waitingKeys);

        if (waitingKeys.Count == 0 || perKeyLimit <= 0)
        {
            return BoundedRefusedMasterProducts.None;
        }

        var keys = waitingKeys.ToList();

        // Counted as distinct pairs, because that is what a refusal is: one pair refused twice -
        // rejected once and undone once - is one refusal, and counting it twice could put a key
        // beyond the bound that is not.
        var counts = await RefusedSuggestions(keys)
            .GroupBy(suggestion => suggestion.WaitingKey)
            .Select(group => new
            {
                WaitingKey = group.Key,
                Count = group.Select(suggestion => suggestion.MasterProductId).Distinct().Count(),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var beyondLimit = counts
            .Where(entry => entry.Count > perKeyLimit)
            .Select(entry => entry.WaitingKey)
            .ToHashSet(StringComparer.Ordinal);

        var withinLimit = counts
            .Where(entry => entry.Count <= perKeyLimit)
            .Select(entry => entry.WaitingKey)
            .ToList();

        if (withinLimit.Count == 0)
        {
            return new BoundedRefusedMasterProducts([], beyondLimit);
        }

        var refusals = await RefusedSuggestions(withinLimit)
            .Select(suggestion => new RefusedMasterProduct(
                suggestion.WaitingKey,
                suggestion.MasterProductId!.Value))
            // In SQL, because one pair refused twice - rejected once and undone once - is one
            // refusal, and sending it twice would read as two.
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new BoundedRefusedMasterProducts(
            refusals
                .OrderBy(refusal => refusal.WaitingKey, StringComparer.Ordinal)
                .ThenBy(refusal => refusal.MasterProductId)
                .ToList(),
            beyondLimit);
    }

    /// <summary>
    /// The refusals recorded against <paramref name="waitingKeys"/>, as the suggestion rows that
    /// hold them.
    ///
    /// <para>Rejected and undone, and nothing else: those are the two statuses a person decided, and
    /// they are kept forever precisely so a matcher never re-applies a pair somebody refused. A
    /// refusal that proposed a new master product names none, so it has no id to send.</para>
    /// </summary>
    private IQueryable<Models.ProductMatchSuggestion> RefusedSuggestions(List<string> waitingKeys) =>
        _db.ProductMatchSuggestions
            .AsNoTracking()
            .Where(suggestion =>
                waitingKeys.Contains(suggestion.WaitingKey)
                && suggestion.MasterProductId != null
                && (suggestion.Status == ProductMatchSuggestionStatus.Rejected
                    || suggestion.Status == ProductMatchSuggestionStatus.Undone));

    /// <summary>
    /// One waiting mapping's search text exactly as its columns hold it, which is what a SQL
    /// <c>DISTINCT</c> can be taken over. It becomes a <see cref="ProductAwaitingMatch"/> only after
    /// the Domain's fold has grouped it.
    /// </summary>
    private sealed record WaitingSearchText(string WaitingKey, string SearchName, string UnitLabel);
}
