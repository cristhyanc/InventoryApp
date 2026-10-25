using Inventory.Application.Businesses;
using Inventory.Application.PlatformOptions;
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
/// </summary>
public sealed class GetCurrentAccess
{
    private readonly ICurrentBusinessProvider _currentBusiness;
    private readonly CurrentCapabilities _capabilities;
    private readonly ISupportSessionAccessor _supportSession;
    private readonly GetCurrentBusiness _getCurrentBusiness;
    private readonly GetOptionVisibility _optionVisibility;

    public GetCurrentAccess(
        ICurrentBusinessProvider currentBusiness,
        CurrentCapabilities capabilities,
        ISupportSessionAccessor supportSession,
        GetCurrentBusiness getCurrentBusiness,
        GetOptionVisibility optionVisibility)
    {
        _currentBusiness = currentBusiness;
        _capabilities = capabilities;
        _supportSession = supportSession;
        _getCurrentBusiness = getCurrentBusiness;
        _optionVisibility = optionVisibility;
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
            options.HistoricalGstClassificationVisible);
    }
}
