using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Upgrade test for <c>AddBusinessState</c> (issue #933). The migration is additive: it adds one
/// nullable text column to <c>Businesses</c> and does nothing else.
///
/// <para>Every business that already exists therefore arrives with <c>NULL</c> - nobody has
/// recorded a state for it - and that is the point rather than an omission: the state cannot be
/// derived from the business's time zone, which is the reason the column exists at all, and
/// inventing one would record a business as trading somewhere it may not. The seeded businesses
/// here are deliberately in <c>Australia/Sydney</c> and <c>Australia/Brisbane</c>, the two zones a
/// backfill would have been most tempted to guess NSW and QLD from.</para>
/// </summary>
public class AddBusinessStateMigrationTests
{
    private const string PreviousMigration = "20261025121047_AddPickListRefillLineMachine";
    private const string TargetMigration = "AddBusinessState";

    [Fact]
    public async Task Migration_adds_an_unrecorded_state_and_preserves_every_business()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using (var before = TestAppDbContext.Unrestricted(options))
        {
            await before.GetService<IMigrator>().MigrateAsync(PreviousMigration);

            Assert.DoesNotContain(
                "State",
                await MigrationSchemaProbe.ColumnNamesAsync(connection, "Businesses"));

            // Two businesses on the old schema - a trading one that predates every profile column
            // and a pending application that recorded them - which is the state of a database at
            // the moment this migration is applied to it.
            await using var seed = connection.CreateCommand();
            seed.CommandText = """
                INSERT INTO Businesses (Name, TimeZoneId, Status, ContactEmail, Abn, GstRegistered,
                                        AppliedAtUtc, DecidedAtUtc, RejectionReason, CreatedAtUtc)
                    VALUES ('Vending Co', 'Australia/Sydney', 20, NULL, NULL, NULL, NULL,
                            NULL, NULL, '2026-02-10 04:00:00');
                INSERT INTO Businesses (Name, TimeZoneId, Status, ContactEmail, Abn, GstRegistered,
                                        AppliedAtUtc, DecidedAtUtc, RejectionReason, CreatedAtUtc)
                    VALUES ('Coastal Vending', 'Australia/Brisbane', 10, 'owner@coastal.example.com',
                            '51824753556', 1, '2026-09-01 06:00:00', NULL, NULL, '2026-09-01 06:00:00');
                """;
            await seed.ExecuteNonQueryAsync();
        }

        await using (var after = TestAppDbContext.Unrestricted(options))
        {
            await after.GetService<IMigrator>().MigrateAsync(TargetMigration);

            Assert.Contains(
                "State",
                await MigrationSchemaProbe.ColumnNamesAsync(connection, "Businesses"));

            // Projected rather than materialised, as the other migration tests here are: this
            // database stops at TargetMigration, so a column a later migration adds would exist in
            // the EF model and not in this schema.
            var businesses = await after.Businesses
                .AsNoTracking()
                .OrderBy(business => business.Id)
                .Select(business => new
                {
                    business.Name,
                    business.State,
                    business.Status,
                    business.TimeZoneId,
                    business.ContactEmail,
                    business.Abn,
                    business.GstRegistered,
                    business.AppliedAtUtc,
                })
                .ToListAsync();

            // Nothing is backfilled, and nothing is inferred from either time zone.
            Assert.Equal(["Vending Co", "Coastal Vending"], businesses.Select(business => business.Name));
            Assert.All(businesses, business => Assert.Null(business.State));

            // And each business's own data came through unchanged, the pending application's
            // detail included.
            Assert.Equal(BusinessStatus.Active, businesses[0].Status);
            Assert.Equal("Australia/Sydney", businesses[0].TimeZoneId);
            Assert.Equal(BusinessStatus.Pending, businesses[1].Status);
            Assert.Equal("Australia/Brisbane", businesses[1].TimeZoneId);
            Assert.Equal("owner@coastal.example.com", businesses[1].ContactEmail);
            Assert.Equal("51824753556", businesses[1].Abn);
            Assert.True(businesses[1].GstRegistered);
            Assert.Equal(new DateTime(2026, 9, 1, 6, 0, 0, DateTimeKind.Utc), businesses[1].AppliedAtUtc);

            // The column the migration added is a column of this table and not of another: the
            // upgrade adds exactly one, and no other table gained or lost one.
            Assert.Contains("TimeZoneId", await MigrationSchemaProbe.ColumnNamesAsync(connection, "Businesses"));
            Assert.DoesNotContain(
                "State",
                await MigrationSchemaProbe.ColumnNamesAsync(connection, "BusinessMemberships"));
        }
    }
}
