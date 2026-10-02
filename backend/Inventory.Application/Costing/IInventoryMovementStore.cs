namespace Inventory.Application.Costing;

/// <summary>
/// Narrow persistence port for recording a stock movement (issue #296), owned by the Application
/// layer. Its implementation only loads the product's state and stages the already costed
/// movement in the caller's unit of work; it never saves, opens a transaction or costs anything,
/// so the caller keeps its existing transaction boundary around the movement and the rebuild it
/// triggers.
/// </summary>
public interface IInventoryMovementStore
{
    /// <summary><c>null</c> when the product does not exist (or is not owned by the caller's business).</summary>
    Task<InventoryMovementCostState?> GetCostStateAsync(long productId, CancellationToken cancellationToken);

    /// <summary>
    /// Stages the product's new physical stock and a new auditable stock movement row, without
    /// saving. The product must already have been loaded through <see cref="GetCostStateAsync"/>.
    /// </summary>
    IStagedInventoryMovement Stage(CostedInventoryMovement movement);
}
