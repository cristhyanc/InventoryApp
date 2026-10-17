using Inventory.Domain.FinancialConfiguration;
using Inventory.Domain.Providers;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Application.Costing;

/// <summary>
/// How historical sale costing and the inventory-cost replay resolve a stored sale's
/// <c>NayaxProductId</c> to a local product (issue #747, parent #329 decision A2c): through this
/// business's <c>ProductExternalIds</c> link, read by <c>INayaxProductLinkStore</c>, never by
/// comparing the stored id with <c>Product.Id</c>.
///
/// Two things are observable from that one decision, and both are financial: which product's
/// inventory ledger a sale's historical cost and COGS come from, and which sales a product's cost
/// replay consumes. Every fixture here links a Nayax id to a local product id with a
/// <strong>different</strong> value, which is the only shape that tells the lookup apart from the
/// coincidence it replaces, and the sale's product name is deliberately one no local product carries
/// so the name fallback cannot stand in for the link.
///
/// Relational SQLite over the real costing use cases, the real EF costing adapters and the real link
/// store, so the path asserted is the one production runs - and so the <c>(BusinessId, ProductId)</c>
/// foreign key that keeps a link inside its own business is actually enforced.
/// </summary>
public class SaleCostingNayaxLinkTests : IDisposable
{
    private const int BusinessA = 1;
    private const int BusinessB = 2;
    private const long LinkedProductId = 5001;
    private const long NayaxProductId = 777;
    private const long TransactionId = 1001;

