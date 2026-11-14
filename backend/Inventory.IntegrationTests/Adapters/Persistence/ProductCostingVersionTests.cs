using Inventory.Application.Purchases;
using Inventory.Application.Stock;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// <c>Product.CostingVersion</c> moves in the same transaction as every rebuilt costing position
/// (issue #1245, finding INV-08 of audit #1222).
///
/// <para><strong>What it is for.</strong> The sales cost-rebuild drain replays a product's pending
/// rebuild <em>outside</em> SQLite's write lock and acknowledges its claim afterwards, and the pending
/// row's <c>Revision</c> only answers for work another <em>sale</em> added. A purchase, a correction or
/// a count committed during that replay changes the history the staged figures were computed from and
/// leaves that row untouched, so the drain needs something on the product itself to compare.
/// <c>NayaxSqsConcurrencyTests</c> covers what the drain then does with it; this covers the half it
/// depends on - that each of those writers really does move it.</para>
///
/// <para><strong>Why one seam covers all of them.</strong> Every stock write, purchase create,
/// purchase edit, purchase delete, count and refill ends in a cost rebuild, and every rebuild stages
/// the product's position through the one method that writes it,
/// <c>EfInventoryCostLedgerStore.StageProductPosition</c>. The version moves there, so a writer cannot
/// rebuild a product's cost and forget to say so. These cases are the ones the issue names - a
/// purchase create, a purchase <em>edit</em> that changes a movement in place and adds none, a purchase
/// delete and a manual stock write - over relational SQLite and the real replay.</para>
///
/// <para>The assertions are that the value <em>changed</em>, never what it is: that is the whole of
/// what an acknowledgement compares, and a rule about the exact count of rebuilds one write performs
/// is not something this column promises.</para>
/// </summary>
public sealed class ProductCostingVersionTests : IDisposable
{
    private const int BusinessId = 1;
    private const long ProductId = 1;

