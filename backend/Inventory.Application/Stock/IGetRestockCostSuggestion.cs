using Inventory.Domain.Stock;

namespace Inventory.Application.Stock;

/// <summary>
/// The authoritative restock-cost-suggestion use case's contract, so another feature slice (Take
/// Inventory, issue #245) can depend on it without depending on the concrete class - the same
/// precedent <c>Inventory.Application.Reporting.Bookkeeping.IGetBookkeepingReport</c> established
/// for a cross-slice use-case dependency.
/// </summary>
public interface IGetRestockCostSuggestion
{
    /// <summary><c>null</c> when the product does not exist (or is not owned by the caller's business).</summary>
    Task<RestockCostSuggestion?> Handle(long productId, CancellationToken cancellationToken);
}
