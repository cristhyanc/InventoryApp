using Inventory.Application.Time;
using Inventory.Domain.PriceCatalogue;

namespace Inventory.Application.PriceCatalogue;

/// <summary>
/// Issues one collector work list (issue #924, parent #817): decides which suppliers are due, reads
/// the deduplicated products to price, stores a run with one work item per product-and-supplier pair,
/// and answers the list the collector works from.
///
/// <para><strong>The ordering is the deduplication.</strong> The products come from the #923 port,
/// which returns one entry per master product however many businesses track it and one entry per
/// waiting key however many businesses' spellings folded to it. This use case adds no second
/// grouping rule: it pairs those products with the due suppliers and nothing else.</para>
///
/// <para><strong>Every pair is a work item, and no product is half-issued.</strong> A product is added
/// to the list only when there is budget for a work item against every due supplier, so a result row
/// can always cite the work item it belongs to. When the budget runs out the list says so
/// (<c>WorkItemLimitReached</c>) instead of returning a silent prefix - a truncated work list nobody
/// is told about reads as "this is everything there is to search", which is the one thing a bound must
/// not do.</para>
///
/// <para><strong>And a bounded run continues where the last one stopped.</strong> The products form
/// one rotation - matched products by master product id, then waiting products by waiting key - and
/// this run starts at the product after the one the most recently issued run stopped at, wrapping
/// around to the beginning. A catalogue larger than one run's budget is therefore searched in full
/// over successive runs, which is the parent issue's requirement; re-issuing the same prefix every
/// run would leave everything past it never searched at all. The cursor is the last work item of the
/// newest run (<see cref="IPriceCollectorRunStore.LastIssuedWorkAsync"/>), so the schedule is derived
/// from the work that was actually issued rather than from a counter that could disagree with it.</para>
///
/// <para><strong>What travels under each product is bounded too.</strong> The known listings of a
/// matched product, the proposals under a waiting key and that key's refusals are read only for the
/// products this run is issued for, each bounded per product
/// (<see cref="PriceCollectorWorkListPolicy.MaxListingsPerProduct"/>,
/// <see cref="PriceCollectorWorkListPolicy.MaxRefusedMasterProductsPerWaitingProduct"/>): the
/// work-item budget bounds how many products a run carries and says nothing about how much travels
/// under each, so one product with ten thousand listings would otherwise be an unbounded response
/// that honoured every stated limit. A product beyond a bound is published with its work, without
/// that context, and <em>says</em> which context it is without - and a waiting key whose refusals are
/// beyond the bound is published with matching suppressed, because a partial refusal list is a
/// safeguard that would let a rejected pair be proposed again.</para>
///
/// <para><strong>The master products to match against are sent only while something is waiting</strong>
/// (the owner's decision of 27 October 2026, for the automatic matching of #941). A run with nothing
/// waiting sends an empty list rather than the whole catalogue, because the only thing that list is
/// for is matching a name nobody has matched yet.</para>
///
/// <para><strong>Nothing a business owns leaves here.</strong> No business id, business name, business
/// product id or price is in any of these contracts, and the use case never reads one: what it has is
/// a product description, a waiting key, a supplier master record and a listing. See
/// <see cref="IPriceCatalogueWorkListStore"/> for why that is what makes the cross-business read
/// safe.</para>
/// </summary>
public sealed class IssuePriceCollectorWorkList
{
    private readonly IPriceCatalogueWorkListStore _catalogue;
    private readonly IPriceCollectorRunStore _runs;
    private readonly IPriceCollectorRunSettings _settings;
    private readonly IClock _clock;
    private readonly int _workItemBudget;

    /// <summary>
    /// Composes the use case. <c>workItemBudget</c> is how much work one run may be issued with and
    /// defaults to the reviewed maximum; it exists so a test can state the truncation behaviour over
    /// four products instead of two thousand, and it can only ever <em>narrow</em> the bound, because
    /// <see cref="PriceCollectorWorkListPolicy.BudgetFor"/> clamps whatever arrives to
    /// <see cref="PriceCollectorWorkListPolicy.MaxWorkItemsPerRun"/> - so no caller, the composition
    /// root included, can widen the response past it.
    /// </summary>
    public IssuePriceCollectorWorkList(
        IPriceCatalogueWorkListStore catalogue,
        IPriceCollectorRunStore runs,
        IPriceCollectorRunSettings settings,
        IClock clock,
        int workItemBudget = PriceCollectorWorkListPolicy.MaxWorkItemsPerRun)
    {
        _catalogue = catalogue;
        _runs = runs;
        _settings = settings;
        _clock = clock;
        _workItemBudget = workItemBudget;
    }

