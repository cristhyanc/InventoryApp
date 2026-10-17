using Inventory.Application.SupportSessions;
using Inventory.Application.Tenancy;

namespace Inventory.Application.Access;

/// <summary>
/// What the current request may do, from whichever of the two sources applies (issue #551),
/// narrowed by a Super Admin's role preview where one applies (issue #720).
///
/// <para>Before support sessions existed there was one answer: the caller's stored role, through
/// <see cref="RoleCapabilities"/>. A support session is the second, and it is not a role - the
/// administrator is a member of no business - so it has its own fixed set in
/// <see cref="SupportSessionAccessPolicy"/>. This class is the one place that chooses between
/// them, so the capability the authorization handler enforces, the capability the dashboard decides
/// its shape from, and the list <c>GET /api/me/access</c> reports can never come from different
/// tables.</para>
///
/// <para><strong>A role preview is a third input, and it is not a third source</strong> (issue
/// #720). It chooses nothing: whichever of the two sources applies still decides what the request
/// would have been allowed, and the preview is then <em>intersected</em> with the previewed role's
/// own capabilities through <see cref="RolePreviewPolicy.Narrow"/>. A preview can therefore only
/// ever remove, never add - a Super Admin previewing an Operator inside a support session is shown
/// the support set minus everything an Operator lacks, and a Super Admin with no usable membership
/// is refused exactly as they are without the header, because the base is resolved first. Because
/// every consumer already asks this class, the capability handler, <c>GET /api/me/access</c> and the
/// financial redaction all follow the previewed role with no rule of their own.</para>
///
/// <para>It answers "which capabilities" and deliberately not "which methods". A support session
/// and a preview are both read-only, and that rule is applied where a request's method is known -
/// at the HTTP boundary, by the capability authorization handler and by
/// <c>RolePreviewMiddleware</c> - and again, independently, by the persistence boundary for a
/// session. A capability answer that silently meant "and only for reads" would be an answer whose
/// meaning depended on who asked.</para>
///
/// <para>Registered per request scope: all three inputs are per-request facts, and a cached answer
/// would outlive a role change, an expired session or a preview.</para>
/// </summary>
public sealed class CurrentCapabilities
{
    private readonly ISupportSessionAccessor _supportSession;
    private readonly ICurrentBusinessProvider _currentBusiness;
    private readonly IRolePreviewAccessor _rolePreview;

    public CurrentCapabilities(
        ISupportSessionAccessor supportSession,
        ICurrentBusinessProvider currentBusiness,
        IRolePreviewAccessor rolePreview)
    {
        _supportSession = supportSession;
        _currentBusiness = currentBusiness;
        _rolePreview = rolePreview;
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
        var preview = await _rolePreview.ResolveAsync(cancellationToken).ConfigureAwait(false);
        var capabilities = await BaseListAsync(cancellationToken).ConfigureAwait(false);

        return preview.State switch
        {
            RolePreviewState.NotRequested => capabilities,
            RolePreviewState.Active => RolePreviewPolicy.Narrow(capabilities, preview.Role!.Value),
            // A preview the boundary refused - not this caller's to ask for, or a value naming no
            // role - grants nothing. RolePreviewMiddleware answers both of these before an endpoint
            // runs, so this is the second layer rather than the one a caller meets; its direction is
            // what matters. A preview that could not be applied must never be the reason a request
            // was answered at full capabilities.
            _ => [],
        };
    }

    /// <summary>
    /// Whether the current request holds <paramref name="capability"/>.
    /// </summary>
    /// <exception cref="BusinessAccessDeniedException">
    /// As <see cref="ListAsync"/>: outside a support session an unresolved membership is a refusal.
    /// </exception>
    public async Task<bool> AllowsAsync(Capability capability, CancellationToken cancellationToken)
    {
        var preview = await _rolePreview.ResolveAsync(cancellationToken).ConfigureAwait(false);
        var allowed = await BaseAllowsAsync(capability, cancellationToken).ConfigureAwait(false);

        return preview.State switch
        {
            RolePreviewState.NotRequested => allowed,
            RolePreviewState.Active =>
                allowed && RoleCapabilities.Allows(preview.Role!.Value, capability),
            // As in ListAsync: an unusable preview grants nothing.
            _ => false,
        };
    }

    /// <summary>
    /// What the request would be allowed without any preview: the support session's fixed set, or
    /// the caller's own role's. This is the "base" every narrowing starts from, and it is resolved
    /// <em>before</em> the preview is applied, so a membership that does not resolve is still a
    /// refusal and a preview can never answer in its place.
    /// </summary>
    private async Task<IReadOnlyList<Capability>> BaseListAsync(CancellationToken cancellationToken)
    {
        if (await IsSupportSessionAsync(cancellationToken).ConfigureAwait(false))
        {
            return SupportSessionAccessPolicy.Capabilities;
        }

        var role = await _currentBusiness.RequireRoleAsync(cancellationToken).ConfigureAwait(false);

        return RoleCapabilities.For(role);
    }

    /// <summary>The same base as <see cref="BaseListAsync"/>, asked about one capability.</summary>
    private async Task<bool> BaseAllowsAsync(Capability capability, CancellationToken cancellationToken)
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
