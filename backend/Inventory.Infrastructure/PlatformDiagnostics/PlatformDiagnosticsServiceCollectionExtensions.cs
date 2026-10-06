using Inventory.Application.PlatformDiagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Inventory.Infrastructure.PlatformDiagnostics;

/// <summary>
/// Registers the SQLite diagnostics read path (issue #336).
///
/// It is a separate extension rather than part of <c>AddInfrastructureServices()</c> for the same
/// reason document storage and the pending-XML source are: it needs something only the composition
/// root knows - the database connection string - so the host passes it in explicitly instead of
/// the adapter reading configuration behind the layer boundary.
///
/// The limits are a parameter with a default of <see cref="PlatformDiagnosticsQueryLimits.Default"/>
/// so the test suite can prove interruption and truncation without waiting five seconds or seeding
/// a megabyte. They can only be made tighter: <see cref="PlatformDiagnosticsQueryLimits.Create"/>
/// clamps to the hard maxima, so neither a host nor a caller can raise them.
/// </summary>
public static class PlatformDiagnosticsServiceCollectionExtensions
{
    public static IServiceCollection AddPlatformDiagnostics(
        this IServiceCollection services,
        SqliteDiagnosticsOptions options,
        PlatformDiagnosticsQueryLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        services.AddSingleton(options);
        services.AddSingleton(limits ?? PlatformDiagnosticsQueryLimits.Default);
        services.AddScoped<IDiagnosticsQueryExecutor, SqliteDiagnosticsQueryExecutor>();

        return services;
    }
}
