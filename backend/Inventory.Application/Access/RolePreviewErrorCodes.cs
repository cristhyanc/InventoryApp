namespace Inventory.Application.Access;

/// <summary>
/// The three stable error codes a role-preview request can be refused with (issue #720), written
/// once here because a client branches on the code rather than on the sentence beside it - exactly
/// as it does for <see cref="SupportSessions.SupportSessionErrorCodes"/>.
///
/// <para><strong>They have a precedence, and it is the order of how little the request is
/// trusted.</strong> <see cref="NotPermitted"/> first: the caller may not ask for a preview at all,
/// so nothing else about the request needs deciding. Then <see cref="Invalid"/>: the preview cannot
/// be read, so it cannot be applied, and a request that cannot be narrowed must not be answered
/// unnarrowed. Then a support session's own refusals, which are about a credential rather than a
/// view. Then <see cref="ReadOnly"/>, which is the one refusal that says the preview itself was
/// fine.</para>
/// </summary>
public static class RolePreviewErrorCodes
{
    /// <summary>
    /// The request would have changed something. A preview exists to show what a role is shown, and
    /// showing is all it does, so this is the answer to every non-<c>GET</c>/<c>HEAD</c> call
    /// carrying the header - on every endpoint, including the ones that need no membership and the
    /// Super Admin's own.
    /// </summary>
    public const string ReadOnly = "read_only_role_preview";

    /// <summary>
    /// The caller is not the configured platform administrator, so the header means nothing on
    /// their request. It is refused rather than ignored: a header whose only effect is to take
    /// access away must never be able to be dropped into full access, and a client that believes it
    /// is previewing a role has to be told plainly that it is not.
    /// </summary>
    public const string NotPermitted = "role_preview_not_permitted";

    /// <summary>
    /// The presented value is not exactly one of <c>Owner</c>, <c>Manager</c> or <c>Operator</c>, or
    /// the header arrived more than once. The request is refused with <c>400</c>: unlike the other
    /// two this is a malformed request rather than a forbidden one, and the caller can fix it.
    /// </summary>
    public const string Invalid = "invalid_role_preview";
}
