using Inventory.Application.Costing;
using InventoryApi.Adapters.Persistence;
using InventoryApi.Data;

namespace InventoryApi.Tests;

/// <summary>
/// Wires the Application costing use cases (issue #296) to their temporary API-owned EF adapters
/// over one <see cref="AppDbContext"/>, the way the production DI container does.
/// </summary>
internal static class TestCostingUseCases
{
    public static RebuildProductCost Rebuild(AppDbContext db) =>
        new(new EfInventoryCostLedgerStore(db));

    public static RecordInventoryMovement RecordMovement(AppDbContext db) =>
        new(new EfInventoryMovementStore(db));
}
