using Inventory.Application.SupplierOrders;
using Inventory.Domain.SupplierOrders;
using Xunit;

namespace InventoryApi.Tests.Application.SupplierOrders;

/// <summary>
/// The projection the purchase form shows before offering the keep-open/complete choice (issue
/// #974). It is the server's own allocation and outstanding rules, which is the point: the browser
/// never subtracts a receipt from an order itself.
/// </summary>
public class PreviewSupplierOrderCompletionTests
{
    private static readonly DateTime PurchaseDate = new(2026, 9, 12, 2, 0, 0, DateTimeKind.Utc);

    private static Task<SupplierOrderCompletionPreview?> Preview(
        FakeSupplierOrderCompletionStore store, params PurchaseLineToReceive[] lines) =>
        new PreviewSupplierOrderCompletion(store).Handle(9, 4, PurchaseDate, lines, CancellationToken.None);

    [Fact]
    public async Task An_order_that_is_not_the_callers_is_not_found()
    {
        var preview = await Preview(new FakeSupplierOrderCompletionStore(null), new PurchaseLineToReceive(3, 10m));

        Assert.Null(preview);
    }

    [Fact]
    public async Task A_short_final_delivery_reports_the_shortfall_with_the_product_and_its_unit()
    {
        var store = new FakeSupplierOrderCompletionStore(
            FakeSupplierOrderCompletionStore.Facts(SupplierOrderStatus.PartiallyReceived, (11, 3, 24m, 6m)));

        var preview = await Preview(store, new PurchaseLineToReceive(3, 10m));

        Assert.NotNull(preview);
        Assert.True(preview!.EligibleForCompletion);
        var line = Assert.Single(preview.OutstandingLines);
        Assert.Equal((11, 3L, "Product 3", "can"),
            (line.SupplierOrderLineId, line.ProductId, line.ProductName, line.Unit));
        Assert.Equal((24m, 6m, 10m, 8m),
            (line.QuantityOrdered, line.QuantityAlreadyReceived, line.QuantityOnThisPurchase, line.OutstandingQuantity));
    }

    /// <summary>
    /// The decision is reported with a fingerprint of the order it was projected over, and the
    /// purchase this preview is about does not change it: the same order projected against a
    /// different purchase is still the same order to close.
    /// </summary>
    [Fact]
    public async Task The_fingerprint_describes_the_order_and_not_the_purchase()
    {
        var facts = FakeSupplierOrderCompletionStore.Facts(SupplierOrderStatus.PartiallyReceived, (11, 3, 24m, 6m));

        var small = await Preview(new FakeSupplierOrderCompletionStore(facts), new PurchaseLineToReceive(3, 1m));
        var large = await Preview(new FakeSupplierOrderCompletionStore(facts), new PurchaseLineToReceive(3, 9m));

        Assert.Equal(
            SupplierOrderCompletionFingerprint.Compute(
                1, 9, SupplierOrderStatus.PartiallyReceived, [new SupplierOrderLineFulfillment(11, 24m, 6m)]),
            small!.DecisionFingerprint);
        Assert.Equal(small.DecisionFingerprint, large!.DecisionFingerprint);
    }

    /// <summary>Only the affected products are listed: a line this purchase finishes is not an open balance.</summary>
    [Fact]
    public async Task Only_the_lines_left_outstanding_are_reported()
    {
        var store = new FakeSupplierOrderCompletionStore(
            FakeSupplierOrderCompletionStore.Facts(
                SupplierOrderStatus.PartiallyReceived, (11, 3, 24m, 6m), (12, 5, 10m, 0m)));

        var preview = await Preview(store, new PurchaseLineToReceive(3, 18m), new PurchaseLineToReceive(5, 4m));

        Assert.Equal([12], preview!.OutstandingLines.Select(line => line.SupplierOrderLineId));
        Assert.Equal(6m, Assert.Single(preview.OutstandingLines).OutstandingQuantity);
    }

    [Fact]
    public async Task A_purchase_that_receives_the_whole_order_leaves_nothing_to_close()
    {
        var store = new FakeSupplierOrderCompletionStore(
            FakeSupplierOrderCompletionStore.Facts(SupplierOrderStatus.PartiallyReceived, (11, 3, 24m, 6m)));

        var preview = await Preview(store, new PurchaseLineToReceive(3, 18m));

        Assert.True(preview!.EligibleForCompletion);
        Assert.Empty(preview.OutstandingLines);
    }

