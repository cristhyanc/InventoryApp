using Inventory.Application.Access;
using Xunit;

namespace InventoryApi.Tests.Application.Access;

/// <summary>
/// What a support session grants, and the read-only rule beside it (issue #551).
///
/// <para>The expected set is written out literally from the issue's own list rather than read back
/// from <see cref="SupportSessionAccessPolicy"/>: a test that asked the table what it contains would
/// pass whatever that table happened to say. The exclusions are asserted as exclusions for the same
/// reason - "it is not in the list" and "it was deliberately left out" are different claims, and
/// only the second one survives somebody adding a capability.</para>
/// </summary>
public class SupportSessionAccessPolicyTests
{
    /// <summary>
    /// The agreed set, in the agreed order, exactly as issue #551 states it. Thirteen capabilities:
    /// the whole of today's vocabulary except the GST defaults and member management.
    /// </summary>
    [Fact]
    public void The_support_capability_set_is_exactly_the_agreed_thirteen()
    {
        Assert.Equal(
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
            ],
            SupportSessionAccessPolicy.Capabilities);
    }

    /// <summary>
    /// The two exclusions, and why each one is the right kind of exclusion to be able to state:
    /// the GST defaults guard only an edit, and the members list is itself private in v1.
    /// </summary>
    [Theory]
    [InlineData(Capability.SuppliersManageGstDefaults)]
    [InlineData(Capability.MembersManage)]
    public void The_two_excluded_capabilities_are_not_granted(Capability capability)
    {
        Assert.Contains(capability, SupportSessionAccessPolicy.Excluded);
        Assert.False(SupportSessionAccessPolicy.Allows(capability));
    }

    /// <summary>
    /// Granted plus excluded is the whole vocabulary, which is what makes the two lists above a
    /// complete decision about today's capabilities rather than a sample of them. A capability
    /// declared later fails this test, which is the point: somebody has to decide which list it
    /// belongs in.
    /// </summary>
    [Fact]
    public void Every_declared_capability_is_either_granted_or_deliberately_excluded()
    {
        Assert.Equal(
            Capabilities.All.Order().ToArray(),
            SupportSessionAccessPolicy.Capabilities
                .Concat(SupportSessionAccessPolicy.Excluded)
                .Order()
                .ToArray());
    }

    [Fact]
    public void Every_capability_in_the_set_is_allowed()
    {
        Assert.All(
            SupportSessionAccessPolicy.Capabilities,
            capability => Assert.True(SupportSessionAccessPolicy.Allows(capability)));
    }

    /// <summary>
    /// A value no deployment declares is not a capability a support session grants. An enum is a
    /// compile-time constraint, not a validation.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(99)]
    [InlineData(-1)]
    public void An_undeclared_capability_is_not_granted(int value) =>
        Assert.False(SupportSessionAccessPolicy.Allows((Capability)value));

    [Theory]
    [InlineData("GET")]
    [InlineData("get")]
    [InlineData("HEAD")]
    [InlineData("Head")]
    public void A_read_method_is_allowed(string method) =>
        Assert.True(SupportSessionAccessPolicy.IsReadMethod(method));

    /// <summary>
    /// An allowlist, not a list of refusals: a verb nobody thought of is refused by construction,
    /// and so is a request whose method cannot be read at all.
    /// </summary>
    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    [InlineData("OPTIONS")]
    [InlineData("QUERY")]
    [InlineData("GETX")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Anything_that_is_not_a_read_method_is_refused(string? method) =>
        Assert.False(SupportSessionAccessPolicy.IsReadMethod(method));
}
