using Inventory.Application.SalesSync;
using Inventory.Domain.Exceptions;
using Inventory.Infrastructure.Persistence;
using Inventory.Infrastructure.Data;
using Inventory.Application.Nayax;
using Inventory.Infrastructure.Models;
using InventoryApi.Tests.Application.Nayax;
using InventoryApi.Tests.Application.SalesSync;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

using Inventory.Domain.FinancialConfiguration;
using DomainBaselineSource = Inventory.Domain.Costing.InventoryCostBaselineSource;

namespace InventoryApi.Tests.Application.Costing;

/// <summary>
/// The inventory-cost transition use cases (issue #298, child 4 of #149), moved from the former
/// <c>InventoryCostTransitionServiceTests</c> and run over the
/// <c>EfInventoryCostTransitionStore</c> adapter exactly as production composes them, together with the
/// live-sale costing behaviour that depends on a transition baseline.
///
/// <para><b>Every local product id here differs from its Nayax id</b> (issue #1256). These cases used
/// to seed a product whose local key was also its <c>NayaxProductID</c>, which made a preview that
/// matched machine slots against <c>Product.Id</c> indistinguishable from one that resolved them
/// through the provider link - and that coincidence hid a baseline being recorded without the
/// product's machine stock, or with another product's. The local keys stay 10 and 20, the Nayax ids
/// are 5001 and 5002, and <see cref="FakeNayaxProductLinkStore"/> is the link between them.</para>
/// </summary>
public class InventoryCostTransitionTests
{
    private const long SnackNayaxId = 5001;
    private const long DrinkNayaxId = 5002;

    /// <summary>Local product 10 is Nayax 5001 and local product 20 is Nayax 5002.</summary>
    private static INayaxProductLinkStore Links() =>
        FakeNayaxProductLinkStore.Linking((SnackNayaxId, 10L), (DrinkNayaxId, 20L));

    [Fact]
    public async Task Preview_uses_home_plus_each_machine_and_reports_legacy_discrepancy()
    {
        await using var db = CreateDb();
        db.Products.Add(Snack(19, 1.25m));
        db.StockAdjustments.Add(new StockAdjustment
        {
            ProductId = 10,
            QuantityChange = 31,
            Reason = StockAdjustmentReason.Restock,
            EffectiveAt = DateTime.UtcNow.AddDays(-10)
        });
        await db.SaveChangesAsync();
        var nayax = NayaxWithStock(SnackNayaxId);
        var preview = await TestCostingUseCases.PreviewTransition(db, nayax.Object, Links()).Handle(new(
            10, 1.25m, DomainBaselineSource.ManualAuthoritative));

        Assert.Equal(19, preview.HomeStockQuantity);
        Assert.Equal(11, preview.MachineStockQuantity);
        Assert.Equal(30, preview.OpeningCostingQuantity);
        Assert.Equal(37.50m, preview.InventoryValue);
        Assert.Equal(31, preview.LegacyReplayedPhysicalQuantity);
        Assert.Equal(-12, preview.LegacyPhysicalDiscrepancy);
        Assert.Collection(
            preview.MachineStocks,
            stock => Assert.Equal(6, stock.StockQuantity),
            stock => Assert.Equal(5, stock.StockQuantity));
    }

    /// <summary>
    /// Issue #1256, fixture A: the machine stock of a product whose local id and Nayax id differ.
    /// Local product 10 is Nayax 5001, so the slot reported for 5001 is this product's - 10 PAR
    /// less 4 missing, six units - and the opening baseline is 19 + 6 = 25 units at $1.25, $31.25.
    /// A preview that matched the slot against <c>Product.Id</c> found no slot for 10 at all and
    /// recorded a baseline of 19 units and $23.75.
    /// </summary>
    [Fact]
    public async Task Preview_counts_the_machine_stock_of_the_slot_carrying_the_products_nayax_id()
    {
        await using var db = CreateDb();
        db.Products.Add(Snack(19, 1.25m));
        await db.SaveChangesAsync();
        var nayax = OneMachineStocking((SnackNayaxId, 10, 4));

        var preview = await TestCostingUseCases.PreviewTransition(db, nayax, Links()).Handle(new(
            10, 1.25m, DomainBaselineSource.ManualAuthoritative));

        Assert.Equal(19, preview.HomeStockQuantity);
        Assert.Equal(6, preview.MachineStockQuantity);
        Assert.Equal(25, preview.OpeningCostingQuantity);
        Assert.Equal(31.25m, preview.InventoryValue);
        Assert.Equal(6, Assert.Single(preview.MachineStocks).StockQuantity);
    }

