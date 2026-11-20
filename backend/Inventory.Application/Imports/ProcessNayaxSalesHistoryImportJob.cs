using Inventory.Application.Costing;
using Inventory.Application.Nayax;
using Inventory.Application.Time;
using Inventory.Domain.Imports;
using Inventory.Domain.Reporting.ProductMatching;

namespace Inventory.Application.Imports;

/// <summary>What one attempt on one sales history job did (issue #1304).</summary>
public enum NayaxSalesHistoryImportJobRunOutcome
{
    /// <summary>Every row of the file has been applied and the job is Completed.</summary>
    Finished,

    /// <summary>
    /// The attempt ended on a database failure and the job is queued again. Every batch it had
    /// committed stays committed, and the next attempt resumes from the row after them.
    /// </summary>
    Requeued,

    /// <summary>
    /// The job stopped for good - its attempts ran out, or its stored file was gone. Whatever it had
    /// imported stays imported, and the progress says how much that was.
    /// </summary>
    Stopped,

    /// <summary>There was nothing to do: the job had already reached a final state.</summary>
    NothingToDo,
}

/// <summary>What one attempt did, with the counts a log line reports (issue #1304).</summary>
/// <param name="Outcome">What happened to the job.</param>
/// <param name="Progress">
/// How far the job has got in total, which for a resumed job includes the rows a previous attempt
/// committed. Zero everywhere for an attempt that could not start.
/// </param>
/// <param name="RowsProcessedNow">How many rows this attempt itself applied.</param>
/// <param name="BatchesCommittedNow">How many batches this attempt itself committed.</param>
public sealed record NayaxSalesHistoryImportJobRunResult(
    NayaxSalesHistoryImportJobRunOutcome Outcome,
    NayaxSalesHistoryImportProgress Progress,
    int RowsProcessedNow,
    int BatchesCommittedNow);

/// <summary>
/// Imports one uploaded Nayax sales history (issue #1304), in a service scope already resolved to
/// the business the job belongs to.
///
/// <para><strong>One batch is the unit of work.</strong> The file is streamed row by row and applied
/// <c>NayaxSalesHistoryPolicy.BatchRows</c> rows at a time; each batch looks up its own stored sales,
/// applies the shared row rules, records the cost replays it made necessary and writes the job's
/// progress - all in <em>one</em> <c>BEGIN IMMEDIATE</c> transaction, and nothing else is inside it.
/// So there is no moment at which sales are committed without the record of how far the job got, or
/// progress recorded for sales that were not committed; and between batches the write lock is
/// released, which is what lets the Nayax SQS queue reader and an ordinary user save commit while a
/// year of history is importing.</para>
///
/// <para><strong>The row rules are the uploaded import's, through the same code.</strong>
/// <see cref="NayaxSalesRowApplier"/> is what applies each batch, so the identity facts a row must
/// carry, the instant precedence, issue #1298's blank-cell rules, the product resolution of issues
/// #745 and #758, the conflicting-sale exception and the sale costing are identical to
/// <see cref="ImportNayaxSales"/>'s by construction. This use case adds no row rule of its own and
/// is not allowed to: the issue puts new row rules explicitly out of scope.</para>
///
/// <para><strong>It is safe to stop and run again.</strong> A sale is matched by its transaction id,
/// so a row applied twice updates the sale it created rather than storing a second one - which is
/// what makes a resumed attempt, a re-run and a re-upload of the same file all end in the same
/// state. The resume is not merely safe but cheap: the claim says how many rows are already
/// committed and the streamed read passes over exactly those.</para>
///
/// <para><strong>Cost replays are recorded, not run here.</strong> Each batch stages one pending
/// inventory-cost rebuild per product its sales affected, from that product's earliest affected
/// instant, through the one recorder every other sales writer uses - so a product touched by eighty
/// batches ends with <em>one</em> pending row at the earliest instant of all of them, and that row
/// is replayed by the existing drain (the request-driven sync, the hourly reconciliation, the queue
/// reader). Replaying inside the job would mean replaying the same product's whole history once per
/// batch, which is the opposite of what the batching is for.</para>
///
/// <para><strong>Nothing here decides which job to do, or when.</strong> That is
/// <see cref="NayaxSalesHistoryImportJobRunner"/>'s, over
/// <see cref="INayaxSalesHistoryImportJobQueue"/>'s one cross-business read.</para>
/// </summary>
public sealed class ProcessNayaxSalesHistoryImportJob
{
    /// <summary>The safe reason a job whose stored file is no longer there records.</summary>
    public const string FileMissingReason =
        "The uploaded CSV is no longer available. Upload it again.";

