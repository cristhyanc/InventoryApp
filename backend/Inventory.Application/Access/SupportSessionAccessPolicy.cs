using System.Collections.Frozen;

namespace Inventory.Application.Access;

/// <summary>
/// What a platform administrator may do inside a support session (issue #551): the exact set of
/// capabilities the session grants, and the rule that every one of them is read-only.
///
/// <para><strong>The set is written out, not derived.</strong> It is deliberately not
/// "<see cref="Capabilities.All"/> minus two": a capability declared by a later deployment must
/// arrive outside a support view and be added here by somebody who decided it belonged, rather
/// than be granted to a reader from outside the business on the day it is invented. The two
/// exclusions from today's vocabulary are recorded in <see cref="Excluded"/> with their reasons, so
/// the difference between the two lists is reviewable rather than implied.</para>
///
/// <para><strong>The method rule is the other half, and the two are independent.</strong> A
/// capability in this set says the supporter may look at that area; the method rule says looking is
/// all they may do. Both have to hold, and a non-read request is refused whatever capability it
/// carried - which is why the method rule is written here beside the set rather than inside the
/// authorization handler that applies it.</para>
///
/// <para>Neither half is the whole guarantee. <c>BusinessOwnershipEnforcer</c> refuses every
/// <c>SaveChanges</c> in <see cref="Tenancy.BusinessScopeState.SupportReadOnly"/> and document
/// storage refuses to write or delete, so a read that turns out to have a side effect still writes
/// nothing. These rules decide what a request is allowed to ask for; those decide what the
/// database will accept.</para>
/// </summary>
public static class SupportSessionAccessPolicy
{
    /// <summary>
    /// Every capability a support session grants, in <see cref="Capabilities.All"/>'s order so
    /// <c>GET /api/me/access</c> reports it deterministically.
    /// </summary>
    public static IReadOnlyList<Capability> Capabilities { get; } =
    [
        Capability.DashboardView,
        Capability.DashboardViewFinancials,
        Capability.PickListView,
        Capability.InventoryOperate,
        Capability.PurchasingOperate,
        Capability.ExpensesManage,
        Capability.ReportsView,
        Capability.ImportsRun,
        Capability.SiteCommissionsManage,
        Capability.IntegrationManage,
        Capability.DataRepair,
        Capability.RolesView,
        Capability.BusinessManage,
    ];

    /// <summary>
    /// The capabilities today's vocabulary declares that a support session deliberately does
    /// <em>not</em> grant, each for its own reason:
    /// <list type="bullet">
    ///   <item><see cref="Capability.SuppliersManageGstDefaults"/> guards nothing but an edit, and
    ///   every edit is already refused, so granting it would be a grant with no effect that a
    ///   later reader would have to work out was harmless.</item>
    ///   <item><see cref="Capability.MembersManage"/> is the one capability whose reads are
    ///   themselves private: the members list carries people's names and email addresses, and in
    ///   v1 a supporter from outside the business does not see those.</item>
    /// </list>
    /// </summary>
    public static IReadOnlyList<Capability> Excluded { get; } =
    [
        Capability.SuppliersManageGstDefaults,
        Capability.MembersManage,
    ];

    private static readonly FrozenSet<Capability> Granted = Capabilities.ToFrozenSet();

    private static readonly FrozenSet<string> ReadMethods =
        new[] { "GET", "HEAD" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether a support session grants <paramref name="capability"/>.</summary>
    public static bool Allows(Capability capability) => Granted.Contains(capability);

    /// <summary>
    /// Whether <paramref name="method"/> is one a support session may make: <c>GET</c> and
    /// <c>HEAD</c>, and nothing else.
    ///
    /// <para>It is an allowlist rather than a list of methods to refuse, so a method nobody thought
    /// of - a <c>PATCH</c>, a <c>QUERY</c>, a verb a proxy invented - is refused by construction. A
    /// missing or blank method is refused for the same reason: a request whose method cannot be
    /// read is not a request anyone has established is a read.</para>
    /// </summary>
    public static bool IsReadMethod(string? method) =>
        !string.IsNullOrWhiteSpace(method) && ReadMethods.Contains(method);
}
