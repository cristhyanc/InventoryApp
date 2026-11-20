using Inventory.Application.Subscriptions;

namespace InventoryApi.Adapters.Subscriptions;

/// <summary>
/// The <c>Subscriptions:BetaProGrantOnApproval</c> setting, read once at startup and published to the
/// Application layer as <see cref="IBetaProGrantOnApprovalFlag"/> (issue #1258).
///
/// <para>It is <see cref="ConfiguredSubscriptionEnforcementFlag"/>'s shape, deliberately and in full:
/// configuration is read in this layer because only the composition root reads configuration, while
/// the <em>decision</em> stays in the Application layer, where the approval consumes it. It is a
/// separate setting from enforcement because the two are switched on at different times - the beta
/// grants have to exist before enforcement starts, or the businesses holding them would be enforced
/// down to Free.</para>
///
/// <para><strong>Off is the default and the fail-closed direction.</strong> An absent section, a
/// blank value and a value that is not a boolean all leave it off, so a mistyped setting can never
/// start writing a plan grant for every business that is approved; <c>false</c> is what is in source
/// control. Reading it once at startup rather than per request is deliberate too: switching it on is
/// a release step, and switching it off again stops new grants only - a grant already written stays
/// until a Super Admin ends it (issue #982).</para>
/// </summary>
public sealed class ConfiguredBetaProGrantOnApprovalFlag : IBetaProGrantOnApprovalFlag
{
    /// <summary>The full configuration key, as a deployment sets it.</summary>
    public const string EnabledKey =
        $"{ConfiguredSubscriptionEnforcementFlag.SectionName}:BetaProGrantOnApproval";

    public ConfiguredBetaProGrantOnApprovalFlag(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        // TryParse with no fallback: a missing key, an empty value and an unparseable one all yield
        // false rather than throwing at startup or, worse, being read as true.
        IsEnabled = bool.TryParse(configuration[EnabledKey], out var enabled) && enabled;
    }

    /// <inheritdoc />
    public bool IsEnabled { get; }
}
