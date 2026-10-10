namespace Inventory.Domain.Tenancy;

/// <summary>
/// The supported <see cref="BusinessRole"/> vocabulary, and the check every value that arrives
/// from outside the code has to pass before access is granted for it (issue #521).
///
/// A C# enum is a compile-time constraint, not a validation. A stored integer column hands back
/// whatever is in it - <c>0</c> from a column an older schema never filled, <c>99</c> from a
/// hand-written <c>UPDATE</c>, or a value a newer deployment wrote and this one does not know -
/// and a configuration string hands back whatever an operator typed. None of those is a role, and
/// the fail-closed answer for all of them is the same: deny, rather than resolve to the lowest
/// privilege and grant something.
/// </summary>
public static class BusinessRoles
{
    /// <summary>
    /// The declared roles, in privilege order. Built from the enum itself so it cannot drift from
    /// the vocabulary, and ordered explicitly rather than by stored value so a role added
    /// out of numeric order still reads correctly.
    /// </summary>
    public static IReadOnlyList<BusinessRole> All { get; } =
    [
        BusinessRole.Operator,
        BusinessRole.Manager,
        BusinessRole.Owner,
    ];

    /// <summary>
    /// The message an unsupported role is rejected with. Built from the enum's own member names so
    /// it cannot drift from the vocabulary it describes.
    /// </summary>
    public static string UnsupportedMessage { get; } =
        $"A business role must be one of {string.Join(", ", Enum.GetNames<BusinessRole>())}.";

    /// <summary>Whether <paramref name="role"/> is one of the declared roles.</summary>
    public static bool IsSupported(BusinessRole role) => Enum.IsDefined(role);

    /// <summary>
    /// Parses a configured role name, for example the optional <c>bootstrap-business</c> member
    /// role. Only a declared member <em>name</em> is accepted, case-insensitively.
    ///
    /// A numeric string is deliberately refused, which <see cref="Enum.TryParse{TEnum}(string, bool, out TEnum)"/>
    /// alone would not do: it happily turns <c>"40"</c> into <see cref="BusinessRole.Owner"/> and
    /// <c>"99"</c> into an undeclared role. Configuration names a role; it does not get to supply
    /// a stored representation.
    /// </summary>
    public static bool TryParse(string? name, out BusinessRole role)
    {
        role = default;

        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var trimmed = name.Trim();

        foreach (var candidate in All)
        {
            if (string.Equals(candidate.ToString(), trimmed, StringComparison.OrdinalIgnoreCase))
            {
                role = candidate;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// <paramref name="role"/> itself when it is declared. This is the fail-closed guard inside
    /// the Domain: a role that reached a capability decision without being validated throws rather
    /// than being treated as some role nobody granted.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="role"/> is not a declared value.</exception>
    public static BusinessRole Require(BusinessRole role) =>
        IsSupported(role)
            ? role
            : throw new ArgumentOutOfRangeException(nameof(role), role, UnsupportedMessage);
}
