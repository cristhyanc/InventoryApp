using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Upgrade test for <c>AddSupplierOrderPartialCompletion</c> (issue #974). The migration is
/// additive: three nullable columns on <c>SupplierOrders</c>, and nothing else.
///
/// <para><strong>Nothing is backfilled and nothing is reclassified.</strong> An order that was
/// partially received before this release stays partially received, with no completion instant and
/// no actor - which is the truth, because nobody made that decision about it. The new status value
/// needs no schema change at all: the column is already an integer, so what this test proves about
/// it is that no existing row's value moved.</para>
/// </summary>
public class AddSupplierOrderPartialCompletionMigrationTests
{
    private const string PreviousMigration = "20261028031316_AddPriceCollectorWorkItems";
    private const string TargetMigration = "AddSupplierOrderPartialCompletion";

    private static readonly string[] AddedColumns =
    [
        "CompletedByDirectoryTenantId", "CompletedByObjectId", "CompletedWithUndeliveredAt",
    ];

    [Fact]
    public async Task Migration_adds_the_completion_audit_columns_and_reclassifies_no_order()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using (var before = TestAppDbContext.Unrestricted(options))
        {
            await before.GetService<IMigrator>().MigrateAsync(PreviousMigration);

            var columnsBefore = await MigrationSchemaProbe.ColumnNamesAsync(connection, "SupplierOrders");
            Assert.All(AddedColumns, column => Assert.DoesNotContain(column, columnsBefore));

            await using var seed = connection.CreateCommand();
            seed.CommandText = """
                INSERT INTO Products (Id, BusinessId, Name, Sku, Unit, AverageUnitCost, CostingQuantity,
                                      InventoryValue, QuantityInStock, LowStockThreshold, RestockTo,
                                      IsActive, GstRule, CreatedAt, UpdatedAt)
                    VALUES (1, 1, 'Chips', 'CHIP-1', 'bag', 1.10, 6, 6.60, 6, 2, 20, 1, 0,
                            '2026-01-01 00:00:00', '2026-01-01 00:00:00');
                INSERT INTO Suppliers (Id, BusinessId, Name, ProductLineGstDefault, DeliveryGstDefault,
                                       PackageGstDefault, IsHidden)
                    VALUES (1, 1, 'Costco', 0, 0, 0, 0);
                INSERT INTO SupplierOrders (SupplierId, OrderDate, ExpectedDate, Reference, Notes, Status,
                                            CreatedAt, UpdatedAt, BusinessId)
                    VALUES (1, '2026-09-01 00:00:00', '2026-09-08 00:00:00', 'PO-1', 'Priority', 1,
                            '2026-09-01 00:00:00', '2026-09-05 00:00:00', 1);
                INSERT INTO SupplierOrderLines (SupplierOrderId, ProductId, QuantityOrdered, QuantityReceived,
                                                UnitPrice, Notes, BusinessId)
                    VALUES (1, 1, 24, 6, 1.25, 'Case of 24', 1);
                INSERT INTO SupplierOrders (SupplierId, OrderDate, Status, CreatedAt, UpdatedAt, BusinessId)
                    VALUES (1, '2026-08-01 00:00:00', 3, '2026-08-01 00:00:00', '2026-08-02 00:00:00', 1);
                """;
            await seed.ExecuteNonQueryAsync();
        }

        await using (var after = TestAppDbContext.Unrestricted(options))
        {
            await after.GetService<IMigrator>().MigrateAsync(TargetMigration);

            var columnsAfter = await MigrationSchemaProbe.ColumnNamesAsync(connection, "SupplierOrders");
            Assert.All(AddedColumns, column => Assert.Contains(column, columnsAfter));

            var orders = await after.SupplierOrders.AsNoTracking()
                .Include(order => order.Lines)
                .OrderBy(order => order.Id)
                .ToListAsync();
            Assert.Equal(2, orders.Count);

            // The partially received order is still partially received, with no decision recorded.
            var partiallyReceived = orders[0];
            Assert.Equal(SupplierOrderStatus.PartiallyReceived, partiallyReceived.Status);
            Assert.Null(partiallyReceived.CompletedWithUndeliveredAt);
            Assert.Null(partiallyReceived.CompletedByDirectoryTenantId);
            Assert.Null(partiallyReceived.CompletedByObjectId);

            // Everything the order already recorded survived untouched.
            Assert.Equal((1, "PO-1", "Priority"),
                (partiallyReceived.SupplierId, partiallyReceived.Reference, partiallyReceived.Notes));
            var line = Assert.Single(partiallyReceived.Lines);
            Assert.Equal((1L, 24m, 6m, 1.25m),
                (line.ProductId, line.QuantityOrdered, line.QuantityReceived, line.UnitPrice));

            // And a cancelled order is not quietly restated as a completion.
            Assert.Equal(SupplierOrderStatus.Cancelled, orders[1].Status);
            Assert.Null(orders[1].CompletedWithUndeliveredAt);
        }
    }

    /// <summary>
    /// <c>Down</c> returns to the schema the previous release's code compiles against. The decision
    /// is not recoverable afterwards, which is why rolling back is a deliberate act: a completed
    /// order's status value survives in the integer column, but what closed it does not.
    /// </summary>
    [Fact]
    public async Task Down_removes_the_completion_audit_columns_and_leaves_the_orders()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using (var up = TestAppDbContext.Unrestricted(options))
        {
            await up.GetService<IMigrator>().MigrateAsync(TargetMigration);
            await using var seed = connection.CreateCommand();
            seed.CommandText = """
                INSERT INTO Suppliers (Id, BusinessId, Name, ProductLineGstDefault, DeliveryGstDefault,
                                       PackageGstDefault, IsHidden)
                    VALUES (1, 1, 'Costco', 0, 0, 0, 0);
                INSERT INTO SupplierOrders (SupplierId, OrderDate, Status, CreatedAt, UpdatedAt, BusinessId,
                                            CompletedWithUndeliveredAt, CompletedByDirectoryTenantId,
                                            CompletedByObjectId)
                    VALUES (1, '2026-09-01 00:00:00', 4, '2026-09-01 00:00:00', '2026-09-12 03:30:00', 1,
                            '2026-09-12 03:30:00', '22222222-2222-2222-2222-222222222222',
                            '44444444-4444-4444-4444-444444444444');
                """;
            await seed.ExecuteNonQueryAsync();
        }

        await using (var down = TestAppDbContext.Unrestricted(options))
        {
            await down.GetService<IMigrator>().MigrateAsync(PreviousMigration);
        }

        var columns = await MigrationSchemaProbe.ColumnNamesAsync(connection, "SupplierOrders");
        Assert.All(AddedColumns, column => Assert.DoesNotContain(column, columns));

        await using var remaining = connection.CreateCommand();
        remaining.CommandText = "SELECT Status FROM SupplierOrders;";
        Assert.Equal(4L, Convert.ToInt64(await remaining.ExecuteScalarAsync()));
    }
}
