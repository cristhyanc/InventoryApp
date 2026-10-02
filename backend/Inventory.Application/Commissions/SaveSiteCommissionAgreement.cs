using Inventory.Domain.Exceptions;
using Inventory.Domain.FinancialConfiguration;

namespace Inventory.Application.Commissions;

public sealed record SaveSiteCommissionAgreementInput(
    long SiteId,
    DateTime EffectiveFrom,
    DateTime? EffectiveTo,
    decimal CommissionRate,
    CommissionFrequency Frequency,
    CommissionBasis Basis,
    int? PaymentDueDaysAfterPeriodEnd);

public sealed class SaveSiteCommissionAgreement
{
    private readonly ISiteCommissionStore _store;

    public SaveSiteCommissionAgreement(ISiteCommissionStore store)
    {
        _store = store;
    }

    public async Task<CommissionAgreement> Handle(
        SaveSiteCommissionAgreementInput input,
        CancellationToken cancellationToken)
    {
        var effectiveFrom = input.EffectiveFrom.Date;
        var effectiveTo = input.EffectiveTo?.Date;
        if (await _store.HasOverlappingAgreementAsync(
                input.SiteId, effectiveFrom, effectiveTo, cancellationToken))
            throw new DomainConflictException("The agreement overlaps an existing agreement for this site.");

        return await _store.AddAgreementAsync(
            new CommissionAgreement(
                0,
                input.SiteId,
                effectiveFrom,
                effectiveTo,
                input.CommissionRate,
                input.Frequency,
                input.Basis,
                input.PaymentDueDaysAfterPeriodEnd,
                default,
                default),
            cancellationToken);
    }
}
