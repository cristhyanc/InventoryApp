namespace InventoryApi.Services.Interfaces;

public interface IImportedFileService
{
    Task<ImportedFileImportResult> ImportPendingFiles();
}

public sealed record ImportedFileImportResult(
    int ImportedFiles,
    int ImportedReimbursements,
    int SkippedFiles,
    int FailedFiles);
