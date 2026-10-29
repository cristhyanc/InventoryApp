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
    ///
    /// <para>Everything else survives, and that is asserted rather than assumed: the order row and
    /// the line hanging off it are still there, with the line still pointing at the order it was
    /// ordered on. Dropping the columns in place is what makes that true - a table rebuild would
    /// have dropped and recreated the very table that reference points at.</para>
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
                INSERT INTO Products (Id, BusinessId, Name, Sku, Unit, AverageUnitCost, CostingQuantity,
                                      InventoryValue, QuantityInStock, LowStockThreshold, RestockTo,
                                      IsActive, GstRule, CreatedAt, UpdatedAt)
                    VALUES (1, 1, 'Chips', 'CHIP-1', 'bag', 1.10, 6, 6.60, 6, 2, 20, 1, 0,
                            '2026-01-01 00:00:00', '2026-01-01 00:00:00');
                INSERT INTO Suppliers (Id, BusinessId, Name, ProductLineGstDefault, DeliveryGstDefault,
                                       PackageGstDefault, IsHidden)
                    VALUES (1, 1, 'Costco', 0, 0, 0, 0);
                INSERT INTO SupplierOrders (Id, SupplierId, OrderDate, Status, CreatedAt, UpdatedAt, BusinessId,
                                            CompletedWithUndeliveredAt, CompletedByDirectoryTenantId,
                                            CompletedByObjectId)
                    VALUES (7, 1, '2026-09-01 00:00:00', 4, '2026-09-01 00:00:00', '2026-09-12 03:30:00', 1,
                            '2026-09-12 03:30:00', '22222222-2222-2222-2222-222222222222',
                            '44444444-4444-4444-4444-444444444444');
                INSERT INTO SupplierOrderLines (SupplierOrderId, ProductId, QuantityOrdered, QuantityReceived,
                                                UnitPrice, Notes, BusinessId)
                    VALUES (7, 1, 24, 16, 1.25, 'Case of 24', 1);
                """;
            await seed.ExecuteNonQueryAsync();
        }

        await using (var down = TestAppDbContext.Unrestricted(options))
        {
            await down.GetService<IMigrator>().MigrateAsync(PreviousMigration);
        }

        var columns = await MigrationSchemaProbe.ColumnNamesAsync(connection, "SupplierOrders");
        Assert.All(AddedColumns, column => Assert.DoesNotContain(column, columns));

        await using (var remaining = connection.CreateCommand())
        {
            remaining.CommandText = "SELECT Id, Status, SupplierId FROM SupplierOrders;";
            await using var reader = await remaining.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal((7L, 4L, 1L), (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2)));
            Assert.False(await reader.ReadAsync());
        }

        // The line is still ordered on the order it was ordered on, with its quantities intact.
        await using var line = connection.CreateCommand();
        line.CommandText = "SELECT SupplierOrderId, QuantityOrdered, QuantityReceived FROM SupplierOrderLines;";
        await using var lines = await line.ExecuteReaderAsync();
        Assert.True(await lines.ReadAsync());
        Assert.Equal((7L, 24m, 16m), (lines.GetInt64(0), lines.GetDecimal(1), lines.GetDecimal(2)));
        Assert.False(await lines.ReadAsync());
    }

    /// <summary>
    /// The rollback must not need a point-in-time model to generate, and that is not a detail: EF
    /// hands a migration's <c>Down</c> the <em>preceding</em> migration's recorded target model, and
    /// several migrations in this history are hand-written and deliberately record none at all
    /// (<c>20261028120000_AddPriceCollectorResults</c> and the four before it say so in their own
    /// designer files). Whichever migration happens to precede this one after a merge is therefore
    /// not this migration's business - so <c>Down</c> drops its columns with SQLite's own in-place
    /// <c>ALTER TABLE ... DROP COLUMN</c> rather than through the provider's model-driven table
    /// rebuild, which has no model to rebuild from when the preceding migration carries none.
    ///
    /// <para>This is the regression for a real failure: with <c>MigrationBuilder.DropColumn</c> the
    /// rollback threw <c>SQLite does not support this migration operation ('DropColumnOperation')</c>
    /// as soon as a model-less migration sat in front of it, which is what merging <c>develop</c>
    /// into this branch did. Generating with <c>model: null</c> is exactly that condition, and it
    /// holds here no matter what the history around this migration becomes.</para>
    /// </summary>
    [Fact]
    public void Both_directions_generate_without_a_point_in_time_model()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var db = TestAppDbContext.Unrestricted(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);

        var migrations = db.GetService<IMigrationsAssembly>();
        var type = migrations.Migrations
            .Single(candidate => candidate.Key.EndsWith(TargetMigration, StringComparison.Ordinal))
            .Value;
        var migration = migrations.CreateMigration(type, "Microsoft.EntityFrameworkCore.Sqlite");
        var generator = db.GetService<IMigrationsSqlGenerator>();

        Assert.NotEmpty(generator.Generate(migration.UpOperations, model: null));
        Assert.NotEmpty(generator.Generate(migration.DownOperations, model: null));
    }
}
