using Inventory.Application.Imports;
using Inventory.Application.Nayax;
using Inventory.Application.Reporting.ProductProfitability;
using Inventory.Application.Reporting.Shared;
using Inventory.Domain.Providers;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using Inventory.Infrastructure.Reporting.Persistence;
using InventoryApi.Tests.Application.SaleTimestampRepair;
using InventoryApi.Tests.Application.SalesSync;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Application.Imports;

/// <summary>
/// The end-to-end acceptance criterion of issue #735 (A4): a product the catalogue import created
/// with a <strong>database-generated</strong> id - a different number from its Nayax id - is
/// attributed correctly all the way through synchronize → cost → inventory cost replay → report,
/// through the <c>NayaxSales.ProductId</c> the sync persists (issue #758), with nothing anywhere
/// relying on two numbers being equal.
///
/// <para>The fixture is built to fail loudly if anything still resolves a sale by number or by name.
/// The Nayax product id is <c>888</c>, and business A also holds an <strong>unrelated</strong> local
/// product whose own key is <c>888</c>, at a deliberately different unit cost ($9.00 against $2.00)
/// and carrying the exact product name the sale reports. So a reader that compared the provider's
/// number with <c>Product.Id</c>, or fell back to the reported name, would cost the sale at $9.00
/// and consume the wrong product's inventory; the imported product's $2.00 is what proves the
/// recorded external id decided it.</para>
///
/// <para>Relational SQLite and the real use cases and adapters throughout - the catalogue import
/// store, the latest-sales sync, the sale costing, the inventory cost rebuild, the provider-id
/// lookup and the product profitability report - because the generated key itself is something only
/// a real database assigns.</para>
/// </summary>
public class ImportedProductGeneratedIdAttributionTests : IDisposable
{
    private const int BusinessA = 1;
    private const long NayaxProductId = 888;
    private const long NayaxProductGroupId = 20;
    private const long CollidingProductId = NayaxProductId;
    private const long MachineId = 1;
    private const string ReportedSaleName = "Mystery Bar";

