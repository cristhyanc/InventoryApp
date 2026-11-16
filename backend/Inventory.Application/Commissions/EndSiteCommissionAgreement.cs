using Inventory.Application.Tenancy;
using Inventory.Application.Time;
using Inventory.Domain.Exceptions;
using Inventory.Domain.FinancialConfiguration;
using Inventory.Domain.Tenancy;

namespace Inventory.Application.Commissions;

/// <param name="AgreementId">
/// The agreement to end, looked up only within the caller's own business - so another business's
/// key, and a key that exists nowhere, are the same answer.
/// </param>
/// <param name="EndDate">The date the agreement is to stop covering sales after, inclusive.</param>
public sealed record EndSiteCommissionAgreementInput(int AgreementId, DateTime EndDate);

/// <summary>
/// Ends a site commission agreement on a date (issue #1254), which is what lets a site's rate change
/// without a database edit: <see cref="SaveSiteCommissionAgreement"/> refuses any overlap, so an
/// open-ended 10% agreement from 1 January blocked a 12% agreement from 1 July for ever.
///
/// <para><strong>Ending only ever narrows</strong> (<see cref="SiteCommissionAgreementEndPolicy"/>).
/// The end date must be on or after the agreement's own start date, and an agreement that already
/// ends may be brought forward but never extended. Editing a rate or a start date is deliberately
/// not here and is not an end date: it would rewrite commission already reported.</para>
///
/// <para><strong>The authoritative read is inside the transaction.</strong> The agreement the end
/// date is measured against is re-read after <c>BEGIN IMMEDIATE</c> has taken the write lock, so the
/// comparison and the write are one operation and no other agreement write can land between them -
/// the same lock <see cref="SaveSiteCommissionAgreement"/>'s overlap check and insert take, which is
/// what makes "end the 10% agreement, then add 12% from the next day" two writes that cannot
/// interleave. The caller's own business is resolved from their membership and never from the
/// request, and the lookup is filtered by it, so the route value selects an agreement rather than a
/// tenant.</para>
///
/// <para><strong>So is the clock</strong> (the pattern issue #681 established): the instant the row
/// is stamped with, and audited at, is sampled after the write lock is held, because the only
/// interesting thing that can happen before that is a wait.</para>
///
/// <para><strong>The audit event is emitted after the commit.</strong> A refused change was rolled
/// back and did not happen, and commission is calculated on read - so an end date brought into the
/// past changes what every later report says the site is owed from that date on, and the trail has
/// to name who decided it, when, and what the end date used to be.</para>
/// </summary>
public sealed class EndSiteCommissionAgreement
{
    private readonly ISiteCommissionStore _store;
    private readonly ICurrentBusinessProvider _currentBusiness;
    private readonly IAuthenticatedActorAccessor _actors;
    private readonly ISiteCommissionAgreementAudit _audit;
    private readonly IClock _clock;

    public EndSiteCommissionAgreement(
        ISiteCommissionStore store,
        ICurrentBusinessProvider currentBusiness,
        IAuthenticatedActorAccessor actors,
        ISiteCommissionAgreementAudit audit,
        IClock clock)
    {
        _store = store;
        _currentBusiness = currentBusiness;
        _actors = actors;
        _audit = audit;
        _clock = clock;
    }

    /// <returns>
    /// The agreement as the change left it, or <see langword="null"/> when the caller's business
    /// holds no agreement with that key.
    /// </returns>
    /// <exception cref="BusinessAccessDeniedException">
    /// The caller has no usable membership, or no identified actor the change could be recorded
    /// against. Nothing is read or written in either case.
    /// </exception>
    /// <exception cref="DomainValidationException">
    /// The end date falls before the agreement's start date, or later than the end date it already
    /// carries. Nothing is committed and nothing is audited.
    /// </exception>
    public async Task<CommissionAgreement?> Handle(
        EndSiteCommissionAgreementInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        var businessId = await _currentBusiness.RequireBusinessIdAsync(cancellationToken).ConfigureAwait(false);

        var actorResult = _actors.GetCurrentActor();
        if (actorResult.Actor is not { } actor)
        {
            throw new BusinessAccessDeniedException(
                actorResult.DenialReason ?? BusinessAccessDenialReason.UnidentifiableActor);
        }

        var endDate = input.EndDate.Date;

        await using var transaction = await _store
            .BeginWriteTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        // Sampled with the write lock held: the first instant at which this request could act,
        // however long it waited for the lock.
        var occurredAtUtc = _clock.UtcNow;

        var existing = await _store.FindAgreementAsync(input.AgreementId, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return null;
        }

        var refusal = SiteCommissionAgreementEndPolicy.Decide(
            existing.EffectiveFrom, existing.EffectiveTo, endDate);
        if (refusal is not SiteCommissionAgreementEndRefusal.None)
        {
            throw new DomainValidationException(SiteCommissionAgreementEndPolicy.Message(refusal)!);
        }

        var ended = await _store
            .SetAgreementEndDateAsync(input.AgreementId, endDate, occurredAtUtc, cancellationToken)
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        _audit.Record(new SiteCommissionAgreementEndedAuditEntry(
            actor.DirectoryTenantId,
            actor.ObjectId,
            businessId.Value,
            existing.Id,
            existing.SiteId,
            existing.EffectiveTo,
            endDate,
            occurredAtUtc));

        return ended;
    }
}
