using Inventory.Application.Machines;
using Inventory.Domain.Machines;
using Inventory.Domain.Providers;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Deleting one of the business's own machines (issue #896), against the real relational schema.
///
/// <para>Relational rather than InMemory, because every claim here is the database's: the four
/// restricted <c>(BusinessId, MachineId)</c> foreign keys that make a referenced machine
/// undeletable, the tenant query filter that makes another business's machine invisible to the read
/// and so unreachable to the delete, and the fact that <strong>nothing is cascaded</strong> - the
/// referencing rows are still there afterwards, which is the whole point of refusing.</para>
/// </summary>
public class EfLocalMachineStoreDeletionTests
{
    private const int BusinessA = 1;
    private const int BusinessB = 2;
    private const long ProductId = 100;

    private static readonly DateTime At = new(2026, 10, 25, 2, 30, 0, DateTimeKind.Utc);

    private static async Task<DbContextOptions<AppDbContext>> CreateSqliteAsync(SqliteConnection connection)
    {
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var setup = TestAppDbContext.Unrestricted(options);
        await setup.Database.EnsureCreatedAsync();
        return options;
    }

    /// <summary>
    /// One machine of business A, with the product the stock tables need, and - when
    /// <paramref name="externalMachineId"/> is given - a Nayax business whose provider names it.
    /// </summary>
    private static async Task<long> SeedMachineAsync(
        DbContextOptions<AppDbContext> options,
        bool isActive = true,
        string? externalMachineId = null)
    {
        await using var seed = TestAppDbContext.Unrestricted(options);
        seed.Businesses.Add(new Business
        {
            Id = BusinessA,
            Name = "Vending A",
            Status = BusinessStatus.Active,
            SalesProvider = externalMachineId is null ? null : ExternalProvider.Nayax,
            CreatedAtUtc = At,
        });
        seed.Products.Add(new Product { Id = ProductId, BusinessId = BusinessA, Name = "Coke Zero" });

        var machine = new LocalMachine
        {
            BusinessId = BusinessA,
            Name = externalMachineId is null ? "Added by mistake" : "Machine 7",
            IsActive = isActive,
            ExternalMachineId = externalMachineId,
        };
        seed.Machines.Add(machine);
        await seed.SaveChangesAsync();
        return machine.Id;
    }

    /// <summary>The four kinds of row that hold a local machine id, each one on its own.</summary>
    public enum ReferenceKind
    {
        MachineStockCount,
        StockMovement,
        PickListRefill,
        OperatingExpense,
    }

    private static async Task ReferenceAsync(
        DbContextOptions<AppDbContext> options, long machineId, ReferenceKind kind)
    {
        await using var seed = TestAppDbContext.For(options, BusinessA);
        switch (kind)
        {
            case ReferenceKind.MachineStockCount:
                seed.MachineStockCounts.Add(new MachineStockCount
                {
                    MachineId = machineId,
                    ProductId = ProductId,
                    CountedQuantity = 4,
                    CountedAtUtc = At,
                });
                break;
            case ReferenceKind.StockMovement:
                seed.StockAdjustments.Add(new StockAdjustment
                {
                    ProductId = ProductId,
                    MachineId = machineId,
                    QuantityChange = -2,
                    QuantityAfter = 8,
                    Reason = StockAdjustmentReason.MachineRefill,
                    EffectiveAt = At,
                });
                break;
            case ReferenceKind.PickListRefill:
                seed.PickListRefills.Add(new PickListRefill
                {
                    IdempotencyKey = "one-pick",
                    RequestFingerprint = new string('a', 64),
                    MachineId = machineId,
                    RecordedAtUtc = At,
                });
                break;
            case ReferenceKind.OperatingExpense:
                seed.OperatingExpenses.Add(new OperatingExpense
                {
                    ExpenseDate = At,
                    Category = OperatingExpenseCategory.Other,
                    Description = "Site visit",
                    AmountExGst = 10m,
                    GstAmount = 1m,
                    TotalAmount = 11m,
                    MachineId = machineId,
                });
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }

        await seed.SaveChangesAsync();
    }

    private static string ExpectedRefusal(ReferenceKind kind) =>
        MachineDeletionPolicy.Refusal(
            isProviderNamed: false,
            provider: null,
            kind switch
            {
                ReferenceKind.MachineStockCount => new LocalMachineDeletionReferences(1, 0, 0, 0),
                ReferenceKind.StockMovement => new LocalMachineDeletionReferences(0, 1, 0, 0),
                ReferenceKind.PickListRefill => new LocalMachineDeletionReferences(0, 0, 1, 0),
                _ => new LocalMachineDeletionReferences(0, 0, 0, 1),
            })!;

