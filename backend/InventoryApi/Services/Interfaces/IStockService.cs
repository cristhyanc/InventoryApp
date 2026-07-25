using InventoryApi.DTOs;
using InventoryApi.Models;

namespace InventoryApi.Services.Interfaces;

public interface IStockService
{
    Task<IEnumerable<StockAdjustment>> History(long productId);
    Task<StockAdjustment?> Adjust(long productId, StockAdjustmentDto dto);
}