    /// <summary>
    /// Issue #1256, fixture B: the slot of a *different* product whose Nayax id happens to equal
    /// this product's local key is not this product's stock. The machine stocks both: Nayax 5001,
    /// which is local product 10, and Nayax 10, which is local product 7. Product 10's machine stock
    /// is the six units of its own slot - never the seven of the slot numbered 10, and not thirteen.
    /// </summary>
    [Fact]
    public async Task Preview_does_not_count_the_slot_of_another_product_whose_nayax_id_equals_the_local_id()
    {
        await using var db = CreateDb();
        db.Products.AddRange(
            Snack(19, 1.25m),
            new Product { Id = 7, Name = "Chips", ExternalProductId = "10", QuantityInStock = 2, AverageUnitCost = 1m });
        await db.SaveChangesAsync();
        var nayax = OneMachineStocking((SnackNayaxId, 10, 4), (NayaxProductId: 10, Par: 9, Missing: 2));
        var links = FakeNayaxProductLinkStore.Linking((SnackNayaxId, 10L), (10L, 7L));

        var preview = await TestCostingUseCases.PreviewTransition(db, nayax, links).Handle(new(
            10, 1.25m, DomainBaselineSource.ManualAuthoritative));

        Assert.Equal(6, preview.MachineStockQuantity);
        Assert.Equal(25, preview.OpeningCostingQuantity);
        Assert.Equal(31.25m, preview.InventoryValue);
    }

