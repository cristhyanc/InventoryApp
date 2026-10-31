using Inventory.Application.Purchases;
using Inventory.Application.SupplierOrders;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;
using DomainStatus = Inventory.Domain.SupplierOrders.SupplierOrderStatus;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Relational SQLite tests for completing a partially delivered supplier order as part of saving the
/// purchase that records its final delivery (issue #974).
///
/// <para>They are relational on purpose. The guarantee under test is that the order's authoritative
/// read, the comparison against the decision the operator was shown, the purchase, its receipt
/// allocations, its stock movements, its cost rebuild and the order's status write are
/// <strong>one transaction</strong>: a refusal has to leave no purchase, no movement and no status
/// change behind, and an EF Core InMemory database - which runs no transaction at all - cannot prove
/// that. The tenant scope is a real one too, so a cross-business order id is refused by the central
/// query filters rather than by anything written here.</para>
///
/// <para>Every prior delivery here is a real earlier purchase rather than a hand-set
/// <c>QuantityReceived</c>, because the authoritative receipt total <em>is</em> the sum of the
/// order's receipt allocations: the rollup derives the column from them on every write. A test that
/// set the column directly would be asserting against a number the next write would discard.</para>
/// </summary>
public sealed class SupplierOrderPartialCompletionTests : IDisposable
{
    private const int BusinessA = 1;
    private const int BusinessB = 2;
    private const long ProductId = 1;
    private const long SecondProductId = 2;
    private const int SupplierId = 1;
    private const decimal UnitCost = 2.50m;

