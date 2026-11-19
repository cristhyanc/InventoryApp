using System.Diagnostics;
using System.Text;
using Inventory.Application.Imports;
using Inventory.Domain.FinancialConfiguration;
using Inventory.Domain.Imports;
using Inventory.Domain.Providers;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Imports;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace InventoryApi.Tests.Application.Imports;

/// <summary>
/// Issue #1300's measured budget: a file at the row limit must not hold SQLite's single write lock
/// for more than <see cref="NayaxSalesFilePolicy.MaxWriteLockHoldSeconds"/> seconds.
///
/// <para><strong>Why that is the number.</strong> Every other write in the application - a purchase,
/// a stock adjustment, the Nayax SQS queue reader's own save - waits on that lock and gives up after
/// the 30-second busy timeout. An import that held it for minutes therefore did not merely run
/// slowly: it failed everything else happening at the time. The row limit is chosen to fit this
/// budget, so if this case ever exceeds it the limit comes down rather than the budget going up -
/// which is what this case made happen: at the owner's first figure of 20,000 rows it measured 13.8 s
/// here and 14.6 s on the merge-validation runner, so <see cref="NayaxSalesFilePolicy.MaxRows"/> came
/// down to 5,000.</para>
///
/// <para><strong>What is measured.</strong> The interval between the import's transaction being
/// opened and committed, from outside the use case, over a real SQLite <strong>file</strong> and the
/// production wiring - the real workbook reader, the real EF store, the real sale costing and the
/// real inventory-cost replay - against a business with a fleet's worth of products, each with a
/// transition baseline and a stock movement, so the per-row costing actually reads the ledger as it
/// does in production. The measured hold is written to the test output, which is where the pull
/// request's figure comes from.</para>
/// </summary>
public class NayaxSalesImportWriteLockHoldTests
{
    private const int Business = 1;
    private const int ProductCount = 50;
    private const long MachineId = 942488501;

    private static readonly DateTime CutoffAt = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime FirstSale = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    private const string Header =
        "TransactionID,TransactionStatusId,MachineID,NayaxProductId,SettlementValue,PaymentMethod,"
        + "ProductName,MachineName,AuthorizationDateTimeGMT";

    private readonly ITestOutputHelper _output;

    public NayaxSalesImportWriteLockHoldTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task A_file_at_the_row_limit_holds_the_write_lock_inside_its_budget()
    {
        await using var fixture = await SalesFileFixture.CreateAsync();

        var held = TimeSpan.Zero;
        var whole = Stopwatch.StartNew();
        NayaxSalesImportResult upload;
        await using (var db = fixture.Context())
        {
            var timed = new TimedTransactions(new EfNayaxSalesImportTransactions(db));
            upload = await Import(db, timed).Handle(fixture.FileOf(NayaxSalesFilePolicy.MaxRows));
            held = timed.Held;
        }

        whole.Stop();
        _output.WriteLine(
            $"{NayaxSalesFilePolicy.MaxRows} rows: write lock held {held.TotalSeconds:F1}s, "
                + $"whole import {whole.Elapsed.TotalSeconds:F1}s, imported {upload.Imported}.");

        Assert.Equal(NayaxSalesFilePolicy.MaxRows, upload.Imported);
        Assert.InRange(
            held.TotalSeconds, 0, NayaxSalesFilePolicy.MaxWriteLockHoldSeconds);
    }

    /// <summary>
    /// A re-upload of the same file, which is the other shape an operator produces: every row is an
    /// update of a sale that is already stored and already costed. It is measured too, because the
    /// budget is about the lock and not about a particular file - and because this is the pass that
    /// reads a file's worth of stored rows before it writes anything.
    /// </summary>
    [Fact]
    public async Task A_re_upload_of_a_file_at_the_row_limit_holds_the_write_lock_inside_its_budget()
    {
        await using var fixture = await SalesFileFixture.CreateAsync();
        var file = fixture.FileOf(NayaxSalesFilePolicy.MaxRows);

        await using (var first = fixture.Context())
            await Import(first, new EfNayaxSalesImportTransactions(first)).Handle(file);

        var held = TimeSpan.Zero;
        NayaxSalesImportResult reupload;
        await using (var db = fixture.Context())
        {
            var timed = new TimedTransactions(new EfNayaxSalesImportTransactions(db));
            reupload = await Import(db, timed).Handle(file);
            held = timed.Held;
        }

        _output.WriteLine(
            $"{NayaxSalesFilePolicy.MaxRows} rows re-uploaded: write lock held {held.TotalSeconds:F1}s, "
                + $"updated {reupload.Updated}.");

        Assert.Equal(NayaxSalesFilePolicy.MaxRows, reupload.Updated);
        Assert.InRange(held.TotalSeconds, 0, NayaxSalesFilePolicy.MaxWriteLockHoldSeconds);
    }

    private static ImportNayaxSales Import(AppDbContext db, INayaxSalesImportTransactions transactions)
    {
        var rebuild = TestCostingUseCases.Rebuild(db);
        return new ImportNayaxSales(
            new ClosedXmlNayaxSalesWorkbookReader(),
            new EfNayaxSalesImportStore(db),
            transactions,
            TestCostingUseCases.CostSale(db, rebuild),
            rebuild,
            new EfNayaxProductLinkStore(db));
    }

