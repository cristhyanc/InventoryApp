using Inventory.Application.Nayax;
using Inventory.Application.SaleTimestampRepair;
using Inventory.Domain.FinancialConfiguration;
using Inventory.Domain.Providers;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using InventoryApi.Tests.Application.Tenancy;
using InventoryApi.Tests.Application.Time;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Application.SaleTimestampRepair;

/// <summary>
/// How the Nayax sale timestamp repair resolves a stored sale's <c>NayaxProductId</c> to a local
/// product (issue #747, parent #329 decision A2c): through this business's <c>ProductExternalIds</c>
/// link, read by <c>INayaxProductLinkStore</c>, never by comparing the stored id with
/// <c>Product.Id</c>.
///
/// The observable effect is which product's inventory cost the repair says it would replay, and then
/// replays - the one decision the repair makes from the match, and a write to historical costing
/// data. Every fixture links a Nayax id to a local product id with a <strong>different</strong>
/// value, which is the only shape that tells the lookup apart from the coincidence it replaces, and
/// the sale's product name is deliberately one no local product carries so the name fallback cannot
/// stand in for the link.
///
/// Relational SQLite over the real preview and apply use cases, the real EF repair store, the real
/// link store and the production Sydney calendar, so the path asserted is the one production runs.
/// </summary>
public class NayaxSaleTimestampRepairNayaxLinkTests : IDisposable
{
    private const int BusinessA = 1;
    private const int BusinessB = 2;
    private const long MachineId = 531595328;
    private const long TransactionId = 3564567268;
    private const long LinkedProductId = 5001;
    private const long NayaxProductId = 777;

    /// <summary>The instant stored by the defect issue #471 fixed but did not repair.</summary>
    private static readonly DateTime StoredUtc = new(2026, 10, 7, 12, 42, 44, 263, DateTimeKind.Utc);

    /// <summary>The authoritative instant, from <c>AuthorizationDateTimeGMT</c>.</summary>
    private static readonly DateTime AuthoritativeUtc = new(2026, 10, 7, 23, 42, 44, 263, DateTimeKind.Utc);

