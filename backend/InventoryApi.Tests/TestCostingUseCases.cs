using Inventory.Application.Costing;
using InventoryApi.Adapters.Persistence;
using InventoryApi.Data;

namespace InventoryApi.Tests;

/// <summary>
/// Wires the Application costing use cases (issues #296 and #297) to their temporary API-owned EF adapters
/// over one <see cref="AppDbContext"/>, the way the production DI container does.
/// </summary>
internal static class TestCostingUseCases
{
    public static RebuildProductCost Rebuild(AppDbContext db) =>
        new(new EfInventoryCostLedgerStore(db));

    public static RecordInventoryMovement RecordMovement(AppDbContext db) =>
        new(new EfInventoryMovementStore(db));

    /// <summary>The sale-costing use case (issue #297), with the ledger rebuild over <paramref name="db"/> unless one is given.</summary>
    public static CostSale CostSale(AppDbContext db, IRebuildProductCost? rebuild = null) =>
        new(new EfSaleCostingStore(db), rebuild ?? Rebuild(db));

    public static CostPendingSales CostPendingSales(AppDbContext db, IRebuildProductCost? rebuild = null) =>
        new(new EfSaleCostingStore(db), CostSale(db, rebuild));

    public static BackfillSaleCosts BackfillSaleCosts(AppDbContext db, IRebuildProductCost? rebuild = null) =>
        new(new EfSaleCostingStore(db), CostSale(db, rebuild));

    public static BackfillNayaxHistoricalSaleCosts BackfillNayaxHistoricalSaleCosts(AppDbContext db) =>
        new(new EfSaleCostingStore(db));
}
