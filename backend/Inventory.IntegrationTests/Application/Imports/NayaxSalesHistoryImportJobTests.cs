using Inventory.Application.Imports;
using Inventory.Domain.FinancialConfiguration;
using Inventory.Domain.Imports;
using Inventory.Infrastructure.Persistence;
using Xunit;

namespace InventoryApi.Tests.Application.Imports;

/// <summary>
/// What the background sales history import does with one job (issue #1304): the batches, the
/// progress, the cost rebuilds it records, re-running it, and the business it all belongs to.
///
/// <para>Every case runs the real use case over relational SQLite with the real adapters - the
/// streaming reader, the EF sales store, the real sale costing over the real inventory-cost replay
/// and the real <c>BEGIN IMMEDIATE</c> transaction - because what is being asserted is what actually
/// lands in a database and in how many commits.</para>
/// </summary>
public class NayaxSalesHistoryImportJobTests
{
    /// <summary>
    /// A file longer than one batch is committed in batches of
    /// <see cref="NayaxSalesHistoryPolicy.BatchRows"/>, every row lands as a sale, and the job ends
    /// Completed with the counts it actually applied.
    /// </summary>
    [Fact]
    public async Task A_history_is_imported_in_batches_and_the_job_completes()
    {
        const int rows = NayaxSalesHistoryPolicy.BatchRows + 5;

        await using var fixture = await NayaxSalesHistoryImportFixture.CreateAsync();
        var jobId = await fixture.QueueJobAsync(NayaxSalesHistoryImportFixture.Csv(rows));

        await using var db = fixture.Context();
        var transactions = new ObservedSalesHistoryTransactions(new EfNayaxSalesImportTransactions(db));

        var result = await fixture.Process(db, transactions).Handle(jobId, CancellationToken.None);

        Assert.Equal(NayaxSalesHistoryImportJobRunOutcome.Finished, result.Outcome);
        Assert.Equal(rows, result.Progress.RowsRead);
        Assert.Equal(rows, result.Progress.Created);
        Assert.Equal(0, result.Progress.Updated);
        Assert.Equal(0, result.Progress.Skipped);
        Assert.Equal(0, result.Progress.Invalid);

        // One full batch and one of 5: the batch size is the policy's, whatever the file holds.
        Assert.Equal(2, transactions.Committed);
        Assert.Equal(transactions.Begun, transactions.Committed);

        var job = await fixture.JobRowAsync(jobId);
        Assert.Equal(NayaxSalesHistoryImportJobState.Completed, job.State);
        Assert.Equal(rows, job.TotalRows);
        Assert.Equal(rows, job.RowsRead);
        Assert.Equal(rows, job.Created);
        Assert.Equal(NayaxSalesHistoryImportFixture.RunAt, job.FinishedAtUtc);
        Assert.Null(job.LastError);

        // The uploaded bytes are deleted the moment the job is final.
        Assert.Null(job.File);

        var sales = await fixture.SalesAsync();
        Assert.Equal(rows, sales.Count);
        Assert.All(sales, sale => Assert.Equal(NayaxSalesHistoryImportFixture.BusinessA, sale.BusinessId));
        Assert.All(sales, sale => Assert.NotNull(sale.ProductId));
    }

    /// <summary>
    /// The cost replay every batch made necessary is recorded once per product, from that product's
    /// <em>earliest</em> affected sale - not once per batch (issue #1304's criterion), and not run
    /// inside the job.
    /// </summary>
    [Fact]
    public async Task One_pending_cost_rebuild_per_product_is_recorded_at_the_earliest_instant()
    {
        const int rows = (NayaxSalesHistoryPolicy.BatchRows * 2) + 1;

        await using var fixture = await NayaxSalesHistoryImportFixture.CreateAsync();
        var jobId = await fixture.QueueJobAsync(NayaxSalesHistoryImportFixture.Csv(rows));

        await using (var db = fixture.Context())
            await fixture.Process(db).Handle(jobId, CancellationToken.None);

        var pending = await fixture.PendingRebuildsAsync();

        // One row per product the history sold, however many batches touched it.
        Assert.Equal(NayaxSalesHistoryImportFixture.ProductCount, pending.Count);

        // The products are sold round-robin from the first row, so product n's earliest sale is the
        // (n-1)-th minute of the file.
        for (var index = 0; index < pending.Count; index++)
        {
            Assert.Equal(
                NayaxSalesHistoryImportFixture.FirstSale.AddMinutes(index),
                pending[index].FromInstantUtc);
        }
    }

