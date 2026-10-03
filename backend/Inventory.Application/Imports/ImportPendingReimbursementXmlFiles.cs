using Inventory.Application.Time;

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

    public ImportPendingReimbursementXmlFiles(
        IPendingReimbursementXmlSource source,
        IImportedReimbursementStore store,
        IClock clock)
    {
        _source = source;
        _store = store;
        _clock = clock;
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

            if (_source.TryDiscard(fileName)) importedFiles++;
            else failedFiles++;
        }

        return new ImportedFileImportResult(importedFiles, importedReimbursements, skippedFiles, failedFiles);
    }
}
