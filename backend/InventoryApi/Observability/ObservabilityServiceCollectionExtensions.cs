using Azure.Monitor.OpenTelemetry.AspNetCore;

namespace InventoryApi.Observability;

/// <summary>
/// The single registration of application telemetry (issue #165), so the composition root and the
/// telemetry composition tests observe exactly the same configuration.
///
/// This is the only instrumentation pipeline in the process. The Microsoft-supported
/// <c>Azure.Monitor.OpenTelemetry.AspNetCore</c> distribution collects incoming ASP.NET Core
/// requests, outgoing <c>HttpClient</c> dependencies, the SQL dependencies its SqlClient
/// instrumentation supports, runtime/HTTP metrics, and every <c>ILogger</c> log with the exception
/// attached to it, and correlates all of them by trace/operation ID. The classic
/// <c>Microsoft.ApplicationInsights.AspNetCore</c> SDK is deliberately not referenced: two
/// pipelines in one process double the cost and emit duplicate, uncorrelated telemetry.
///
/// Application code must keep logging through <see cref="Microsoft.Extensions.Logging.ILogger"/>.
/// Nothing in this application calls a telemetry client directly, which is what lets telemetry be
/// switched off - or swapped - in one place.
/// </summary>
public static class ObservabilityServiceCollectionExtensions
{
    /// <summary>
    /// The configuration key carrying the Application Insights connection string. In Azure it is
    /// the App Service application setting of the same name, which the hosting platform also uses;
    /// it is a secret and is never committed to this repository. See README.md
    /// § Observability and error diagnostics.
    /// </summary>
    public const string ConnectionStringKey = "APPLICATIONINSIGHTS_CONNECTION_STRING";

    /// <summary>
    /// Registers Azure Monitor OpenTelemetry when, and only when,
    /// <see cref="ConnectionStringKey"/> is configured with a non-blank value. With no connection
    /// string nothing is registered at all, so a developer machine and the automated test suite
    /// start and run normally with no Azure resource, no credential and no exporter - a missing
    /// setting must not be a startup failure, because telemetry is diagnostics rather than a
    /// dependency the API needs in order to serve requests.
    ///
    /// A setting that exists but is blank is treated as absent rather than as an exporter with an
    /// unusable destination.
    /// </summary>
    public static IServiceCollection AddInventoryApiTelemetry(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration[ConnectionStringKey];
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return services;
        }

        services.AddOpenTelemetry()
            .UseAzureMonitor(options => options.ConnectionString = connectionString);

        return services;
    }
}
