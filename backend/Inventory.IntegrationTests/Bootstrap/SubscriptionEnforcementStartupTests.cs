using InventoryApi.Adapters.Subscriptions;
using InventoryApi.Bootstrap;
using InventoryApi.Tests.Application.Subscriptions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace InventoryApi.Tests.Bootstrap;

/// <summary>
/// The startup check of issue #966: <strong>enforcement switched on without the grandfathering step
/// having completed is a startup error</strong>, naming the missing step.
///
/// <para>The entitlement rule already fails safe on its own - with no completion marker every
/// business is unrestricted - so what this check adds is visibility: a deployment that set the
/// rollout setting and believes plans are being enforced has to be told, in the log, that they are
/// not. That is the one thing worth asserting here, together with the other half of it: it reports
/// and never refuses to start, because an API that stopped serving a working business over a plan
/// rollout that has not begun would be the wrong trade.</para>
/// </summary>
public class SubscriptionEnforcementStartupTests
{
    private static readonly DateTime Cutoff = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task With_enforcement_off_it_reports_that_nothing_is_enforced_and_logs_no_error()
    {
        var logs = await ReportAsync(
            FakeSubscriptionEnforcementFlag.Disabled,
            new FakeSubscriptionGrandfatheringStore());

        Assert.Contains(logs.Entries, entry => entry.Level == LogLevel.Information);
        Assert.DoesNotContain(logs.Entries, entry => entry.Level >= LogLevel.Error);
        Assert.Contains("unrestricted", logs.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(ConfiguredSubscriptionEnforcementFlag.EnabledKey, logs.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The criterion: the setting is on and no completion marker exists, so this is an error that
    /// names the command to run and says that every business is still unrestricted.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task With_enforcement_on_and_the_step_unfinished_it_logs_an_error_naming_the_command(
        bool started)
    {
        var logs = await ReportAsync(
            FakeSubscriptionEnforcementFlag.Enabled,
            started
                ? FakeSubscriptionGrandfatheringStore.Started(Cutoff)
                : new FakeSubscriptionGrandfatheringStore());

        var error = Assert.Single(logs.Entries.Where(entry => entry.Level >= LogLevel.Error));

        Assert.Equal(LogLevel.Error, error.Level);
        Assert.Contains(
            SubscriptionEnforcementStartup.GrandfatheringCommandName,
            error.Message,
            StringComparison.Ordinal);
        Assert.Contains("unrestricted", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            started ? "did not finish" : "never run",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_the_step_completed_it_reports_the_cutoff_and_logs_no_error()
    {
        var logs = await ReportAsync(
            FakeSubscriptionEnforcementFlag.Enabled,
            FakeSubscriptionGrandfatheringStore.Completed(Cutoff, grantedBusinessCount: 4));

        Assert.DoesNotContain(logs.Entries, entry => entry.Level >= LogLevel.Error);
        Assert.Contains("2026-11-01 00:00:00Z", logs.Text, StringComparison.Ordinal);
        Assert.Contains("4", logs.Text, StringComparison.Ordinal);
    }

    private static async Task<RecordingLoggerProvider> ReportAsync(
        FakeSubscriptionEnforcementFlag flag,
        FakeSubscriptionGrandfatheringStore grandfathering)
    {
        using var provider = new RecordingLoggerProvider();
        using var factory = LoggerFactory.Create(builder => builder
            .SetMinimumLevel(LogLevel.Trace)
            .AddProvider(provider));

        await SubscriptionEnforcementStartup.ReportAsync(
            flag,
            grandfathering,
            factory,
            CancellationToken.None);

        return provider;
    }

    /// <summary>Collects the level and the formatted message of every log entry.</summary>
    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public string Text => string.Join('\n', Entries.Select(entry => entry.Message));

        public ILogger CreateLogger(string categoryName) => new Recording(this);

        public void Dispose()
        {
        }

        private sealed class Recording(RecordingLoggerProvider provider) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                ArgumentNullException.ThrowIfNull(formatter);
                provider.Entries.Add((logLevel, formatter(state, exception)));
            }
        }
    }
}
