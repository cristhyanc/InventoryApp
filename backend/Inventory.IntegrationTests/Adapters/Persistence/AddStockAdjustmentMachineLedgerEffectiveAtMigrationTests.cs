using Inventory.Application.Machines;
using Inventory.Domain.MachineStock;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Upgrade test for <c>AddStockAdjustmentMachineLedgerEffectiveAt</c> (issue #1243). The migration is
/// additive: it adds one nullable <c>MachineLedgerEffectiveAt</c> column to <c>StockAdjustments</c>,
/// with no default and no backfill, and does nothing else.
///
/// <para>The absence of a backfill is the behaviour under test as much as the column is. A movement
/// recorded before the upgrade has no event instant recorded anywhere on it, so the ledger falls back
/// to its <c>EffectiveAt</c> and replays it exactly where it replayed it yesterday - re-dating a
/// refill that was already applied is explicitly a separate repair. Nothing financial may move with
/// it either: the column records no quantity, cost or value, so the movement's own figures, the
/// product's costing position and the stored cost of its sale all have to come through untouched, and
/// the rest of <c>StockAdjustments</c>' columns and indexes have to be exactly as they were, because
/// this is the table every cost replay and every machine refill hangs off.</para>
/// </summary>
public class AddStockAdjustmentMachineLedgerEffectiveAtMigrationTests
{
    private const string PreviousMigration = "20261114100146_AddProductCostingVersion";
    private const string TargetMigration = "AddStockAdjustmentMachineLedgerEffectiveAt";
    private const string Column = "MachineLedgerEffectiveAt";

    private const long ProductId = 1;

    /// <summary>The machine's local id, which a refill records, and its Nayax number.</summary>
    private const long LocalMachineId = 7;
    private const long NayaxMachineId = 4200001;

    private static readonly DateTime RefilledAt = new(2026, 1, 4, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Migration_adds_one_nullable_column_backfills_nothing_and_moves_nothing_else()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        List<string> columnsBefore;
        List<string> indexesBefore;
        await using (var before = TestAppDbContext.Unrestricted(options))
        {
            await before.GetService<IMigrator>().MigrateAsync(PreviousMigration);

            columnsBefore = await MigrationSchemaProbe.ColumnNamesAsync(connection, "StockAdjustments");
            indexesBefore = await MigrationSchemaProbe.IndexNamesAsync(connection, "StockAdjustments");
            Assert.DoesNotContain(Column, columnsBefore);

            await SeedPreUpgradeHistoryAsync(connection);
        }

        await using (var after = TestAppDbContext.Unrestricted(options))
        {
            await after.GetService<IMigrator>().MigrateAsync(TargetMigration);

            // The one column that is new, and every other column still in its place: the upgrade
            // appends rather than rebuilding the table every cost replay hangs off.
            Assert.Equal(
                columnsBefore.Append(Column),
                await MigrationSchemaProbe.ColumnNamesAsync(connection, "StockAdjustments"));
            Assert.Equal(indexesBefore, await MigrationSchemaProbe.IndexNamesAsync(connection, "StockAdjustments"));

            // Nullable with no default: the column says nothing at all about a movement recorded
            // before the upgrade, which is what leaves that movement's ledger position unchanged.
            Assert.Null(await MigrationSchemaProbe.ColumnDefaultAsync(connection, "StockAdjustments", Column));

            var movements = await after.StockAdjustments.AsNoTracking().OrderBy(m => m.Id).ToListAsync();
            Assert.All(movements, movement => Assert.Null(movement.MachineLedgerEffectiveAt));

            // Nothing financial moved with it.
            var refill = movements.Single(movement => movement.MachineId == LocalMachineId);
            Assert.Equal((-4, RefilledAt), (refill.QuantityChange, refill.EffectiveAt));
            var product = await after.Products.AsNoTracking().SingleAsync();
            Assert.Equal(
                (8, 12, 13.20m, 1.10m),
                (product.QuantityInStock, product.CostingQuantity, product.InventoryValue, product.AverageUnitCost));
            var sale = await after.NayaxSales.AsNoTracking().SingleAsync();
            Assert.Equal((1.10m, 1.10m), (sale.UnitCostAtSale, sale.CostOfGoodsSold));
        }
    }

    /// <summary>
    /// What the absence of a backfill means for the one reader of the column: a refill recorded
    /// before the upgrade is still replayed at its own <c>EffectiveAt</c>, so the machine's expected
    /// stock after the upgrade is the number it was before it.
    /// </summary>
    [Fact]
    public async Task A_refill_recorded_before_the_upgrade_is_still_replayed_at_its_effective_time()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using (var before = TestAppDbContext.Unrestricted(options))
        {
            await before.GetService<IMigrator>().MigrateAsync(PreviousMigration);
            await SeedPreUpgradeHistoryAsync(connection);
        }

        await using (var after = TestAppDbContext.Unrestricted(options))
            await after.GetService<IMigrator>().MigrateAsync(TargetMigration);

        await using var db = TestAppDbContext.For(options, businessId: 1);
        var facts = await new EfMachineStockLedgerStore(db)
            .GetLedgerFactsAsync(new MachineIdentity(LocalMachineId, NayaxMachineId), [], CancellationToken.None);

        var refill = Assert.Single(facts.Refills);
        Assert.Equal(RefilledAt, refill.EffectiveAtUtc);

        // Counted 10 on the day before the refill, four units delivered, one dispensed since.
        var ledger = Assert.Single(MachineStockLedger.Project(
            new MachineStockLedgerInputs(facts.Refills, facts.Sales, facts.Counts, facts.OpeningBaselines)));
        Assert.Equal(13, ledger.ExpectedQuantity);
        Assert.Equal(4, ledger.RefilledQuantity);
        Assert.Equal(1, ledger.SoldQuantity);
    }

    /// <summary>
    /// A trading database as the migration finds it: one costed product, the purchase and restock
    /// movement behind its costing position, a machine refill of four units with its own effective
    /// instant, the operator's count before that refill, and one costed sale of a dispensed unit.
    /// Written over the connection rather than through the model, because the model describes the
    /// schema after the upgrade.
    /// </summary>
    private static async Task SeedPreUpgradeHistoryAsync(SqliteConnection connection)
    {
        await using var seed = connection.CreateCommand();
        seed.CommandText = """
            INSERT INTO Products (Name, Sku, AverageUnitCost, QuantityInStock, LowStockThreshold,
                                  RestockTo, IsActive, Unit, GstRule, CreatedAt, UpdatedAt, BusinessId,
                                  CostingQuantity, InventoryValue)
                VALUES ('Chips', 'CHIP-1', 1.10, 8, 2, 20, 1, 'unit', 0,
                        '2026-01-01 00:00:00', '2026-01-01 00:00:00', 1, 12, 13.20);
            INSERT INTO Machines (BusinessId, Id, Name, ExternalMachineId, IsActive)
                VALUES (1, 7, 'Machine 4200001', '4200001', 1);
            INSERT INTO Receipts (Title, TotalAmount, PurchaseDate, CreatedAt, BusinessId)
                VALUES ('Weekly restock', 13.20, '2026-01-02 00:00:00', '2026-01-02 00:00:00', 1);
            INSERT INTO ReceiptItems (ReceiptId, ProductId, Quantity, UnitCost, BusinessId)
                VALUES (1, 1, 12, 1.10, 1);
            INSERT INTO StockAdjustments (ProductId, ReceiptItemId, QuantityChange, QuantityAfter, Reason, Source,
                                          UnitCost, TotalCost, CostingQuantityAfter, InventoryValueAfter,
                                          AverageUnitCostAfter, CreatedAt, EffectiveAt, BusinessId)
                VALUES (1, 1, 12, 12, 0, 0, 1.10, 13.20, 12, 13.20, 1.10,
                        '2026-01-02 00:00:00', '2026-01-02 00:00:00', 1);
            INSERT INTO StockAdjustments (ProductId, QuantityChange, QuantityAfter, Reason, Source,
                                          MachineId, CreatedAt, EffectiveAt, BusinessId)
                VALUES (1, -4, 8, 5, 0, 7, '2026-01-04 08:00:00', '2026-01-04 08:00:00', 1);
            INSERT INTO MachineStockCounts (BusinessId, MachineId, ProductId, CountedQuantity, CountedAtUtc)
                VALUES (1, 7, 1, 10, '2026-01-03 08:00:00');
            INSERT INTO NayaxSales (BusinessId, TransactionID, TransactionStatusId, MachineID,
                                    ProductId, SettlementValue, MachineAuthorizationTime, Provider,
                                    ProviderProductIdIsRaw, CostingStatus, CostSource,
                                    UnitCostAtSale, CostOfGoodsSold)
                VALUES (1, 9100000001, 12, 4200001, 1, 2.50, '2026-01-05 00:00:00', 1,
                        1, 1, 1, 1.10, 1.10);
            """;
        await seed.ExecuteNonQueryAsync();
    }
}
