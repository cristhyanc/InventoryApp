namespace Inventory.Application.Reorder;

/// <summary>
/// Which facts a reorder calculation accounted for (issue #726). Stock-storage management is the
/// application's own purpose and works with no sales provider connected, so reorder has two explicit
/// modes rather than one mode and a failure: every <see cref="ReorderNeedsResult"/> carries the one
/// it was produced in, and a caller publishes it rather than inferring it from a machine need that
/// happens to be zero.
///
/// <see cref="StorageOnly"/> is deliberately the default value of the enum. A result nobody labelled
/// is then the one that claims less, not the one that claims the machines were read.
/// </summary>
public enum ReorderMode
{
    /// <summary>
    /// Local facts only: storage stock, the low-stock threshold, the restock target and outstanding
    /// supplier-order quantity, with the machines' replenishment need taken as 0. It is a complete
    /// answer about storage, not a partial answer about the fleet.
    /// </summary>
    StorageOnly,

    /// <summary>
    /// The storage-only facts plus the machines' current replenishment need, read live across the
    /// whole Nayax fleet. Reached only when the fleet read and every machine-products read succeeded.
    /// </summary>
    MachineAware,
}

/// <summary>
/// Why a reorder calculation could not account for the machines' current replenishment need, and so
/// ran in <see cref="ReorderMode.StorageOnly"/> (issue #726).
///
/// Availability is per feature (issue #329, decision P9): these are the answers that say "this
/// feature is unavailable for this business right now", not "this request failed". A transport
/// failure, a cancellation and the support-session refusal are none of them - they propagate, so a
/// real outage is never published as a business with no machine need.
/// </summary>
public enum MachineNeedUnavailableReason
{
    /// <summary>
    /// The business has no Nayax credentials an ordinary operation may use - nothing stored, or a
    /// connection the status gate refuses (<c>NotConfigured</c>, <c>NeedsAttention</c>). An operator
    /// has to connect or reconnect Nayax; everything about storage is unaffected.
    /// </summary>
    NayaxNotConnected,

    /// <summary>
    /// The credentials are usable, and Nayax refused one of the fleet reads for lack of permission
    /// (<c>403</c>, while the connection is <c>PendingPermissions</c>). The Nayax contract documents
    /// 403 as the token's scopes not covering the requested resource or action, and each endpoint
    /// carries its own permission, so this is one feature's answer and not a verdict on the
    /// connection - which is why nothing here writes a status.
    /// </summary>
    PermissionNotGranted,

    /// <summary>
    /// The business's effective plan does not include the sales provider (issue #967), so the
    /// credential gate refused before the stored connection was read and no Nayax request was made.
    /// It is deliberately distinct from <see cref="NayaxNotConnected"/>: the credentials may be
    /// perfectly valid and still saved, and what would change the answer is the plan rather than
    /// reconnecting anything. Everything about storage is unaffected, exactly as it is above.
    /// </summary>
    PlanRequired,
}

/// <summary>
/// The stable codes <see cref="ReorderMode"/> is published under. A code a client branches on, like
/// the dashboard's unavailable-section reason beside it, never a sentence to display: the published
/// name is written out here rather than taken from the enum member, so renaming a C# member cannot
/// silently change the API contract.
/// </summary>
public static class ReorderModes
{
    /// <summary>The published code for <see cref="ReorderMode.StorageOnly"/>.</summary>
    public const string StorageOnly = "StorageOnly";

    /// <summary>The published code for <see cref="ReorderMode.MachineAware"/>.</summary>
    public const string MachineAware = "MachineAware";

    /// <exception cref="ArgumentOutOfRangeException">
    /// A mode this vocabulary has never heard of - a value cast from an integer, which an enum
    /// cannot prevent. Refused rather than published under a guessed code.
    /// </exception>
    public static string Code(ReorderMode mode) => mode switch
    {
        ReorderMode.StorageOnly => StorageOnly,
        ReorderMode.MachineAware => MachineAware,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown reorder mode."),
    };
}

/// <summary>
/// The stable codes <see cref="MachineNeedUnavailableReason"/> is published under. Written out here
/// for the same reason <see cref="ReorderModes"/> is - renaming a C# member must not silently change
/// the API contract - and it is the <em>one</em> vocabulary: the dashboard's withheld refill section
/// (<c>UnavailableDashboardSectionDto</c>) and the reorder-alert listing's response metadata both
/// publish these constants, so the two cannot drift apart into two spellings of one answer.
/// </summary>
public static class MachineNeedUnavailableReasons
{
    /// <summary>The published code for <see cref="MachineNeedUnavailableReason.NayaxNotConnected"/>.</summary>
    public const string NayaxNotConnected = "NayaxNotConnected";

    /// <summary>The published code for <see cref="MachineNeedUnavailableReason.PermissionNotGranted"/>.</summary>
    public const string PermissionNotGranted = "PermissionNotGranted";

    /// <summary>
    /// The published code for <see cref="MachineNeedUnavailableReason.PlanRequired"/> (issue #967),
    /// spelled as every other per-feature vocabulary spells the same answer and as
    /// <c>NayaxNotConnectedException.PlanRequiredReason</c> publishes it on the HTTP boundary.
    /// </summary>
    public const string PlanRequired = "PlanRequired";

    /// <exception cref="ArgumentOutOfRangeException">
    /// A reason this vocabulary has never heard of - a value cast from an integer, which an enum
    /// cannot prevent. Refused rather than published under a guessed code.
    /// </exception>
    public static string Code(MachineNeedUnavailableReason reason) => reason switch
    {
        MachineNeedUnavailableReason.NayaxNotConnected => NayaxNotConnected,
        MachineNeedUnavailableReason.PermissionNotGranted => PermissionNotGranted,
        MachineNeedUnavailableReason.PlanRequired => PlanRequired,
        _ => throw new ArgumentOutOfRangeException(
            nameof(reason), reason, "Unknown machine-need unavailability reason."),
    };
}
