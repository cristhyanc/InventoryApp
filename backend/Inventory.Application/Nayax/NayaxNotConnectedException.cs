using Inventory.Domain.Nayax;

namespace Inventory.Application.Nayax;

/// <summary>
/// The stable "Nayax is not connected" failure (issue #520): the current business has no Nayax
/// credentials the status gate will let an ordinary operation use.
///
/// One exception type for every way that can happen - no connection record at all,
/// <see cref="NayaxConnectionStatus.NotConfigured"/>,
/// <see cref="NayaxConnectionStatus.NeedsAttention"/>, and a 401 that has just moved the connection
/// there - because the caller's answer is the same in all of them: an operator has to connect or
/// reconnect Nayax, and retrying will not help. The frontend shows
/// <see cref="StableMessage"/> as it arrives, and <see cref="ErrorCode"/> is what it may branch on;
/// <c>InventoryApi.Http.NayaxConnectionExceptionHandler</c> maps both onto the HTTP boundary.
///
/// <see cref="Status"/> is the business's own configuration state, not another actor's data and not
/// a secret, so it is safe to publish and is what lets a screen eventually say "connect Nayax"
/// rather than "reconnect Nayax" (issue #329). The message carries no operator id, no token and no
/// business identifier.
/// </summary>
public sealed class NayaxNotConnectedException : Exception
{
    /// <summary>
    /// The stable code a client may branch on. It is part of the API contract: change it only
    /// deliberately, with the frontend.
    /// </summary>
    public const string ErrorCode = "nayax_not_connected";

    /// <summary>The stable, caller-safe sentence every one of these failures carries.</summary>
    public const string StableMessage =
        "Nayax is not connected. Connect this business's Nayax account to use this feature.";

    /// <param name="status">The connection status that refused the operation.</param>
    public NayaxNotConnectedException(NayaxConnectionStatus status)
        : base(StableMessage)
    {
        Status = status;
    }

    /// <summary>
    /// The status the gate refused, or <see cref="NayaxConnectionStatus.NotConfigured"/> when there
    /// is no stored connection to have a status.
    /// </summary>
    public NayaxConnectionStatus Status { get; }
}