    /// <summary>
    /// A product that is not on the order delivers nothing against it, so the whole balance stays
    /// outstanding. Nothing is inferred from a quantity happening to match.
    /// </summary>
    [Fact]
    public async Task A_purchase_line_for_a_product_the_order_never_held_changes_nothing()
    {
        var store = new FakeSupplierOrderCompletionStore(
            FakeSupplierOrderCompletionStore.Facts(SupplierOrderStatus.PartiallyReceived, (11, 3, 24m, 6m)));

        var preview = await Preview(store, new PurchaseLineToReceive(99, 18m));

        Assert.Equal(18m, Assert.Single(preview!.OutstandingLines).OutstandingQuantity);
        Assert.Equal(0m, preview.OutstandingLines[0].QuantityOnThisPurchase);
    }

    /// <summary>
    /// Two lines of the same purchase for one product are allocated once across the order's lines,
    /// never twice - the allocation policy's own rule, reached through this preview.
    /// </summary>
    [Fact]
    public async Task Several_purchase_lines_for_one_product_are_allocated_once()
    {
        var store = new FakeSupplierOrderCompletionStore(
            FakeSupplierOrderCompletionStore.Facts(SupplierOrderStatus.Ordered, (11, 3, 24m, 0m)));

        var preview = await Preview(store, new PurchaseLineToReceive(3, 10m), new PurchaseLineToReceive(3, 6m));

        var line = Assert.Single(preview!.OutstandingLines);
        Assert.Equal((16m, 8m), (line.QuantityOnThisPurchase, line.OutstandingQuantity));
    }

    /// <summary>
    /// An order that was cancelled or already completed owes nothing at all, so there is nothing to
    /// offer and nothing to close - even if a receipt landed against it in the meantime.
    /// </summary>
    [Theory]
    [InlineData(SupplierOrderStatus.Cancelled)]
    [InlineData(SupplierOrderStatus.ClosedPartiallyDelivered)]
    public async Task A_closed_order_is_reported_as_ineligible_with_nothing_outstanding(SupplierOrderStatus closed)
    {
        var store = new FakeSupplierOrderCompletionStore(
            FakeSupplierOrderCompletionStore.Facts(closed, (11, 3, 24m, 6m)));

        var preview = await Preview(store, new PurchaseLineToReceive(3, 1m));

        Assert.False(preview!.EligibleForCompletion);
        Assert.Empty(preview.OutstandingLines);
        Assert.Equal(closed, preview.Status);
    }

    /// <summary>
    /// A fully received order is ineligible too, and its lines are square by definition, so the
    /// existing fully received path is never asked to close a balance that does not exist.
    /// </summary>
    [Fact]
    public async Task A_fully_received_order_is_ineligible_and_owes_nothing()
    {
        var store = new FakeSupplierOrderCompletionStore(
            FakeSupplierOrderCompletionStore.Facts(SupplierOrderStatus.Received, (11, 3, 24m, 24m)));

        var preview = await Preview(store, new PurchaseLineToReceive(3, 1m));

        Assert.False(preview!.EligibleForCompletion);
        Assert.Empty(preview.OutstandingLines);
    }

    /// <summary>
    /// The supplier, the purchase date and the distinct products are what the allocation's candidate
    /// query is scoped by, so the preview has to pass all three through unchanged.
    /// </summary>
    [Fact]
    public async Task The_supplier_date_and_distinct_products_reach_the_candidate_query()
    {
        var store = new FakeSupplierOrderCompletionStore(
            FakeSupplierOrderCompletionStore.Facts(SupplierOrderStatus.Ordered, (11, 3, 24m, 0m)));

        await Preview(store, new PurchaseLineToReceive(3, 1m), new PurchaseLineToReceive(3, 2m), new PurchaseLineToReceive(5, 1m));

        Assert.Equal(9, store.LastRequestedOrderId);
        Assert.Equal(4, store.LastRequestedSupplierId);
        Assert.Equal(PurchaseDate, store.LastRequestedPurchaseDate);
        Assert.Equal([3L, 5L], store.LastRequestedProductIds!);
    }
}