    private static readonly DateTime CutoffAt = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime RestockAt = new(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Now = new(2026, 10, 8, 2, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly FakeClock _clock = new(Now);

    public NayaxSaleTimestampRepairNayaxLinkTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var setup = TestAppDbContext.Unrestricted(_options);
        setup.Database.EnsureCreated();
        setup.Businesses.AddRange(
            new Business { Id = BusinessA, Name = "Vending A", Status = BusinessStatus.Active, CreatedAtUtc = Now },
            new Business { Id = BusinessB, Name = "Vending B", Status = BusinessStatus.Active, CreatedAtUtc = Now });
        setup.SaveChanges();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// The repaired sale's product is the one its Nayax link names, whose local id is nothing like
    /// the Nayax id the sale stores - and the apply replays exactly that product's costing.
    /// </summary>
    [Fact]
    public async Task The_repair_replays_the_product_the_sales_Nayax_link_names()
    {
        await SeedAsync(LinkedProductId, "Snack", link: true);

        var preview = await PreviewAsync();

        var product = Assert.Single(preview.AffectedProducts);
        Assert.Equal((LinkedProductId, "Snack", StoredUtc, true), (
            product.ProductId, product.ProductName, product.RebuildFromUtc, product.RebuildPlanned));

        var applied = await ApplyAsync(preview.PreviewId);

        Assert.Equal((1, 1, 1), (applied.SalesRepaired, applied.ProductsRebuilt, applied.RecostedSales));
        await using var read = TestAppDbContext.Unrestricted(_options);
        Assert.Equal(AuthoritativeUtc, (await read.NayaxSales.AsNoTracking().SingleAsync()).MachineAuthorizationTime);
        var stored = await read.Products.AsNoTracking().SingleAsync();
        Assert.Equal((9, 18m, 2m), (stored.CostingQuantity, stored.InventoryValue, stored.AverageUnitCost));
    }

    /// <summary>
    /// The other half of the rule: a local product whose own id equals the sale's Nayax id, with no
    /// link to it, is not that product. The sale is still examined, reported and repaired - the
    /// repair's authority is one column and it is unchanged - but no product's costing is replayed,
    /// exactly as an unmatched product already produced, instead of silently restating the costing
    /// history of the row that happened to hold that number.
    /// </summary>
    [Fact]
    public async Task A_local_product_whose_id_equals_an_unlinked_Nayax_id_is_not_replayed()
    {
        await SeedAsync(NayaxProductId, "Unrelated product", link: false);

        var preview = await PreviewAsync();

        Assert.Equal(1, preview.Repairable);
        Assert.Empty(preview.AffectedProducts);

        var applied = await ApplyAsync(preview.PreviewId);

        Assert.Equal((1, 0, 0), (applied.SalesRepaired, applied.ProductsRebuilt, applied.RecostedSales));
        await using var read = TestAppDbContext.Unrestricted(_options);
        Assert.Equal(AuthoritativeUtc, (await read.NayaxSales.AsNoTracking().SingleAsync()).MachineAuthorizationTime);
        var stored = await read.Products.AsNoTracking().SingleAsync();
        Assert.Equal((10, 20m, 2m), (stored.CostingQuantity, stored.InventoryValue, stored.AverageUnitCost));
    }

    /// <summary>
    /// A link belongs to one business, by a database foreign key and not only by the query filter, so
    /// another business's link is not an answer here: the sale's Nayax id stays unknown and no
    /// product of this business is replayed.
    /// </summary>
    [Fact]
    public async Task Another_businesss_link_does_not_name_this_businesss_product()
    {
        await using (var other = TestAppDbContext.For(_options, BusinessB))
        {
            other.Products.Add(new Product { Id = 6001, Name = "Business B snack" });
            await other.SaveChangesAsync();
            other.ProductExternalIds.Add(NayaxLink(NayaxProductId, 6001));
            await other.SaveChangesAsync();
        }

        await SeedAsync(LinkedProductId, "Snack", link: false);

        var preview = await PreviewAsync();

        Assert.Equal(1, preview.Repairable);
        Assert.Empty(preview.AffectedProducts);
    }

    /// <summary>
    /// A sale carrying no Nayax id at all still reaches its product by name, unchanged: the link
    /// resolution replaced the id comparison, not the fallback that follows it.
    /// </summary>
    [Fact]
    public async Task A_sale_with_no_Nayax_id_still_replays_the_product_it_names()
    {
        await SeedAsync(LinkedProductId, "Snack", link: false, saleNayaxProductId: null, saleProductName: "Snack");

        var preview = await PreviewAsync();

        var product = Assert.Single(preview.AffectedProducts);
        Assert.Equal((LinkedProductId, true), (product.ProductId, product.RebuildPlanned));
    }

    /// <summary>
    /// Existing data is unaffected: issue #732's backfill linked every product to the row with the
    /// same id, so a sale storing that id reaches the same product and the same replay it always did.
    /// </summary>
    [Fact]
    public async Task A_product_linked_to_its_own_id_is_replayed_exactly_as_before()
    {
        await SeedAsync(LinkedProductId, "Snack", link: true, saleNayaxProductId: LinkedProductId);

        var preview = await PreviewAsync();
        var applied = await ApplyAsync(preview.PreviewId);

        var product = Assert.Single(preview.AffectedProducts);
        Assert.Equal((LinkedProductId, StoredUtc, true), (
            product.ProductId, product.RebuildFromUtc, product.RebuildPlanned));
        Assert.Equal((1, 1, 1), (applied.SalesRepaired, applied.ProductsRebuilt, applied.RecostedSales));
    }

    /// <summary>
    /// One costed product, and one stored completed sale carrying the misdated instant. The link is
    /// written only when <paramref name="link"/> says so, and always from the sale's Nayax product id
    /// to the local product - so a fixture without it is a business that holds no Nayax link for that
    /// id at all.
    /// </summary>
    private async Task SeedAsync(
        long productId,
        string productName,
        bool link,
        long? saleNayaxProductId = NayaxProductId,
        string? saleProductName = "Mystery Bar")
    {
        await using var db = TestAppDbContext.For(_options, BusinessA);
        db.Products.Add(new Product
        {
            Id = productId,
            Name = productName,
            QuantityInStock = 10,
            CostingQuantity = 10,
            InventoryValue = 20m,
            AverageUnitCost = 2m,
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
            UnitCost = 2m,
            EffectiveAt = RestockAt,
        });
        db.NayaxSales.Add(new NayaxSales
        {
            TransactionID = TransactionId,
            TransactionStatusId = NayaxTransactionStatusIds.Completed,
            MachineID = MachineId,
            MachineName = "Pavillion Right",
            SettlementValue = 3.00m,
            PaymentMethod = "Credit Card",
            NayaxProductId = saleNayaxProductId,
            ProductName = saleProductName,
            MachineAuthorizationTime = StoredUtc,
        });
        await db.SaveChangesAsync();

        if (link && saleNayaxProductId is { } nayaxProductId)
        {
            db.ProductExternalIds.Add(NayaxLink(nayaxProductId, productId));
            await db.SaveChangesAsync();
        }
    }

    private static ProductExternalId NayaxLink(long nayaxProductId, long localProductId) =>
        new()
        {
            Provider = ExternalProvider.Nayax,
            ExternalId = NayaxExternalId.From(nayaxProductId),
            ProductId = localProductId,
        };

    private async Task<NayaxSaleTimestampRepairPreview> PreviewAsync()
    {
        await using var db = TestAppDbContext.For(_options, BusinessA);
        return await TestSaleTimestampRepairUseCases.Preview(db, LiveWindow(), _clock)
            .Handle(new(IncludeLatestSalesApiEvidence: true, Reconciliation: null));
    }

    private async Task<NayaxSaleTimestampRepairApplied> ApplyAsync(Guid previewId)
    {
        await using var db = TestAppDbContext.For(_options, BusinessA);
        return await TestSaleTimestampRepairUseCases
            .Apply(db, FakeAuthenticatedActorAccessor.Identified(Identity()), _clock)
            .Handle(new(previewId, Confirmed: true));
    }

    /// <summary>The live rolling window, carrying the authoritative instant for the stored sale.</summary>
    private static LastSalesNayaxLynxClient LiveWindow() =>
        new(new Dictionary<long, List<NayaxLastSalesReport>>
        {
            [MachineId] =
            [
                new()
                {
                    TransactionID = TransactionId,
                    MachineID = MachineId,
                    MachineName = "Pavillion Right",
                    SettlementValue = 3.00m,
                    AuthorizationDateTimeGmt = new DateTimeOffset(AuthoritativeUtc, TimeSpan.Zero),
                },
            ],
        });

    private static ActorIdentity Identity()
    {
        Assert.True(ActorIdentity.TryCreate(
            "33333333-3333-3333-3333-333333333333", "44444444-4444-4444-4444-444444444444", out var actor));
        return actor!;
    }
}
