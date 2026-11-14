using System.Data.Common;
using Inventory.Application.InventoryCounting;
using Inventory.Application.Stock;
using Inventory.Domain.Exceptions;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;
using DomainStock = Inventory.Domain.Stock;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// The Take Inventory apply under real concurrency (issue #1241).
///
/// <para>A count is a check-then-write rule - "is storage still the quantity the operator counted
/// against?" - and a check-then-write rule that is not serialised is not a rule. Before this issue
/// the use case read the quantity, compared it with <c>ExpectedCurrentStock</c> and only then opened
/// a transaction to record the difference, so a change that committed in that gap was silently
/// overwritten: stock 10, the operator counts 8, a refill of 4 commits, and the stale -2 is applied
/// to 14 leaving 12 - neither what the operator counted nor what the shelf holds. So the apply now
/// takes <c>SqliteWriteLockTransaction</c> (<c>BEGIN IMMEDIATE</c>) <em>before</em> it reads the
/// quantity and holds it through its commit, exactly as issue #743's pick-list refill and issue
/// #1127's Sync Restock do.
/// </para>
///
/// <para><strong>The window is opened deliberately rather than hoped for.</strong> The first test
/// drives a competing restock from a <see cref="DbCommandInterceptor"/> at the instant the count's
/// authoritative read has completed - the gap the stale difference used to be decided in - on its
/// own, deliberately impatient connection, so "it cannot even begin" is answered in a second rather
/// than after SQLite's default thirty. Because that read now happens inside the lock, a competing
/// change can only land <em>before</em> it, and the remaining tests are what that leaves: the count
/// waits, re-reads the committed quantity and answers <c>409</c> having recorded nothing, rather
/// than applying its difference to a quantity that no longer exists or failing with a provider lock
/// error.</para>
///
/// <para>Everything runs against a real SQLite <strong>file</strong> with one connection per writer,
/// because that is the only way to have two genuine writers: a shared in-memory database is one
/// connection, and the EF InMemory provider has no locking at all.</para>
/// </summary>
public sealed class InventoryCountApplyConcurrencyTests : IDisposable
{
    private const int BusinessA = 1;
    private const long ProductId = 100;
    private const int OpeningStock = 10;
    private const decimal OpeningUnitCost = 2m;

    private static readonly DateTime RecordedAt = new(2026, 11, 13, 2, 30, 0, DateTimeKind.Utc);

