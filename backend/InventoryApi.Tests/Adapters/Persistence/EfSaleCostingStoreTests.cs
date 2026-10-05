using System.Reflection;
using Inventory.Application.Costing;
using Inventory.Domain.FinancialConfiguration;
using InventoryApi.Adapters.Persistence;
using InventoryApi.Controllers;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// The temporary API-owned <see cref="EfSaleCostingStore"/> adapter and the sale-costing endpoints
/// (issue #297) over relational SQLite. The central business query filter and the ownership stamp
/// on save (issue #64) are what keep a backfill from reading or re-costing another business's
/// sales; the adapter adds no business filter of its own.
/// </summary>
public class EfSaleCostingStoreTests
{
    private const int BusinessA = 1;
    private const int BusinessB = 2;
    private const long ProductA = 100;
    private const long ProductB = 200;
    private static readonly DateTime OpeningAt = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Backfills_read_and_recost_only_the_callers_business()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await SeedTwoBusinessesAsync(connection);

        await using (var db = TestAppDbContext.For(options, BusinessA))
        {
            var historical = await TestCostingUseCases.BackfillNayaxHistoricalSaleCosts(db).Handle(dryRun: false);
            var ledger = await TestCostingUseCases.BackfillSaleCosts(db).Handle(dryRun: false, force: true);
            var pending = await TestCostingUseCases.CostPendingSales(db).Handle();

            Assert.Equal(1, historical.SalesReviewed);
            Assert.Equal(new SaleCostingBackfillResult(1, 0, 0, 0, 0, false), ledger);
            Assert.Equal(0, pending);
        }

        await using var verify = TestAppDbContext.Unrestricted(options);
        var saleA = await verify.NayaxSales.SingleAsync(s => s.BusinessId == BusinessA);
        Assert.Equal(2m, saleA.UnitCostAtSale);
        Assert.Equal(2m, saleA.CostOfGoodsSold);
        Assert.Equal(SaleCostingStatus.Costed, saleA.CostingStatus);
        Assert.Equal(SaleCostSource.InventoryLedger, saleA.CostSource);