    public async Task<PriceCollectorWorkList> Handle(CancellationToken cancellationToken)
    {
        var issuedAtUtc = _clock.UtcNow;

        var suppliers = await DueSuppliersAsync(cancellationToken);
        var matched = await _catalogue.ListTrackedMasterProductsAsync(cancellationToken);
        var waiting = await _catalogue.ListProductsAwaitingMatchAsync(cancellationToken);
        var continueAfter = await _runs.LastIssuedWorkAsync(cancellationToken);

        var plan = Plan(suppliers, matched, waiting, _workItemBudget, continueAfter);

        var issued = await _runs.IssueAsync(
            new NewPriceCollectorRun(
                issuedAtUtc,
                PriceCollectorRunPolicy.ExpiresAt(issuedAtUtc, _settings.RunExpiryHours),
                plan.WorkItems),
            cancellationToken);

        var handles = HandlesByProduct(issued);

        var includedMatchedIds = plan.MatchedProducts
            .Select(product => product.Id)
            .ToList();

        var includedWaitingKeys = plan.WaitingProducts
            .Select(product => product.WaitingKey)
            .ToList();

        // The context of the products this run was issued for, and of no others: bounded per product,
        // and read after the plan rather than before it, so neither the read nor the response grows
        // with a catalogue the run is not searching.
        var knownListings = includedMatchedIds.Count == 0
            ? BoundedMasterProductListings.None
            : await _catalogue.ListListingsForMasterProductsAsync(
                includedMatchedIds,
                PriceCollectorWorkListPolicy.MaxListingsPerProduct,
                cancellationToken);

        var proposedListings = includedWaitingKeys.Count == 0
            ? BoundedWaitingListings.None
            : await _catalogue.ListWaitingListingsAsync(
                includedWaitingKeys,
                PriceCollectorWorkListPolicy.MaxListingsPerProduct,
                cancellationToken);

        var refusals = includedWaitingKeys.Count == 0
            ? BoundedRefusedMasterProducts.None
            : await _catalogue.ListRefusedMasterProductsAsync(
                includedWaitingKeys,
                PriceCollectorWorkListPolicy.MaxRefusedMasterProductsPerWaitingProduct,
                cancellationToken);

        // Only while something is actually waiting: the matching list is the vocabulary a name is
        // matched against, and a run with nothing waiting has nothing to match.
        IReadOnlyList<CatalogueMasterProduct> masterProductsForMatching =
            includedWaitingKeys.Count == 0
                ? []
                : await _catalogue.ListMasterProductsForMatchingAsync(
                    PriceCollectorWorkListPolicy.MaxMasterProductsForMatching,
                    cancellationToken);

        return new PriceCollectorWorkList(
            issued.RunId,
            issued.IssuedAtUtc,
            issued.ExpiresAtUtc,
            [.. suppliers.Select(supplier => new PriceCollectorSupplier(
                supplier.SupplierPresetId,
                supplier.Name,
                supplier.PriceConnector!.Value,
                supplier.PriceConnectorKey,
                supplier.SiteUrl,
                supplier.PricesIncludeGst))],
            [.. plan.MatchedProducts.Select(product => new PriceCollectorMatchedProduct(
                product.Id,
                product.Brand,
                product.Name,
                product.SizeValue,
                product.SizeUnit,
                knownListings.ByMasterProduct.TryGetValue(product.Id, out var own)
                    ? [.. own.Select(KnownListing)]
                    : [],
                knownListings.MasterProductsBeyondLimit.Contains(product.Id),
                handles.MatchedProducts.TryGetValue(product.Id, out var ownWork) ? ownWork : []))],
            [.. plan.WaitingProducts.Select(product => new PriceCollectorWaitingProduct(
                product.WaitingKey,
                product.SearchName,
                product.UnitLabel,
                proposedListings.ByWaitingKey.TryGetValue(product.WaitingKey, out var proposals)
                    ? [.. proposals.Select(KnownListing)]
                    : [],
                proposedListings.WaitingKeysBeyondLimit.Contains(product.WaitingKey),
                [.. refusals.Refusals
                    .Where(refusal => string.Equals(
                        refusal.WaitingKey, product.WaitingKey, StringComparison.Ordinal))
                    .Select(refusal => refusal.MasterProductId)],
                refusals.WaitingKeysBeyondLimit.Contains(product.WaitingKey),
                handles.WaitingProducts.TryGetValue(product.WaitingKey, out var waitingOwn)
                    ? waitingOwn
                    : []))],
            [.. masterProductsForMatching.Select(master => new PriceCollectorMasterProduct(
                master.Id,
                master.Brand,
                master.Name,
                master.SizeValue,
                master.SizeUnit))],
            plan.WorkItemLimitReached);
    }

