namespace InventoryApi.Auth.PriceCollector;

/// <summary>
/// Marks an endpoint as part of the price collector surface (issue #924, parent #817): class 5 of
/// the endpoint authorization classes, and the only one that is not reached by a person.
///
/// <para><strong>It grants nothing by itself, and both halves are required.</strong> Like the
/// platform-admin marker it is paired with a named policy -
/// <c>[Authorize(Policy = PriceCollectorPolicy.Name, AuthenticationSchemes =
/// PriceCollectorBearerScheme.Name)]</c> - and <c>EndpointAuthorizationClassificationTests</c>
/// asserts that a marked endpoint carries the policy and that a policy-carrying endpoint carries the
/// marker. The marker is what <see cref="BusinessScopeMiddleware"/> looks for in order to let the
/// request past the membership requirement, and it does so only after re-evaluating that same policy
/// on that same request: a bypass that depended on middleware ordering would be one pipeline edit
/// away from applying to a caller nobody authorised.</para>
///
/// <para><strong>A marked endpoint runs with a denied <c>BusinessScope</c>.</strong> The collector is
/// not an actor and has no business, so there is nothing to resolve: the tenant query filters and
/// <c>BusinessOwnershipEnforcer</c> stay fully in force, and the only tenant-owned data a collector
/// endpoint may reach is the business-free search text of <c>IPriceCatalogueWorkListStore</c>'s
/// reviewed cross-business read. Nothing else tenant-owned is readable or writable through it.</para>
///
/// <para><strong>A marked endpoint carries no capability and no delegated scope.</strong> A
/// capability is a business role's power, and the collector holds no role in any business; a
/// delegated scope is a person's consent, and there is no person. Those absences are asserted rather
/// than assumed, because a collector endpoint that also declared a capability would be inviting a
/// business role onto a cross-business surface.</para>
///
/// <para>Marking an endpoint is the exemption itself, and the route is recorded in that test's own
/// allowlist, so taking it is a reviewed change in its own pull request. Never mark an endpoint that
/// serves a business's own data, and never use the marker to make an endpoint anonymous.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class PriceCollectorEndpointAttribute : Attribute;