    private static readonly DateTime CutoffAt = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime ImportedAt = new(2026, 10, 2, 2, 15, 0, DateTimeKind.Utc);
    private static readonly DateTime SaleInstant = new(2026, 10, 3, 19, 30, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public ImportedProductGeneratedIdAttributionTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var setup = TestAppDbContext.Unrestricted(_options);
        setup.Database.EnsureCreated();
        setup.Businesses.Add(new Business
        {
            Id = BusinessA,
            Name = "Vending A",
            Status = BusinessStatus.Active,
            SalesProvider = ExternalProvider.Nayax,
            CreatedAtUtc = DateTime.UtcNow,
        });
        setup.SaveChanges();

        using var seed = TestAppDbContext.For(_options, BusinessA);
        // The operator's own product, keyed by the number the Nayax product happens to carry and
        // named the way the sale will report itself. Nothing in this test may touch it.
        seed.Products.Add(CostedProduct(CollidingProductId, ReportedSaleName, averageUnitCost: 9m));
        seed.InventoryCostTransitionBaselines.Add(Baseline(CollidingProductId, openingValue: 90m));
        seed.SaveChanges();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task An_imported_product_is_attributed_through_sync_costing_replay_and_reporting()
    {
        var importedProductId = await ImportCatalogueAsync();

        // The whole point of the slice: the key is the database's own, and the Nayax id is recorded
        // beside it rather than being it.
        Assert.NotEqual(NayaxProductId, importedProductId);
        await using (var verify = TestAppDbContext.For(_options, BusinessA))
        {
            var created = await verify.Products.AsNoTracking().SingleAsync(p => p.Id == importedProductId);
            Assert.Equal(NayaxExternalId.From(NayaxProductId), created.ExternalProductId);
            var category = await verify.Categories.AsNoTracking().SingleAsync();
            Assert.NotEqual(NayaxProductGroupId, category.Id);
            Assert.Equal(category.Id, created.CategoryId);
        }

        await GiveTheImportedProductInventoryAsync(importedProductId);

        await SyncAsync();

        await using var db = TestAppDbContext.For(_options, BusinessA);

        // Sync: the sale records the local product the recorded Nayax id named, and keeps the
        // provider's own id as a provider id.
        var sale = await db.NayaxSales.AsNoTracking().SingleAsync();
        Assert.Equal(importedProductId, sale.ProductId);
        Assert.Equal(NayaxProductId, sale.NayaxProductId);
        Assert.True(sale.ProviderProductIdIsRaw);

        // Costing: from the imported product's own inventory ledger, at its unit cost - not the
        // colliding product's $9.00.
        Assert.Equal(2m, sale.CostOfGoodsSold);
        Assert.Equal(2m, sale.UnitCostAtSale);
        Assert.Equal(SaleCostSource.InventoryLedger, sale.CostSource);
        Assert.Equal(SaleCostingStatus.Costed, sale.CostingStatus);

        // Cost replay: the imported product's costing inventory is what the completed sale consumed.
        var imported = await db.Products.AsNoTracking().SingleAsync(p => p.Id == importedProductId);
        Assert.Equal(9, imported.CostingQuantity);
        Assert.Equal(18m, imported.InventoryValue);

        // And the unrelated local product whose key equals the Nayax id, and whose name the sale
        // reported, is exactly as it was.
        var colliding = await db.Products.AsNoTracking().SingleAsync(p => p.Id == CollidingProductId);
        Assert.Equal((10, 90m, 9m), (colliding.CostingQuantity, colliding.InventoryValue, colliding.AverageUnitCost));

        // Reporting: one line, against the imported product, under its own catalogue name.
        var row = Assert.Single(await ProductProfitabilityRowsAsync());
        Assert.Equal(importedProductId, row.ProductId);
        Assert.Equal("Chips", row.ProductName);
        Assert.False(row.IsUnmapped);
        Assert.Equal(2m, row.CostOfGoods);
        Assert.Equal(3m, row.Sales);
    }

    /// <summary>
    /// A second import of the same catalogue, after the sale: it finds the product it created
    /// through <c>ExternalProductId</c>, so the sale's stored attribution still names a row that
    /// exists and the report is unchanged. A re-import that created a second row would silently
    /// strand the sale's product on a duplicate.
    /// </summary>
    [Fact]
    public async Task A_reimport_keeps_the_product_the_stored_sale_is_attributed_to()
    {
        var importedProductId = await ImportCatalogueAsync();
        await GiveTheImportedProductInventoryAsync(importedProductId);
        await SyncAsync();

        Assert.Equal(importedProductId, await ImportCatalogueAsync());

        await using var db = TestAppDbContext.For(_options, BusinessA);
        Assert.Equal(importedProductId, (await db.NayaxSales.AsNoTracking().SingleAsync()).ProductId);
        Assert.Equal(2, await db.Products.CountAsync());

        var row = Assert.Single(await ProductProfitabilityRowsAsync());
        Assert.Equal(importedProductId, row.ProductId);
        Assert.Equal(2m, row.CostOfGoods);
    }

    /// <summary>
    /// One catalogue import of one group and one product, over the real store, returning the local
    /// id the database assigned the imported product - which the test only learns by reading the
    /// recorded Nayax id back, exactly as every production reader does.
    /// </summary>
    private async Task<long> ImportCatalogueAsync()
    {
        await using (var db = TestAppDbContext.For(_options, BusinessA))
        {
            await new EfNayaxProductCatalogImportStore(db).ApplyAsync(
                new NayaxProductCatalogImport(
                    [new ImportedProductCategory(NayaxProductGroupId, "Snacks", "Snacks")],
                    [new ImportedProductCatalogEntry(NayaxProductId, "Chips", "Salted", 3.50m, NayaxProductGroupId)],
                    ImportedAt),
                CancellationToken.None);
        }

        await using var verify = TestAppDbContext.For(_options, BusinessA);
        var externalId = NayaxExternalId.From(NayaxProductId);
        return (await verify.Products.AsNoTracking()
                .SingleAsync(product => product.ExternalProductId == externalId))
            .Id;
    }

    /// <summary>
    /// The imported product's starting costing inventory: ten units at $2.00, with the costing
    /// transition baseline the replay reads. The catalogue import deliberately writes none of this -
    /// stock and costing are local state it does not own - so the test records it the way the
    /// business would have.
    /// </summary>
    private async Task GiveTheImportedProductInventoryAsync(long importedProductId)
    {
        await using var db = TestAppDbContext.For(_options, BusinessA);
        var product = await db.Products.SingleAsync(p => p.Id == importedProductId);
        product.QuantityInStock = 10;
        product.CostingQuantity = 10;
        product.InventoryValue = 20m;
        product.AverageUnitCost = 2m;
        db.InventoryCostTransitionBaselines.Add(Baseline(importedProductId, openingValue: 20m));
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// One synchronization of one machine reporting one completed sale of Nayax product 888, over
    /// the real <see cref="EfLatestNayaxSalesStore"/>, sale costing, inventory cost rebuild and
    /// provider-id lookup.
    /// </summary>
    private async Task SyncAsync()
    {
        await using var db = TestAppDbContext.For(_options, BusinessA);
        var rebuild = TestCostingUseCases.Rebuild(db);
        var store = new EfLatestNayaxSalesStore(
            db, TestCostingUseCases.CostSale(db, rebuild), rebuild, new EfNayaxProductLinkStore(db));

        var nayax = new LastSalesNayaxLynxClient(new Dictionary<long, List<NayaxLastSalesReport>>
        {
            [MachineId] =
            [
                new NayaxLastSalesReport
                {
                    TransactionID = 1001,
                    MachineID = MachineId,
                    MachineName = "Machine A",
                    SettlementValue = 3m,
                    PaymentMethod = "Card",
                    NayaxProductId = NayaxProductId,
                    ProductName = ReportedSaleName,
                    AuthorizationDateTimeGmt = SaleInstant,
                },
            ],
        });

        await TestSalesSync.UseCase(nayax, store).Handle();
    }

    private async Task<IReadOnlyList<ProductProfitabilityRowDto>> ProductProfitabilityRowsAsync()
    {
        await using var db = TestAppDbContext.For(_options, BusinessA);
        var report = await new GetProductProfitabilityReport(
                new EfProductProfitabilityReportFactsProvider(db),
                new EfProductPurchaseCostFactsProvider(db))
            .Handle(
                new ReportingFilterDto(SaleInstant.Date.AddDays(-7), SaleInstant.Date.AddDays(7)),
                CancellationToken.None);
        return report.Rows;
    }

    private static Product CostedProduct(long id, string name, decimal averageUnitCost) =>
        new()
        {
            Id = id,
            Name = name,
            QuantityInStock = 10,
            CostingQuantity = 10,
            InventoryValue = averageUnitCost * 10,
            AverageUnitCost = averageUnitCost,
        };

    private static InventoryCostTransitionBaseline Baseline(long productId, decimal openingValue) =>
        new()
        {
            ProductId = productId,
            HomeStockQuantity = 10,
            OpeningCostingQuantity = 10,
            InventoryValue = openingValue,
            AverageUnitCost = openingValue / 10,
            CutoffAt = CutoffAt,
        };
}
