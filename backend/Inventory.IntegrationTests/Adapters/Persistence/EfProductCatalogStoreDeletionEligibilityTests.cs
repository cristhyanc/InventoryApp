using Inventory.Application.Products;
using Inventory.Domain.FinancialConfiguration;
using Inventory.Domain.Providers;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// The catalogue read's deletion answer (issue #737, B2 of parent #329): the reads a person
/// maintains the catalogue from say whether each product may be deleted, so a screen offers
/// <strong>Delete</strong> only where the API allows it instead of attempting a delete that must
/// fail.
///
/// <para>What these tests hold to is that the read's answer is the <em>delete's</em> answer. Each
/// refusal below is compared with the message <see cref="EfProductStore.DeleteAsync"/> itself
/// produces for the same product, so the two can never drift into a screen that hides a delete the
/// server would allow, or offers one it would refuse for a reason it words differently.</para>
///
/// <para>Relational SQLite rather than an in-memory provider: the counts are grouped reads over six
/// tables, and what is asserted is that each product gets its own counts out of them.</para>
/// </summary>
public class EfProductCatalogStoreDeletionEligibilityTests : IDisposable
{
    private const int BusinessA = 1;

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public EfProductCatalogStoreDeletionEligibilityTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var setup = TestAppDbContext.Unrestricted(_options);
        setup.Database.EnsureCreated();

        // The business sells through Nayax, which is what makes an external id on one of its rows a
        // Nayax id and lets the refusal name the provider (issue #759).
        setup.Businesses.Add(new Business
        {
            Id = BusinessA,
            Name = "Vending A",
            Status = BusinessStatus.Active,
            SalesProvider = ExternalProvider.Nayax,
            CreatedAtUtc = DateTime.UtcNow,
        });
        setup.SaveChanges();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private static ProductCatalogFilter WholeCatalogue() => new(null, null, IncludeRetired: true);

    [Fact]
    public async Task ListOrderedByNameAsync_AllowsTheDelete_ForAProductNothingRefersTo()
    {
        await SeedProductAsync("Coke");

        await using var db = TestAppDbContext.For(_options, BusinessA);
        var product = Assert.Single(
            await new EfProductCatalogStore(db).ListOrderedByNameAsync(WholeCatalogue(), CancellationToken.None));

        Assert.NotNull(product.DeletionEligibility);
        Assert.True(product.DeletionEligibility.Allowed);
        Assert.Null(product.DeletionEligibility.Refusal);
    }

    /// <summary>
    /// Every kind of row the delete counts also refuses it on the read, worded exactly as the delete
    /// words it - this is the gap the screens could not close while the refusal was only discoverable
    /// by attempting the delete.
    /// </summary>
    [Theory]
    [InlineData(HistoryKind.StockAdjustment, "1 stock movement")]
    [InlineData(HistoryKind.PurchaseLine, "1 purchase line")]
    [InlineData(HistoryKind.SupplierOrderLine, "1 supplier order line")]
    [InlineData(HistoryKind.Sale, "1 sale")]
    [InlineData(HistoryKind.MachineStockCount, "1 machine stock count")]
    [InlineData(HistoryKind.CostRepair, "1 costing record")]
    [InlineData(HistoryKind.CostTransitionBaseline, "1 costing record")]
    public async Task ListOrderedByNameAsync_RefusesTheDelete_ForEachKindOfHistory(
        HistoryKind kind, string expected)
    {
        var productId = await SeedProductAsync("Coke");
        await SeedHistoryAsync(kind, productId);

        string? refusal;
        await using (var db = TestAppDbContext.For(_options, BusinessA))
        {
            var product = Assert.Single(
                await new EfProductCatalogStore(db).ListOrderedByNameAsync(WholeCatalogue(), CancellationToken.None));

            Assert.NotNull(product.DeletionEligibility);
            Assert.False(product.DeletionEligibility.Allowed);
            refusal = product.DeletionEligibility.Refusal;
        }

        Assert.NotNull(refusal);
        Assert.Contains(expected, refusal, StringComparison.Ordinal);
        Assert.Equal(await DeleteRefusalAsync(productId), refusal);
    }

