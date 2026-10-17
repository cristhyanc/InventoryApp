using Inventory.Domain.Tenancy;

namespace Inventory.Application.Access;

/// <summary>
/// Whether this request is being answered as a previewed role, and which one (issue #720).
///
/// <para><strong>Why it is a port.</strong> Asking for a preview is a transport fact - the caller
/// names the role in the <c>X-Role-Preview</c> header, and whether they are the configured platform
/// administrator is answered by the API boundary's own authorization policy - so the implementation
/// belongs at that boundary, exactly as <see cref="SupportSessions.ISupportSessionAccessor"/>'s
/// does. What the inner layers need is the answer: which role the request is to be answered as.</para>
///
/// <para><strong>It is resolved once per request and memoised</strong>, like
/// <see cref="SupportSessions.ISupportSessionAccessor"/> and
/// <see cref="Tenancy.ICurrentBusinessProvider"/>, so the middleware that refuses an unusable
/// preview, the capability authorization handler, the use cases and the response projections cannot
/// disagree about which role this request is being shown.</para>
///
/// <para><strong>It is the only thing in the application that reads that header's value.</strong>
/// Everything downstream reads this answer. The header never chooses which business is resolved -
/// it only narrows what the request may do.</para>
/// </summary>
public interface IRolePreviewAccessor
{
    /// <summary>
    /// Resolves - once per request - what the requested role preview, if any, amounts to.
    /// </summary>
    Task<RolePreviewAccess> ResolveAsync(CancellationToken cancellationToken);
}

/// <summary>
/// What a request asked for, as far as role previews are concerned (issue #720). There are exactly
/// four outcomes and no "unknown but proceed" state.
/// </summary>
public enum RolePreviewState
{
    /// <summary>
    /// This request is not previewing anything: it carries no <c>X-Role-Preview</c> header. Such a
    /// request is completely unaffected by this feature - which is what keeps every existing
    /// caller, member and support session alike, exactly as it was.
    /// </summary>
    NotRequested = 0,

    /// <summary>
    /// A preview was asked for by a caller the platform-admin policy refuses. The request is
    /// refused with <see cref="RolePreviewErrorCodes.NotPermitted"/> and is never served at the
    /// caller's own capabilities instead: a header that was going to narrow access must not be able
    /// to be ignored into full access.
    /// </summary>
    NotPermitted = 1,

    /// <summary>
    /// A platform administrator asked for a preview the vocabulary does not declare - a blank
    /// value, a number, a different case, a role that does not exist, or the header sent more than
    /// once. The request is refused with <see cref="RolePreviewErrorCodes.Invalid"/>, for the same
    /// reason: the one thing an unreadable preview must not do is widen the answer.
    /// </summary>
    Invalid = 2,

    /// <summary>
    /// A platform administrator asked for one of the three declared roles. The request is answered
    /// with what it would otherwise have been allowed, intersected with that role's capabilities,
    /// and may only read.
    /// </summary>
    Active = 3,
}

/// <summary>
/// The resolved role-preview state of one request (issue #720).
/// </summary>
/// <param name="State">Which of the four outcomes this request is in.</param>
/// <param name="Role">
/// The role the request is being answered as, or <see langword="null"/> unless
/// <see cref="RolePreviewState.Active"/>. It is a declared <see cref="BusinessRole"/> and never the
/// text the header carried.
/// </param>
public sealed record RolePreviewAccess(RolePreviewState State, BusinessRole? Role)
{
    /// <summary>No preview was asked for, so nothing about this request changes.</summary>
    public static RolePreviewAccess NotRequested { get; } = new(RolePreviewState.NotRequested, null);

    /// <summary>A preview was asked for by somebody who may not ask for one.</summary>
    public static RolePreviewAccess NotPermitted { get; } = new(RolePreviewState.NotPermitted, null);

    /// <summary>A preview was asked for with a value this vocabulary does not declare.</summary>
    public static RolePreviewAccess Invalid { get; } = new(RolePreviewState.Invalid, null);

    /// <summary>A usable preview of <paramref name="role"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="role"/> is not a declared <see cref="BusinessRole"/>. An undeclared role has
    /// no capability set to intersect with, and the fail-closed answer is to refuse the question
    /// rather than to carry a role nobody declared into an access decision.
    /// </exception>
    public static RolePreviewAccess Active(BusinessRole role) =>
        new(RolePreviewState.Active, BusinessRoles.Require(role));

    /// <summary>Whether this request is being answered as a previewed role.</summary>
    public bool IsActive => State == RolePreviewState.Active;
}
