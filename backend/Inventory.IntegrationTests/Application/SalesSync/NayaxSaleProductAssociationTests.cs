using System.Text;
using Inventory.Application.Costing;
using Inventory.Application.Imports;
using Inventory.Application.Nayax;
using Inventory.Application.Reporting.Shared;
using Inventory.Application.Reporting.ProductProfitability;
using Inventory.Domain.Providers;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Imports;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using Inventory.Infrastructure.Reporting.Persistence;
using InventoryApi.Tests.Application.SaleTimestampRepair;
using InventoryApi.Tests.Application.Time;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Application.SalesSync;

/// <summary>
/// The whole path a sale travels, over relational SQLite and the real use cases and adapters, with
/// the local product association issue #758 persists on the sale: synchronize or import, cost,
/// replay the inventory cost, report, and repair a timestamp.
///
/// <para>Every fixture is built on the shape that tells the stored association apart from the
/// coincidence it replaces. Product <c>5001</c> is the one the provider's id <c>777</c> names
/// (through <c>Product.ExternalProductId</c>, issue #759); product <c>777</c> is an
/// <strong>unrelated</strong> local product whose own key happens to be that number, with its own
/// inventory at a deliberately different unit cost. A sale costed at $2.00 therefore proves it was
/// attributed to 5001, and one costed at $9.00 would prove the opposite.</para>
/// </summary>
public class NayaxSaleProductAssociationTests : IDisposable
{
    private const int BusinessA = 1;
    private const int BusinessB = 2;
    private const long LinkedProductId = 5001;
    private const long CollidingProductId = 777;
    private const long ProviderProductId = 777;
    private const long MachineId = 1;

