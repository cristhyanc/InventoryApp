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
/// <para><strong>One flag, read in one layer.</strong> Both the apply endpoint and the
/// <c>onboardingEnabled</c> field of <c>GET /api/me/account-state</c> read it here, so what the
/// account-state response promises and what the apply endpoint does cannot drift apart - a screen
/// that offers "Start a new business" against an endpoint that answers <c>404</c> is exactly the
/// failure a second read at the API boundary would eventually produce.</para>
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
