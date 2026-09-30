namespace Inventory.Domain.Products;

/// <summary>
/// A machine product listing row's suggested net proceeds and break-even suggested retail price,
/// given already-resolved card commission/fee facts. Mirrors the former
/// <c>InventoryApi.Services.MachineService.GetMachineProducts</c> pricing block exactly (issue #240).
/// Both suggestions require a known cost basis, a resolvable commission configuration, and a known
/// Nayax processing fee rate; otherwise there is nothing authoritative to suggest.
/// </summary>
public static class MachineProductPricingPolicy
{
    public static (decimal? SuggestedNetValue, decimal? SuggestedPriceValue) Calculate(
        decimal machinePrice,
        decimal averageUnitCost,
        bool hasCostBasis,
        decimal commissionAmount,
        decimal commissionPerDollar,
        decimal? feeIncGst,
        bool commissionConfigurationUnavailable)
    {
        if (feeIncGst is null || commissionConfigurationUnavailable || !hasCostBasis)
            return (null, null);

        var suggestedNetValue = machinePrice - averageUnitCost - commissionAmount - feeIncGst.Value;

        var denominator = 0.5m - commissionPerDollar;
        decimal? suggestedPriceValue = denominator > 0m
            ? (averageUnitCost + feeIncGst.Value) / denominator
            : null;

        return (suggestedNetValue, suggestedPriceValue);
    }
}
