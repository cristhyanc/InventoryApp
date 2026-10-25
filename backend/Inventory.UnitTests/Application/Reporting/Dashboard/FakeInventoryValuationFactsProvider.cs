using Inventory.Application.Reporting.Dashboard;
using Inventory.Domain.Reporting.Dashboard;

namespace InventoryApi.Tests.Application.Reporting.Dashboard;

/// <summary>
/// In-memory fake of the inventory-valuation facts port, so the use case's composition with the
/// Domain policy can be tested without EF Core or SQLite. Which rows the real adapter reads to
/// resolve a product's inventory history (issue #861) is relational behaviour and is covered by
/// <c>EfInventoryValuationFactsProviderTests</c>.
/// </summary>
public sealed class FakeInventoryValuationFactsProvider : IInventoryValuationFactsProvider
{
    private readonly IReadOnlyList<ProductInventoryValuationFact> _facts;

    public FakeInventoryValuationFactsProvider(IReadOnlyList<ProductInventoryValuationFact> facts) =>
        _facts = facts;

    /// <summary>
    /// A catalogue of products that each hold stock of the given value - the shape every valuation
    /// test used before a product's history became part of the evidence.
    /// </summary>
    public static FakeInventoryValuationFactsProvider Stocked(params decimal?[] inventoryValues) =>
        new([.. inventoryValues.Select(value => Stocked(value))]);

    /// <summary>One product holding 6 units in storage, valued at <paramref name="inventoryValue"/>.</summary>
    public static ProductInventoryValuationFact Stocked(decimal? inventoryValue) =>
        new(inventoryValue, CostingQuantity: 6, QuantityInStock: 6, HasInventoryHistory: true);

    /// <summary>
    /// One product exactly as a create leaves it: no established valuation, no costing position, no
    /// storage units and nothing anywhere that records inventory having been in it.
    /// </summary>
    public static ProductInventoryValuationFact NeverStocked() =>
        new(InventoryValue: null, CostingQuantity: null, QuantityInStock: 0, HasInventoryHistory: false);

    public Task<IReadOnlyList<ProductInventoryValuationFact>> GetProductValuationFactsAsync(
        CancellationToken cancellationToken) => Task.FromResult(_facts);
}