    /// <summary>
    /// Issue #1256: a product the sales provider does not name has no machine stock to resolve, and
    /// the preview says so rather than presenting an unmeasured zero as a measured one. Its opening
    /// quantity is home stock alone, which is the honest answer for a product no slot can be matched
    /// to.
    /// </summary>
    [Fact]
    public async Task Preview_of_a_product_with_no_provider_link_notes_that_machine_stock_is_unresolved()
    {
        await using var db = CreateDb();
        db.Products.Add(new Product { Id = 10, Name = "Snack", QuantityInStock = 19, AverageUnitCost = 1.25m });
        await db.SaveChangesAsync();
        var nayax = OneMachineStocking((SnackNayaxId, 10, 4));
        // No link store resolves a product that records no provider id - both sides read the same
        // column - so the product's own slot in the machine cannot be found at all.
        var links = FakeNayaxProductLinkStore.Linking();

        var preview = await TestCostingUseCases.PreviewTransition(db, nayax, links).Handle(new(
            10, 1.25m, DomainBaselineSource.ManualAuthoritative));

        Assert.Equal(0, preview.MachineStockQuantity);
        Assert.Equal(19, preview.OpeningCostingQuantity);
        Assert.Equal(23.75m, preview.InventoryValue);
        Assert.StartsWith(
            Inventory.Domain.Costing.InventoryCostTransitionPolicy.UnresolvedMachineStockNote,
            preview.DataQualityNote,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Issue #1256: a slot this business holds no link for is an unknown product and is counted for
    /// nobody - never for the product whose local key happens to be that number.
    /// </summary>
    [Fact]
    public async Task Preview_ignores_a_slot_whose_nayax_product_is_not_linked_to_any_local_product()
    {
        await using var db = CreateDb();
        db.Products.Add(Snack(19, 1.25m));
        await db.SaveChangesAsync();
        var nayax = OneMachineStocking((SnackNayaxId, 10, 4), (NayaxProductId: 10, Par: 9, Missing: 2));
        var links = FakeNayaxProductLinkStore.Linking((SnackNayaxId, 10L));

        var preview = await TestCostingUseCases.PreviewTransition(db, nayax, links).Handle(new(
            10, 1.25m, DomainBaselineSource.ManualAuthoritative));

        Assert.Equal(6, preview.MachineStockQuantity);
        Assert.Equal(25, preview.OpeningCostingQuantity);
    }

    // Issue #59: every deliberate check in these use cases is caller-facing validation, so it throws
    // the narrowly typed DomainValidationException the central handler is allowed to publish as a
    // 400. An ordinary InvalidOperationException would now become a generic logged 500 instead.
    [Fact]
    public async Task Preview_rejects_a_negative_opening_cost_with_a_domain_validation_exception()
    {
        await using var db = CreateDb();
        var preview = TestCostingUseCases.PreviewTransition(db, NayaxWithStock(SnackNayaxId).Object, Links());

        var exception = await Assert.ThrowsAsync<DomainValidationException>(() =>
            preview.Handle(new(10, -1m, DomainBaselineSource.ManualAuthoritative)));

        Assert.Equal("The opening average unit cost cannot be negative.", exception.Message);
    }

    /// <summary>
    /// Issue #833: the preview refuses incomplete Nayax slot data, and the refusal the operator
    /// sees names the product rather than only its ID. The name comes from the product the preview
    /// was asked for, which the builder already holds.
    /// </summary>
    [Fact]
    public async Task Preview_rejecting_incomplete_machine_stock_names_the_product()
    {
        await using var db = CreateDb();
        db.Products.Add(Snack(19, 1.25m));
        await db.SaveChangesAsync();
        var nayax = NayaxWithStock(SnackNayaxId);
        nayax.Setup(x => x.GetMachineProductsFreshAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxMachineProduct>
            {
                new() { NayaxProductID = SnackNayaxId, PAR = 10, MissingStockByMDB = null }
            });

        var exception = await Assert.ThrowsAsync<DomainValidationException>(() =>
            TestCostingUseCases.PreviewTransition(db, nayax.Object, Links())
                .Handle(new(10, 1.25m, DomainBaselineSource.ManualAuthoritative)));

        Assert.Equal(
            "Nayax stock is incomplete for product Snack (10) in machine Machine A: PAR and MissingStockByMDB are required.",
            exception.Message);
    }

    [Fact]
    public async Task Apply_without_confirmation_throws_a_domain_validation_exception()
    {
        await using var db = CreateDb();
        var apply = TestCostingUseCases.ApplyTransition(db, NayaxWithStock(SnackNayaxId).Object, Links());

        var exception = await Assert.ThrowsAsync<DomainValidationException>(() =>
            apply.Handle(new(Guid.NewGuid(), false)));

        Assert.Equal("Explicit confirmation is required to save the transition baseline.", exception.Message);
    }

    [Fact]
    public async Task Apply_preserves_legacy_history_and_rebuilds_only_after_cutoff()
    {
        await using var db = CreateDb();
        db.Products.Add(Snack(19, 1.25m));
        var legacyRestock = new StockAdjustment
        {
            ProductId = 10,
            QuantityChange = 31,
            Reason = StockAdjustmentReason.Restock,
            UnitCost = null,
            EffectiveAt = DateTime.UtcNow.AddDays(-10)
        };
        var historicalSale = new NayaxSales
        {
            TransactionID = 1,
            TransactionStatusId = NayaxTransactionStatusIds.Completed,
            MachineID = 1,
            ProductId = 10,
            NayaxProductId = 10,
            MachineAuthorizationTime = DateTime.UtcNow.AddDays(-5),
            NayaxProductCostPrice = 1.10m,
            UnitCostAtSale = 1.10m,
            CostOfGoodsSold = 1.10m,
            CostingStatus = SaleCostingStatus.Costed,
            CostSource = SaleCostSource.NayaxTransactionExport
        };
        db.AddRange(legacyRestock, historicalSale);
        await db.SaveChangesAsync();
        var nayax = NayaxWithStock(SnackNayaxId);
        var rebuild = TestCostingUseCases.Rebuild(db);
        var preview = await TestCostingUseCases.PreviewTransition(db, nayax.Object, Links()).Handle(new(
            10, 1.25m, DomainBaselineSource.ManualAuthoritative));

        await TestCostingUseCases.ApplyTransition(db, nayax.Object, Links(), rebuild)
            .Handle(new(preview.PreviewId, Confirmed: true));

        var baseline = await db.InventoryCostTransitionBaselines
            .Include(x => x.MachineStocks)
            .SingleAsync();
        Assert.Equal(30, baseline.OpeningCostingQuantity);
        Assert.Equal(-12, baseline.LegacyPhysicalDiscrepancy);
        Assert.Null((await db.StockAdjustments.SingleAsync()).UnitCost);
        Assert.Equal(1.10m, historicalSale.CostOfGoodsSold);
        Assert.Equal(SaleCostSource.NayaxTransactionExport, historicalSale.CostSource);

        db.StockAdjustments.Add(new StockAdjustment
        {
            ProductId = 10,
            ReceiptItemId = 99,
            QuantityChange = 10,
            Reason = StockAdjustmentReason.Restock,
            UnitCost = 2m,
            EffectiveAt = preview.CutoffAt.AddMinutes(1)
        });
        db.StockAdjustments.Add(new StockAdjustment
        {
            ProductId = 10,
            QuantityChange = -5,
            Reason = StockAdjustmentReason.MachineRefill,
            EffectiveAt = preview.CutoffAt.AddMinutes(1).AddSeconds(30)
        });
        var newSale = new NayaxSales
        {
            TransactionID = 2,
            TransactionStatusId = NayaxTransactionStatusIds.Completed,
            MachineID = 1,
            ProductId = 10,
            NayaxProductId = 10,
            MachineAuthorizationTime = preview.CutoffAt.AddMinutes(2)
        };
        db.NayaxSales.Add(newSale);
        await db.SaveChangesAsync();

        await rebuild.RebuildAsync(10, preview.CutoffAt);
        await db.SaveChangesAsync();

        var product = await db.Products.SingleAsync();
        Assert.Equal(24, product.QuantityInStock);
        Assert.Equal(39, product.CostingQuantity);
        Assert.Equal(1.4375m, newSale.CostOfGoodsSold);
        Assert.Equal(SaleCostSource.InventoryLedger, newSale.CostSource);
        Assert.Equal(1.10m, historicalSale.CostOfGoodsSold);
    }

    [Fact]
    public async Task Historical_sale_before_cutoff_uses_nayax_cost_instead_of_legacy_ledger()
    {
        await using var db = CreateDb();
        var cutoff = DateTime.UtcNow;
        db.Products.Add(new Product { Id = 10, Name = "Snack", QuantityInStock = 5, AverageUnitCost = 2m });
        db.StockAdjustments.Add(new StockAdjustment
        {
            ProductId = 10,
            QuantityChange = 10,
            Reason = StockAdjustmentReason.Restock,
            UnitCost = 2m,
            EffectiveAt = cutoff.AddDays(-2)
        });
        db.InventoryCostTransitionBaselines.Add(new InventoryCostTransitionBaseline
        {
            ProductId = 10,
            CutoffAt = cutoff,
            HomeStockQuantity = 5,
            MachineStockQuantity = 5,
            OpeningCostingQuantity = 10,
            AverageUnitCost = 2m,
            InventoryValue = 20m,
            CostSource = InventoryCostBaselineSource.ManualAuthoritative,
            DataQualityNote = "Legacy history retired."
        });
        await db.SaveChangesAsync();
        var sale = new NayaxSales
        {
            TransactionID = 3,
            TransactionStatusId = NayaxTransactionStatusIds.Completed,
            MachineID = 1,
            ProductId = 10,
            NayaxProductId = 10,
            MachineAuthorizationTime = cutoff.AddDays(-1),
            NayaxProductCostPrice = 1.10m
        };

        await TestCostingUseCases.CostSale(db).CostAsync(sale);

        Assert.Equal(1.10m, sale.CostOfGoodsSold);
        Assert.Equal(SaleCostSource.NayaxTransactionExport, sale.CostSource);
    }

    [Fact]
    public async Task Live_post_cutoff_sale_advances_business_inventory_ledger()
    {
        await using var db = CreateDb();
        var cutoff = DateTime.UtcNow.AddHours(-1);
        db.Products.Add(new Product
        {
            Id = 10,
            Name = "Snack",
            QuantityInStock = 5,
            CostingQuantity = 10,
            InventoryValue = 20m,
            AverageUnitCost = 2m
        });
        db.InventoryCostTransitionBaselines.Add(new InventoryCostTransitionBaseline
        {
            ProductId = 10,
            CutoffAt = cutoff,
            HomeStockQuantity = 5,
            MachineStockQuantity = 5,
            OpeningCostingQuantity = 10,
            AverageUnitCost = 2m,
            InventoryValue = 20m,
            CostSource = InventoryCostBaselineSource.ManualAuthoritative,
            DataQualityNote = "Transition."
        });
        await db.SaveChangesAsync();
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(x => x.GetMachinesFreshAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxMachine> { new() { MachineID = 1, MachineName = "Machine A" } });
        nayax.Setup(x => x.GetMachineLastSalesAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxLastSalesReport>
            {
                new()
                {
                    TransactionID = 4,
                    MachineID = 1,
                    ProductName = "Snack",
                    SettlementValue = 4m,
                    AuthorizationDateTimeGmt = cutoff.AddMinutes(30)
                }
            });
        nayax.Setup(x => x.GetMachineProductsFreshAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxMachineProduct>
            {
                new() { NayaxProductID = 10, ProductName = "Snack", PAR = 5, MissingStockByMDB = 1 }
            });
        var syncLatestSales = LatestSalesSync(db, nayax.Object);

        await syncLatestSales.Handle();

        var product = await db.Products.SingleAsync();
        var sale = await db.NayaxSales.SingleAsync();
        Assert.Equal(9, product.CostingQuantity);
        Assert.Equal(18m, product.InventoryValue);
        Assert.Equal(10, sale.ProductId);
        Assert.Null(sale.NayaxProductId);
        Assert.Equal(NayaxTransactionStatusIds.Completed, sale.TransactionStatusId);
        Assert.Equal(2m, sale.CostOfGoodsSold);
        Assert.Equal(SaleCostSource.InventoryLedger, sale.CostSource);
    }

    [Fact]
    public async Task Preview_and_apply_all_creates_one_atomic_baseline_per_eligible_product()
    {
        await using var db = CreateDb();
        db.Products.AddRange(
            Snack(4, 1.50m),
            new Product { Id = 20, Name = "Drink", ExternalProductId = "5002", QuantityInStock = 6, AverageUnitCost = 2.00m });
        await db.SaveChangesAsync();
        var nayax = OneMachineStocking((SnackNayaxId, 8, 3), (DrinkNayaxId, 10, 2));
        var preview = await TestCostingUseCases.PreviewAllTransitions(db, nayax, Links()).Handle(
            new(DomainBaselineSource.ManualAuthoritative));

        Assert.Equal(2, preview.ProductCount);
        Assert.Equal(10, preview.HomeStockQuantity);
        Assert.Equal(13, preview.MachineStockQuantity);
        Assert.Equal(23, preview.OpeningCostingQuantity);
        Assert.Equal(41.50m, preview.InventoryValue);

        await TestCostingUseCases.ApplyAllTransitions(db, nayax, Links()).Handle(new(preview.PreviewId, Confirmed: true));

        Assert.Equal(2, await db.InventoryCostTransitionBaselines.CountAsync());
        var products = await db.Products.OrderBy(x => x.Id).ToListAsync();
        Assert.Equal(9, products[0].CostingQuantity);
        Assert.Equal(14, products[1].CostingQuantity);
        Assert.Equal(13.50m, products[0].InventoryValue);
        Assert.Equal(28m, products[1].InventoryValue);
    }

    [Fact]
    public async Task Live_sales_defaults_missing_status_for_existing_rows_without_overwriting_known_status()
    {
        await using var db = CreateDb();
        db.NayaxSales.AddRange(
            new NayaxSales
            {
                TransactionID = 40,
                MachineID = 1,
                TransactionStatusId = null,
                NayaxProductCostPrice = 1m,
                MachineAuthorizationTime = DateTime.UtcNow
            },
            new NayaxSales
            {
                TransactionID = 41,
                MachineID = 1,
                TransactionStatusId = NayaxTransactionStatusIds.CancelledOrDeclined31,
                MachineAuthorizationTime = DateTime.UtcNow
            });
        await db.SaveChangesAsync();
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(x => x.GetMachinesFreshAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxMachine> { new() { MachineID = 1 } });
        nayax.Setup(x => x.GetMachineLastSalesAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxLastSalesReport>
            {
                new() { TransactionID = 40, MachineID = 1, AuthorizationDateTimeGmt = DateTime.UtcNow, SettlementValue = 1 },
                new() { TransactionID = 41, MachineID = 1, AuthorizationDateTimeGmt = DateTime.UtcNow, SettlementValue = 1 },
                new() { TransactionID = 42, MachineID = 1, AuthorizationDateTimeGmt = DateTime.UtcNow }
            });
        await LatestSalesSync(db, nayax.Object).Handle();

        // Looked up by the Nayax transaction id, not by the local primary key: TransactionID is
        // a remote identifier and is no longer this table's key (see NayaxSales.Id).
        Assert.Equal(
            NayaxTransactionStatusIds.Completed,
            (await SaleAsync(db, 40L)).TransactionStatusId);
        Assert.Equal(
            NayaxTransactionStatusIds.CancelledOrDeclined31,
            (await SaleAsync(db, 41L)).TransactionStatusId);
        Assert.Equal(
            NayaxTransactionStatusIds.CancelledOrDeclined250,
            (await SaleAsync(db, 42L)).TransactionStatusId);
    }

    /// <summary>
    /// The latest-sales synchronization use case (issue #187) over the real
    /// <see cref="EfLatestNayaxSalesStore"/> adapter, so the live-sale costing and ledger behaviour
    /// asserted here is exercised through the same path production uses. Costing and the store share
    /// one rebuild instance, exactly as the DI-composed graph does.
    /// </summary>
    private static SyncLatestNayaxSales LatestSalesSync(AppDbContext db, INayaxLynxClient nayax)
    {
        var rebuild = TestCostingUseCases.Rebuild(db);
        return TestSalesSync.UseCase(
            nayax,
            new EfLatestNayaxSalesStore(
                db,
                TestCostingUseCases.CostSale(db, rebuild),
                rebuild,
                // The links issue #732's backfill recorded for data that already existed - every
                // Nayax id names the local product with the same id - so these costing cases keep
                // asserting the same matches while the store resolves them through the link lookup
                // (issue #745) instead of comparing the remote id with Product.Id.
                InventoryApi.Tests.Application.Nayax.FakeNayaxProductLinkStore.EveryIdLinkedToItself(),
                TestClocks.Fixed));
    }

    private static async Task<NayaxSales> SaleAsync(AppDbContext db, long transactionId) =>
        await db.NayaxSales.SingleAsync(sale => sale.TransactionID == transactionId);

    private static AppDbContext CreateDb() =>
        TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    /// <summary>Local product 10, whose Nayax id is <see cref="SnackNayaxId"/>.</summary>
    private static Product Snack(int quantityInStock, decimal averageUnitCost) => new()
    {
        Id = 10,
        Name = "Snack",
        ExternalProductId = "5001",
        QuantityInStock = quantityInStock,
        AverageUnitCost = averageUnitCost
    };

    /// <summary>
    /// One Nayax machine stocking the given slots, each named by the <em>Nayax</em> product id it
    /// reports rather than by any local key.
    /// </summary>
    private static INayaxLynxClient OneMachineStocking(params (long NayaxProductId, int Par, int Missing)[] slots)
    {
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(x => x.GetMachinesFreshAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxMachine> { new() { MachineID = 1, MachineName = "Machine A" } });
        nayax.Setup(x => x.GetMachineProductsFreshAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(slots
                .Select(slot => new NayaxMachineProduct
                {
                    NayaxProductID = slot.NayaxProductId,
                    PAR = slot.Par,
                    MissingStockByMDB = slot.Missing
                })
                .ToList());
        return nayax.Object;
    }

    private static Mock<INayaxLynxClient> NayaxWithStock(long nayaxProductId)
    {
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(x => x.GetMachinesFreshAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxMachine>
            {
                new() { MachineID = 1, MachineName = "Machine A" },
                new() { MachineID = 2, MachineName = "Machine B" }
            });
        nayax.Setup(x => x.GetMachineProductsFreshAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxMachineProduct>
            {
                new() { NayaxProductID = nayaxProductId, PAR = 10, MissingStockByMDB = 4 }
            });
        nayax.Setup(x => x.GetMachineProductsFreshAsync(2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxMachineProduct>
            {
                new() { NayaxProductID = nayaxProductId, PAR = 8, MissingStockByMDB = 3 }
            });
        return nayax;
    }
}
