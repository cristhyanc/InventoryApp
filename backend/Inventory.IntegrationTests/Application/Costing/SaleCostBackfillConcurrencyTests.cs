using Inventory.Application.Costing;
using Inventory.Domain.FinancialConfiguration;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Application.Costing;

/// <summary>
/// Issue #1261's read-to-write race in the two <c>Data.Repair</c> sale-cost backfills, over a real
/// SQLite <strong>file</strong> with two connections - which is the only way two writers and SQLite's
/// own locking exist at all (<c>AGENTS.md</c> § Database and migrations: "EF Core InMemory tests do
/// not prove relational behavior").
///
/// <para><strong>The defect these are about.</strong> Both backfills load tracked sales and ask
/// <see cref="SaleCostPrecedence"/> what they may write <em>before</em>
/// <c>EfSaleCostingStore.SaveChangesAsync</c> takes the write lock. Another context can commit a cost
/// on the very same sale in that gap - a ledger rebuild's step-1 cost, say - and the staged decision,
/// made against a row that no longer exists in that form, would then be saved over it and enqueue a
/// cost rebuild for a precedence decision it had already lost. The decision is therefore re-asked
/// inside the write-locked transaction against the latest persisted row, and a staged cost it refuses
/// is discarded: nothing is written for that sale, no rebuild is recorded for it, and the run does not
/// report it as changed.</para>
///
/// <para>The interleaving sits inside the store's own read rather than being left to the scheduler, so
/// each case exercises exactly the window it is about.</para>
/// </summary>
public class SaleCostBackfillConcurrencyTests
{
    private const int BusinessA = 1;
    private const long ProductA = 100;
    private const long TransactionId = 5001;
    private static readonly DateTime OpeningAt = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime SaleAt = OpeningAt.AddDays(1);

