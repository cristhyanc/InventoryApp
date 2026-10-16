using Inventory.Application.Access;
using Inventory.Application.Tenancy;
using Inventory.Domain.Tenancy;
using InventoryApi.Tests.Application.Gst;
using InventoryApi.Tests.Application.SupportSessions;
using Xunit;

namespace InventoryApi.Tests.Application.Access;

/// <summary>
/// <see cref="CurrentCapabilities"/> under a Super Admin's role preview (issue #720): the one place
/// the narrowing happens, so that the capability policies, <c>GET /api/me/access</c> and the
/// financial redaction all follow the previewed role without a rule of their own.
///
/// <para>Every case here is about the <em>intersection</em>. The base is resolved first and then
/// narrowed, so a preview can only ever remove: it cannot grant a capability the request did not
/// hold, and it cannot turn a caller with no usable membership into one with access.</para>
/// </summary>
public class CurrentCapabilitiesRolePreviewTests
{
    /// <summary>
    /// The criterion for an administrator who is an active Owner of their own business: each
    /// preview role answers exactly that role's published set.
    /// </summary>
    [Theory]
    [InlineData(BusinessRole.Owner)]
    [InlineData(BusinessRole.Manager)]
    [InlineData(BusinessRole.Operator)]
    public async Task A_members_capabilities_are_narrowed_to_the_previewed_role(BusinessRole preview)
    {
        var capabilities = ForMember(BusinessRole.Owner, FakeRolePreviewAccessor.Previewing(preview));

        Assert.Equal(RoleCapabilities.For(preview), await capabilities.ListAsync(CancellationToken.None));
    }

    /// <summary>
    /// The base is the caller's <em>own</em> role, not the strongest one there is: a Manager
    /// previewing an Owner is still answered a Manager's capabilities, because the intersection has
    /// nothing wider to draw on. This is what makes the header harmless to honour.
    /// </summary>
    [Fact]
    public async Task A_preview_of_a_wider_role_grants_the_callers_own_capabilities_and_no_more()
    {
        var capabilities = ForMember(
            BusinessRole.Manager, FakeRolePreviewAccessor.Previewing(BusinessRole.Owner));

        Assert.Equal(
            RoleCapabilities.For(BusinessRole.Manager),
            await capabilities.ListAsync(CancellationToken.None));
    }

    /// <summary>
    /// The criterion for a request inside a support session: the base is the session's fixed set,
    /// and previewing a Manager leaves <c>Suppliers.ManageGstDefaults</c> absent because the
    /// session never held it.
    /// </summary>
    [Fact]
    public async Task A_support_sessions_set_is_narrowed_to_the_previewed_role()
    {
        var capabilities = InSupportSession(FakeRolePreviewAccessor.Previewing(BusinessRole.Manager));

        var listed = await capabilities.ListAsync(CancellationToken.None);

        Assert.Equal(
            SupportSessionAccessPolicy.Capabilities
                .Where(capability => RoleCapabilities.Allows(BusinessRole.Manager, capability)),
            listed);
        Assert.DoesNotContain(Capability.SuppliersManageGstDefaults, listed);
        Assert.Contains(Capability.ReportsView, listed);
    }

    [Fact]
    public async Task A_support_session_previewing_an_operator_loses_the_managers_capabilities()
    {
        var capabilities = InSupportSession(FakeRolePreviewAccessor.Previewing(BusinessRole.Operator));

        var listed = await capabilities.ListAsync(CancellationToken.None);

        Assert.Equal(
            [
                Capability.DashboardView,
                Capability.PickListView,
                Capability.InventoryOperate,
                Capability.PurchasingOperate,
            ],
            listed);
        Assert.DoesNotContain(Capability.DashboardViewFinancials, listed);
        Assert.DoesNotContain(Capability.ReportsView, listed);
    }

