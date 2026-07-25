using InventoryApi.Models;

namespace InventoryApi.Services.Interfaces;

public interface IReceiptService
{
    Task<IEnumerable<Receipt>> GetAll(int? supplierId);
    Task<Receipt?> Get(int id);
    Task<(byte[]? Content, string? ContentType, string? FileName)> GetFile(int id);
    Task<Receipt?> Upload(IFormFile file, string title, string? notes, decimal? totalAmount, DateTime? purchaseDate, int? supplierId);
    Task<bool> Delete(int id);
}
