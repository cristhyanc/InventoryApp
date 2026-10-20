using System.Globalization;
using Inventory.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Upgrade test for <c>RepointStockMachineReferences</c> (issue #741, parent #329 child G2, the stock
/// half): the migration that moves <c>StockAdjustments.MachineId</c> and
/// <c>MachineStockCounts.MachineId</c> from the provider's own machine number onto the local machine
/// row, and <c>AddStockMachineForeignKeys</c>, which then makes each a foreign key.
///
/// <para>The risk is the same as the expense half's (<c>RepointOperatingExpenseMachines</c>, issue
/// #780) and so are the tests. <strong>No refill and no count may lose or change its
/// machine</strong>: the machine-stock ledger is built from exactly those rows, so one that detached
/// or landed on another machine would move expected stock between machines. Two businesses that
/// refer to the same provider machine number must each keep their own machine. A movement that names
/// no machine must stay that way. A number with no local machine must make the migration
/// <strong>fail</strong> and change nothing. And <c>Down</c> must put the provider's numbers back,
/// refusing for a machine that has none.</para>
/// </summary>
public class RepointStockMachineReferencesMigrationTests
{
    /// <summary>
    /// The last migration before <c>AddMachines</c>: the seed goes in there, so the
    /// <c>AddMachines</c> backfill creates the machines from it exactly as it did in a real
    /// database, and this migration then joins to what that backfill actually wrote.
    /// </summary>
    private const string BeforeMachines = "20261018060210_ResolveNayaxSaleProductsByName";

    /// <summary>The migration immediately before this one: the state <c>Down</c> returns to.</summary>
    private const string PreviousMigration = "20261020021941_RepointOperatingExpenseMachines";

    /// <summary>
    /// The repoint and the foreign keys are two migrations (<c>RepointStockMachineReferences</c>,
    /// then <c>AddStockMachineForeignKeys</c>), and a real upgrade applies both, so the tests go to
    /// the second.
    /// </summary>
    private const string TargetMigration = "AddStockMachineForeignKeys";

    /// <summary>
    /// Two businesses that both sell through Nayax and both operate a machine 7, plus business 1's
    /// machine 8. Business 1 has a refill into machine 7, one into machine 8, a storage restock that
    /// names no machine, and a count of machine 7; business 2 has a refill into and a count of its
    /// own machine 7. The <c>AddMachines</c> backfill that runs as part of the upgrade creates the
    /// machines from these very columns.
    /// </summary>
    private const string Seed = """
        INSERT INTO Businesses (Id, Name, CreatedAtUtc, TimeZoneId, Status, GstRegistered, SalesProvider)
            VALUES (1, 'Vending A', '2026-01-01 00:00:00', 'Australia/Sydney', 1, NULL, 1),
                   (2, 'Vending B', '2026-01-01 00:00:00', 'Australia/Sydney', 1, NULL, 1);
        INSERT INTO Products (Id, Name, UnitPrice, AverageUnitCost, QuantityInStock, LowStockThreshold,
                              RestockTo, IsActive, CreatedAt, UpdatedAt, BusinessId, GstRule)
            VALUES (100, 'Coke',  3.50, 1.00, 12, 1, 2, 1, '2026-01-01 00:00:00', '2026-01-01 00:00:00', 1, 0),
                   (200, 'Chips', 3.50, 1.00, 5,  1, 2, 1, '2026-01-01 00:00:00', '2026-01-01 00:00:00', 2, 0);
        INSERT INTO StockAdjustments (BusinessId, ProductId, QuantityChange, QuantityAfter, Reason,
                                      Source, MachineId, Notes, CreatedAt, EffectiveAt)
            VALUES (1, 100, -6, 4, 4, 0, 7,    'Lobby refill',     '2026-02-01 00:00:00', '2026-02-01 00:00:00'),
                   (1, 100, -2, 2, 4, 0, 8,    'Corridor refill',  '2026-02-02 00:00:00', '2026-02-02 00:00:00'),
                   (1, 100, 10, 12, 0, 0, NULL, 'Storage restock', '2026-02-03 00:00:00', '2026-02-03 00:00:00'),
                   (2, 200, -3, 5, 4, 0, 7,    'Gym foyer refill', '2026-02-04 00:00:00', '2026-02-04 00:00:00');
        INSERT INTO MachineStockCounts (BusinessId, MachineId, ProductId, CountedQuantity, CountedAtUtc, Notes)
            VALUES (1, 7, 100, 6, '2026-02-05 00:00:00', 'Lobby count'),
                   (2, 7, 200, 3, '2026-02-06 00:00:00', 'Gym foyer count');
        """;

    [Fact]
    public async Task Every_refill_and_count_keeps_the_machine_it_was_recorded_against()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await SeededUpToAsync(connection, PreviousMigration);
        await MigrateAsync(options, TargetMigration);

        await using var after = TestAppDbContext.Unrestricted(options);
        var machines = await after.Machines.ToDictionaryAsync(
            machine => machine.Id, machine => (machine.BusinessId, machine.ExternalMachineId));

        string? ProviderNumber(long? localMachineId) =>
            localMachineId is null ? null : machines[localMachineId.Value].ExternalMachineId;

        var movements = await after.StockAdjustments
            .OrderBy(movement => movement.Id)
            .Select(movement => new { movement.BusinessId, movement.Notes, movement.MachineId })
            .ToListAsync();
        (int BusinessId, string? Notes, string? ProviderMachineNumber)[] expectedMovements =
        [
            (1, "Lobby refill", "7"),
            (1, "Corridor refill", "8"),
            (1, "Storage restock", null),
            (2, "Gym foyer refill", "7"),
        ];
        Assert.Equal(
            expectedMovements,
            movements.Select(movement => (movement.BusinessId, movement.Notes, ProviderNumber(movement.MachineId))).ToArray());

        var counts = await after.MachineStockCounts
            .OrderBy(count => count.Id)
            .Select(count => new { count.BusinessId, count.Notes, count.MachineId })
            .ToListAsync();
        (int BusinessId, string? Notes, string? ProviderMachineNumber)[] expectedCounts =
        [
            (1, "Lobby count", "7"),
            (2, "Gym foyer count", "7"),
        ];
        Assert.Equal(
            expectedCounts,
            counts.Select(count => (count.BusinessId, count.Notes, ProviderNumber(count.MachineId))).ToArray());

        // Each repointed value is a local key of the row's own business - business 2's machine 7 is
        // its own machine, not business 1's - and none is still the provider number it came from.
        foreach (var (businessId, machineId) in movements.Where(m => m.MachineId is not null)
                     .Select(m => (m.BusinessId, m.MachineId!.Value))
                     .Concat(counts.Select(c => (c.BusinessId, c.MachineId))))
        {
            Assert.Equal(businessId, machines[machineId].BusinessId);
        }
        Assert.NotEqual(
            counts[0].MachineId, counts[1].MachineId);

        Assert.DoesNotContain(
            "StockMachineRepointGuard", await MigrationSchemaProbe.TableNamesAsync(connection));
    }

    /// <summary>
    /// The constraints the repoint exists for: after it, a refill or a count can only name a machine
    /// of its own business, and the database is what says so.
    /// </summary>
    [Fact]
    public async Task The_migrated_schema_refuses_a_refill_or_a_count_that_names_another_businesss_machine()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await SeededUpToAsync(connection, PreviousMigration);
        await MigrateAsync(options, TargetMigration);

        await using var after = TestAppDbContext.Unrestricted(options);
        var ownMachine = (await after.Machines
            .Where(machine => machine.BusinessId == 1)
            .Select(machine => machine.Id)
            .FirstAsync()).ToString(CultureInfo.InvariantCulture);
        var otherBusinessMachine = (await after.Machines
            .Where(machine => machine.BusinessId == 2)
            .Select(machine => machine.Id)
            .SingleAsync()).ToString(CultureInfo.InvariantCulture);
        const string ownProduct = "100";
        await ExecuteAsync(connection, "PRAGMA foreign_keys = ON;");

        // The control: the same row naming the business's own machine is accepted, so the refusals
        // below are the machine reference and nothing else.
        await ExecuteAsync(
            connection,
            $"""
            INSERT INTO MachineStockCounts (BusinessId, MachineId, ProductId, CountedQuantity, CountedAtUtc)
                VALUES (1, {ownMachine}, {ownProduct}, 1, '2026-02-08 00:00:00');
            """);

        var refusedMovement = await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(
            connection,
            $"""
            INSERT INTO StockAdjustments (BusinessId, ProductId, QuantityChange, QuantityAfter, Reason,
                                          Source, MachineId, Notes, CreatedAt, EffectiveAt)
                VALUES (1, {ownProduct}, -1, 1, 4, 0, {otherBusinessMachine}, 'Crossing businesses',
                        '2026-02-08 00:00:00', '2026-02-08 00:00:00');
            """));
        Assert.Contains("FOREIGN KEY", refusedMovement.Message, StringComparison.OrdinalIgnoreCase);

        var refusedCount = await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(
            connection,
            $"""
            INSERT INTO MachineStockCounts (BusinessId, MachineId, ProductId, CountedQuantity, CountedAtUtc)
                VALUES (1, {otherBusinessMachine}, {ownProduct}, 1, '2026-02-08 00:00:00');
            """));
        Assert.Contains("FOREIGN KEY", refusedCount.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A machine number no local machine carries - a stored <c>0</c>, which <c>AddMachines</c>
    /// deliberately created no machine for, or a number whose machine was removed. The association
    /// cannot be preserved, so the migration refuses rather than nulling it, and nothing at all is
    /// changed: neither table is updated and no constraint is added. The refusal on one table also
    /// leaves the other untouched.
    /// </summary>
    [Theory]
    [InlineData("StockAdjustments", "Lobby refill", 0)]
    [InlineData("StockAdjustments", "Lobby refill", 99)]
    [InlineData("MachineStockCounts", "Lobby count", 0)]
    [InlineData("MachineStockCounts", "Lobby count", 99)]
    public async Task A_row_whose_machine_number_has_no_local_machine_fails_the_migration(
        string table, string notes, int machineNumber)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await SeededUpToAsync(connection, PreviousMigration);
        await ExecuteAsync(
            connection,
            $"""
            UPDATE "{table}" SET MachineId = {machineNumber.ToString(CultureInfo.InvariantCulture)}
                WHERE Notes = '{notes}';
            """);

        var refused = await Assert.ThrowsAsync<SqliteException>(() => MigrateAsync(options, TargetMigration));
        Assert.Contains("has no local machine in its", refused.Message, StringComparison.Ordinal);

        await using var after = TestAppDbContext.Unrestricted(options);
        var lobby = table == "StockAdjustments" ? (long)machineNumber : 7L;
        Assert.Equal(
            [lobby, 8, null, 7],
            await after.StockAdjustments.OrderBy(movement => movement.Id)
                .Select(movement => movement.MachineId).ToListAsync());
        Assert.Equal(
            [table == "MachineStockCounts" ? machineNumber : 7L, 7L],
            await after.MachineStockCounts.OrderBy(count => count.Id)
                .Select(count => count.MachineId).ToListAsync());
        Assert.DoesNotContain(
            "FK_StockAdjustments_Machines_BusinessId_MachineId",
            await MigrationSchemaProbe.CreateTableSqlAsync(connection, "StockAdjustments"),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "FK_MachineStockCounts_Machines_BusinessId_MachineId",
            await MigrationSchemaProbe.CreateTableSqlAsync(connection, "MachineStockCounts"),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The downgrade puts every provider number back exactly where it was, because the release it
    /// returns to reads these columns as provider numbers, and drops the constraints again.
    /// </summary>
    [Fact]
    public async Task Down_restores_every_provider_machine_number()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await SeededUpToAsync(connection, PreviousMigration);
        await MigrateAsync(options, TargetMigration);

        await MigrateAsync(options, PreviousMigration);

        await using var after = TestAppDbContext.Unrestricted(options);
        Assert.Equal(
            [7L, 8L, null, 7L],
            await after.StockAdjustments.OrderBy(movement => movement.Id)
                .Select(movement => movement.MachineId).ToListAsync());
        Assert.Equal(
            [7L, 7L],
            await after.MachineStockCounts.OrderBy(count => count.Id)
                .Select(count => count.MachineId).ToListAsync());
        Assert.DoesNotContain(
            "FK_StockAdjustments_Machines_BusinessId_MachineId",
            await MigrationSchemaProbe.CreateTableSqlAsync(connection, "StockAdjustments"),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "StockMachineRepointGuard", await MigrationSchemaProbe.TableNamesAsync(connection));
    }

    /// <summary>
    /// A machine the business created locally has no provider number, so a refill recorded against
    /// it after the upgrade has nothing to go back to: the repoint's downgrade refuses and changes no
    /// machine reference. The foreign keys' own downgrade, which runs first, has already committed,
    /// so the database is left exactly at <c>RepointStockMachineReferences</c> - local ids with no
    /// constraint - which the code of that release reads correctly.
    /// </summary>
    [Fact]
    public async Task Down_refuses_when_a_refill_names_a_machine_without_a_provider_number()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await SeededUpToAsync(connection, PreviousMigration);
        await MigrateAsync(options, TargetMigration);
        await ExecuteAsync(
            connection,
            """
            INSERT INTO Machines (BusinessId, Name, IsActive, ExternalMachineId) VALUES (1, 'Back office', 1, NULL);
            UPDATE StockAdjustments SET MachineId = last_insert_rowid() WHERE Notes = 'Storage restock';
            """);

        await using var before = TestAppDbContext.Unrestricted(options);
        var localIds = await before.StockAdjustments.OrderBy(movement => movement.Id)
            .Select(movement => movement.MachineId).ToListAsync();

        var refused = await Assert.ThrowsAsync<SqliteException>(() => MigrateAsync(options, PreviousMigration));
        Assert.Contains("carries no provider machine number", refused.Message, StringComparison.Ordinal);

        await using var after = TestAppDbContext.Unrestricted(options);
        Assert.Equal(
            localIds,
            await after.StockAdjustments.OrderBy(movement => movement.Id)
                .Select(movement => movement.MachineId).ToListAsync());
        Assert.Contains(
            "20261020081911_RepointStockMachineReferences",
            await after.Database.GetAppliedMigrationsAsync());
        Assert.DoesNotContain(
            "StockMachineRepointGuard", await MigrationSchemaProbe.TableNamesAsync(connection));
    }

    /// <summary>
    /// Opens <paramref name="connection"/>, migrates to just before <c>AddMachines</c>, seeds the
    /// provider-numbered rows there, and migrates on to <paramref name="target"/>.
    /// </summary>
    private static async Task<DbContextOptions<AppDbContext>> SeededUpToAsync(
        SqliteConnection connection, string target)
    {
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await MigrateAsync(options, BeforeMachines);
        await ExecuteAsync(connection, Seed);
        await MigrateAsync(options, target);
        return options;
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
}
