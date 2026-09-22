namespace Inventory.Domain.Purchases;

/// <summary>
/// A purchase line quantity/unit cost pair, as needed to validate an entered purchase total.
/// </summary>
public readonly record struct PurchaseTotalValidationItem(decimal Quantity, decimal UnitCost);

public readonly record struct PurchaseTotalValidationResult(
    bool HasMismatch,
    decimal? ItemSubtotal,
    decimal? CalculatedTotal,
    decimal? Difference);

/// <summary>
/// Compares an entered purchase total against its items, delivery cost, and package cost.
/// The one authoritative calculation for purchase total-mismatch data-quality warnings.
/// </summary>
public static class PurchaseTotalValidationPolicy
{
    public const decimal Tolerance = 0.02m;

    public static PurchaseTotalValidationResult Evaluate(
        decimal? totalAmount,
        decimal? deliveryCost,
        decimal? packageCost,
        IEnumerable<PurchaseTotalValidationItem> items)
    {
        if (!totalAmount.HasValue)
            return new PurchaseTotalValidationResult(false, null, null, null);

        var itemSubtotal = items.Sum(i => i.Quantity * i.UnitCost);
        var calculatedTotal = itemSubtotal + (deliveryCost ?? 0m) + (packageCost ?? 0m);
        var difference = Math.Abs(calculatedTotal - totalAmount.Value);
        var hasMismatch = difference > Tolerance;

        return new PurchaseTotalValidationResult(
            hasMismatch,
            itemSubtotal,
            calculatedTotal,
            hasMismatch ? difference : null);
    }
}
