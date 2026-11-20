using Inventory.Application.Imports;
using Inventory.Domain.Imports;
using Inventory.Infrastructure.Persistence;
using Xunit;

namespace InventoryApi.Tests.Application.Imports;

/// <summary>
/// What a <strong>rolled-back batch</strong> of the background sales history import leaves behind
/// (issue #1304).
///
/// <para>These are the two failures that happen with part of a batch already staged on the unit of
/// work - a row that fails part-way through the batch, and a failure at the commit with the batch's
/// sales, its pending cost rebuilds and its progress all staged - and they are the ones that matter,
/// because the requeue or the failure the attempt records next is a save on that same unit of work
/// and <em>outside</em> any transaction. A rollback undoes what the database was told; it does not
/// empty the unit of work.</para>
///
/// <para><strong>The two differ, and both are pinned here.</strong> A row that throws part-way
/// through the batch leaves the rows before it <em>unsaved</em> and tracked as added, so without the
/// discard in <c>ProcessNayaxSalesHistoryImportJob.ApplyBatchAsync</c> the save that records the
/// requeue inserts them after all - measured as 503 stored sales against a recorded progress of 500,
/// outside any transaction, which is exactly the state the resume then trusts. A failure at the
/// commit instead leaves the batch's writes rolled back but believed persisted by the tracker, so
/// nothing is re-inserted and what the discard removes is a unit of work that disagrees with the
/// database: the outcome is recorded from the job's durable row rather than from the instance the
/// lost batch had moved on.</para>
///
/// <para>Every assertion about what survived is read <strong>from a fresh context</strong> through
/// the fixture, never from the context the attempt ran on, because what is being asserted is the
/// durable state and a tracked instance would answer for the staged one.</para>
///
/// <para>Each case gives its first batch and its second batch <em>different products</em>, so that a
/// sale or a pending rebuild leaking out of the second is visible rather than indistinguishable from
/// the first batch's own.</para>
/// </summary>
public class NayaxSalesHistoryRollbackTests
{
    private const long FirstTransactionId = 9_000_001;

    /// <summary>
    /// The history's two batches: the first over products 1 and 2, the second over products 3 and 4
    /// an hour later, so the second batch's rows and rebuilds are identifiable.
    /// </summary>
    private static string TwoBatches(int secondBatchRows) =>
        NayaxSalesHistoryImportFixture.Concat(
            NayaxSalesHistoryImportFixture.Csv(
                NayaxSalesHistoryPolicy.BatchRows,
                FirstTransactionId,
                products: 2,
                firstProduct: 1),
            NayaxSalesHistoryImportFixture.Csv(
                secondBatchRows,
                FirstTransactionId + NayaxSalesHistoryPolicy.BatchRows,
                products: 2,
                firstProduct: 3,
                firstSaleAt: NayaxSalesHistoryImportFixture.FirstSale.AddDays(1)));

