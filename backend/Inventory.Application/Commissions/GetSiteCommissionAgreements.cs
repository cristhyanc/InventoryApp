using Inventory.Domain.FinancialConfiguration;

namespace Inventory.Application.Commissions;

public sealed class GetSiteCommissionAgreements
{
    private readonly ISiteCommissionStore _store;

    public GetSiteCommissionAgreements(ISiteCommissionStore store)
    {
        _store = store;
    }

    public async Task<IReadOnlyList<CommissionAgreement>> Handle(
        long? siteId,
        CancellationToken cancellationToken) =>
        (await _store.GetAgreementsAsync(siteId, cancellationToken))
            .OrderByDescending(agreement => agreement.EffectiveFrom)
            .ToList();
}
