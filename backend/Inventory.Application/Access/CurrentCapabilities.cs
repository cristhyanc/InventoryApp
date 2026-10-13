using Inventory.Application.SupportSessions;
using Inventory.Application.Tenancy;

namespace Inventory.Application.Access;

/// <summary>
/// What the current request may do, from whichever of the two sources applies (issue #551).
///
/// <para>Before support sessions existed there was one answer: the caller's stored role, through
/// <see cref="RoleCapabilities"/>. A support session is the second, and it is not a role - the
/// administrator is a member of no business - so it has its own fixed set in
/// <see cref="SupportSessionAccessPolicy"/>. This class is the one place that chooses between
/// them, so the capability the authorization handler enforces, the capability the dashboard decides
/// its shape from, and the list <c>GET /api/me/access</c> reports can never come from different
/// tables.</para>
///
/// <para>It answers "which capabilities" and deliberately not "which methods". A support session is
/// read-only, and that rule is applied where a request's method is known - at the HTTP boundary, by
/// the capability authorization handler - and again, independently, by the persistence boundary. A
/// capability answer that silently meant "and only for reads" would be an answer whose meaning
/// depended on who asked.</para>
///
/// <para>Registered per request scope: both sources are per-request facts, and a cached answer
/// would outlive a role change or an expired session.</para>
/// </summary>
public sealed class CurrentCapabilities
{
    private readonly ISupportSessionAccessor _supportSession;
    private readonly ICurrentBusinessProvider _currentBusiness;

    public CurrentCapabilities(
        ISupportSessionAccessor supportSession,
        ICurrentBusinessProvider currentBusiness)
    {
        _supportSession = supportSession;
        _currentBusiness = currentBusiness;
    }

    /// <summary>
    /// Everything the current request may do, in the agreed table's order.
    /// </summary>
    /// <exception cref="BusinessAccessDeniedException">
    /// The request is not in a support session and the caller has no usable membership, so there is
    /// no access to describe. An unresolved membership is a refusal, never a fall back to the
    /// least-privileged role.
    /// </exception>
    public async Task<IReadOnlyList<Capability>> ListAsync(CancellationToken cancellationToken)
    {
        if (await IsSupportSessionAsync(cancellationToken).ConfigureAwait(false))
        {
            return SupportSessionAccessPolicy.Capabilities;
        }

        var role = await _currentBusiness.RequireRoleAsync(cancellationToken).ConfigureAwait(false);

        return RoleCapabilities.For(role);
    }

    /// <summary>
    /// Whether the current request holds <paramref name="capability"/>.
    /// </summary>
    /// <exception cref="BusinessAccessDeniedException">
    /// As <see cref="ListAsync"/>: outside a support session an unresolved membership is a refusal.
    /// </exception>
    public async Task<bool> AllowsAsync(Capability capability, CancellationToken cancellationToken)
    {
        if (await IsSupportSessionAsync(cancellationToken).ConfigureAwait(false))
        {
            return SupportSessionAccessPolicy.Allows(capability);
        }

        var role = await _currentBusiness.RequireRoleAsync(cancellationToken).ConfigureAwait(false);

        return RoleCapabilities.Allows(role, capability);
    }

    private async Task<bool> IsSupportSessionAsync(CancellationToken cancellationToken) =>
        (await _supportSession.ResolveAsync(cancellationToken).ConfigureAwait(false)).IsActive;
}
