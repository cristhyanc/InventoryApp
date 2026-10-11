using System.Collections.Frozen;

namespace Inventory.Application.Access;

/// <summary>
/// The one external spelling of each <see cref="Capability"/> (issue #521).
///
/// The names are the agreed <c>Area.Action</c> vocabulary - <c>Dashboard.ViewFinancials</c>,
/// <c>Inventory.Operate</c> - and they are a published contract: <c>GET /api/me/access</c> returns
/// them, and issue #502's endpoint policies will be named after them. They are therefore written
/// out once, here, rather than derived from the C# member names, because no mechanical rule
/// recovers where the dot belongs and a clever one would be free to move it.
/// </summary>
public static class Capabilities
{
    private static readonly FrozenDictionary<Capability, string> Names =
        new Dictionary<Capability, string>
        {
            [Capability.DashboardView] = "Dashboard.View",
            [Capability.DashboardViewFinancials] = "Dashboard.ViewFinancials",
            [Capability.PickListView] = "PickList.View",
            [Capability.InventoryOperate] = "Inventory.Operate",
            [Capability.PurchasingOperate] = "Purchasing.Operate",
            [Capability.SuppliersManageGstDefaults] = "Suppliers.ManageGstDefaults",
            [Capability.ExpensesManage] = "Expenses.Manage",
            [Capability.ReportsView] = "Reports.View",
            [Capability.ImportsRun] = "Imports.Run",
            [Capability.SiteCommissionsManage] = "SiteCommissions.Manage",
            [Capability.IntegrationManage] = "Integration.Manage",
            [Capability.DataRepair] = "Data.Repair",
            [Capability.MembersManage] = "Members.Manage",
            [Capability.RolesView] = "Roles.View",
            [Capability.BusinessManage] = "Business.Manage",
        }.ToFrozenDictionary();

    /// <summary>
    /// Every declared capability in the agreed table's order. Used to report a role's
    /// capabilities deterministically and by the tests that pin the vocabulary.
    /// </summary>
    public static IReadOnlyList<Capability> All { get; } = Enum.GetValues<Capability>();

    /// <summary>The published name of <paramref name="capability"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="capability"/> is not a declared capability. An undeclared value has no
    /// name to publish, and inventing one would put a capability on the wire that no role grants.
    /// </exception>
    public static string Name(Capability capability) =>
        Names.TryGetValue(capability, out var name)
            ? name
            : throw new ArgumentOutOfRangeException(
                nameof(capability),
                capability,
                $"A capability must be one of {string.Join(", ", Names.Values)}.");
}
