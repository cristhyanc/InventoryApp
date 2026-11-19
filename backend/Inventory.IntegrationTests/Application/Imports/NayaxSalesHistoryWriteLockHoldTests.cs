using System.Diagnostics;
using Inventory.Application.Imports;
using Inventory.Domain.Imports;
using Inventory.Infrastructure.Persistence;
using Xunit;
using Xunit.Abstractions;

namespace InventoryApi.Tests.Application.Imports;

/// <summary>
/// Issue #1304's measured budget: <strong>no batch</strong> of a sales history import may hold
/// SQLite's single write lock for more than
/// <see cref="NayaxSalesHistoryPolicy.MaxWriteLockHoldSeconds"/> seconds.
///
/// <para><strong>Why that is the number.</strong> It is the same budget issue #1300 measured the
/// synchronous upload against, because it is the same lock: every other write in the application
/// waits on it and gives up after the 30-second busy timeout. The difference is what the budget
/// applies to - there, one whole file; here, one batch of
/// <see cref="NayaxSalesHistoryPolicy.BatchRows"/> rows, which is why a history fifty times the size
/// of the uploaded import's ceiling is safe at all.</para>
///
/// <para><strong>Why the file in this case is not 250,000 rows.</strong> The hold being measured is
/// per batch and a batch is 2,000 rows whatever the file holds, so three batches measure the same
/// thing as a hundred and twenty-five of them - and this repository stops any test that runs past 90
/// seconds (AGENTS.md § Commands), which a quarter of a million rows of real costing would. The
/// measured per-batch figures are written to the test output, which is where the pull request's
/// numbers come from, and the total for a full-length history is stated there as what it is: the
/// per-batch figure multiplied by the batches, not a measured end-to-end run.</para>
///
/// <para>What is measured is the interval between a batch's transaction being opened and committed,
/// from outside the use case, over a real SQLite <strong>file</strong> and the production wiring -
/// the real streaming reader, the real EF store, the real sale costing and the real inventory-cost
/// replay - against products that each have a transition baseline and a stock movement, so the
/// per-row costing actually reads the ledger as it does in production.</para>
/// </summary>
public class NayaxSalesHistoryWriteLockHoldTests
{
    /// <summary>
    /// How many rows the measured file holds. Enough batches to show what the hold <em>does</em> as
    /// a history grows - which is the thing the batch size is chosen against - and few enough to
    /// finish well inside this repository's 90-second per-test ceiling.
    /// </summary>
    private const int Rows = 6_000;

    private readonly ITestOutputHelper _output;

    public NayaxSalesHistoryWriteLockHoldTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Every_batch_holds_the_write_lock_inside_its_budget()
    {
        const int rows = Rows;

        await using var fixture = await NayaxSalesHistoryImportFixture.OnDiskAsync();
        var jobId = await fixture.QueueJobAsync(NayaxSalesHistoryImportFixture.Csv(rows));

        var whole = Stopwatch.StartNew();
        NayaxSalesHistoryImportJobRunResult result;
        ObservedSalesHistoryTransactions observed;
        await using (var db = fixture.Context())
        {
            observed = new ObservedSalesHistoryTransactions(new EfNayaxSalesImportTransactions(db));
            result = await fixture.Process(db, observed).Handle(jobId, CancellationToken.None);
        }

        whole.Stop();

        var holds = observed.Holds;
        var longest = holds.Max(hold => hold.TotalSeconds);
        var average = holds.Average(hold => hold.TotalSeconds);

        _output.WriteLine(
            $"{rows} rows in {holds.Count} batches of {NayaxSalesHistoryPolicy.BatchRows}, over "
                + $"{NayaxSalesHistoryImportFixture.ProductCount} products: write lock held "
                + $"{string.Join("s, ", holds.Select(hold => hold.TotalSeconds.ToString("F1")))}s.");
        _output.WriteLine(
            $"Longest batch {longest:F1}s against a budget of "
                + $"{NayaxSalesHistoryPolicy.MaxWriteLockHoldSeconds}s, average {average:F1}s, first "
                + $"{holds[0].TotalSeconds:F1}s, last {holds[^1].TotalSeconds:F1}s; whole import "
                + $"{whole.Elapsed.TotalSeconds:F1}s.");
        _output.WriteLine(
            "The hold grows with the sales already stored, because each row's cost replays its "
                + "product's own ledger, so the figure to read is the last batch rather than the "
                + $"average. These four products end with {rows / NayaxSalesHistoryImportFixture.ProductCount} "
                + "sales each, which is what a 250,000-row history over 200 products reaches - "
                + "deliberately the fast end of the growth rather than a comfortable one.");

        Assert.Equal(NayaxSalesHistoryImportJobRunOutcome.Finished, result.Outcome);
        Assert.Equal(rows, result.Progress.Created);
        Assert.Equal(rows / NayaxSalesHistoryPolicy.BatchRows, holds.Count);
        Assert.All(
            holds,
            hold => Assert.InRange(
                hold.TotalSeconds, 0, NayaxSalesHistoryPolicy.MaxWriteLockHoldSeconds));
    }
}