    /// <summary>
    /// Running the same job again, and uploading the same file again, both leave exactly the state
    /// the first run left: a row is matched by its transaction id, so nothing is duplicated and the
    /// second pass updates the sales the first one created.
    /// </summary>
    [Fact]
    public async Task Re_running_a_job_and_re_uploading_the_file_create_no_duplicates()
    {
        const int rows = 10;
        var csv = NayaxSalesHistoryImportFixture.Csv(rows);

        await using var fixture = await NayaxSalesHistoryImportFixture.CreateAsync();
        var firstJob = await fixture.QueueJobAsync(csv);

        await using (var db = fixture.Context())
            await fixture.Process(db).Handle(firstJob, CancellationToken.None);

        var afterFirst = await fixture.SalesAsync();

        // The same job again: it is final, so there is nothing to claim and nothing happens.
        await using (var db = fixture.Context())
        {
            var again = await fixture.Process(db).Handle(firstJob, CancellationToken.None);
            Assert.Equal(NayaxSalesHistoryImportJobRunOutcome.NothingToDo, again.Outcome);
        }

        // The same file uploaded again - which is the operator's own way of re-running one - updates
        // every row instead of inserting it a second time.
        var secondJob = await fixture.QueueJobAsync(csv);
        await using (var db = fixture.Context())
        {
            var result = await fixture.Process(db).Handle(secondJob, CancellationToken.None);

            Assert.Equal(NayaxSalesHistoryImportJobRunOutcome.Finished, result.Outcome);
            Assert.Equal(0, result.Progress.Created);
            Assert.Equal(rows, result.Progress.Updated);
        }

        var afterSecond = await fixture.SalesAsync();
        Assert.Equal(rows, afterSecond.Count);
        Assert.Equal(
            afterFirst.Select(sale => sale.TransactionID),
            afterSecond.Select(sale => sale.TransactionID));
        Assert.Equal(
            afterFirst.Select(sale => sale.UnitCostAtSale),
            afterSecond.Select(sale => sale.UnitCostAtSale));
        Assert.Equal(
            afterFirst.Select(sale => sale.MachineAuthorizationTime),
            afterSecond.Select(sale => sale.MachineAuthorizationTime));
    }

    /// <summary>
    /// An attempt that fails part-way keeps the batches it had already committed, records how far it
    /// got, and the next attempt resumes from the row after them rather than starting again.
    /// </summary>
    [Fact]
    public async Task A_failed_attempt_keeps_its_committed_batches_and_the_next_one_resumes()
    {
        const int rows = (NayaxSalesHistoryPolicy.BatchRows * 2) + 7;

        await using var fixture = await NayaxSalesHistoryImportFixture.CreateAsync();
        var jobId = await fixture.QueueJobAsync(NayaxSalesHistoryImportFixture.Csv(rows));

        await using (var db = fixture.Context())
        {
            // The second batch's transaction never opens, so the first batch's rows are committed
            // and nothing of the second is.
            var failing = new FailingSalesHistoryTransactions(
                new EfNayaxSalesImportTransactions(db), failOnBatch: 2);

            var result = await fixture.Process(db, failing).Handle(jobId, CancellationToken.None);

            Assert.Equal(NayaxSalesHistoryImportJobRunOutcome.Requeued, result.Outcome);
        }

        var interrupted = await fixture.JobRowAsync(jobId);
        Assert.Equal(NayaxSalesHistoryImportJobState.Queued, interrupted.State);
        Assert.Equal(NayaxSalesHistoryPolicy.BatchRows, interrupted.RowsRead);
        Assert.Equal(NayaxSalesHistoryPolicy.BatchRows, interrupted.Created);
        Assert.Equal(1, interrupted.Attempts);
        Assert.NotNull(interrupted.LastError);

        // The bytes are kept, because the next attempt reads from them.
        Assert.NotNull(interrupted.File);
        Assert.Equal(NayaxSalesHistoryPolicy.BatchRows, (await fixture.SalesAsync()).Count);

        await using (var db = fixture.Context())
        {
            var observed = new ObservedSalesHistoryTransactions(new EfNayaxSalesImportTransactions(db));

            var result = await fixture.Process(db, observed).Handle(jobId, CancellationToken.None);

            Assert.Equal(NayaxSalesHistoryImportJobRunOutcome.Finished, result.Outcome);
            Assert.Equal(rows, result.Progress.RowsRead);

            // The resumed attempt applied only what was left: one full batch and the remainder.
            Assert.Equal(2, observed.Committed);
            Assert.Equal(rows - NayaxSalesHistoryPolicy.BatchRows, result.RowsProcessedNow);
        }

        var finished = await fixture.JobRowAsync(jobId);
        Assert.Equal(NayaxSalesHistoryImportJobState.Completed, finished.State);
        Assert.Equal(rows, finished.RowsRead);

        // Every row created exactly once: a resumed job neither skips a row nor re-applies one.
        Assert.Equal(rows, finished.Created);
        Assert.Equal(0, finished.Updated);
        Assert.Equal(rows, (await fixture.SalesAsync()).Count);
        Assert.Equal(2, finished.Attempts);
    }

