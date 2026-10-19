using Inventory.Application.Machines;
using Inventory.Domain.Machines;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// <see cref="EfLocalMachineStore"/> against the real relational schema (issue #740, parent #329
/// child G1): the machines a business maintains itself, created, renamed, deactivated and
/// reactivated.
///
/// <para>Relational rather than InMemory, because three of the claims here are the database's: the
/// per-business filtered unique index on the provider machine id, the database-assigned key, and the
/// tenant query filter that makes another business's machine invisible to every read and unreachable
/// to every write.</para>
/// </summary>
public class EfLocalMachineStoreTests
{
    private const int BusinessA = 1;
    private const int BusinessB = 2;

    private static async Task<DbContextOptions<AppDbContext>> CreateSqliteAsync(SqliteConnection connection)
    {
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var setup = TestAppDbContext.Unrestricted(options);
        await setup.Database.EnsureCreatedAsync();
        return options;
    }

    private static LocalMachineWriteFields Named(string? name) => new(name);

    [Fact]
    public async Task CreateAsync_records_an_active_machine_with_a_database_assigned_key_and_no_provider_id()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await using var db = TestAppDbContext.For(options, BusinessA);
        var store = new EfLocalMachineStore(db);

        var attempt = await store.CreateAsync(Named("  Lobby level 1  "), CancellationToken.None);

        Assert.Equal(LocalMachineWriteOutcome.Succeeded, attempt.Outcome);
        Assert.NotNull(attempt.MachineId);
        Assert.True(attempt.MachineId > 0);

