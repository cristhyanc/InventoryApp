using Inventory.Application.Imports;
using Inventory.Domain.Imports;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Application.Imports;

/// <summary>
/// Issue #1304's required concurrency case, over a real SQLite <strong>file</strong> with two
/// connections - which is the only way two writers and SQLite's own locking exist at all.
///
/// <para><strong>What these are about.</strong> The whole point of importing a long history in
/// batches is that the database's single write lock is released between them. A year of history is
/// dozens of batches; if the lock were held across them, every other write in the application - a
/// user's save, the Nayax SQS queue reader's own save - would wait on it and then fail after its
/// 30-second busy timeout, which is exactly the failure issue #1300's 5,000-row ceiling exists to
/// prevent for the synchronous upload.</para>
/// </summary>
public class NayaxSalesHistoryConcurrencyTests
{
    /// <summary>
    /// <strong>The required case.</strong> A write made by another connection while the job is
    /// running - between two of its batches - succeeds, and the job carries on and finishes. The
    /// interleaving is forced rather than hoped for: the other writer's save runs at the moment the
    /// job is about to begin its next batch's transaction.
    /// </summary>
    [Fact]
    public async Task A_save_made_between_batches_succeeds_while_the_job_runs()
    {
        const int rows = (NayaxSalesHistoryPolicy.BatchRows * 2) + 1;

        await using var fixture = await NayaxSalesHistoryImportFixture.OnDiskAsync();
        var jobId = await fixture.QueueJobAsync(NayaxSalesHistoryImportFixture.Csv(rows));

        await using var importing = fixture.Context();
        await using var other = fixture.SecondWriter();

        var saves = 0;
        var observed = new ObservedSalesHistoryTransactions(
            new EfNayaxSalesImportTransactions(importing),
            beforeBatch: async committed =>
            {
                // Between batches: the batches before this one are committed and the next one has
                // not taken the lock yet. The first call happens before any batch has run, which is
                // not "between" anything, so only the later ones are the case under test.
                if (committed == 0) return;

                await WriteOneAdjustmentAsync(other);
                saves++;
            });

        var result = await fixture.Process(importing, observed).Handle(jobId, CancellationToken.None);

        Assert.Equal(NayaxSalesHistoryImportJobRunOutcome.Finished, result.Outcome);
        Assert.Equal(rows, result.Progress.Created);

        // Two interleavings actually happened - before batch 2 and before batch 3.
        Assert.Equal(2, saves);
        Assert.Equal(3, observed.Committed);

        // Both writers' rows are there: the history's sales and the other connection's saves.
        Assert.Equal(2, await DamagedAdjustmentsAsync(fixture));
        Assert.Equal(rows, (await fixture.SalesAsync()).Count);
    }

    /// <summary>
    /// The other half of the same guarantee: a batch takes <c>BEGIN IMMEDIATE</c>, so a save
    /// attempted <em>while</em> it holds the lock waits for that batch rather than failing - and then
    /// succeeds. Together with the case above, that is "the lock is held for one batch and released
    /// between them".
    /// </summary>
    [Fact]
    public async Task A_save_attempted_while_a_batch_holds_the_lock_waits_for_it()
    {
        await using var fixture = await NayaxSalesHistoryImportFixture.OnDiskAsync();
        var jobId = await fixture.QueueJobAsync(NayaxSalesHistoryImportFixture.Csv(4));

        await using var importing = fixture.Context();
        await using var other = fixture.SecondWriter();

        Task? attempted = null;
        var observed = new ObservedSalesHistoryTransactions(
            new EfNayaxSalesImportTransactions(importing),
            // Started inside the batch's own transaction, on another thread, so it is attempted
            // against a write lock that is actually held.
            afterBegin: () => attempted ??= Task.Run(() => WriteOneAdjustmentAsync(other)));

        var result = await fixture.Process(importing, observed).Handle(jobId, CancellationToken.None);

        Assert.Equal(NayaxSalesHistoryImportJobRunOutcome.Finished, result.Outcome);
        Assert.Equal(1, observed.Committed);

        // It waited for the batch instead of failing on a busy database.
        Assert.NotNull(attempted);
        await attempted!;

        Assert.Equal(1, await DamagedAdjustmentsAsync(fixture));
        Assert.Equal(4, (await fixture.SalesAsync()).Count);
    }

    /// <summary>
    /// One ordinary tenant-owned write on the other connection - the shape of a user's own save, in
    /// its own <c>BEGIN IMMEDIATE</c> transaction, which is how every write in the application takes
    /// the lock.
    /// </summary>
    private static async Task WriteOneAdjustmentAsync(AppDbContext db)
    {
        // The same BEGIN IMMEDIATE every write in the application takes, through the one port that
        // is reachable from here.
        await using var transaction =
            await new EfNayaxSalesImportTransactions(db).BeginAsync(CancellationToken.None);

        var productId = await db.Products.OrderBy(product => product.Id).Select(product => product.Id).FirstAsync();

        db.StockAdjustments.Add(new StockAdjustment
        {
            ProductId = productId,
            QuantityChange = -1,
            QuantityAfter = 0,
            Reason = StockAdjustmentReason.Damaged,
            EffectiveAt = NayaxSalesHistoryImportFixture.RunAt,
        });

        await db.SaveChangesAsync();
        await transaction.CommitAsync(CancellationToken.None);
    }

    /// <summary>How many of the other writer's saves actually landed.</summary>
    private static async Task<int> DamagedAdjustmentsAsync(NayaxSalesHistoryImportFixture fixture)
    {
        await using var read = fixture.Unrestricted();

        return await read.StockAdjustments
            .IgnoreQueryFilters()
            .CountAsync(row => row.Reason == StockAdjustmentReason.Damaged);
    }
}