    /// <summary>
    /// How long a competing change that already holds the write lock stays open before committing.
    /// It is a window for the count to arrive and start waiting for that lock, not a synchronisation
    /// point: whenever the count arrives, the lock is what orders the two, and the count reads the
    /// committed quantity rather than one that is about to change.
    /// </summary>
    private static readonly TimeSpan LockWaitWindow = TimeSpan.FromSeconds(1);

    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"inventory-count-apply-{Guid.NewGuid():N}.db");

    private readonly string _connectionString;
    private readonly string _impatientConnectionString;

    public InventoryCountApplyConcurrencyTests()
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = _databasePath }.ToString();
        _impatientConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            // One second, not the ADO.NET "0 means wait forever" and not SQLite's default thirty.
            DefaultTimeout = 1,
        }.ToString();

        using var db = TestAppDbContext.Unrestricted(Options());
        db.Database.Migrate();
        db.Businesses.Add(new Business
        {
            Id = BusinessA,
            Name = "Vending Co",
            Status = BusinessStatus.Active,
            CreatedAtUtc = RecordedAt,
        });
        db.SaveChanges();

        // The opening costed Restock gives the cost rebuild each count triggers a real history to
        // replay and a known average cost to write a shortage off at.
        db.Products.Add(new Product
        {
            Id = ProductId,
            BusinessId = BusinessA,
            Name = "Coke Zero",
            QuantityInStock = OpeningStock,
        });
        db.StockAdjustments.Add(new StockAdjustment
        {
            BusinessId = BusinessA,
            ProductId = ProductId,
            QuantityChange = OpeningStock,
            QuantityAfter = OpeningStock,
            Reason = StockAdjustmentReason.Restock,
            UnitCost = OpeningUnitCost,
            EffectiveAt = RecordedAt.AddDays(-1),
        });
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

    /// <summary>
    /// The regression test AGENTS.md § Automated task contract asks for: the authoritative read, the
    /// comparison and the mutation are one operation, so nothing can change the quantity between
    /// them. A competing restock of 4 is driven at the instant the count has read its quantity and
    /// cannot even begin, so the count applies exactly what the operator counted. Without the lock
    /// that restock committed inside the window and the stale difference was applied on top of it,
    /// leaving storage reporting 12 for a shelf the operator had just counted as 8.
    /// </summary>
    [Fact]
    public async Task A_change_cannot_commit_between_a_counts_read_and_its_mutation()
    {
        var competing = new CompetesWhenTheCountHasReadTheProduct(() => RestockAsync(ImpatientOptions(), 4));

        var answer = await ApplyAsync(OptionsWith(competing), countedStock: 8, expectedCurrentStock: OpeningStock);

        Assert.True(competing.Ran, "The competing restock was never attempted, so nothing was proved.");
        Assert.Null(answer.Failure);
        Assert.Equal(InventoryCountApplyOutcome.Applied, answer.Result!.Outcome);
        Assert.Equal(-2, answer.Result.QuantityChange);
        Assert.Equal(8, answer.Result.CurrentStock);

        // The competing restock could not commit inside the count's transaction, so what storage
        // holds is what was counted and the count's own movement is the only one added.
        Assert.IsType<SqliteException>(competing.Failure);
        var (quantityInStock, countMovements) = Read();
        Assert.Equal(8, quantityInStock);
        Assert.Equal(1, countMovements);
    }

    /// <summary>
    /// The other side of the same guarantee, and the answer the first acceptance criterion asks for:
    /// a change that commits while the count is waiting for the write lock is read by the count,
    /// which refuses it with <c>409</c> and records no movement. The loser of a write lock must be
    /// answered with a decision, never with a provider lock error.
    /// </summary>
    [Fact]
    public async Task A_count_is_refused_when_stock_changed_while_it_waited_for_the_write_lock()
    {
        await using var competing = await BeginUncommittedRestockAsync(4);

        var apply = Task.Run(() => ApplyAsync(Options(), countedStock: 8, expectedCurrentStock: OpeningStock));

        await Task.Delay(LockWaitWindow);
        await competing.CommitAsync(CancellationToken.None);

        var answer = await apply;

        Assert.Null(answer.Result);
        var conflict = Assert.IsType<DomainConflictException>(answer.Failure);
        Assert.Contains("has changed to 14", conflict.Message, StringComparison.Ordinal);

        // The competing restock's quantity, untouched: the count recorded no movement of its own.
        var (quantityInStock, countMovements) = Read();
        Assert.Equal(14, quantityInStock);
        Assert.Equal(0, countMovements);
    }

    /// <summary>
    /// Two operators counting the same product at the same instant, the production shape of the same
    /// race. Exactly one count is applied, and the loser is refused against the quantity the winner
    /// left rather than applying its own difference on top of it.
    /// </summary>
    [Fact]
    public async Task Two_simultaneous_counts_of_one_product_apply_exactly_one()
    {
        var answers = await RaceAsync(
            () => ApplyAsync(Options(), countedStock: 8, expectedCurrentStock: OpeningStock),
            () => ApplyAsync(Options(), countedStock: 7, expectedCurrentStock: OpeningStock));

        var applied = Assert.Single(answers.Where(answer => answer.Result is not null));
        var refused = Assert.Single(answers.Where(answer => answer.Failure is not null));

        Assert.Equal(InventoryCountApplyOutcome.Applied, applied.Result!.Outcome);
        Assert.IsType<DomainConflictException>(refused.Failure);

        var (quantityInStock, countMovements) = Read();
        Assert.Equal(applied.Result.CurrentStock, quantityInStock);
        Assert.Equal(1, countMovements);
    }

    /// <summary>
    /// A count whose difference is zero reads the authoritative quantity under the same lock, so a
    /// change that committed first is refused rather than confirmed. It writes nothing either way,
    /// which is exactly why that refusal has to come from the read.
    /// </summary>
    [Fact]
    public async Task A_zero_difference_count_is_refused_when_stock_changed_first()
    {
        await using (var competing = await BeginUncommittedRestockAsync(4))
        {
            await competing.CommitAsync(CancellationToken.None);
        }

        var answer = await ApplyAsync(Options(), countedStock: OpeningStock, expectedCurrentStock: OpeningStock);

        Assert.Null(answer.Result);
        Assert.IsType<DomainConflictException>(answer.Failure);

        var (quantityInStock, countMovements) = Read();
        Assert.Equal(14, quantityInStock);
        Assert.Equal(0, countMovements);
    }

    /// <summary>What one apply answered: its result, or the exception it was refused with.</summary>
    private sealed record ApplyAnswer(InventoryCountApplyResultDto? Result, Exception? Failure);

    private async Task<ApplyAnswer> ApplyAsync(
        DbContextOptions<AppDbContext> options, int countedStock, int expectedCurrentStock)
    {
        await using var db = TestAppDbContext.For(options, BusinessA);
        try
        {
            var result = await new ApplyInventoryCount(StoreFor(db)).Handle(
                ProductId,
                new InventoryCountApplyRequestDto(countedStock, expectedCurrentStock),
                CancellationToken.None);
            return new ApplyAnswer(result, null);
        }
        catch (Exception failure)
        {
            return new ApplyAnswer(null, failure);
        }
    }

    /// <summary>Releases both applies at the same instant and returns what each of them answered.</summary>
    private static async Task<ApplyAnswer[]> RaceAsync(params Func<Task<ApplyAnswer>>[] applies)
    {
        using var barrier = new Barrier(applies.Length);

        return await Task.WhenAll(applies.Select(apply => Task.Run(async () =>
        {
            barrier.SignalAndWait();
            return await apply();
        })));
    }

    /// <summary>
    /// A manual restock of <paramref name="quantity"/> units through the ordinary adjustment path -
    /// the competing request. It carries its own unit cost, as a positive Restock must. Returns the
    /// exception it was refused with, or <see langword="null"/> when it was recorded.
    /// </summary>
    private async Task<Exception?> RestockAsync(DbContextOptions<AppDbContext> options, int quantity)
    {
        await using var db = TestAppDbContext.For(options, BusinessA);
        try
        {
            await new AdjustStock(
                    new EfStockAdjustmentStore(db, TestCostingUseCases.RecordMovement(db), TestCostingUseCases.Rebuild(db)),
                    new EfLocalMachineStore(db))
                .Handle(
                    ProductId,
                    new ManualStockAdjustmentInput(
                        quantity,
                        DomainStock.StockAdjustmentReason.Restock,
                        "competing restock",
                        null,
                        null,
                        OpeningUnitCost),
                    CancellationToken.None);
            return null;
        }
        catch (Exception failure)
        {
            return failure;
        }
    }

    /// <summary>
    /// A restock of <paramref name="quantity"/> units that has taken SQLite's write lock and is not
    /// committed yet - another request mid-flight. It records the movement as well as the quantity,
    /// so the product's stored stock still reconciles to its replayed history once it commits.
    /// </summary>
    private async Task<UncommittedRestock> BeginUncommittedRestockAsync(int quantity)
    {
        var db = TestAppDbContext.For(Options(), BusinessA);
        var transaction = await db.Database.BeginTransactionAsync();

        var product = await db.Products.SingleAsync(p => p.Id == ProductId);
        product.QuantityInStock += quantity;
        db.StockAdjustments.Add(new StockAdjustment
        {
            ProductId = ProductId,
            QuantityChange = quantity,
            QuantityAfter = product.QuantityInStock,
            Reason = StockAdjustmentReason.Restock,
            UnitCost = OpeningUnitCost,
            EffectiveAt = RecordedAt,
        });

        // The write is what takes the lock; it is held until this transaction commits.
        await db.SaveChangesAsync();
        return new UncommittedRestock(db, transaction);
    }

    private sealed class UncommittedRestock(AppDbContext db, IDbContextTransaction transaction) : IAsyncDisposable
    {
        public Task CommitAsync(CancellationToken cancellationToken) =>
            transaction.CommitAsync(cancellationToken);

        public async ValueTask DisposeAsync()
        {
            await transaction.DisposeAsync();
            await db.DisposeAsync();
        }
    }

    private static EfInventoryCountAdjustmentStore StoreFor(AppDbContext db)
    {
        var record = TestCostingUseCases.RecordMovement(db);
        var rebuild = TestCostingUseCases.Rebuild(db);
        return new EfInventoryCountAdjustmentStore(
            db, record, rebuild, new GetRestockCostSuggestion(new EfStockAdjustmentStore(db, record, rebuild)));
    }

    /// <summary>The stored storage quantity, and how many movements the count itself recorded.</summary>
    private (int QuantityInStock, int CountMovements) Read()
    {
        using var db = TestAppDbContext.Unrestricted(Options());
        return (
            db.Products.AsNoTracking().Single(product => product.Id == ProductId).QuantityInStock,
            db.StockAdjustments.AsNoTracking().Count(adjustment =>
                adjustment.ProductId == ProductId && adjustment.Notes == "Take Inventory count"));
    }

    private DbContextOptions<AppDbContext> Options() =>
        new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connectionString).Options;

    private DbContextOptions<AppDbContext> ImpatientOptions() =>
        new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_impatientConnectionString).Options;

    private DbContextOptions<AppDbContext> OptionsWith(IInterceptor interceptor) =>
        new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connectionString)
            .AddInterceptors(interceptor)
            .Options;

    /// <summary>
    /// Runs a competing restock once the count's authoritative read of the product has executed - the
    /// quantity its comparison and its difference are decided on. That is the instant the
    /// unserialised implementation was exposed at: with no lock held around that read, another writer
    /// could commit a new quantity and the count would still apply the difference it had decided.
    /// </summary>
    private sealed class CompetesWhenTheCountHasReadTheProduct(Func<Task<Exception?>> competing)
        : DbCommandInterceptor
    {
        private bool _attempted;

        /// <summary>Whether the window was reached at all; a test that never reaches it proves nothing.</summary>
        public bool Ran { get; private set; }

        public Exception? Failure { get; private set; }

        public override ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(command);
            Compete(command.CommandText);
            return base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
        }

        private void Compete(string? commandText)
        {
            if (_attempted || commandText?.Contains(@"""Products""", StringComparison.Ordinal) != true) return;

            _attempted = true;
            Ran = true;
            try
            {
                // Blocking is deliberate: the competing request has to be resolved inside this
                // window, and it runs on its own impatient connection, so nothing about the count's
                // own connection is waiting on it. `Task.Run` is what keeps blocking safe - it takes
                // the competing restock off the test runner's limited synchronization context, which
                // this thread is sitting on while it waits.
                Failure = Task.Run(competing).GetAwaiter().GetResult();
            }
            catch (Exception failure)
            {
                Failure = failure;
            }
        }
    }
}
