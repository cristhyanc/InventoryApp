namespace Inventory.Application.Imports;

/// <summary>
/// What one pending-reimbursement-XML import run did, as
/// <c>POST api/imports/pending-xml</c> has always answered it (moved here from
/// <c>InventoryApi.Services.Interfaces</c> by issue #299 with no JSON change).
/// </summary>
/// <param name="ImportedFiles">Files parsed, persisted and removed from the pending folder.</param>
/// <param name="ImportedReimbursements">Reimbursement rows persisted across those files.</param>
/// <param name="SkippedFiles">Files whose content hash this business had already imported.</param>
/// <param name="FailedFiles">Files that could not be read, parsed or removed.</param>
public sealed record ImportedFileImportResult(
    int ImportedFiles,
    int ImportedReimbursements,
    int SkippedFiles,
    int FailedFiles);
