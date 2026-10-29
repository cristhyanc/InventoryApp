using InventoryApi.Adapters.Subscriptions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace InventoryApi.Tests.Adapters.Subscriptions;

/// <summary>
/// The rollout switch of issue #966 (decided 29 October 2026 on #965): the
/// <c>Subscriptions:EnforcementEnabled</c> setting, read once at startup.
///
/// <para>What matters here is the fail-closed direction, and it is the whole of what this adapter
/// does: <strong>absent, blank and unparseable are all off</strong>. The answer that must never come
/// from a mistyped setting is "start withholding features the business is using today", so every
/// value that is not an explicit boolean true leaves plans unenforced.</para>
/// </summary>
public class ConfiguredSubscriptionEnforcementFlagTests
{
    [Fact]
    public void An_absent_section_leaves_enforcement_off() =>
        Assert.False(Flag(null).IsEnabled);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("yes")]
    [InlineData("1")]
    [InlineData("TRUE-ish")]
    [InlineData("on")]
    public void A_blank_or_unparseable_value_leaves_enforcement_off(string value) =>
        Assert.False(Flag(value).IsEnabled);

    [Fact]
    public void An_explicit_false_leaves_enforcement_off() =>
        Assert.False(Flag("false").IsEnabled);

    [Theory]
    [InlineData("true")]
    [InlineData("True")]
    [InlineData("TRUE")]
    public void An_explicit_true_switches_enforcement_on(string value) =>
        Assert.True(Flag(value).IsEnabled);

    /// <summary>
    /// The key a deployment sets, pinned: it is named in the startup log and in the documentation,
    /// so a rename is a change to an operator-facing contract rather than an internal detail.
    /// </summary>
    [Fact]
    public void The_configuration_key_is_the_documented_one() =>
        Assert.Equal("Subscriptions:EnforcementEnabled", ConfiguredSubscriptionEnforcementFlag.EnabledKey);

    private static ConfiguredSubscriptionEnforcementFlag Flag(string? value)
    {
        var builder = new ConfigurationBuilder();
        if (value is not null)
        {
            builder.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    [ConfiguredSubscriptionEnforcementFlag.EnabledKey] = value,
                });
        }

        return new ConfiguredSubscriptionEnforcementFlag(builder.Build());
    }
}
