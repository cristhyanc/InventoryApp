using Inventory.Application.Tenancy;
using Inventory.Application.Time;
using Inventory.Domain.Exceptions;
using Inventory.Domain.Tenancy;

namespace Inventory.Application.Businesses;

/// <summary>
/// Saves the settings of the signed-in Owner's own business (issue #548): its name, contact
/// address, ABN, GST registration and the IANA time zone its business days are derived in.
///
/// <para><strong>Which business is never asked.</strong> The use case takes the submitted settings
/// and nothing else: the business comes from the authenticated actor's membership through
/// <see cref="ICurrentBusinessProvider"/>, so no route, query, body or header value can aim a save
/// at another business. Which <em>roles</em> may call it is the endpoint's <c>Business.Manage</c>
/// capability, decided by the authorization middleware before this runs.</para>
///
/// <para><strong>Validation first, then the write, then the audit.</strong>
/// <see cref="BusinessSettingsPolicy"/> refuses a submission as a whole, so a rejected save stores
/// nothing at all; the time zone's resolvability is the one check made further in, centrally on
/// <c>SaveChanges</c>, where every write path to a business is validated alike (see that policy).
/// The audit event is recorded only after the write has been stored and only when the write
/// actually changed something, because the event is evidence about a change rather than about an
/// attempt.</para>
///
/// <para><strong>An unauditable change is not made.</strong> The acting identity is read
/// <em>before</em> the write, and a request with no identified actor is refused rather than stored
/// with an anonymous audit event. A request cannot normally reach this - resolving the current
/// business already requires an identified actor - and the ordering is what keeps that true if it
/// ever can.</para>
///
/// <para><strong>Nothing stored is rewritten by a zone change.</strong> A new time zone changes how
/// stored instants are read, not the instants: business days and report boundaries for past and
/// future data alike are re-derived in the new zone from the next request onwards, because the zone
/// is resolved per request from the business's own record (docs/architecture.md § Per-business time
/// zone). This use case therefore touches no sale, purchase, expense, commission or costing
/// row.</para>
/// </summary>
public sealed class UpdateBusinessSettings
{
    private readonly ICurrentBusinessProvider _currentBusiness;
    private readonly IAuthenticatedActorAccessor _actors;
    private readonly IBusinessSettingsStore _store;
    private readonly IBusinessSettingsAudit _audit;
    private readonly IClock _clock;

    public UpdateBusinessSettings(
        ICurrentBusinessProvider currentBusiness,
        IAuthenticatedActorAccessor actors,
        IBusinessSettingsStore store,
        IBusinessSettingsAudit audit,
        IClock clock)
    {
        _currentBusiness = currentBusiness;
        _actors = actors;
        _store = store;
        _audit = audit;
        _clock = clock;
    }

    /// <exception cref="BusinessAccessDeniedException">
    /// The caller has no usable membership, or no identified actor the change could be recorded
    /// against. Nothing is written in either case.
    /// </exception>
    /// <exception cref="DomainValidationException">
    /// The submitted settings break one of the rules in <see cref="BusinessSettingsPolicy"/>, or -
    /// from the central enforcer on the way into the database - name a time zone the host cannot
    /// resolve. Nothing is written.
    /// </exception>
    /// <exception cref="DomainConflictException">
    /// The resolved business no longer exists; see <see cref="GetBusinessSettings"/>.
    /// </exception>
    public async Task<BusinessSettings> Handle(
        BusinessSettingsSubmission submission,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submission);

        var businessId = await _currentBusiness.RequireBusinessIdAsync(cancellationToken).ConfigureAwait(false);

        var actorResult = _actors.GetCurrentActor();
        if (actorResult.Actor is not { } actor)
        {
            throw new BusinessAccessDeniedException(
                actorResult.DenialReason ?? BusinessAccessDenialReason.UnidentifiableActor);
        }

        var submitted = BusinessSettingsPolicy.Validate(
            submission.Name,
            submission.ContactEmail,
            submission.Abn,
            submission.GstRegistered,
            submission.TimeZoneId);

        var result = await _store.SaveAsync(businessId, submitted, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainConflictException(BusinessSettingsMessages.BusinessUnavailable);

        if (result.ChangedFields.Count > 0)
        {
            _audit.Record(new BusinessSettingsAuditEntry(
                actor.DirectoryTenantId,
                actor.ObjectId,
                businessId.Value,
                _clock.UtcNow,
                result.ChangedFields));
        }

        return result.Saved;
    }
}

/// <summary>
/// The settings an Owner submitted, exactly as they arrived and before any rule has looked at them
/// (issue #548).
///
/// Every member is nullable on purpose: "the caller did not send this" has to be distinguishable
/// from "the caller sent an empty value", and both are refusals rather than silent defaults - a
/// missing <see cref="GstRegistered"/> that bound to <see langword="false"/> would record a tax fact
/// nobody stated. It carries no business identifier, because the business is resolved from the
/// caller's membership and never submitted.
/// </summary>
public sealed record BusinessSettingsSubmission(
    string? Name,
    string? ContactEmail,
    string? Abn,
    bool? GstRegistered,
    string? TimeZoneId);