    /// <summary>
    /// A job that has used every attempt it is allowed stops as Failed with a readable reason, and
    /// says what it did import: the batches it committed stay committed.
    /// </summary>
    [Fact]
    public async Task A_job_that_runs_out_of_attempts_fails_and_keeps_what_it_imported()
    {
        const int rows = NayaxSalesHistoryPolicy.BatchRows + 3;

        await using var fixture = await NayaxSalesHistoryImportFixture.CreateAsync();
        var jobId = await fixture.QueueJobAsync(NayaxSalesHistoryImportFixture.Csv(rows));

        for (var attempt = 1; attempt <= NayaxSalesHistoryPolicy.MaxAttempts; attempt++)
        {
            await using var db = fixture.Context();

            // The first attempt commits its first batch and fails on the second; the attempts after
            // it resume at that row and fail immediately, committing nothing at all.
            var failing = new FailingSalesHistoryTransactions(
                new EfNayaxSalesImportTransactions(db), failOnBatch: attempt == 1 ? 2 : 1);

            await fixture.Process(db, failing).Handle(jobId, CancellationToken.None);
        }

        var job = await fixture.JobRowAsync(jobId);
        Assert.Equal(NayaxSalesHistoryImportJobState.Failed, job.State);
        Assert.Equal(NayaxSalesHistoryPolicy.AttemptsExhaustedReason, job.LastError);
        Assert.Equal(NayaxSalesHistoryPolicy.MaxAttempts, job.Attempts);
        Assert.Equal(NayaxSalesHistoryImportFixture.RunAt, job.FinishedAtUtc);
        Assert.Null(job.File);

        // Only the first batch of the first attempt ever committed, and it is still there - which is
        // what the recorded progress says.
        Assert.Equal(NayaxSalesHistoryPolicy.BatchRows, job.RowsRead);
        Assert.Equal(NayaxSalesHistoryPolicy.BatchRows, (await fixture.SalesAsync()).Count);

        // And a further attempt on a stopped job does nothing at all.
        await using (var db = fixture.Context())
        {
            var result = await fixture.Process(db).Handle(jobId, CancellationToken.None);
            Assert.Equal(NayaxSalesHistoryImportJobRunOutcome.NothingToDo, result.Outcome);
        }
    }

