using Inventory.Domain.Providers;
using Inventory.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Upgrade test for <c>AddProviderIdentityColumns</c> (issue #759): the migration that moves a
/// provider's identifier out of the link tables of issue #732 and onto the local row, and records
/// which provider a business sells through.
///
/// Four things have to hold, and each is a thing a reviewer cannot read off the SQL. Every Nayax
/// link must arrive as its row's own column, with the row's own business. A business with no Nayax
/// evidence at all must come out with <em>no</em> provider, because inventory works fully without
/// one and a provider nobody configured must not be invented. The post-copy verification must be
/// recorded where #760 can read it before dropping the tables. And a state the column model cannot
/// represent - a link that crosses a business, a business holding one external id twice, a row
/// carrying two of them - must make the migration <strong>fail</strong> rather than copy something
/// ambiguous into a column every later lookup trusts.
///
/// The three refusal cases are seeded with the link tables' own constraints turned off
/// (<c>PRAGMA foreign_keys</c>) or through a second provider, because that is the only way such a
/// state can exist: it is what a restored backup or an out-of-band write could leave behind, which
/// is exactly why the migration checks rather than assumes.
/// </summary>
public class AddProviderIdentityColumnsMigrationTests
{
    private const string PreviousMigration = "20261017051546_AddProviderDataCoverageTable";
    private const string TargetMigration = "AddProviderIdentityColumns";

    /// <summary>
    /// Business 1 has a stored connection, links and a sale; business 2 only a stored Nayax sale;
    /// business 3 only a provider link; business 4 nothing Nayax at all. The local keys and the
    /// external ids are deliberately different numbers for business 1, so a column that was filled
    /// from the key rather than from the link would be visibly wrong.
    /// </summary>
    private const string Seed = """
        INSERT INTO Businesses (Id, Name, CreatedAtUtc, TimeZoneId, Status, GstRegistered)
            VALUES (1, 'Vending A', '2026-01-01 00:00:00', 'Australia/Sydney', 1, NULL),
                   (2, 'Vending B', '2026-01-01 00:00:00', 'Australia/Sydney', 1, NULL),
                   (3, 'Vending C', '2026-01-01 00:00:00', 'Australia/Sydney', 1, NULL),
                   (4, 'Vending D', '2026-01-01 00:00:00', 'Australia/Sydney', 1, NULL);
        INSERT INTO Categories (Id, Name, Description, BusinessId)
            VALUES (55, 'Snacks', 'Snacks', 1),
                   (56, 'Local only', NULL, 1),
                   (11, 'Drinks', NULL, 3),
                   (12, 'Nothing remote', NULL, 4);
        INSERT INTO Products (Id, Name, Sku, Description, UnitPrice, AverageUnitCost, QuantityInStock,
                              LowStockThreshold, RestockTo, IsActive, Unit, CreatedAt, UpdatedAt,
                              BusinessId, CategoryId, CostingQuantity, InventoryValue, GstRule)
            VALUES (5001, 'Chips', 'CHIP-1', 'Salted', 3.50, 1.10, 12, 2, 20, 1, 'unit',
                    '2026-01-01 00:00:00', '2026-01-02 00:00:00', 1, 55, 12, 13.20, 0),
                   (5002, 'Locally created', NULL, NULL, 2.00, 1.00, 4, 1, 10, 1, 'unit',
                    '2026-01-01 00:00:00', '2026-01-02 00:00:00', 1, 56, 4, 4.00, 0),
                   (200, 'Cola', 'COLA-1', NULL, 4.00, 2.00, 5, 1, 10, 1, 'unit',
                    '2026-01-01 00:00:00', '2026-01-02 00:00:00', 3, 11, 5, 10.00, 0),
                   (300, 'Own catalogue', NULL, NULL, 1.00, 1.00, 1, 1, 2, 1, 'unit',
                    '2026-01-01 00:00:00', '2026-01-02 00:00:00', 4, 12, 1, 1.00, 0);
        INSERT INTO ProductExternalIds (BusinessId, Provider, ExternalId, ProductId)
            VALUES (1, 1, '100', 5001),
                   (3, 1, '100', 200);
        INSERT INTO CategoryExternalIds (BusinessId, Provider, ExternalId, CategoryId)
            VALUES (1, 1, '10', 55),
                   (3, 1, '10', 11);
        INSERT INTO BusinessNayaxConnections (BusinessId, OperatorId, AccessTokenCiphertext, EncryptionKeyId,
                                              Status, CredentialRevision, MachineChecksPending, UpdatedAtUtc)
            VALUES (1, '2002736764', 'ciphertext', '2026-10', 1, 1, 0, '2026-01-01 00:00:00');
        INSERT INTO NayaxSales (BusinessId, TransactionID, MachineID, SettlementValue, MachineAuthorizationTime,
                                CostSource, CostingStatus)
            VALUES (1, 900, 7, 3.50, '2026-01-03 00:00:00', 0, 0),
                   (2, 901, 8, 2.50, '2026-01-03 00:00:00', 0, 0);
        """;

    [Fact]
    public async Task Migration_copies_every_nayax_id_onto_its_row_and_records_the_verification()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await MigrateAsync(options, PreviousMigration);

        Assert.DoesNotContain(
            "ExternalProductId", await MigrationSchemaProbe.ColumnNamesAsync(connection, "Products"));
        Assert.DoesNotContain(
            "SalesProvider", await MigrationSchemaProbe.ColumnNamesAsync(connection, "Businesses"));

        await ExecuteAsync(connection, Seed);
        await MigrateAsync(options, TargetMigration);

        await using var after = TestAppDbContext.Unrestricted(options);

        // Every Nayax link is now its row's own column, with the row's own business, and a row the
        // links never named has none - it is a local product, not one Nayax stopped returning.
        Assert.Equal(
            [(1, 5001L, "100"), (1, 5002L, null), (3, 200L, "100"), (4, 300L, null)],
            await after.Products
                .OrderBy(product => product.BusinessId).ThenBy(product => product.Id)
                .Select(product => ValueTuple.Create(product.BusinessId, product.Id, product.ExternalProductId))
                .ToListAsync());

        Assert.Equal(
            [(1, 55L, "10"), (1, 56L, null), (3, 11L, "10"), (4, 12L, null)],
            await after.Categories
                .OrderBy(category => category.BusinessId).ThenBy(category => category.Id)
                .Select(category => ValueTuple.Create(category.BusinessId, category.Id, category.ExternalCategoryId))
                .ToListAsync());

        // The provider, from each of the three kinds of evidence - a stored connection, a stored
        // sale, a provider link - and from nothing at all.
        Assert.Equal(
            [
                (1, (ExternalProvider?)ExternalProvider.Nayax),
                (2, ExternalProvider.Nayax),
                (3, ExternalProvider.Nayax),
                (4, null),
            ],
            await after.Businesses
                .OrderBy(business => business.Id)
                .Select(business => ValueTuple.Create(business.Id, business.SalesProvider))
                .ToListAsync());

        // The verification #760 reads before it drops the tables: one row per link table, carrying
        // how many Nayax rows were checked against the columns, and when.
        Assert.Equal(
            [("CategoryExternalIds", 2L), ("ProductExternalIds", 2L)],
            await VerificationCountsAsync(connection));
        Assert.All(
            await VerificationInstantsAsync(connection),
            instant => Assert.False(string.IsNullOrWhiteSpace(instant)));

        // The scratch guard the refusals are raised through is not left behind, and neither are the
        // link tables - which stay until #760 precisely because the code in production still reads
        // them.
        var tables = await MigrationSchemaProbe.TableNamesAsync(connection);
        Assert.DoesNotContain("ProviderIdentityCopyGuard", tables);
        Assert.Contains("ProductExternalIds", tables);
        Assert.Contains("CategoryExternalIds", tables);
    }

    /// <summary>
    /// The uniqueness the column model needs is per business and only over the rows that have an id,
    /// so the index is filtered: the migrated schema has to carry that filter, or a second product
    /// with no external id would be refused on a business that already has one.
    /// </summary>
    [Fact]
    public async Task The_migrated_schema_scopes_the_external_id_uniqueness_to_rows_that_have_one()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await MigrateAsync(options, PreviousMigration);
        await ExecuteAsync(connection, Seed);
        await MigrateAsync(options, TargetMigration);

        Assert.Contains(
            "IX_Products_BusinessId_ExternalProductId",
            await MigrationSchemaProbe.IndexNamesAsync(connection, "Products"));
        Assert.Contains(
            "IX_Categories_BusinessId_ExternalCategoryId",
            await MigrationSchemaProbe.IndexNamesAsync(connection, "Categories"));

        // Two products of one business with no external id at all: a plain unique index would have
        // allowed exactly one of them.
        await ExecuteAsync(
            connection,
            """
            INSERT INTO Products (Id, Name, UnitPrice, AverageUnitCost, QuantityInStock, LowStockThreshold,
                                  RestockTo, IsActive, CreatedAt, UpdatedAt, BusinessId, GstRule)
                VALUES (5003, 'Also local only', 1.00, 1.00, 0, 1, 2, 1,
                        '2026-01-01 00:00:00', '2026-01-01 00:00:00', 1, 0);
            """);

        // And the same external id in two businesses, which is legitimate: two operators' catalogues
        // may both contain product 100 (the seed already holds business 3's).
        await using var db = TestAppDbContext.Unrestricted(options);
        Assert.Equal(
            2,
            await db.Products.CountAsync(product => product.ExternalProductId == "100"));
    }

    /// <summary>
    /// A link naming a row in another business cannot be inserted while the link table's composite
    /// foreign key is enforced, so this is what a restored backup or an out-of-band write could have
    /// left behind. Copying from it would put one business's identifier on another's product, so the
    /// migration refuses before it copies anything at all.
    /// </summary>
    [Fact]
    public async Task A_link_that_crosses_a_business_fails_the_migration()
    {
        await AssertRefusedAsync(
            """
            PRAGMA foreign_keys = OFF;
            INSERT INTO ProductExternalIds (BusinessId, Provider, ExternalId, ProductId)
                VALUES (4, 1, '777', 5001);
            """,
            "different business");
    }

    /// <summary>
    /// One business, one external id, two links - here through a second provider, which the link
    /// tables' indexes allow because they are scoped by provider. The column is not: it holds the id
    /// of the business's one sales provider, so there is no honest way to copy both.
    /// </summary>
    [Fact]
    public async Task A_business_holding_the_same_external_id_twice_fails_the_migration()
    {
        await AssertRefusedAsync(
            """
            INSERT INTO ProductExternalIds (BusinessId, Provider, ExternalId, ProductId)
                VALUES (1, 2, '100', 5002);
            """,
            "the same external id twice");
    }

    /// <summary>The same ambiguity the other way round: one row, two external ids.</summary>
    [Fact]
    public async Task A_local_row_carrying_two_external_ids_fails_the_migration()
    {
        await AssertRefusedAsync(
            """
            INSERT INTO CategoryExternalIds (BusinessId, Provider, ExternalId, CategoryId)
                VALUES (1, 2, '99', 55);
            """,
            "more than one external id");
    }

    /// <summary>
    /// Seeds the fixture plus <paramref name="ambiguity"/> at the previous migration, runs the
    /// upgrade, and asserts it failed with <paramref name="expectedReason"/> and copied nothing: the
    /// columns exist (they are added before the checks) but every one of them is still empty.
    /// </summary>
    private static async Task AssertRefusedAsync(string ambiguity, string expectedReason)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await MigrateAsync(options, PreviousMigration);
        await ExecuteAsync(connection, Seed);
        await ExecuteAsync(connection, ambiguity);

        var refused = await Assert.ThrowsAsync<SqliteException>(() => MigrateAsync(options, TargetMigration));
        Assert.Contains(expectedReason, refused.Message, StringComparison.Ordinal);

        // Nothing was copied, and no verification was recorded: the migration ran in one
        // transaction, so the whole upgrade - including the columns it had added - is rolled back.
        Assert.DoesNotContain(
            "ExternalProductId", await MigrationSchemaProbe.ColumnNamesAsync(connection, "Products"));
        Assert.DoesNotContain(
            "ProviderIdentityColumnVerifications", await MigrationSchemaProbe.TableNamesAsync(connection));
    }

    private static async Task MigrateAsync(DbContextOptions<AppDbContext> options, string target)
    {
        await using var db = TestAppDbContext.Unrestricted(options);
        await db.GetService<IMigrator>().MigrateAsync(target);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<List<(string LinkTable, long VerifiedRowCount)>> VerificationCountsAsync(
        SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT "LinkTable", "VerifiedRowCount" FROM "ProviderIdentityColumnVerifications"
            ORDER BY "LinkTable";
            """;
        await using var reader = await command.ExecuteReaderAsync();

        var rows = new List<(string, long)>();
        while (await reader.ReadAsync()) rows.Add((reader.GetString(0), reader.GetInt64(1)));
        return rows;
    }

    private static async Task<List<string>> VerificationInstantsAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """SELECT "VerifiedAtUtc" FROM "ProviderIdentityColumnVerifications";""";
        await using var reader = await command.ExecuteReaderAsync();

        var instants = new List<string>();
        while (await reader.ReadAsync()) instants.Add(reader.GetString(0));
        return instants;
    }
}
