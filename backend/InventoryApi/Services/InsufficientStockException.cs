namespace InventoryApi.Services;

public sealed class InsufficientStockException : Exception
{
    public InsufficientStockException(int availableStock)
        : base($"Not enough products in stock. Available stock: {availableStock}")
    {
    }
}
