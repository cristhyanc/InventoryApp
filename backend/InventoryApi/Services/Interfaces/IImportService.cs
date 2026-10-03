namespace InventoryApi.Services.Interfaces;

public interface IImportService
{
    Task<NayaxSalesImportResult> ImportNayaxSalesFromExcelAsync(IFormFile file, CancellationToken cancellationToken = default);
}

public sealed record NayaxSalesImportResult(int Imported, int Updated, int Skipped);
