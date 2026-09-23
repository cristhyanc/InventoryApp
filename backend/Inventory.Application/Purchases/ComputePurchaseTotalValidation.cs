using Inventory.Domain.Purchases;

namespace Inventory.Application.Purchases;

/// <summary>
/// Use case wrapping <see cref="PurchaseTotalValidationPolicy"/> so InventoryApi calls one
/// authoritative implementation instead of duplicating the total-mismatch formula locally.
/// </summary>
public sealed class ComputePurchaseTotalValidation
{
    public PurchaseTotalValidationResult Handle(
        decimal? totalAmount,
        decimal? deliveryCost,
        decimal? packageCost,
        IEnumerable<PurchaseTotalValidationItem> items) =>
        PurchaseTotalValidationPolicy.Evaluate(totalAmount, deliveryCost, packageCost, items);
}