    /// <summary>
    /// The machine added by mistake, which is the whole reason the delete exists: nothing refers to
    /// it, no provider names it, and the row is gone afterwards.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DeleteAsync_removes_an_unreferenced_machine_whether_it_is_active_or_not(bool isActive)
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        var id = await SeedMachineAsync(options, isActive);

        await using var db = TestAppDbContext.For(options, BusinessA);
        var store = new EfLocalMachineStore(db);

        var attempt = await store.DeleteAsync(id, CancellationToken.None);

        Assert.Equal(LocalMachineWriteOutcome.Succeeded, attempt.Outcome);

        await using var verify = TestAppDbContext.Unrestricted(options);
        Assert.Empty(await verify.Machines.ToListAsync());
    }

    /// <summary>
    /// Each of the four tables that name a local machine refuses the delete on its own, with the
    /// sentence that says what is in the way - and the referencing row is still there, because
    /// nothing here cascades.
    /// </summary>
    [Theory]
    [InlineData(ReferenceKind.MachineStockCount)]
    [InlineData(ReferenceKind.StockMovement)]
    [InlineData(ReferenceKind.PickListRefill)]
    [InlineData(ReferenceKind.OperatingExpense)]
    public async Task DeleteAsync_refuses_a_machine_each_kind_of_history_refers_to_and_preserves_everything(
        ReferenceKind kind)
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        var id = await SeedMachineAsync(options);
        await ReferenceAsync(options, id, kind);

        await using var db = TestAppDbContext.For(options, BusinessA);
        var store = new EfLocalMachineStore(db);

        var attempt = await store.DeleteAsync(id, CancellationToken.None);

        Assert.Equal(LocalMachineWriteOutcome.Refused, attempt.Outcome);
        Assert.Equal(ExpectedRefusal(kind), attempt.Refusal);

        await using var verify = TestAppDbContext.Unrestricted(options);
        Assert.Single(await verify.Machines.ToListAsync());
        Assert.Equal(
            1,
            await verify.MachineStockCounts.CountAsync()
                + await verify.StockAdjustments.CountAsync()
                + await verify.PickListRefills.CountAsync()
                + await verify.OperatingExpenses.CountAsync());
    }

    /// <summary>
    /// A machine the business's sales provider names is refused even with no local history at all:
    /// it is managed at the provider, and its stored provider id is what every later lookup resolves
    /// that machine's sales through.
    /// </summary>
    [Fact]
    public async Task DeleteAsync_refuses_a_machine_the_provider_names_and_writes_nothing()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        var id = await SeedMachineAsync(options, externalMachineId: "7");

        await using var db = TestAppDbContext.For(options, BusinessA);
        var store = new EfLocalMachineStore(db);

        var attempt = await store.DeleteAsync(id, CancellationToken.None);

        Assert.Equal(LocalMachineWriteOutcome.Refused, attempt.Outcome);
        Assert.Equal(
            "This machine comes from Nayax and cannot be deleted here. Nothing was deleted.",
            attempt.Refusal);

        await using var verify = TestAppDbContext.Unrestricted(options);
        var stored = await verify.Machines.SingleAsync();
        Assert.Equal(("Machine 7", "7"), (stored.Name, stored.ExternalMachineId));
    }

    /// <summary>
    /// Only the machine named is deleted. Another machine of the same business, and the history that
    /// refers to <em>it</em>, are untouched - the delete is one row, not a clean-up.
    /// </summary>
    [Fact]
    public async Task DeleteAsync_leaves_every_other_machine_and_its_history_exactly_as_it_was()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        var mistake = await SeedMachineAsync(options);

        long busy;
        await using (var seed = TestAppDbContext.For(options, BusinessA))
        {
            var machine = new LocalMachine { Name = "Lobby level 1" };
            seed.Machines.Add(machine);
            await seed.SaveChangesAsync();
            busy = machine.Id;
        }

        await ReferenceAsync(options, busy, ReferenceKind.OperatingExpense);

        await using var db = TestAppDbContext.For(options, BusinessA);
        var store = new EfLocalMachineStore(db);

        Assert.Equal(
            LocalMachineWriteOutcome.Succeeded,
            (await store.DeleteAsync(mistake, CancellationToken.None)).Outcome);

        await using var verify = TestAppDbContext.Unrestricted(options);
        Assert.Equal(busy, (await verify.Machines.SingleAsync()).Id);
        Assert.Equal(busy, (await verify.OperatingExpenses.SingleAsync()).MachineId);
    }

    /// <summary>
    /// Another business's machine is invisible to the read, so the delete reports the same
    /// <c>NotFound</c> an unknown id gets - through the central query filters alone, with no
    /// <c>BusinessId</c> predicate in the adapter - and that machine is untouched.
    /// </summary>
    [Fact]
    public async Task DeleteAsync_reports_a_machine_of_another_business_as_missing_and_leaves_it_alone()
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

        Assert.Equal(
            LocalMachineWriteOutcome.NotFound,
            (await store.DeleteAsync(otherId, CancellationToken.None)).Outcome);
        Assert.Equal(
            LocalMachineWriteOutcome.NotFound,
            (await store.DeleteAsync(999, CancellationToken.None)).Outcome);

        await using var verify = TestAppDbContext.Unrestricted(options);
        Assert.Equal(BusinessB, (await verify.Machines.SingleAsync()).BusinessId);
    }

    /// <summary>
    /// A caller whose membership did not resolve deletes nothing: the fail-closed rule, which for a
    /// delete is the difference between reading no machine and destroying somebody else's.
    /// </summary>
    [Fact]
    public async Task An_unresolved_business_deletes_no_machine()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        var id = await SeedMachineAsync(options);

        await using var denied = TestAppDbContext.Denied(options);
        var store = new EfLocalMachineStore(denied);

        Assert.Equal(
            LocalMachineWriteOutcome.NotFound,
            (await store.DeleteAsync(id, CancellationToken.None)).Outcome);

        await using var verify = TestAppDbContext.Unrestricted(options);
        Assert.Single(await verify.Machines.ToListAsync());
    }

    /// <summary>
    /// The read publishes the delete's own answer for every machine in one statement (issue #896):
    /// deletable where nothing refers to it, refused with the server's sentence where something does,
    /// and refused as a provider machine where the provider names it. The list and the
    /// single-machine read agree, because they project the same rows.
    /// </summary>
    [Fact]
    public async Task The_reads_publish_the_deletion_eligibility_of_every_machine()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        var provider = await SeedMachineAsync(options, externalMachineId: "7");

        long mistake;
        long busy;
        await using (var seed = TestAppDbContext.For(options, BusinessA))
        {
            var added = new LocalMachine { Name = "Added by mistake" };
            var used = new LocalMachine { Name = "Busy machine" };
            seed.Machines.AddRange(added, used);
            await seed.SaveChangesAsync();
            mistake = added.Id;
            busy = used.Id;
        }

        await ReferenceAsync(options, busy, ReferenceKind.MachineStockCount);
        await ReferenceAsync(options, busy, ReferenceKind.OperatingExpense);

        await using var db = TestAppDbContext.For(options, BusinessA);
        var store = new EfLocalMachineStore(db);

        var machines = (await store.ListOrderedByNameAsync(CancellationToken.None))
            .ToDictionary(machine => machine.Id);

        Assert.True(machines[mistake].DeletionEligibility!.Allowed);
        Assert.Null(machines[mistake].DeletionEligibility!.Refusal);

        // Both kinds are counted and both are named, from one read.
        Assert.False(machines[busy].DeletionEligibility!.Allowed);
        Assert.Equal(
            MachineDeletionPolicy.Refusal(
                isProviderNamed: false, provider: null, new LocalMachineDeletionReferences(1, 0, 0, 1)),
            machines[busy].DeletionEligibility!.Refusal);

        // The provider refusal names the provider and never the provider's id for the machine.
        Assert.False(machines[provider].DeletionEligibility!.Allowed);
        Assert.Contains("comes from Nayax", machines[provider].DeletionEligibility!.Refusal!, StringComparison.Ordinal);
        Assert.DoesNotContain("7", machines[provider].DeletionEligibility!.Refusal!, StringComparison.Ordinal);

        var one = await store.FindByIdAsync(busy, CancellationToken.None);
        Assert.Equal(machines[busy].DeletionEligibility, one!.DeletionEligibility);
    }

    /// <summary>
    /// The eligibility a read publishes and the answer the delete gives are the same rule over the
    /// same rows, which is what stops a screen from offering an action the server must refuse.
    /// </summary>
    [Fact]
    public async Task Every_machine_the_read_calls_deletable_is_actually_deleted()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        var id = await SeedMachineAsync(options);

        await using var db = TestAppDbContext.For(options, BusinessA);
        var store = new EfLocalMachineStore(db);

        Assert.True((await store.FindByIdAsync(id, CancellationToken.None))!.DeletionEligibility!.Allowed);
        Assert.Equal(
            LocalMachineWriteOutcome.Succeeded,
            (await store.DeleteAsync(id, CancellationToken.None)).Outcome);
    }
}