    /// <summary>
    /// <strong>The required concurrency case.</strong> A forced Nayax backfill reads a pending sale and
    /// stages its step-3 export cost; a second context commits the product's step-1 ledger cost on that
    /// sale before the backfill reaches the write lock. The stronger cost survives, the backfill
    /// reports nothing costed, and it records no rebuild - the staged write changed nothing, so there
    /// is nothing to replay.
    /// </summary>
    [Fact]
    public async Task A_ledger_cost_committed_after_the_Nayax_backfills_read_survives_it()
    {
        await using var fixture = await BackfillRaceFixture.CreateAsync();

        await using var db = fixture.Context();
        await using var otherWriter = fixture.SecondWriter();

        var store = new InterleavingSaleCostingStore(
            TestCostingUseCases.SaleCostingStore(db),
            interleave: () => CommitCostAsync(
                otherWriter, 2m, SaleCostSource.InventoryLedger));

        var result = await new BackfillNayaxHistoricalSaleCosts(store).Handle(dryRun: false, force: true);

        Assert.Equal(0, result.SalesWouldBeCosted);
        Assert.Equal(1, result.SalesAlreadyCosted);

        // The ledger cost the other writer committed is what stands: the export cost (step 3) is a
        // worse claim than it, and the backfill lost that comparison while it held a stale read.
        await using var verify = fixture.Unrestricted();
        await AssertStoredCostAsync(verify, 2m, SaleCostSource.InventoryLedger);
        Assert.Empty(await verify.PendingInventoryCostRebuilds.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// <strong>The equal-source case.</strong> The forced ledger backfill recomputes the sale's step-1
    /// cost from the history it read, and a cost rebuild commits a <em>corrected</em> step-1 cost on
    /// that sale in the gap. Equal precedence with a different value is a correction the rule allows -
    /// but this run's value was computed from a history that has since moved, so the committed
    /// correction stands and the stale one is left to the next run.
    /// </summary>
    [Fact]
    public async Task A_corrected_ledger_cost_committed_after_the_forced_backfills_read_survives_it()
    {
        await using var fixture = await BackfillRaceFixture.CreateAsync();

        await using var db = fixture.Context();
        await using var otherWriter = fixture.SecondWriter();

        var store = new InterleavingSaleCostingStore(
            TestCostingUseCases.SaleCostingStore(db),
            interleave: () => CommitCostAsync(
                otherWriter, 2.50m, SaleCostSource.InventoryLedger));
        var backfill = new BackfillSaleCosts(
            store, new CostSale(store, TestCostingUseCases.Rebuild(db)));

        var result = await backfill.Handle(dryRun: false, force: true);

        Assert.Equal(0, result.CostedCount);
        Assert.Equal(1, result.AlreadyFinalizedCount);

        await using var verify = fixture.Unrestricted();
        await AssertStoredCostAsync(verify, 2.50m, SaleCostSource.InventoryLedger);
        Assert.Empty(await verify.PendingInventoryCostRebuilds.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// <strong>The equal-source, equal-value case.</strong> Another writer commits exactly the cost the
    /// run decided. There is nothing to write, so nothing is written - and, which is the part that
    /// matters, no cost rebuild is recorded and the run does not count the sale as changed: a product
    /// whose history nobody altered must not be queued for a replay.
    /// </summary>
    [Fact]
    public async Task An_identical_cost_committed_after_the_read_is_not_rewritten_and_records_no_rebuild()
    {
        await using var fixture = await BackfillRaceFixture.CreateAsync();

        await using var db = fixture.Context();
        await using var otherWriter = fixture.SecondWriter();

        var store = new InterleavingSaleCostingStore(
            TestCostingUseCases.SaleCostingStore(db),
            interleave: () => CommitCostAsync(
                otherWriter, 1.10m, SaleCostSource.NayaxTransactionExport));

        var result = await new BackfillNayaxHistoricalSaleCosts(store).Handle(dryRun: false, force: true);

        Assert.Equal(0, result.SalesWouldBeCosted);
        Assert.Equal(1, result.SalesAlreadyCosted);

        await using var verify = fixture.Unrestricted();
        await AssertStoredCostAsync(verify, 1.10m, SaleCostSource.NayaxTransactionExport);
        Assert.Empty(await verify.PendingInventoryCostRebuilds.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// The guard refuses a stale write; it does not refuse every write. Another writer commits the
    /// sale's step-3 export cost in the gap, and the forced ledger backfill's step-1 decision is a
    /// strictly better claim than that - so it is still written, and it still records the product's
    /// rebuild in the same transaction.
    /// </summary>
    [Fact]
    public async Task A_better_step_still_replaces_a_cost_committed_after_the_read()
    {
        await using var fixture = await BackfillRaceFixture.CreateAsync();

        await using var db = fixture.Context();
        await using var otherWriter = fixture.SecondWriter();

        var store = new InterleavingSaleCostingStore(
            TestCostingUseCases.SaleCostingStore(db),
            interleave: () => CommitCostAsync(
                otherWriter, 1.10m, SaleCostSource.NayaxTransactionExport));
        var backfill = new BackfillSaleCosts(
            store, new CostSale(store, TestCostingUseCases.Rebuild(db)));

        var result = await backfill.Handle(dryRun: false, force: true);

        Assert.Equal(1, result.CostedCount);

        await using var verify = fixture.Unrestricted();
        await AssertStoredCostAsync(verify, 2m, SaleCostSource.InventoryLedger);
        var pending = Assert.Single(await verify.PendingInventoryCostRebuilds.AsNoTracking().ToListAsync());
        Assert.Equal((BusinessA, ProductA, SaleAt), (pending.BusinessId, pending.ProductId, pending.FromInstantUtc));
    }

    /// <summary>
    /// What the other writer does: commits a final cost on the sale, as a cost rebuild or another
    /// repair tool would, on its own connection and before the backfill takes the write lock.
    /// </summary>
    private static async Task CommitCostAsync(AppDbContext db, decimal unitCost, SaleCostSource source)
    {
        var sale = await db.NayaxSales.SingleAsync(row => row.TransactionID == TransactionId);
        sale.UnitCostAtSale = unitCost;
        sale.CostOfGoodsSold = unitCost;
        sale.CostingStatus = SaleCostingStatus.Costed;
        sale.CostSource = source;
        await db.SaveChangesAsync();
    }

    private static async Task AssertStoredCostAsync(
        AppDbContext db, decimal unitCost, SaleCostSource source)
    {
        var sale = await db.NayaxSales.AsNoTracking().SingleAsync(row => row.BusinessId == BusinessA);
        Assert.Equal(
            (unitCost, unitCost, SaleCostingStatus.Costed, source),
            (sale.UnitCostAtSale, sale.CostOfGoodsSold, sale.CostingStatus, sale.CostSource));
    }

    /// <summary>
    /// The real <see cref="ISaleCostingStore"/> with <paramref name="interleave"/> run once after it
    /// has read the sales a run decides on and before anything is staged or saved - which is exactly
    /// the window these cases are about, placed deterministically rather than left to the scheduler.
    /// Everything else is the production adapter's own behaviour, including its write lock.
    /// </summary>
    private sealed class InterleavingSaleCostingStore(
        ISaleCostingStore inner, Func<Task> interleave) : ISaleCostingStore
    {
        private bool _interleaved;

        public Task<DateTime?> GetTransitionCutoffAsync(long productId, CancellationToken cancellationToken) =>
            inner.GetTransitionCutoffAsync(productId, cancellationToken);

        public async Task<IReadOnlyList<CostableSale>> LoadStockConsumingSalesAsync(
            StockConsumingSaleSelection selection, bool forUpdate, CancellationToken cancellationToken)
        {
            var sales = await inner.LoadStockConsumingSalesAsync(selection, forUpdate, cancellationToken);

            if (!_interleaved)
            {
                _interleaved = true;
                await interleave();
            }

            return sales;
        }

        public void StageCost(CostableSale sale, SaleCostAssignment cost) => inner.StageCost(sale, cost);

        public Task<SaleCostSaveOutcome> SaveChangesAsync(CancellationToken cancellationToken) =>
            inner.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// One business with one costed product, one restock of ten units at $2 and one pending sale
    /// carrying a $1.10 Nayax export cost, on a real SQLite file. The file is what makes the second
    /// context a second <em>writer</em>: a shared in-memory database has one connection and therefore
    /// no two writers to have a race between.
    /// </summary>
    private sealed class BackfillRaceFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly string _databasePath;
        private readonly string _connectionString;

        private BackfillRaceFixture(SqliteConnection connection, string databasePath, string connectionString)
        {
            _connection = connection;
            _databasePath = databasePath;
            _connectionString = connectionString;
            Options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        }

        private DbContextOptions<AppDbContext> Options { get; }

        public static async Task<BackfillRaceFixture> CreateAsync()
        {
            var databasePath = Path.Combine(Path.GetTempPath(), $"sale-cost-backfill-{Guid.NewGuid():N}.db");
            var connectionString = $"Data Source={databasePath};Default Timeout=5";
            var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync();

            var fixture = new BackfillRaceFixture(connection, databasePath, connectionString);

            await using (var setup = TestAppDbContext.Unrestricted(fixture.Options))
                await setup.Database.EnsureCreatedAsync();

            await using (var seed = TestAppDbContext.For(fixture.Options, BusinessA))
            {
                seed.Products.Add(new Product { Id = ProductA, Name = "Coke Zero", QuantityInStock = 10 });
                seed.StockAdjustments.Add(new StockAdjustment
                {
                    ProductId = ProductA,
                    QuantityChange = 10,
                    QuantityAfter = 10,
                    Reason = StockAdjustmentReason.Restock,
                    UnitCost = 2m,
                    EffectiveAt = OpeningAt,
                });
                seed.NayaxSales.Add(new NayaxSales
                {
                    TransactionID = TransactionId,
                    TransactionStatusId = NayaxTransactionStatusIds.Completed,
                    MachineID = 1,
                    ProductId = ProductA,
                    NayaxProductId = ProductA,
                    ProductName = "Coke Zero",
                    SettlementValue = 3m,
                    NayaxProductCostPrice = 1.10m,
                    MachineAuthorizationTime = SaleAt,
                });
                await seed.SaveChangesAsync();
            }

            return fixture;
        }

        /// <summary>A context acting as the business, exactly as the backfill's request would.</summary>
        public AppDbContext Context() => TestAppDbContext.For(Options, BusinessA);

        /// <summary>
        /// A context on a <strong>second connection</strong> to the same file, which is what makes it a
        /// second writer rather than the same one under another name.
        /// </summary>
        public AppDbContext SecondWriter() => TestAppDbContext.For(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connectionString).Options, BusinessA);

        /// <summary>A context that sees every business, for reading back from outside the boundary.</summary>
        public AppDbContext Unrestricted() => TestAppDbContext.Unrestricted(Options);

        public async ValueTask DisposeAsync()
        {
            await _connection.DisposeAsync();

            // Pooling keeps the file handle open, so it has to be released before the file can go.
            SqliteConnection.ClearAllPools();

            try
            {
                File.Delete(_databasePath);
            }
            catch (IOException)
            {
                // A leftover temporary file is not worth failing a test over.
            }
        }
    }
}