    /// <summary>
    /// Times the real transaction from outside the use case: begun once per import, committed once,
    /// so what it reports is exactly the interval the write lock was held for.
    /// </summary>
    private sealed class TimedTransactions : INayaxSalesImportTransactions
    {
        private readonly INayaxSalesImportTransactions _inner;

        public TimedTransactions(INayaxSalesImportTransactions inner) => _inner = inner;

        public TimeSpan Held { get; private set; }

        public async Task<INayaxSalesImportTransaction> BeginAsync(CancellationToken cancellationToken)
        {
            var started = Stopwatch.StartNew();
            return new Transaction(await _inner.BeginAsync(cancellationToken), this, started);
        }

        private sealed class Transaction : INayaxSalesImportTransaction
        {
            private readonly INayaxSalesImportTransaction _inner;
            private readonly TimedTransactions _owner;
            private readonly Stopwatch _started;

            public Transaction(
                INayaxSalesImportTransaction inner, TimedTransactions owner, Stopwatch started)
            {
                _inner = inner;
                _owner = owner;
                _started = started;
            }

            public async Task CommitAsync(CancellationToken cancellationToken)
            {
                await _inner.CommitAsync(cancellationToken);
                _owner.Held = _started.Elapsed;
            }

            public ValueTask DisposeAsync() => _inner.DisposeAsync();
        }
    }

    /// <summary>
    /// A business on a real SQLite file with a fleet's worth of costed products: each one has a
    /// transition baseline before the sales and a restock movement, which is what makes the per-row
    /// costing read and replay the ledger instead of answering "nothing to cost" for free.
    /// </summary>
    private sealed class SalesFileFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly string _databasePath;

        private SalesFileFixture(SqliteConnection connection, string databasePath)
        {
            _connection = connection;
            _databasePath = databasePath;
            Options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        }

        public DbContextOptions<AppDbContext> Options { get; }

        public static async Task<SalesFileFixture> CreateAsync()
        {
            var path = Path.Combine(Path.GetTempPath(), $"nayax-import-{Guid.NewGuid():N}.db");
            var connection = new SqliteConnection($"Data Source={path};Default Timeout=30");
            await connection.OpenAsync();
            var fixture = new SalesFileFixture(connection, path);

            await using (var setup = TestAppDbContext.Unrestricted(fixture.Options))
            {
                await setup.Database.EnsureCreatedAsync();
                setup.Businesses.Add(new Business
                {
                    Id = Business,
                    Name = "Fictional Vending",
                    Status = BusinessStatus.Active,
                    SalesProvider = ExternalProvider.Nayax,
                    CreatedAtUtc = CutoffAt,
                });
                await setup.SaveChangesAsync();
            }

            await using (var seed = TestAppDbContext.For(fixture.Options, Business))
            {
                for (var product = 1; product <= ProductCount; product++)
                {
                    seed.Products.Add(new Product
                    {
                        Id = product,
                        Name = $"Fictional line {product}",
                        ExternalProductId = NayaxExternalId.From(product),
                        QuantityInStock = 100_000,
                        CostingQuantity = 100_000,
                        InventoryValue = 100_000 * 2m,
                        AverageUnitCost = 2m,
                    });
                    seed.InventoryCostTransitionBaselines.Add(new InventoryCostTransitionBaseline
                    {
                        ProductId = product,
                        HomeStockQuantity = 0,
                        OpeningCostingQuantity = 100_000,
                        InventoryValue = 100_000 * 2m,
                        AverageUnitCost = 2m,
                        CutoffAt = CutoffAt,
                    });
                    seed.StockAdjustments.Add(new StockAdjustment
                    {
                        ProductId = product,
                        QuantityChange = 100_000,
                        QuantityAfter = 100_000,
                        Reason = StockAdjustmentReason.Restock,
                        UnitCost = 2m,
                        EffectiveAt = CutoffAt.AddDays(1),
                    });
                }

                await seed.SaveChangesAsync();
            }

            return fixture;
        }

        public AppDbContext Context() => TestAppDbContext.For(Options, Business);

        /// <summary>A synthetic export of <paramref name="rows"/> completed card sales.</summary>
        public NayaxSalesFileInput FileOf(int rows)
        {
            var csv = new StringBuilder(Header).Append('\n');
            for (var row = 1; row <= rows; row++)
            {
                var product = ((row - 1) % ProductCount) + 1;
                csv.Append(9_000_000 + row).Append(',')
                    .Append(NayaxTransactionStatusIds.Completed).Append(',')
                    .Append(MachineId).Append(',')
                    .Append(product)
                    .Append(",3.50,Credit Card,")
                    .Append("Fictional line ").Append(product)
                    .Append(",Machine A,")
                    .Append(FirstSale.AddMinutes(row).ToString("yyyy-MM-ddTHH:mm:ssZ"))
                    .Append('\n');
            }

            var content = Encoding.UTF8.GetBytes(csv.ToString());
            return new NayaxSalesFileInput("sales.csv", () => new MemoryStream(content));
        }

        public async ValueTask DisposeAsync()
        {
            await _connection.DisposeAsync();
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