    private readonly INayaxSalesHistoryImportJobStore _jobs;
    private readonly INayaxSalesHistoryCsvReader _reader;
    private readonly INayaxSalesHistoryBatchCosting _costing;
    private readonly INayaxSalesImportStore _store;
    private readonly INayaxProductLinkStore _productLinks;
    private readonly IPendingInventoryCostRebuildStore _rebuilds;
    private readonly INayaxSalesImportTransactions _transactions;
    private readonly IClock _clock;

    public ProcessNayaxSalesHistoryImportJob(
        INayaxSalesHistoryImportJobStore jobs,
        INayaxSalesHistoryCsvReader reader,
        INayaxSalesImportStore store,
        INayaxSalesHistoryBatchCosting costing,
        INayaxProductLinkStore productLinks,
        IPendingInventoryCostRebuildStore rebuilds,
        INayaxSalesImportTransactions transactions,
        IClock clock)
    {
        _jobs = jobs;
        _reader = reader;
        _costing = costing;
        _store = store;
        _productLinks = productLinks;
        _rebuilds = rebuilds;
        _transactions = transactions;
        _clock = clock;
    }

    /// <summary>
    /// Claims the job and works through whatever is left of it.
    /// </summary>
    public async Task<NayaxSalesHistoryImportJobRunResult> Handle(
        int jobId, CancellationToken cancellationToken)
    {
        var claimed = await _jobs.ClaimAsync(
            jobId, NayaxSalesHistoryPolicy.MaxAttempts, cancellationToken);

        switch (claimed.State)
        {
            case NayaxSalesHistoryImportJobClaimState.AttemptsExhausted:
                await _jobs.FailAsync(
                    jobId,
                    NayaxSalesHistoryPolicy.AttemptsExhaustedReason,
                    _clock.UtcNow,
                    cancellationToken);
                return Stopped();

            case NayaxSalesHistoryImportJobClaimState.FileMissing:
                await _jobs.FailAsync(jobId, FileMissingReason, _clock.UtcNow, cancellationToken);
                return Stopped();

            case NayaxSalesHistoryImportJobClaimState.Claimed:
                break;

            default:
                return new NayaxSalesHistoryImportJobRunResult(
                    NayaxSalesHistoryImportJobRunOutcome.NothingToDo, default, 0, 0);
        }

        var claim = claimed.Claim!;

        // Read once for the whole attempt, outside every batch's transaction: the catalogue a row's
        // product name is matched against, and the sales whose two candidate products disagreed when
        // issue #758 resolved the existing history. Neither changes while one job runs, and reading
        // them per batch would be one read of the whole catalogue every
        // NayaxSalesHistoryPolicy.BatchRows rows - hundreds of them for a file at the row ceiling.
        var candidates = await _store.GetProductCandidatesAsync(cancellationToken);
        var conflicting = (await _store.GetConflictingTransactionIdsAsync(cancellationToken)).ToHashSet();

        var progress = claim.Progress;
        var rowsNow = 0;
        var batchesNow = 0;

        using var content = new MemoryStream(claim.File, writable: false);
        foreach (var batch in _reader
                     .ReadRows(content, claim.Progress.RowsRead)
                     .Chunk(NayaxSalesHistoryPolicy.BatchRows))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                progress = await ApplyBatchAsync(
                    claim.JobId, batch, candidates, conflicting, progress, cancellationToken);
            }
            catch (Exception exception) when (NotCancellation(exception, cancellationToken))
            {
                // The batch's transaction rolled back and discarded what it had staged, so this
                // batch has neither sales nor progress - in the database or in the unit of work this
                // outcome is about to be recorded through - and every batch before it is committed.
                // What reaches here is the database - the
                // writer was busy, the connection dropped, a constraint the application does not
                // model refused the write - or a cost replay that could not be staged; either way the
                // attempt ends and the job is retried from the rows that did commit.
                return await StopOrRequeueAsync(claim, exception, cancellationToken);
            }

