using System.Text.Json;
using InventoryApi.Adapters.Subscriptions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace InventoryApi.Tests.Adapters.Subscriptions;

/// <summary>
/// The beta Pro grant switch of issue #1258: the <c>Subscriptions:BetaProGrantOnApproval</c> setting,
/// read once at startup.
///
/// <para>What matters here is the fail-closed direction, and it is the whole of what this adapter
/// does: <strong>absent, blank and unparseable are all off</strong>. The answer that must never come
/// from a mistyped setting is "start writing a plan grant for every business that is approved", so
/// every value that is not an explicit boolean true leaves approval behaving exactly as it did
/// before.</para>
/// </summary>
public class ConfiguredBetaProGrantOnApprovalFlagTests
{
    /// <summary>The committed settings file carries comments, exactly as the host's reader allows.</summary>
    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    [Fact]
    public void An_absent_section_leaves_the_grant_off() =>
        Assert.False(Flag(null).IsEnabled);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("yes")]
    [InlineData("1")]
    [InlineData("TRUE-ish")]
    [InlineData("on")]
    public void A_blank_or_unparseable_value_leaves_the_grant_off(string value) =>
        Assert.False(Flag(value).IsEnabled);

    [Fact]
    public void An_explicit_false_leaves_the_grant_off() =>
        Assert.False(Flag("false").IsEnabled);

    [Theory]
    [InlineData("true")]
    [InlineData("True")]
    [InlineData("TRUE")]
    public void An_explicit_true_switches_the_grant_on(string value) =>
        Assert.True(Flag(value).IsEnabled);

    /// <summary>
    /// The key a deployment sets, pinned: it is named in the documentation and set on the App
    /// Service, so a rename is a change to an operator-facing contract rather than an internal
    /// detail. It is in the same section as the enforcement flag and is a separate key.
    /// </summary>
    [Fact]
    public void The_configuration_key_is_the_documented_one()
    {
        Assert.Equal(
            "Subscriptions:BetaProGrantOnApproval",
            ConfiguredBetaProGrantOnApprovalFlag.EnabledKey);
        Assert.NotEqual(
            ConfiguredSubscriptionEnforcementFlag.EnabledKey,
            ConfiguredBetaProGrantOnApprovalFlag.EnabledKey);
    }

    /// <summary>
    /// And what is in source control is <c>false</c>: the shipped deployment grants nothing until a
    /// human sets the App Service setting, which is this issue's whole deployment story.
    /// </summary>
    [Fact]
    public void The_shipped_configuration_leaves_the_grant_off()
    {
        using var settings = JsonDocument.Parse(File.ReadAllText(ShippedAppSettingsPath()), JsonOptions);

        var subscriptions = settings.RootElement.GetProperty("Subscriptions");
        Assert.False(subscriptions.GetProperty("BetaProGrantOnApproval").GetBoolean());
        Assert.False(subscriptions.GetProperty("EnforcementEnabled").GetBoolean());
    }

    /// <summary>
    /// The committed <c>appsettings.json</c>, located the way <c>LoggingLevelPolicyTests</c> locates
    /// it: the value that <em>ships</em> is the thing being asserted, so the file is read rather than
    /// a hosted application's configuration.
    /// </summary>
    private static string ShippedAppSettingsPath()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !Directory.Exists(Path.Combine(current.FullName, "InventoryApi")))
        {
            current = current.Parent;
        }

        var backendRoot = current?.FullName
            ?? throw new InvalidOperationException(
                "Could not locate the backend directory containing InventoryApi.");

        return Path.Combine(backendRoot, "InventoryApi", "appsettings.json");
    }

    private static ConfiguredBetaProGrantOnApprovalFlag Flag(string? value)
    {
        var builder = new ConfigurationBuilder();
        if (value is not null)
        {
            builder.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    [ConfiguredBetaProGrantOnApprovalFlag.EnabledKey] = value,
                });
        }

        return new ConfiguredBetaProGrantOnApprovalFlag(builder.Build());
    }
}
