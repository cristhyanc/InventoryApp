using Inventory.Domain.FinancialConfiguration;
using Inventory.Domain.Reporting.Dashboard;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Reporting.Persistence;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Relational SQLite test: proves the projection reads the persisted <c>InventoryValue</c> column,
/// including its nullability, rather than relying on in-memory LINQ semantics that could hide a
/// translation problem.
///
/// <para>Since issue #861 it also proves which rows the adapter reads to answer whether a product has
/// any recorded inventory history - the evidence that separates a product that has never held
/// inventory from one whose inventory is unknown. That is relational by nature: the facts are rows in
/// six other tenant-owned tables.</para>
/// </summary>
public class EfInventoryValuationFactsProviderTests
{
    private const int BusinessA = 1;
    private const long MachineOfA = 1;

    private static readonly DateTime When = new(2026, 10, 7, 0, 0, 0, DateTimeKind.Utc);

    private static async Task<SqliteConnection> CreateSqliteAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var setup = TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        await setup.Database.EnsureCreatedAsync();
        return connection;
    }

    private static DbContextOptions<AppDbContext> Options(SqliteConnection connection) =>
        new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

    /// <summary>A product exactly as a create leaves it: no value, no costing position, no stock.</summary>
    private static Product FreshProduct(long id, int businessId = 0, bool isActive = true) => new()
    {
        Id = id,
        BusinessId = businessId,
        Name = $"Fresh {id}",
        IsActive = isActive,
        QuantityInStock = 0,
        CostingQuantity = null,
        InventoryValue = null,
    };

    [Fact]
    public async Task Returns_each_products_persisted_inventory_value_including_unknown()
    {
        await using var connection = await CreateSqliteAsync();
        await using var db = TestAppDbContext.Unrestricted(Options(connection));
        db.Products.AddRange(
            new Product { Id = 1, Name = "Coke", InventoryValue = 40m, CostingQuantity = 10, QuantityInStock = 10 },
            new Product { Id = 2, Name = "Chips", InventoryValue = 0m, CostingQuantity = 0, QuantityInStock = 0 },
            new Product { Id = 3, Name = "Water", InventoryValue = null, QuantityInStock = 5 });
        await db.SaveChangesAsync();
        var provider = new EfInventoryValuationFactsProvider(db);

        var facts = await provider.GetProductValuationFactsAsync(CancellationToken.None);

        Assert.Equal(3, facts.Count);
        Assert.Contains(facts, fact => fact.InventoryValue == 40m);
        Assert.Contains(facts, fact => fact.InventoryValue == 0m);
        Assert.Contains(facts, fact => fact.InventoryValue is null);
    }

    [Fact]
    public async Task No_products_returns_an_empty_list()
    {
        await using var connection = await CreateSqliteAsync();
        await using var db = TestAppDbContext.Unrestricted(Options(connection));
        var provider = new EfInventoryValuationFactsProvider(db);

        var facts = await provider.GetProductValuationFactsAsync(CancellationToken.None);

        Assert.Empty(facts);
    }

    [Fact]
    public async Task Business_scoped_context_only_sees_its_own_products()
    {
        await using var connection = await CreateSqliteAsync();
        await using var setup = TestAppDbContext.Unrestricted(Options(connection));
        setup.Products.AddRange(
            new Product
            {
                Id = 1,
                Name = "Business A product",
                InventoryValue = 40m,
                CostingQuantity = 10,
                QuantityInStock = 10,
                BusinessId = BusinessA,
            },
            new Product
            {
                Id = 2,
                Name = "Business B product",
                InventoryValue = 999m,
                CostingQuantity = 10,
                QuantityInStock = 10,
                BusinessId = 2,
            });
        await setup.SaveChangesAsync();

        await using var scopedDb = TestAppDbContext.For(Options(connection), businessId: BusinessA);
        var provider = new EfInventoryValuationFactsProvider(scopedDb);

        var facts = await provider.GetProductValuationFactsAsync(CancellationToken.None);

        Assert.Equal(40m, Assert.Single(facts).InventoryValue);
    }

    /// <summary>
    /// The product from the issue: never costed, no units in storage, and nothing anywhere that
    /// records inventory having been in it. The adapter must report that absence, which is what lets
    /// the Domain policy leave it out of the total instead of making the total unavailable.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_fresh_product_with_no_recorded_history_reports_no_inventory_history(bool isActive)
    {
        await using var connection = await CreateSqliteAsync();
        await using var db = TestAppDbContext.Unrestricted(Options(connection));
        db.Products.Add(FreshProduct(1, isActive: isActive));
        await db.SaveChangesAsync();

        var fact = Assert.Single(
            await new EfInventoryValuationFactsProvider(db).GetProductValuationFactsAsync(CancellationToken.None));

        Assert.False(fact.HasInventoryHistory);
        Assert.True(fact.HasNeverHeldInventory);
        Assert.Equal(0m, InventoryValuationPolicy.Summarize([fact]).TotalInventoryValue);
    }

    /// <summary>
    /// Every row that records inventory this product held or moved, each on its own: an empty storage
    /// shelf proves nothing while any of them exists, and the machine-held cases are the reason zero
    /// storage quantity could never have been the test.
    /// </summary>
    public static TheoryData<string> EvidenceOfInventory() => new()
    {
        "refill into a machine",
        "machine stock count",
        "cost transition baseline",
        "costing repair",
        "purchase line",
        "completed sale",
        "unprocessed machine stock event",
    };

    [Theory]
    [MemberData(nameof(EvidenceOfInventory))]
    public async Task A_product_with_zero_storage_stock_but_recorded_history_reports_inventory_history(
        string evidence)
    {
        await using var connection = await CreateSqliteAsync();
        await using var db = TestAppDbContext.Unrestricted(Options(connection));
        db.Businesses.Add(new Business
        {
            Id = BusinessA,
            Name = "Vending A",
            Status = BusinessStatus.Active,
            CreatedAtUtc = When,
        });
        db.Machines.Add(new LocalMachine { Id = MachineOfA, BusinessId = BusinessA, Name = "Machine 1" });
        db.Products.Add(FreshProduct(1, BusinessA));
        await db.SaveChangesAsync();
        AddEvidence(db, evidence);
        await db.SaveChangesAsync();

        var fact = Assert.Single(
            await new EfInventoryValuationFactsProvider(db).GetProductValuationFactsAsync(CancellationToken.None));

        Assert.True(fact.HasInventoryHistory);
        Assert.False(fact.HasNeverHeldInventory);
        Assert.Null(InventoryValuationPolicy.Summarize([fact]).TotalInventoryValue);
    }

    private static void AddEvidence(AppDbContext db, string evidence)
    {
        switch (evidence)
        {
            case "refill into a machine":
                db.StockAdjustments.Add(new StockAdjustment
                {
                    BusinessId = BusinessA,
                    ProductId = 1,
                    QuantityChange = -6,
                    QuantityAfter = 0,
                    Reason = StockAdjustmentReason.MachineRefill,
                    MachineId = MachineOfA,
                    EffectiveAt = When,
                });
                break;
            case "machine stock count":
                db.MachineStockCounts.Add(new MachineStockCount
                {
                    BusinessId = BusinessA,
                    MachineId = MachineOfA,
                    ProductId = 1,
                    CountedQuantity = 6,
                    CountedAtUtc = When,
                });
                break;
            case "cost transition baseline":
                db.InventoryCostTransitionBaselines.Add(new InventoryCostTransitionBaseline
                {
                    BusinessId = BusinessA,
                    ProductId = 1,
                    CutoffAt = When,
                    MachineStockQuantity = 6,
                    OpeningCostingQuantity = 6,
                    AverageUnitCost = 1m,
                    InventoryValue = 6m,
                    CostSource = InventoryCostBaselineSource.ManualEstimated,
                });
                break;
            case "costing repair":
                db.InventoryCostRepairs.Add(new InventoryCostRepair
                {
                    BusinessId = BusinessA,
                    ProductId = 1,
                    EffectiveAt = When,
                    Quantity = 6,
                    UnitCost = 1m,
                    TotalValue = 6m,
                    Reason = "Opening costing inventory was never recorded.",
                    CreatedAt = When,
                    CreatedByDirectoryTenantId = "tid",
                    CreatedByObjectId = "oid",
                });
                break;
            case "purchase line":
                db.Receipts.Add(new Purchase
                {
                    Id = 1,
                    BusinessId = BusinessA,
                    PurchaseDate = When,
                    Items = [new PurchaseItem { BusinessId = BusinessA, ProductId = 1, Quantity = 6, UnitCost = 1m }],
                });
                break;
            case "completed sale":
                db.NayaxSales.Add(new NayaxSales
                {
                    BusinessId = BusinessA,
                    TransactionID = 9_001,
                    MachineID = 10,
                    ProductId = 1,
                    SettlementValue = 3m,
                    PaymentMethod = "Credit",
                    MachineAuthorizationTime = When,
                    TransactionStatusId = NayaxTransactionStatusIds.Completed,
                });
                break;
            case "unprocessed machine stock event":
                db.NayaxMachineStockEvents.Add(new NayaxMachineStockEvent
                {
                    BusinessId = BusinessA,
                    NayaxEventLogId = 7_001,
                    MachineId = 10,
                    EventCode = 501,
                    EventDateTimeGmt = When,
                    ParsedQuantity = 6,
                    MatchedProductId = 1,
                });
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(evidence), evidence, "Unknown evidence kind.");
        }
    }

    /// <summary>
    /// A sale no product was ever resolved for belongs to no product (issue #758), so it is not
    /// evidence about one: it must not be read as history for the product whose local id happens to
    /// be a number, and it leaves the fresh product empty.
    /// </summary>
    [Fact]
    public async Task An_unresolved_sale_is_not_evidence_about_any_product()
    {
        await using var connection = await CreateSqliteAsync();
        await using var db = TestAppDbContext.Unrestricted(Options(connection));
        db.Products.Add(FreshProduct(1));
        db.NayaxSales.Add(new NayaxSales
        {
            TransactionID = 9_002,
            MachineID = 10,
            ProductId = null,
            NayaxProductId = 1,
            SettlementValue = 3m,
            PaymentMethod = "Credit",
            MachineAuthorizationTime = When,
            TransactionStatusId = NayaxTransactionStatusIds.Completed,
        });
        await db.SaveChangesAsync();

        var fact = Assert.Single(
            await new EfInventoryValuationFactsProvider(db).GetProductValuationFactsAsync(CancellationToken.None));

        Assert.False(fact.HasInventoryHistory);
    }

    /// <summary>
    /// The history is read under the same tenant filters as everything else: another business's
    /// movements are not this business's product's history. Both businesses here hold a product with
    /// the same local id - which is normal, since a product id is this application's own key - and
    /// only the one whose business recorded the movement has a history.
    /// </summary>
    [Fact]
    public async Task Another_businesses_stock_movement_is_not_evidence_about_this_businesses_product()
    {
        await using var connection = await CreateSqliteAsync();
        await using var setup = TestAppDbContext.Unrestricted(Options(connection));
        setup.Businesses.AddRange(
            new Business { Id = BusinessA, Name = "A", Status = BusinessStatus.Active, CreatedAtUtc = When },
            new Business { Id = 2, Name = "B", Status = BusinessStatus.Active, CreatedAtUtc = When });
        setup.Products.AddRange(FreshProduct(1, BusinessA), FreshProduct(2, businessId: 2));
        setup.StockAdjustments.Add(new StockAdjustment
        {
            BusinessId = 2,
            ProductId = 2,
            QuantityChange = -6,
            QuantityAfter = 0,
            Reason = StockAdjustmentReason.MachineRefill,
            EffectiveAt = When,
        });
        await setup.SaveChangesAsync();

        await using var scopedToA = TestAppDbContext.For(Options(connection), BusinessA);
        var factOfA = Assert.Single(
            await new EfInventoryValuationFactsProvider(scopedToA).GetProductValuationFactsAsync(
                CancellationToken.None));

        await using var scopedToB = TestAppDbContext.For(Options(connection), businessId: 2);
        var factOfB = Assert.Single(
            await new EfInventoryValuationFactsProvider(scopedToB).GetProductValuationFactsAsync(
                CancellationToken.None));

        Assert.False(factOfA.HasInventoryHistory);
        Assert.True(factOfB.HasInventoryHistory);
    }

    /// <summary>
    /// A product whose stored columns already establish a position is decided by them: the adapter
    /// reports it as having history without asking, because its value or its stock is the answer and
    /// a history read could only agree. What must never happen is the opposite - a stocked product
    /// reported as empty.
    /// </summary>
    [Fact]
    public async Task A_product_holding_stock_is_never_reported_as_having_never_held_inventory()
    {
        await using var connection = await CreateSqliteAsync();
        await using var db = TestAppDbContext.Unrestricted(Options(connection));
        db.Products.AddRange(
            new Product { Id = 1, Name = "Stocked, never costed", QuantityInStock = 4 },
            new Product { Id = 2, Name = "Costing units, no value", CostingQuantity = 4, QuantityInStock = 0 },
            new Product { Id = 3, Name = "Costed", InventoryValue = 8m, CostingQuantity = 4, QuantityInStock = 4 });
        await db.SaveChangesAsync();

        var facts = await new EfInventoryValuationFactsProvider(db)
            .GetProductValuationFactsAsync(CancellationToken.None);

        Assert.All(facts, fact => Assert.False(fact.HasNeverHeldInventory));
        var summary = InventoryValuationPolicy.Summarize(facts);
        Assert.Null(summary.TotalInventoryValue);
        Assert.Equal(2, summary.ProductsWithUnknownCost);
        Assert.Equal(3, summary.TotalProducts);
    }
}
