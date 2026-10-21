using Inventory.Application.Purchases;
using Inventory.Domain.Purchases;
using InventoryApi.Tests.Application.Reporting.ProductProfitability;
using Xunit;

namespace InventoryApi.Tests.Application.Purchases;

/// <summary>
/// The bulk product purchase-cost summary use case (issue #788, B of #786). It answers the same
/// last/cheapest facts <c>GET api/products/{id}/price-history</c> answers for one product, for every
/// product at once, and it must answer them by <em>applying</em>
/// <see cref="SupplierPriceComparisonPolicy"/> rather than by selecting a latest or a cheapest row
/// itself: a second selection rule here would let the product screens and the product's own price
/// history disagree about what was last paid.
///
/// <para>These tests therefore pin the policy's observable decisions through the use case - the
/// tie-breaks, the preserved zero cost and the null supplier - with the records supplied in an order
/// that would give a different answer if anything here sorted by arrival.</para>
/// </summary>
public class ListProductPurchaseCostSummariesTests
{
    private static readonly DateTime January = new(2026, 1, 5);
    private static readonly DateTime February = new(2026, 2, 5);
    private static readonly DateTime March = new(2026, 3, 5);

    private static SupplierPriceHistoryEntry Entry(
        int purchaseItemId, DateTime purchaseDate, decimal unitCost, string? supplierName = "Acme Supplies") =>
        new(
            purchaseItemId,
            PurchaseId: purchaseItemId * 10,
            PurchaseTitle: $"Order {purchaseItemId}",
            purchaseDate,
            SupplierId: supplierName is null ? null : purchaseItemId,
            supplierName,
            unitCost);

    private static ListProductPurchaseCostSummaries UseCase(
        params (long ProductId, SupplierPriceHistoryEntry[] Entries)[] history) =>
        new(new FakeProductPurchaseCostFactsProvider(
            history.ToDictionary(
                product => product.ProductId,
                product => (IReadOnlyList<SupplierPriceHistoryEntry>)product.Entries.ToList())));

    private static Task<IReadOnlyList<ProductPurchaseCostSummaryDto>> HandleAll(
        ListProductPurchaseCostSummaries useCase) =>
        useCase.Handle(Array.Empty<long>(), CancellationToken.None);

    [Fact]
    public async Task A_business_with_no_purchase_history_at_all_is_answered_no_summaries()
    {
        var summaries = await HandleAll(UseCase());

        Assert.Empty(summaries);
    }

    /// <summary>
    /// A product the provider returns with an empty history is absent rather than reported with a
    /// fabricated cost. The provider does not produce such an entry today; the use case must not
    /// depend on that, because the alternative to being absent is publishing a last cost of zero.
    /// </summary>
    [Fact]
    public async Task A_product_with_no_purchase_line_is_absent_rather_than_reported_as_zero()
    {
        var summaries = await HandleAll(UseCase((7, [])));

        Assert.Empty(summaries);
    }

    [Fact]
    public async Task One_purchase_is_both_the_last_and_the_cheapest_cost()
    {
        var summaries = await HandleAll(UseCase((7, [Entry(1, January, 2.00m)])));

        var summary = Assert.Single(summaries);
        Assert.Equal(7, summary.ProductId);
        Assert.Equal(2.00m, summary.LastUnitCost);
        Assert.Equal("Acme Supplies", summary.LastSupplierName);
        Assert.Equal(January, summary.LastPurchaseDate);
        Assert.Equal(2.00m, summary.LowestUnitCost);
        Assert.Equal("Acme Supplies", summary.LowestSupplierName);
        Assert.Equal(January, summary.LowestPurchaseDate);
    }

