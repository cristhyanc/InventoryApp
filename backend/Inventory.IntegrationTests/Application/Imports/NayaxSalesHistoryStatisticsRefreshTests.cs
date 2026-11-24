using Inventory.Application.Imports;
using Inventory.Application.Maintenance;
using Inventory.Domain.Imports;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Application.Imports;

/// <summary>
/// The second trigger of issue #1287's statistics policy: a <em>finished</em> background sales
/// history import asks for the sales date-range statistics to be refreshed, because that is the one
/// path in this application that can add a quarter of a million sales between two restarts.
///
/// <para>Three things are asserted here, and they are the whole of the hook: a finished job asks
/// exactly once, after the job is recorded Completed; an attempt that did not finish the file asks
/// nothing, because the next attempt will; and nothing the refresh answers - including that the
/// database was unavailable for it - can change the import. The bound itself, and what collecting
/// does to a query plan, are
/// <c>InventoryApi.Tests.Adapters.Persistence.NayaxSalesDateRangeIndexMigrationTests</c>'s, over a
/// real database file and the real adapter.</para>
/// </summary>
public class NayaxSalesHistoryStatisticsRefreshTests
{
    /// <summary>
    /// The history is imported, the job is Completed, and the refresh is asked for exactly once -
    /// after the completion, so what it finds is the table with every row of the file in it.
    /// </summary>
    [Fact]
    public async Task A_finished_import_asks_once_for_the_sales_date_range_statistics()
    {
        const int rows = NayaxSalesHistoryPolicy.BatchRows + 5;

        await using var fixture = await NayaxSalesHistoryImportFixture.CreateAsync();
        var jobId = await fixture.QueueJobAsync(NayaxSalesHistoryImportFixture.Csv(rows));

        await using var db = fixture.Context();
        var statistics = new RecordingSalesDateRangeStatistics(
            SalesDateRangeStatisticsOutcome.Collected,
            onCall: async () => Assert.Equal(
                NayaxSalesHistoryImportJobState.Completed, (await fixture.JobRowAsync(jobId)).State));

        var result = await fixture.Process(db, statistics: statistics)
            .Handle(jobId, CancellationToken.None);

        Assert.Equal(NayaxSalesHistoryImportJobRunOutcome.Finished, result.Outcome);
        Assert.Equal(1, statistics.Calls);
        Assert.Equal(SalesDateRangeStatisticsOutcome.Collected, result.StatisticsRefresh);

        // Every row of the file is imported, which is the point of asking after the completion.
        Assert.Equal(rows, (await fixture.SalesAsync()).Count);
    }

    /// <summary>
    /// An attempt that ends without finishing the file asks for nothing: the import is not over, the
    /// job is queued again, and the attempt that does finish it is what asks.
    /// </summary>
    [Fact]
    public async Task A_requeued_attempt_asks_for_no_refresh_and_the_finishing_one_does()
    {
        const int rows = NayaxSalesHistoryPolicy.BatchRows + 3;

        await using var fixture = await NayaxSalesHistoryImportFixture.CreateAsync();
        var jobId = await fixture.QueueJobAsync(NayaxSalesHistoryImportFixture.Csv(rows));

        var statistics = new RecordingSalesDateRangeStatistics(
            SalesDateRangeStatisticsOutcome.AlreadyCurrent);

        await using (var db = fixture.Context())
        {
            var failing = new FailingSalesHistoryTransactions(
                new EfNayaxSalesImportTransactions(db), failOnBatch: 2);

            var requeued = await fixture.Process(db, failing, statistics: statistics)
                .Handle(jobId, CancellationToken.None);

            Assert.Equal(NayaxSalesHistoryImportJobRunOutcome.Requeued, requeued.Outcome);
            Assert.Null(requeued.StatisticsRefresh);
            Assert.Equal(0, statistics.Calls);
        }

        await using (var db = fixture.Context())
        {
            var finished = await fixture.Process(db, statistics: statistics)
                .Handle(jobId, CancellationToken.None);

            Assert.Equal(NayaxSalesHistoryImportJobRunOutcome.Finished, finished.Outcome);
            Assert.Equal(SalesDateRangeStatisticsOutcome.AlreadyCurrent, finished.StatisticsRefresh);
            Assert.Equal(1, statistics.Calls);
        }
    }

    /// <summary>
    /// A refresh that could not be made leaves the import exactly as it was: the job stays Completed,
    /// every row stays imported, and the attempt reports what happened rather than failing over it.
    /// Statistics are an optimisation, so a held write lock must never turn a finished import into a
    /// retried one.
    /// </summary>
    [Fact]
    public async Task A_refresh_that_could_not_be_made_leaves_the_finished_import_alone()
    {
        const int rows = 12;

        await using var fixture = await NayaxSalesHistoryImportFixture.CreateAsync();
        var jobId = await fixture.QueueJobAsync(NayaxSalesHistoryImportFixture.Csv(rows));

        await using var db = fixture.Context();
        var result = await fixture
            .Process(
                db,
                statistics: new RecordingSalesDateRangeStatistics(
                    SalesDateRangeStatisticsOutcome.Unavailable))
            .Handle(jobId, CancellationToken.None);

        Assert.Equal(NayaxSalesHistoryImportJobRunOutcome.Finished, result.Outcome);
        Assert.Equal(SalesDateRangeStatisticsOutcome.Unavailable, result.StatisticsRefresh);

        var job = await fixture.JobRowAsync(jobId);
        Assert.Equal(NayaxSalesHistoryImportJobState.Completed, job.State);
        Assert.Null(job.LastError);
        Assert.Equal(rows, (await fixture.SalesAsync()).Count);
    }

    /// <summary>
    /// The real adapter keeps the promise its callers rely on: a database it cannot read or write
    /// reports <see cref="SalesDateRangeStatisticsOutcome.Unavailable"/> with the failure's type name
    /// rather than raising. Startup and a finished import both continue on that answer.
    /// </summary>
    [Fact]
    public async Task The_real_adapter_reports_an_unreachable_database_rather_than_raising()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(
                "Data Source="
                + Path.Combine(
                    Path.GetTempPath(),
                    $"inventory-no-such-directory-{Guid.NewGuid():N}",
                    "inventory.db"))
            .Options;

        await using var db = TestAppDbContext.Unrestricted(options);

        var result = await new SqliteSalesDateRangeStatistics(db)
            .CollectIfStaleAsync(CancellationToken.None);

        Assert.Equal(SalesDateRangeStatisticsOutcome.Unavailable, result.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(result.FailureType));
        Assert.Null(result.CurrentSales);
        Assert.Equal(TimeSpan.Zero, result.Duration);
    }

    /// <summary>
    /// A given answer, with the asks counted and an optional check made at the moment of the ask -
    /// which is how "it is asked after the job is Completed" is asserted rather than assumed.
    /// </summary>
    private sealed class RecordingSalesDateRangeStatistics : ISalesDateRangeStatistics
    {
        private readonly SalesDateRangeStatisticsOutcome _outcome;
        private readonly Func<Task>? _onCall;

        public RecordingSalesDateRangeStatistics(
            SalesDateRangeStatisticsOutcome outcome, Func<Task>? onCall = null)
        {
            _outcome = outcome;
            _onCall = onCall;
        }

        /// <summary>How many times the refresh has been asked for.</summary>
        public int Calls { get; private set; }

        public async Task<SalesDateRangeStatisticsResult> CollectIfStaleAsync(
            CancellationToken cancellationToken)
        {
            Calls++;

            if (_onCall is not null)
                await _onCall();

            return new SalesDateRangeStatisticsResult(_outcome);
        }
    }
}
