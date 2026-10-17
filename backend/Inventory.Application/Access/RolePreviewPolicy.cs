using System.Collections.Frozen;
using Inventory.Domain.Tenancy;

namespace Inventory.Application.Access;

/// <summary>
/// The rules of a Super Admin's role preview (issue #720): which values name a role, which methods
/// a previewed request may use, and what a preview does to the capabilities the request would
/// otherwise have had.
///
/// <para><strong>A preview only ever takes access away.</strong> <see cref="Narrow"/> is an
/// intersection, so the answer is a subset of the base - the caller's own role's capabilities
/// outside a support session, or the session's fixed set inside one. There is no path here that can
/// add a capability the request did not already hold, which is what makes "the preview is for
/// looking, not for reaching further" a property of the code rather than a claim about the three
/// roles that happen to be declared today.</para>
///
/// <para><strong>The value is matched exactly.</strong> A preview is a deliberate request for one
/// of three published role names, not a value to be interpreted: <c>operator</c>, <c>OPERATOR</c>,
/// <c>20</c>, <c>Viewer</c> and a blank value are all refused rather than guessed at. That is
/// stricter than <see cref="BusinessRoles.TryParse"/>, which accepts any case because it reads an
/// operator's configuration file; this reads a header on a request that is about to be answered
/// differently because of it, and a client that cannot spell the role it asked for has not asked
/// for a role.</para>
///
/// <para><strong>The method rule is separate from the capability rule, and both hold.</strong> A
/// previewed request may read; every other method is refused whatever capability it carried, which
/// is the same shape <see cref="SupportSessionAccessPolicy"/> uses for a support session and
/// deliberately a separate rule from it: a request can be inside both and each refusal has its own
/// code. The allowlist is repeated here rather than shared with that policy because the two are
/// independent rules that happen to agree today, and because a support session's set is not this
/// feature's to change.</para>
/// </summary>
public static class RolePreviewPolicy
{
    /// <summary>
    /// The roles a preview may name: the three declared member roles, and nothing else. A support
    /// session's capability set is not a role and cannot be previewed, and neither can a custom
    /// set.
    /// </summary>
    public static IReadOnlyList<BusinessRole> PreviewableRoles { get; } = BusinessRoles.All;

    private static readonly FrozenSet<string> ReadMethods =
        new[] { "GET", "HEAD" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The one place a presented preview value becomes a role. Only an exact, case-sensitive
    /// published role name is accepted; everything else leaves <paramref name="role"/> at an
    /// undeclared value and answers <see langword="false"/>.
    /// </summary>
    public static bool TryParseRole(string? value, out BusinessRole role)
    {
        role = default;

        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        // No declared role is the enum's default (0), so "no match" comes back as an undeclared
        // value - the same fail-closed shape BusinessRoles.TryParse relies on.
        role = PreviewableRoles.FirstOrDefault(
            candidate => string.Equals(candidate.ToString(), value, StringComparison.Ordinal));

        return BusinessRoles.IsSupported(role);
    }

    /// <summary>
    /// Whether <paramref name="method"/> is one a previewed request may make: <c>GET</c> and
    /// <c>HEAD</c>, and nothing else.
    ///
    /// <para>It is an allowlist rather than a list of methods to refuse, so a method nobody thought
    /// of - a <c>PATCH</c>, a <c>QUERY</c>, a verb a proxy invented - is refused by construction. A
    /// missing or blank method is refused for the same reason: a request whose method cannot be
    /// read is not a request anyone has established is a read.</para>
    /// </summary>
    public static bool IsReadMethod(string? method) =>
        !string.IsNullOrWhiteSpace(method) && ReadMethods.Contains(method);

    /// <summary>
    /// <paramref name="capabilities"/> intersected with everything <paramref name="previewRole"/>
    /// grants, in <paramref name="capabilities"/>'s own order - which is
    /// <see cref="Capabilities.All"/>'s order for both of the sources <c>CurrentCapabilities</c>
    /// chooses between, so the published order is preserved without re-sorting.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="previewRole"/> is not a declared <see cref="BusinessRole"/>.
    /// </exception>
    public static IReadOnlyList<Capability> Narrow(
        IReadOnlyList<Capability> capabilities,
        BusinessRole previewRole)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        // Checked here rather than left to the first capability looked up, so an undeclared role is
        // refused even when the base set is empty: "nothing to intersect" must not be the reason an
        // undeclared role was accepted.
        var role = BusinessRoles.Require(previewRole);

        return [.. capabilities.Where(capability => RoleCapabilities.Allows(role, capability))];
    }
}
