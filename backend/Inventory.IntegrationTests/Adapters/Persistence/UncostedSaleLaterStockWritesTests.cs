using Inventory.Application.InventoryCounting;
using Inventory.Application.PickList;
using Inventory.Application.Purchases;
using Inventory.Application.Stock;
using Inventory.Domain.Costing;
using Inventory.Domain.FinancialConfiguration;
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
/// Issue #1239, the regression this change is about: a business that connects Nayax with machines
/// already stocked has completed sales that predate any costed stock. The replay can cost none of
/// them - no ledger cost, no earlier costed purchase and no Nayax <c>Product Cost Price</c> - and
/// while that was a fatal <c>MissingOpening</c>/<c>UnknownCost</c>, <em>every</em> later write that
/// rebuilds the product's cost (a purchase, a pick-list refill, a manual adjustment, a Take
/// Inventory apply) was rolled back with <c>InventoryCostDataQualityException</c>. The only way out
/// was to backdate a purchase before the sale.
///
/// <para>Precedence step 4 (AGENTS.md § Inventory and historical costing invariants) leaves such a
/// sale uncosted, so the replay now reports it as the non-fatal <c>UncostedSale</c> and the four
/// writes commit. This walks them in the order the audit trigger describes: the purchase dated
/// <em>after</em> the sale first, because that is the write that used to be refused.</para>
///
/// <para>Relational (SQLite) rather than InMemory, deliberately: what it asserts is that four real
/// write transactions each commit with their cost rebuild. The replay's own rule is
/// <c>InventoryApi.Tests.Domain.Costing.WeightedAverageCostReplayTests</c>.</para>
/// </summary>
public sealed class UncostedSaleLaterStockWritesTests : IDisposable
{
    private const int BusinessA = 1;
    private const long MachineA = 40;
    private const long ProductA = 7;
    private const long SaleTransactionId = 500;
    private static readonly DateTime SaleTime = new(2026, 2, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime PurchaseDate = new(2026, 2, 10, 0, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public UncostedSaleLaterStockWritesTests()
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
    public async Task A_purchase_dated_after_an_uncosted_sale_is_saved_and_leaves_the_sale_uncosted()
    {
        await SeedProductWithUncostableSaleAsync();

        await PurchaseAsync();

        await using var verify = TestAppDbContext.For(_options, BusinessA);
        var product = await verify.Products.SingleAsync(p => p.Id == ProductA);
        Assert.Equal(10, product.QuantityInStock);
        Assert.Equal(10, product.CostingQuantity);
        Assert.Equal(25m, product.InventoryValue);
        Assert.Equal(2.50m, product.AverageUnitCost);
        var movement = await verify.StockAdjustments.SingleAsync(x => x.ProductId == ProductA);
        Assert.Equal(StockAdjustmentReason.Restock, movement.Reason);
        Assert.Equal(10, movement.CostingQuantityAfter);
        await AssertSaleIsStillUncostedAsync(verify);
    }

    /// <summary>
    /// The other three writes of the acceptance criteria, over the same product and the same
    /// uncosted sale, each in its own transaction and each rebuilding the product's cost.
    /// </summary>
    [Fact]
    public async Task A_refill_an_adjustment_and_a_Take_Inventory_apply_after_an_uncosted_sale_are_saved()
    {
        await SeedProductWithUncostableSaleAsync();
        await PurchaseAsync();

        // 1. A pick-list refill: five units out of storage into the machine, costing-neutral.
        await using (var db = TestAppDbContext.For(_options, BusinessA))
        {
            var refill = await new RecordPickListRefill(
                new EfPickListRefillStore(
                    db, TestCostingUseCases.RecordMovement(db), TestCostingUseCases.Rebuild(db)),
                new EfPickListStorageStockStore(db),
                new EfLocalMachineStore(db),
                new FakeClock(PurchaseDate.AddDays(1))).Handle(
                    new PickListRefillInput(
                        Guid.NewGuid().ToString(),
                        MachineA,
                        null,
                        [new PickListRefillRequestRow(ProductA, 5)]),
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
                    ProductA,
                    new ManualStockAdjustmentInput(-2, DomainStock.StockAdjustmentReason.Correction, "Damaged in storage", null, null, null),
                    CancellationToken.None);

            Assert.NotNull(adjustment);
            Assert.Equal(-2, adjustment!.QuantityChange);
        }

        // 3. A Take Inventory apply: three units more on the shelf than the history explains.
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
                    ProductA,
                    new InventoryCountApplyRequestDto(CountedStock: 6, ExpectedCurrentStock: 3),
                    CancellationToken.None);

            Assert.NotNull(count);
            Assert.Equal(InventoryCountApplyOutcome.Applied, count!.Outcome);
            Assert.Equal(3, count.QuantityChange);
        }

        // Every write committed: 10 purchased, less 5 refilled into the machine, less 2 corrected,
        // plus 3 counted. The refill is an internal transfer, so costing quantity keeps its five
        // units (AGENTS.md § Inventory and historical costing invariants): 10 - 2 + 3.
        await using var after = TestAppDbContext.For(_options, BusinessA);
        var product = await after.Products.SingleAsync(p => p.Id == ProductA);
        Assert.Equal(6, product.QuantityInStock);
        Assert.Equal(11, product.CostingQuantity);
        Assert.Equal(27.50m, product.InventoryValue);
        Assert.Equal(2.50m, product.AverageUnitCost);
        Assert.Equal(4, await after.StockAdjustments.CountAsync(x => x.ProductId == ProductA));
        await AssertSaleIsStillUncostedAsync(after);
    }