            rowsNow += batch.Length;
            batchesNow++;
        }

        await _jobs.CompleteAsync(claim.JobId, _clock.UtcNow, cancellationToken);

        return new NayaxSalesHistoryImportJobRunResult(
            NayaxSalesHistoryImportJobRunOutcome.Finished, progress, rowsNow, batchesNow);
    }

    /// <summary>
    /// One batch, in one write-locked transaction: the stored sales of the transactions this batch
    /// names, the merge and costing of each row, the pending cost rebuilds the batch made necessary,
    /// and the job's progress - committed together or not at all.
    ///
    /// <para>The product-id lookup is made <em>before</em> the transaction, deliberately: it is a
    /// read of this business's own products that nothing in the batch writes, so holding the write
    /// lock across it would add to the one number the batch size is chosen against.</para>
    /// </summary>
    private async Task<NayaxSalesHistoryImportProgress> ApplyBatchAsync(
        int jobId,
        NayaxSalesImportRow[] batch,
        IReadOnlyList<ProductMatchCandidate> candidates,
        IReadOnlySet<long> conflicting,
        NayaxSalesHistoryImportProgress progress,
        CancellationToken cancellationToken)
    {
        var facts = new NayaxSalesRowFacts(
            candidates,
            conflicting,
            await _productLinks.GetLocalProductIdsAsync(
                NayaxSalesRowApplier.NayaxProductIdsOf(batch), cancellationToken));

        // This batch's own affected products. One pending rebuild row per product is what the
        // recorder keeps - its instant only ever moves earlier - so a product this batch and eighty
        // others touched ends with one row at the earliest instant of all of them.
        var affected = new Dictionary<long, DateTime>();

        // The per-row rules, over the same store and the same sale costing the uploaded import is
        // given (issue #1304). Constructed rather than injected for the reason ImportNayaxSales
        // constructs its own: it is the rules, not a port, and both imports building it from the
        // same two collaborators is what makes "the same rules" structural. It is built per batch
        // because the costing is: its ledger reads are a snapshot that holds only while this
        // batch's pass has saved nothing (NayaxSalesHistoryBatchCosting), so one product's ledger
        // is read once for the batch rather than once for each of its rows.
        var rows = new NayaxSalesRowApplier(_store, _costing.ForOneBatch());

        NayaxSalesHistoryImportProgress applied;
        var transaction = await _transactions.BeginAsync(cancellationToken);
        await using (transaction)
        {
            try
            {
                applied = progress.Plus(
                    batch.Length, await rows.ApplyAsync(batch, facts, affected, cancellationToken));

                await _rebuilds.StageAsync(affected, cancellationToken);

                await _jobs.SaveProgressAsync(jobId, applied, cancellationToken);

                await transaction.CommitAsync(cancellationToken);
            }
            catch
            {
                // This batch is lost, and the rollback that follows undoes what the database was
                // told - but not what the unit of work is still holding. A row that failed to cost
                // leaves the sales applied before it staged; a failure at the commit leaves those
                // sales, the batch's pending cost rebuilds and the progress staged onto the job row.
                // The save that records the requeue or the failure next is outside any transaction,
                // so without this it would write the lost batch after all, and report a progress no
                // committed row supports. Discarding here is what makes the batch all or nothing in
                // the application's state too, and what makes the failure record re-read the job's
                // durable progress.
                transaction.DiscardStagedChanges();
                throw;
            }
        }

        // Committed, so there is nothing staged to lose: the sales this batch loaded and wrote are
        // forgotten, and the context stays the size of one batch however long the history is.
        _store.ForgetTrackedSales();

        return applied;
    }

    /// <summary>
    /// Ends the attempt: the job stops for good when it has used its last attempt, and is queued
    /// again otherwise. Either way the batches it committed stay committed and the progress row says
    /// how far it got, which is what the next attempt - or a re-upload - resumes from.
    ///
    /// <para>Both writes are made outside any transaction, on a unit of work the lost batch has
    /// already been discarded from (see <see cref="ApplyBatchAsync"/>), so each one loads the job's
    /// durable row and saves the outcome and nothing else.</para>
    /// </summary>
    private async Task<NayaxSalesHistoryImportJobRunResult> StopOrRequeueAsync(
        NayaxSalesHistoryImportJobClaim claim, Exception exception, CancellationToken cancellationToken)
    {
        if (claim.Attempts >= NayaxSalesHistoryPolicy.MaxAttempts)
        {
            await _jobs.FailAsync(
                claim.JobId,
                NayaxSalesHistoryPolicy.AttemptsExhaustedReason,
                _clock.UtcNow,
                cancellationToken);

            return Stopped();
        }

        // The type name, never the message: a database message can quote the parameter values of the
        // statement that failed, and the values in this transaction are a business's sales.
        await _jobs.RequeueAsync(claim.JobId, SafeReason(exception), cancellationToken);

        return new NayaxSalesHistoryImportJobRunResult(
            NayaxSalesHistoryImportJobRunOutcome.Requeued, default, 0, 0);
    }

    private static string SafeReason(Exception exception) =>
        $"The import stopped on a database failure ({exception.GetType().Name}). The rows already "
        + "imported are saved, and it will be retried.";

    private static NayaxSalesHistoryImportJobRunResult Stopped() =>
        new(NayaxSalesHistoryImportJobRunOutcome.Stopped, default, 0, 0);

    private static bool NotCancellation(Exception exception, CancellationToken cancellationToken) =>
        exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested;
}
