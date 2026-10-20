using Inventory.Application.Expenses;
using Inventory.Domain.Expenses;
using Inventory.Domain.Providers;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using Inventory.Application.Documents;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// An operating expense's machine against the real relational schema (issue #780, parent #329 child
/// G2a): it is this business's own machine row, and nothing else may be stored in that column.
///
/// <para>Relational and with two businesses, because every claim here is the database's or the
/// tenant boundary's: the foreign key on <c>(BusinessId, MachineId)</c>, the query filter that makes
/// another business's machine invisible to the check, and the round trip of a local machine id
/// through create, read, update and the list filter.</para>
/// </summary>
public class OperatingExpenseLocalMachineTests
{
    private const int BusinessA = 1;
    private const int BusinessB = 2;

    /// <summary>
    /// A business with no sales provider at all that nevertheless holds a machine carrying a
    /// provider id. It is seeded directly, because no path creates it - which is the point: the
    /// column does not say who issued the value, so the lookup has to ask the business rather than
    /// trust the column.
    /// </summary>
    private const int BusinessWithoutProvider = 3;

    private static async Task<DbContextOptions<AppDbContext>> CreateSqliteAsync(SqliteConnection connection)
    {
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var setup = TestAppDbContext.Unrestricted(options);
        await setup.Database.EnsureCreatedAsync();
        setup.Businesses.AddRange(
            new Business
            {
                Id = BusinessA,
                Name = "Vending A",
                Status = BusinessStatus.Active,
                CreatedAtUtc = DateTime.UtcNow,
                SalesProvider = ExternalProvider.Nayax,
            },
            new Business
            {
                Id = BusinessB,
                Name = "Vending B",
                Status = BusinessStatus.Active,
                CreatedAtUtc = DateTime.UtcNow,
                SalesProvider = ExternalProvider.Nayax,
            },
            new Business
            {
                Id = BusinessWithoutProvider,
                Name = "Vending C",
                Status = BusinessStatus.Active,
                CreatedAtUtc = DateTime.UtcNow,
                SalesProvider = null,
            });
        // Both Nayax businesses operate a machine the provider calls 7 - an external id is a value
        // two operators may legitimately both hold - and each gets its own local key.
        setup.Machines.AddRange(
            new LocalMachine { BusinessId = BusinessA, Name = "Lobby", ExternalMachineId = "7" },
            new LocalMachine { BusinessId = BusinessB, Name = "Gym foyer", ExternalMachineId = "7" },
            new LocalMachine { BusinessId = BusinessWithoutProvider, Name = "Hall", ExternalMachineId = "7" });
        await setup.SaveChangesAsync();
        return options;
    }

    private static async Task<long> MachineIdAsync(DbContextOptions<AppDbContext> options, int businessId)
    {
        await using var db = TestAppDbContext.Unrestricted(options);
        return await db.Machines.Where(machine => machine.BusinessId == businessId)
            .Select(machine => machine.Id).SingleAsync();
    }

    private static OperatingExpenseFields Fields(long? machineId) => new(
        new DateTime(2026, 3, 1), ExpenseCategory.RepairsAndMaintenance, "Door repair", 10m, 1m, 11m,
        null, null, machineId, null, null, null);

    // No attachment is submitted anywhere here, so document storage is never reached.
    private static CreateOperatingExpense Create(AppDbContext db) =>
        new(new EfOperatingExpenseStore(db), Mock.Of<IDocumentStorage>(), new EfLocalMachineStore(db));

    private static UpdateOperatingExpense Update(AppDbContext db) =>
        new(new EfOperatingExpenseStore(db), Mock.Of<IDocumentStorage>(), new EfLocalMachineStore(db));

    [Fact]
    public async Task An_expense_round_trips_the_local_machine_id_through_create_read_update_and_filter()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        var machineA = await MachineIdAsync(options, BusinessA);
        await using var db = TestAppDbContext.For(options, BusinessA);

        var created = await Create(db).Handle(Fields(machineA), null, CancellationToken.None);
        Assert.True(created.IsValid);
        Assert.Equal(machineA, created.Record!.MachineId);

        // What is actually stored is the local key, not the provider's number 7.
        await using (var verify = TestAppDbContext.Unrestricted(options))
        {
            Assert.Equal(machineA, await verify.OperatingExpenses.Select(x => x.MachineId).SingleAsync());
        }

        var read = await new GetOperatingExpense(new EfOperatingExpenseStore(db))
            .Handle(created.Record.Id, CancellationToken.None);
        Assert.Equal(machineA, read!.MachineId);

        var filtered = await new ListOperatingExpenses(new EfOperatingExpenseStore(db)).Handle(
            new OperatingExpenseFilter(null, null, null, null, null, machineA), CancellationToken.None);
        Assert.Equal(machineA, Assert.Single(filtered).MachineId);