    private static readonly DateTime CutoffAt = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime SaleInstant = new(2026, 10, 3, 19, 30, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public NayaxSaleProductAssociationTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var setup = TestAppDbContext.Unrestricted(_options);
        setup.Database.EnsureCreated();
        setup.Businesses.Add(NayaxBusiness(BusinessA, "Vending A"));
        setup.SaveChanges();

        using var seed = TestAppDbContext.For(_options, BusinessA);
        seed.Products.AddRange(
            CostedProduct(LinkedProductId, "Snack", averageUnitCost: 2m, externalProductId: ProviderProductId),
            CostedProduct(CollidingProductId, "Unrelated snack", averageUnitCost: 9m, externalProductId: null));
        seed.InventoryCostTransitionBaselines.AddRange(
            Baseline(LinkedProductId, openingValue: 20m),
            Baseline(CollidingProductId, openingValue: 90m));
        seed.SaveChanges();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Synchronize, cost, replay, report - with a local product id that differs from its provider
    /// id, and a colliding local product that must be left completely alone.
    /// </summary>
    [Fact]
    public async Task A_synchronized_sale_costs_replays_and_reports_against_the_product_it_is_attributed_to()
    {
        await SyncAsync(providerProductId: ProviderProductId, productName: "Mystery Bar");

        await using var db = Context();
        var sale = await db.NayaxSales.AsNoTracking().SingleAsync();
        Assert.Equal(LinkedProductId, sale.ProductId);
        Assert.Equal(ProviderProductId, sale.NayaxProductId);
        Assert.True(sale.ProviderProductIdIsRaw);
        Assert.Equal(2m, sale.CostOfGoodsSold);
        Assert.Equal(SaleCostSource.InventoryLedger, sale.CostSource);

        // The replay consumed the linked product's inventory...
        var linked = await db.Products.AsNoTracking().SingleAsync(p => p.Id == LinkedProductId);
        Assert.Equal(9, linked.CostingQuantity);
        Assert.Equal(18m, linked.InventoryValue);

        // ... and the unrelated product whose key equals the provider id kept its own COGS,
        // inventory and history untouched.
        var colliding = await db.Products.AsNoTracking().SingleAsync(p => p.Id == CollidingProductId);
        Assert.Equal(10, colliding.CostingQuantity);
        Assert.Equal(90m, colliding.InventoryValue);
        Assert.Equal(9m, colliding.AverageUnitCost);

        var rows = await ProductProfitabilityRowsAsync();
        var row = Assert.Single(rows);
        Assert.Equal(LinkedProductId, row.ProductId);
        Assert.Equal("Snack", row.ProductName);
        Assert.False(row.IsUnmapped);
        Assert.Equal(2m, row.CostOfGoods);
    }

    /// <summary>
    /// Renaming a product after the sale changes nothing about which product the sale belongs to:
    /// the association was decided once and stored, so no reader can re-derive it from a name.
    /// </summary>
    [Fact]
    public async Task A_product_renamed_after_the_sale_does_not_move_the_sales_attribution()
    {
        await SyncAsync(providerProductId: ProviderProductId, productName: "Mystery Bar");

        await using (var rename = Context())
        {
            var product = await rename.Products.SingleAsync(p => p.Id == LinkedProductId);
            product.Name = "Snack (renamed)";
            // And the colliding product takes the old name, which under a name fallback would be
            // the obvious wrong answer.
            var colliding = await rename.Products.SingleAsync(p => p.Id == CollidingProductId);
            colliding.Name = "Mystery Bar";
            await rename.SaveChangesAsync();
        }

        await using var db = Context();
        Assert.Equal(LinkedProductId, (await db.NayaxSales.AsNoTracking().SingleAsync()).ProductId);
        var row = Assert.Single(await ProductProfitabilityRowsAsync());
        Assert.Equal(LinkedProductId, row.ProductId);
        Assert.Equal("Snack (renamed)", row.ProductName);
    }

    /// <summary>
    /// A payload carrying no provider product id still reaches its product by the unambiguous name
    /// at the moment it is stored, and that answer is persisted like any other.
    /// </summary>
    [Fact]
    public async Task A_payload_with_no_provider_product_id_is_attributed_by_its_unambiguous_name()
    {
        await SyncAsync(providerProductId: null, productName: "Snack");

        await using var db = Context();
        var sale = await db.NayaxSales.AsNoTracking().SingleAsync();
        Assert.Equal(LinkedProductId, sale.ProductId);
        Assert.Null(sale.NayaxProductId);
        Assert.Equal(2m, sale.CostOfGoodsSold);
    }

    /// <summary>
    /// A provider id this business recorded nowhere, with a name no product carries: unresolved,
    /// uncosted, consumed by no replay, and reported as unresolved rather than dropped.
    /// </summary>
    [Fact]
    public async Task A_name_that_cannot_rescue_the_match_leaves_the_sale_unresolved_and_visible()
    {
        await SyncAsync(providerProductId: 424242, productName: "Mystery Bar");

        await using var db = Context();
        var sale = await db.NayaxSales.AsNoTracking().SingleAsync();
        Assert.Null(sale.ProductId);
        Assert.Equal(424242, sale.NayaxProductId);
        Assert.Null(sale.CostOfGoodsSold);

        Assert.Equal(10, (await db.Products.AsNoTracking().SingleAsync(p => p.Id == LinkedProductId)).CostingQuantity);

        var row = Assert.Single(await ProductProfitabilityRowsAsync());
        Assert.True(row.IsUnmapped);
        Assert.Equal("Mystery Bar", row.ProductName);
        Assert.Equal(3m, row.Sales);
    }

    /// <summary>
    /// Another business's recorded provider id is never an answer here, and neither is its product:
    /// the sale stays unresolved and nothing of business B moves.
    /// </summary>
    [Fact]
    public async Task Another_businesss_product_is_never_the_answer()
    {
        using (var setup = TestAppDbContext.Unrestricted(_options))
        {
            setup.Businesses.Add(NayaxBusiness(BusinessB, "Vending B"));
            await setup.SaveChangesAsync();
        }

        await using (var other = TestAppDbContext.For(_options, BusinessB))
        {
            other.Products.Add(CostedProduct(6001, "Mystery Bar", averageUnitCost: 4m, externalProductId: 424242));
            await other.SaveChangesAsync();
        }

        await SyncAsync(providerProductId: 424242, productName: "Mystery Bar");

        await using var db = Context();
        Assert.Null((await db.NayaxSales.AsNoTracking().SingleAsync()).ProductId);

        await using var verify = TestAppDbContext.Unrestricted(_options);
        var otherProduct = await verify.Products.AsNoTracking().SingleAsync(p => p.Id == 6001);
        Assert.Equal(10, otherProduct.CostingQuantity);
    }

    /// <summary>
    /// The timestamp repair reads the same association: the product whose cost it would replay is
    /// the one the sale is attributed to, never the local product whose key equals the provider id.
    /// </summary>
    [Fact]
    public async Task The_timestamp_repair_replays_the_product_the_sale_is_attributed_to()
    {
        await SyncAsync(providerProductId: ProviderProductId, productName: "Mystery Bar");

        // The stored instant is moved back an hour so the repair has something to repair, and the
        // authoritative window reports the instant the sync originally wrote.
        await using (var shift = Context())
        {
            var sale = await shift.NayaxSales.SingleAsync();
            sale.MachineAuthorizationTime = SaleInstant.AddHours(-1);
            await shift.SaveChangesAsync();
        }

        var clock = new FakeClock(new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc));
        await using var db = Context();
        var preview = await TestSaleTimestampRepairUseCases
            .Preview(db, LiveWindow(), clock)
            .Handle(new(IncludeLatestSalesApiEvidence: true), null);

        var affected = Assert.Single(preview.AffectedProducts);
        Assert.Equal(LinkedProductId, affected.ProductId);
        Assert.Equal("Snack", affected.ProductName);
    }

