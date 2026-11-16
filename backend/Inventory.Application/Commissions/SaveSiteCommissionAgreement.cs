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

/// <summary>
/// Adds a site commission agreement, refusing one that overlaps an agreement the site already has.
///
/// <para><strong>The overlap check and the insert are one operation</strong> (issue #1254). Both run
/// inside the store's <c>BEGIN IMMEDIATE</c> transaction, so the write lock is held before the check
/// reads the site's agreements: two callers adding overlapping agreements are serialised into a
/// refusal rather than both passing a check and both inserting, and an agreement being ended
/// (<see cref="EndSiteCommissionAgreement"/>) cannot commit between this check and this insert.
/// Ending the 10% agreement on 30 June and adding 12% from 1 July therefore either sees the ended
/// agreement or waits for it - never the state in between.</para>
/// </summary>
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
        ArgumentNullException.ThrowIfNull(input);

        var effectiveFrom = input.EffectiveFrom.Date;
        var effectiveTo = input.EffectiveTo?.Date;

        await using var transaction = await _store
            .BeginWriteTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        if (await _store.HasOverlappingAgreementAsync(
                input.SiteId, effectiveFrom, effectiveTo, cancellationToken).ConfigureAwait(false))
            throw new DomainConflictException("The agreement overlaps an existing agreement for this site.");

        var added = await _store.AddAgreementAsync(
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
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return added;
    }
}