    /// <summary>
    /// A batch that dies after it has staged rows - the third sale of the second batch cannot be
    /// costed - commits none of them. Only the first batch survives, the progress says exactly that,
    /// and the next attempt imports every remaining row exactly once.
    /// </summary>
    [Fact]
    public async Task A_failure_after_a_batch_staged_rows_commits_none_of_them()
    {
        const int secondBatchRows = 7;
        const int rows = NayaxSalesHistoryPolicy.BatchRows + secondBatchRows;

        await using var fixture = await NayaxSalesHistoryImportFixture.CreateAsync();
        var jobId = await fixture.QueueJobAsync(TwoBatches(secondBatchRows));

        ObservedSalesHistoryTransactions transactions;
        await using (var db = fixture.Context())
        {
            // The first batch's 500 sales are costed and committed; the second batch's third row
            // throws with its first two sales added, costed and tracked.
            var costing = new FailingSaleCosting(
                TestCostingUseCases.CostSale(db, TestCostingUseCases.Rebuild(db), fixture.Clock),
                failOnSale: NayaxSalesHistoryPolicy.BatchRows + 3);
            transactions = new ObservedSalesHistoryTransactions(
                new EfNayaxSalesImportTransactions(db));

            var result = await fixture.Process(db, transactions, costing)
                .Handle(jobId, CancellationToken.None);

            Assert.Equal(NayaxSalesHistoryImportJobRunOutcome.Requeued, result.Outcome);

            // Two batches begun, one committed.
            Assert.Equal(2, transactions.Begun);
            Assert.Equal(1, transactions.Committed);
        }

        // The durable state, read from a fresh context: only the committed batch is there.
        var interrupted = await fixture.JobRowAsync(jobId);
        Assert.Equal(NayaxSalesHistoryImportJobState.Queued, interrupted.State);
        Assert.Equal(NayaxSalesHistoryPolicy.BatchRows, interrupted.RowsRead);
        Assert.Equal(NayaxSalesHistoryPolicy.BatchRows, interrupted.Created);
        Assert.Equal(0, interrupted.Updated);
        Assert.Equal(1, interrupted.Attempts);
        Assert.NotNull(interrupted.LastError);
        Assert.NotNull(interrupted.File);

        var staged = await fixture.SalesAsync();
        Assert.Equal(NayaxSalesHistoryPolicy.BatchRows, staged.Count);

        // Not one row of the failed batch reached the database: the two sales it had staged before
        // the third threw are the ones a save on the rolled-back unit of work would have written.
        Assert.DoesNotContain(
            staged,
            sale => sale.TransactionID >= FirstTransactionId + NayaxSalesHistoryPolicy.BatchRows);

        // And the progress agrees with what is stored, which is what the resume depends on.
        Assert.Equal(staged.Count, interrupted.RowsRead);

        // Only the first batch's two products have a pending cost rebuild.
        var rebuilds = await fixture.PendingRebuildsAsync();
        Assert.Equal(2, rebuilds.Count);

        // All of which is true because the lost batch's unit of work was discarded.
        Assert.Equal(1, transactions.Discarded);

        // The next attempt resumes at the row after the committed batch and loses nothing.
        await using (var db = fixture.Context())
        {
            var result = await fixture.Process(db).Handle(jobId, CancellationToken.None);

            Assert.Equal(NayaxSalesHistoryImportJobRunOutcome.Finished, result.Outcome);
            Assert.Equal(secondBatchRows, result.RowsProcessedNow);
        }

        var finished = await fixture.JobRowAsync(jobId);
        Assert.Equal(NayaxSalesHistoryImportJobState.Completed, finished.State);
        Assert.Equal(rows, finished.RowsRead);

        // Every row created exactly once: nothing duplicated, nothing skipped.
        Assert.Equal(rows, finished.Created);
        Assert.Equal(0, finished.Updated);

        var sales = await fixture.SalesAsync();
        Assert.Equal(rows, sales.Count);
        Assert.Equal(rows, sales.Select(sale => sale.TransactionID).Distinct().Count());

        // All four products now, each with one row at its own earliest affected instant.
        Assert.Equal(NayaxSalesHistoryImportFixture.ProductCount, (await fixture.PendingRebuildsAsync()).Count);
    }