    /// <summary>
    /// The issue is reported where the app lists a product's cost data-quality issues, naming the
    /// product, the sale and its time, and it is not fatal - which is what a dry run can show
    /// without the rebuild throwing first.
    /// </summary>
    [Fact]
    public async Task The_rebuild_reports_the_uncosted_sale_as_a_non_fatal_issue_naming_the_sale()
    {
        await SeedProductWithUncostableSaleAsync();
        await PurchaseAsync();

        await using var db = TestAppDbContext.For(_options, BusinessA);
        var result = await TestCostingUseCases.Rebuild(db).RebuildAsync(ProductA, dryRun: true);

        var issue = Assert.Single(result.Issues);
        Assert.Equal(CostDataQualityIssueCodes.UncostedSale, issue.Code);
        Assert.False(CostDataQualityIssueCodes.IsFatal(issue.Code));
        Assert.Equal(
            $"Completed Nayax sale {SaleTransactionId} for product Coke ({ProductA}) has no known cost at {SaleTime:O} and is left uncosted.",
            issue.Message);
    }

    private async Task PurchaseAsync()
    {
        await using var db = TestAppDbContext.For(_options, BusinessA);
        var purchase = await new EfPurchaseStore(db, TestCostingUseCases.Rebuild(db), TestClocks.Fixed).CreateAsync(
            new PurchaseFields("Restock", null, null, null, null, PurchaseDate, null),
            [new PurchaseItemInput(ProductA, 10m, 2.50m)],
            new PurchaseFileMetadata("stored.jpg", "image/jpeg", "receipt.jpg", 3),
            supplierOrderCompletion: null,
            attachmentReservation: null,
            CancellationToken.None);

        Assert.Single(purchase.Items);
    }

    private static async Task AssertSaleIsStillUncostedAsync(AppDbContext db)
    {
        var sale = await db.NayaxSales.AsNoTracking().SingleAsync(x => x.TransactionID == SaleTransactionId);
        Assert.Null(sale.UnitCostAtSale);
        Assert.Null(sale.CostOfGoodsSold);
        Assert.Equal(SaleCostingStatus.Pending, sale.CostingStatus);
    }

    /// <summary>
    /// The state the audit found: no transition baseline, no costing quantity, no inventory value,
    /// nothing on the shelf, and one completed sale that no precedence step can cost.
    /// </summary>
    private async Task SeedProductWithUncostableSaleAsync()
    {
        await using var seed = TestAppDbContext.For(_options, BusinessA);
        seed.Machines.Add(new LocalMachine { Id = MachineA, Name = "Machine 40" });
        seed.Products.Add(new Product { Id = ProductA, Name = "Coke" });
        seed.NayaxSales.Add(new NayaxSales
        {
            TransactionID = SaleTransactionId,
            TransactionStatusId = NayaxTransactionStatusIds.Completed,
            MachineID = MachineA,
            ProductId = ProductA,
            NayaxProductId = 991,
            MachineAuthorizationTime = SaleTime,
        });
        await seed.SaveChangesAsync();
    }
}
