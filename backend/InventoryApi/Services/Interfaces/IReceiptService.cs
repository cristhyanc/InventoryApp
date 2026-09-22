using InventoryApi.DTOs;
using InventoryApi.Models;

namespace InventoryApi.Services.Interfaces;

public interface IPurchaseService
{
    Task<IEnumerable<Purchase>> GetAll(int? supplierId);
    Task<Purchase?> Get(int id);
    Task<(byte[]? Content, string? ContentType, string? FileName)> GetFile(int id);
    Task<Purchase?> Upload(IFormFile file, string title, string? notes, decimal? totalAmount, decimal? deliveryCost, decimal? packageCost, DateTime? purchaseDate, int? supplierId, IReadOnlyList<PurchaseItemDto>? items = null);
    Task<Purchase?> Update(int id, string? title, string? notes, decimal? totalAmount, decimal? deliveryCost, decimal? packageCost, DateTime? purchaseDate, int? supplierId, IReadOnlyList<PurchaseItemDto>? items = null);
    Task<bool> Delete(int id);
    PurchaseValidationDto? ComputeValidation(Purchase purchase);
}