    /// <summary>
    /// A batch that fails at its commit - with its sales, its pending cost rebuilds and its progress
    /// all staged - leaves none of the three. The job's durable progress is still the committed
    /// batch's, and the resume imports the rest exactly once.
    ///
    /// <para>The lost batch had been written inside the transaction before the commit failed, so the
    /// tracker believes it persisted and would re-insert nothing; what this case pins is that the
    /// recorded requeue is decided from the durable row and not from that stale unit of work, and
    /// that neither the rolled-back rows, nor the batch's pending rebuilds, nor the progress it had
    /// written survive anywhere.</para>
    /// </summary>
    [Fact]
    public async Task A_failure_at_the_commit_leaves_neither_rows_nor_rebuilds_nor_progress()
    {
        const int secondBatchRows = 9;
        const int rows = NayaxSalesHistoryPolicy.BatchRows + secondBatchRows;

        await using var fixture = await NayaxSalesHistoryImportFixture.CreateAsync();
        var jobId = await fixture.QueueJobAsync(TwoBatches(secondBatchRows));

        CommitFailingSalesHistoryTransactions transactions;
        await using (var db = fixture.Context())
        {
            transactions = new CommitFailingSalesHistoryTransactions(
                new EfNayaxSalesImportTransactions(db), failOnBatch: 2);

            var result = await fixture.Process(db, transactions).Handle(jobId, CancellationToken.None);

            Assert.Equal(NayaxSalesHistoryImportJobRunOutcome.Requeued, result.Outcome);
            Assert.Equal(2, transactions.Begun);
            Assert.Equal(1, transactions.Committed);
        }

        var interrupted = await fixture.JobRowAsync(jobId);
        Assert.Equal(NayaxSalesHistoryImportJobState.Queued, interrupted.State);

        // The progress the failed batch had already staged onto the job row - 509 rows read - is
        // gone with it; what the row says is what is committed.
        Assert.Equal(NayaxSalesHistoryPolicy.BatchRows, interrupted.RowsRead);
        Assert.Equal(NayaxSalesHistoryPolicy.BatchRows, interrupted.Created);
        Assert.Equal(0, interrupted.Skipped);
        Assert.Equal(0, interrupted.Invalid);
        Assert.Equal(1, interrupted.Attempts);

        var staged = await fixture.SalesAsync();
        Assert.Equal(NayaxSalesHistoryPolicy.BatchRows, staged.Count);
        Assert.DoesNotContain(
            staged,
            sale => sale.TransactionID >= FirstTransactionId + NayaxSalesHistoryPolicy.BatchRows);

        // The failed batch's own pending rebuilds were staged before its commit, and are not there:
        // its two products have no row at all.
        var rebuilds = await fixture.PendingRebuildsAsync();
        Assert.Equal(2, rebuilds.Count);

        var firstBatchProducts = staged
            .Where(sale => sale.ProductId is not null)
            .Select(sale => sale.ProductId!.Value)
            .Distinct()
            .ToHashSet();

        Assert.Equal(1, transactions.Discarded);
        Assert.All(rebuilds, rebuild => Assert.Contains(rebuild.ProductId, firstBatchProducts));

        // The resume commits the batch that was lost, once.
        await using (var db = fixture.Context())
        {
            var result = await fixture.Process(db).Handle(jobId, CancellationToken.None);

            Assert.Equal(NayaxSalesHistoryImportJobRunOutcome.Finished, result.Outcome);
            Assert.Equal(secondBatchRows, result.RowsProcessedNow);
        }

        var finished = await fixture.JobRowAsync(jobId);
        Assert.Equal(NayaxSalesHistoryImportJobState.Completed, finished.State);
        Assert.Equal(rows, finished.RowsRead);
        Assert.Equal(rows, finished.Created);
        Assert.Equal(0, finished.Updated);

        var sales = await fixture.SalesAsync();
        Assert.Equal(rows, sales.Count);
        Assert.Equal(rows, sales.Select(sale => sale.TransactionID).Distinct().Count());
        Assert.Equal(NayaxSalesHistoryImportFixture.ProductCount, (await fixture.PendingRebuildsAsync()).Count);
    }

    /// <summary>
    /// The same two failures on the job's <em>last</em> allowed attempt: the job stops as Failed,
    /// and the batch that was staged when it stopped is no more committed than it was on a requeue.
    /// The failure record is the other save on the rolled-back unit of work.
    /// </summary>
    [Fact]
    public async Task A_failure_at_the_commit_on_the_last_attempt_stops_and_commits_nothing_of_it()
    {
        const int secondBatchRows = 5;

        await using var fixture = await NayaxSalesHistoryImportFixture.CreateAsync();
        var jobId = await fixture.QueueJobAsync(TwoBatches(secondBatchRows));

        for (var attempt = 1; attempt <= NayaxSalesHistoryPolicy.MaxAttempts; attempt++)
        {
            await using var db = fixture.Context();

            // Every attempt fails at the commit of the batch it reaches: the first commits its first
            // batch and loses the second, and the attempts after it resume at that row and lose the
            // only batch they have.
            var transactions = new CommitFailingSalesHistoryTransactions(
                new EfNayaxSalesImportTransactions(db), failOnBatch: attempt == 1 ? 2 : 1);

            await fixture.Process(db, transactions).Handle(jobId, CancellationToken.None);
        }

        var job = await fixture.JobRowAsync(jobId);
        Assert.Equal(NayaxSalesHistoryImportJobState.Failed, job.State);
        Assert.Equal(NayaxSalesHistoryPolicy.AttemptsExhaustedReason, job.LastError);
        Assert.Equal(NayaxSalesHistoryPolicy.MaxAttempts, job.Attempts);

        // What it imported is the one committed batch, and that is what it reports.
        Assert.Equal(NayaxSalesHistoryPolicy.BatchRows, job.RowsRead);
        Assert.Equal(NayaxSalesHistoryPolicy.BatchRows, job.Created);

        var sales = await fixture.SalesAsync();
        Assert.Equal(NayaxSalesHistoryPolicy.BatchRows, sales.Count);
        Assert.DoesNotContain(
            sales,
            sale => sale.TransactionID >= FirstTransactionId + NayaxSalesHistoryPolicy.BatchRows);
        Assert.Equal(2, (await fixture.PendingRebuildsAsync()).Count);
    }
}