        await using var verify = TestAppDbContext.Unrestricted(options);
        var stored = await verify.Machines.SingleAsync();
        Assert.Equal(BusinessA, stored.BusinessId);
        // Trimmed by the Domain's normalisation, active, and with no provider id at all: a machine a
        // person created is this business's own record.
        Assert.Equal("Lobby level 1", stored.Name);
        Assert.True(stored.IsActive);
        Assert.Null(stored.ExternalMachineId);
    }

    [Fact]
    public async Task CreateAsync_refuses_a_machine_with_no_name_and_writes_nothing()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await using var db = TestAppDbContext.For(options, BusinessA);
        var store = new EfLocalMachineStore(db);

        var attempt = await store.CreateAsync(Named("   "), CancellationToken.None);

        Assert.Equal(LocalMachineWriteOutcome.Refused, attempt.Outcome);
        Assert.Equal(MachineCataloguePolicy.NameRequiredMessage, attempt.Refusal);

        await using var verify = TestAppDbContext.Unrestricted(options);
        Assert.Empty(await verify.Machines.ToListAsync());
    }

    /// <summary>
    /// A machine's name is the operator's own label and nothing synchronises it, so two machines may
    /// legitimately be called the same thing - unlike a category, whose name the catalogue resolves
    /// by. The local id is what identifies a machine.
    /// </summary>
    [Fact]
    public async Task Two_machines_of_one_business_may_share_a_name()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await using var db = TestAppDbContext.For(options, BusinessA);
        var store = new EfLocalMachineStore(db);

        Assert.Equal(
            LocalMachineWriteOutcome.Succeeded,
            (await store.CreateAsync(Named("Level 3"), CancellationToken.None)).Outcome);
        Assert.Equal(
            LocalMachineWriteOutcome.Succeeded,
            (await store.CreateAsync(Named("Level 3"), CancellationToken.None)).Outcome);

        var machines = await store.ListOrderedByNameAsync(CancellationToken.None);
        Assert.Equal(2, machines.Count);
        Assert.Distinct(machines.Select(machine => machine.Id));
    }

    [Fact]
    public async Task ListOrderedByNameAsync_returns_active_and_inactive_machines_ordered_by_name()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await using var db = TestAppDbContext.For(options, BusinessA);
        db.Machines.AddRange(
            new LocalMachine { Name = "Zebra bay", IsActive = false },
            new LocalMachine { Name = "Atrium", ExternalMachineId = "7" });
        await db.SaveChangesAsync();
        var store = new EfLocalMachineStore(db);

        var machines = await store.ListOrderedByNameAsync(CancellationToken.None);

        Assert.Equal(["Atrium", "Zebra bay"], machines.Select(machine => machine.Name));
        // A deactivated machine stays listed: it is history an operator may reactivate.
        Assert.Equal([true, false], machines.Select(machine => machine.IsActive));
        // Whether the provider names the machine is reported; its identifier never is.
        Assert.Equal([true, false], machines.Select(machine => machine.IsProviderNamed));
    }

    [Fact]
    public async Task RenameAsync_writes_the_new_name_and_leaves_the_provider_id_alone()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        long id;
        await using (var seed = TestAppDbContext.For(options, BusinessA))
        {
            var machine = new LocalMachine { Name = "Machine 7", ExternalMachineId = "7" };
            seed.Machines.Add(machine);
            await seed.SaveChangesAsync();
            id = machine.Id;
        }

        await using var db = TestAppDbContext.For(options, BusinessA);
        var store = new EfLocalMachineStore(db);

        var attempt = await store.RenameAsync(id, Named("Lobby level 1"), CancellationToken.None);

        Assert.Equal(LocalMachineWriteOutcome.Succeeded, attempt.Outcome);

        await using var verify = TestAppDbContext.Unrestricted(options);
        var stored = await verify.Machines.SingleAsync();
        // A provider-named machine is renamed like any other: nothing syncs a machine's name, so no
        // import can overwrite the operator's own label.
        Assert.Equal("Lobby level 1", stored.Name);
        Assert.Equal("7", stored.ExternalMachineId);
    }

    [Fact]
    public async Task RenameAsync_refuses_a_blank_name_and_reports_a_missing_machine()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await using var db = TestAppDbContext.For(options, BusinessA);
        var machine = new LocalMachine { Name = "Atrium" };
        db.Machines.Add(machine);
        await db.SaveChangesAsync();
        var store = new EfLocalMachineStore(db);

        Assert.Equal(
            LocalMachineWriteOutcome.Refused,
            (await store.RenameAsync(machine.Id, Named(""), CancellationToken.None)).Outcome);
        Assert.Equal(
            LocalMachineWriteOutcome.NotFound,
            (await store.RenameAsync(999, Named("Anything"), CancellationToken.None)).Outcome);

        await using var verify = TestAppDbContext.Unrestricted(options);
        Assert.Equal("Atrium", await verify.Machines.Select(stored => stored.Name).SingleAsync());
    }

    [Fact]
    public async Task SetActiveAsync_deactivates_reactivates_and_is_idempotent()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await using var db = TestAppDbContext.For(options, BusinessA);
        var machine = new LocalMachine { Name = "Atrium" };
        db.Machines.Add(machine);
        await db.SaveChangesAsync();
        var store = new EfLocalMachineStore(db);

        Assert.Equal(
            LocalMachineWriteOutcome.Succeeded,
            (await store.SetActiveAsync(machine.Id, false, CancellationToken.None)).Outcome);
        Assert.False((await store.FindByIdAsync(machine.Id, CancellationToken.None))!.IsActive);

        // Deactivating an already inactive machine succeeds and changes nothing.
        Assert.Equal(
            LocalMachineWriteOutcome.Succeeded,
            (await store.SetActiveAsync(machine.Id, false, CancellationToken.None)).Outcome);
        Assert.False((await store.FindByIdAsync(machine.Id, CancellationToken.None))!.IsActive);

        Assert.Equal(
            LocalMachineWriteOutcome.Succeeded,
            (await store.SetActiveAsync(machine.Id, true, CancellationToken.None)).Outcome);
        Assert.True((await store.FindByIdAsync(machine.Id, CancellationToken.None))!.IsActive);

        Assert.Equal(
            LocalMachineWriteOutcome.NotFound,
            (await store.SetActiveAsync(999, false, CancellationToken.None)).Outcome);

        // Nothing was deleted: a machine that stops trading is history, not a mistake.
        await using var verify = TestAppDbContext.Unrestricted(options);
        Assert.Single(await verify.Machines.ToListAsync());
    }

    /// <summary>
    /// The per-business filtered unique index of issue #759's column model, applied to machines: one
    /// business cannot hold the same provider machine id twice, two businesses legitimately can, and
    /// a business may hold any number of machines with no provider id at all.
    /// </summary>
    [Fact]
    public async Task One_business_cannot_hold_the_same_external_machine_id_twice()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);

        await using (var a = TestAppDbContext.For(options, BusinessA))
        {
            a.Machines.AddRange(
                new LocalMachine { Name = "Atrium", ExternalMachineId = "7" },
                new LocalMachine { Name = "Typed in", ExternalMachineId = null },
                new LocalMachine { Name = "Also typed in", ExternalMachineId = null });
            await a.SaveChangesAsync();
        }

        // Another operator's fleet may legitimately contain machine 7 too.
        await using (var b = TestAppDbContext.For(options, BusinessB))
        {
            b.Machines.Add(new LocalMachine { Name = "Gym foyer", ExternalMachineId = "7" });
            await b.SaveChangesAsync();
        }

        await using var clash = TestAppDbContext.For(options, BusinessA);
        clash.Machines.Add(new LocalMachine { Name = "Second claim on 7", ExternalMachineId = "7" });
        var refused = await Assert.ThrowsAsync<DbUpdateException>(() => clash.SaveChangesAsync());
        Assert.IsType<SqliteException>(refused.InnerException);

        await using var verify = TestAppDbContext.Unrestricted(options);
        Assert.Equal(2, await verify.Machines.CountAsync(machine => machine.ExternalMachineId == "7"));
    }

    /// <summary>
    /// Another business's machine is invisible to every read here and unreachable to every write,
    /// through the central <see cref="AppDbContext"/> query filters alone - this adapter writes no
    /// <c>BusinessId</c> predicate of its own.
    /// </summary>
    [Fact]
    public async Task A_machine_of_another_business_is_neither_readable_nor_writable()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        long otherId;
        await using (var b = TestAppDbContext.For(options, BusinessB))
        {
            var machine = new LocalMachine { Name = "Gym foyer" };
            b.Machines.Add(machine);
            await b.SaveChangesAsync();
            otherId = machine.Id;
        }

        await using var db = TestAppDbContext.For(options, BusinessA);
        var store = new EfLocalMachineStore(db);

        Assert.Empty(await store.ListOrderedByNameAsync(CancellationToken.None));
        Assert.Null(await store.FindByIdAsync(otherId, CancellationToken.None));
        Assert.Equal(
            LocalMachineWriteOutcome.NotFound,
            (await store.RenameAsync(otherId, Named("Mine now"), CancellationToken.None)).Outcome);
        Assert.Equal(
            LocalMachineWriteOutcome.NotFound,
            (await store.SetActiveAsync(otherId, false, CancellationToken.None)).Outcome);

        await using var verify = TestAppDbContext.Unrestricted(options);
        var untouched = await verify.Machines.SingleAsync();
        Assert.Equal((BusinessB, "Gym foyer", true), (untouched.BusinessId, untouched.Name, untouched.IsActive));
    }

    /// <summary>
    /// A caller whose membership did not resolve reads nothing and writes nothing - the fail-closed
    /// rule, asserted here because a machine list is the first thing a page asks for.
    /// </summary>
    [Fact]
    public async Task An_unresolved_business_reads_no_machine_and_creates_none()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await using (var a = TestAppDbContext.For(options, BusinessA))
        {
            a.Machines.Add(new LocalMachine { Name = "Atrium" });
            await a.SaveChangesAsync();
        }

        await using var denied = TestAppDbContext.Denied(options);
        var store = new EfLocalMachineStore(denied);

        Assert.Empty(await store.ListOrderedByNameAsync(CancellationToken.None));
        await Assert.ThrowsAnyAsync<Exception>(
            () => store.CreateAsync(Named("Smuggled in"), CancellationToken.None));

        await using var verify = TestAppDbContext.Unrestricted(options);
        Assert.Single(await verify.Machines.ToListAsync());
    }
}