    /// <summary>
    /// The suppliers searched on this run: every supplier master record
    /// <see cref="PriceCollectorWorkListPolicy.IsSupplierDue"/> says is collectable, whenever it was
    /// last checked, in their own key order
    /// so two reads of the work list agree about which supplier a work item belongs to.
    /// </summary>
    private async Task<List<CollectableSupplier>> DueSuppliersAsync(
        CancellationToken cancellationToken)
    {
        var suppliers = await _catalogue.ListCollectableSuppliersAsync(cancellationToken);

        return [.. suppliers
            .Where(supplier => PriceCollectorWorkListPolicy.IsSupplierDue(
                supplier.IsEnabled,
                supplier.PriceConnector is not null))
            .OrderBy(supplier => supplier.SupplierPresetId)];
    }

    /// <summary>
    /// Which products this run is issued for, and the work items that pairs them with the due
    /// suppliers.
    ///
    /// <para>One rotation - matched products by master product id, then waiting products by waiting
    /// key, each in the order the #923 port returned them - walked from the product after
    /// <paramref name="continueAfter"/> and wrapping around to the start. A product is included only
    /// when the whole of its work fits, which is what keeps every published product answerable, and
    /// the walk stops at the first product that does not fit so the run is one contiguous stretch of
    /// the rotation rather than whichever products happened to be small enough.</para>
    ///
    /// <para>That is what makes the work-item bound a schedule: the next run reads the product this
    /// one stopped at as its own cursor, so an oversized catalogue is issued stretch by stretch and
    /// every tracked product is eventually searched.</para>
    /// </summary>
    private static WorkPlan Plan(
        IReadOnlyList<CollectableSupplier> dueSuppliers,
        IReadOnlyList<TrackedMasterProduct> matched,
        IReadOnlyList<ProductAwaitingMatch> waiting,
        int workItemBudget,
        PriceCollectorWorkCursor? continueAfter)
    {
        List<TrackedMasterProduct> plannedMatched = [];
        List<ProductAwaitingMatch> plannedWaiting = [];
        List<NewPriceCollectorWorkItem> workItems = [];

        List<Candidate> rotation =
        [
            .. matched.Select(product => new Candidate(product, null)),
            .. waiting.Select(product => new Candidate(null, product)),
        ];

        if (dueSuppliers.Count == 0 || rotation.Count == 0)
        {
            // Nothing is due, or nothing is tracked, so there is no pair to issue and no product to
            // publish. That is an answer, not a truncation: the collector has nothing to do on this
            // run.
            return new WorkPlan(plannedMatched, plannedWaiting, workItems, WorkItemLimitReached: false);
        }

        var budget = PriceCollectorWorkListPolicy.BudgetFor(workItemBudget);
        var start = StartOf(rotation, continueAfter);

        for (var offset = 0; offset < rotation.Count; offset++)
        {
            if (workItems.Count + dueSuppliers.Count > budget)
            {
                break;
            }

            var candidate = rotation[(start + offset) % rotation.Count];

            if (candidate.Matched is { } matchedProduct)
            {
                plannedMatched.Add(matchedProduct);
                workItems.AddRange(dueSuppliers.Select(supplier => new NewPriceCollectorWorkItem(
                    supplier.SupplierPresetId,
                    matchedProduct.Id,
                    WaitingKey: null)));
            }
            else if (candidate.Waiting is { } waitingProduct)
            {
                plannedWaiting.Add(waitingProduct);
                workItems.AddRange(dueSuppliers.Select(supplier => new NewPriceCollectorWorkItem(
                    supplier.SupplierPresetId,
                    MasterProductId: null,
                    waitingProduct.WaitingKey)));
            }
        }

        return new WorkPlan(
            plannedMatched,
            plannedWaiting,
            workItems,
            WorkItemLimitReached: plannedMatched.Count + plannedWaiting.Count < rotation.Count);
    }

