using Inventory.Application.Access;
using Inventory.Application.Dashboard;
using Inventory.Application.Nayax;
using Inventory.Application.Reorder;
using Inventory.Domain.FinancialConfiguration;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using InventoryApi.Tests.Application.Access;
using InventoryApi.Tests.Application.Gst;
using InventoryApi.Tests.Application.Nayax;
using InventoryApi.Tests.Application.SupportSessions;
using InventoryApi.Tests.Application.Time;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Application.Dashboard;

/// <summary>
/// The home Dashboard's inventory valuation over the rows that decide whether a product's inventory
/// is genuinely empty or genuinely unknown (issue #861), relationally, through the real
/// <see cref="EfProductCatalogStore"/> and the central <c>AppDbContext</c> query filters.
///
/// <para>The reported defect: a newly created, deactivated product with no storage units and no
/// purchase, movement, sale or costing history at all made the whole total <em>Unavailable</em>,
/// because a <c>null InventoryValue</c> was the only evidence the valuation had and it cannot tell
/// "never costed" from "costed at nothing". A product that has never held inventory owns nothing, so
/// it contributes nothing - while a product whose inventory really is unknown, including one holding
/// stock inside a machine, still makes the total unavailable.</para>
///
/// <para>These are relational rather than in-memory tests because the evidence is rows in six other
/// tables, each read under the tenant filters; an in-memory fake of the catalogue port would prove
/// nothing about which rows the adapter actually looks at.</para>
/// </summary>
public class GetDashboardSummaryEmptyProductValuationTests : IDisposable
{
    private const int BusinessA = 1;
    private const int BusinessB = 2;

    private const long CostedProduct = 100;
    private const long FreshProduct = 200;
    private const long MachineOfA = 1;

    // Wednesday 7 October 2026, 11:00 in Sydney.
    private static readonly FixedSydneyTime Time =
        new(new DateTime(2026, 10, 7, 0, 0, 0, DateTimeKind.Utc));

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public GetDashboardSummaryEmptyProductValuationTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var setup = TestAppDbContext.Unrestricted(_options);
        setup.Database.EnsureCreated();
        setup.Businesses.AddRange(
            new Business
            {
                Id = BusinessA,
                Name = "Vending A",
                Status = BusinessStatus.Active,
                CreatedAtUtc = DateTime.UtcNow,
            },
            new Business
            {
                Id = BusinessB,
                Name = "Vending B",
                Status = BusinessStatus.Active,
                CreatedAtUtc = DateTime.UtcNow,
            });
        setup.Machines.Add(new LocalMachine { Id = MachineOfA, BusinessId = BusinessA, Name = "Machine 1" });
        setup.SaveChanges();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// A normally costed product of business A: 12 units in storage, valued by a completed cost
    /// rebuild, with the restock movement that acquired them.
    /// </summary>
    private void GivenCostedStock(decimal inventoryValue = 30m)
    {
        using var setup = TestAppDbContext.Unrestricted(_options);
        setup.Products.Add(new Product
        {
            Id = CostedProduct,
            BusinessId = BusinessA,
            Name = "A-Coke",
            QuantityInStock = 12,
            CostingQuantity = 12,
            AverageUnitCost = 2.50m,
            InventoryValue = inventoryValue,
        });
        setup.StockAdjustments.Add(new StockAdjustment
        {
            BusinessId = BusinessA,
            ProductId = CostedProduct,
            QuantityChange = 12,
            QuantityAfter = 12,
            UnitCost = 2.50m,
            TotalCost = 30m,
            Reason = StockAdjustmentReason.Restock,
            EffectiveAt = Time.Window.CurrentWeek.StartUtc,
        });
        setup.SaveChanges();
    }

