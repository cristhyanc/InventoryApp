namespace InventoryApi.Auth;

/// <summary>
/// Marks an endpoint that a signed-in person may reach without a business membership (issue #523).
///
/// <para>It exists for one family of endpoints: the ones a person who has <em>no</em> usable
/// membership has to be able to call - today <c>GET /api/me/account-state</c>, which tells them
/// which screen to show instead of a bare <c>403</c>, and later the invite redemption of issue
/// #504 and the application endpoints of issue #507. <see cref="BusinessScopeMiddleware"/> honours
/// it only on the endpoint carrying it.</para>
///
/// <para><strong>It grants nothing.</strong> A marked request still requires authentication and
/// the delegated <c>access_as_user</c> scope, and it runs with a <em>denied</em>
/// <c>BusinessScope</c> - never an unscoped one - so the tenant query filters and
/// <c>BusinessOwnershipEnforcer</c> stay fully in force and a marked endpoint reads no business
/// data at all through <c>AppDbContext</c>. The marker removes the membership
/// <em>requirement</em>; it does not supply a business, and nothing downstream may treat it as
/// one. On an unmarked endpoint it changes nothing whatsoever.</para>
///
/// <para>Which endpoints may carry it is reviewed rather than assumed:
/// <c>MembershipNotRequiredEndpointAllowlistTests</c> fails the build when the marker appears on
/// an endpoint that is not on its allowlist, so taking the exemption is a reviewed change. Issue
/// #502 folds that allowlist into its endpoint-classification test as class 3. Do not mark an
/// endpoint that serves business data, and do not infer the exemption from pipeline ordering.</para>
///
/// <para>It is deliberately a separate marker from
/// <see cref="PlatformAdmin.PlatformDiagnosticsEndpointAttribute"/> and is not an alternative to
/// it: the diagnostics bypass is for a configured platform administrator and is granted only after
/// the middleware re-evaluates <c>PlatformAdminPolicy</c> on the request, while this one asks no
/// policy because the answer it protects is the caller's own account state. An endpoint that
/// carries both stays a diagnostics endpoint, decided by that policy.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class MembershipNotRequiredEndpointAttribute : Attribute;
