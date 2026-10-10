using Inventory.Application.Access;
using Inventory.Domain.Tenancy;
using Xunit;

namespace InventoryApi.Tests.Application.Access;

/// <summary>
/// The agreed role → capability table of issue #521 (#328's contract review of 10 Oct 2026),
/// written out here as the issue states it.
///
/// This is deliberately a restatement of the agreement rather than a walk over the production
/// definition: the table *is* the requirement, so a change to who may see financial figures or
/// manage members has to be made in two places and explained. A test that asked
/// <c>RoleCapabilities</c> what it contained and then asserted it contained that would pass for
/// any table at all.
/// </summary>
public class RoleCapabilitiesTests
{
    private static readonly Capability[] OperatorCapabilities =
    [
        Capability.DashboardView,
        Capability.PickListView,
        Capability.InventoryOperate,
        Capability.PurchasingOperate,
    ];

    private static readonly Capability[] ManagerCapabilities =
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
    ];

    private static readonly Capability[] OwnerCapabilities =
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
    ];

    public static TheoryData<BusinessRole, Capability[]> AgreedTable => new()
    {
        { BusinessRole.Operator, OperatorCapabilities },
        { BusinessRole.Manager, ManagerCapabilities },
        { BusinessRole.Owner, OwnerCapabilities },
    };

    [Theory]
    [MemberData(nameof(AgreedTable))]
    public void A_role_grants_exactly_the_agreed_capabilities(BusinessRole role, Capability[] expected)
    {
        Assert.Equal(expected, RoleCapabilities.For(role));
    }

    /// <summary>
    /// The other half of the same statement: everything the table does <em>not</em> tick must come
    /// back refused. Asserting only the granted list would pass a definition that granted
    /// everything and happened to list these first.
    /// </summary>
    [Theory]
    [MemberData(nameof(AgreedTable))]
    public void A_role_grants_nothing_outside_the_agreed_capabilities(BusinessRole role, Capability[] expected)
    {
        foreach (var capability in Capabilities.All)
        {
            Assert.Equal(expected.Contains(capability), RoleCapabilities.Allows(role, capability));
        }
    }

    /// <summary>
    /// The three financial and configuration boundaries the table exists for, spelled out so a
    /// reviewer can see them without reconstructing a set difference: an Operator sees no money,
    /// a Manager configures no integration and no members, and only an Owner repairs data.
    /// </summary>
    [Fact]
    public void An_operator_sees_no_financial_figures_and_configures_nothing()
    {
        Assert.False(RoleCapabilities.Allows(BusinessRole.Operator, Capability.DashboardViewFinancials));
        Assert.False(RoleCapabilities.Allows(BusinessRole.Operator, Capability.ReportsView));
        Assert.False(RoleCapabilities.Allows(BusinessRole.Operator, Capability.ExpensesManage));
        Assert.False(RoleCapabilities.Allows(BusinessRole.Operator, Capability.ImportsRun));
        Assert.False(RoleCapabilities.Allows(BusinessRole.Operator, Capability.SiteCommissionsManage));
        Assert.False(RoleCapabilities.Allows(BusinessRole.Operator, Capability.SuppliersManageGstDefaults));
    }

    [Fact]
    public void A_manager_does_not_reach_the_owner_only_configuration()
    {
        Assert.False(RoleCapabilities.Allows(BusinessRole.Manager, Capability.IntegrationManage));
        Assert.False(RoleCapabilities.Allows(BusinessRole.Manager, Capability.DataRepair));
        Assert.False(RoleCapabilities.Allows(BusinessRole.Manager, Capability.MembersManage));
        Assert.False(RoleCapabilities.Allows(BusinessRole.Manager, Capability.RolesView));
        Assert.False(RoleCapabilities.Allows(BusinessRole.Manager, Capability.BusinessManage));
    }

    [Fact]
    public void Only_the_owner_holds_every_capability()
    {
        Assert.Equal(Capabilities.All, RoleCapabilities.For(BusinessRole.Owner));

        Assert.NotEqual(Capabilities.All.Count, RoleCapabilities.For(BusinessRole.Manager).Count);
        Assert.NotEqual(Capabilities.All.Count, RoleCapabilities.For(BusinessRole.Operator).Count);
    }

    /// <summary>
    /// The hierarchy the agreed table happens to describe today, asserted rather than assumed:
    /// the production definition writes each role's set out in full, so this is a real check that
    /// no capability was granted to a Manager and then forgotten for an Owner.
    /// </summary>
    [Fact]
    public void Each_role_includes_everything_the_role_below_it_grants()
    {
        Assert.Subset(
            new HashSet<Capability>(RoleCapabilities.For(BusinessRole.Manager)),
            new HashSet<Capability>(RoleCapabilities.For(BusinessRole.Operator)));

        Assert.Subset(
            new HashSet<Capability>(RoleCapabilities.For(BusinessRole.Owner)),
            new HashSet<Capability>(RoleCapabilities.For(BusinessRole.Manager)));
    }

    /// <summary>
    /// A role no vocabulary declares has no capabilities defined for it, and the fail-closed
    /// answer is to refuse the question. Returning an empty list would be read as "nothing is
    /// granted" by a caller that checks one capability, and as a usable answer by one that does
    /// not check at all.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(99)]
    public void An_undeclared_role_has_no_capability_answer_at_all(int storedRole)
    {
        var role = (BusinessRole)storedRole;

        Assert.Throws<ArgumentOutOfRangeException>(() => RoleCapabilities.For(role));
        Assert.Throws<ArgumentOutOfRangeException>(() => RoleCapabilities.Allows(role, Capability.DashboardView));
    }

    [Fact]
    public void Every_declared_capability_is_granted_to_at_least_one_role()
    {
        foreach (var capability in Capabilities.All)
        {
            Assert.Contains(
                capability,
                BusinessRoles.All.SelectMany(RoleCapabilities.For));
        }
    }
}
