using Inventory.Application.Sites;
using Inventory.Application.Time;
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
    private readonly IBusinessCalendar _businessCalendar;

    public ResolveMachineProductPricing(ISiteFactsStore facts, IBusinessCalendar businessCalendar)
    {
        _facts = facts;
        _businessCalendar = businessCalendar;
    }

    public async Task<IReadOnlyList<MachineProductPricingResult>> Handle(
        long? siteId, IReadOnlyList<MachineProductPricingFact> facts, CancellationToken cancellationToken)
    {
        if (siteId is null || facts.Count == 0)
            return facts.Select(fact => new MachineProductPricingResult(fact.ProductId, null, null)).ToList();

        // The effective-dated commission and Nayax fee configuration is selected by the Australia/Sydney
        // business date (issue #310), not the host's local date: on a UTC host the two differ for ten to
        // eleven hours of every day, which would price a slot with the previous day's configuration on
        // the day a new rate takes effect.
        var today = _businessCalendar.Today;
        var candidatePrices = facts.Select(fact => fact.MachinePrice).Append(1m).Distinct().ToList();

        // Awaited one at a time, deliberately not with Task.WhenAll: both calls land on the same
        // scoped ISiteFactsStore, whose EF adapter shares one AppDbContext, and a DbContext supports
        // only one operation at a time. SQLite's synchronous async implementation would usually hide
        // the overlap locally while failing against a real asynchronous provider.
        var commission = await _facts.ResolveCardCommissionAsync(siteId.Value, today, candidatePrices, cancellationToken);
        var feeExGst = await _facts.ResolveEffectiveFeeExGstAsync(today, cancellationToken);
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
