namespace Inventory.Application.Nayax;

/// <summary>
/// The stable per-feature "Nayax hasn't granted permission for this" failure (issue #520): Nayax
/// answered one call with <c>403 Forbidden</c> while the business's credentials were stored but not
/// yet verified (<see cref="Domain.Nayax.NayaxConnectionStatus.PendingPermissions"/>).
///
/// It is deliberately **not** a credential verdict, which is why it is a different type from
/// <see cref="NayaxNotConnectedException"/> and why nothing on this path writes a status. Nayax
/// documents 403 as the token's scopes not covering the requested resource or action
/// (https://devzone.nayax.com/docs/manage-data-operations/lynx-api/app-tokens), so the connection
/// as a whole may be perfectly usable while one feature is not: marking it
/// <see cref="Domain.Nayax.NayaxConnectionStatus.NeedsAttention"/> would take every other Nayax
/// feature down with it.
///
/// <see cref="Operation"/> names the client call that was refused, so an operator can find which
/// permission is missing. It is a safe internal diagnostic - the name of a method, like
/// <c>Inventory.Infrastructure.Nayax.NayaxUpstreamException.Operation</c> - and it is logged rather
/// than returned to the caller; the public response carries only
/// <see cref="StableMessage"/> and <see cref="ErrorCode"/>.
/// </summary>
public sealed class NayaxPermissionNotGrantedException : Exception
{
    /// <summary>
    /// The stable code a client may branch on. It is part of the API contract: change it only
    /// deliberately, with the frontend.
    /// </summary>
    public const string ErrorCode = "nayax_permission_not_granted";

    /// <summary>The stable, caller-safe sentence every one of these failures carries.</summary>
    public const string StableMessage =
        "Nayax hasn't granted permission for this. Check the Nayax account's permissions for this feature.";

    /// <param name="operation">The Nayax client operation Nayax refused, for example <c>GetMachinesAsync</c>.</param>
    public NayaxPermissionNotGrantedException(string operation)
        : base(StableMessage)
    {
        Operation = operation;
    }

    /// <summary>The stable name of the refused client call, for diagnostics. Never a secret.</summary>
    public string Operation { get; }
}
