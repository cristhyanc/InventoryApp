using Inventory.Domain.Providers;
using Inventory.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Upgrade test for <c>AddMachineRestockSyncCoverage</c> (issue #891). The migration is additive: it
/// adds two nullable columns to <c>Machines</c> and does nothing else.
///
/// <para>Every machine that already exists therefore arrives with no recorded sync, and that is the
/// point rather than an omission: restocks were being synced before this metadata existed, so the
/// only honest statement about those machines is that nothing recorded it. The reminder then
/// over-warns, which is the safe direction - a backfill dated from an imported event or a stock
/// count would invent coverage that was never measured. Each machine's own name, activation and
/// provider id must come through untouched.</para>
/// </summary>
public class AddMachineRestockSyncCoverageMigrationTests
{
    private const string PreviousMigration = "20261024094143_AddPlatformOptionVisibility";
    private const string TargetMigration = "AddMachineRestockSyncCoverage";

    [Fact]
    public async Task Migration_adds_unset_coverage_and_preserves_every_machine()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using (var before = TestAppDbContext.Unrestricted(options))
        {
            await before.GetService<IMigrator>().MigrateAsync(PreviousMigration);

            var columnsBefore = await MigrationSchemaProbe.ColumnNamesAsync(connection, "Machines");
            Assert.DoesNotContain("LastSuccessfulRestockSyncStartedAtUtc", columnsBefore);
            Assert.DoesNotContain("LastSuccessfulRestockSyncCredentialRevision", columnsBefore);

            // Two businesses on the old schema, with a provider-named machine, a deactivated one and
            // a machine the operator created themselves - the state of a database at the moment this
            // migration is applied to it.
            await using var seed = connection.CreateCommand();
            seed.CommandText = """
                INSERT INTO Businesses (Name, TimeZoneId, Status, ContactEmail, Abn, AppliedAtUtc,
                                        DecidedAtUtc, RejectionReason, CreatedAtUtc, SalesProvider,
                                        SupplierPresetsCopiedAtUtc)
                    VALUES ('Vending Co', 'Australia/Sydney', 20, NULL, NULL, NULL,
                            NULL, NULL, '2026-02-10 04:00:00', 1, NULL);
                INSERT INTO Businesses (Name, TimeZoneId, Status, ContactEmail, Abn, AppliedAtUtc,
                                        DecidedAtUtc, RejectionReason, CreatedAtUtc, SalesProvider,
                                        SupplierPresetsCopiedAtUtc)
                    VALUES ('Coastal Vending', 'Australia/Brisbane', 20, NULL, NULL, NULL,
                            NULL, NULL, '2026-09-01 06:00:00', 1, NULL);
                INSERT INTO Machines (BusinessId, Name, IsActive, ExternalMachineId)
                    VALUES (1, 'Avenue Hotel', 1, '900');
                INSERT INTO Machines (BusinessId, Name, IsActive, ExternalMachineId)
                    VALUES (1, 'Retired Foyer', 0, '901');
                INSERT INTO Machines (BusinessId, Name, IsActive, ExternalMachineId)
                    VALUES (1, 'Workshop Fridge', 1, NULL);
                INSERT INTO Machines (BusinessId, Name, IsActive, ExternalMachineId)
                    VALUES (2, 'Coastal Kiosk', 1, '900');
                """;
            await seed.ExecuteNonQueryAsync();
        }

        await using (var after = TestAppDbContext.Unrestricted(options))
        {
            await after.GetService<IMigrator>().MigrateAsync(TargetMigration);

            var columnsAfter = await MigrationSchemaProbe.ColumnNamesAsync(connection, "Machines");
            Assert.Contains("LastSuccessfulRestockSyncStartedAtUtc", columnsAfter);
            Assert.Contains("LastSuccessfulRestockSyncCredentialRevision", columnsAfter);

            var machines = await after.Machines.AsNoTracking().OrderBy(machine => machine.Id).ToListAsync();

            Assert.Equal(
                ["Avenue Hotel", "Retired Foyer", "Workshop Fridge", "Coastal Kiosk"],
                machines.Select(machine => machine.Name));

            // Nothing is backfilled, for any machine, in either business.
            Assert.All(machines, machine => Assert.Null(machine.LastSuccessfulRestockSyncStartedAtUtc));
            Assert.All(
                machines, machine => Assert.Null(machine.LastSuccessfulRestockSyncCredentialRevision));

            // And each machine's own data came through unchanged - the deactivated one still
            // deactivated, the operator-created one still carrying no provider id, and the two
            // businesses' machines still legitimately sharing provider number 900.
            Assert.Equal([true, false, true, true], machines.Select(machine => machine.IsActive));
            Assert.Equal(
                [NayaxExternalId.From(900), NayaxExternalId.From(901), null, NayaxExternalId.From(900)],
                machines.Select(machine => machine.ExternalMachineId));
            Assert.Equal([1, 1, 1, 2], machines.Select(machine => machine.BusinessId));
        }
    }
}