    /// <summary>
    /// The same answer through the predicate the capability policies call, for every capability in
    /// the vocabulary: enforcement and the reported list cannot disagree about a previewed request
    /// any more than they can about an ordinary one.
    /// </summary>
    [Theory]
    [InlineData(BusinessRole.Owner)]
    [InlineData(BusinessRole.Manager)]
    [InlineData(BusinessRole.Operator)]
    public async Task Allows_agrees_with_the_narrowed_list_for_every_capability(BusinessRole preview)
    {
        var capabilities = ForMember(BusinessRole.Owner, FakeRolePreviewAccessor.Previewing(preview));
        var listed = await capabilities.ListAsync(CancellationToken.None);

        foreach (var capability in Capabilities.All)
        {
            Assert.Equal(
                listed.Contains(capability),
                await capabilities.AllowsAsync(capability, CancellationToken.None));
        }
    }

    /// <summary>
    /// The criterion: a request with no header behaves exactly as before, for a member and inside a
    /// support session alike.
    /// </summary>
    [Fact]
    public async Task Without_a_preview_nothing_changes()
    {
        Assert.Equal(
            RoleCapabilities.For(BusinessRole.Manager),
            await ForMember(BusinessRole.Manager, new FakeRolePreviewAccessor())
                .ListAsync(CancellationToken.None));
        Assert.Equal(
            SupportSessionAccessPolicy.Capabilities,
            await InSupportSession(new FakeRolePreviewAccessor()).ListAsync(CancellationToken.None));
    }

    /// <summary>
    /// The criterion: an administrator with no usable membership and no session gets the same
    /// refusal with the header as without it. The base is resolved before the narrowing, so a
    /// preview never resolves access - or a business - that a membership did not.
    /// </summary>
    [Fact]
    public async Task A_preview_never_turns_an_unresolved_membership_into_access()
    {
        var capabilities = new CurrentCapabilities(
            new FakeSupportSessionAccessor(),
            FakeCurrentBusinessProvider.Denied(),
            FakeRolePreviewAccessor.Previewing(BusinessRole.Operator));

        await Assert.ThrowsAsync<BusinessAccessDeniedException>(
            () => capabilities.ListAsync(CancellationToken.None));
        await Assert.ThrowsAsync<BusinessAccessDeniedException>(
            () => capabilities.AllowsAsync(Capability.DashboardView, CancellationToken.None));
    }

    /// <summary>
    /// The second layer behind <c>RolePreviewMiddleware</c>, which refuses both of these states at
    /// the HTTP boundary before an endpoint runs. If one ever reached a capability decision anyway,
    /// it grants nothing: a preview that could not be applied must never be the reason a request
    /// was answered at full capabilities.
    /// </summary>
    [Theory]
    [InlineData(RolePreviewState.NotPermitted)]
    [InlineData(RolePreviewState.Invalid)]
    public async Task An_unusable_preview_grants_nothing(RolePreviewState state)
    {
        var capabilities = ForMember(
            BusinessRole.Owner,
            new FakeRolePreviewAccessor(new RolePreviewAccess(state, null)));

        Assert.Empty(await capabilities.ListAsync(CancellationToken.None));
        Assert.False(await capabilities.AllowsAsync(Capability.DashboardView, CancellationToken.None));
    }

    /// <summary>
    /// The preview is asked for once per request, like the support session and the membership: the
    /// real accessor re-evaluates the platform-admin policy, and several components ask in one
    /// request.
    /// </summary>
    [Fact]
    public async Task The_preview_is_resolved_through_the_memoised_accessor()
    {
        var preview = FakeRolePreviewAccessor.Previewing(BusinessRole.Operator);
        var capabilities = ForMember(BusinessRole.Owner, preview);

        _ = await capabilities.ListAsync(CancellationToken.None);

        Assert.Equal(1, preview.CallCount);
    }

    private static CurrentCapabilities ForMember(BusinessRole role, FakeRolePreviewAccessor preview) =>
        new(
            new FakeSupportSessionAccessor(),
            new FakeCurrentBusinessProvider(businessId: 1, role),
            preview);

    private static CurrentCapabilities InSupportSession(FakeRolePreviewAccessor preview) =>
        new(
            FakeSupportSessionAccessor.Active(businessId: 1),
            FakeCurrentBusinessProvider.SupportSession(businessId: 1),
            preview);
}