    /// <summary>
    /// The rows the normal import skips, counts invalid or keeps a stored value for are counted here
    /// in exactly the same way, because they are applied by exactly the same rules.
    /// </summary>
    [Fact]
    public async Task The_row_rules_and_their_counts_are_the_uploaded_imports_own()
    {
        const string csv =
            "TransactionID,TransactionStatusId,MachineID,NayaxProductId,SettlementValue,PaymentMethod,ProductName,AuthorizationDateTimeGMT\n"
            // Applied: a completed sale of product 1.
            + "9000001,12,942488501,1,3.50,Credit Card,Fictional line 1,2026-06-01T00:00:00Z\n"
            // Skipped: no machine identifier, so the row has no identity the import will guess at.
            + "9000002,12,0,1,3.50,Credit Card,Fictional line 1,2026-06-01T00:01:00Z\n"
            // Invalid: an amount nobody can read is not a blank cell.
            + "9000003,12,942488501,1,$3.50,Credit Card,Fictional line 1,2026-06-01T00:02:00Z\n"
            // Skipped: a new sale with no amount is not a $0 sale.
            + "9000004,12,942488501,1,,Credit Card,Fictional line 1,2026-06-01T00:03:00Z\n"
            // Applied twice: the file's last word about a transaction is what the business holds, and
            // the second row's blank status keeps the stored one.
            + "9000005,12,942488501,1,4.00,Credit Card,Fictional line 1,2026-06-01T00:04:00Z\n"
            + "9000005,,942488501,1,5.00,Credit Card,Fictional line 1,2026-06-01T00:04:00Z\n";

        await using var fixture = await NayaxSalesHistoryImportFixture.CreateAsync();
        var jobId = await fixture.QueueJobAsync(csv);

        await using var db = fixture.Context();
        var result = await fixture.Process(db).Handle(jobId, CancellationToken.None);

        Assert.Equal(6, result.Progress.RowsRead);
        Assert.Equal(2, result.Progress.Created);
        Assert.Equal(1, result.Progress.Updated);
        Assert.Equal(2, result.Progress.Skipped);
        Assert.Equal(1, result.Progress.Invalid);
        Assert.Equal(1, result.Progress.KeptStoredValues);

        var sales = await fixture.SalesAsync();
        Assert.Equal([9000001L, 9000005L], sales.Select(sale => sale.TransactionID));

        var repeated = sales.Single(sale => sale.TransactionID == 9000005L);
        Assert.Equal(5.00m, repeated.SettlementValue);
        Assert.Equal(NayaxTransactionStatusIds.Completed, repeated.TransactionStatusId);
    }

    /// <summary>
    /// A history is a fact about one business: the job, its file and every row it writes belong to
    /// the business that uploaded it, and a job of another business is not even visible to the one
    /// that did not.
    /// </summary>
    [Fact]
    public async Task A_job_never_reads_or_writes_another_businesss_data()
    {
        await using var fixture = await NayaxSalesHistoryImportFixture.CreateAsync();

        // Both businesses hold the same remote transaction ids, which is ordinary: a Nayax
        // TransactionID is unique only inside the operator account that issued it.
        var csv = NayaxSalesHistoryImportFixture.Csv(6);

        var jobB = await fixture.QueueJobAsync(
            csv, NayaxSalesHistoryImportFixture.BusinessB, NayaxSalesHistoryImportFixture.UploadedAt);
        await using (var db = fixture.Context(NayaxSalesHistoryImportFixture.BusinessB))
            await fixture.Process(db).Handle(jobB, CancellationToken.None);

        var businessBSalesBefore = await fixture.SalesAsync(NayaxSalesHistoryImportFixture.BusinessB);
        Assert.Equal(6, businessBSalesBefore.Count);

        var jobA = await fixture.QueueJobAsync(csv, NayaxSalesHistoryImportFixture.BusinessA);
        await using (var db = fixture.Context(NayaxSalesHistoryImportFixture.BusinessA))
        {
            var result = await fixture.Process(db).Handle(jobA, CancellationToken.None);

            // Every row is a new sale of business A, even though business B already holds those
            // transaction ids.
            Assert.Equal(6, result.Progress.Created);
            Assert.Equal(0, result.Progress.Updated);
        }

        var businessASales = await fixture.SalesAsync(NayaxSalesHistoryImportFixture.BusinessA);
        Assert.Equal(6, businessASales.Count);
        Assert.All(businessASales, sale => Assert.Equal(NayaxSalesHistoryImportFixture.BusinessA, sale.BusinessId));

        // Business B's sales are exactly as they were: same rows, same costs, same instants.
        var businessBSalesAfter = await fixture.SalesAsync(NayaxSalesHistoryImportFixture.BusinessB);
        Assert.Equal(
            businessBSalesBefore.Select(sale => (sale.Id, sale.TransactionID, sale.UnitCostAtSale)),
            businessBSalesAfter.Select(sale => (sale.Id, sale.TransactionID, sale.UnitCostAtSale)));

        // Each product's pending rebuild belongs to the business whose product it is.
        var pendingA = await fixture.PendingRebuildsAsync(NayaxSalesHistoryImportFixture.BusinessA);
        var pendingB = await fixture.PendingRebuildsAsync(NayaxSalesHistoryImportFixture.BusinessB);
        Assert.NotEmpty(pendingA);
        Assert.NotEmpty(pendingB);
        Assert.Empty(pendingA.Select(row => row.ProductId).Intersect(pendingB.Select(row => row.ProductId)));

        // And one business cannot see the other's job at all - the status read is tenant-scoped
        // centrally, so there is no comparison to get wrong.
        await using (var db = fixture.Context(NayaxSalesHistoryImportFixture.BusinessA))
        {
            var jobs = await NayaxSalesHistoryImportFixture.Jobs(db).ListRecentAsync(10, CancellationToken.None);

            Assert.Equal([jobA], jobs.Select(job => job.Id));
        }

        await using (var db = fixture.Context(NayaxSalesHistoryImportFixture.BusinessB))
        {
            var jobs = await NayaxSalesHistoryImportFixture.Jobs(db).ListRecentAsync(10, CancellationToken.None);

            Assert.Equal([jobB], jobs.Select(job => job.Id));
        }
    }

