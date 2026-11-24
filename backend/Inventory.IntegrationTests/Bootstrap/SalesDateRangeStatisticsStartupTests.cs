using Inventory.Application.Maintenance;
using Inventory.Domain.Maintenance;
using Inventory.Infrastructure;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Persistence;
using InventoryApi.Bootstrap;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace InventoryApi.Tests.Bootstrap;

/// <summary>
/// The first trigger of issue #1287's statistics policy: startup asks once, after the schema step,
/// and whatever the answer is the application starts.
///
/// <para>Two things are asserted. Every outcome is reported in a line an operator can read - what was
/// collected and how long it held the write lock, that nothing was needed, that there is too little
/// history to bother, or that it could not be done - and a failure is a <strong>warning</strong>
/// rather than an exception, because a slower query plan is not a reason to refuse to serve requests.
/// The last case goes further and uses the real adapter over a real database, so what is asserted is
/// that the deployed startup path behaves this way and not merely that a fake does.</para>
/// </summary>
public class SalesDateRangeStatisticsStartupTests
{
    [Fact]
    public async Task A_collection_is_reported_with_its_duration_and_the_bound_for_the_next_one()
    {
        var logs = await CollectAsync(new FixedSalesDateRangeStatistics(
            new SalesDateRangeStatisticsResult(
                SalesDateRangeStatisticsOutcome.Collected,
                RecordedSales: null,
                CurrentSales: 1_000_000,
                Duration: TimeSpan.FromMilliseconds(104))));

        Assert.DoesNotContain(logs.Entries, entry => entry.Level >= LogLevel.Warning);
        Assert.Contains(SqliteSalesDateRangeStatistics.IndexName, logs.Text, StringComparison.Ordinal);
        Assert.Contains("1000000", logs.Text, StringComparison.Ordinal);
        Assert.Contains("104", logs.Text, StringComparison.Ordinal);
        Assert.Contains(
            SalesDateRangeStatisticsPolicy.GrowthFactor.ToString(),
            logs.Text,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Statistics_that_are_already_current_are_reported_as_no_write_lock_taken()
    {
        var logs = await CollectAsync(new FixedSalesDateRangeStatistics(
            new SalesDateRangeStatisticsResult(
                SalesDateRangeStatisticsOutcome.AlreadyCurrent,
                RecordedSales: 40_000,
                CurrentSales: 41_000)));

        Assert.DoesNotContain(logs.Entries, entry => entry.Level >= LogLevel.Warning);
        Assert.Contains("no write lock", logs.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Too_little_history_is_reported_with_the_floor_it_is_below()
    {
        var logs = await CollectAsync(new FixedSalesDateRangeStatistics(
            new SalesDateRangeStatisticsResult(
                SalesDateRangeStatisticsOutcome.NotWorthCollecting, CurrentSales: 12)));

        Assert.DoesNotContain(logs.Entries, entry => entry.Level >= LogLevel.Warning);
        Assert.Contains(
            SalesDateRangeStatisticsPolicy.MinimumSales.ToString(),
            logs.Text,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A database startup cannot read or write for this is a warning and nothing else: the real
    /// adapter over an unreachable file, so the whole deployed path is what is exercised. Startup
    /// continues, which is what "statistics are an optimisation" means in practice.
    /// </summary>
    [Fact]
    public async Task An_unreachable_database_is_a_warning_and_startup_continues()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(
                new SqliteConnectionStringBuilder
                {
                    DataSource = Path.Combine(
                        Path.GetTempPath(),
                        $"inventory-no-such-directory-{Guid.NewGuid():N}",
                        "inventory.db"),
                }.ToString())
            .Options;

        await using var db = TestAppDbContext.Unrestricted(options);

        var logs = await CollectAsync(new SqliteSalesDateRangeStatistics(db));

        var warning = Assert.Single(logs.Entries.Where(entry => entry.Level >= LogLevel.Warning));
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains(SqliteSalesDateRangeStatistics.IndexName, warning.Message, StringComparison.Ordinal);
        Assert.Contains("starts normally", warning.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The composition check the startup line would otherwise only get in production: the port
    /// startup resolves really is registered, and what satisfies it is the SQLite collection path. A
    /// missing registration would otherwise be found after a deployment, in a startup failure.
    /// </summary>
    [Fact]
    public void The_composition_root_registers_the_sqlite_collection_path()
    {
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options => options.UseSqlite("Data Source=:memory:"));
        services.AddScoped(_ => TestAppDbContext.DeniedScope());
        services.AddInfrastructureServices();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.IsType<SqliteSalesDateRangeStatistics>(
            scope.ServiceProvider.GetRequiredService<ISalesDateRangeStatistics>());
    }

    private static async Task<RecordingLogs> CollectAsync(ISalesDateRangeStatistics statistics)
    {
        var logs = new RecordingLogs();
        using var factory = LoggerFactory.Create(builder => builder
            .SetMinimumLevel(LogLevel.Trace)
            .AddProvider(logs));

        await SalesDateRangeStatisticsStartup.CollectAsync(statistics, factory, CancellationToken.None);

        return logs;
    }

    /// <summary>One given answer, however many times it is asked for.</summary>
    private sealed class FixedSalesDateRangeStatistics : ISalesDateRangeStatistics
    {
        private readonly SalesDateRangeStatisticsResult _result;

        public FixedSalesDateRangeStatistics(SalesDateRangeStatisticsResult result) => _result = result;

        public Task<SalesDateRangeStatisticsResult> CollectIfStaleAsync(
            CancellationToken cancellationToken) => Task.FromResult(_result);
    }

    /// <summary>Collects the level and the formatted message of every log entry.</summary>
    private sealed class RecordingLogs : ILoggerProvider
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public string Text => string.Join('\n', Entries.Select(entry => entry.Message));

        public ILogger CreateLogger(string categoryName) => new Recording(this);

        public void Dispose()
        {
        }

        private sealed class Recording(RecordingLogs provider) : ILogger
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
