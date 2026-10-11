using Inventory.Application.Access;
using Xunit;

namespace InventoryApi.Tests.Application.Access;

/// <summary>
/// The published capability names of issue #521, restated as the issue's table spells them.
///
/// These strings are a contract: <c>GET /api/me/access</c> returns them and issue #502's endpoint
/// policies will be named after them, so a renamed capability silently breaks a client or a
/// policy. Written out independently of the production map for that reason.
/// </summary>
public class CapabilitiesTests
{
    public static TheoryData<Capability, string> AgreedNames => new()
    {
        { Capability.DashboardView, "Dashboard.View" },
        { Capability.DashboardViewFinancials, "Dashboard.ViewFinancials" },
        { Capability.PickListView, "PickList.View" },
        { Capability.InventoryOperate, "Inventory.Operate" },
        { Capability.PurchasingOperate, "Purchasing.Operate" },
        { Capability.SuppliersManageGstDefaults, "Suppliers.ManageGstDefaults" },
        { Capability.ExpensesManage, "Expenses.Manage" },
        { Capability.ReportsView, "Reports.View" },
        { Capability.ImportsRun, "Imports.Run" },
        { Capability.SiteCommissionsManage, "SiteCommissions.Manage" },
        { Capability.IntegrationManage, "Integration.Manage" },
        { Capability.DataRepair, "Data.Repair" },
        { Capability.MembersManage, "Members.Manage" },
        { Capability.RolesView, "Roles.View" },
        { Capability.BusinessManage, "Business.Manage" },
    };

    [Theory]
    [MemberData(nameof(AgreedNames))]
    public void A_capability_has_its_agreed_published_name(Capability capability, string expected)
    {
        Assert.Equal(expected, Capabilities.Name(capability));
    }

    [Fact]
    public void The_vocabulary_is_exactly_the_fifteen_agreed_capabilities()
    {
        Assert.Equal(15, Capabilities.All.Count);
        Assert.Equal(AgreedNames.Count(), Capabilities.All.Count);
    }

    /// <summary>
    /// Every declared capability has a name, and no two share one: an unnamed capability would
    /// throw the moment a role that grants it is reported, and a duplicated name would make two
    /// different capabilities indistinguishable to a client and to a policy.
    /// </summary>
    [Fact]
    public void Every_capability_has_a_distinct_name()
    {
        var names = Capabilities.All.Select(Capabilities.Name).ToList();

        Assert.DoesNotContain(names, name => string.IsNullOrWhiteSpace(name));
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    [InlineData(999)]
    public void An_undeclared_capability_has_no_name_to_publish(int value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Capabilities.Name((Capability)value));
    }
}
