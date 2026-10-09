using Inventory.Application.PlatformDiagnostics;
using InventoryApi.Auth.E2ETesting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace InventoryApi.Tests.Auth;

/// <summary>
/// The API hosted with a configured platform administrator, for the issue #336 tests.
///
/// <para>It uses the dedicated end-to-end host for one reason: that is the only way this suite can
/// speak as a specific Entra <c>(tid, oid)</c> actor through the complete real pipeline -
/// authentication, <c>[Authorize]</c>, <c>[RequiredScope]</c>, the platform-admin policy,
/// <c>BusinessScopeMiddleware</c>, the membership lookup, the tenant query filters - without an
/// interactive sign-in. Nothing about the authorisation under test is synthetic; only the
/// authentication is.</para>
///
/// <para><see cref="PlatformAdministrator"/> is deliberately <c>E2ETestActors.NoMembership</c>, the
/// synthetic actor the fixture never gives a business membership. That is the case the issue is
/// actually about: a platform administrator who is not a member of any business must reach the
/// diagnostics endpoints and must still be refused by every business endpoint.</para>
///
/// <para>The database is a real file rather than <c>:memory:</c>, because the diagnostics adapter
/// opens its own connection with <c>Mode=ReadOnly</c> - which is the point - and an in-memory
/// database cannot be opened that way. Startup migrates it and <c>E2ETestFixture</c> seeds the two
/// synthetic businesses into it, so the cross-business reads below have real rows from two owners
/// to find.</para>
/// </summary>
public class PlatformDiagnosticsHostFactory : WebApplicationFactory<Program>
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"inventory-diagnostics-host-{Guid.NewGuid():N}.db");

    private readonly List<string> _logMessages = [];

    /// <summary>The actor the host is configured to recognise as the platform administrator.</summary>
    public static E2ETestActor PlatformAdministrator => E2ETestActors.NoMembership;

    /// <summary>A synthetic actor who is an ordinary member of a business and nothing more.</summary>
    public static E2ETestActor BusinessMember => E2ETestActors.BusinessAOwner;

    /// <summary>
    /// Every <c>ILogger</c> message the host has written, so the audit event can be asserted on -
    /// including what it must <em>not</em> contain.
    /// </summary>
    public IReadOnlyList<string> LogMessages
    {
        get
        {
            lock (_logMessages)
            {
                return _logMessages.ToArray();
            }
        }
    }

    /// <summary>Overridden by a derived factory that needs a tighter limit than the hard maxima.</summary>
    protected virtual PlatformDiagnosticsQueryLimits? Limits => null;

    /// <summary>Overridden by the factory that proves the unconfigured host refuses everyone.</summary>
    protected virtual bool ConfigurePlatformAdministrator => true;

    public HttpClient As(E2ETestActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);

        var client = CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation(E2ETestActors.ActorHeaderName, actor.Key);
        return client;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Hosting must not depend on the process's current directory; see E2ETestHostContentRoot.
        E2ETestHostContentRoot.Pin();

        builder.UseEnvironment(E2ETestEnvironment.EnvironmentName);

        // The E2E environment is not one of the environments that migrate automatically, so the
        // disposable database is opted in with the setting that exists for exactly this case.
        builder.UseSetting("Database:AllowAutomaticMigrationUnsafeOutsideDevelopment", "true");
        builder.UseSetting(
            "ConnectionStrings:DefaultConnection",
            new SqliteConnectionStringBuilder { DataSource = _databasePath }.ToString());

        if (ConfigurePlatformAdministrator)
        {
            builder.UseSetting(
                $"{InventoryApi.Auth.PlatformAdmin.PlatformAdminOptions.SectionName}:DirectoryTenantId",
                PlatformAdministrator.DirectoryTenantId);
            builder.UseSetting(
                $"{InventoryApi.Auth.PlatformAdmin.PlatformAdminOptions.SectionName}:ObjectId",
                PlatformAdministrator.ObjectId);
        }

        builder.ConfigureServices(services =>
        {
            services.AddSingleton<ILoggerProvider>(new CapturingLoggerProvider(Capture));

            // Registered after the composition root's own registration, so this wins. It can only
            // tighten: PlatformDiagnosticsQueryLimits.Create clamps to the hard maxima.
            if (Limits is { } limits)
            {
                services.AddSingleton(limits);
            }
        });
    }

    private void Capture(string message)
    {
        lock (_logMessages)
        {
            _logMessages.Add(message);
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (!disposing)
        {
            return;
        }

        SqliteConnection.ClearAllPools();

        foreach (var path in new[] { _databasePath, $"{_databasePath}-wal", $"{_databasePath}-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private sealed class CapturingLoggerProvider(Action<string> capture) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(capture);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(Action<string> capture) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                capture(formatter(state, exception));
        }
    }
}

/// <summary>
/// The same host with the response byte budget tightened, so the serialised-byte cap can be
/// exercised against real HTTP without seeding a megabyte of rows (issue #336). The cap is the same
/// code path the 1 MiB maximum uses, with a smaller number.
/// </summary>
public sealed class ByteCappedPlatformDiagnosticsHostFactory : PlatformDiagnosticsHostFactory
{
    /// <summary>
    /// Just enough row budget for a row or two of the seeded products, so a four-row result is cut
    /// short by bytes rather than by the row limit.
    /// </summary>
    public static PlatformDiagnosticsQueryLimits TightenedLimits { get; } =
        PlatformDiagnosticsQueryLimits.Create(
            maxResponseBytes: PlatformDiagnosticsQueryLimits.ResponseEnvelopeReserveBytes + 24);

    protected override PlatformDiagnosticsQueryLimits? Limits => TightenedLimits;
}

/// <summary>
/// The host exactly as it ships: no platform administrator configured at all. Everything about the
/// diagnostics API must be unreachable on it, for every caller (issue #336).
/// </summary>
public sealed class UnconfiguredPlatformDiagnosticsHostFactory : PlatformDiagnosticsHostFactory
{
    protected override bool ConfigurePlatformAdministrator => false;
}
