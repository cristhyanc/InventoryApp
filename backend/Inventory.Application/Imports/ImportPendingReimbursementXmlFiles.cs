namespace Inventory.Application.Imports;

/// <summary>
/// The pending reimbursement XML import use case (issue #299, child 1 of 3 of #151), moved
/// unchanged in behaviour out of <c>InventoryApi.Services.ImportService.ImportPendingXmlFilesAsync</c>:
/// for every file waiting in the pending queue, read and parse it through
/// <see cref="IPendingReimbursementXmlSource"/>, skip it when this business has already imported
/// the same bytes, otherwise persist its reimbursement graph, and remove it from the queue either
/// way.
///
/// Imported reimbursement data feeds financial reconciliation, so the counts this returns are a
/// reported fact, not a summary: a file that could not be read, parsed or removed is counted as
/// failed and stays visible instead of being silently dropped, and a file whose hash is already
/// present is counted as skipped rather than imported a second time.
///
/// <para>Since issue #1149 the per-file steps - hash dedupe, the atomic write, and the settlement
/// coverage a committed file proves - are <see cref="ImportReimbursementXmlFile"/>, shared with the
/// uploaded-archive import so the two paths cannot drift apart. Nothing about this use case's own
/// behaviour changed: the same counters move in the same order, a persistence failure still
/// propagates, and the pending folder is still the only place this import looks.</para>
/// </summary>
public sealed class ImportPendingReimbursementXmlFiles
{
    private readonly IPendingReimbursementXmlSource _source;
    private readonly ImportReimbursementXmlFile _importFile;

    public ImportPendingReimbursementXmlFiles(
        IPendingReimbursementXmlSource source,
        ImportReimbursementXmlFile importFile)
    {
        _source = source;
        _importFile = importFile;
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

            var outcome = await _importFile.Handle(file, cancellationToken);

            if (!outcome.WasImported)
            {
                // A duplicate is only skipped once it is out of the queue; a file that could
                // not be removed would otherwise be reported as skipped on every single run.
                if (_source.TryDiscard(fileName)) skippedFiles++;
                else failedFiles++;
                continue;
            }

            // The rows are persisted at this point, so they are counted here rather than with
            // the file: if removing the file then fails, the run reports the reimbursements it
            // genuinely imported alongside the failure, as the legacy implementation did.
            importedReimbursements += outcome.ImportedReimbursements;

            if (_source.TryDiscard(fileName)) importedFiles++;
            else failedFiles++;
        }

        return new ImportedFileImportResult(importedFiles, importedReimbursements, skippedFiles, failedFiles);
    }
}
