using Inventory.Application.Machines;
using Inventory.Domain.Exceptions;
using Inventory.Domain.Machines;
using Inventory.Domain.Providers;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Deleting one of the business's own machines under real concurrency (issue #896).
///
/// <para>This is the test the design exists for. "Does anything refer to this machine, and does the
/// provider name it?" is a check-then-write rule, and a check-then-write rule that is not serialised
/// is not a rule: the provider machine review's apply can record this machine's provider id in the
/// window between the check and the delete, and the restricted foreign keys cannot stop that one -
/// the link is a write to the machine row itself, not a reference to it. Losing it would delete the
/// row that a provider id had just been recorded on, which is the silent identity error the
/// provider-id work exists to end.</para>
///
/// <para>It therefore runs against a real SQLite <strong>file</strong> with two independent
/// connections, because that is the only way to have two genuine writers: a shared in-memory
/// database is one connection, and InMemory has no locking at all. The two requests are released
/// together at a barrier, so the window the rule has to close is actually opened. It is the same
/// shape as <see cref="PickListRefillConcurrencyTests"/> (issue #743), for the same reason.</para>
///
/// <para>The link side drives <see cref="EfProviderMachineLinkStore"/> directly rather than
/// <c>ApplyProviderMachineLinks</c>: the use case reads the live Nayax fleet <em>before</em> it opens
/// the transaction, and what has to be serialised against the delete is the transaction and the write
/// - which are the store's. Nothing here makes a provider request, which is also what the delete
/// itself never does.</para>
/// </summary>
public class LocalMachineDeletionConcurrencyTests : IDisposable
{
    private const int BusinessA = 1;
    private const long ProductId = 100;
    private const string ProviderMachineId = "7";

