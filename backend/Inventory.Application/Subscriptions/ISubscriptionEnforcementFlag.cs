namespace Inventory.Application.Subscriptions;

/// <summary>
/// Whether plan entitlement is enforced in this deployment at all (issue #966, decided 29 October
/// 2026 on #965): the <c>Subscriptions:EnforcementEnabled</c> setting, read through a port so the
/// Application layer owns the <em>decision</em> while the InventoryApi composition root owns the
/// configuration source.
///
/// <para>It follows <see cref="Onboarding.ISelfServiceOnboardingFlag"/> exactly, including why.
/// <strong>It is off unless a deployment says otherwise</strong>: an absent section, a blank value
/// and an unparseable one are all off, because the answer that must never come from a mistyped
/// setting is "start withholding features customers are using today". While it is off every
/// business is unrestricted and the application behaves exactly as it does now.</para>
///
/// <para><strong>One flag, one source.</strong> The entitlement reported by
/// <c>GET /api/me/access</c>, the startup check that the grandfathering step has run, and every
/// enforcement decision the later tasks add all read this one port. Nothing reads
/// <c>Subscriptions:EnforcementEnabled</c> any other way: a second configuration read at the API
/// boundary is exactly what would let a page lock a feature an endpoint still serves.</para>
///
/// <para>It is read once at startup rather than per request, for the reason the onboarding flag is:
/// switching plan enforcement on is a deliberate release step with a human gate in front of it
/// (#965 § Launch gate), not something that should take effect from a half-saved configuration file
/// mid-request. A request can neither see nor influence it.</para>
/// </summary>
public interface ISubscriptionEnforcementFlag
{
    /// <summary>Whether a business's plan restricts anything.</summary>
    bool IsEnabled { get; }
}
