using Inventory.Application.Businesses;
using Inventory.Application.Tenancy;
using Inventory.Domain.Exceptions;

namespace Inventory.Application.Access;

/// <summary>
/// Answers "what may I do, and where?" for the signed-in member (issue #521).
///
/// Both halves of the answer are resolved facts: the role comes from the caller's membership
/// through <see cref="ICurrentBusinessProvider"/>, and the business name and time zone from
/// <see cref="GetCurrentBusiness"/>, which resolves the same membership. The use case takes no
/// identifier at all, so no route, query, body or header value can choose whose access is
/// described, and a caller with no usable membership - including one whose stored role this code
/// does not declare - is denied rather than told anything.
///
/// It reuses <see cref="GetCurrentBusiness"/> instead of reading the business record itself so
/// there is one definition of "the current business's name and zone", including what a business
/// row that has gone missing means. That costs no second membership lookup: resolution is
/// memoised per request.
/// </summary>
public sealed class GetCurrentAccess
{
    private readonly ICurrentBusinessProvider _currentBusiness;
    private readonly GetCurrentBusiness _getCurrentBusiness;

    public GetCurrentAccess(ICurrentBusinessProvider currentBusiness, GetCurrentBusiness getCurrentBusiness)
    {
        _currentBusiness = currentBusiness;
        _getCurrentBusiness = getCurrentBusiness;
    }

    /// <exception cref="BusinessAccessDeniedException">
    /// The caller has no usable membership, so there is no access to describe.
    /// </exception>
    /// <exception cref="DomainConflictException">
    /// The resolved business no longer exists; see <see cref="GetCurrentBusiness"/>.
    /// </exception>
    public async Task<CurrentAccess> Handle(CancellationToken cancellationToken)
    {
        var role = await _currentBusiness.RequireRoleAsync(cancellationToken).ConfigureAwait(false);
        var business = await _getCurrentBusiness.Handle(cancellationToken).ConfigureAwait(false);

        return new CurrentAccess(
            role,
            RoleCapabilities.For(role),
            business.Name,
            business.TimeZoneId);
    }
}
