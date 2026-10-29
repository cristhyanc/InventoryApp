using Inventory.Application.Subscriptions;

namespace InventoryApi.Adapters.Subscriptions;

/// <summary>
/// The <c>Subscriptions:EnforcementEnabled</c> setting, read once at startup and published to the
/// Application layer as <see cref="ISubscriptionEnforcementFlag"/> (issue #966, decided 29 October
/// 2026 on #965).
///
/// <para>It is <c>ConfiguredSelfServiceOnboardingFlag</c>'s shape, deliberately and in full:
/// configuration is read in this layer because only the composition root reads configuration, while
/// the <em>decision</em> stays in the Application layer, where the entitlement read and every later
/// enforcement check consume it.</para>
///
/// <para><strong>Off is the default and the fail-closed direction.</strong> An absent section, a
/// blank value and a value that is not a boolean all leave it off, because the answer that must
/// never come from a mistyped setting is "start withholding features the business is using today".
/// Reading it once at startup rather than per request is deliberate too: switching plan enforcement
/// on is a release step with a human gate in front of it (#965 § Launch gate), not something that
/// should take effect from a half-saved configuration file mid-request.</para>
/// </summary>
public sealed class ConfiguredSubscriptionEnforcementFlag : ISubscriptionEnforcementFlag
{
    /// <summary>The configuration section the flag lives in.</summary>
    public const string SectionName = "Subscriptions";

    /// <summary>The full configuration key, as a deployment sets it.</summary>
    public const string EnabledKey = $"{SectionName}:EnforcementEnabled";

    public ConfiguredSubscriptionEnforcementFlag(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        // TryParse with no fallback: a missing key, an empty value and an unparseable one all yield
        // false rather than throwing at startup or, worse, being read as true.
        IsEnabled = bool.TryParse(configuration[EnabledKey], out var enabled) && enabled;
    }

    /// <inheritdoc />
    public bool IsEnabled { get; }
}
