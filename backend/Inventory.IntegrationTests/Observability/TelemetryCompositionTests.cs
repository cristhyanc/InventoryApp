using System.Xml.Linq;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using InventoryApi.Observability;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace InventoryApi.Tests.Observability;

/// <summary>
/// Issue #165: telemetry composition. The API exports requests, dependencies, metrics, ILogger
/// logs and exceptions through the Microsoft-supported Azure Monitor OpenTelemetry distribution,
/// and only when an Application Insights connection string is configured - a developer machine and
/// this test suite have none and must still start.
///
/// These tests assert what the composition root registers; they deliberately never build the
/// tracer/meter providers, so no exporter is constructed and no test ever sends telemetry
/// anywhere. Live export against a real Application Insights resource is human-verified in Azure.
/// </summary>
public class TelemetryCompositionTests
{
    /// <summary>
    /// A syntactically valid but entirely synthetic connection string. The instrumentation key is
    /// the all-zero GUID and the endpoints point at <c>localhost</c>, so this is not a credential
    /// and resolves to nothing: no production connection string or instrumentation key may ever
    /// appear in this repository.
    /// </summary>
    private const string SyntheticConnectionString =
        "InstrumentationKey=00000000-0000-0000-0000-000000000000;"
        + "IngestionEndpoint=https://localhost/;LiveEndpoint=https://localhost/";

    [Fact]
    public void No_telemetry_is_registered_when_no_connection_string_is_configured()
    {
        var services = BuildServices(connectionString: null);

        Assert.Empty(OpenTelemetryDescriptors(services));
    }

    /// <summary>
    /// An App Service application setting that exists but is blank - a setting someone cleared
    /// rather than deleted - must behave exactly like an absent one, not like a configured
    /// exporter with an unusable destination.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_connection_string_is_treated_as_absent(string connectionString)
    {
        var services = BuildServices(connectionString);

        Assert.Empty(OpenTelemetryDescriptors(services));
    }

    [Fact]
    public void Traces_metrics_and_logs_are_registered_when_a_connection_string_is_configured()
    {
        var services = BuildServices(SyntheticConnectionString);

        var registeredServiceTypes = services.Select(descriptor => descriptor.ServiceType.FullName).ToArray();
        Assert.Contains("OpenTelemetry.Trace.TracerProvider", registeredServiceTypes);
        Assert.Contains("OpenTelemetry.Metrics.MeterProvider", registeredServiceTypes);

        // ILogger logs and the exceptions attached to them reach Azure Monitor through an
        // OpenTelemetry ILoggerProvider, which is what keeps application code logging through
        // Microsoft.Extensions.Logging instead of calling a telemetry client directly.
        Assert.Contains("OpenTelemetry.Logs.LoggerProvider", registeredServiceTypes);
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(ILoggerProvider));
    }

    /// <summary>
    /// The connection string reaches the distribution from configuration, so the single App
    /// Service application setting documented in README.md is all an environment needs.
    /// </summary>
    [Fact]
    public void The_configured_connection_string_is_handed_to_azure_monitor()
    {
        var services = BuildServices(SyntheticConnectionString);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AzureMonitorOptions>>().Value;

        Assert.Equal(SyntheticConnectionString, options.ConnectionString);
    }

    /// <summary>
    /// Two instrumentation pipelines in one process double the cost and produce duplicate
    /// requests, dependencies and exceptions that no longer correlate. The classic Application
    /// Insights SDK is therefore absent by construction: it has no package reference, so nothing
    /// can register <c>TelemetryClient</c> or the classic logger provider.
    /// </summary>
    [Fact]
    public void The_classic_application_insights_sdk_is_not_referenced_or_registered()
    {
        var packages = XDocument.Load(ApiProjectFile())
            .Descendants("PackageReference")
            .Select(element => element.Attribute("Include")?.Value ?? string.Empty)
            .ToArray();

        Assert.DoesNotContain(
            packages,
            package => package.StartsWith("Microsoft.ApplicationInsights", StringComparison.Ordinal));

        var services = BuildServices(SyntheticConnectionString);
        Assert.DoesNotContain(
            services,
            descriptor => descriptor.ServiceType.FullName?.StartsWith("Microsoft.ApplicationInsights", StringComparison.Ordinal) == true);
    }

    private static IServiceCollection BuildServices(string? connectionString)
    {
        var settings = new Dictionary<string, string?>();
        if (connectionString is not null)
        {
            settings[ObservabilityServiceCollectionExtensions.ConnectionStringKey] = connectionString;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var services = new ServiceCollection();
        services.AddInventoryApiTelemetry(configuration);
        return services;
    }

    private static ServiceDescriptor[] OpenTelemetryDescriptors(IServiceCollection services) =>
        services
            .Where(descriptor =>
                descriptor.ServiceType.FullName?.StartsWith("OpenTelemetry.", StringComparison.Ordinal) == true
                || descriptor.ImplementationType?.FullName?.StartsWith("OpenTelemetry.", StringComparison.Ordinal) == true)
            .ToArray();

    private static string ApiProjectFile()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !Directory.Exists(Path.Combine(current.FullName, "InventoryApi")))
        {
            current = current.Parent;
        }

        var backendRoot = current?.FullName
            ?? throw new InvalidOperationException("Could not locate the backend directory containing InventoryApi.");

        return Path.Combine(backendRoot, "InventoryApi", "InventoryApi.csproj");
    }
}
