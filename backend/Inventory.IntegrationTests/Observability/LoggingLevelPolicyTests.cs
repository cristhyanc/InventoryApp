using System.Text.Json;
using Microsoft.Extensions.Logging;
using Xunit;

namespace InventoryApi.Tests.Observability;

/// <summary>
/// Issue #165: once logs are exported to Application Insights, the configured levels decide both
/// the monthly bill and whether an operator can find anything in what is retained. The deployed
/// levels live in <c>appsettings.json</c> - there is no committed <c>appsettings.Production.json</c>
/// - and <c>appsettings.Development.json</c> raises them again for local debugging, where nothing
/// is exported.
///
/// These tests read the committed configuration files rather than a hosted application, because
/// what matters is the value that ships: a future change that re-enables the per-query EF command
/// log in a deployed environment, or that silences a category's warnings and errors, has to be a
/// deliberate update here.
/// </summary>
public class LoggingLevelPolicyTests
{
    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    [Fact]
    public void Deployed_levels_suppress_framework_and_ef_command_noise()
    {
        var levels = LogLevels("appsettings.json");

        Assert.Equal(LogLevel.Warning, levels["Microsoft"]);
        Assert.Equal(LogLevel.Warning, levels["Microsoft.AspNetCore"]);
        Assert.Equal(LogLevel.Warning, levels["Microsoft.EntityFrameworkCore"]);

        // The per-query SQL command log is the noisiest and most expensive category, and the one
        // most likely to carry business data into retained telemetry.
        Assert.Equal(LogLevel.Warning, levels["Microsoft.EntityFrameworkCore.Database.Command"]);
    }

    [Fact]
    public void Deployed_levels_retain_application_logs_and_the_deployment_record()
    {
        var levels = LogLevels("appsettings.json");

        // Application code logs under its own namespaces, which fall to Default.
        Assert.Equal(LogLevel.Information, levels["Default"]);

        // Normal Production startup applies pending migrations (issue #201), so which migrations
        // were applied has to stay in the record, as do host start/stop and the bound URLs.
        Assert.Equal(LogLevel.Information, levels["Microsoft.EntityFrameworkCore.Migrations"]);
        Assert.Equal(LogLevel.Information, levels["Microsoft.Hosting.Lifetime"]);
    }

    /// <summary>
    /// No category may be quieter than Warning: suppressing a category's warnings and errors would
    /// make a real failure invisible in the only place an operator can look after the fact.
    /// </summary>
    [Fact]
    public void No_deployed_category_silences_warnings_or_errors()
    {
        foreach (var (category, level) in LogLevels("appsettings.json"))
        {
            Assert.True(
                level <= LogLevel.Warning,
                $"Logging level for '{category}' is {level}, which hides application warnings and errors.");
        }
    }

    /// <summary>
    /// The quiet levels are a deployment decision, not a developer-experience regression: locally
    /// the framework, EF command and Identity.Web categories are back at Information.
    /// </summary>
    [Fact]
    public void Development_raises_the_suppressed_categories_again()
    {
        var levels = LogLevels("appsettings.Development.json");

        Assert.Equal(LogLevel.Information, levels["Microsoft"]);
        Assert.Equal(LogLevel.Information, levels["Microsoft.AspNetCore"]);
        Assert.Equal(LogLevel.Information, levels["Microsoft.EntityFrameworkCore.Database.Command"]);
        Assert.Equal(LogLevel.Information, levels["Microsoft.Identity.Web"]);
    }

    /// <summary>
    /// A connection string is a secret and an instrumentation key is an identifier of a live Azure
    /// resource; neither may ever be committed, in any environment's settings file.
    /// </summary>
    [Theory]
    [InlineData("appsettings.json")]
    [InlineData("appsettings.Development.json")]
    public void No_settings_file_commits_an_application_insights_connection_string(string fileName)
    {
        var contents = File.ReadAllText(SettingsFile(fileName));

        Assert.DoesNotContain("APPLICATIONINSIGHTS_CONNECTION_STRING", contents, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("InstrumentationKey", contents, StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, LogLevel> LogLevels(string fileName)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(SettingsFile(fileName)), JsonOptions);

        var levels = document.RootElement.GetProperty("Logging").GetProperty("LogLevel");

        return levels.EnumerateObject().ToDictionary(
            property => property.Name,
            property => Enum.Parse<LogLevel>(property.Value.GetString()!, ignoreCase: false),
            StringComparer.Ordinal);
    }

    private static string SettingsFile(string fileName)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !Directory.Exists(Path.Combine(current.FullName, "InventoryApi")))
        {
            current = current.Parent;
        }

        var backendRoot = current?.FullName
            ?? throw new InvalidOperationException("Could not locate the backend directory containing InventoryApi.");

        return Path.Combine(backendRoot, "InventoryApi", fileName);
    }
}