    /// <summary>
    /// The worker's own discovery read: the oldest unfinished job across every business, as a job key
    /// and a business key and nothing else. It is the one cross-business statement this feature
    /// makes, and it is made in a scope that has deliberately resolved no business.
    /// </summary>
    [Fact]
    public async Task The_discovery_read_answers_the_oldest_unfinished_job_of_any_business()
    {
        await using var fixture = await NayaxSalesHistoryImportFixture.CreateAsync();

        var older = await fixture.QueueJobAsync(
            NayaxSalesHistoryImportFixture.Csv(2),
            NayaxSalesHistoryImportFixture.BusinessB,
            NayaxSalesHistoryImportFixture.UploadedAt.AddMinutes(-10));
        var newer = await fixture.QueueJobAsync(
            NayaxSalesHistoryImportFixture.Csv(2), NayaxSalesHistoryImportFixture.BusinessA);

        await using (var denied = fixture.DeniedContext())
        {
            var due = await NayaxSalesHistoryImportFixture.Queue(denied)
                .FindNextDueAsync(CancellationToken.None);

            Assert.NotNull(due);
            Assert.Equal(older, due!.Value.JobId);
            Assert.Equal(NayaxSalesHistoryImportFixture.BusinessB, due.Value.BusinessId);
        }

        // Finish it, and the next pass is offered the other business's job instead.
        await using (var db = fixture.Context(NayaxSalesHistoryImportFixture.BusinessB))
            await fixture.Process(db).Handle(older, CancellationToken.None);

        await using (var denied = fixture.DeniedContext())
        {
            var due = await NayaxSalesHistoryImportFixture.Queue(denied)
                .FindNextDueAsync(CancellationToken.None);

            Assert.Equal(newer, due!.Value.JobId);
            Assert.Equal(NayaxSalesHistoryImportFixture.BusinessA, due.Value.BusinessId);
        }

        // And when nothing is unfinished, nothing is offered.
        await using (var db = fixture.Context(NayaxSalesHistoryImportFixture.BusinessA))
            await fixture.Process(db).Handle(newer, CancellationToken.None);

        await using (var denied = fixture.DeniedContext())
        {
            Assert.Null(
                await NayaxSalesHistoryImportFixture.Queue(denied).FindNextDueAsync(CancellationToken.None));
        }
    }
}
