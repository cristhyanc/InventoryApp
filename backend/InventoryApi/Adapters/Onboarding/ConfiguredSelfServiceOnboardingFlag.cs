using Inventory.Application.Onboarding;

namespace InventoryApi.Adapters.Onboarding;

/// <summary>
/// The <c>SelfServiceOnboarding:Enabled</c> setting, read once at startup and published to the
/// Application layer as <see cref="ISelfServiceOnboardingFlag"/> (issue #541).
///
/// <para>Configuration is read in this layer because only the composition root reads configuration
/// - the same rule the Nayax client's options and the document storage options follow - while the
/// <em>decision</em> the flag feeds stays in the Application layer, where the apply endpoint and
/// <c>GET /api/me/account-state</c> both read it.</para>
///
/// <para><strong>Off is the default and the fail-closed direction.</strong> An absent section, a
/// blank value and a value that is not a boolean all leave it off, because the one answer that must
/// never come from a mistyped setting is "yes, strangers may create tenants in this deployment".
/// Reading it once at startup rather than per request is deliberate too: switching it on is a
/// deliberate deployment change with a human-verified release gate behind it
/// (docs/tenant-rollout.md), not something that should take effect from a half-saved configuration
/// file mid-request.</para>
/// </summary>
public sealed class ConfiguredSelfServiceOnboardingFlag : ISelfServiceOnboardingFlag
{
    /// <summary>The configuration section the flag lives in.</summary>
    public const string SectionName = "SelfServiceOnboarding";

    /// <summary>The full configuration key, as a deployment sets it.</summary>
    public const string EnabledKey = $"{SectionName}:Enabled";

    public ConfiguredSelfServiceOnboardingFlag(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        // GetValue with an explicit default: a missing key, an empty value and an unparseable one
        // all yield false rather than throwing at startup or, worse, being read as true.
        IsEnabled = bool.TryParse(configuration[EnabledKey], out var enabled) && enabled;
    }

    /// <inheritdoc />
    public bool IsEnabled { get; }
}