    private static readonly DateTime OrderDate = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime PriorDeliveryDate = new(2026, 9, 5, 2, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime PurchaseDate = new(2026, 9, 12, 2, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime DecidedAt = new(2026, 9, 12, 3, 30, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public SupplierOrderPartialCompletionTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var setup = TestAppDbContext.Unrestricted(_options);
        setup.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    // ---------------------------------------------------------------- the happy path

    /// <summary>
    /// The business outcome: a final delivery short of the order is recorded in full, and the
    /// undelivered balance is closed in the same save. Nothing about the goods that never arrived is
    /// invented - no quantity, no stock movement, no cost - and everything about the order stays
    /// inspectable.
    /// </summary>
    [Fact]
    public async Task Completing_records_only_what_arrived_and_closes_the_undelivered_balance()
    {
        var orderId = await SeedOrderAsync((ProductId, 24m));
        await SavePurchaseAsync(PriorDeliveryDate, null, null, (ProductId, 6m));

        var preview = await PreviewAsync(orderId, (ProductId, 10m));
        var outstanding = Assert.Single(preview.OutstandingLines);
        Assert.Equal((24m, 6m, 10m, 8m),
            (outstanding.QuantityOrdered, outstanding.QuantityAlreadyReceived, outstanding.QuantityOnThisPurchase,
                outstanding.OutstandingQuantity));
        // The outstanding quantity is reported in the unit the product is ordered in.
        Assert.Equal("can", outstanding.Unit);

        await SavePurchaseAsync(PurchaseDate, orderId, preview.DecisionFingerprint, (ProductId, 10m));

        await using var verify = TestAppDbContext.For(_options, BusinessA);
        var order = await verify.SupplierOrders.Include(o => o.Lines).SingleAsync(o => o.Id == orderId);
        Assert.Equal(SupplierOrderStatus.ClosedPartiallyDelivered, order.Status);
        Assert.Equal(DecidedAt, order.CompletedWithUndeliveredAt);
        Assert.Equal(TestPurchaseUseCases.Operator.DirectoryTenantId, order.CompletedByDirectoryTenantId);
        Assert.Equal(TestPurchaseUseCases.Operator.ObjectId, order.CompletedByObjectId);

        // Ordered quantity, actual receipts and the purchase links are all preserved as history.
        var line = Assert.Single(order.Lines);
        Assert.Equal((24m, 16m), (line.QuantityOrdered, line.QuantityReceived));
        Assert.Equal(
            [6m, 10m],
            await verify.SupplierOrderReceiptAllocations
                .Where(allocation => allocation.SupplierOrderLineId == line.Id)
                .OrderBy(allocation => allocation.Id)
                .Select(allocation => allocation.QuantityApplied)
                .ToListAsync());

        // What arrived became stock and cost; the 8 units that never did became neither.
        var product = await verify.Products.SingleAsync(candidate => candidate.Id == ProductId);
        Assert.Equal(16, product.QuantityInStock);
        Assert.Equal(16, product.CostingQuantity);
        Assert.Equal(40m, product.InventoryValue);
        Assert.Equal(2, await verify.StockAdjustments.CountAsync());
        Assert.Equal(16, await verify.StockAdjustments.SumAsync(movement => movement.QuantityChange));
        Assert.Equal(40m, await verify.StockAdjustments.SumAsync(movement => movement.TotalCost));
    }

    /// <summary>
    /// The undelivered balance stops counting as supply that is on its way, which is what makes the
    /// product reorder again, and the order stops being listed as active. Nothing about the goods'
    /// value changes - there never was any.
    /// </summary>
    [Fact]
    public async Task A_completed_orders_balance_is_no_longer_outstanding_supply()
    {
        var orderId = await SeedOrderAsync((ProductId, 24m));
        await SavePurchaseAsync(PriorDeliveryDate, null, null, (ProductId, 6m));

        await using (var before = TestAppDbContext.For(_options, BusinessA))
        {
            Assert.Equal(18m, await OutstandingForProductAsync(before));
            Assert.Contains(
                await new EfSupplierOrderStore(before).ListActiveAsync(CancellationToken.None),
                order => order.Id == orderId);
        }

        var preview = await PreviewAsync(orderId, (ProductId, 10m));
        await SavePurchaseAsync(PurchaseDate, orderId, preview.DecisionFingerprint, (ProductId, 10m));

        await using var after = TestAppDbContext.For(_options, BusinessA);
        Assert.Empty(await new EfOutstandingSupplierOrderQuantityStore(after)
            .GetOutstandingQuantitiesByProductAsync(CancellationToken.None));
        Assert.DoesNotContain(
            await new EfSupplierOrderStore(after).ListActiveAsync(CancellationToken.None),
            order => order.Id == orderId);
    }

    /// <summary>
    /// A line this purchase leaves out entirely is closed at the quantity that really arrived - zero.
    /// Nothing infers a receipt to make the order add up.
    /// </summary>
    [Fact]
    public async Task An_omitted_line_is_closed_undelivered_rather_than_marked_received()
    {
        var orderId = await SeedOrderAsync((ProductId, 24m), (SecondProductId, 10m));

        var preview = await PreviewAsync(orderId, (ProductId, 24m));
        Assert.Equal(
            (SecondProductId, 10m),
            (Assert.Single(preview.OutstandingLines).ProductId, preview.OutstandingLines[0].OutstandingQuantity));

        await SavePurchaseAsync(PurchaseDate, orderId, preview.DecisionFingerprint, (ProductId, 24m));

        await using var verify = TestAppDbContext.For(_options, BusinessA);
        var order = await verify.SupplierOrders.Include(o => o.Lines).SingleAsync(o => o.Id == orderId);
        Assert.Equal(SupplierOrderStatus.ClosedPartiallyDelivered, order.Status);
        var omitted = order.Lines.Single(line => line.ProductId == SecondProductId);
        Assert.Equal((10m, 0m), (omitted.QuantityOrdered, omitted.QuantityReceived));
        Assert.Equal(0, (await verify.Products.SingleAsync(p => p.Id == SecondProductId)).QuantityInStock);
        Assert.Null((await verify.Products.SingleAsync(p => p.Id == SecondProductId)).CostingQuantity);
    }

    /// <summary>
    /// Keeping the order open is the other half of the choice, and it is simply the save without a
    /// decision: the order stays partially received and its balance stays outstanding supply.
    /// </summary>
    [Fact]
    public async Task Keeping_the_order_open_leaves_it_partially_received_and_still_outstanding()
    {
        var orderId = await SeedOrderAsync((ProductId, 24m));
        await SavePurchaseAsync(PriorDeliveryDate, null, null, (ProductId, 6m));

        await SavePurchaseAsync(PurchaseDate, orderId: null, fingerprint: null, (ProductId, 10m));

        await using var verify = TestAppDbContext.For(_options, BusinessA);
        var order = await verify.SupplierOrders.SingleAsync(candidate => candidate.Id == orderId);
        Assert.Equal(SupplierOrderStatus.PartiallyReceived, order.Status);
        Assert.Null(order.CompletedWithUndeliveredAt);
        Assert.Equal(8m, await OutstandingForProductAsync(verify));
    }

    // ---------------------------------------------------------------- stale and refused decisions

    /// <summary>
    /// The concurrency case the fingerprint exists for: another receipt lands between the preview and
    /// the save. The purchase is refused whole - no purchase row, no line, no allocation, no stock
    /// movement, no costing change and no status change - and the operator is asked to review.
    /// </summary>
    [Fact]
    public async Task A_competing_receipt_between_the_preview_and_the_save_persists_nothing()
    {
        var orderId = await SeedOrderAsync((ProductId, 24m));
        var preview = await PreviewAsync(orderId, (ProductId, 10m));

        // A second receipt commits in between, so the balance the operator decided over is gone.
        await SavePurchaseAsync(PriorDeliveryDate, null, null, (ProductId, 6m));
        var before = await SnapshotAsync(orderId);

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => SavePurchaseAsync(PurchaseDate, orderId, preview.DecisionFingerprint, (ProductId, 10m)));

        Assert.Contains("changed after the completion choice was shown", refusal.Message, StringComparison.Ordinal);
        await AssertUnchangedAsync(orderId, before);
    }

    [Fact]
    public async Task A_fingerprint_that_was_never_issued_persists_nothing()
    {
        var orderId = await SeedOrderAsync((ProductId, 24m));
        var before = await SnapshotAsync(orderId);

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => SavePurchaseAsync(PurchaseDate, orderId, "not-a-fingerprint-this-server-issued", (ProductId, 10m)));

        Assert.Contains("changed after the completion choice was shown", refusal.Message, StringComparison.Ordinal);
        await AssertUnchangedAsync(orderId, before);
    }

    /// <summary>
    /// The order was cancelled or already completed after the preview. It is no longer an order a
    /// completion applies to, so the save stops rather than closing it twice or relabelling a
    /// cancellation.
    /// </summary>
    [Theory]
    [InlineData(SupplierOrderStatus.Cancelled)]
    [InlineData(SupplierOrderStatus.ClosedPartiallyDelivered)]
    [InlineData(SupplierOrderStatus.Received)]
    public async Task An_order_closed_between_the_preview_and_the_save_persists_nothing(SupplierOrderStatus closed)
    {
        var orderId = await SeedOrderAsync((ProductId, 24m));
        var preview = await PreviewAsync(orderId, (ProductId, 10m));

        await using (var competing = TestAppDbContext.For(_options, BusinessA))
        {
            var order = await competing.SupplierOrders.SingleAsync(candidate => candidate.Id == orderId);
            order.Status = closed;
            await competing.SaveChangesAsync();
        }

        var before = await SnapshotAsync(orderId);
        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => SavePurchaseAsync(PurchaseDate, orderId, preview.DecisionFingerprint, (ProductId, 10m)));

        Assert.Contains("no longer open", refusal.Message, StringComparison.Ordinal);
        await AssertUnchangedAsync(orderId, before);
    }

