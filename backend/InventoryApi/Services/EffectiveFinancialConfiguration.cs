using InventoryApi.Models;

namespace InventoryApi.Services;

public static class EffectiveFinancialConfiguration
{
    public static SiteCommissionAgreement? ResolveAgreement(
        IEnumerable<SiteCommissionAgreement> agreements,
        long siteId,
        DateTime effectiveAt)
    {
        var matches = agreements.Where(x =>
            x.SiteId == siteId &&
            x.EffectiveFrom.Date <= effectiveAt.Date &&
            (!x.EffectiveTo.HasValue || x.EffectiveTo.Value.Date >= effectiveAt.Date)).ToList();

        return matches.Count switch
        {
            0 => null,
            1 => matches[0],
            _ => throw new InvalidOperationException(
                $"Multiple site commission agreements cover site {siteId} on {effectiveAt:yyyy-MM-dd}.")
        };
    }

    public static NayaxProcessingFeeRate? ResolveNayaxFeeRate(
        IEnumerable<NayaxProcessingFeeRate> rates,
        DateTime effectiveAt) =>
        rates
            .Where(x => x.EffectiveFrom.Date <= effectiveAt.Date)
            .OrderByDescending(x => x.EffectiveFrom)
            .FirstOrDefault();
}
