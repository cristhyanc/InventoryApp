using System.Globalization;
using Inventory.Application.Subscriptions;

namespace InventoryApi.Bootstrap;

/// <summary>
/// One line of startup evidence about whether plan enforcement can take effect (issue #966), and
/// <strong>a startup error when it is switched on without the grandfathering step having run</strong>.
///
/// <para>It reports rather than enforces, exactly as the external-identity and price-collector
/// startup decisions beside it do. The entitlement rule already fails safe on its own - with no
/// completion marker every business is unrestricted whatever the rollout setting says - so the thing
/// missing in that state is not safety but <em>visibility</em>: a deployment that switched the
/// setting on and believes plans are being enforced has to be told, in the log, that they are not
/// and which step is missing. Refusing to start would be the wrong trade: the API would stop serving
/// a business that is working perfectly well over a plan rollout that has not begun.</para>
///
/// <para>It reads the one grandfathering row through the Application port and nothing else - no
/// business, no grant, no subscription - so it names no business and no member. It is called once,
/// from the composition root, after the schema step, and is not reachable from a request.</para>
/// </summary>
public static class SubscriptionEnforcementStartup
{
    /// <summary>The logger category this reports under.</summary>
    public const string LoggerCategory = "InventoryApi.Subscriptions";

    /// <summary>The command an operator has to run before enforcement can take effect.</summary>
    public const string GrandfatheringCommandName = BusinessPlanGrandfatheringArguments.CommandName;

    public static async Task ReportAsync(
        ISubscriptionEnforcementFlag enforcement,
        ISubscriptionGrandfatheringStore grandfathering,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(enforcement);
        ArgumentNullException.ThrowIfNull(grandfathering);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        var logger = loggerFactory.CreateLogger(LoggerCategory);

        if (!enforcement.IsEnabled)
        {
            // The shipped state. Logged at information so the answer is in the log either way: a
            // deployment that thinks it switched enforcement on can see that it did not.
            logger.LogInformation(
                "Subscription plan enforcement is off ({Setting} is not true). Every business is "
                    + "unrestricted and behaves exactly as it did before plans existed.",
                ConfiguredSubscriptionEnforcementFlagKey);
            return;
        }

        var record = await grandfathering.FindAsync(cancellationToken).ConfigureAwait(false);

        if (record is { IsComplete: true })
        {
            logger.LogInformation(
                "Subscription plan enforcement is on. The grandfathering step completed at "
                    + "{CompletedAtUtc} with a cutoff of {CutoffUtc} and {GrantedBusinessCount} "
                    + "grandfathered business(es).",
                record.CompletedAtUtc?.ToString("u", CultureInfo.InvariantCulture),
                record.CutoffUtc.ToString("u", CultureInfo.InvariantCulture),
                record.GrantedBusinessCount);
            return;
        }

        // The state this check exists for: the setting is on, so somebody intends plans to be
        // enforced, and the step that gives every established business its grant has not finished.
        // Enforcement stays off for everybody until it has.
        logger.LogError(
            "Subscription plan enforcement is switched on ({Setting}) but the grandfathering step "
                + "has not completed ({State}), so every business is still treated as unrestricted. "
                + "Run the human-invoked `{Command}` command (dry run first) and verify its output; "
                + "enforcement takes effect only once it records its completion marker.",
            ConfiguredSubscriptionEnforcementFlagKey,
            record is null ? "it has never run" : "it started but did not finish",
            GrandfatheringCommandName);
    }

    /// <summary>
    /// The configuration key, named in the log so an operator reads the setting to change rather
    /// than guessing at one. It is the adapter's own constant; this indirection exists only because
    /// the message is built here.
    /// </summary>
    private static string ConfiguredSubscriptionEnforcementFlagKey =>
        Adapters.Subscriptions.ConfiguredSubscriptionEnforcementFlag.EnabledKey;
}
