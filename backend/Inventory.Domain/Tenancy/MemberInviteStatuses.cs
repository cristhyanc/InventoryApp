namespace Inventory.Domain.Tenancy;

/// <summary>
/// The supported <see cref="MemberInviteStatus"/> vocabulary, and the check every value that
/// arrives from outside the code has to pass (issue #531). It is the same shape, and exists for the
/// same reason, as <see cref="BusinessRoles"/> and <see cref="BusinessStatuses"/>.
///
/// A C# enum is a compile-time constraint, not a validation: a stored integer column hands back
/// whatever is in it - <c>0</c> from a column an older schema never filled, <c>99</c> from a
/// hand-written <c>UPDATE</c>, or a value a newer deployment wrote and this one does not know.
/// None of those is an invite state, and the fail-closed answer for all of them is the same: report
/// it as unknown and never as <see cref="MemberInviteStatus.Pending"/>, which is the one state that
/// says an invite may still be used.
/// </summary>
public static class MemberInviteStatuses
{
    /// <summary>
    /// The declared states, in lifecycle order. Ordered explicitly rather than by stored value so a
    /// state added out of numeric order still reads correctly.
    /// </summary>
    public static IReadOnlyList<MemberInviteStatus> All { get; } =
    [
        MemberInviteStatus.Pending,
        MemberInviteStatus.Used,
        MemberInviteStatus.Expired,
        MemberInviteStatus.Cancelled,
    ];

    /// <summary>Whether <paramref name="status"/> is one of the declared states.</summary>
    public static bool IsSupported(MemberInviteStatus status) => Enum.IsDefined(status);
}
