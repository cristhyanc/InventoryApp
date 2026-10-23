using Inventory.Application.Costing;
using Inventory.Application.Nayax;
using Inventory.Domain.FinancialConfiguration;
using Inventory.Domain.Providers;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Application.SalesSync;

/// <summary>
/// How the latest-sales synchronization resolves a payload item's remote <c>NayaxProductID</c> to a
/// local product (issue #745, parent #329 decision A2b): through the provider id recorded on this
/// business's product - <c>Product.ExternalProductId</c> since issue #759 - read once per batch by
/// <c>INayaxProductLinkStore</c>, never by comparing the remote id with <c>Product.Id</c>.
///
/// Two effects are observable, and both are asserted: the product id this path persists onto a
/// matched sale, and whether the sale's product has its inventory cost replayed. Every fixture
/// records a Nayax id on a local row whose key is a <strong>different</strong> value - the only
/// shape that tells the lookup apart from the coincidence it replaces - and the payload's product
/// name is one no local product carries, so the name fallback cannot stand in for the recorded id.
///
/// Relational SQLite over the real use case, the real <see cref="EfLatestNayaxSalesStore"/> and the
/// real lookup store, so the path asserted is the one production runs.
/// </summary>
public class SyncLatestNayaxSalesNayaxLinkTests : IDisposable
{
    private static readonly DateTime SaleInstant = new(2026, 10, 3, 19, 30, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public SyncLatestNayaxSalesNayaxLinkTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var setup = TestAppDbContext.Unrestricted(_options);
        setup.Database.EnsureCreated();
        setup.Businesses.Add(NayaxBusiness(TestSalesSync.BusinessId, "Vending A"));
        setup.SaveChanges();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task A_synchronized_sale_is_attached_to_the_product_that_carries_its_Nayax_id()
    {
        await using var db = Context();
        db.Products.Add(NayaxProduct(localProductId: 5001, nayaxProductId: 777, "Snack"));
        db.InventoryCostTransitionBaselines.Add(Baseline(5001));
        await db.SaveChangesAsync();
        var rebuild = new Mock<IRebuildProductCost>();

        await UseCase(db, rebuild.Object).Handle();

        // Issue #758: the matched local product goes to ProductId, and NayaxProductId keeps the
        // payload's own raw provider id, marked as such. It used to hold the local id instead.
        var sale = await db.NayaxSales.AsNoTracking().SingleAsync();
        Assert.Equal(5001, sale.ProductId);
        Assert.Equal(777, sale.NayaxProductId);
        Assert.True(sale.ProviderProductIdIsRaw);
        Assert.Equal(ExternalProvider.Nayax, sale.Provider);
        Assert.Equal(NayaxTransactionStatusIds.Completed, sale.TransactionStatusId);
        rebuild.Verify(
            x => x.RebuildAsync(5001, It.IsAny<DateTime?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// The other half of the rule: a local product whose own id equals the payload's Nayax id, with
    /// that id recorded nowhere, is not that product. The sale is still imported and stays visible with the
    /// remote id it arrived with, and no inventory cost is replayed - exactly how an unmatched
    /// product id was already handled here.
    /// </summary>
    [Fact]
    public async Task A_local_product_whose_id_equals_an_unrelated_Nayax_id_is_not_attached()
    {
        await using var db = Context();
        db.Products.Add(new Product { Id = 777, Name = "Unrelated product", QuantityInStock = 10 });
        db.InventoryCostTransitionBaselines.Add(Baseline(777));
        await db.SaveChangesAsync();
        var rebuild = new Mock<IRebuildProductCost>();

        await UseCase(db, rebuild.Object).Handle();

        var sale = await db.NayaxSales.AsNoTracking().SingleAsync();
        Assert.Null(sale.ProductId);
        Assert.Equal(777, sale.NayaxProductId);
        rebuild.Verify(
            x => x.RebuildAsync(It.IsAny<long>(), It.IsAny<DateTime?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// A recorded provider id belongs to one business's row, so another business's resolves nothing
    /// here: the payload's Nayax id stays unknown and no inventory cost is replayed. The boundary
    /// comes from the ordinary tenant query filters, not from a predicate this lookup adds.
    /// </summary>
    [Fact]
    public async Task Another_businesss_recorded_id_does_not_attach_this_businesss_sale()
    {
        using (var setup = TestAppDbContext.Unrestricted(_options))
        {
            setup.Businesses.Add(NayaxBusiness(2, "Vending B"));
            await setup.SaveChangesAsync();
        }

        await using (var other = TestAppDbContext.For(_options, 2))
        {
            other.Products.Add(NayaxProduct(localProductId: 6001, nayaxProductId: 777, "Business B snack"));
            await other.SaveChangesAsync();
        }

        await using var db = Context();
        db.Products.Add(new Product { Id = 5001, Name = "Snack", QuantityInStock = 10 });
        db.InventoryCostTransitionBaselines.Add(Baseline(5001));
        await db.SaveChangesAsync();
        var rebuild = new Mock<IRebuildProductCost>();

        await UseCase(db, rebuild.Object).Handle();

        var sale = await db.NayaxSales.AsNoTracking().SingleAsync();
        Assert.Null(sale.ProductId);
        Assert.Equal(777, sale.NayaxProductId);
        rebuild.Verify(
            x => x.RebuildAsync(It.IsAny<long>(), It.IsAny<DateTime?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// A payload item carrying no Nayax product id still reaches its product by name, unchanged: the
    /// link resolution replaced the id comparison, not the fallback that follows it.
    /// </summary>
    [Fact]
    public async Task A_payload_item_with_no_Nayax_id_still_matches_the_product_by_name()
    {
        await using var db = Context();
        db.Products.Add(new Product { Id = 5001, Name = "Snack", QuantityInStock = 10 });
        db.InventoryCostTransitionBaselines.Add(Baseline(5001));
        await db.SaveChangesAsync();
        var rebuild = new Mock<IRebuildProductCost>();

        await UseCase(db, rebuild.Object, nayaxProductId: null, productName: "Snack").Handle();

        var sale = await db.NayaxSales.AsNoTracking().SingleAsync();
        Assert.Equal(5001, sale.ProductId);
        Assert.Null(sale.NayaxProductId);
        rebuild.Verify(
            x => x.RebuildAsync(5001, It.IsAny<DateTime?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private AppDbContext Context() => TestAppDbContext.For(_options, TestSalesSync.BusinessId);

    /// <summary>
    /// The synchronization over the real store, the real link store and the real sale costing on one
    /// context, with one machine reporting one completed sale.
    /// </summary>
    private static Inventory.Application.SalesSync.SyncLatestNayaxSales UseCase(
        AppDbContext db,
        IRebuildProductCost rebuild,
        long? nayaxProductId = 777,
        string productName = "Mystery Bar")
    {
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(x => x.GetMachinesFreshAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new NayaxMachine { MachineID = 1, MachineName = "Machine A" }]);
        nayax.Setup(x => x.GetMachineLastSalesAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new NayaxLastSalesReport
                {
                    TransactionID = 100,
                    MachineID = 1,
                    MachineName = "Machine A",
                    SettlementValue = 3m,
                    PaymentMethod = "Card",
                    NayaxProductId = nayaxProductId,
                    ProductName = productName,
                    AuthorizationDateTimeGmt = SaleInstant,
                },
            ]);

        return TestSalesSync.UseCase(
            nayax.Object,
            new EfLatestNayaxSalesStore(
                db, TestCostingUseCases.CostSale(db, rebuild), rebuild, new EfNayaxProductLinkStore(db)));
    }

    /// <summary>
    /// A local product carrying a Nayax id that is deliberately not its own key (issue #759), which
    /// is the only shape that tells the recorded id apart from the coincidence it replaces.
    /// </summary>
    private static Product NayaxProduct(long localProductId, long nayaxProductId, string name) =>
        new()
        {
            Id = localProductId,
            ExternalProductId = NayaxExternalId.From(nayaxProductId),
            Name = name,
            QuantityInStock = 10,
        };

    private static Business NayaxBusiness(int id, string name) => new()
    {
        Id = id,
        Name = name,
        Status = BusinessStatus.Active,
        SalesProvider = ExternalProvider.Nayax,
        CreatedAtUtc = DateTime.UtcNow,
    };

    /// <summary>A baseline whose cutoff the fixture's sale falls after, so a matched sale replays.</summary>
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
}
