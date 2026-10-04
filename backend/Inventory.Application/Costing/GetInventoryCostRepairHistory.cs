using Inventory.Domain.Exceptions;

namespace Inventory.Application.Costing;

/// <summary>
/// One product's costing-repair history, newest first (issue #359).
///
/// Repairs are append-only and change historical COGS, so the history is the audit trail: what was
/// repaired, when it takes effect, at what cost, why, and who applied it. The product is required
/// to exist for the caller's business, and a product belonging to another business is reported the
/// same way as one that does not exist at all, so the query cannot be used to probe for them.
/// </summary>
public sealed class GetInventoryCostRepairHistory
{
    private readonly IInventoryCostRepairStore _store;

    public GetInventoryCostRepairHistory(IInventoryCostRepairStore store) => _store = store;

    public async Task<IReadOnlyList<InventoryCostRepairRecord>> Handle(
        long productId,
        CancellationToken cancellationToken = default)
    {
        _ = await _store.GetProductAsync(productId, cancellationToken)
            ?? throw new DomainValidationException($"Product {productId} does not exist.");

        return await _store.ListAsync(productId, cancellationToken);
    }
}
