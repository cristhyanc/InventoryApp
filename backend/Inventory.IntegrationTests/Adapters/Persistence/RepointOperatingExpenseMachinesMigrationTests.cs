using Inventory.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Upgrade test for <c>RepointOperatingExpenseMachines</c> (issue #780, parent #329 child G2a): the
/// migration that moves <c>OperatingExpenses.MachineId</c> from the provider's own machine number
/// onto the local machine row and makes it a foreign key.
///
/// <para>What a reviewer cannot read off the SQL, and what each test here is for. <strong>No expense
/// may lose or change its machine</strong>, which is the whole risk of the change: an expense feeds
/// machine direct profit, so one that silently detached or landed on a different machine would move
/// money between machines. Two businesses that refer to the same provider machine number must each
/// keep their own machine, because that number is only unique per business. A <c>null</c> machine -
/// a whole-business expense - must stay null. And a non-null number with no local machine must make
/// the migration <strong>fail</strong> and change nothing at all, rather than null an association or
/// leave the upgrade half applied.</para>
/// </summary>
public class RepointOperatingExpenseMachinesMigrationTests
{
    /// <summary>
    /// The last migration before <c>AddMachines</c>: the seed goes in there, so the
    /// <c>AddMachines</c> backfill creates the machines from it exactly as it did in a real
    /// database, and this migration then joins to what that backfill actually wrote.
    /// </summary>
    private const string BeforeMachines = "20261018060210_ResolveNayaxSaleProductsByName";

    /// <summary>
    /// Everything up to and including <c>AddMachineBusinessKey</c>, the constraint-only migration
    /// the foreign key below needs; the seed is in place before it so the <c>AddMachines</c>
    /// backfill it carries creates the machines this migration then joins to.
    /// </summary>
    private const string MachinesMigration = "20261020021907_AddMachineBusinessKey";

    private const string TargetMigration = "RepointOperatingExpenseMachines";

    /// <summary>
    /// Two businesses that both sell through Nayax and both operate a machine 7, plus business 1's
    /// machine 8. The expenses: business 1 has one on machine 7, one on machine 8 and one on no
    /// machine at all; business 2 has one on its own machine 7. The sales rows exist only so the
    /// <c>AddMachines</c> backfill that runs as part of the upgrade creates the machines this
    /// migration then joins to - exactly as a real database reached this point.
    /// </summary>
    private const string Seed = """
        INSERT INTO Businesses (Id, Name, CreatedAtUtc, TimeZoneId, Status, GstRegistered, SalesProvider)
            VALUES (1, 'Vending A', '2026-01-01 00:00:00', 'Australia/Sydney', 1, NULL, 1),
                   (2, 'Vending B', '2026-01-01 00:00:00', 'Australia/Sydney', 1, NULL, 1);
        INSERT INTO NayaxSales (BusinessId, TransactionID, TransactionStatusId, MachineID, MachineName,
                                SettlementValue, MachineAuthorizationTime, CostingStatus, CostSource,
                                Provider, ProviderProductIdIsRaw)
            VALUES (1, 900, 12, 7, 'Lobby',     3.50, '2026-02-01 00:00:00', 0, 0, 1, 1),
                   (1, 901, 12, 8, 'Corridor',  3.50, '2026-02-01 00:00:00', 0, 0, 1, 1),
                   (2, 902, 12, 7, 'Gym foyer', 4.00, '2026-02-15 00:00:00', 0, 0, 1, 1);
        INSERT INTO OperatingExpenses (BusinessId, Category, Description, ExpenseDate, AmountExGst,
                                       GstAmount, TotalAmount, MachineId, CreatedAt, UpdatedAt)
            VALUES (1, 2, 'Lobby repair',     '2026-02-04 00:00:00', 10.00, 1.00, 11.00, 7,
                    '2026-02-04 00:00:00', '2026-02-04 00:00:00'),
                   (1, 2, 'Corridor repair',  '2026-02-05 00:00:00', 20.00, 2.00, 22.00, 8,
                    '2026-02-05 00:00:00', '2026-02-05 00:00:00'),
                   (1, 1, 'Insurance',        '2026-02-06 00:00:00', 30.00, 3.00, 33.00, NULL,
                    '2026-02-06 00:00:00', '2026-02-06 00:00:00'),
                   (2, 2, 'Gym foyer repair', '2026-02-07 00:00:00', 40.00, 4.00, 44.00, 7,
                    '2026-02-07 00:00:00', '2026-02-07 00:00:00');
        """;

    [Fact]
    public async Task Every_expense_keeps_the_machine_it_was_recorded_against()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await MigrateAsync(options, BeforeMachines);
        await ExecuteAsync(connection, Seed);
        await MigrateAsync(options, MachinesMigration);
        await MigrateAsync(options, TargetMigration);

        await using var after = TestAppDbContext.Unrestricted(options);

        // The machine each expense now names, read back through its local machine's provider number:
        // every expense is on the same machine it was recorded against, business 2's machine 7 is
        // its own machine and not business 1's, and the whole-business expense is still whole-business.
        var machines = await after.Machines.ToDictionaryAsync(
            machine => machine.Id, machine => (machine.BusinessId, machine.ExternalMachineId));
        var expenses = await after.OperatingExpenses
            .OrderBy(expense => expense.Id)
            .Select(expense => new { expense.BusinessId, expense.Description, expense.MachineId })
            .ToListAsync();

        (int BusinessId, string Description, string? ProviderMachineNumber)[] expected =
        [
            (1, "Lobby repair", "7"),
            (1, "Corridor repair", "8"),
            (1, "Insurance", null),
            (2, "Gym foyer repair", "7"),
        ];
        Assert.Equal(
            expected,
            expenses.Select(expense => (
                expense.BusinessId,
                expense.Description,
                expense.MachineId is null ? null : machines[expense.MachineId.Value].ExternalMachineId))
                .ToArray());

        // The repointed value is the local key, not the provider number it came from, and the
        // machine it names belongs to the expense's own business.
        foreach (var expense in expenses.Where(expense => expense.MachineId is not null))
        {
            Assert.Equal(expense.BusinessId, machines[expense.MachineId!.Value].BusinessId);
        }
        Assert.Contains(expenses, expense => expense.MachineId != 7 && expense.Description == "Lobby repair");

        // The scratch guard the refusals are raised through is not left behind.
        Assert.DoesNotContain(
            "OperatingExpenseMachineRepointGuard", await MigrationSchemaProbe.TableNamesAsync(connection));
    }

    /// <summary>
    /// The constraint the repoint exists for: after it, an expense can only name a machine of its
    /// own business, and the database is what says so rather than the ownership enforcer alone.
    /// </summary>
    [Fact]
    public async Task The_migrated_schema_refuses_an_expense_that_names_another_businesss_machine()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await MigrateAsync(options, BeforeMachines);
        await ExecuteAsync(connection, Seed);
        await MigrateAsync(options, MachinesMigration);
        await MigrateAsync(options, TargetMigration);
        await ExecuteAsync(connection, "PRAGMA foreign_keys = ON;");

        await using var after = TestAppDbContext.Unrestricted(options);
        var otherBusinessMachine = await after.Machines
            .Where(machine => machine.BusinessId == 2)
            .Select(machine => machine.Id)
            .SingleAsync();

        var refused = await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(
            connection,
            $"""
            INSERT INTO OperatingExpenses (BusinessId, Category, Description, ExpenseDate, AmountExGst,
                                           GstAmount, TotalAmount, MachineId, CreatedAt, UpdatedAt)
                VALUES (1, 2, 'Crossing businesses', '2026-02-08 00:00:00', 1.00, 0.10, 1.10,
                        {otherBusinessMachine.ToString(System.Globalization.CultureInfo.InvariantCulture)},
                        '2026-02-08 00:00:00', '2026-02-08 00:00:00');
            """));
        Assert.Contains("FOREIGN KEY", refused.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A machine number no local machine carries - a stored <c>0</c>, which is the absent value a
    /// non-nullable machine column records and which <c>AddMachines</c> deliberately created no
    /// machine for, or a number whose machine was removed. The association cannot be preserved, so
    /// the migration refuses rather than nulling it, and nothing at all is changed: no expense is
    /// updated and no constraint is added.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(99)]
    public async Task An_expense_whose_machine_number_has_no_local_machine_fails_the_migration(int machineNumber)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await MigrateAsync(options, BeforeMachines);
        await ExecuteAsync(connection, Seed);
        await MigrateAsync(options, MachinesMigration);
        await ExecuteAsync(
            connection,
            $"""
            UPDATE OperatingExpenses SET MachineId = {machineNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)}
                WHERE Description = 'Lobby repair';
            """);

        var refused = await Assert.ThrowsAsync<SqliteException>(() => MigrateAsync(options, TargetMigration));
        Assert.Contains("has no local machine in its business", refused.Message, StringComparison.Ordinal);

        // Nothing was written and nothing was constrained: the migration runs in one transaction, so
        // every expense still holds the provider number it held, including the ones that did match.
        await using var after = TestAppDbContext.Unrestricted(options);
        Assert.Equal(
            [machineNumber, 8, null, 7],
            await after.OperatingExpenses.OrderBy(expense => expense.Id)
                .Select(expense => (long?)expense.MachineId).ToListAsync());
        Assert.DoesNotContain(
            "FK_OperatingExpenses_Machines_BusinessId_MachineId",
            await MigrationSchemaProbe.CreateTableSqlAsync(connection, "OperatingExpenses"),
            StringComparison.Ordinal);
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
