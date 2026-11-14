using Inventory.Application.InventoryCounting;
using Inventory.Application.PickList;
using Inventory.Application.Products;
using Inventory.Application.Purchases;
using Inventory.Application.Stock;
using Inventory.Domain.Stock;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using InventoryApi.Tests.Application.Time;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;
using DomainStock = Inventory.Domain.Stock;

// Both layers declare this enum (the shared wire vocabulary documented on
// StockAdjustmentResponseMapper); the persisted rows this test reads back carry the persistence one.
using StockAdjustmentReason = Inventory.Infrastructure.Models.StockAdjustmentReason;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Issue #1238, the regression this change is about: a product created with opening stock is a
/// product every later stock write must still accept.
///
/// <para>Before the change, <c>POST api/products</c> accepted opening stock above zero with no
/// initial unit cost and recorded the opening <c>Restock</c> movement uncosted.
/// <c>WeightedAverageCostReplay</c> treats a positive <c>Restock</c> with no unit cost as a fatal
/// data-quality fault, so from then on <em>every</em> write that rebuilds that product's cost - a
/// pick-list refill, a manual adjustment, a Take Inventory apply, a new purchase - failed with
/// <c>InventoryCostDataQualityException</c>, and no ordinary screen could repair it. The create now
/// requires the cost, and this test walks the four writes that used to be frozen over one product
/// created the way the form creates it.</para>
///
/// <para>Relational (SQLite) rather than InMemory, deliberately: what it asserts is that four real
/// write transactions each commit with their cost rebuild, which is the thing the replay refused.
/// The policy's own rule and the <c>400</c> are
/// <c>InventoryApi.Tests.Domain.Products.ProductInitialCostPolicyTests</c> and
/// <c>InventoryApi.Tests.Controllers.ProductsControllerTests</c>.</para>
/// </summary>
public sealed class ProductOpeningStockLaterWritesTests : IDisposable
{
    private const int BusinessA = 1;
    private const long MachineA = 40;
    private const decimal OpeningUnitCost = 2.50m;
    private const int OpeningStock = 20;

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public ProductOpeningStockLaterWritesTests()
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

    [Fact]
    public async Task A_product_created_with_opening_stock_and_a_cost_accepts_a_refill_an_adjustment_a_count_and_a_purchase()
    {
        await SeedMachineAsync();
        var productId = await CreateProductWithOpeningStockAsync(OpeningUnitCost);

        // The opening movement carries the cost, which is what makes the four writes below
        // replayable at all.
        await using (var verify = TestAppDbContext.For(_options, BusinessA))
        {
            var opening = await verify.StockAdjustments.SingleAsync(x => x.ProductId == productId);
            Assert.Equal(StockAdjustmentReason.Restock, opening.Reason);
            Assert.Equal(OpeningUnitCost, opening.UnitCost);
            var product = await verify.Products.SingleAsync(p => p.Id == productId);
            Assert.Equal(OpeningStock, product.CostingQuantity);
            Assert.Equal(OpeningStock * OpeningUnitCost, product.InventoryValue);
        }

        // 1. A pick-list refill: five units out of storage into the machine, costing-neutral.
        await using (var db = TestAppDbContext.For(_options, BusinessA))
        {
            var refill = await new RecordPickListRefill(
                new EfPickListRefillStore(
                    db, TestCostingUseCases.RecordMovement(db), TestCostingUseCases.Rebuild(db)),
                new EfPickListStorageStockStore(db),
                new EfLocalMachineStore(db),
                new FakeClock(DateTime.UtcNow)).Handle(
                    new PickListRefillInput(
                        Guid.NewGuid().ToString(),
                        MachineA,
                        null,
                        [new PickListRefillRequestRow(productId, 5)]),
                    CancellationToken.None);

            Assert.Equal(PickListRefillOutcome.Recorded, refill.Outcome);
        }

        // 2. A manual adjustment: a Correction that writes two units off.
        await using (var db = TestAppDbContext.For(_options, BusinessA))
        {
            var adjustment = await new AdjustStock(
                new EfStockAdjustmentStore(
                    db, TestCostingUseCases.RecordMovement(db), TestCostingUseCases.Rebuild(db)),
                new EfLocalMachineStore(db)).Handle(
                    productId,
                    new ManualStockAdjustmentInput(-2, DomainStock.StockAdjustmentReason.Correction, "Damaged in storage", null, null, null),
                    CancellationToken.None);

            Assert.NotNull(adjustment);
            Assert.Equal(-2, adjustment!.QuantityChange);
        }

        // 3. A Take Inventory apply: a positive difference, costed from the suggestion the opening
        //    movement is what makes available.
        await using (var db = TestAppDbContext.For(_options, BusinessA))
        {
            var recordMovement = TestCostingUseCases.RecordMovement(db);
            var rebuild = TestCostingUseCases.Rebuild(db);
            var count = await new ApplyInventoryCount(
                new EfInventoryCountAdjustmentStore(
                    db,
                    recordMovement,
                    rebuild,
                    new GetRestockCostSuggestion(new EfStockAdjustmentStore(db, recordMovement, rebuild))))
                .Handle(
                    productId,
                    new InventoryCountApplyRequestDto(CountedStock: 16, ExpectedCurrentStock: 13),
                    CancellationToken.None);

            Assert.NotNull(count);
            Assert.Equal(InventoryCountApplyOutcome.Applied, count!.Outcome);
            Assert.Equal(3, count.QuantityChange);
            Assert.Equal(16, count.CurrentStock);
        }

        // 4. A purchase of the same product, at a different unit cost.
        await using (var db = TestAppDbContext.For(_options, BusinessA))
        {
            var purchase = await new EfPurchaseStore(db, TestCostingUseCases.Rebuild(db), TestClocks.Fixed)
                .CreateAsync(
                    new PurchaseFields("Restock", null, null, null, null, DateTime.UtcNow, null),
                    [new PurchaseItemInput(productId, 10m, 3m)],
                    new PurchaseFileMetadata("stored.jpg", "image/jpeg", "receipt.jpg", 3),
                    supplierOrderCompletion: null,
                    attachmentReservation: null,
                    CancellationToken.None);

            Assert.Single(purchase.Items);
        }

        // Every write committed, and the product's costing position is still a known one: 20 opening
        // less 5 refilled, less 2 corrected, plus 3 counted, plus 10 purchased.
        await using var after = TestAppDbContext.For(_options, BusinessA);
        var final = await after.Products.SingleAsync(p => p.Id == productId);
        Assert.Equal(26, final.QuantityInStock);
        // The refill is an internal transfer, so costing quantity keeps the five units it moved:
        // 20 - 2 + 3 + 10 (AGENTS.md § Inventory and historical costing invariants).
        Assert.Equal(31, final.CostingQuantity);
        Assert.NotNull(final.InventoryValue);
        Assert.Equal(5, await after.StockAdjustments.CountAsync(x => x.ProductId == productId));
    }