    /// <summary>
    /// The uploaded export travels the same path, and a re-import that moves a transaction to
    /// another product replays both the product it left and the product it joined - keyed on the
    /// stored association, in one pass.
    /// </summary>
    [Fact]
    public async Task A_reimport_that_moves_a_transaction_replays_the_product_it_left_and_the_one_it_joined()
    {
        await using var db = Context();
        var replayed = new List<long>();
        var rebuild = new RecordingRebuild(TestCostingUseCases.Rebuild(db), replayed);

        await Import(db, rebuild).Handle(Csv(
            $"{SalesHeader}\n1001,12,1,{ProviderProductId},3.00,Card,Mystery Bar,3/10/2026 7:30:00 PM"));
        Assert.Equal(LinkedProductId, (await db.NayaxSales.AsNoTracking().SingleAsync()).ProductId);

        replayed.Clear();

        // The same transaction, now reported against a provider id that names no product and a
        // name that matches the other local product exactly.
        await Import(db, rebuild).Handle(Csv(
            $"{SalesHeader}\n1001,12,1,,3.00,Card,Unrelated snack,3/10/2026 7:30:00 PM"));

        var moved = await db.NayaxSales.AsNoTracking().SingleAsync();
        Assert.Equal(CollidingProductId, moved.ProductId);
        Assert.Equal([CollidingProductId, LinkedProductId], replayed.Order());
    }

    private const string SalesHeader =
        "TransactionID,TransactionStatusId,MachineID,NayaxProductId,SettlementValue,PaymentMethod,ProductName,MachineAuthorizationTime";

    private AppDbContext Context() => TestAppDbContext.For(_options, BusinessA);

    /// <summary>
    /// One synchronization of one machine reporting one completed sale, over the real
    /// <see cref="EfLatestNayaxSalesStore"/>, the real sale costing, the real inventory-cost
    /// rebuild and the real provider-id lookup, all on one context.
    /// </summary>
    private async Task SyncAsync(long? providerProductId, string productName)
    {
        await using var db = Context();
        var rebuild = TestCostingUseCases.Rebuild(db);
        var store = new EfLatestNayaxSalesStore(
            db, TestCostingUseCases.CostSale(db, rebuild), rebuild, new EfNayaxProductLinkStore(db));

        await TestSalesSync.UseCase(LiveWindow(providerProductId, productName), store).Handle();
    }

    private static LastSalesNayaxLynxClient LiveWindow(
        long? providerProductId = ProviderProductId, string productName = "Mystery Bar") =>
        new(new Dictionary<long, List<NayaxLastSalesReport>>
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
                    NayaxProductId = providerProductId,
                    ProductName = productName,
                    AuthorizationDateTimeGmt = SaleInstant,
                },
            ],
        });

    private async Task<IReadOnlyList<ProductProfitabilityRowDto>> ProductProfitabilityRowsAsync()
    {
        await using var db = Context();
        var report = await new GetProductProfitabilityReport(
                new EfProductProfitabilityReportFactsProvider(db),
                new EfProductPurchaseCostFactsProvider(db))
            .Handle(
                new ReportingFilterDto(SaleInstant.Date.AddDays(-7), SaleInstant.Date.AddDays(7)),
                CancellationToken.None);
        return report.Rows;
    }

    private static ImportNayaxSales Import(AppDbContext db, IRebuildProductCost rebuild) =>
        new(
            new ClosedXmlNayaxSalesWorkbookReader(),
            new EfNayaxSalesImportStore(db),
            TestCostingUseCases.CostSale(db, rebuild),
            rebuild,
            new EfNayaxProductLinkStore(db));

    private static NayaxSalesFileInput Csv(string content) =>
        new("sales.csv", () => new MemoryStream(Encoding.UTF8.GetBytes(content)));

    private static Product CostedProduct(
        long id, string name, decimal averageUnitCost, long? externalProductId) =>
        new()
        {
            Id = id,
            Name = name,
            ExternalProductId = externalProductId is null ? null : NayaxExternalId.From(externalProductId.Value),
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

    private static Business NayaxBusiness(int id, string name) => new()
    {
        Id = id,
        Name = name,
        Status = BusinessStatus.Active,
        SalesProvider = ExternalProvider.Nayax,
        CreatedAtUtc = DateTime.UtcNow,
    };

    /// <summary>
    /// The real rebuild, recording which products were replayed. The re-import case is about
    /// exactly that list - the product the transaction left and the product it joined - which no
    /// stored value afterwards can show.
    /// </summary>
    private sealed class RecordingRebuild(IRebuildProductCost inner, List<long> replayed) : IRebuildProductCost
    {
        public Task<InventoryCostRebuildResult> RebuildAsync(
            long productId, DateTime? from = null, bool dryRun = false, CancellationToken cancellationToken = default)
        {
            replayed.Add(productId);
            return inner.RebuildAsync(productId, from, dryRun, cancellationToken);
        }

        public Task<InventoryCostRebuildResult> RebuildCostingOnlyAsync(
            long productId, DateTime recostCompletedSalesFrom, CancellationToken cancellationToken = default)
        {
            replayed.Add(productId);
            return inner.RebuildCostingOnlyAsync(productId, recostCompletedSalesFrom, cancellationToken);
        }

        public Task<decimal?> GetAverageUnitCostAtAsync(
            long productId, DateTime at, long? excludingTransactionId = null, CancellationToken cancellationToken = default) =>
            inner.GetAverageUnitCostAtAsync(productId, at, excludingTransactionId, cancellationToken);
    }
}