        // And an edit may take it back off the machine.
        var updated = await Update(db).Handle(created.Record.Id, Fields(null), null, CancellationToken.None);
        Assert.True(updated.IsValid);
        Assert.Null(updated.Record!.MachineId);
    }

    [Fact]
    public async Task Another_businesss_machine_is_rejected_on_create_and_on_update()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        var machineA = await MachineIdAsync(options, BusinessA);
        var machineB = await MachineIdAsync(options, BusinessB);
        Assert.NotEqual(machineA, machineB);
        await using var db = TestAppDbContext.For(options, BusinessA);

        var refusedCreate = await Create(db).Handle(Fields(machineB), null, CancellationToken.None);

        Assert.False(refusedCreate.IsValid);
        Assert.Equal(OperatingExpenseDetails.UnknownMachineMessage, refusedCreate.ValidationError);
        await using (var verify = TestAppDbContext.Unrestricted(options))
        {
            Assert.Empty(await verify.OperatingExpenses.ToListAsync());
        }

        var created = await Create(db).Handle(Fields(machineA), null, CancellationToken.None);
        var refusedUpdate = await Update(db)
            .Handle(created.Record!.Id, Fields(machineB), null, CancellationToken.None);

        Assert.False(refusedUpdate.IsValid);
        Assert.Equal(OperatingExpenseDetails.UnknownMachineMessage, refusedUpdate.ValidationError);
        await using (var verify = TestAppDbContext.Unrestricted(options))
        {
            Assert.Equal(machineA, await verify.OperatingExpenses.Select(x => x.MachineId).SingleAsync());
        }
    }

    /// <summary>
    /// The provider's own machine number is no longer a value this column accepts: machine 7 exists
    /// in both businesses, and <c>7</c> is not the local key of either. A stale client submitting
    /// the old value is refused rather than quietly attributing the cost to whichever machine
    /// happens to hold that local id.
    /// </summary>
    [Fact]
    public async Task A_provider_machine_number_is_not_accepted_as_a_machine_id()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await using var db = TestAppDbContext.For(options, BusinessA);

        var result = await Create(db).Handle(Fields(7), null, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal(OperatingExpenseDetails.UnknownMachineMessage, result.ValidationError);
    }

    /// <summary>
    /// The structural backstop under the use-case check: even written straight through the context,
    /// an expense cannot name another business's machine. The ownership enforcer stamps the
    /// business, and the composite foreign key is what makes the pair impossible.
    /// </summary>
    [Fact]
    public async Task The_database_refuses_an_expense_that_names_another_businesss_machine()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        var machineB = await MachineIdAsync(options, BusinessB);
        await using var db = TestAppDbContext.For(options, BusinessA);

        db.OperatingExpenses.Add(new OperatingExpense
        {
            ExpenseDate = new DateTime(2026, 3, 1),
            Category = OperatingExpenseCategory.RepairsAndMaintenance,
            Description = "Crossing businesses",
            AmountExGst = 10m,
            GstAmount = 1m,
            TotalAmount = 11m,
            MachineId = machineB,
        });

        var refused = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.IsType<SqliteException>(refused.InnerException);
    }

    /// <summary>
    /// The lookup the machine list and the reports read a provider machine number through: it is
    /// per business, so one number resolves to a different machine in each, and a number nothing
    /// recorded resolves to nothing rather than to the machine whose local id is that number.
    /// </summary>
    [Fact]
    public async Task The_machine_link_lookup_resolves_a_provider_number_within_the_callers_business_only()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        var machineA = await MachineIdAsync(options, BusinessA);
        var machineB = await MachineIdAsync(options, BusinessB);

        await using (var a = TestAppDbContext.For(options, BusinessA))
        {
            var resolved = await new EfNayaxMachineLinkStore(a)
                .GetLocalMachineIdsAsync([7, 8], CancellationToken.None);
            Assert.Equal(machineA, Assert.Single(resolved).Value);
            Assert.False(resolved.ContainsKey(8));
        }

        await using (var b = TestAppDbContext.For(options, BusinessB))
        {
            var resolved = await new EfNayaxMachineLinkStore(b)
                .GetLocalMachineIdsAsync([7], CancellationToken.None);
            Assert.Equal(machineB, resolved[7]);
        }
    }

    /// <summary>
    /// The column holds whatever provider the business sells through and does not say who issued
    /// the value, so a business that does not sell through Nayax resolves no Nayax machine number -
    /// the same rule the product lookup follows (issue #759).
    /// </summary>
    [Fact]
    public async Task The_machine_link_lookup_resolves_nothing_for_a_business_that_does_not_sell_through_Nayax()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await using var db = TestAppDbContext.For(options, BusinessWithoutProvider);

        // Its machine does carry "7"; the business issues no Nayax ids, so nothing resolves.
        Assert.NotEmpty(await db.Machines.Where(machine => machine.ExternalMachineId == "7").ToListAsync());
        Assert.Empty(await new EfNayaxMachineLinkStore(db)
            .GetLocalMachineIdsAsync([7], CancellationToken.None));
    }
}
