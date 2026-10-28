namespace Inventory.Application.PriceCatalogue;

/// <summary>
/// How long an issued collector run stays open for this deployment (issue #924): the
/// <c>PriceCollector:RunExpiryHours</c> setting, read through a port so the Application layer owns
/// the <em>decision</em> while the InventoryApi composition root owns the configuration source - the
/// arrangement <see cref="Onboarding.ISelfServiceOnboardingFlag"/> already uses.
///
/// <para><strong>It falls back to the owner's default, never to "no expiry".</strong> Absent
/// configuration, a blank value, an unparseable one and one outside the range
/// <c>PriceCollectorRunPolicy</c> accepts all yield
/// <c>PriceCollectorRunPolicy.DefaultExpiryHours</c>, because the one answer that must never come
/// from a mistake is "this run stays open forever". The value a deployment sets is read once at
/// startup: it is a configured lifetime, not a per-request value, and no route, query, body or
/// header takes part in it.</para>
/// </summary>
public interface IPriceCollectorRunSettings
{
    /// <summary>How many hours an issued run stays open unless the collector reports back.</summary>
    int RunExpiryHours { get; }
}