    /// <summary>The transition baseline's cutoff, before every write below, so the replay runs.</summary>
    private static readonly DateTime CutoffAt = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime PurchaseDate = new(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public ProductCostingVersionTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using (var setup = TestAppDbContext.Unrestricted(_options))
        {
            setup.Database.EnsureCreated();
            setup.Businesses.Add(new Business
            {
                Id = BusinessId,
                Name = "Fictional Vending",
                Status = BusinessStatus.Active,
                CreatedAtUtc = CutoffAt,
            });
            setup.SaveChanges();
        }

        // Seeded as the business, so the central ownership stamp gives the product and its baseline
        // the owner every write below is scoped to - exactly as a real one arrives.
        using var seed = TestAppDbContext.For(_options, BusinessId);
        seed.Products.Add(new Product
        {
            Id = ProductId,
            Name = "Fictional Spring Water 600ml",
            QuantityInStock = 10,
            CostingQuantity = 10,
            InventoryValue = 20m,
            AverageUnitCost = 2m,
        });
        seed.InventoryCostTransitionBaselines.Add(new InventoryCostTransitionBaseline
        {
            ProductId = ProductId,
            CutoffAt = CutoffAt,
            HomeStockQuantity = 10,
            OpeningCostingQuantity = 10,
            InventoryValue = 20m,
            AverageUnitCost = 2m,
            CostSource = InventoryCostBaselineSource.ManualAuthoritative,
        });
        seed.SaveChanges();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// A product that has never been rebuilt starts at <c>0</c> - the value the migration gives every
    /// existing row, and the one the first rebuild moves off.
    /// </summary>
    [Fact]
    public async Task A_product_that_has_never_been_rebuilt_starts_at_zero()
    {
        Assert.Equal(0, await CostingVersionAsync());
    }

    /// <summary>
    /// A purchase create, then an <strong>edit that changes the line's unit cost in place</strong> -
    /// the same quantity, so no movement is added and none is removed - and then the delete: each one
    /// rebuilds the product's cost, and each one moves the version. The edit is the case the issue
    /// singles out, because a latest-movement id would not notice it at all.
    /// </summary>
    [Fact]
    public async Task A_purchase_create_edit_and_delete_each_move_the_costing_version()
    {
        var beforeAnyWrite = await CostingVersionAsync();

        int purchaseId;
        await using (var db = TestAppDbContext.For(_options, BusinessId))
        {
            var created = await PurchaseStore(db).CreateAsync(
                Fields(),
                [new PurchaseItemInput(ProductId, 10m, 4m)],
                file: null,
                supplierOrderCompletion: null,
                attachmentReservation: null,
                CancellationToken.None);
            purchaseId = created.Id;
        }

        var afterCreate = await CostingVersionAsync();
        Assert.True(afterCreate > beforeAnyWrite, $"A purchase create left the version at {afterCreate}.");

        // The same single line at the same quantity and a different unit cost: the stored movement's
        // cost is rewritten in place and the movement count is unchanged, which is what makes the
        // version the only thing that says the history moved.
        long movementsBeforeEdit;
        await using (var db = TestAppDbContext.For(_options, BusinessId))
        {
            movementsBeforeEdit = await db.StockAdjustments.CountAsync();
            await PurchaseStore(db).UpdateAsync(
                purchaseId,
                Fields(),
                [new PurchaseItemInput(ProductId, 10m, 6m)],
                CancellationToken.None);
        }

        var afterEdit = await CostingVersionAsync();
        Assert.True(afterEdit > afterCreate, $"A purchase edit left the version at {afterEdit}.");

        await using (var read = TestAppDbContext.Unrestricted(_options))
        {
            Assert.Equal(movementsBeforeEdit, await read.StockAdjustments.CountAsync());
            var movement = await read.StockAdjustments.AsNoTracking().SingleAsync();
            Assert.Equal(6m, movement.UnitCost);
        }

        await using (var db = TestAppDbContext.For(_options, BusinessId))
        {
            await PurchaseStore(db).DeleteAsync(purchaseId, CancellationToken.None);
        }

        var afterDelete = await CostingVersionAsync();
        Assert.True(afterDelete > afterEdit, $"A purchase delete left the version at {afterDelete}.");
    }

    /// <summary>
    /// A manual stock write is a cost rebuild too, and moves the version with the position it writes.
    /// </summary>
    [Fact]
    public async Task A_manual_stock_write_moves_the_costing_version()
    {
        var before = await CostingVersionAsync();

        await using (var db = TestAppDbContext.For(_options, BusinessId))
        {
            await new EfStockAdjustmentStore(
                    db, TestCostingUseCases.RecordMovement(db), TestCostingUseCases.Rebuild(db))
                .ApplyAsync(
                    ProductId,
                    new ManualStockAdjustmentInput(
                        5,
                        Inventory.Domain.Stock.StockAdjustmentReason.Restock,
                        "Fictional restock",
                        null,
                        null,
                        4m),
                    CancellationToken.None);
        }

        var after = await CostingVersionAsync();
        Assert.True(after > before, $"A manual stock write left the version at {after}.");
    }

    /// <summary>
    /// A dry run stages no costing position at all (issue #362), so it moves nothing: the version says
    /// a rebuild was <em>written</em>, not that one was computed. A drain whose claim a preview
    /// invalidated would discard a replay that is still correct.
    /// </summary>
    [Fact]
    public async Task A_dry_run_rebuild_leaves_the_costing_version_alone()
    {
        await using (var db = TestAppDbContext.For(_options, BusinessId))
        {
            await TestCostingUseCases.Rebuild(db).RebuildAsync(
                ProductId, dryRun: true, cancellationToken: CancellationToken.None);
            await db.SaveChangesAsync();
        }

        Assert.Equal(0, await CostingVersionAsync());
    }

    private static PurchaseFields Fields() =>
        new("Fictional restock", null, null, null, null, PurchaseDate, null);

    private static EfPurchaseStore PurchaseStore(AppDbContext db) =>
        new(db, TestCostingUseCases.Rebuild(db), TestClocks.Fixed);

    private async Task<long> CostingVersionAsync()
    {
        await using var db = TestAppDbContext.Unrestricted(_options);
        return await db.Products.AsNoTracking()
            .Where(product => product.Id == ProductId)
            .Select(product => product.CostingVersion)
            .SingleAsync();
    }
}