    /// <summary>
    /// Where in the rotation this run starts: the first product after the one the most recently
    /// issued run stopped at, and the beginning of the rotation when there is no such product -
    /// either because no run has ever been issued or because the previous run stopped at the end.
    /// </summary>
    private static int StartOf(List<Candidate> rotation, PriceCollectorWorkCursor? continueAfter)
    {
        if (continueAfter is null)
        {
            return 0;
        }

        for (var index = 0; index < rotation.Count; index++)
        {
            var comesAfter = rotation[index] switch
            {
                { Matched: { } product } => PriceCollectorWorkListPolicy.MatchedProductComesAfter(
                    product.Id,
                    continueAfter.MasterProductId,
                    continueAfter.WaitingKey),
                { Waiting: { } product } => PriceCollectorWorkListPolicy.WaitingProductComesAfter(
                    product.WaitingKey,
                    continueAfter.WaitingKey),
                _ => false,
            };

            if (comesAfter)
            {
                return index;
            }
        }

        return 0;
    }

    /// <summary>
    /// The stored work items grouped by the product they are for, so each published product carries
    /// its own handles and no handle is published twice.
    /// </summary>
    private static (
        Dictionary<long, IReadOnlyList<PriceCollectorWorkItemHandle>> MatchedProducts,
        Dictionary<string, IReadOnlyList<PriceCollectorWorkItemHandle>> WaitingProducts)
        HandlesByProduct(IssuedPriceCollectorRun issued)
    {
        Dictionary<long, List<PriceCollectorWorkItemHandle>> byMasterProduct = [];
        Dictionary<string, List<PriceCollectorWorkItemHandle>> byWaitingKey = new(StringComparer.Ordinal);

        foreach (var item in issued.WorkItems)
        {
            var handle = new PriceCollectorWorkItemHandle(item.Id, item.SupplierPresetId);

            if (item.MasterProductId is { } masterProductId)
            {
                if (!byMasterProduct.TryGetValue(masterProductId, out var own))
                {
                    byMasterProduct[masterProductId] = own = [];
                }

                own.Add(handle);
            }
            else if (item.WaitingKey is { } waitingKey)
            {
                if (!byWaitingKey.TryGetValue(waitingKey, out var own))
                {
                    byWaitingKey[waitingKey] = own = [];
                }

                own.Add(handle);
            }
        }

        return (
            byMasterProduct.ToDictionary(
                entry => entry.Key,
                entry => (IReadOnlyList<PriceCollectorWorkItemHandle>)entry.Value),
            byWaitingKey.ToDictionary(
                entry => entry.Key,
                entry => (IReadOnlyList<PriceCollectorWorkItemHandle>)entry.Value,
                StringComparer.Ordinal));
    }

    private static PriceCollectorKnownListing KnownListing(TrackedSupplierListing listing) =>
        new(
            listing.Id,
            listing.SupplierPresetId,
            listing.SupplierProductId,
            listing.Url,
            listing.Title,
            listing.UnitsPerPack,
            listing.Barcode,
            listing.BarcodeScope,
            listing.LastCheckedAtUtc);

    private sealed record WorkPlan(
        IReadOnlyList<TrackedMasterProduct> MatchedProducts,
        IReadOnlyList<ProductAwaitingMatch> WaitingProducts,
        IReadOnlyList<NewPriceCollectorWorkItem> WorkItems,
        bool WorkItemLimitReached);

    /// <summary>
    /// One place in the rotation: a product named in exactly one of the two ways a product can be
    /// named, which is the same rule a stored work item is held to.
    /// </summary>
    private sealed record Candidate(TrackedMasterProduct? Matched, ProductAwaitingMatch? Waiting);
}
