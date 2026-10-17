using Inventory.Application.Providers;
using Inventory.Application.Time;
using Inventory.Domain.Providers;
using Inventory.Domain.Reporting.Availability;

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

            // For the same reason, the periods this file accounts for are recorded here (issue
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
    /// Records the settlement periods a committed file accounted for (issue #730, parent #329
    /// decision P9) - <b>one coverage row per period the file proves, never one row spanning them
    /// all</b>.
    ///
    /// <para>Each reimbursement row declares its own settled period through
    /// <c>ReimbursementStartDate</c>/<c>ReimbursementEndDate</c>, which is the provider's own
    /// statement of what that row settles (AGENTS.md § Reimbursements and reconciliation: a
    /// reimbursement belongs to its coverage dates, not its payout or import date). Those declared
    /// intervals are the evidence, so they are recorded as they are: overlapping and exactly
    /// touching rows merge into one range through <see cref="ReportCoverageRange.Merge"/>, and a
    /// <b>gap between two rows is never bridged</b>. A file holding July and September settlements
    /// proves nothing whatsoever about August, and pooling the earliest start with the latest end
    /// would have claimed it - exactly the false completeness this record exists to prevent.</para>
    ///
    /// <para>Only a row carrying <b>both</b> of its own dates declares an interval; one carrying a
    /// start or an end alone declares none, and a period is never inferred from a payout date, an
    /// import instant or another row's dates. A file that declares nothing records nothing, and the
    /// affected report stays unverified instead of falsely complete. The end date is an inclusive
    /// calendar day, so the exclusive end of the coverage is the following midnight.</para>
    ///
    /// <para>The claim is business-wide because the row is: a reimbursement row is the operator
    /// account's own settlement statement for its period - it carries the account's company name,
    /// customer id and device counts, and every device it settles is nested beneath it - and the
    /// fee figures the reports show are summed from exactly these rows, for every device in them
    /// (<c>EfReportingSharedQueries.ImportedSummaryAsync</c>). Coverage therefore claims the same
    /// scope as the figures it governs, rather than a wider one. A machine-filtered report narrows
    /// which stored rows a figure counts and already says in a data-quality note that imported fees
    /// are account-level and unallocated; it never narrows what coverage was recorded.</para>
    /// </summary>
    private async Task RecordCoverageAsync(PendingReimbursementXmlFile file, CancellationToken cancellationToken)
    {
        var declared = file.Reimbursements
            .Where(row => row.ReimbursementStartDate.HasValue && row.ReimbursementEndDate.HasValue)
            .Select(row => new ReportCoverageRange(
                row.ReimbursementStartDate!.Value, row.ReimbursementEndDate!.Value.Date.AddDays(1)));

        foreach (var range in ReportCoverageRange.Merge(declared))
        {
            await _coverage.RecordAsync(
                ExternalProvider.Nayax,
                ProviderDataSource.SettlementImport,
                range.FromUtc,
                range.ToUtc,
                cancellationToken);
        }
    }
}
