using System.Text;
using Inventory.Application.Imports;
using Inventory.Domain.FinancialConfiguration;
using Inventory.Domain.Providers;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Imports;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using InventoryApi.Tests.Application.Time;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace InventoryApi.Tests.Application.Imports;

/// <summary>
/// The world the uploaded Nayax sales history import's cases run in (issue #1304): two businesses
/// with a small product catalogue, over <strong>relational SQLite</strong>, with the real adapters
/// the composition root wires.
///
/// <para>A real SQLite database rather than the EF InMemory provider, deliberately: a batch is a
/// <c>BEGIN IMMEDIATE</c> transaction, re-run safety rests on the unique index over
/// <c>(BusinessId, TransactionID)</c>, the pending cost rebuilds rest on their own unique index, and
/// several of these cases are about what a rolled-back batch leaves behind - none of which exists on
/// the InMemory provider (AGENTS.md § Database and migrations).</para>
///
/// <para>The on-disk variant exists for the cases that need two <em>connections</em>, which is the
/// only way SQLite's own locking and a second writer exist at all.</para>
/// </summary>
internal sealed class NayaxSalesHistoryImportFixture : IAsyncDisposable
{
    /// <summary>The business whose histories are uploaded in most of the cases.</summary>
    public const int BusinessA = 1;

    /// <summary>A second business, for the isolation cases.</summary>
    public const int BusinessB = 2;

    /// <summary>How many products each business's catalogue holds.</summary>
    public const int ProductCount = 4;

    /// <summary>The machine every synthetic row names.</summary>
    public const long MachineId = 942488501;

    /// <summary>The instant an upload is accepted in these cases.</summary>
    public static readonly DateTime UploadedAt = new(2026, 11, 19, 3, 0, 0, DateTimeKind.Utc);

    /// <summary>The instant the worker runs in these cases.</summary>
    public static readonly DateTime RunAt = new(2026, 11, 19, 3, 5, 0, DateTimeKind.Utc);

    /// <summary>The transition baseline cutoff every product's costing starts from.</summary>
    public static readonly DateTime CutoffAt = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>The instant the first synthetic sale is authorized at.</summary>
    public static readonly DateTime FirstSale = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    private const string Header =
        "TransactionID,TransactionStatusId,MachineID,NayaxProductId,SettlementValue,PaymentMethod,"
        + "ProductName,MachineName,AuthorizationDateTimeGMT";

    private readonly SqliteConnection _connection;
    private readonly string? _databasePath;

    private NayaxSalesHistoryImportFixture(SqliteConnection connection, string? databasePath)
    {
        _connection = connection;
        _databasePath = databasePath;
        Options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
    }

    public DbContextOptions<AppDbContext> Options { get; }

    /// <summary>The clock every adapter and use case in the fixture reads.</summary>
    public FakeClock Clock { get; } = new(RunAt);

    /// <summary>A fixture on one in-memory connection, which is enough for one writer.</summary>
    public static Task<NayaxSalesHistoryImportFixture> CreateAsync() => CreateAsync(onDisk: false);

    /// <summary>
    /// A fixture on a real SQLite <strong>file</strong>, so a second connection is a second writer
    /// and the write lock between batches is the real one.
    /// </summary>
    public static Task<NayaxSalesHistoryImportFixture> OnDiskAsync() => CreateAsync(onDisk: true);

    private static async Task<NayaxSalesHistoryImportFixture> CreateAsync(bool onDisk)
    {
        var path = onDisk
            ? Path.Combine(Path.GetTempPath(), $"nayax-sales-history-{Guid.NewGuid():N}.db")
            : null;

        var connection = new SqliteConnection(
            path is null ? "Data Source=:memory:" : $"Data Source={path};Default Timeout=30");
        await connection.OpenAsync();

        var fixture = new NayaxSalesHistoryImportFixture(connection, path);

        await using (var setup = TestAppDbContext.Unrestricted(fixture.Options))
        {
            await setup.Database.EnsureCreatedAsync();
            setup.Businesses.AddRange(
                Business(BusinessA, "Fictional Vending A"), Business(BusinessB, "Fictional Vending B"));
            await setup.SaveChangesAsync();
        }

        foreach (var businessId in new[] { BusinessA, BusinessB })
            await SeedCatalogueAsync(fixture, businessId);

        return fixture;
    }

