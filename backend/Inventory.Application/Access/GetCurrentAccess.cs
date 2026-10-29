using Inventory.Application.Attachments;
using Inventory.Application.Businesses;
using Inventory.Application.PlatformOptions;
using Inventory.Application.Subscriptions;
using Inventory.Application.SupportSessions;
using Inventory.Application.Tenancy;
using Inventory.Domain.Exceptions;
using Inventory.Domain.Tenancy;

namespace Inventory.Application.Access;

/// <summary>
/// Answers "what may I do, and where?" for the signed-in caller (issue #521, extended by issue
/// #551).
///
/// Every part of the answer is a resolved fact. The capabilities come from
/// <see cref="CurrentCapabilities"/>, which chooses between the caller's own role and a support
/// session's fixed set; the business name and time zone from <see cref="GetCurrentBusiness"/>,
/// which resolves the same business the rest of the request acts in; and the role from the caller's
/// membership, which a support session has none of. The use case takes no identifier at all, so no
/// route, query, body or header value can choose whose access is described, and a caller with
/// neither a usable membership nor a usable session is denied rather than told anything.
///
/// It reuses <see cref="GetCurrentBusiness"/> instead of reading the business record itself so
/// there is one definition of "the current business's name and zone", including what a business
/// row that has gone missing means. That costs no second lookup: both resolutions are memoised per
/// request.
///
/// Since issue #873 it also reports what the application <em>offers</em>, from
/// <see cref="GetOptionVisibility"/>: the shared setting that says whether Historical GST
/// Classification is offered to anybody but a Super Admin. That is deliberately part of this one
/// answer rather than a second read of its own - the shell builds its menu from this response, so
/// "what may I do" and "what is on offer" have to arrive together or a menu can be built from one
/// without the other. It is not a capability: the capability set above is identical in both states,
/// and the classification endpoints authorize themselves by <c>Data.Repair</c> regardless.
///
/// Since issue #966 it also reports what the business has <em>bought</em>, from
/// <see cref="GetEffectiveEntitlement"/>: the effective plan, or that nothing is enforced at all,
/// which is the shipped state. That is part of this one answer for the same reason the option
/// visibility is - the shell builds its menu from this response - and it is read last, after the
/// resolutions above, so a caller with no access to describe is denied before anything is read about
/// a business's plan. Inside a support session the business resolved is the one being viewed, so the
/// plan reported is that business's own; a role preview narrows capabilities and cannot reach the
/// plan at all.
/// </summary>
public sealed class GetCurrentAccess
{
    private readonly ICurrentBusinessProvider _currentBusiness;
    private readonly CurrentCapabilities _capabilities;
    private readonly ISupportSessionAccessor _supportSession;
    private readonly GetCurrentBusiness _getCurrentBusiness;
    private readonly GetOptionVisibility _optionVisibility;
    private readonly GetEffectiveEntitlement _entitlement;
    private readonly GetAttachmentStorageStatus _attachmentStorage;

    public GetCurrentAccess(
        ICurrentBusinessProvider currentBusiness,
        CurrentCapabilities capabilities,
        ISupportSessionAccessor supportSession,
        GetCurrentBusiness getCurrentBusiness,
        GetOptionVisibility optionVisibility,
        GetEffectiveEntitlement entitlement,
        GetAttachmentStorageStatus attachmentStorage)
    {
        _currentBusiness = currentBusiness;
        _capabilities = capabilities;
        _supportSession = supportSession;
        _getCurrentBusiness = getCurrentBusiness;
        _optionVisibility = optionVisibility;
        _entitlement = entitlement;
        _attachmentStorage = attachmentStorage;
    }

    /// <exception cref="BusinessAccessDeniedException">
    /// The caller has no usable membership and is not in a support session, so there is no access
    /// to describe.
    /// </exception>
    /// <exception cref="DomainConflictException">
    /// The resolved business no longer exists; see <see cref="GetCurrentBusiness"/>.
    /// </exception>
    public async Task<CurrentAccess> Handle(CancellationToken cancellationToken)
    {
        var support = await _supportSession.ResolveAsync(cancellationToken).ConfigureAwait(false);
        var capabilities = await _capabilities.ListAsync(cancellationToken).ConfigureAwait(false);
        var business = await _getCurrentBusiness.Handle(cancellationToken).ConfigureAwait(false);
        // What the application offers, which is about no business and the same for everybody
        // (issue #873). Read after the resolutions above, so a caller with no access to describe is
        // denied before anything is read about what they would have been offered.
        var options = await _optionVisibility.Handle(cancellationToken).ConfigureAwait(false);
        // And what the business has bought (issue #966), read on the request's own resolved scope -
        // which inside a support session is the business being viewed, so the plan reported is that
        // business's own. Read after the resolutions above for the same reason the options are.
        var entitlement = await _entitlement.Handle(cancellationToken).ConfigureAwait(false);
        // And, only while that entitlement enforces a plan, how much of its attachment storage the
        // business has used (issue #968). With nothing enforced there is no allowance to report and
        // no aggregate runs, so the shipped state's cost per request is unchanged.
        var attachmentStorage = await _attachmentStorage
            .Handle(entitlement, cancellationToken)
            .ConfigureAwait(false);

        // The role is read only for a member. A support session resolves none - the administrator
        // holds no membership in the business at all - and asking for one would deny the request
        // rather than describe it.
        BusinessRole? role = support.IsActive
            ? null
            : await _currentBusiness.RequireRoleAsync(cancellationToken).ConfigureAwait(false);

        return new CurrentAccess(
            role,
            capabilities,
            business.Name,
            business.TimeZoneId,
            support is { IsActive: true, ExpiresAtUtc: { } expiresAtUtc }
                ? new CurrentSupportSession(business.Name, expiresAtUtc, ReadOnly: true)
                : null,
            options.HistoricalGstClassificationVisible,
            entitlement,
            attachmentStorage);
    }
}
