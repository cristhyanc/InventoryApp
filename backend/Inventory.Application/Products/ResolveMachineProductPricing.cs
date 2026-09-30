using Inventory.Application.Sites;
using Inventory.Domain.Products;
using Inventory.Domain.Reporting;

namespace Inventory.Application.Products;

/// <summary>
/// Resolves each machine product listing row's suggested net/price values, reusing the site
/// dashboard's commission/fee resolution port (<see cref="ISiteFactsStore"/>) rather than a second
/// copy of the <c>EffectiveFinancialConfiguration</c>/<c>SiteCommissionCalculator</c> lookup. Mirrors
/// the former <c>InventoryApi.Services.MachineService.GetMachineProducts</c> pricing block exactly
/// (issue #240); <see cref="Handle"/> returns results in the same order as its input facts, since
/// more than one machine slot can list the same product id with a different machine price.
/// </summary>
public sealed class ResolveMachineProductPricing
{
    private readonly ISiteFactsStore _facts;

    public ResolveMachineProductPricing(ISiteFactsStore facts)
    {
        _facts = facts;
    }

    public async Task<IReadOnlyList<MachineProductPricingResult>> Handle(
        long? siteId, IReadOnlyList<MachineProductPricingFact> facts, CancellationToken cancellationToken)
    {
        if (siteId is null || facts.Count == 0)
            return facts.Select(fact => new MachineProductPricingResult(fact.ProductId, null, null)).ToList();

        var today = DateTime.Today;
        var candidatePrices = facts.Select(fact => fact.MachinePrice).Append(1m).Distinct().ToList();

        var commissionTask = _facts.ResolveCardCommissionAsync(siteId.Value, today, candidatePrices, cancellationToken);
        var feeExGstTask = _facts.ResolveEffectiveFeeExGstAsync(today, cancellationToken);
        await Task.WhenAll(commissionTask, feeExGstTask);
        var commission = await commissionTask;
        var feeExGst = await feeExGstTask;
        var feeIncGst = feeExGst.HasValue
            ? feeExGst.Value + ReportingCalculations.GstFromExcluding(feeExGst.Value)
            : (decimal?)null;

        return facts.Select(fact =>
        {
            var commissionAmount = commission.ConfigurationUnavailable
                ? 0m
                : commission.CommissionAmountByRetailPrice.GetValueOrDefault(fact.MachinePrice);
            var commissionPerDollar = commission.ConfigurationUnavailable
                ? 0m
                : commission.CommissionAmountByRetailPrice.GetValueOrDefault(1m);

            var (suggestedNetValue, suggestedPriceValue) = MachineProductPricingPolicy.Calculate(
                fact.MachinePrice,
                fact.AverageUnitCost,
                fact.AverageUnitCost > 0m,
                commissionAmount,
                commissionPerDollar,
                feeIncGst,
                commission.ConfigurationUnavailable);

            return new MachineProductPricingResult(fact.ProductId, suggestedNetValue, suggestedPriceValue);
        }).ToList();
    }
}