    /// <summary>A context acting as one business, exactly as a request or a worker scope would.</summary>
    public AppDbContext Context(int businessId = BusinessA) =>
        TestAppDbContext.For(Options, businessId);

    /// <summary>
    /// A second context on a second connection to the same database file, which is what makes
    /// another writer a real one. Only available on an <see cref="OnDiskAsync"/> fixture.
    /// </summary>
    public AppDbContext SecondWriter(int businessId = BusinessA)
    {
        if (_databasePath is null)
            throw new InvalidOperationException("A second writer needs an on-disk fixture.");

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_databasePath};Default Timeout=30")
            .Options;

        return TestAppDbContext.For(options, businessId);
    }

    /// <summary>A context with no business resolved, as the worker's discovery scope has.</summary>
    public AppDbContext DeniedContext() => TestAppDbContext.Denied(Options);

    /// <summary>A context that sees every business, for arranging and reading back from outside.</summary>
    public AppDbContext Unrestricted() => TestAppDbContext.Unrestricted(Options);

    /// <summary>The real job store over <paramref name="db"/>.</summary>
    public static EfNayaxSalesHistoryImportJobStore Jobs(AppDbContext db) => new(db);

    /// <summary>The real streaming CSV reader, which holds nothing between reads.</summary>
    public static StreamingNayaxSalesHistoryCsvReader Reader() => new();

    /// <summary>The real cross-business discovery read over <paramref name="db"/>.</summary>
    public static EfNayaxSalesHistoryImportJobQueue Queue(AppDbContext db) => new(db);

    /// <summary>The real upload use case over <paramref name="db"/>.</summary>
    public UploadNayaxSalesHistory Upload(AppDbContext db) =>
        new(
            Reader(),
            Jobs(db),
            InventoryApi.Tests.Application.Tenancy.FakeAuthenticatedActorAccessor.Identified(Actor()),
            Clock);

    /// <summary>
    /// The real job processor over <paramref name="db"/>, wired exactly as the composition root
    /// wires it: the real streaming reader, the real sales store, the real sale costing over the real
    /// inventory-cost replay, the real provider-id link store, the real pending-rebuild recorder and
    /// the real <c>BEGIN IMMEDIATE</c> transaction.
    ///
    /// <para><paramref name="transactions"/> is how the batching, the lock timing and the
    /// interrupted-attempt cases get at the transaction from outside; left unset, it is the real
    /// one.</para>
    /// </summary>
    public ProcessNayaxSalesHistoryImportJob Process(
        AppDbContext db, INayaxSalesImportTransactions? transactions = null)
    {
        var rebuild = TestCostingUseCases.Rebuild(db);

        return new ProcessNayaxSalesHistoryImportJob(
            Jobs(db),
            Reader(),
            new EfNayaxSalesImportStore(db),
            TestCostingUseCases.CostSale(db, rebuild, Clock),
            new EfNayaxProductLinkStore(db),
            new EfPendingInventoryCostRebuildStore(db, Clock),
            transactions ?? new EfNayaxSalesImportTransactions(db),
            Clock);
    }

    /// <summary>
    /// Stores one uploaded history as a queued job of <paramref name="businessId"/> through the real
    /// upload use case, and answers its key. A refusal fails the arrangement loudly rather than
    /// quietly returning nothing.
    /// </summary>
    public async Task<int> QueueJobAsync(
        string csv, int businessId = BusinessA, DateTime? uploadedAtUtc = null)
    {
        var was = Clock.UtcNow;
        Clock.UtcNow = uploadedAtUtc ?? UploadedAt;

        try
        {
            await using var db = Context(businessId);

            var result = await Upload(db).Handle(
                "history.csv", Encoding.UTF8.GetBytes(csv), CancellationToken.None);

            return result.JobId
                ?? throw new InvalidOperationException(
                    $"The fixture's upload was refused: {result.Rejection}");
        }
        finally
        {
            Clock.UtcNow = was;
        }
    }

    /// <summary>One job row as the database holds it, read from outside the boundary.</summary>
    public async Task<NayaxSalesHistoryImportJob> JobRowAsync(int jobId)
    {
        await using var db = Unrestricted();

        return await db.NayaxSalesHistoryImportJobs
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(job => job.Id == jobId);
    }

    /// <summary>One business's stored sales, in transaction order, read from outside the boundary.</summary>
    public async Task<List<NayaxSales>> SalesAsync(int businessId = BusinessA)
    {
        await using var db = Unrestricted();

        return await db.NayaxSales
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(sale => sale.BusinessId == businessId)
            .OrderBy(sale => sale.TransactionID)
            .ToListAsync();
    }

    /// <summary>One business's pending inventory-cost rebuilds, read from outside the boundary.</summary>
    public async Task<List<PendingInventoryCostRebuild>> PendingRebuildsAsync(
        int businessId = BusinessA)
    {
        await using var db = Unrestricted();

        return await db.PendingInventoryCostRebuilds
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(row => row.BusinessId == businessId)
            .OrderBy(row => row.ProductId)
            .ToListAsync();
    }

    /// <summary>
    /// A synthetic export of <paramref name="rows"/> completed card sales, spread over the
    /// catalogue's products one minute apart - the same shape issue #1300's own measured file has.
    /// </summary>
    /// <param name="rows">How many data rows to write.</param>
    /// <param name="firstTransactionId">Where the transaction identifiers start.</param>
    /// <param name="status">The transaction status every row carries.</param>
    public static string Csv(
        int rows, long firstTransactionId = 9_000_001, int? status = null)
    {
        var csv = new StringBuilder(Header).Append('\n');

        for (var row = 0; row < rows; row++)
        {
            var product = (row % ProductCount) + 1;
            csv.Append(firstTransactionId + row).Append(',')
                .Append(status ?? NayaxTransactionStatusIds.Completed).Append(',')
                .Append(MachineId).Append(',')
                .Append(product)
                .Append(",3.50,Credit Card,")
                .Append("Fictional line ").Append(product)
                .Append(",Machine A,")
                .Append(FirstSale.AddMinutes(row).ToString("yyyy-MM-ddTHH:mm:ssZ"))
                .Append('\n');
        }

        return csv.ToString();
    }

    /// <summary>The header row on its own, for the "nothing to import" case.</summary>
    public static string HeaderOnly() => Header + "\n";

    /// <summary>A made-up uploader identity, as a validated token would supply one.</summary>
    public static ActorIdentity Actor()
    {
        ActorIdentity.TryCreate(
            "11111111-1111-1111-1111-111111111111",
            "22222222-2222-2222-2222-222222222222",
            out var identity);

        return identity!;
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();

        if (_databasePath is null) return;

        SqliteConnection.ClearAllPools();
        try
        {
            System.IO.File.Delete(_databasePath);
        }
        catch (IOException)
        {
            // A leftover temporary file is not worth failing a test over.
        }
    }

    /// <summary>
    /// One business's products, each with a transition baseline before the sales and a costed
    /// restock - which is what makes the per-row costing read and replay the ledger as it does in
    /// production instead of answering "nothing to cost" for free.
    /// </summary>
    private static async Task SeedCatalogueAsync(NayaxSalesHistoryImportFixture fixture, int businessId)
    {
        await using var seed = fixture.Context(businessId);

        for (var product = 1; product <= ProductCount; product++)
        {
            seed.Products.Add(new Product
            {
                Name = $"Fictional line {product}",
                ExternalProductId = NayaxExternalId.From(product),
                QuantityInStock = 100_000,
                CostingQuantity = 100_000,
                InventoryValue = 100_000 * 2m,
                AverageUnitCost = 2m,
            });
        }

        await seed.SaveChangesAsync();

        var products = await seed.Products.OrderBy(product => product.Id).ToListAsync();
        foreach (var product in products)
        {
            seed.InventoryCostTransitionBaselines.Add(new InventoryCostTransitionBaseline
            {
                ProductId = product.Id,
                HomeStockQuantity = 0,
                OpeningCostingQuantity = 100_000,
                InventoryValue = 100_000 * 2m,
                AverageUnitCost = 2m,
                CutoffAt = CutoffAt,
            });
            seed.StockAdjustments.Add(new StockAdjustment
            {
                ProductId = product.Id,
                QuantityChange = 100_000,
                QuantityAfter = 100_000,
                Reason = StockAdjustmentReason.Restock,
                UnitCost = 2m,
                EffectiveAt = CutoffAt.AddDays(1),
            });
        }

        await seed.SaveChangesAsync();
    }

    private static Inventory.Infrastructure.Models.Business Business(int id, string name) => new()
    {
        Id = id,
        Name = name,
        Status = BusinessStatus.Active,
        SalesProvider = ExternalProvider.Nayax,
        CreatedAtUtc = CutoffAt.AddYears(-1),
    };
}

