namespace Inventory.Application.Products;

/// <summary>One machine product listing row's price/cost facts needed to suggest net/price values.</summary>
public sealed record MachineProductPricingFact(long ProductId, decimal MachinePrice, decimal AverageUnitCost);

public sealed record MachineProductPricingResult(long ProductId, decimal? SuggestedNetValue, decimal? SuggestedPriceValue);
