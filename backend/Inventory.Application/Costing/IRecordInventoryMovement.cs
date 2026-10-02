namespace Inventory.Application.Costing;

/// <summary>
/// The authoritative movement-recording use case's contract (issue #296), replacing the former
/// <c>InventoryApi.Services.Interfaces.IInventoryCostService</c>, so the stock, Take Inventory and
/// machine-stock adapters can depend on it rather than on a concrete class.
/// </summary>
public interface IRecordInventoryMovement
{
    /// <summary>
    /// Costs the movement through <see cref="Inventory.Domain.Costing.StockMovementCostPolicy"/>
    /// and stages it, without saving.
    /// </summary>
    /// <exception cref="InvalidOperationException">The product was not found, or the purchase unit cost is negative.</exception>
    /// <exception cref="Inventory.Domain.Exceptions.InsufficientStockException">The movement would make physical stock negative.</exception>
    Task<IStagedInventoryMovement> RecordAsync(InventoryMovement movement, CancellationToken cancellationToken);
}
