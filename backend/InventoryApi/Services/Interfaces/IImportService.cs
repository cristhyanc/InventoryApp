using InventoryApi.DTOs;
using Microsoft.AspNetCore.Http;

namespace InventoryApi.Services.Interfaces;

public interface IImportService
{
    Task<bool> ImportProductsAsync();
    Task<NayaxSalesImportResult> ImportNayaxSalesFromExcelAsync(IFormFile file, CancellationToken cancellationToken = default);
    Task<ImportedFileImportResult> ImportPendingXmlFilesAsync();
}

public sealed record NayaxSalesImportResult(int Imported, int Updated, int Skipped);

public sealed record ImportedFileImportResult(
    int ImportedFiles,
    int ImportedReimbursements,
    int SkippedFiles,
    int FailedFiles);