    /// <summary>
    /// Several suppliers over time: the last cost is the most recent purchase's, the cheapest is the
    /// cheapest purchase's, and each carries its own supplier. The entries are supplied
    /// newest-first so an implementation that took the first or last element of the input would
    /// get one of the two wrong.
    /// </summary>
    [Fact]
    public async Task Several_suppliers_report_the_newest_cost_and_the_cheapest_cost_separately()
    {
        var summaries = await HandleAll(UseCase((7,
        [
            Entry(3, March, 2.20m, "Best Wholesale"),
            Entry(2, February, 1.50m, "Cheap Cartons"),
            Entry(1, January, 2.00m, "Acme Supplies"),
        ])));

        var summary = Assert.Single(summaries);
        Assert.Equal(2.20m, summary.LastUnitCost);
        Assert.Equal("Best Wholesale", summary.LastSupplierName);
        Assert.Equal(March, summary.LastPurchaseDate);
        Assert.Equal(1.50m, summary.LowestUnitCost);
        Assert.Equal("Cheap Cartons", summary.LowestSupplierName);
        Assert.Equal(February, summary.LowestPurchaseDate);
    }

    /// <summary>
    /// The cheapest-cost tie-break the policy owns: equal unit costs break to the earliest
    /// occurrence, then to the lower purchase item id. Both candidates are supplied after the
    /// losing one, in the reverse of the winning order, so insertion order cannot be what picks
    /// the winner.
    /// </summary>
    [Fact]
    public async Task An_equal_cheapest_cost_breaks_to_the_earliest_purchase_then_the_lower_item_id()
    {
        var summaries = await HandleAll(UseCase((7,
        [
            Entry(9, February, 1.50m, "Later Supplier"),
            Entry(4, January, 1.50m, "Higher Item Id"),
            Entry(2, January, 1.50m, "Earliest Occurrence"),
            Entry(1, March, 4.00m, "Dearest"),
        ])));

        var summary = Assert.Single(summaries);
        Assert.Equal(1.50m, summary.LowestUnitCost);
        Assert.Equal(January, summary.LowestPurchaseDate);
        Assert.Equal("Earliest Occurrence", summary.LowestSupplierName);
    }

    /// <summary>
    /// The last-cost tie-break the policy owns: two purchases on the same date break to the higher
    /// (most recently recorded) purchase item id. Supplied lowest id first, so the answer cannot be
    /// "whatever arrived last".
    /// </summary>
    [Fact]
    public async Task An_equal_latest_date_breaks_to_the_higher_purchase_item_id()
    {
        var summaries = await HandleAll(UseCase((7,
        [
            Entry(5, March, 3.00m, "Recorded First"),
            Entry(8, March, 2.40m, "Recorded Last"),
        ])));

        var summary = Assert.Single(summaries);
        Assert.Equal(2.40m, summary.LastUnitCost);
        Assert.Equal("Recorded Last", summary.LastSupplierName);
        Assert.Equal(March, summary.LastPurchaseDate);
    }

    /// <summary>
    /// A purchase with no supplier recorded reports a null supplier name. It is never replaced with
    /// a guess, with another purchase's supplier, or with a placeholder sentence the client cannot
    /// tell apart from a real supplier name - and the purchase itself is never dropped, which would
    /// silently move the last cost to an older one.
    /// </summary>
    [Fact]
    public async Task A_purchase_with_no_supplier_reports_a_null_supplier_name_and_is_not_dropped()
    {
        var summaries = await HandleAll(UseCase((7,
        [
            Entry(1, January, 2.00m, "Acme Supplies"),
            Entry(2, March, 1.20m, supplierName: null),
        ])));

        var summary = Assert.Single(summaries);
        Assert.Equal(1.20m, summary.LastUnitCost);
        Assert.Null(summary.LastSupplierName);
        Assert.Equal(1.20m, summary.LowestUnitCost);
        Assert.Null(summary.LowestSupplierName);
    }

