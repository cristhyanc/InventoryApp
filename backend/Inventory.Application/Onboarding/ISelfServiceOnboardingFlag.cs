namespace Inventory.Application.Onboarding;

/// <summary>
/// Whether self-service business onboarding is switched on for this deployment (issue #541): the
/// <c>SelfServiceOnboarding:Enabled</c> setting, read through a port so the Application layer owns
/// the <em>decision</em> while the InventoryApi composition root owns the configuration source.
///
/// <para><strong>It is off unless a deployment says otherwise.</strong> Absent configuration, a
/// blank value and an unparseable one are all off, because the one answer that must never come from
/// a mistake is "yes, strangers may create tenants here". Turning it on in production is gated on
/// human verification recorded in <c>docs/tenant-rollout.md</c>, not on anything this code can
/// check.</para>
///
/// <para><strong>One flag, one source.</strong> The apply use case, the
/// <c>onboardingEnabled</c> field of <c>GET /api/me/account-state</c>, and the apply endpoint's
/// pre-binding gate (<c>InventoryApi.Http.SelfServiceOnboardingGateAttribute</c>) all read this one
/// port, so what the account-state response promises and what the apply endpoint does cannot drift
/// apart. Nothing reads <c>SelfServiceOnboarding:Enabled</c> any other way: a second source - a
/// configuration read at the API boundary, say - is exactly what would let a screen offer "Start a
/// new business" against an endpoint that answers <c>404</c>.</para>
///
/// <para>It is a value a request can neither see nor influence: no route, query, body or header
/// takes part in it, and a disabled deployment answers an apply with a bare <c>404</c> rather than
/// a message explaining that a feature exists but is off.</para>
/// </summary>
public interface ISelfServiceOnboardingFlag
{
    /// <summary>Whether a person with no membership may apply for a business themselves.</summary>
    bool IsEnabled { get; }
}