/// <summary>
/// The real batch transaction with a hook on each <c>BEGIN IMMEDIATE</c> and each commit, so the
/// batching cases can count batches, time the lock each one holds, interleave another writer between
/// batches, or make one batch fail (issue #1304).
/// </summary>
internal sealed class ObservedSalesHistoryTransactions : INayaxSalesImportTransactions
{
    private readonly INayaxSalesImportTransactions _inner;
    private readonly Func<int, Task>? _beforeBatch;
    private readonly Action? _afterBegin;
    private readonly Action<int, TimeSpan>? _afterCommit;

    public ObservedSalesHistoryTransactions(
        INayaxSalesImportTransactions inner,
        Func<int, Task>? beforeBatch = null,
        Action? afterBegin = null,
        Action<int, TimeSpan>? afterCommit = null)
    {
        _inner = inner;
        _beforeBatch = beforeBatch;
        _afterBegin = afterBegin;
        _afterCommit = afterCommit;
    }

    /// <summary>How many batch transactions have been begun.</summary>
    public int Begun { get; private set; }

    /// <summary>How many have been committed.</summary>
    public int Committed { get; private set; }

    /// <summary>How long each committed batch held the write lock, in batch order.</summary>
    public List<TimeSpan> Holds { get; } = [];

    public async Task<INayaxSalesImportTransaction> BeginAsync(CancellationToken cancellationToken)
    {
        // Before the lock is taken, deliberately: this is where "between batches" is.
        if (_beforeBatch is not null)
            await _beforeBatch(Begun);

        Begun++;

        var started = System.Diagnostics.Stopwatch.StartNew();
        var transaction = new Transaction(
            await _inner.BeginAsync(cancellationToken), this, Begun, started);

        // Inside the batch's transaction: this is where "while the lock is held" is.
        _afterBegin?.Invoke();

        return transaction;
    }

