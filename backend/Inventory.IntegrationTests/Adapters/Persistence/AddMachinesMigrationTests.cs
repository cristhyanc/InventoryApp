using Inventory.Domain.Machines;
using Inventory.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Upgrade test for <c>AddMachines</c> (issue #740, parent #329 child G1): the migration that
/// creates the <c>Machines</c> table and gives every machine this database already refers to a local
/// record.
///
/// <para>What a reviewer cannot read off the SQL, and what each test here is for. One machine must
/// appear for each distinct machine number a business refers to - across all seven columns that hold
/// one, not only the sales table - and exactly one, however many rows refer to it. Two businesses
/// that refer to the same machine number must each get their own machine, because an external id is
/// a value two operators may legitimately both hold. The name must come from the most recent stored
/// data that carried one and otherwise from the default
/// <see cref="MachineCataloguePolicy.DefaultName"/> states, which is the one claim the SQL mirrors
/// rather than calls. A business with no machine data at all must come out with no machines. And a
/// business holding machine data while its sales provider is not Nayax must make the migration
/// <strong>fail</strong>, because a machine number stored against it was issued by a provider
/// nothing has recorded.</para>
/// </summary>
public class AddMachinesMigrationTests
{
    private const string PreviousMigration = "20261018060210_ResolveNayaxSaleProductsByName";
    private const string TargetMigration = "AddMachines";

    /// <summary>
    /// Business 1 sells through Nayax and refers to four machines, each through a different kind of
    /// stored row: 7 through two sales (so the later name wins), 8 through a machine-refill stock
    /// adjustment and a machine count with no name anywhere, 9 through an operating expense, and 10
    /// through an inventory-cost transition that recorded a name. It also holds a sale with no
    /// machine at all (<c>MachineID</c> of 0), which refers to no machine.
    ///
    /// <para>Business 2 sells through Nayax and refers to machine 7 as well, under its own name.
    /// Business 3 sells through Nayax and refers to no machine. Business 4 has no provider at all
    /// and no machine data, which is the ordinary state of a business that never connected
    /// one.</para>
    /// </summary>
    private const string Seed = """
        INSERT INTO Businesses (Id, Name, CreatedAtUtc, TimeZoneId, Status, GstRegistered, SalesProvider)
            VALUES (1, 'Vending A', '2026-01-01 00:00:00', 'Australia/Sydney', 1, NULL, 1),
                   (2, 'Vending B', '2026-01-01 00:00:00', 'Australia/Sydney', 1, NULL, 1),
                   (3, 'Vending C', '2026-01-01 00:00:00', 'Australia/Sydney', 1, NULL, 1),
                   (4, 'Vending D', '2026-01-01 00:00:00', 'Australia/Sydney', 1, NULL, NULL);
        INSERT INTO Products (Id, Name, UnitPrice, AverageUnitCost, QuantityInStock, LowStockThreshold,
                              RestockTo, IsActive, Unit, CreatedAt, UpdatedAt, BusinessId,
                              CostingQuantity, InventoryValue, GstRule)
            VALUES (5001, 'Chips', 3.50, 1.10, 12, 2, 20, 1, 'unit',
                    '2026-01-01 00:00:00', '2026-01-02 00:00:00', 1, 12, 13.20, 0);
        INSERT INTO NayaxSales (BusinessId, TransactionID, TransactionStatusId, MachineID, MachineName,
                                SettlementValue, MachineAuthorizationTime, CostingStatus, CostSource,
                                Provider, ProviderProductIdIsRaw)
            VALUES (1, 900, 12, 7, 'Lobby (old name)', 3.50, '2026-02-01 00:00:00', 0, 0, 1, 1),
                   (1, 901, 12, 7, 'Lobby level 1',    3.50, '2026-03-01 00:00:00', 0, 0, 1, 1),
                   (1, 902, 12, 0, NULL,               2.50, '2026-03-02 00:00:00', 0, 0, 1, 1),
                   (2, 903, 12, 7, 'Gym foyer',        4.00, '2026-02-15 00:00:00', 0, 0, 1, 1);
        INSERT INTO StockAdjustments (BusinessId, ProductId, QuantityChange, QuantityAfter, Reason,
                                      MachineId, CreatedAt, EffectiveAt)
            VALUES (1, 5001, -4, 8, 4, 8, '2026-02-02 00:00:00', '2026-02-02 00:00:00');
        INSERT INTO MachineStockCounts (BusinessId, MachineId, ProductId, CountedQuantity, CountedAtUtc)
            VALUES (1, 8, 5001, 3, '2026-02-03 00:00:00');
        INSERT INTO OperatingExpenses (BusinessId, Category, Description, ExpenseDate, AmountExGst,
                                       GstAmount, TotalAmount, MachineId, CreatedAt, UpdatedAt)
            VALUES (1, 0, 'Nayax monthly fee', '2026-02-04 00:00:00', 10.00, 1.00, 11.00, 9,
                    '2026-02-04 00:00:00', '2026-02-04 00:00:00');
        INSERT INTO InventoryCostTransitionBaselines (Id, BusinessId, ProductId, CutoffAt, CostSource,
                                                      AverageUnitCost, InventoryValue, OpeningCostingQuantity,
                                                      HomeStockQuantity, MachineStockQuantity,
                                                      LegacyReplayedPhysicalQuantity,
                                                      LegacyPhysicalDiscrepancy, DataQualityNote, CreatedAt)
            VALUES (1, 1, 5001, '2026-01-15 00:00:00', 0, 1.10, 13.20, 12, 12, 6, 6, 0, '',
                    '2026-01-15 00:00:00');
        INSERT INTO InventoryCostTransitionMachineStocks (BusinessId, InventoryCostTransitionBaselineId,
                                                          MachineId, MachineName, StockQuantity, Source)
            VALUES (1, 1, 10, 'Reception', 6, 'Nayax PAR - MissingStockByMDB');
        """;

    [Fact]
    public async Task Migration_creates_one_machine_per_distinct_referenced_id_per_business()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await MigrateAsync(options, PreviousMigration);
        Assert.DoesNotContain("Machines", await MigrationSchemaProbe.TableNamesAsync(connection));

        await ExecuteAsync(connection, Seed);
        await MigrateAsync(options, TargetMigration);

        await using var after = TestAppDbContext.Unrestricted(options);

        // One machine per distinct number per business, with the provider's number recorded as text
        // and the name taken from the most recent stored data that carried one. Machine 8 and
        // machine 9 were never named, so they take the default shape; business 2's machine 7 is its
        // own machine with its own name, which is the point of a per-business external id.
        Assert.Equal(
            [
                (1, "Lobby level 1", "7", true),
                (1, "Machine 8", "8", true),
                (1, "Machine 9", "9", true),
                (1, "Reception", "10", true),
                (2, "Gym foyer", "7", true),
            ],
            await after.Machines
                .OrderBy(machine => machine.BusinessId).ThenBy(machine => machine.Id)
                .Select(machine => ValueTuple.Create(
                    machine.BusinessId, machine.Name, machine.ExternalMachineId, machine.IsActive))
                .ToListAsync());

        // The default name is the Domain's, mirrored in SQL because a migration cannot call it.
        Assert.Equal(
            MachineCataloguePolicy.DefaultName("8"),
            await after.Machines.Where(machine => machine.ExternalMachineId == "8")
                .Select(machine => machine.Name)
                .SingleAsync());

        // A business that refers to no machine gets none - including the one with no provider at
        // all, and including the sale that carried no machine number.
        // Projected rather than materialised whole, like the reads above: this context is pinned to
        // an older migration, so selecting every column of the entity would read columns a later
        // migration added (issue #891's restock-sync coverage, for one).
        Assert.Empty(
            await after.Machines
                .Where(machine => machine.BusinessId >= 3)
                .Select(machine => machine.Id)
                .ToListAsync());

        // Nothing is repointed: every stored reference still holds the provider's own number.
        Assert.Equal(
            [0L, 7L, 7L, 7L],
            await after.NayaxSales.OrderBy(sale => sale.MachineID).Select(sale => sale.MachineID).ToListAsync());

        // The scratch guard the refusals are raised through is not left behind.
        Assert.DoesNotContain(
            "MachineBackfillGuard", await MigrationSchemaProbe.TableNamesAsync(connection));
    }

    /// <summary>
    /// The uniqueness the machine column needs is per business and only over the machines that have
    /// a provider id, so the index is filtered: the migrated schema has to carry that filter, or a
    /// second machine somebody typed in would be refused on a business that already has one.
    /// </summary>
    [Fact]
    public async Task The_migrated_schema_scopes_the_external_machine_id_uniqueness_to_rows_that_have_one()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await MigrateAsync(options, PreviousMigration);
        await ExecuteAsync(connection, Seed);
        await MigrateAsync(options, TargetMigration);

        Assert.Contains(
            "IX_Machines_BusinessId_ExternalMachineId",
            await MigrationSchemaProbe.IndexNamesAsync(connection, "Machines"));

        // Two machines of one business with no provider id at all: a plain unique index would have
        // allowed exactly one of them.
        await ExecuteAsync(
            connection,
            """
            INSERT INTO Machines (BusinessId, Name, IsActive, ExternalMachineId)
                VALUES (1, 'Corridor A', 1, NULL), (1, 'Corridor B', 1, NULL);
            """);

        // One business cannot hold the same provider machine id twice.
        var duplicate = await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(
            connection,
            """
            INSERT INTO Machines (BusinessId, Name, IsActive, ExternalMachineId)
                VALUES (1, 'Another lobby', 1, '7');
            """));
        Assert.Contains("UNIQUE", duplicate.Message, StringComparison.OrdinalIgnoreCase);

        // And the same provider machine id in two businesses is legitimate: the seed already holds
        // business 1's and business 2's machine 7.
        await using var db = TestAppDbContext.Unrestricted(options);
        Assert.Equal(2, await db.Machines.CountAsync(machine => machine.ExternalMachineId == "7"));
    }

    /// <summary>
    /// A machine number stored against a business was issued by that business's sales provider, and
    /// a business's provider cannot change (issue #759). So machine data on a business whose provider
    /// is not Nayax is a state nothing can explain, and copying the number into a column every later
    /// lookup reads as "the provider's id" would record a claim nothing supports: the migration
    /// refuses before it writes anything at all.
    /// </summary>
    [Theory]
    [InlineData("NULL")]
    [InlineData("2")]
    public async Task Machine_data_on_a_business_whose_provider_is_not_Nayax_fails_the_migration(string provider)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await MigrateAsync(options, PreviousMigration);
        await ExecuteAsync(connection, Seed);
        await ExecuteAsync(connection, $"""UPDATE Businesses SET SalesProvider = {provider} WHERE Id = 1;""");

        var refused = await Assert.ThrowsAsync<SqliteException>(() => MigrateAsync(options, TargetMigration));
        Assert.Contains("sales provider is not Nayax", refused.Message, StringComparison.Ordinal);

        // Nothing was created: the migration runs in one transaction, so the whole upgrade -
        // including the table it had added - is rolled back.
        Assert.DoesNotContain("Machines", await MigrationSchemaProbe.TableNamesAsync(connection));
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
