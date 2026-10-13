using System.Security.Claims;
using Inventory.Application.Access;
using Inventory.Application.Tenancy;
using Inventory.Domain.Tenancy;
using InventoryApi.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InventoryApi.Tests.Auth;

/// <summary>
/// The capability handler of issue #525, at the point the decision is made rather than over HTTP.
///
/// <para>What these tests pin is where the answer comes from and which way it fails. The handler
/// reads the caller's role from the request's own memoised membership resolution and grants only
/// what <c>RoleCapabilities</c> grants; every other outcome - no authenticated caller, a membership
/// that did not resolve for any reason, a role that does not hold the capability - leaves the
/// requirement unmet, which is a refusal and never a fallback to the least-privileged role.</para>
///
/// <para>The handler deliberately takes no <c>BusinessScope</c> and reads none, so it cannot pass
/// because <c>BusinessScopeMiddleware</c> has already run - the middleware runs <em>after</em> the
/// authorization middleware, so a handler that depended on it would be able to succeed before any
/// membership had been checked. <see cref="It_resolves_the_membership_itself_rather_than_reading_a_published_scope"/>
/// holds that independence in place.</para>
/// </summary>
public class BusinessCapabilityAuthorizationHandlerTests
{
    private static readonly BusinessId Business = BusinessId.From(7);

    /// <summary>
    /// The granting direction, for one capability per role boundary: an Operator holds the four
    /// operational capabilities, a Manager the financial ones as well, an Owner the configuration
    /// ones too.
    /// </summary>
    [Theory]
    [InlineData(BusinessRole.Operator, Capability.InventoryOperate)]
    [InlineData(BusinessRole.Operator, Capability.PurchasingOperate)]
    [InlineData(BusinessRole.Operator, Capability.DashboardView)]
    [InlineData(BusinessRole.Operator, Capability.PickListView)]
    [InlineData(BusinessRole.Manager, Capability.InventoryOperate)]
    [InlineData(BusinessRole.Manager, Capability.SuppliersManageGstDefaults)]
    [InlineData(BusinessRole.Manager, Capability.ReportsView)]
    [InlineData(BusinessRole.Owner, Capability.SuppliersManageGstDefaults)]
    [InlineData(BusinessRole.Owner, Capability.BusinessManage)]
    public async Task A_role_that_holds_the_capability_satisfies_the_requirement(
        BusinessRole role,
        Capability capability)
    {
        var context = await HandleAsync(
            capability,
            BusinessMembershipResolution.Resolved(Business, role));

        Assert.True(context.HasSucceeded);
    }

    /// <summary>
    /// The refusing direction. Each row is a capability the agreed table withholds from that role,
    /// including the one this slice enforces on an endpoint - an Operator may record a purchase but
    /// may not configure a supplier's GST defaults.
    /// </summary>
    [Theory]
    [InlineData(BusinessRole.Operator, Capability.SuppliersManageGstDefaults)]
    [InlineData(BusinessRole.Operator, Capability.DashboardViewFinancials)]
    [InlineData(BusinessRole.Operator, Capability.ReportsView)]
    [InlineData(BusinessRole.Operator, Capability.BusinessManage)]
    [InlineData(BusinessRole.Manager, Capability.IntegrationManage)]
    [InlineData(BusinessRole.Manager, Capability.DataRepair)]
    [InlineData(BusinessRole.Manager, Capability.MembersManage)]
    [InlineData(BusinessRole.Manager, Capability.BusinessManage)]
    public async Task A_role_that_does_not_hold_the_capability_leaves_the_requirement_unmet(
        BusinessRole role,
        Capability capability)
    {
        var context = await HandleAsync(
            capability,
            BusinessMembershipResolution.Resolved(Business, role));

        Assert.False(context.HasSucceeded);
    }

    /// <summary>
    /// Every way a membership can fail to resolve denies the capability. None of them is read as a
    /// role: an unresolved membership has no capabilities at all, which is the same answer the
    /// business scope gives for the same caller.
    /// </summary>
    [Theory]
    [InlineData(BusinessAccessDenialReason.MembershipMissing)]
    [InlineData(BusinessAccessDenialReason.MembershipInactive)]
    [InlineData(BusinessAccessDenialReason.MembershipAmbiguous)]
    [InlineData(BusinessAccessDenialReason.BusinessInactive)]
    [InlineData(BusinessAccessDenialReason.RoleUnrecognised)]
    [InlineData(BusinessAccessDenialReason.UnidentifiableActor)]
    [InlineData(BusinessAccessDenialReason.NotAuthenticated)]
    public async Task A_membership_that_does_not_resolve_leaves_the_requirement_unmet(
        BusinessAccessDenialReason reason)
    {
        var context = await HandleAsync(
            Capability.InventoryOperate,
            BusinessMembershipResolution.Denied(reason));

        Assert.False(context.HasSucceeded);
    }

    /// <summary>
    /// An unauthenticated caller is refused without a membership lookup. The authentication
    /// middleware answers them <c>401</c>; querying the membership table for an identity nothing
    /// can be recorded against would only cost a round trip and could not change the answer.
    /// </summary>
    [Fact]
    public async Task An_unauthenticated_caller_is_refused_without_a_membership_lookup()
    {
        var provider = new RecordingCurrentBusinessProvider(
            BusinessMembershipResolution.Resolved(Business, BusinessRole.Owner));

        var context = await HandleAsync(
            Capability.InventoryOperate,
            provider,
            new ClaimsPrincipal(new ClaimsIdentity()));

        Assert.False(context.HasSucceeded);
        Assert.Equal(0, provider.Resolutions);
    }