    [Fact]
    public async Task ListOrderedByNameAsync_RefusesTheDelete_ForAProviderManagedProduct()
    {
        var productId = await SeedProductAsync("Coke", externalProductId: "N-1");

        string? refusal;
        await using (var db = TestAppDbContext.For(_options, BusinessA))
        {
            var product = Assert.Single(
                await new EfProductCatalogStore(db).ListOrderedByNameAsync(WholeCatalogue(), CancellationToken.None));

            Assert.NotNull(product.DeletionEligibility);
            Assert.False(product.DeletionEligibility.Allowed);
            refusal = product.DeletionEligibility.Refusal;
        }

        Assert.NotNull(refusal);
        Assert.Contains("managed by Nayax", refusal, StringComparison.Ordinal);
        Assert.Equal(await DeleteRefusalAsync(productId), refusal);
    }

    /// <summary>
    /// The counts are one grouped read per table for the whole page, so the test that matters is
    /// that they are attributed per product: one product's history must not make its neighbour
    /// undeletable, and a sale that resolved to no product at all (a null <c>ProductId</c>, issue
    /// #758) must not be counted against anything.
    /// </summary>
    [Fact]
    public async Task ListOrderedByNameAsync_AnswersEachProductFromItsOwnReferences()
    {
        var used = await SeedProductAsync("Coke");
        await SeedProductAsync("Water");
        await SeedHistoryAsync(HistoryKind.StockAdjustment, used);
        await SeedUnresolvedSaleAsync();

        await using var db = TestAppDbContext.For(_options, BusinessA);
        var products = await new EfProductCatalogStore(db)
            .ListOrderedByNameAsync(WholeCatalogue(), CancellationToken.None);

        var coke = products.Single(product => product.Name == "Coke");
        var water = products.Single(product => product.Name == "Water");
        Assert.False(coke.DeletionEligibility!.Allowed);
        Assert.Contains("1 stock movement", coke.DeletionEligibility.Refusal!, StringComparison.Ordinal);
        Assert.True(water.DeletionEligibility!.Allowed);
    }