    private static readonly DateTime At = new(2026, 10, 25, 2, 30, 0, DateTimeKind.Utc);

    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"inventory-machine-delete-{Guid.NewGuid():N}.db");

    private readonly string _connectionString;

    public LocalMachineDeletionConcurrencyTests()
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = _databasePath }.ToString();

        using var db = TestAppDbContext.Unrestricted(Options());
        db.Database.Migrate();
        db.Businesses.Add(new Business
        {
            Id = BusinessA,
            Name = "Vending Co",
            Status = BusinessStatus.Active,
            // Nayax, because the link side refuses to run at all for a business whose provider is
            // not its own (issue #759). The machine itself still carries no provider id, which is
            // what makes it deletable to begin with.
            SalesProvider = ExternalProvider.Nayax,
            CreatedAtUtc = At,
        });
        db.Products.Add(new Product { Id = ProductId, BusinessId = BusinessA, Name = "Coke Zero" });
        db.SaveChanges();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }

        GC.SuppressFinalize(this);
    }

    private DbContextOptions<AppDbContext> Options() =>
        new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connectionString).Options;

    private long SeedMachine()
    {
        using var db = TestAppDbContext.For(Options(), BusinessA);
        var machine = new LocalMachine { Name = "Added by mistake" };
        db.Machines.Add(machine);
        db.SaveChanges();
        return machine.Id;
    }

    /// <summary>Runs the given operations released at the same instant and returns what each answered.</summary>
    private static async Task<(T? Answer, Exception? Failure)[]> RaceAsync<T>(
        params Func<Task<T>>[] operations)
    {
        using var barrier = new Barrier(operations.Length);

        var answers = operations
            .Select(operation => Task.Run(async () =>
            {
                barrier.SignalAndWait();
                try
                {
                    return (await operation(), (Exception?)null);
                }
                catch (Exception exception)
                {
                    return ((T?)default, exception);
                }
            }))
            .ToArray();

        return await Task.WhenAll(answers);
    }

    private async Task<LocalMachineWriteAttempt> DeleteAsync(long machineId)
    {
        // Its own context on its own connection, the way two HTTP requests arrive.
        await using var db = TestAppDbContext.For(Options(), BusinessA);
        return await new EfLocalMachineStore(db).DeleteAsync(machineId, CancellationToken.None);
    }

    /// <summary>The provider machine review's apply, as its store carries it out.</summary>
    private async Task<IReadOnlyList<ProviderMachineLinkWritten>> LinkAsync(long machineId)
    {
        await using var db = TestAppDbContext.For(Options(), BusinessA);
        var store = new EfProviderMachineLinkStore(db);

        await using var transaction = await store.BeginTransactionAsync(CancellationToken.None);
        var written = await store.LinkAsync(
            [
                new ProviderMachineLinkCommand(
                    ProviderMachineId,
                    "Machine 7",
                    ProviderMachineLinkAction.LinkExisting,
                    machineId),
            ],
            CancellationToken.None);
        await transaction.CommitAsync(CancellationToken.None);
        return written;
    }

    private (int Machines, string? ExternalMachineId) ReadMachines()
    {
        using var db = TestAppDbContext.Unrestricted(Options());
        var machines = db.Machines.AsNoTracking().ToList();
        return (machines.Count, machines.SingleOrDefault()?.ExternalMachineId);
    }

    /// <summary>
    /// The race the database cannot stop: the provider machine review linking the very machine a
    /// delete is removing. Exactly one of them happens, and the two outcomes are the two coherent
    /// ones - the machine is gone and nothing was linked, or the machine carries the provider id and
    /// the delete was refused as a provider machine. A linked machine is never deleted, and a deleted
    /// machine never comes back carrying a provider id.
    /// </summary>
    [Fact]
    public async Task A_delete_and_a_provider_link_of_one_machine_leave_exactly_one_outcome()
    {
        var machineId = SeedMachine();

        var answers = await RaceAsync<object>(
            async () => await DeleteAsync(machineId),
            async () => await LinkAsync(machineId));

        var (deleted, deleteFailure) = answers[0];
        var (linked, linkFailure) = answers[1];

        // Neither side may fail with a lock error or any other unhandled exception: the delete
        // answers an attempt, and the link either writes or refuses with its own domain refusal.
        Assert.Null(deleteFailure);
        Assert.True(
            linkFailure is null or DomainValidationException,
            $"The link failed with {linkFailure?.GetType().Name}: {linkFailure?.Message}");

        var attempt = Assert.IsType<LocalMachineWriteAttempt>(deleted);
        var (machines, externalMachineId) = ReadMachines();

        if (attempt.Outcome == LocalMachineWriteOutcome.Succeeded)
        {
            // The delete won: the machine is gone, and the link was refused rather than recording a
            // provider id on a row that no longer exists.
            Assert.Equal(0, machines);
            Assert.NotNull(linkFailure);
        }
        else
        {
            // The link won: the delete is refused as a provider machine, and the link is intact.
            Assert.Equal(LocalMachineWriteOutcome.Refused, attempt.Outcome);
            Assert.Equal(
                MachineCataloguePolicy.ProviderMachineDeletionRefusal(ExternalProvider.Nayax),
                attempt.Refusal);
            Assert.Equal(1, machines);
            Assert.Equal(ProviderMachineId, externalMachineId);
            Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<ProviderMachineLinkWritten>>(linked));
        }
    }

    /// <summary>
    /// A reference recorded against the machine at the same moment as the delete. Both orders are
    /// safe: the expense lands and the delete is refused with the counted refusal, or the delete
    /// lands and the expense insert is refused by the restricted foreign key - never an expense
    /// pointing at a machine that is not there.
    /// </summary>
    [Fact]
    public async Task A_delete_and_a_new_expense_naming_the_machine_never_leave_an_orphan()
    {
        var machineId = SeedMachine();

        var answers = await RaceAsync<object>(
            async () => await DeleteAsync(machineId),
            async () =>
            {
                await using var db = TestAppDbContext.For(Options(), BusinessA);
                db.OperatingExpenses.Add(new OperatingExpense
                {
                    ExpenseDate = At,
                    Category = OperatingExpenseCategory.Other,
                    Description = "Site visit",
                    AmountExGst = 10m,
                    GstAmount = 1m,
                    TotalAmount = 11m,
                    MachineId = machineId,
                });
                return await db.SaveChangesAsync();
            });

        var (deleted, deleteFailure) = answers[0];
        var (_, expenseFailure) = answers[1];

        Assert.Null(deleteFailure);
        var attempt = Assert.IsType<LocalMachineWriteAttempt>(deleted);

        await using var verify = TestAppDbContext.Unrestricted(Options());
        var expenses = await verify.OperatingExpenses.AsNoTracking().ToListAsync();
        var machines = await verify.Machines.AsNoTracking().ToListAsync();

        if (attempt.Outcome == LocalMachineWriteOutcome.Succeeded)
        {
            // The delete won, so the expense was refused by the database rather than left pointing
            // at a machine that no longer exists.
            Assert.Empty(machines);
            Assert.Empty(expenses);
            Assert.IsType<DbUpdateException>(expenseFailure);
        }
        else
        {
            // The expense won, so the delete counted it and refused, and both rows are intact.
            Assert.Equal(LocalMachineWriteOutcome.Refused, attempt.Outcome);
            Assert.Equal(
                MachineDeletionPolicy.Refusal(
                    isProviderNamed: false, provider: null, new LocalMachineDeletionReferences(0, 0, 0, 1)),
                attempt.Refusal);
            Assert.Null(expenseFailure);
            Assert.Single(machines);
            Assert.Equal(machineId, Assert.Single(expenses).MachineId);
        }
    }

    /// <summary>
    /// The database's own backstop, under the count. A reference that lands inside the delete's own
    /// transaction - after it counted, before the row is removed - makes SQLite refuse the statement,
    /// and that refusal has to reach the operator as a refusal rather than as a server error. The
    /// interceptor is what opens that exact window deterministically; the window itself is real,
    /// which is why the restricted foreign keys are there at all.
    /// </summary>
    [Fact]
    public async Task A_reference_the_count_could_not_see_is_refused_by_the_database_as_a_refusal()
    {
        var machineId = SeedMachine();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connectionString)
            .AddInterceptors(new CountsMachineStockCountIn(machineId))
            .Options;

        await using var db = TestAppDbContext.For(options, BusinessA);
        var attempt = await new EfLocalMachineStore(db).DeleteAsync(machineId, CancellationToken.None);

        Assert.Equal(LocalMachineWriteOutcome.Refused, attempt.Outcome);
        Assert.Equal(MachineDeletionPolicy.ReferencedWhileDeletingRefusal, attempt.Refusal);

        // Nothing was deleted, and the whole transaction rolled back - the count the interceptor
        // inserted is not there either.
        await using var verify = TestAppDbContext.Unrestricted(Options());
        Assert.Single(await verify.Machines.AsNoTracking().ToListAsync());
        Assert.Empty(await verify.MachineStockCounts.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// Records a machine stock count for the machine inside the delete's own transaction, just before
    /// its <c>DELETE</c> statement runs: the one interleaving no lock can exclude, because it happens
    /// after the reference count and inside the same transaction. Raw SQL, so EF's change tracker has
    /// nothing to say about it.
    /// </summary>
    private sealed class CountsMachineStockCountIn(long machineId) : SaveChangesInterceptor
    {
        private bool _inserted;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(eventData);

            if (!_inserted && eventData.Context is AppDbContext context)
            {
                _inserted = true;
                context.Database.ExecuteSqlRaw(
                    """
                    INSERT INTO "MachineStockCounts"
                        ("BusinessId", "MachineId", "ProductId", "CountedQuantity", "CountedAtUtc")
                    VALUES ({0}, {1}, {2}, 4, {3})
                    """,
                    BusinessA,
                    machineId,
                    ProductId,
                    At);
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
