using Inventory.Application.Providers;
using Inventory.Application.Time;
using Inventory.Domain.Providers;

namespace Inventory.Application.Imports;

/// <summary>
/// The pending reimbursement XML import use case (issue #299, child 1 of 3 of #151), moved
/// unchanged in behaviour out of <c>InventoryApi.Services.ImportService.ImportPendingXmlFilesAsync</c>:
/// for every file waiting in the pending queue, read and parse it through
/// <see cref="IPendingReimbursementXmlSource"/>, skip it when this business has already imported
/// the same bytes, otherwise persist its reimbursement graph through
/// <see cref="IImportedReimbursementStore"/>, and remove it from the queue either way.
///
/// Imported reimbursement data feeds financial reconciliation, so the counts this returns are a
/// reported fact, not a summary: a file that could not be read, parsed or removed is counted as
/// failed and stays visible instead of being silently dropped, and a file whose hash is already
/// present is counted as skipped rather than imported a second time.
/// </summary>
public sealed class ImportPendingReimbursementXmlFiles
{
    private readonly IPendingReimbursementXmlSource _source;
    private readonly IImportedReimbursementStore _store;
    private readonly IClock _clock;
    private readonly IProviderDataCoverageStore _coverage;

    public ImportPendingReimbursementXmlFiles(
        IPendingReimbursementXmlSource source,
        IImportedReimbursementStore store,
        IClock clock,
        IProviderDataCoverageStore coverage)
    {
        _source = source;
        _store = store;
        _clock = clock;
        _coverage = coverage;
    }

    /// <summary>
    /// Imports every pending file and reports what happened to each of them.
    /// </summary>
    /// <remarks>
    /// A persistence failure is deliberately not caught: it propagates, exactly as before, so a
    /// database problem cannot be reported as a handful of "failed files" while the file that
    /// caused it has already been deleted.
    /// </remarks>
    public async Task<ImportedFileImportResult> Handle(CancellationToken cancellationToken)
    {
        var importedFiles = 0;
        var importedReimbursements = 0;
        var skippedFiles = 0;
        var failedFiles = 0;

        foreach (var fileName in _source.ListPendingFiles())
        {
            var file = await _source.ReadAsync(fileName, cancellationToken);
            if (file is null)
            {
                failedFiles++;
                continue;
            }

            if (await _store.HasFileWithContentHashAsync(file.ContentHash, cancellationToken))
            {
                // A duplicate is only skipped once it is out of the queue; a file that could
                // not be removed would otherwise be reported as skipped on every single run.
                if (_source.TryDiscard(fileName)) skippedFiles++;
                else failedFiles++;
                continue;
            }

            await _store.ImportAsync(file, _clock.UtcNow, cancellationToken);

            // The rows are persisted at this point, so they are counted here rather than with
            // the file: if removing the file then fails, the run reports the reimbursements it
            // genuinely imported alongside the failure, as the legacy implementation did.
            importedReimbursements += file.Reimbursements.Count;

            // For the same reason, the coverage this file accounts for is recorded here (issue
            // #730): the whole file is committed at this point, which is the one circumstance a
            // coverage record may be written in. A file that could not be read or parsed is counted
            // as failed above and never reaches this line, and a duplicate is skipped before it -
            // its coverage was recorded by the import that first committed those same bytes.
            await RecordCoverageAsync(file, cancellationToken);

            if (_source.TryDiscard(fileName)) importedFiles++;
            else failedFiles++;
        }

        return new ImportedFileImportResult(importedFiles, importedReimbursements, skippedFiles, failedFiles);
    }

    /// <summary>
    /// Records the settlement period a committed file accounted for (issue #730, parent #329
    /// decision P9): from the earliest <c>ReimbursementStartDate</c> it carries to the end of the
    /// last day of its latest <c>ReimbursementEndDate</c>.
    ///
    /// <para>Those two dates are the provider's own statement of the period the file settles
    /// (AGENTS.md § Reimbursements and reconciliation: a reimbursement belongs to its coverage
    /// dates, not its payout or import date), so unlike a transaction export this file really does
    /// declare its range, and the range it declares is what is recorded. The end date is an
    /// inclusive calendar day, so the exclusive end of the coverage is the following midnight.</para>
    ///
    /// <para>A file whose rows carry neither date declares no period, and a period cannot be
    /// inferred from a payout date or an import instant, so nothing is recorded for it and the
    /// affected report stays unverified instead of falsely complete.</para>
    /// </summary>
    private async Task RecordCoverageAsync(PendingReimbursementXmlFile file, CancellationToken cancellationToken)
    {
        var starts = file.Reimbursements.Where(row => row.ReimbursementStartDate.HasValue)
            .Select(row => row.ReimbursementStartDate!.Value).ToList();
        var ends = file.Reimbursements.Where(row => row.ReimbursementEndDate.HasValue)
            .Select(row => row.ReimbursementEndDate!.Value).ToList();
        if (starts.Count == 0 || ends.Count == 0)
            return;

        await _coverage.RecordAsync(
            ExternalProvider.Nayax,
            ProviderDataSource.SettlementImport,
            starts.Min(),
            ends.Max().Date.AddDays(1),
            cancellationToken);
    }
}
