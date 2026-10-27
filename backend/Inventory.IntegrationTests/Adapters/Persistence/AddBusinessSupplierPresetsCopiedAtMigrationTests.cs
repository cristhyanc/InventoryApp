using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Upgrade test for <c>AddBusinessSupplierPresetsCopiedAt</c> (issue #849). The migration is
/// additive: it adds one nullable instant to <c>Businesses</c> and does nothing else.
///
/// <para>Every business that already exists therefore arrives with <c>NULL</c> - "no copy was ever
/// made" - and that is the whole point rather than an omission: those businesses already hold the
/// suppliers they use, so a migration that stamped an instant on them would claim an event that
/// never happened, and one that inserted suppliers would change a trading business's own data. The
/// suppliers each of them already has must come through untouched, and the platform presets
/// <c>AddSupplierPresets</c> seeded must neither be copied nor changed.</para>
/// </summary>
public class AddBusinessSupplierPresetsCopiedAtMigrationTests
{
    private const string PreviousMigration = "20261023012310_AddSupplierPresets";
    private const string TargetMigration = "AddBusinessSupplierPresetsCopiedAt";

    [Fact]
    public async Task Migration_adds_an_unset_marker_and_preserves_every_business_and_its_suppliers()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using (var before = TestAppDbContext.Unrestricted(options))
        {
            await before.GetService<IMigrator>().MigrateAsync(PreviousMigration);

            Assert.DoesNotContain(
                "SupplierPresetsCopiedAtUtc",
                await MigrationSchemaProbe.ColumnNamesAsync(connection, "Businesses"));

            // Two businesses on the old schema, each with a supplier of its own - the state of a
            // database at the moment this migration is applied to it.
            await using var seed = connection.CreateCommand();
            seed.CommandText = """
                INSERT INTO Businesses (Name, TimeZoneId, Status, ContactEmail, Abn, AppliedAtUtc,
                                        DecidedAtUtc, RejectionReason, CreatedAtUtc)
                    VALUES ('Vending Co', 'Australia/Sydney', 20, NULL, NULL, NULL,
                            NULL, NULL, '2026-02-10 04:00:00');
                INSERT INTO Businesses (Name, TimeZoneId, Status, ContactEmail, Abn, AppliedAtUtc,
                                        DecidedAtUtc, RejectionReason, CreatedAtUtc)
                    VALUES ('Coastal Vending', 'Australia/Brisbane', 10, 'owner@coastal.example.com',
                            '51824753556', '2026-09-01 06:00:00', NULL, NULL, '2026-09-01 06:00:00');
                INSERT INTO Suppliers (Name, ContactName, Phone, Email, Address, Url, BusinessId,
                                       ProductLineGstDefault, DeliveryGstDefault, PackageGstDefault)
                    VALUES ('Wholesale Co', 'Pat', '555', 'pat@wholesale.invalid', '1 Road',
                            'https://wholesale.invalid', 1, 0, 0, 0);
                """;
            await seed.ExecuteNonQueryAsync();
        }

        await using (var after = TestAppDbContext.Unrestricted(options))
        {
            await after.GetService<IMigrator>().MigrateAsync(TargetMigration);

            Assert.Contains(
                "SupplierPresetsCopiedAtUtc",
                await MigrationSchemaProbe.ColumnNamesAsync(connection, "Businesses"));

            // Nothing is backfilled: both businesses record no copy, the trading one included.
            var businesses = await after.Businesses
                .AsNoTracking()
                .OrderBy(business => business.Id)
                .ToListAsync();

            Assert.Equal(["Vending Co", "Coastal Vending"], businesses.Select(business => business.Name));
            Assert.All(businesses, business => Assert.Null(business.SupplierPresetsCopiedAtUtc));

            // And each business's own data came through unchanged, the pending application's
            // detail included.
            Assert.Equal(BusinessStatus.Active, businesses[0].Status);
            Assert.Equal(BusinessStatus.Pending, businesses[1].Status);
            Assert.Equal("owner@coastal.example.com", businesses[1].ContactEmail);
            Assert.Equal("51824753556", businesses[1].Abn);
            Assert.Equal(new DateTime(2026, 9, 1, 6, 0, 0, DateTimeKind.Utc), businesses[1].AppliedAtUtc);

            // No supplier was invented for either business, and the one that existed is intact.
            var supplier = Assert.Single(await after.Suppliers.AsNoTracking().ToListAsync());
            Assert.Equal(("Wholesale Co", "https://wholesale.invalid", 1), (supplier.Name, supplier.Url, supplier.BusinessId));

            // The platform's own suggestions are exactly the rows AddSupplierPresets seeded: this
            // migration copies nothing and changes nothing about them. Read with SQL rather than
            // through the entity, because this test stands on a schema that predates issue #931's
            // supplier-master columns and the model that maps them would ask for them.
            Assert.Equal(
                ["Big W", "Coles", "Costco", "Woolworths"],
                await PresetNamesAsync(connection));
        }
    }

    private static async Task<List<string>> PresetNamesAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Name FROM SupplierPresets ORDER BY Name;";
        await using var reader = await command.ExecuteReaderAsync();

        var names = new List<string>();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }
}