    /// <summary>
    /// A genuine $0 purchase line is a real recorded cost - a free sample, a replacement delivery,
    /// a promotional case - and stays one. It is the cheapest cost paid, it is reported as
    /// <c>0</c>, and it is never filtered out or treated as a missing figure.
    /// </summary>
    [Fact]
    public async Task A_genuine_zero_cost_is_the_cheapest_cost_rather_than_a_missing_one()
    {
        var summaries = await HandleAll(UseCase((7,
        [
            Entry(1, January, 2.00m, "Acme Supplies"),
            Entry(2, February, 0m, "Free Sample Co"),
            Entry(3, March, 2.40m, "Best Wholesale"),
        ])));

        var summary = Assert.Single(summaries);
        Assert.Equal(0m, summary.LowestUnitCost);
        Assert.Equal("Free Sample Co", summary.LowestSupplierName);
        Assert.Equal(February, summary.LowestPurchaseDate);
        Assert.Equal(2.40m, summary.LastUnitCost);
    }

    /// <summary>
    /// The other direction: the most recent purchase being free makes the <em>last</em> cost zero,
    /// which is still an answer and not an absent one.
    /// </summary>
    [Fact]
    public async Task A_genuine_zero_cost_on_the_newest_purchase_is_the_last_cost()
    {
        var summaries = await HandleAll(UseCase((7,
        [
            Entry(1, January, 2.00m, "Acme Supplies"),
            Entry(2, March, 0m, supplierName: null),
        ])));

        var summary = Assert.Single(summaries);
        Assert.Equal(0m, summary.LastUnitCost);
        Assert.Null(summary.LastSupplierName);
        Assert.Equal(March, summary.LastPurchaseDate);
        Assert.Equal(0m, summary.LowestUnitCost);
    }

    /// <summary>
    /// Several products in one answer, each evaluated on its own history, ordered by product id so
    /// the array is deterministic rather than dependent on the adapter's grouping order.
    /// </summary>
    [Fact]
    public async Task Each_product_is_summarised_from_its_own_history_in_product_id_order()
    {
        var summaries = await HandleAll(UseCase(
            (9, [Entry(4, March, 5.00m, "Nine's Supplier")]),
            (7, [Entry(1, January, 2.00m, "Seven's Supplier")])));

        Assert.Equal([7, 9], summaries.Select(summary => summary.ProductId));
        Assert.Equal("Seven's Supplier", summaries[0].LastSupplierName);
        Assert.Equal("Nine's Supplier", summaries[1].LastSupplierName);
    }

    /// <summary>
    /// No requested ids means every product of the current business, retired included, which is the
    /// port's all-products read rather than an empty id list - the latter would answer nothing at
    /// all.
    /// </summary>
    [Fact]
    public async Task No_requested_ids_reads_the_whole_businesss_history()
    {
        var facts = new FakeProductPurchaseCostFactsProvider(
            new Dictionary<long, IReadOnlyList<SupplierPriceHistoryEntry>>
            {
                [7] = [Entry(1, January, 2.00m)],
            });

        var summaries = await new ListProductPurchaseCostSummaries(facts)
            .Handle(Array.Empty<long>(), CancellationToken.None);

        Assert.True(facts.ReadEveryProduct);
        Assert.Null(facts.LastRequestedProductIds);
        Assert.Single(summaries);
    }

    /// <summary>
    /// Requested ids are passed through to the port unchanged, so the narrowing happens in the one
    /// query rather than by filtering a whole business's history in memory.
    /// </summary>
    [Fact]
    public async Task Requested_ids_are_asked_of_the_port_as_given()
    {
        var facts = new FakeProductPurchaseCostFactsProvider(
            new Dictionary<long, IReadOnlyList<SupplierPriceHistoryEntry>>
            {
                [7] = [Entry(1, January, 2.00m)],
            });

        await new ListProductPurchaseCostSummaries(facts).Handle([7, 9], CancellationToken.None);

        Assert.False(facts.ReadEveryProduct);
        Assert.Equal([7L, 9L], facts.LastRequestedProductIds);
    }
}
