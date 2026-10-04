using Inventory.Application.Costing;
using Inventory.Application.Nayax;
using Inventory.Application.Tenancy;
using Inventory.Application.Time;
using Inventory.Infrastructure.Clock;
using InventoryApi.Adapters.Persistence;
using InventoryApi.Data;

namespace InventoryApi.Tests;

/// <summary>
/// Wires the Application costing use cases (issues #296, #297 and #298) to their temporary API-owned EF adapters
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

    /// <summary>The inventory-cost transition use cases (issue #298), on the system clock unless one is given.</summary>
    public static PreviewInventoryCostTransition PreviewTransition(AppDbContext db, INayaxLynxClient nayax, IClock? clock = null) =>
        new(new EfInventoryCostTransitionStore(db), nayax, clock ?? new SystemClock());

    public static ApplyInventoryCostTransition ApplyTransition(
        AppDbContext db, INayaxLynxClient nayax, IRebuildProductCost? rebuild = null, IClock? clock = null) =>
        new(new EfInventoryCostTransitionStore(db), nayax, rebuild ?? Rebuild(db), clock ?? new SystemClock());

    /// <summary>The costing-repair use cases (issue #359), on the system clock unless one is given.</summary>
    public static PreviewInventoryCostRepair PreviewRepair(AppDbContext db) =>
        new(new EfInventoryCostRepairStore(db), new EfInventoryCostLedgerStore(db));

    public static ApplyInventoryCostRepair ApplyRepair(
        AppDbContext db,
        IAuthenticatedActorAccessor actors,
        IRebuildProductCost? rebuild = null,
        IClock? clock = null) =>
        new(
            new EfInventoryCostRepairStore(db),
            new EfInventoryCostLedgerStore(db),
            rebuild ?? Rebuild(db),
            actors,
            clock ?? new SystemClock());

    public static GetInventoryCostRepairHistory RepairHistory(AppDbContext db) =>
        new(new EfInventoryCostRepairStore(db));

    public static PreviewAllInventoryCostTransitions PreviewAllTransitions(AppDbContext db, INayaxLynxClient nayax, IClock? clock = null) =>
        new(new EfInventoryCostTransitionStore(db), nayax, clock ?? new SystemClock());

    public static ApplyAllInventoryCostTransitions ApplyAllTransitions(
        AppDbContext db, INayaxLynxClient nayax, IRebuildProductCost? rebuild = null, IClock? clock = null) =>
        new(new EfInventoryCostTransitionStore(db), nayax, rebuild ?? Rebuild(db), clock ?? new SystemClock());
}
