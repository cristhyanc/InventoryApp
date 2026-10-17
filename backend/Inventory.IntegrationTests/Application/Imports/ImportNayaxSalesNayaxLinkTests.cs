using System.Text;
using Inventory.Application.Costing;
using Inventory.Application.Imports;
using Inventory.Domain.Providers;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Imports;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Application.Imports;

/// <summary>
/// How the uploaded Nayax sales import resolves a row's remote <c>NayaxProductID</c> to a local
/// product (issue #745, parent #329 decision A2b): through this business's <c>ProductExternalIds</c>
/// link, read once per import by <c>INayaxProductLinkStore</c>, never by comparing the remote id
/// with <c>Product.Id</c>.
///
/// The observable effect is which product's inventory cost the import replays, because that is the
/// one decision the import makes from the match: a completed sale's product is replayed from the
/// earliest instant that touched it. Every fixture links a Nayax id to a local id with a
/// <strong>different</strong> value, which is the only shape that tells the lookup apart from the
/// coincidence it replaces, and the row's product name is deliberately one no local product carries
/// so the name fallback cannot stand in for the link.
///
/// Relational SQLite over the real import, the real EF import store and the real link store, so the
/// path asserted is the one production runs.
/// </summary>
public class ImportNayaxSalesNayaxLinkTests : IDisposable
{
    private const string SalesHeader =
        "TransactionID,TransactionStatusId,MachineID,NayaxProductId,SettlementValue,PaymentMethod,ProductName,MachineAuthorizationTime";