    /// <summary>
    /// A product exactly as a create leaves it: no persisted costing position, no storage units, and
    /// nothing anywhere that records inventory ever having been in it.
    /// </summary>
    private void GivenFreshProduct(bool isActive = true, int businessId = BusinessA, long id = FreshProduct)
    {
        using var setup = TestAppDbContext.Unrestricted(_options);
        setup.Products.Add(new Product
        {
            Id = id,
            BusinessId = businessId,
            Name = "Brand new",
            QuantityInStock = 0,
            CostingQuantity = null,
            InventoryValue = null,
            IsActive = isActive,
        });
        setup.SaveChanges();
    }

    private void Given(Action<AppDbContext> rows)
    {
        using var setup = TestAppDbContext.Unrestricted(_options);
        rows(setup);
        setup.SaveChanges();
    }

    private async Task<DashboardInventorySummaryDto> InventoryCardFor(int businessId = BusinessA)
    {
        await using var db = TestAppDbContext.For(_options, businessId);

        var outstandingOrders = new Mock<IOutstandingSupplierOrderQuantityStore>();
        outstandingOrders.Setup(x => x.GetOutstandingQuantitiesByProductAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<long, decimal>());
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(x => x.GetMachinesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var summary = await new GetDashboardSummary(
                Time.Clock,
                Time.Calendar,
                new CurrentCapabilities(
                    new FakeSupportSessionAccessor(),
                    new FakeCurrentBusinessProvider(businessId),
                    new FakeRolePreviewAccessor()),
                TestAppDbContext.ScopeFor(businessId),
                new EfDashboardSummarySalesFactsProvider(db),
                new EfProductCatalogStore(db),
                new CalculateReorderNeeds(
                    nayax.Object, outstandingOrders.Object, FakeNayaxProductLinkStore.EveryIdLinkedToItself()))
            .Handle(CancellationToken.None);

        return Assert.IsType<DashboardSummaryDto>(summary).Inventory;
    }

    /// <summary>
    /// The reported defect, as the user described it: the fresh product is deactivated, holds no
    /// units and has no history, and the business's known stock must still have a total. The product
    /// count still counts it - it is in the catalogue - which is why the count a card shows and the
    /// number of products that block the valuation are separate figures.
    /// </summary>
    [Fact]
    public async Task A_fresh_deactivated_product_with_no_history_does_not_make_the_total_unavailable()
    {
        GivenCostedStock();
        GivenFreshProduct(isActive: false);

        var inventory = await InventoryCardFor();

        Assert.Equal(30m, inventory.InventoryValueAtCost);
        Assert.True(inventory.IsInventoryValueComplete);
        Assert.Equal(0, inventory.ProductsWithUnknownCost);
        Assert.Equal(2, inventory.ProductCount);
        Assert.Equal(12, inventory.UnitsInStorage);
    }

    /// <summary>
    /// The same evidence rule applies whatever the product's active status: deactivating a product is
    /// not what makes its inventory empty, and activating one is not what makes it unknown.
    /// </summary>
    [Fact]
    public async Task A_fresh_active_product_with_no_history_contributes_nothing_and_blocks_nothing()
    {
        GivenCostedStock();
        GivenFreshProduct(isActive: true);

        var inventory = await InventoryCardFor();

        Assert.Equal(30m, inventory.InventoryValueAtCost);
        Assert.True(inventory.IsInventoryValueComplete);
        Assert.Equal(0, inventory.ProductsWithUnknownCost);
        Assert.Equal(2, inventory.ProductCount);
    }

    /// <summary>
    /// A catalogue of nothing but never-stocked products is a known <c>$0.00</c> of inventory, not an
    /// unavailable total: the business owns nothing, which is a measured answer.
    /// </summary>
    [Fact]
    public async Task A_catalogue_of_only_never_stocked_products_is_a_known_zero_total()
    {
        GivenFreshProduct(id: FreshProduct);
        GivenFreshProduct(id: FreshProduct + 1);

        var inventory = await InventoryCardFor();

        Assert.Equal(0m, inventory.InventoryValueAtCost);
        Assert.True(inventory.IsInventoryValueComplete);
        Assert.Equal(0, inventory.ProductsWithUnknownCost);
        Assert.Equal(2, inventory.ProductCount);
        Assert.Equal(0, inventory.UnitsInStorage);
    }

    /// <summary>
    /// Not all inactive products are excluded (the whole point of making this about evidence rather
    /// than status): an inactive product still holding costed stock contributes its value.
    /// </summary>
    [Fact]
    public async Task An_inactive_product_holding_costed_stock_still_contributes_its_value()
    {
        GivenCostedStock();
        Given(db =>
        {
            db.Products.Add(new Product
            {
                Id = FreshProduct,
                BusinessId = BusinessA,
                Name = "Discontinued but stocked",
                IsActive = false,
                QuantityInStock = 4,
                CostingQuantity = 4,
                InventoryValue = 8m,
            });
            db.StockAdjustments.Add(new StockAdjustment
            {
                BusinessId = BusinessA,
                ProductId = FreshProduct,
                QuantityChange = 4,
                QuantityAfter = 4,
                UnitCost = 2m,
                TotalCost = 8m,
                Reason = StockAdjustmentReason.Restock,
                EffectiveAt = Time.Window.CurrentWeek.StartUtc,
            });
        });

        var inventory = await InventoryCardFor();

        Assert.Equal(38m, inventory.InventoryValueAtCost);
        Assert.True(inventory.IsInventoryValueComplete);
        Assert.Equal(16, inventory.UnitsInStorage);
    }

    /// <summary>
    /// And an inactive product holding stock whose value was never established still makes the total
    /// unavailable: its inventory is unknown, and retiring it did not value it.
    /// </summary>
    [Fact]
    public async Task An_inactive_product_holding_stock_of_unknown_value_still_blocks_the_total()
    {
        GivenCostedStock();
        Given(db => db.Products.Add(new Product
        {
            Id = FreshProduct,
            BusinessId = BusinessA,
            Name = "Discontinued, never costed",
            IsActive = false,
            QuantityInStock = 4,
            CostingQuantity = null,
            InventoryValue = null,
        }));

        var inventory = await InventoryCardFor();

        Assert.Null(inventory.InventoryValueAtCost);
        Assert.False(inventory.IsInventoryValueComplete);
        Assert.Equal(1, inventory.ProductsWithUnknownCost);
        Assert.Equal(2, inventory.ProductCount);
    }

    /// <summary>
    /// Zero storage quantity is not evidence on its own: these are the rows that record inventory
    /// this product held or moved even though the storage shelf is empty, and every one of them must
    /// keep the total unavailable. Machine-held stock is the dangerous case - the units are in a
    /// machine, so <c>QuantityInStock</c> is 0 and the valuation would silently drop them.
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
    public async Task Zero_storage_stock_with_recorded_inventory_history_still_blocks_the_total(string evidence)
    {
        GivenCostedStock();
        GivenFreshProduct();
        Given(db => AddEvidence(db, evidence));

        var inventory = await InventoryCardFor();

        Assert.Null(inventory.InventoryValueAtCost);
        Assert.False(inventory.IsInventoryValueComplete);
        Assert.Equal(1, inventory.ProductsWithUnknownCost);
        Assert.Equal(2, inventory.ProductCount);
    }

    private static void AddEvidence(AppDbContext db, string evidence)
    {
        switch (evidence)
        {
            case "refill into a machine":
                // Units left storage for a machine: the shelf is empty and the business still owns them.
                db.StockAdjustments.Add(new StockAdjustment
                {
                    BusinessId = BusinessA,
                    ProductId = FreshProduct,
                    QuantityChange = -6,
                    QuantityAfter = 0,
                    Reason = StockAdjustmentReason.MachineRefill,
                    MachineId = MachineOfA,
                    EffectiveAt = Time.Window.CurrentWeek.StartUtc,
                });
                break;
            case "machine stock count":
                db.MachineStockCounts.Add(new MachineStockCount
                {
                    BusinessId = BusinessA,
                    MachineId = MachineOfA,
                    ProductId = FreshProduct,
                    CountedQuantity = 6,
                    CountedAtUtc = Time.Window.CurrentWeek.StartUtc,
                });
                break;
            case "cost transition baseline":
                db.InventoryCostTransitionBaselines.Add(new InventoryCostTransitionBaseline
                {
                    BusinessId = BusinessA,
                    ProductId = FreshProduct,
                    CutoffAt = Time.Window.CurrentWeek.StartUtc,
                    HomeStockQuantity = 0,
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
                    ProductId = FreshProduct,
                    EffectiveAt = Time.Window.CurrentWeek.StartUtc,
                    Quantity = 6,
                    UnitCost = 1m,
                    TotalValue = 6m,
                    Reason = "Opening costing inventory was never recorded.",
                    CreatedAt = Time.Window.CurrentWeek.StartUtc,
                    CreatedByDirectoryTenantId = "tid",
                    CreatedByObjectId = "oid",
                });
                break;
            case "purchase line":
                db.Receipts.Add(new Purchase
                {
                    Id = 1,
                    BusinessId = BusinessA,
                    PurchaseDate = Time.Window.CurrentWeek.StartUtc,
                    Items =
                    [
                        new PurchaseItem
                        {
                            BusinessId = BusinessA, ProductId = FreshProduct, Quantity = 6, UnitCost = 1m,
                        },
                    ],
                });
                break;
            case "completed sale":
                db.NayaxSales.Add(new NayaxSales
                {
                    BusinessId = BusinessA,
                    TransactionID = 9_001,
                    MachineID = 10,
                    ProductId = FreshProduct,
                    SettlementValue = 3m,
                    PaymentMethod = "Credit",
                    MachineAuthorizationTime = Time.Window.CurrentWeek.StartUtc,
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
                    EventDateTimeGmt = Time.Window.CurrentWeek.StartUtc,
                    ParsedQuantity = 6,
                    MatchedProductId = FreshProduct,
                });
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(evidence), evidence, "Unknown evidence kind.");
        }
    }

