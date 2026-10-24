using Inventory.Domain.Reporting.Dashboard;

namespace Inventory.Application.Reporting.Dashboard;

/// <summary>
/// The Dashboard "Inventory Value" tile use case: retrieves every business-owned product's
/// persisted inventory position through <see cref="IInventoryValuationFactsProvider"/> and applies
/// the Domain <see cref="InventoryValuationPolicy"/> to derive the authoritative total and its
/// completeness. The backend is authoritative for this calculation; callers (the API controller and
/// ultimately Angular) only map and display the result.
/// </summary>
public sealed class GetInventoryValuationSummary
{
    private readonly IInventoryValuationFactsProvider _facts;

    public GetInventoryValuationSummary(IInventoryValuationFactsProvider facts) => _facts = facts;

    public async Task<InventoryValuationSummaryDto> Handle(CancellationToken cancellationToken)
    {
        var facts = await _facts.GetProductValuationFactsAsync(cancellationToken);
        var result = InventoryValuationPolicy.Summarize(facts);

        return new InventoryValuationSummaryDto(
            result.TotalInventoryValue,
            result.IsComplete,
            result.ProductsWithUnknownCost,
            result.TotalProducts);
    }
}