    private void Commit(int batch, TimeSpan held)
    {
        Committed++;
        Holds.Add(held);
        _afterCommit?.Invoke(batch, held);
    }

    private sealed class Transaction : INayaxSalesImportTransaction
    {
        private readonly INayaxSalesImportTransaction _inner;
        private readonly ObservedSalesHistoryTransactions _owner;
        private readonly int _batch;
        private readonly System.Diagnostics.Stopwatch _started;

        public Transaction(
            INayaxSalesImportTransaction inner,
            ObservedSalesHistoryTransactions owner,
            int batch,
            System.Diagnostics.Stopwatch started)
        {
            _inner = inner;
            _owner = owner;
            _batch = batch;
            _started = started;
        }

        public async Task CommitAsync(CancellationToken cancellationToken)
        {
            await _inner.CommitAsync(cancellationToken);
            _owner.Commit(_batch, _started.Elapsed);
        }

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
}

/// <summary>
/// The real batch transaction, with the <em>n</em>-th batch made to fail before it is begun
/// (issue #1304). It is the shape of a database that became unavailable part-way through a long
/// import: the batches before it are committed and the job has to resume from them.
/// </summary>
internal sealed class FailingSalesHistoryTransactions : INayaxSalesImportTransactions
{
    private readonly INayaxSalesImportTransactions _inner;
    private readonly int _failOnBatch;

    public FailingSalesHistoryTransactions(INayaxSalesImportTransactions inner, int failOnBatch)
    {
        _inner = inner;
        _failOnBatch = failOnBatch;
    }

    /// <summary>How many batch transactions have been begun, the failed one included.</summary>
    public int Begun { get; private set; }

    public Task<INayaxSalesImportTransaction> BeginAsync(CancellationToken cancellationToken)
    {
        Begun++;

        if (Begun == _failOnBatch)
            throw new SqliteException("the database became unavailable", 5);

        return _inner.BeginAsync(cancellationToken);
    }
}