    /// <summary>The transition baseline cutoff: before the restock, so the replay owns the history.</summary>
    private static readonly DateTime CutoffAt = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime RestockAt = new(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime SaleAt = new(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public SaleCostingNayaxLinkTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var setup = TestAppDbContext.Unrestricted(_options);
        setup.Database.EnsureCreated();
        setup.Businesses.AddRange(
            new Business { Id = BusinessA, Name = "Vending A", Status = BusinessStatus.Active, CreatedAtUtc = CutoffAt },
            new Business { Id = BusinessB, Name = "Vending B", Status = BusinessStatus.Active, CreatedAtUtc = CutoffAt });
        setup.SaveChanges();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    // ------------------------------------------------------------ sale costing

    /// <summary>
    /// The sale's historical cost comes from the inventory ledger of the product its Nayax link
    /// names, whose local id is nothing like the Nayax id the sale stores.
    /// </summary>
    [Fact]
    public async Task A_sale_is_costed_from_the_ledger_of_the_product_its_Nayax_link_names()
    {
        await using var db = TestAppDbContext.For(_options, BusinessA);
        await SeedCostedProductAsync(db, LinkedProductId, "Snack", unitCost: 2m);
        await LinkAsync(db, NayaxProductId, LinkedProductId);
        db.NayaxSales.Add(Sale(NayaxProductId));
        await db.SaveChangesAsync();

        var costed = await TestCostingUseCases.CostPendingSales(db).Handle();

        Assert.Equal(1, costed);
        var sale = await db.NayaxSales.AsNoTracking().SingleAsync();
        Assert.Equal((2m, 2m, SaleCostingStatus.Costed, SaleCostSource.InventoryLedger), (
            sale.UnitCostAtSale, sale.CostOfGoodsSold, sale.CostingStatus, sale.CostSource));
    }

    /// <summary>
    /// The other half of the rule, and the reason this change is about financial correctness: a local
    /// product whose own id equals the sale's Nayax id, with no link to it, is not that product. The
    /// sale stays visibly uncosted - the state an unmatched product already produced - and that
    /// product's costing position is untouched, instead of its stock being consumed at its cost.
    /// </summary>
    [Fact]
    public async Task A_local_product_whose_id_equals_an_unlinked_Nayax_id_does_not_cost_the_sale()
    {
        await using var db = TestAppDbContext.For(_options, BusinessA);
        await SeedCostedProductAsync(db, NayaxProductId, "Unrelated product", unitCost: 5m);
        db.NayaxSales.Add(Sale(NayaxProductId));
        await db.SaveChangesAsync();

        var costed = await TestCostingUseCases.CostPendingSales(db).Handle();

        Assert.Equal(0, costed);
        var sale = await db.NayaxSales.AsNoTracking().SingleAsync();
        Assert.Null(sale.UnitCostAtSale);
        Assert.Null(sale.CostOfGoodsSold);
        Assert.Equal(SaleCostingStatus.Error, sale.CostingStatus);
        var product = await db.Products.AsNoTracking().SingleAsync();
        Assert.Equal((10, 50m), (product.CostingQuantity, product.InventoryValue));
    }

    /// <summary>
    /// A link belongs to one business, by a database foreign key and not only by the query filter, so
    /// another business's link is not an answer here: the sale's Nayax id stays unknown and nothing
    /// is costed from the local product that is stocked and ready to be.
    /// </summary>
    [Fact]
    public async Task Another_businesss_link_does_not_cost_this_businesss_sale()
    {
        await using (var other = TestAppDbContext.For(_options, BusinessB))
        {
            other.Products.Add(new Product { Id = 6001, Name = "Business B snack" });
            await other.SaveChangesAsync();
            await LinkAsync(other, NayaxProductId, 6001);
        }

        await using var db = TestAppDbContext.For(_options, BusinessA);
        await SeedCostedProductAsync(db, LinkedProductId, "Snack", unitCost: 2m);
        db.NayaxSales.Add(Sale(NayaxProductId));
        await db.SaveChangesAsync();

        Assert.Equal(0, await TestCostingUseCases.CostPendingSales(db).Handle());
        Assert.Equal(SaleCostingStatus.Error, (await db.NayaxSales.AsNoTracking().SingleAsync()).CostingStatus);
    }

    /// <summary>
    /// A sale carrying no Nayax id at all still reaches its product by name, unchanged: the link
    /// resolution replaced the id comparison, not the fallback that follows it.
    /// </summary>
    [Fact]
    public async Task A_sale_with_no_Nayax_id_is_still_costed_through_the_product_it_names()
    {
        await using var db = TestAppDbContext.For(_options, BusinessA);
        await SeedCostedProductAsync(db, LinkedProductId, "Snack", unitCost: 2m);
        db.NayaxSales.Add(Sale(nayaxProductId: null, productName: "Snack"));
        await db.SaveChangesAsync();

        Assert.Equal(1, await TestCostingUseCases.CostPendingSales(db).Handle());
        Assert.Equal(2m, (await db.NayaxSales.AsNoTracking().SingleAsync()).CostOfGoodsSold);
    }

    /// <summary>
    /// The acceptance criterion that nothing moves for existing data: issue #732's backfill linked
    /// every product to the row with the same id, so a fixture in that shape produces exactly the
    /// cost, COGS, provenance and costing position it produced before the lookup changed.
    /// </summary>
    [Fact]
    public async Task Cost_values_and_COGS_are_identical_for_a_product_linked_to_its_own_id()
    {
        await using var db = TestAppDbContext.For(_options, BusinessA);
        await SeedCostedProductAsync(db, LinkedProductId, "Snack", unitCost: 2m);
        await LinkAsync(db, LinkedProductId, LinkedProductId);
        db.NayaxSales.Add(Sale(LinkedProductId));
        await db.SaveChangesAsync();

        Assert.Equal(1, await TestCostingUseCases.CostPendingSales(db).Handle());
        await TestCostingUseCases.Rebuild(db).RebuildAsync(LinkedProductId, CutoffAt);
        await db.SaveChangesAsync();

        var sale = await db.NayaxSales.AsNoTracking().SingleAsync();
        Assert.Equal((2m, 2m, SaleCostingStatus.Costed, SaleCostSource.InventoryLedger), (
            sale.UnitCostAtSale, sale.CostOfGoodsSold, sale.CostingStatus, sale.CostSource));
        var product = await db.Products.AsNoTracking().SingleAsync();
        Assert.Equal((9, 18m, 2m), (product.CostingQuantity, product.InventoryValue, product.AverageUnitCost));
    }

    /// <summary>
    /// The historical Nayax-cost backfill's product filter names a local product id, so the sale
    /// whose Nayax id links to it is the one reviewed and costed from the transaction export cost.
    /// </summary>
    [Fact]
    public async Task The_historical_backfills_product_filter_selects_the_linked_sale()
    {
        await using var db = TestAppDbContext.For(_options, BusinessA);
        await SeedCostedProductAsync(db, LinkedProductId, "Snack", unitCost: 2m);
        await LinkAsync(db, NayaxProductId, LinkedProductId);
        db.NayaxSales.Add(Sale(NayaxProductId, nayaxProductCostPrice: 1.10m));
        await db.SaveChangesAsync();

        var result = await TestCostingUseCases.BackfillNayaxHistoricalSaleCosts(db)
            .Handle(dryRun: false, productId: LinkedProductId);

        Assert.Equal((1, 1), (result.SalesReviewed, result.SalesWouldBeCosted));
        var sale = await db.NayaxSales.AsNoTracking().SingleAsync();
        Assert.Equal((1.10m, SaleCostSource.NayaxTransactionExport), (sale.CostOfGoodsSold, sale.CostSource));
    }

    /// <summary>
    /// And it excludes a sale whose Nayax id merely equals the filtered product's key: the filter is
    /// about the product the link names, not about a number two identifier spaces share.
    /// </summary>
    [Fact]
    public async Task The_historical_backfills_product_filter_excludes_an_unlinked_equal_id()
    {
        await using var db = TestAppDbContext.For(_options, BusinessA);
        await SeedCostedProductAsync(db, NayaxProductId, "Unrelated product", unitCost: 5m);
        db.NayaxSales.Add(Sale(NayaxProductId, nayaxProductCostPrice: 1.10m));
        await db.SaveChangesAsync();

        var result = await TestCostingUseCases.BackfillNayaxHistoricalSaleCosts(db)
            .Handle(dryRun: false, productId: NayaxProductId);

        Assert.Equal((0, 0), (result.SalesReviewed, result.SalesWouldBeCosted));
        Assert.Null((await db.NayaxSales.AsNoTracking().SingleAsync()).CostOfGoodsSold);
    }

    // ------------------------------------------------------- inventory replay

    /// <summary>
    /// The cost replay reads the same link: the product's history consumes the sale its Nayax link
    /// names, so one unit leaves the costed inventory and the sale is recosted from the ledger.
    /// </summary>
    [Fact]
    public async Task The_cost_replay_consumes_the_sale_of_the_product_its_Nayax_link_names()
    {
        await using var db = TestAppDbContext.For(_options, BusinessA);
        await SeedCostedProductAsync(db, LinkedProductId, "Snack", unitCost: 2m);
        await LinkAsync(db, NayaxProductId, LinkedProductId);
        db.NayaxSales.Add(Sale(NayaxProductId));
        await db.SaveChangesAsync();

        var result = await TestCostingUseCases.Rebuild(db).RebuildAsync(LinkedProductId, CutoffAt);
        await db.SaveChangesAsync();

        Assert.Equal(1, result.RecostedSaleCount);
        var product = await db.Products.AsNoTracking().SingleAsync();
        Assert.Equal((9, 18m, 2m), (product.CostingQuantity, product.InventoryValue, product.AverageUnitCost));
    }

    /// <summary>
    /// A product whose own id equals a stored sale's unlinked Nayax id does not consume that sale's
    /// unit: its costing quantity and value stay exactly where they were, and nothing is recosted.
    /// Consuming it would be a silent write to historical costing data.
    /// </summary>
    [Fact]
    public async Task The_cost_replay_does_not_consume_a_sale_whose_Nayax_id_is_not_linked()
    {
        await using var db = TestAppDbContext.For(_options, BusinessA);
        await SeedCostedProductAsync(db, NayaxProductId, "Unrelated product", unitCost: 2m);
        db.NayaxSales.Add(Sale(NayaxProductId));
        await db.SaveChangesAsync();

        var result = await TestCostingUseCases.Rebuild(db).RebuildAsync(NayaxProductId, CutoffAt);
        await db.SaveChangesAsync();

        Assert.Equal(0, result.RecostedSaleCount);
        var product = await db.Products.AsNoTracking().SingleAsync();
        Assert.Equal((10, 20m, 2m), (product.CostingQuantity, product.InventoryValue, product.AverageUnitCost));
        Assert.Null((await db.NayaxSales.AsNoTracking().SingleAsync()).CostOfGoodsSold);
    }

    /// <summary>
    /// A product with ten units of costed stock at <paramref name="unitCost"/>, acquired by a restock
    /// after its transition baseline cutoff, so the replay has a complete history to cost a sale from.
    /// </summary>
    private static async Task SeedCostedProductAsync(
        AppDbContext db, long productId, string name, decimal unitCost)
    {
        db.Products.Add(new Product
        {
            Id = productId,
            Name = name,
            QuantityInStock = 10,
            CostingQuantity = 10,
            InventoryValue = 10 * unitCost,
            AverageUnitCost = unitCost,
        });
        db.InventoryCostTransitionBaselines.Add(new InventoryCostTransitionBaseline
        {
            ProductId = productId,
            CutoffAt = CutoffAt,
            HomeStockQuantity = 0,
            OpeningCostingQuantity = 0,
            InventoryValue = 0m,
            AverageUnitCost = 0m,
        });
        db.StockAdjustments.Add(new StockAdjustment
        {
            ProductId = productId,
            QuantityChange = 10,
            QuantityAfter = 10,
            Reason = StockAdjustmentReason.Restock,
            UnitCost = unitCost,
            EffectiveAt = RestockAt,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// The product's Nayax link, written after the product exists because the link's foreign key is
    /// the pair <c>(BusinessId, ProductId)</c> against the products' business key.
    /// </summary>
    private static async Task LinkAsync(AppDbContext db, long nayaxProductId, long localProductId)
    {
        db.ProductExternalIds.Add(new ProductExternalId
        {
            Provider = ExternalProvider.Nayax,
            ExternalId = NayaxExternalId.From(nayaxProductId),
            ProductId = localProductId,
        });
        await db.SaveChangesAsync();
    }

    private static NayaxSales Sale(
        long? nayaxProductId,
        string? productName = "Mystery Bar",
        decimal? nayaxProductCostPrice = null) =>
        new()
        {
            TransactionID = TransactionId,
            TransactionStatusId = NayaxTransactionStatusIds.Completed,
            MachineID = 1,
            MachineName = "Machine A",
            NayaxProductId = nayaxProductId,
            ProductName = productName,
            SettlementValue = 3.00m,
            PaymentMethod = "Credit Card",
            NayaxProductCostPrice = nayaxProductCostPrice,
            MachineAuthorizationTime = SaleAt,
        };
}