    /// <summary>
    /// The evidence is read under the tenant filters like everything else: another business's
    /// movements, sales and counts are not this business's product's history, and must neither
    /// resurrect its unknown cost nor leak into its total.
    /// </summary>
    [Fact]
    public async Task Another_businesses_history_is_not_evidence_about_this_businesses_product()
    {
        GivenCostedStock();
        GivenFreshProduct();
        GivenFreshProduct(businessId: BusinessB, id: FreshProduct + 1);
        Given(db =>
        {
            // Business B's own machine, count and sale, for a product B owns - the same shapes that
            // make A's fresh product unknown above.
            db.Machines.Add(new LocalMachine { Id = MachineOfA + 1, BusinessId = BusinessB, Name = "B machine" });
            db.MachineStockCounts.Add(new MachineStockCount
            {
                BusinessId = BusinessB,
                MachineId = MachineOfA + 1,
                ProductId = FreshProduct + 1,
                CountedQuantity = 6,
                CountedAtUtc = Time.Window.CurrentWeek.StartUtc,
            });
        });

        var businessA = await InventoryCardFor();
        var businessB = await InventoryCardFor(BusinessB);

        Assert.Equal(30m, businessA.InventoryValueAtCost);
        Assert.True(businessA.IsInventoryValueComplete);
        Assert.Equal(2, businessA.ProductCount);

        // B's own product does hold machine stock of unknown value, so B's total is unavailable.
        Assert.Null(businessB.InventoryValueAtCost);
        Assert.False(businessB.IsInventoryValueComplete);
        Assert.Equal(1, businessB.ProductsWithUnknownCost);
        Assert.Equal(1, businessB.ProductCount);
    }
}
