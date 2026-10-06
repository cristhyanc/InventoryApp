using Inventory.Application.Reporting.Dashboard;

namespace InventoryApi.Tests.Application.Reporting.Dashboard;

/// <summary>
/// In-memory fake of the inventory-valuation facts port, so the use case's composition with the
/// Domain policy can be tested without EF Core or SQLite.
/// </summary>
public sealed class FakeInventoryValuationFactsProvider : IInventoryValuationFactsProvider
{
    private readonly IReadOnlyList<decimal?> _values;

    public FakeInventoryValuationFactsProvider(IReadOnlyList<decimal?> values) => _values = values;

    public Task<IReadOnlyList<decimal?>> GetProductInventoryValuesAsync(CancellationToken cancellationToken) =>
        Task.FromResult(_values);
}