    /// <summary>
    /// The existing fully received path is preserved: a purchase that completes the order leaves no
    /// undelivered balance, so a completion request over it is refused rather than recording a
    /// completion over a complete delivery. Nothing is saved, because the whole transaction rolls back.
    /// </summary>
    [Fact]
    public async Task A_purchase_that_receives_the_whole_order_cannot_also_complete_it()
    {
        var orderId = await SeedOrderAsync((ProductId, 24m));
        await SavePurchaseAsync(PriorDeliveryDate, null, null, (ProductId, 6m));

        var preview = await PreviewAsync(orderId, (ProductId, 18m));
        Assert.Empty(preview.OutstandingLines);
        var before = await SnapshotAsync(orderId);

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => SavePurchaseAsync(PurchaseDate, orderId, preview.DecisionFingerprint, (ProductId, 18m)));

        Assert.Contains("nothing outstanding", refusal.Message, StringComparison.Ordinal);
        await AssertUnchangedAsync(orderId, before);
    }

    /// <summary>
    /// The same purchase without the completion request is the unchanged fully received path: the
    /// order becomes <c>Received</c> and nobody is asked to close a balance that does not exist.
    /// </summary>
    [Fact]
    public async Task A_purchase_that_receives_the_whole_order_still_marks_it_received()
    {
        var orderId = await SeedOrderAsync((ProductId, 24m));
        await SavePurchaseAsync(PriorDeliveryDate, null, null, (ProductId, 6m));

        await SavePurchaseAsync(PurchaseDate, orderId: null, fingerprint: null, (ProductId, 18m));

        await using var verify = TestAppDbContext.For(_options, BusinessA);
        var order = await verify.SupplierOrders.SingleAsync(candidate => candidate.Id == orderId);
        Assert.Equal(SupplierOrderStatus.Received, order.Status);
        Assert.Null(order.CompletedWithUndeliveredAt);
    }

    /// <summary>An unlinked purchase - no supplier order anywhere near it - is completely unaffected.</summary>
    [Fact]
    public async Task An_unlinked_purchase_is_unchanged()
    {
        await SeedCatalogueAsync();

        var purchaseId = await SavePurchaseAsync(PurchaseDate, orderId: null, fingerprint: null, (ProductId, 10m));

        await using var verify = TestAppDbContext.For(_options, BusinessA);
        Assert.Equal(purchaseId, (await verify.Receipts.SingleAsync()).Id);
        Assert.Empty(await verify.SupplierOrderReceiptAllocations.ToListAsync());
        Assert.Equal(10, (await verify.Products.SingleAsync(p => p.Id == ProductId)).QuantityInStock);
    }

    // ---------------------------------------------------------------- tenancy

    /// <summary>
    /// Another business's order id reaches no order at all: the central tenant query filters match
    /// nothing, so the preview is a not-found and the save is refused as unknown, with business B's
    /// order untouched. The id in the request never chooses an owner.
    /// </summary>
    [Fact]
    public async Task Another_businesss_order_cannot_be_completed_and_is_left_untouched()
    {
        await SeedCatalogueAsync();
        int otherOrderId;
        await using (var seed = TestAppDbContext.Unrestricted(_options))
        {
            seed.Businesses.Add(new Business
            {
                Id = BusinessB,
                Name = "Other",
                Status = BusinessStatus.Active,
                CreatedAtUtc = DateTime.UtcNow,
            });
            seed.Products.Add(new Product { Id = 99, Name = "Theirs", BusinessId = BusinessB });
            seed.Suppliers.Add(new Supplier { Id = 99, Name = "Their supplier", BusinessId = BusinessB });
            var order = new SupplierOrder
            {
                BusinessId = BusinessB,
                SupplierId = 99,
                OrderDate = OrderDate,
                Status = SupplierOrderStatus.PartiallyReceived,
                Lines =
                {
                    new SupplierOrderLine
                    {
                        BusinessId = BusinessB, ProductId = 99, QuantityOrdered = 24m, QuantityReceived = 6m,
                    },
                },
            };
            seed.SupplierOrders.Add(order);
            await seed.SaveChangesAsync();
            otherOrderId = order.Id;
        }

        await using (var db = TestAppDbContext.For(_options, BusinessA))
        {
            Assert.Null(await new PreviewSupplierOrderCompletion(new EfSupplierOrderCompletionStore(db)).Handle(
                otherOrderId, SupplierId, PurchaseDate, [new PurchaseLineToReceive(ProductId, 1m)],
                CancellationToken.None));
        }

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => SavePurchaseAsync(PurchaseDate, otherOrderId, "anything", (ProductId, 1m)));

        Assert.Contains("does not exist", refusal.Message, StringComparison.Ordinal);
        await using var verify = TestAppDbContext.Unrestricted(_options);
        var theirs = await verify.SupplierOrders.SingleAsync(order => order.Id == otherOrderId);
        Assert.Equal(SupplierOrderStatus.PartiallyReceived, theirs.Status);
        Assert.Null(theirs.CompletedWithUndeliveredAt);
        Assert.Empty(await verify.Receipts.ToListAsync());
    }

    // ---------------------------------------------------------------- after completion

    /// <summary>
    /// A completed order accepts no further receipts through the normal entry path: a later purchase
    /// for the same supplier and product allocates nothing to it, and its stored receipts stay as
    /// they were.
    /// </summary>
    [Fact]
    public async Task A_completed_order_receives_nothing_from_a_later_purchase()
    {
        var (orderId, _) = await CompleteOrderAsync();

        await SavePurchaseAsync(PurchaseDate.AddDays(1), orderId: null, fingerprint: null, (ProductId, 8m));

        await using var verify = TestAppDbContext.For(_options, BusinessA);
        var order = await verify.SupplierOrders.Include(o => o.Lines).SingleAsync(o => o.Id == orderId);
        Assert.Equal(SupplierOrderStatus.ClosedPartiallyDelivered, order.Status);
        Assert.Equal(16m, Assert.Single(order.Lines).QuantityReceived);
        // The later purchase's own stock still landed; it simply is not a receipt against this order.
        Assert.Equal(24, (await verify.Products.SingleAsync(p => p.Id == ProductId)).QuantityInStock);
    }

    [Fact]
    public async Task A_completed_order_cannot_be_cancelled()
    {
        var (orderId, _) = await CompleteOrderAsync();

        await using var db = TestAppDbContext.For(_options, BusinessA);
        Assert.False(await new EfSupplierOrderStore(db).CancelAsync(orderId, CancellationToken.None));
        var order = await db.SupplierOrders.SingleAsync(candidate => candidate.Id == orderId);
        Assert.Equal(SupplierOrderStatus.ClosedPartiallyDelivered, order.Status);
        Assert.Equal(DecidedAt, order.CompletedWithUndeliveredAt);
    }

    /// <summary>
    /// Correcting the purchase a completed order was closed on is refused (issue #974). Reopening is
    /// out of scope, and the two alternatives are both worse: detaching the allocations would leave
    /// the order recording deliveries no purchase holds, and re-allocating would make a closed order
    /// accept a receipt. Nothing is written either way.
    /// </summary>
    [Fact]
    public async Task A_purchase_a_completed_order_was_closed_on_can_be_neither_edited_nor_deleted()
    {
        var (orderId, purchaseId) = await CompleteOrderAsync();
        var before = await SnapshotAsync(orderId);

        await using (var db = TestAppDbContext.For(_options, BusinessA))
        {
            var store = new EfPurchaseStore(db, TestCostingUseCases.Rebuild(db), TestClocks.Fixed);
            var editRefusal = await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpdateAsync(
                purchaseId,
                new PurchaseFields("Weekly restock", null, null, null, null, PurchaseDate, SupplierId),
                [new PurchaseItemInput(ProductId, 4m, UnitCost)],
                CancellationToken.None));
            Assert.Contains($"supplier order #{orderId}", editRefusal.Message, StringComparison.Ordinal);
        }

        await using (var db = TestAppDbContext.For(_options, BusinessA))
        {
            var store = new EfPurchaseStore(db, TestCostingUseCases.Rebuild(db), TestClocks.Fixed);
            var deleteRefusal = await Assert.ThrowsAsync<InvalidOperationException>(
                () => store.DeleteAsync(purchaseId, CancellationToken.None));
            Assert.Contains($"supplier order #{orderId}", deleteRefusal.Message, StringComparison.Ordinal);
        }

        // Neither refusal wrote anything: the receipts the order was closed on, the stock they
        // created and the explicit completion are all exactly as they were.
        await AssertUnchangedAsync(orderId, before);
    }

    /// <summary>
    /// A purchase linked to an order that is still open is unaffected by any of this and is still
    /// editable, with its reduced quantity reconciled back onto the order as before.
    /// </summary>
    [Fact]
    public async Task A_purchase_linked_to_an_open_order_is_still_editable()
    {
        var orderId = await SeedOrderAsync((ProductId, 24m));
        var purchaseId = await SavePurchaseAsync(PurchaseDate, orderId: null, fingerprint: null, (ProductId, 10m));

        await using (var db = TestAppDbContext.For(_options, BusinessA))
        {
            var store = new EfPurchaseStore(db, TestCostingUseCases.Rebuild(db), TestClocks.Fixed);
            Assert.NotNull(await store.UpdateAsync(
                purchaseId,
                new PurchaseFields("Weekly restock", null, null, null, null, PurchaseDate, SupplierId),
                [new PurchaseItemInput(ProductId, 4m, UnitCost)],
                CancellationToken.None));
        }

        await using var verify = TestAppDbContext.For(_options, BusinessA);
        Assert.Equal(
            4m,
            (await verify.SupplierOrderLines.SingleAsync(line => line.SupplierOrderId == orderId)).QuantityReceived);
    }

    // ---------------------------------------------------------------- helpers

    private async Task SeedCatalogueAsync()
    {
        await using var seed = TestAppDbContext.Unrestricted(_options);
        seed.Businesses.Add(new Business
        {
            Id = BusinessA,
            Name = "Ours",
            Status = BusinessStatus.Active,
            CreatedAtUtc = DateTime.UtcNow,
        });
        seed.Products.AddRange(
            new Product { Id = ProductId, Name = "Coke", BusinessId = BusinessA, Unit = "can" },
            new Product { Id = SecondProductId, Name = "Chips", BusinessId = BusinessA, Unit = "bag" });
        seed.Suppliers.Add(new Supplier { Id = SupplierId, Name = "Costco", BusinessId = BusinessA });
        // A transition baseline before the first purchase, so the weighted-average replay has an
        // opening position to start from, exactly as EfPurchaseStoreTests arranges one.
        seed.InventoryCostTransitionBaselines.AddRange(
            new InventoryCostTransitionBaseline
            {
                BusinessId = BusinessA,
                ProductId = ProductId,
                CutoffAt = OrderDate.AddDays(-1),
                HomeStockQuantity = 0,
                CostSource = InventoryCostBaselineSource.ManualAuthoritative,
            },
            new InventoryCostTransitionBaseline
            {
                BusinessId = BusinessA,
                ProductId = SecondProductId,
                CutoffAt = OrderDate.AddDays(-1),
                HomeStockQuantity = 0,
                CostSource = InventoryCostBaselineSource.ManualAuthoritative,
            });
        await seed.SaveChangesAsync();
    }

    /// <summary>One open order of business A, with nothing received yet.</summary>
    private async Task<int> SeedOrderAsync(params (long ProductId, decimal Ordered)[] lines)
    {
        await SeedCatalogueAsync();

        await using var seed = TestAppDbContext.Unrestricted(_options);
        var order = new SupplierOrder
        {
            BusinessId = BusinessA,
            SupplierId = SupplierId,
            OrderDate = OrderDate,
            Status = SupplierOrderStatus.Ordered,
        };
        foreach (var line in lines)
        {
            order.Lines.Add(new SupplierOrderLine
            {
                BusinessId = BusinessA,
                ProductId = line.ProductId,
                QuantityOrdered = line.Ordered,
                UnitPrice = UnitCost,
            });
        }

        seed.SupplierOrders.Add(order);
        await seed.SaveChangesAsync();
        return order.Id;
    }

    /// <summary>
    /// The whole workflow once: 24 ordered, 6 delivered earlier, 10 on the final purchase, and the
    /// remaining 8 closed as undelivered.
    /// </summary>
    private async Task<(int OrderId, int FinalPurchaseId)> CompleteOrderAsync()
    {
        var orderId = await SeedOrderAsync((ProductId, 24m));
        await SavePurchaseAsync(PriorDeliveryDate, null, null, (ProductId, 6m));
        var preview = await PreviewAsync(orderId, (ProductId, 10m));
        var purchaseId = await SavePurchaseAsync(
            PurchaseDate, orderId, preview.DecisionFingerprint, (ProductId, 10m));
        return (orderId, purchaseId);
    }

    private async Task<SupplierOrderCompletionPreview> PreviewAsync(
        int orderId, params (long ProductId, decimal Quantity)[] purchaseLines)
    {
        await using var db = TestAppDbContext.For(_options, BusinessA);
        var preview = await new PreviewSupplierOrderCompletion(new EfSupplierOrderCompletionStore(db)).Handle(
            orderId,
            SupplierId,
            PurchaseDate,
            purchaseLines.Select(line => new PurchaseLineToReceive(line.ProductId, line.Quantity)).ToList(),
            CancellationToken.None);
        Assert.NotNull(preview);
        return preview!;
    }

    /// <summary>The purchase save, with or without the completion decision. Returns the new purchase's id.</summary>
    private async Task<int> SavePurchaseAsync(
        DateTime purchaseDate,
        int? orderId,
        string? fingerprint,
        params (long ProductId, decimal Quantity)[] lines)
    {
        await using var db = TestAppDbContext.For(_options, BusinessA);
        var store = new EfPurchaseStore(db, TestCostingUseCases.Rebuild(db), TestClocks.Fixed);
        var decision = orderId is { } id && fingerprint is not null
            ? new SupplierOrderCompletionDecision(
                id,
                fingerprint,
                TestPurchaseUseCases.Operator.DirectoryTenantId,
                TestPurchaseUseCases.Operator.ObjectId,
                DecidedAt)
            : null;

        var purchase = await store.CreateAsync(
            new PurchaseFields("Weekly restock", null, null, null, null, purchaseDate, SupplierId),
            lines.Select(line => new PurchaseItemInput(line.ProductId, line.Quantity, UnitCost)).ToList(),
            new PurchaseFileMetadata("stored.jpg", "image/jpeg", "receipt.jpg", 3),
            decision,
            attachmentReservation: null,
            CancellationToken.None);
        return purchase.Id;
    }

    private static async Task<decimal> OutstandingForProductAsync(AppDbContext db)
    {
        var outstanding = await new EfOutstandingSupplierOrderQuantityStore(db)
            .GetOutstandingQuantitiesByProductAsync(CancellationToken.None);
        return outstanding.GetValueOrDefault(ProductId);
    }

    /// <summary>
    /// Everything a refused save must leave exactly as it was: the purchase history, its receipt
    /// allocations, its stock movements, the product's costing position and the order's own state.
    /// </summary>
    private sealed record StoreSnapshot(
        int Purchases,
        int Items,
        int Allocations,
        int Movements,
        int QuantityInStock,
        int? CostingQuantity,
        decimal? InventoryValue,
        SupplierOrderStatus Status,
        DateTime? CompletedWithUndeliveredAt,
        string? CompletedByDirectoryTenantId,
        string? CompletedByObjectId,
        decimal QuantityReceived);

    private async Task<StoreSnapshot> SnapshotAsync(int orderId)
    {
        await using var db = TestAppDbContext.Unrestricted(_options);
        var product = await db.Products.AsNoTracking().SingleAsync(candidate => candidate.Id == ProductId);
        var order = await db.SupplierOrders.AsNoTracking().SingleAsync(candidate => candidate.Id == orderId);
        return new StoreSnapshot(
            await db.Receipts.CountAsync(),
            await db.ReceiptItems.CountAsync(),
            await db.SupplierOrderReceiptAllocations.CountAsync(),
            await db.StockAdjustments.CountAsync(),
            product.QuantityInStock,
            product.CostingQuantity,
            product.InventoryValue,
            order.Status,
            order.CompletedWithUndeliveredAt,
            order.CompletedByDirectoryTenantId,
            order.CompletedByObjectId,
            await db.SupplierOrderLines.AsNoTracking()
                .Where(line => line.SupplierOrderId == orderId)
                .SumAsync(line => line.QuantityReceived));
    }

    private async Task AssertUnchangedAsync(int orderId, StoreSnapshot before) =>
        Assert.Equal(before, await SnapshotAsync(orderId));

    /// <summary>The Domain enum the Application layer reports, kept in step with the persisted one.</summary>
    [Fact]
    public void The_persisted_and_domain_completed_statuses_are_the_same_value() =>
        Assert.Equal((int)SupplierOrderStatus.ClosedPartiallyDelivered, (int)DomainStatus.ClosedPartiallyDelivered);
}
