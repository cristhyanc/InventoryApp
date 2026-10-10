using System.Collections.Frozen;
using Inventory.Domain.Tenancy;

namespace Inventory.Application.Access;

/// <summary>
/// The one role → capability definition (issue #521). Every answer to "may this member do that?"
/// comes from here: there is no second table, no per-controller list and nothing computed from a
/// role's stored value.
///
/// Each role's set is written out in full rather than as "the role below, plus these". The table
/// in issue #328 is what a human reviews against, and a literal set is read the same way that
/// table is - one row, one list. The hierarchy that happens to hold today (Operator ⊂ Manager ⊂
/// Owner) is therefore an assertion in the tests, not an assumption in the code, so a future role
/// that is not simply "more of the one below" - the read-only Viewer #328 anticipates, for example
/// - needs no restructuring here.
///
/// Issue #521 defines and reports these capabilities. Enforcing one on an endpoint is issue
/// #502's work: nothing in this slice changes what any member may reach.
/// </summary>
public static class RoleCapabilities
{
    private static readonly FrozenDictionary<BusinessRole, FrozenSet<Capability>> ByRole =
        new Dictionary<BusinessRole, FrozenSet<Capability>>
        {
            [BusinessRole.Operator] = FrozenSet.ToFrozenSet(
            [
                Capability.DashboardView,
                Capability.PickListView,
                Capability.InventoryOperate,
                Capability.PurchasingOperate,
            ]),
            [BusinessRole.Manager] = FrozenSet.ToFrozenSet(
            [
                Capability.DashboardView,
                Capability.DashboardViewFinancials,
                Capability.PickListView,
                Capability.InventoryOperate,
                Capability.PurchasingOperate,
                Capability.SuppliersManageGstDefaults,
                Capability.ExpensesManage,
                Capability.ReportsView,
                Capability.ImportsRun,
                Capability.SiteCommissionsManage,
            ]),
            [BusinessRole.Owner] = FrozenSet.ToFrozenSet(
            [
                Capability.DashboardView,
                Capability.DashboardViewFinancials,
                Capability.PickListView,
                Capability.InventoryOperate,
                Capability.PurchasingOperate,
                Capability.SuppliersManageGstDefaults,
                Capability.ExpensesManage,
                Capability.ReportsView,
                Capability.ImportsRun,
                Capability.SiteCommissionsManage,
                Capability.IntegrationManage,
                Capability.DataRepair,
                Capability.MembersManage,
                Capability.RolesView,
                Capability.BusinessManage,
            ]),
        }.ToFrozenDictionary();

    /// <summary>
    /// Every capability <paramref name="role"/> grants, in the agreed table's order.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="role"/> is not a declared <see cref="BusinessRole"/>. An undeclared role
    /// has no capabilities defined for it, and the fail-closed answer is to refuse the question
    /// rather than return an empty list a caller might read as "nothing is forbidden". A request
    /// cannot reach this: <see cref="BusinessMembershipResolutionPolicy"/> denies an unrecognised
    /// stored role before an endpoint runs.
    /// </exception>
    public static IReadOnlyList<Capability> For(BusinessRole role)
    {
        var granted = ByRole[BusinessRoles.Require(role)];

        return [.. Capabilities.All.Where(granted.Contains)];
    }

    /// <summary>
    /// Whether <paramref name="role"/> grants <paramref name="capability"/>. This is the predicate
    /// issue #502's endpoint policies are expected to call, so that enforcement and the reported
    /// capability list can never disagree.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="role"/> is not a declared <see cref="BusinessRole"/>.
    /// </exception>
    public static bool Allows(BusinessRole role, Capability capability) =>
        ByRole[BusinessRoles.Require(role)].Contains(capability);
}
