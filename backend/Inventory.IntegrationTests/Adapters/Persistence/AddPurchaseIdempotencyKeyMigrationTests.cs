using Inventory.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Upgrade test for <c>AddPurchaseIdempotencyKey</c> (issue #1247). The migration is additive: two
/// nullable columns on <c>Receipts</c> and the per-business unique index that makes the key mean
/// something, and nothing else.
///
/// <para>There is no backfill, and there is nothing to backfill from: a purchase recorded before
/// this migration was created by a request that sent no key, so every existing row arrives with
/// <c>NULL</c> in both columns - which is exactly what "this purchase was not created under a key"
/// means. The purchase itself, its line, its restock movement and its product's costing position
/// must come through untouched, because an idempotency key is a fact about the request rather than
/// about the purchase.</para>
/// </summary>
public class AddPurchaseIdempotencyKeyMigrationTests
{
    private const string PreviousMigration = "20261116031500_AddStockAdjustmentMachineLedgerEffectiveAt";
    private const string TargetMigration = "AddPurchaseIdempotencyKey";
    private const string KeyColumn = "IdempotencyKey";
    private const string FingerprintColumn = "IdempotencyFingerprint";
    private const string KeyIndex = "IX_Receipts_BusinessId_IdempotencyKey";

    [Fact]
    public async Task Migration_adds_an_empty_key_and_preserves_every_purchase()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        List<string> columnsBefore;
        List<string> indexesBefore;
        await using (var before = TestAppDbContext.Unrestricted(options))
        {
            await before.GetService<IMigrator>().MigrateAsync(PreviousMigration);

            columnsBefore = await MigrationSchemaProbe.ColumnNamesAsync(connection, "Receipts");
            indexesBefore = await MigrationSchemaProbe.IndexNamesAsync(connection, "Receipts");
            Assert.DoesNotContain(KeyColumn, columnsBefore);

            // One purchase as a trading database holds it: a line, its restock movement and the
            // product's costing position that movement produced.
            await using var seed = connection.CreateCommand();
            seed.CommandText = """
                INSERT INTO Products (Name, Sku, AverageUnitCost, QuantityInStock, LowStockThreshold,
                                      RestockTo, IsActive, Unit, GstRule, CreatedAt, UpdatedAt, BusinessId,
                                      CostingQuantity, InventoryValue)
                    VALUES ('Chips', 'CHIP-1', 1.10, 12, 2, 20, 1, 'unit', 0,
                            '2026-01-01 00:00:00', '2026-01-01 00:00:00', 1, 12, 13.20);
                INSERT INTO Receipts (Title, Notes, InvoiceNumber, TotalAmount, DeliveryCost, PackageCost,
                                      PurchaseDate, CreatedAt, BusinessId)
                    VALUES ('Weekly restock', 'note', 'INV-1', 20.90, 5.00, 2.70,
                            '2026-01-02 00:00:00', '2026-01-02 00:00:00', 1);
                INSERT INTO ReceiptItems (ReceiptId, ProductId, Quantity, UnitCost, BusinessId)
                    VALUES (1, 1, 12, 1.10, 1);
                INSERT INTO StockAdjustments (ProductId, ReceiptItemId, QuantityChange, QuantityAfter, Reason, Source,
                                              UnitCost, TotalCost, CostingQuantityAfter, InventoryValueAfter,
                                              AverageUnitCostAfter, CreatedAt, EffectiveAt, BusinessId)
                    VALUES (1, 1, 12, 12, 0, 0, 1.10, 13.20, 12, 13.20, 1.10,
                            '2026-01-02 00:00:00', '2026-01-02 00:00:00', 1);
                """;
            await seed.ExecuteNonQueryAsync();
        }

        await using (var after = TestAppDbContext.Unrestricted(options))
        {
            await after.GetService<IMigrator>().MigrateAsync(TargetMigration);

            // The two columns that are new, and every other column still in its place: the upgrade
            // appends rather than rebuilding the table every purchase line hangs off.
            Assert.Equal(
                columnsBefore.Append(FingerprintColumn).Append(KeyColumn),
                await MigrationSchemaProbe.ColumnNamesAsync(connection, "Receipts"));
            Assert.Equal(
                indexesBefore.Append(KeyIndex).Order(),
                (await MigrationSchemaProbe.IndexNamesAsync(connection, "Receipts")).Order());
            Assert.Null(await MigrationSchemaProbe.ColumnDefaultAsync(connection, "Receipts", KeyColumn));

            // Issue #976's all-or-nothing document rule is untouched by this upgrade.
            Assert.Contains(
                "CK_Receipts_DocumentMetadataAllOrNothing",
                await MigrationSchemaProbe.CreateTableSqlAsync(connection, "Receipts"));

            var purchase = await after.Receipts.Include(receipt => receipt.Items).SingleAsync();
            Assert.Null(purchase.IdempotencyKey);
            Assert.Null(purchase.IdempotencyFingerprint);

            // Nothing else moved: the purchase's own fields, its line, and the costing history that
            // line produced.
            Assert.Equal(
                ("Weekly restock", "note", "INV-1", 20.90m),
                (purchase.Title, purchase.Notes, purchase.InvoiceNumber, purchase.TotalAmount));
            var item = Assert.Single(purchase.Items);
            Assert.Equal((12m, 1.10m), (item.Quantity, item.UnitCost));
            var product = await after.Products
                .Select(candidate => new
                {
                    candidate.QuantityInStock,
                    candidate.CostingQuantity,
                    candidate.InventoryValue,
                    candidate.AverageUnitCost,
                })
                .SingleAsync();
            Assert.Equal(
                (12, 12, 13.20m, 1.10m),
                (product.QuantityInStock, product.CostingQuantity, product.InventoryValue, product.AverageUnitCost));
        }
    }

    /// <summary>
    /// The index is what the claim rests on, and it is bounded in both directions: one business
    /// cannot save two purchases under one key, two businesses may both use the same key, and any
    /// number of purchases may carry no key at all - which is every purchase a client that sends
    /// none creates.
    /// </summary>
    [Fact]
    public async Task One_key_per_business_is_enforced_and_keyless_purchases_are_unconstrained()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using var db = TestAppDbContext.Unrestricted(options);
        await db.GetService<IMigrator>().MigrateAsync(TargetMigration);

        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO Receipts (Title, PurchaseDate, CreatedAt, BusinessId, IdempotencyKey)
                    VALUES ('Mine', '2026-01-02 00:00:00', '2026-01-02 00:00:00', 1, 'shared-key');
                INSERT INTO Receipts (Title, PurchaseDate, CreatedAt, BusinessId, IdempotencyKey)
                    VALUES ('Theirs', '2026-01-02 00:00:00', '2026-01-02 00:00:00', 2, 'shared-key');
                INSERT INTO Receipts (Title, PurchaseDate, CreatedAt, BusinessId)
                    VALUES ('No key at all', '2026-01-03 00:00:00', '2026-01-03 00:00:00', 1);
                INSERT INTO Receipts (Title, PurchaseDate, CreatedAt, BusinessId)
                    VALUES ('No key either', '2026-01-04 00:00:00', '2026-01-04 00:00:00', 1);
                """;
            await insert.ExecuteNonQueryAsync();
        }

        await using var duplicate = connection.CreateCommand();
        duplicate.CommandText = """
            INSERT INTO Receipts (Title, PurchaseDate, CreatedAt, BusinessId, IdempotencyKey)
                VALUES ('Mine again', '2026-01-05 00:00:00', '2026-01-05 00:00:00', 1, 'shared-key');
            """;

        var refusal = await Assert.ThrowsAsync<SqliteException>(() => duplicate.ExecuteNonQueryAsync());

        Assert.Contains("Receipts.BusinessId", refusal.Message, StringComparison.Ordinal);
        Assert.Contains($"Receipts.{KeyColumn}", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(4, await db.Receipts.AsNoTracking().CountAsync());
    }
}
