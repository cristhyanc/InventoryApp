using Inventory.Application.Access;
using Inventory.Domain.Tenancy;
using Xunit;

namespace InventoryApi.Tests.Application.Access;

/// <summary>
/// The rules of a role preview (issue #720): which presented values name a role, which methods a
/// previewed request may use, and that narrowing can only ever remove.
/// </summary>
public class RolePreviewPolicyTests
{
    [Theory]
    [InlineData("Owner", BusinessRole.Owner)]
    [InlineData("Manager", BusinessRole.Manager)]
    [InlineData("Operator", BusinessRole.Operator)]
    public void An_exact_published_role_name_names_that_role(string value, BusinessRole expected)
    {
        Assert.True(RolePreviewPolicy.TryParseRole(value, out var role));
        Assert.Equal(expected, role);
    }

    /// <summary>
    /// The criterion: anything that is not exactly one of the three names is refused rather than
    /// interpreted - a different case, a stored value, a role that does not exist, a blank value,
    /// surrounding whitespace, and the comma-joined value a repeated header collapses to.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("owner")]
    [InlineData("OWNER")]
    [InlineData("oPerator")]
    [InlineData("20")]
    [InlineData("40")]
    [InlineData("Viewer")]
    [InlineData("SuperAdmin")]
    [InlineData(" Owner")]
    [InlineData("Owner ")]
    [InlineData("Owner,Manager")]
    [InlineData("Owner, Manager")]
    public void Anything_else_names_no_role(string? value)
    {
        Assert.False(RolePreviewPolicy.TryParseRole(value, out var role));
        Assert.False(BusinessRoles.IsSupported(role));
    }

    /// <summary>
    /// The three declared member roles and nothing else may be previewed: a support session's fixed
    /// set is not a role, and there is no custom set.
    /// </summary>
    [Fact]
    public void The_previewable_roles_are_the_three_declared_member_roles()
    {
        Assert.Equal(
            [BusinessRole.Operator, BusinessRole.Manager, BusinessRole.Owner],
            RolePreviewPolicy.PreviewableRoles);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("get")]
    [InlineData("HEAD")]
    [InlineData("Head")]
    public void A_read_method_is_allowed(string method) =>
        Assert.True(RolePreviewPolicy.IsReadMethod(method));

    /// <summary>
    /// An allowlist, so a verb nobody thought of - and a method that cannot be read at all - is
    /// refused by construction rather than by having been listed.
    /// </summary>
    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    [InlineData("QUERY")]
    [InlineData("OPTIONS")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public void Every_other_method_is_refused(string? method) =>
        Assert.False(RolePreviewPolicy.IsReadMethod(method));

    /// <summary>
    /// The criterion: narrowing an Owner's own capabilities to each preview role answers exactly
    /// that role's published set, in the published order.
    /// </summary>
    [Theory]
    [InlineData(BusinessRole.Owner)]
    [InlineData(BusinessRole.Manager)]
    [InlineData(BusinessRole.Operator)]
    public void Narrowing_an_owners_capabilities_answers_the_previewed_roles_own_set(BusinessRole preview) =>
        Assert.Equal(
            RoleCapabilities.For(preview),
            RolePreviewPolicy.Narrow(RoleCapabilities.For(BusinessRole.Owner), preview));

    /// <summary>
    /// The guarantee the whole feature rests on, over every base/preview pair there is: the answer
    /// is always a subset of the base. A preview can take access away and can never add any.
    /// </summary>
    [Fact]
    public void Narrowing_never_adds_a_capability_the_base_did_not_hold()
    {
        IReadOnlyList<IReadOnlyList<Capability>> bases =
        [
            .. BusinessRoles.All.Select(RoleCapabilities.For),
            SupportSessionAccessPolicy.Capabilities,
            [],
        ];

        foreach (var baseCapabilities in bases)
        {
            foreach (var preview in RolePreviewPolicy.PreviewableRoles)
            {
                var narrowed = RolePreviewPolicy.Narrow(baseCapabilities, preview);

                Assert.Empty(narrowed.Except(baseCapabilities));
                Assert.Empty(narrowed.Except(RoleCapabilities.For(preview)));
                // And the base's own order is kept, which is what makes the reported list
                // deterministic without re-sorting it.
                Assert.Equal(baseCapabilities.Where(narrowed.Contains), narrowed);
            }
        }
    }

    /// <summary>
    /// The support-session composition (issue #551 plus #720): previewing a Manager inside a
    /// session leaves <c>Suppliers.ManageGstDefaults</c> absent, because the support set never held
    /// it - the intersection cannot restore what neither side grants.
    /// </summary>
    [Fact]
    public void Narrowing_the_support_set_to_a_manager_still_excludes_the_gst_defaults_capability()
    {
        var narrowed = RolePreviewPolicy.Narrow(
            SupportSessionAccessPolicy.Capabilities, BusinessRole.Manager);

        Assert.DoesNotContain(Capability.SuppliersManageGstDefaults, narrowed);
        Assert.Contains(Capability.SuppliersManageGstDefaults, RoleCapabilities.For(BusinessRole.Manager));
    }

    [Fact]
    public void An_undeclared_preview_role_is_refused_rather_than_granted_nothing()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => RolePreviewPolicy.Narrow(RoleCapabilities.For(BusinessRole.Owner), (BusinessRole)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => RolePreviewAccess.Active((BusinessRole)99));
    }
}
