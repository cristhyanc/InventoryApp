using System.Globalization;
using Inventory.Application.PriceCatalogue;
using Inventory.Domain.PriceCatalogue;
using InventoryApi.Auth.PriceCollector;

namespace InventoryApi.Adapters.PriceCatalogue;

/// <summary>
/// How long an issued collector run stays open, read once from the <c>PriceCollector</c> section
/// (issue #924, parent #817).
///
/// <para>It is the composition root's half of <see cref="IPriceCollectorRunSettings"/>: only this
/// layer reads configuration, and the Application layer owns what the value <em>means</em> - the
/// arrangement <c>ConfiguredSelfServiceOnboardingFlag</c> already uses for the onboarding flag.</para>
///
/// <para><strong>It falls back to the owner's default, never to "no expiry".</strong> A blank value,
/// an unparseable one and one outside the range <see cref="PriceCollectorRunPolicy"/> accepts are all
/// <see cref="PriceCollectorRunPolicy.DefaultExpiryHours"/>, and the fallback is logged once at
/// startup because a deployment that set the value intended something that is not happening. Parsing
/// is invariant, so a host's locale cannot change what a configured number means.</para>
/// </summary>
public sealed class ConfiguredPriceCollectorRunSettings : IPriceCollectorRunSettings
{
    public ConfiguredPriceCollectorRunSettings(IConfiguration configuration, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);

        var configured = configuration
            .GetSection(PriceCollectorOptions.SectionName)[nameof(PriceCollectorOptions.RunExpiryHours)]
            ?.Trim();

        if (string.IsNullOrEmpty(configured))
        {
            RunExpiryHours = PriceCollectorRunPolicy.DefaultExpiryHours;
            return;
        }

        if (int.TryParse(configured, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hours)
            && PriceCollectorRunPolicy.IsAcceptedExpiry(hours))
        {
            RunExpiryHours = hours;
            return;
        }

        RunExpiryHours = PriceCollectorRunPolicy.DefaultExpiryHours;

        // The configured value is deliberately not logged: it is not a secret, but naming the
        // setting and the fallback is what an operator needs, and echoing submitted configuration
        // into a log is a habit this repository does not keep.
        logger.LogError(
            "{Setting} is not a whole number of hours in the accepted range, so issued price collector runs use the default lifetime of {DefaultExpiryHours} hours.",
            $"{PriceCollectorOptions.SectionName}:{nameof(PriceCollectorOptions.RunExpiryHours)}",
            PriceCollectorRunPolicy.DefaultExpiryHours);
    }

    /// <inheritdoc />
    public int RunExpiryHours { get; }
}