        // Business B's sale shares business A's transaction ID and names business A's product; it is
        // neither read nor costed by business A's backfills.
        var saleB = await verify.NayaxSales.SingleAsync(s => s.BusinessId == BusinessB);
        Assert.Null(saleB.UnitCostAtSale);
        Assert.Null(saleB.CostOfGoodsSold);
        Assert.Equal(SaleCostingStatus.Pending, saleB.CostingStatus);
        Assert.Equal(SaleCostSource.Unknown, saleB.CostSource);
    }

    [Fact]
    public async Task The_store_sees_only_the_callers_products_baselines_and_sales()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await SeedTwoBusinessesAsync(connection);

        await using var db = TestAppDbContext.For(options, BusinessB);
        var store = new EfSaleCostingStore(db);

        Assert.Equal([ProductB], (await store.GetProductCandidatesAsync(CancellationToken.None)).Select(p => p.Id));
        Assert.Null(await store.GetTransitionCutoffAsync(ProductA, CancellationToken.None));
        var sale = Assert.Single(await store.LoadCompletedSalesAsync(new CompletedSaleSelection(), forUpdate: false, CancellationToken.None));
        Assert.Equal(9m, sale.NayaxProductCostPrice);

        // Business B's sale names business A's product, which business B cannot see: unmatched, so
        // its Nayax export cost applies rather than business A's ledger.
        Assert.Equal(
            new SaleCostAssignment(9m, SaleCostStatus.Costed, SaleCostOrigin.NayaxTransactionExport),
            await TestCostingUseCases.CostSale(db).Handle(sale));
    }

    [Fact]
    public async Task A_caller_without_a_business_reads_and_costs_nothing()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await SeedTwoBusinessesAsync(connection);

        await using (var db = TestAppDbContext.Denied(options))
        {
            var store = new EfSaleCostingStore(db);
            Assert.Empty(await store.GetProductCandidatesAsync(CancellationToken.None));
            Assert.Empty(await store.LoadCompletedSalesAsync(new CompletedSaleSelection(), forUpdate: true, CancellationToken.None));
            Assert.Equal(0, (await TestCostingUseCases.BackfillNayaxHistoricalSaleCosts(db).Handle(dryRun: false)).SalesReviewed);
        }

        await using var verify = TestAppDbContext.Unrestricted(options);
        Assert.All(await verify.NayaxSales.ToListAsync(), s => Assert.Equal(SaleCostingStatus.Pending, s.CostingStatus));
    }

    [Fact]
    public async Task Dry_run_backfills_persist_nothing_and_apply_persists_with_provenance()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await SeedTwoBusinessesAsync(connection);

        await using (var db = TestAppDbContext.For(options, BusinessA))
        {
            var controller = new SaleCostingController(
                TestCostingUseCases.BackfillSaleCosts(db), TestCostingUseCases.BackfillNayaxHistoricalSaleCosts(db));

            var dryRun = Ok<NayaxCostBackfillResult>(await controller.DryRunNayaxCostBackfill());
            var ledgerDryRun = Ok<SaleCostingBackfillResult>(await controller.Backfill(dryRun: true));
            Assert.True(dryRun.DryRun);
            Assert.Equal(1, dryRun.SalesWouldBeCosted);
            Assert.True(ledgerDryRun.DryRun);
            Assert.Equal(1, ledgerDryRun.CostedCount);
            Assert.Empty(db.ChangeTracker.Entries<NayaxSales>());
        }

        await using (var verify = TestAppDbContext.Unrestricted(options))
            Assert.All(await verify.NayaxSales.ToListAsync(), s => Assert.Equal(SaleCostingStatus.Pending, s.CostingStatus));

        await using (var db = TestAppDbContext.For(options, BusinessA))
        {
            var controller = new SaleCostingController(
                TestCostingUseCases.BackfillSaleCosts(db), TestCostingUseCases.BackfillNayaxHistoricalSaleCosts(db));
            var applied = Ok<NayaxCostBackfillResult>(await controller.ApplyNayaxCostBackfill());
            Assert.False(applied.DryRun);
            Assert.Equal(1, applied.SalesWouldBeCosted);

            // Idempotent: a repeat apply finds the sale already costed and changes nothing.
            var repeated = Ok<NayaxCostBackfillResult>(await controller.ApplyNayaxCostBackfill());
            Assert.Equal(0, repeated.SalesWouldBeCosted);
            Assert.Equal(1, repeated.SalesAlreadyCosted);
        }

        await using var final = TestAppDbContext.Unrestricted(options);
        var saleA = await final.NayaxSales.SingleAsync(s => s.BusinessId == BusinessA);
        Assert.Equal(1.10m, saleA.UnitCostAtSale);
        Assert.Equal(SaleCostingStatus.Costed, saleA.CostingStatus);
        Assert.Equal(SaleCostSource.NayaxTransactionExport, saleA.CostSource);
    }

    [Fact]
    public async Task A_non_dry_run_ledger_backfill_without_force_is_still_rejected()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await SeedTwoBusinessesAsync(connection);
        await using var db = TestAppDbContext.For(options, BusinessA);
        var controller = new SaleCostingController(
            TestCostingUseCases.BackfillSaleCosts(db), TestCostingUseCases.BackfillNayaxHistoricalSaleCosts(db));

        var result = await controller.Backfill(dryRun: false, force: false);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public void The_sale_costing_routes_are_unchanged()
    {
        Assert.Equal("api/sale-costing", typeof(SaleCostingController).GetCustomAttribute<RouteAttribute>()!.Template);
        Assert.Equal("backfill", Route(nameof(SaleCostingController.Backfill)));
        Assert.Equal("nayax-cost-backfill/dry-run", Route(nameof(SaleCostingController.DryRunNayaxCostBackfill)));
        Assert.Equal("nayax-cost-backfill/apply", Route(nameof(SaleCostingController.ApplyNayaxCostBackfill)));

        static string? Route(string action) =>
            typeof(SaleCostingController).GetMethod(action)!.GetCustomAttribute<HttpPostAttribute>()!.Template;
    }

    private static T Ok<T>(ActionResult<T> result) =>
        Assert.IsType<T>(Assert.IsType<OkObjectResult>(result.Result).Value);

    private static async Task<DbContextOptions<AppDbContext>> SeedTwoBusinessesAsync(SqliteConnection connection)
    {
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using (var setup = TestAppDbContext.Unrestricted(options))
            await setup.Database.EnsureCreatedAsync();

        await using (var a = TestAppDbContext.For(options, BusinessA))
        {
            a.Products.Add(new Product { Id = ProductA, Name = "Coke Zero", QuantityInStock = 10 });
            a.StockAdjustments.Add(new StockAdjustment
            {
                ProductId = ProductA,
                QuantityChange = 10,
                QuantityAfter = 10,
                Reason = StockAdjustmentReason.Restock,
                UnitCost = 2m,
                EffectiveAt = OpeningAt,
            });
            a.NayaxSales.Add(CompletedSale(ProductA, 1.10m));
            await a.SaveChangesAsync();
        }

        await using (var b = TestAppDbContext.For(options, BusinessB))
        {
            b.Products.Add(new Product { Id = ProductB, Name = "Business B Product", QuantityInStock = 5 });
            b.NayaxSales.Add(CompletedSale(ProductA, 9m));
            await b.SaveChangesAsync();
        }

        return options;
    }

    private static NayaxSales CompletedSale(long productId, decimal nayaxCost) => new()
    {
        TransactionID = 5001,
        TransactionStatusId = NayaxTransactionStatusIds.Completed,
        MachineID = 1,
        NayaxProductId = productId,
        ProductName = "Coke Zero",
        SettlementValue = 3m,
        NayaxProductCostPrice = nayaxCost,
        MachineAuthorizationTime = OpeningAt.AddDays(1),
    };
}