    /// <summary>After every fixture's transition cutoff, so a matched completed sale is replayed.</summary>
    private const string SaleRow = "1001,12,1,777,3.00,Card,Mystery Bar,3/10/2026 7:30:00 PM";

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public ImportNayaxSalesNayaxLinkTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var setup = TestAppDbContext.Unrestricted(_options);
        setup.Database.EnsureCreated();
        setup.Businesses.Add(
            new Business { Id = 1, Name = "Vending A", Status = BusinessStatus.Active, CreatedAtUtc = DateTime.UtcNow });
        setup.SaveChanges();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task A_row_is_matched_to_the_product_its_Nayax_link_names()
    {
        await using var db = TestAppDbContext.For(_options, 1);
        db.Products.Add(new Product { Id = 5001, Name = "Snack", QuantityInStock = 10 });
        db.InventoryCostTransitionBaselines.Add(Baseline(5001));
        await db.SaveChangesAsync();
        db.ProductExternalIds.Add(NayaxLink(nayaxProductId: 777, localProductId: 5001));
        await db.SaveChangesAsync();
        var rebuild = new Mock<IRebuildProductCost>();

        var result = await Import(db, rebuild.Object).Handle(Csv(SalesHeader + "\n" + SaleRow));

        Assert.Equal(new NayaxSalesImportResult(1, 0, 0), result);
        rebuild.Verify(
            x => x.RebuildAsync(5001, It.IsAny<DateTime?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Once);
        // The raw remote id is still what the row persists, exactly as before: the import stores
        // imported facts, and nothing about that column changed here.
        Assert.Equal(777, (await db.NayaxSales.AsNoTracking().SingleAsync()).NayaxProductId);
    }

    /// <summary>
    /// The other half of the rule: a local product whose own id equals the row's Nayax id, with no
    /// link to it, is not that product. The sale is still imported and stays visible, and no
    /// product's inventory cost is replayed - which is exactly how an unknown product id was
    /// already handled here.
    /// </summary>
    [Fact]
    public async Task A_local_product_whose_id_equals_an_unrelated_Nayax_id_is_not_matched()
    {
        await using var db = TestAppDbContext.For(_options, 1);
        db.Products.Add(new Product { Id = 777, Name = "Unrelated product", QuantityInStock = 10 });
        db.InventoryCostTransitionBaselines.Add(Baseline(777));
        await db.SaveChangesAsync();
        var rebuild = new Mock<IRebuildProductCost>();

        var result = await Import(db, rebuild.Object).Handle(Csv(SalesHeader + "\n" + SaleRow));

        Assert.Equal(new NayaxSalesImportResult(1, 0, 0), result);
        rebuild.Verify(
            x => x.RebuildAsync(It.IsAny<long>(), It.IsAny<DateTime?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
        var sale = await db.NayaxSales.AsNoTracking().SingleAsync();
        Assert.Equal(777, sale.NayaxProductId);
        Assert.Equal(1001, sale.TransactionID);
    }

    /// <summary>
    /// A link belongs to one business, so another business's link is not an answer here: the row's
    /// Nayax id stays unknown and nothing is replayed, which is the tenant boundary the lookup
    /// inherits from the ordinary query filters rather than from a predicate of its own.
    /// </summary>
    [Fact]
    public async Task Another_businesss_link_does_not_match_this_businesss_import()
    {
        using (var setup = TestAppDbContext.Unrestricted(_options))
        {
            setup.Businesses.Add(new Business
            {
                Id = 2,
                Name = "Vending B",
                Status = BusinessStatus.Active,
                CreatedAtUtc = DateTime.UtcNow,
            });
            await setup.SaveChangesAsync();
        }

        await using (var other = TestAppDbContext.For(_options, 2))
        {
            other.Products.Add(new Product { Id = 6001, Name = "Business B snack" });
            await other.SaveChangesAsync();
            other.ProductExternalIds.Add(NayaxLink(nayaxProductId: 777, localProductId: 6001));
            await other.SaveChangesAsync();
        }

        await using var db = TestAppDbContext.For(_options, 1);
        db.Products.Add(new Product { Id = 5001, Name = "Snack", QuantityInStock = 10 });
        db.InventoryCostTransitionBaselines.Add(Baseline(5001));
        await db.SaveChangesAsync();
        var rebuild = new Mock<IRebuildProductCost>();

        await Import(db, rebuild.Object).Handle(Csv(SalesHeader + "\n" + SaleRow));

        rebuild.Verify(
            x => x.RebuildAsync(It.IsAny<long>(), It.IsAny<DateTime?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// A row carrying no usable Nayax id at all still reaches its product by name, unchanged: the
    /// link resolution replaced the id comparison, not the fallback that follows it.
    /// </summary>
    [Fact]
    public async Task A_row_with_no_Nayax_id_still_matches_the_product_by_name()
    {
        await using var db = TestAppDbContext.For(_options, 1);
        db.Products.Add(new Product { Id = 5001, Name = "Snack", QuantityInStock = 10 });
        db.InventoryCostTransitionBaselines.Add(Baseline(5001));
        await db.SaveChangesAsync();
        var rebuild = new Mock<IRebuildProductCost>();

        await Import(db, rebuild.Object).Handle(Csv(
            SalesHeader + "\n1001,12,1,,3.00,Card,Snack,3/10/2026 7:30:00 PM"));

        rebuild.Verify(
            x => x.RebuildAsync(5001, It.IsAny<DateTime?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private static ProductExternalId NayaxLink(long nayaxProductId, long localProductId) =>
        new()
        {
            Provider = ExternalProvider.Nayax,
            ExternalId = NayaxExternalId.From(nayaxProductId),
            ProductId = localProductId,
        };

    private static InventoryCostTransitionBaseline Baseline(long productId) =>
        new()
        {
            ProductId = productId,
            HomeStockQuantity = 10,
            OpeningCostingQuantity = 10,
            InventoryValue = 20m,
            AverageUnitCost = 2m,
            CutoffAt = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
        };

    /// <summary>
    /// The import over its real workbook reader, its real EF store and the real Nayax product link
    /// store on the same context - the wiring the production container builds.
    /// </summary>
    private static ImportNayaxSales Import(AppDbContext db, IRebuildProductCost rebuild) =>
        new(
            new ClosedXmlNayaxSalesWorkbookReader(),
            new EfNayaxSalesImportStore(db),
            TestCostingUseCases.CostSale(db, rebuild),
            rebuild,
            new EfNayaxProductLinkStore(db));

    private static NayaxSalesFileInput Csv(string content) =>
        new("sales.csv", () => new MemoryStream(Encoding.UTF8.GetBytes(content)));
}
