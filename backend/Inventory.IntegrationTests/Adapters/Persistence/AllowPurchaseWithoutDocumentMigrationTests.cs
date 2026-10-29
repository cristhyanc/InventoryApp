using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Migrations;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Upgrade test for <c>AllowPurchaseWithoutDocument</c> (issue #976). The migration rebuilds
/// <c>Receipts</c> to drop <c>NOT NULL</c> from the four document columns and to add the
/// all-or-nothing <c>CHECK</c> constraint, and changes no data at all.
///
/// <para>A rebuild is the part that needs proving. SQLite cannot drop a <c>NOT NULL</c> or add a
/// table constraint in place, so the table every purchase line hangs off is created afresh, copied
/// into, dropped and renamed - which is exactly the sequence that could lose a column, a value, an
/// index or a purchase's lines. Each of those is asserted here rather than assumed.</para>
/// </summary>
public class AllowPurchaseWithoutDocumentMigrationTests
{
    private const string PreviousMigration = "20261029002125_AddSupplierOrderPartialCompletion";
    private const string TargetMigration = "AllowPurchaseWithoutDocument";

    private static readonly string[] DocumentColumns =
    [
        "ContentType", "FileName", "FileSizeBytes", "StoredFileName",
    ];

    /// <summary>
    /// Every column <c>Receipts</c> carries, so the rebuild is shown to have kept all of them. The
    /// hand-written copy names its columns explicitly, which is what makes this list the regression
    /// for a column some other change adds to the table before this migration merges.
    /// </summary>
    private static readonly string[] AllColumns =
    [
        "BusinessId", "ContentType", "CreatedAt", "DeliveryCost", "DeliveryGstClassification",
        "DeliveryGstClassificationSource", "FileName", "FileSizeBytes", "Id", "Notes", "PackageCost",
        "PackageGstClassification", "PackageGstClassificationSource", "PurchaseDate",
        "StoredFileName", "SupplierId", "Title", "TotalAmount",
    ];

    /// <summary>
    /// The upgrade: the four columns become nullable, the constraint arrives, the existing purchase
    /// keeps its document and everything hanging off it, and a purchase with no document becomes
    /// storable - which it was not a statement earlier.
    /// </summary>
    [Fact]
    public async Task Migration_makes_the_document_columns_nullable_and_preserves_existing_purchases()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using (var before = TestAppDbContext.Unrestricted(options))
        {
            await before.GetService<IMigrator>().MigrateAsync(PreviousMigration);

            var createSql = await MigrationSchemaProbe.CreateTableSqlAsync(connection, "Receipts");
            Assert.All(
                DocumentColumns,
                column => Assert.Contains($@"""{column}"" ", createSql, StringComparison.Ordinal));
            Assert.DoesNotContain("CK_Receipts_DocumentMetadataAllOrNothing", createSql, StringComparison.Ordinal);
            Assert.True(await NotNullAsync(connection, "StoredFileName"));

            await SeedDocumentedPurchaseAsync(connection);
        }

        await using (var after = TestAppDbContext.Unrestricted(options))
        {
            await after.GetService<IMigrator>().MigrateAsync(TargetMigration);

            // The rebuild kept every column and both indexes, and dropped the NOT NULLs only on the
            // four document columns.
            Assert.Equal(
                AllColumns,
                (await MigrationSchemaProbe.ColumnNamesAsync(connection, "Receipts")).Order(StringComparer.Ordinal));
            Assert.Equal(
                ["IX_Receipts_BusinessId", "IX_Receipts_SupplierId"],
                await MigrationSchemaProbe.IndexNamesAsync(connection, "Receipts"));
            foreach (var column in DocumentColumns) Assert.False(await NotNullAsync(connection, column));
            Assert.True(await NotNullAsync(connection, "Title"));
            Assert.True(await NotNullAsync(connection, "PurchaseDate"));
            Assert.Contains(
                "CK_Receipts_DocumentMetadataAllOrNothing",
                await MigrationSchemaProbe.CreateTableSqlAsync(connection, "Receipts"),
                StringComparison.Ordinal);

            // The purchase that already had a document still has exactly the document it had, and
            // its line, its stock movement and its product's costing position are untouched.
            var purchase = await after.Receipts.AsNoTracking().Include(r => r.Items).SingleAsync();
            Assert.Equal(
                ("scan.jpg", "abc.jpg", "image/jpeg", 2048L),
                (purchase.FileName, purchase.StoredFileName, purchase.ContentType, purchase.FileSizeBytes));
            Assert.Equal((20.90m, 5.00m, 2.70m), (purchase.TotalAmount, purchase.DeliveryCost, purchase.PackageCost));
            Assert.Equal("Weekly restock", purchase.Title);
            var item = Assert.Single(purchase.Items);
            Assert.Equal((purchase.Id, 1L, 12m, 1.10m), (item.ReceiptId, item.ProductId, item.Quantity, item.UnitCost));
            var movement = await after.StockAdjustments.AsNoTracking().SingleAsync();
            Assert.Equal((item.Id, 12, 1.10m, 13.20m), (movement.ReceiptItemId, movement.QuantityChange, movement.UnitCost, movement.TotalCost));
            var product = await after.Products.AsNoTracking().SingleAsync();
            Assert.Equal((12, 12, 13.20m, 1.10m),
                (product.QuantityInStock, product.CostingQuantity, product.InventoryValue, product.AverageUnitCost));

            // And the point of the whole migration: a purchase with no document is now storable.
            await using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO Receipts (Title, PurchaseDate, CreatedAt, BusinessId)
                    VALUES ('No receipt', '2026-02-01 00:00:00', '2026-02-01 00:00:00', 1);
                """;
            Assert.Equal(1, await insert.ExecuteNonQueryAsync());
        }
    }

    /// <summary>
    /// The constraint itself: a half-recorded document is refused by the database, whichever half is
    /// missing. Without this, "has no document" would be a value some writer could make ambiguous.
    /// </summary>
    [Theory]
    [InlineData("StoredFileName", "'abc.jpg'")]
    [InlineData("ContentType", "'image/jpeg'")]
    [InlineData("FileName", "'scan.jpg'")]
    [InlineData("FileSizeBytes", "2048")]
    public async Task The_check_constraint_refuses_a_half_recorded_document(string column, string value)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using var db = TestAppDbContext.Unrestricted(options);
        await db.GetService<IMigrator>().MigrateAsync(TargetMigration);

        await using var insert = connection.CreateCommand();
        insert.CommandText = $"""
            INSERT INTO Receipts (Title, PurchaseDate, CreatedAt, BusinessId, {column})
                VALUES ('Half a document', '2026-02-01 00:00:00', '2026-02-01 00:00:00', 1, {value});
            """;

        var failure = await Assert.ThrowsAsync<SqliteException>(() => insert.ExecuteNonQueryAsync());
        Assert.Contains("CK_Receipts_DocumentMetadataAllOrNothing", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, await MigrationSchemaProbe.RowCountAsync(connection, "Receipts"));
    }

    /// <summary>
    /// <c>Down</c> returns to the schema the previous release's code compiles against, and the
    /// purchases - with their lines still pointing at them - survive the second rebuild as well as
    /// the first. A rollback is only possible while every purchase has a document, which is the test
    /// below.
    /// </summary>
    [Fact]
    public async Task Down_restores_the_required_document_columns_and_leaves_the_purchases()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using (var up = TestAppDbContext.Unrestricted(options))
        {
            await up.GetService<IMigrator>().MigrateAsync(TargetMigration);
            await SeedDocumentedPurchaseAsync(connection);
        }

        await using (var down = TestAppDbContext.Unrestricted(options))
        {
            await down.GetService<IMigrator>().MigrateAsync(PreviousMigration);
        }

        foreach (var column in DocumentColumns) Assert.True(await NotNullAsync(connection, column));
        Assert.DoesNotContain(
            "CK_Receipts_DocumentMetadataAllOrNothing",
            await MigrationSchemaProbe.CreateTableSqlAsync(connection, "Receipts"),
            StringComparison.Ordinal);
        Assert.Equal(
            ["IX_Receipts_BusinessId", "IX_Receipts_SupplierId"],
            await MigrationSchemaProbe.IndexNamesAsync(connection, "Receipts"));

        await using var remaining = connection.CreateCommand();
        remaining.CommandText = """
            SELECT r.Id, r.Title, r.StoredFileName, r.FileSizeBytes, i.Quantity
            FROM Receipts r JOIN ReceiptItems i ON i.ReceiptId = r.Id;
            """;
        await using var reader = await remaining.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(
            ("Weekly restock", "abc.jpg", 2048L, 12m),
            (reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetDecimal(4)));
        Assert.False(await reader.ReadAsync());
    }

    /// <summary>
    /// A purchase recorded with no document has no value a restored <c>NOT NULL</c> column could
    /// hold, so the rollback refuses by name and changes nothing - rather than inventing a stored
    /// name for a document that does not exist, or dropping the purchase that has none.
    /// </summary>
    [Fact]
    public async Task Down_refuses_while_a_purchase_has_no_document_and_changes_nothing()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using (var up = TestAppDbContext.Unrestricted(options))
        {
            await up.GetService<IMigrator>().MigrateAsync(TargetMigration);
            await SeedDocumentedPurchaseAsync(connection);

            await using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO Receipts (Title, PurchaseDate, CreatedAt, BusinessId)
                    VALUES ('No receipt', '2026-02-01 00:00:00', '2026-02-01 00:00:00', 1);
                """;
            await insert.ExecuteNonQueryAsync();
        }

        await using (var down = TestAppDbContext.Unrestricted(options))
        {
            var failure = await Assert.ThrowsAsync<SqliteException>(
                () => down.GetService<IMigrator>().MigrateAsync(PreviousMigration));
            Assert.Contains(
                AllowPurchaseWithoutDocument.DownBlockedMessage, failure.Message, StringComparison.Ordinal);
        }

        // Still the post-migration schema, with both purchases and the scratch table gone.
        Assert.False(await NotNullAsync(connection, "StoredFileName"));
        Assert.Equal(2, await MigrationSchemaProbe.RowCountAsync(connection, "Receipts"));
        Assert.DoesNotContain(
            "AllowPurchaseWithoutDocumentDownGuard", await MigrationSchemaProbe.TableNamesAsync(connection));
    }

    /// <summary>
    /// Neither direction may need a point-in-time model to generate, for the reason
    /// <c>AddSupplierOrderPartialCompletionMigrationTests</c> sets out: EF hands a migration's
    /// <c>Down</c> the <em>preceding</em> migration's recorded target model, several migrations in
    /// this history deliberately record none, and SQLite cannot drop a <c>NOT NULL</c> or add a table
    /// constraint without rebuilding the table - which the provider's model-driven rebuild cannot do
    /// without a model. Writing both directions out as SQL is what makes whichever migration ends up
    /// in front of this one after a merge not this migration's business.
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

    /// <summary>Whether SQLite declares <paramref name="column"/> of <c>Receipts</c> as <c>NOT NULL</c>.</summary>
    private static async Task<bool> NotNullAsync(SqliteConnection connection, string column)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT \"notnull\" FROM pragma_table_info('Receipts') WHERE name = '{column}';";
        return (long)(await command.ExecuteScalarAsync())! == 1;
    }

    /// <summary>
    /// One purchase with a document, its line, its restock movement and the product they are about -
    /// the whole chain the rebuild has to carry across untouched.
    /// </summary>
    private static async Task SeedDocumentedPurchaseAsync(SqliteConnection connection)
    {
        await using var seed = connection.CreateCommand();
        seed.CommandText = """
            INSERT INTO Products (Id, BusinessId, Name, Sku, Unit, AverageUnitCost, CostingQuantity,
                                  InventoryValue, QuantityInStock, LowStockThreshold, RestockTo,
                                  IsActive, GstRule, CreatedAt, UpdatedAt)
                VALUES (1, 1, 'Chips', 'CHIP-1', 'bag', 1.10, 12, 13.20, 12, 2, 20, 1, 0,
                        '2026-01-01 00:00:00', '2026-01-01 00:00:00');
            INSERT INTO Receipts (Title, Notes, TotalAmount, DeliveryCost, PackageCost, PurchaseDate,
                                  FileName, StoredFileName, ContentType, FileSizeBytes, CreatedAt, BusinessId)
                VALUES ('Weekly restock', 'note', 20.90, 5.00, 2.70, '2026-01-02 00:00:00',
                        'scan.jpg', 'abc.jpg', 'image/jpeg', 2048, '2026-01-02 00:00:00', 1);
            INSERT INTO ReceiptItems (ReceiptId, ProductId, Quantity, UnitCost, BusinessId)
                VALUES (1, 1, 12, 1.10, 1);
            INSERT INTO StockAdjustments (ProductId, ReceiptItemId, QuantityChange, QuantityAfter, Reason,
                                          Source, UnitCost, TotalCost, CostingQuantityAfter,
                                          InventoryValueAfter, AverageUnitCostAfter, CreatedAt,
                                          EffectiveAt, BusinessId)
                VALUES (1, 1, 12, 12, 0, 0, 1.10, 13.20, 12, 13.20, 1.10,
                        '2026-01-02 00:00:00', '2026-01-02 00:00:00', 1);
            """;
        await seed.ExecuteNonQueryAsync();
    }
}