    [Fact]
    public async Task FindAsync_AnswersTheSameEligibilityAsTheListing()
    {
        var productId = await SeedProductAsync("Coke");
        await SeedHistoryAsync(HistoryKind.PurchaseLine, productId);

        await using var db = TestAppDbContext.For(_options, BusinessA);
        var product = await new EfProductCatalogStore(db).FindAsync(productId, CancellationToken.None);

        Assert.NotNull(product);
        Assert.False(product.DeletionEligibility!.Allowed);
        Assert.Contains("1 purchase line", product.DeletionEligibility.Refusal!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The ranking read does not resolve it, and <see langword="null"/> there means "not asked",
    /// never "deletable": the reorder alerts, the Dashboard summary and the machine product views
    /// delete nothing and must not pay six counts for an answer no caller reads.
    /// </summary>
    [Fact]
    public async Task ListUnorderedAsync_LeavesTheEligibilityUnresolved()
    {
        await SeedProductAsync("Coke");

        await using var db = TestAppDbContext.For(_options, BusinessA);
        var product = Assert.Single(
            await new EfProductCatalogStore(db).ListUnorderedAsync(
                WholeCatalogue(), resolveInventoryHistory: false, CancellationToken.None));

        Assert.Null(product.DeletionEligibility);
    }

    /// <summary>
    /// The message <c>DELETE api/products/{id}</c> itself answers for this product, read through the
    /// write adapter that decides it inside the delete's own transaction.
    /// </summary>
    private async Task<string?> DeleteRefusalAsync(long productId)
    {
        await using var db = TestAppDbContext.For(_options, BusinessA);
        var attempt = await new EfProductStore(db, TestCostingUseCases.Rebuild(db))
            .DeleteAsync(productId, CancellationToken.None);
        return attempt.Refusal;
    }

    public enum HistoryKind
    {
        StockAdjustment,
        PurchaseLine,
        SupplierOrderLine,
        Sale,
        MachineStockCount,
        CostRepair,
        CostTransitionBaseline,
    }

    private async Task<long> SeedProductAsync(string name, string? externalProductId = null)
    {
        await using var seed = TestAppDbContext.For(_options, BusinessA);
        var product = new Product
        {
            Name = name,
            ExternalProductId = externalProductId,
            LowStockThreshold = 5,
            RestockTo = 10,
        };
        seed.Products.Add(product);
        await seed.SaveChangesAsync();
        return product.Id;
    }

    /// <summary>A sale that resolved to no local product, which refers to nothing and blocks nothing.</summary>
    private async Task SeedUnresolvedSaleAsync()
    {
        await using var seed = TestAppDbContext.For(_options, BusinessA);
        seed.NayaxSales.Add(new NayaxSales
        {
            TransactionID = 9100,
            MachineID = 10,
            MachineName = "Machine A",
            ProductId = null,
            SettlementValue = 3.50m,
            PaymentMethod = "Credit Card",
            MachineAuthorizationTime = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            TransactionStatusId = NayaxTransactionStatusIds.Completed,
        });
        await seed.SaveChangesAsync();
    }

    private async Task SeedHistoryAsync(HistoryKind kind, long productId)
    {
        await using var seed = TestAppDbContext.For(_options, BusinessA);
        switch (kind)
        {
            case HistoryKind.StockAdjustment:
                seed.StockAdjustments.Add(new StockAdjustment
                {
                    ProductId = productId,
                    QuantityChange = 4,
                    QuantityAfter = 4,
                    Reason = StockAdjustmentReason.Restock,
                });
                break;
            case HistoryKind.PurchaseLine:
                seed.Receipts.Add(new Purchase
                {
                    Title = "January order",
                    PurchaseDate = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc),
                    Items = { new PurchaseItem { ProductId = productId, Quantity = 10, UnitCost = 2m } },
                });
                break;
            case HistoryKind.SupplierOrderLine:
                seed.SupplierOrders.Add(new SupplierOrder
                {
                    SupplierId = null,
                    Lines = { new SupplierOrderLine { ProductId = productId, QuantityOrdered = 8 } },
                });
                break;
            case HistoryKind.Sale:
                seed.NayaxSales.Add(new NayaxSales
                {
                    TransactionID = 9001,
                    MachineID = 10,
                    MachineName = "Machine A",
                    ProductId = productId,
                    SettlementValue = 3.50m,
                    PaymentMethod = "Credit Card",
                    MachineAuthorizationTime = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
                    TransactionStatusId = NayaxTransactionStatusIds.Completed,
                });
                break;
            case HistoryKind.MachineStockCount:
                // A count names a local machine (issue #741), so the machine exists.
                seed.Machines.Add(new LocalMachine { Id = 10, Name = "Machine A" });
                seed.MachineStockCounts.Add(new MachineStockCount
                {
                    MachineId = 10,
                    ProductId = productId,
                    CountedQuantity = 6,
                    CountedAtUtc = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
                });
                break;
            case HistoryKind.CostRepair:
                seed.InventoryCostRepairs.Add(new InventoryCostRepair
                {
                    ProductId = productId,
                    Quantity = 3,
                    UnitCost = 1.50m,
                    TotalValue = 4.50m,
                    Reason = "Never recorded",
                    EffectiveAt = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
                    CreatedAt = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
                    CreatedByDirectoryTenantId = "tenant",
                    CreatedByObjectId = "object",
                });
                break;
            case HistoryKind.CostTransitionBaseline:
                seed.InventoryCostTransitionBaselines.Add(new InventoryCostTransitionBaseline
                {
                    ProductId = productId,
                    CutoffAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    OpeningCostingQuantity = 5,
                    AverageUnitCost = 1m,
                    InventoryValue = 5m,
                });
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled history kind.");
        }

        await seed.SaveChangesAsync();
    }
}