    /// <summary>
    /// The other half of the regression, and the half that fails without this change: the product
    /// whose later writes were frozen can no longer be created. The real relational create refuses
    /// opening stock with no cost and commits nothing - no product row, so no uncosted opening
    /// movement for a rebuild to refuse later.
    /// </summary>
    [Fact]
    public async Task A_create_with_opening_stock_and_no_cost_writes_no_product_and_no_movement()
    {
        await using (var db = TestAppDbContext.For(_options, BusinessA))
        {
            var result = await new CreateProduct(new EfProductStore(db, TestCostingUseCases.Rebuild(db))).Handle(
                new ProductCreateFields("Coke", "SKU-1", null, OpeningStock, 5, 30, "can", null, true, null),
                CancellationToken.None);

            Assert.False(result.IsValid);
            Assert.Equal(
                Inventory.Domain.Products.ProductInitialCostPolicy.InitialUnitCostRequiredMessage,
                result.ValidationError);
            Assert.Null(result.ProductId);
        }

        await using var verify = TestAppDbContext.For(_options, BusinessA);
        Assert.Empty(await verify.Products.ToListAsync());
        Assert.Empty(await verify.StockAdjustments.ToListAsync());
    }

    /// <summary>
    /// The same refusal when the adapter is called directly, which is the guard that keeps a future
    /// caller from writing the uncosted opening movement the use case now refuses (issue #1238).
    /// </summary>
    [Fact]
    public async Task The_adapter_itself_refuses_opening_stock_with_no_cost()
    {
        await using (var db = TestAppDbContext.For(_options, BusinessA))
        {
            var attempt = await new EfProductStore(db, TestCostingUseCases.Rebuild(db)).CreateAsync(
                new ProductCreateFields("Coke", "SKU-1", null, OpeningStock, 5, 30, "can", null, true, null),
                CancellationToken.None);

            Assert.Equal(ProductWriteOutcome.Refused, attempt.Outcome);
            Assert.Equal(
                Inventory.Domain.Products.ProductInitialCostPolicy.InitialUnitCostRequiredMessage,
                attempt.Refusal);
        }

        await using var verify = TestAppDbContext.For(_options, BusinessA);
        Assert.Empty(await verify.Products.ToListAsync());
        Assert.Empty(await verify.StockAdjustments.ToListAsync());
    }

    /// <summary>
    /// The create the product form makes, through the real use case and the real adapter:
    /// <see cref="OpeningStock"/> units at <paramref name="initialUnitCost"/>.
    /// </summary>
    private async Task<long> CreateProductWithOpeningStockAsync(decimal? initialUnitCost)
    {
        await using var db = TestAppDbContext.For(_options, BusinessA);
        var result = await new CreateProduct(new EfProductStore(db, TestCostingUseCases.Rebuild(db))).Handle(
            new ProductCreateFields(
                "Coke", "SKU-1", null, OpeningStock, 5, 30, "can", null, true, initialUnitCost),
            CancellationToken.None);

        Assert.True(result.IsValid, result.ValidationError);
        return result.ProductId!.Value;
    }

    private async Task SeedMachineAsync()
    {
        await using var seed = TestAppDbContext.For(_options, BusinessA);
        seed.Machines.Add(new LocalMachine { Id = MachineA, Name = "Machine 40" });
        await seed.SaveChangesAsync();
    }
}