    /// <summary>
    /// The handler asks <see cref="ICurrentBusinessProvider"/> itself rather than reading a
    /// <c>BusinessScope</c> the middleware published, so it is not relying on pipeline order: it
    /// succeeds for a member even though nothing has resolved a scope for this request, and it
    /// refuses a non-member for the same reason. Were it reading a published scope, the first case
    /// would fail and - far worse - a rearranged pipeline could make the second one pass.
    /// </summary>
    [Fact]
    public async Task It_resolves_the_membership_itself_rather_than_reading_a_published_scope()
    {
        var scopeNobodyResolved = new BusinessScope();
        var provider = new RecordingCurrentBusinessProvider(
            BusinessMembershipResolution.Resolved(Business, BusinessRole.Operator));

        var context = await HandleAsync(Capability.InventoryOperate, provider);

        Assert.True(context.HasSucceeded);
        Assert.Equal(1, provider.Resolutions);
        Assert.Null(scopeNobodyResolved.BusinessId);
    }

    /// <summary>
    /// Two requirements in one evaluation - the shape a controller-level capability plus an
    /// action-level one produces - are answered independently, and the unmet one is enough to
    /// refuse the endpoint. This is what makes narrowing a single action additive.
    /// </summary>
    [Fact]
    public async Task One_unmet_requirement_is_enough_to_refuse_an_endpoint_that_declares_two()
    {
        var operatorMember = BusinessMembershipResolution.Resolved(Business, BusinessRole.Operator);
        var requirements = new IAuthorizationRequirement[]
        {
            new BusinessCapabilityRequirement(Capability.PurchasingOperate),
            new BusinessCapabilityRequirement(Capability.SuppliersManageGstDefaults),
        };
        var context = new AuthorizationHandlerContext(requirements, AuthenticatedCaller(), resource: null);
        var handler = Handler(new RecordingCurrentBusinessProvider(operatorMember));

        await handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
        Assert.Contains(
            context.PendingRequirements.OfType<BusinessCapabilityRequirement>(),
            requirement => requirement.Capability == Capability.SuppliersManageGstDefaults);
        Assert.DoesNotContain(
            context.PendingRequirements.OfType<BusinessCapabilityRequirement>(),
            requirement => requirement.Capability == Capability.PurchasingOperate);
    }

    /// <summary>
    /// The policy name is the published capability name behind the fixed prefix, which is what lets
    /// the endpoint-classification test and a reader of an endpoint's metadata tell a capability
    /// policy from any other named policy. An undeclared capability names no policy at all.
    /// </summary>
    [Fact]
    public void A_policy_name_is_the_published_capability_name_behind_the_fixed_prefix()
    {
        Assert.Equal("Capability:Inventory.Operate", CapabilityPolicy.Name(Capability.InventoryOperate));
        Assert.Equal(
            "Capability:Suppliers.ManageGstDefaults",
            CapabilityPolicy.Name(Capability.SuppliersManageGstDefaults));
        Assert.Equal(
            Capabilities.All.Count,
            Capabilities.All.Select(CapabilityPolicy.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.Throws<ArgumentOutOfRangeException>(() => CapabilityPolicy.Name((Capability)0));
    }

    /// <summary>
    /// The attribute is how a controller declares a capability, and it carries the matching policy
    /// name so the authorization middleware evaluates the right one. An undeclared capability is
    /// refused at construction, which means a controller naming one fails startup rather than
    /// silently requiring a policy nothing registered.
    /// </summary>
    [Fact]
    public void The_attribute_carries_the_policy_name_of_the_capability_it_declares()
    {
        var attribute = new RequireCapabilityAttribute(Capability.PickListView);

        Assert.Equal(Capability.PickListView, attribute.Capability);
        Assert.Equal(CapabilityPolicy.Name(Capability.PickListView), attribute.Policy);
        Assert.Throws<ArgumentOutOfRangeException>(() => new RequireCapabilityAttribute((Capability)99));
    }

    private static async Task<AuthorizationHandlerContext> HandleAsync(
        Capability capability,
        BusinessMembershipResolution resolution) =>
        await HandleAsync(capability, new RecordingCurrentBusinessProvider(resolution));

    private static async Task<AuthorizationHandlerContext> HandleAsync(
        Capability capability,
        RecordingCurrentBusinessProvider provider,
        ClaimsPrincipal? user = null)
    {
        var requirement = new BusinessCapabilityRequirement(capability);
        var context = new AuthorizationHandlerContext(
            [requirement],
            user ?? AuthenticatedCaller(),
            resource: null);

        await Handler(provider).HandleAsync(context);

        return context;
    }

    private static BusinessCapabilityAuthorizationHandler Handler(ICurrentBusinessProvider provider) =>
        new(
            provider,
            new HttpContextAccessor { HttpContext = new DefaultHttpContext() },
            NullLogger<BusinessCapabilityAuthorizationHandler>.Instance);

    private static ClaimsPrincipal AuthenticatedCaller() =>
        new(new ClaimsIdentity(
            [new Claim("tid", "11111111-1111-1111-1111-111111111111"), new Claim("oid", "22222222-2222-2222-2222-222222222222")],
            authenticationType: "Test"));

    /// <summary>
    /// Returns one fixed resolution and counts how often it was asked, so "the role comes from the
    /// request's memoised resolution" and "an anonymous caller costs no lookup" are both assertable.
    /// </summary>
    private sealed class RecordingCurrentBusinessProvider(BusinessMembershipResolution resolution)
        : ICurrentBusinessProvider
    {
        public int Resolutions { get; private set; }

        public Task<BusinessMembershipResolution> ResolveAsync(CancellationToken cancellationToken)
        {
            Resolutions++;
            return Task.FromResult(resolution);
        }

        public Task<BusinessId> RequireBusinessIdAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<BusinessRole> RequireRoleAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
