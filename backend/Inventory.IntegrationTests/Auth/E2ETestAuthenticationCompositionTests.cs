using InventoryApi.Auth.E2ETesting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace InventoryApi.Tests.Auth;

/// <summary>
/// The gate in front of the test-only end-to-end authentication scheme (issue #46), tested at the
/// level it is decided: the hosting environment and the composition root.
///
/// The requirement is not "the scheme usually is not there". It is that the scheme is
/// structurally unavailable anywhere except one exactly named host, that the decision is taken
/// from the environment rather than from anything a caller can send, and that an unrecognised
/// environment keeps the real Microsoft Entra scheme rather than silently losing it. Those are
/// the three things asserted here; <see cref="E2ETestAuthenticationFailsClosedTests"/> then proves
/// the same thing through the real HTTP pipeline.
/// </summary>
public class E2ETestAuthenticationCompositionTests
{
    /// <summary>
    /// Every one of these is "not the E2E host", including the near-misses. Case, surrounding
    /// whitespace and a suffix all matter, because the safe answer to an ambiguous environment
    /// name is to leave the synthetic scheme out.
    /// </summary>
    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    [InlineData("Testing")]
    [InlineData("Staging")]
    [InlineData("e2etest")]
    [InlineData("E2ETEST")]
    [InlineData("E2ETest ")]
    [InlineData(" E2ETest")]
    [InlineData("E2ETest2")]
    [InlineData("NotE2ETest")]
    [InlineData("")]
    public void The_dedicated_environment_gate_is_closed_for_every_other_environment_name(string environmentName)
    {
        Assert.False(E2ETestEnvironment.IsEnabled(new StubHostEnvironment(environmentName)));
    }

    [Fact]
    public void The_dedicated_environment_gate_is_open_only_for_the_exact_E2E_environment_name()
    {
        Assert.Equal("E2ETest", E2ETestEnvironment.EnvironmentName);
        Assert.True(E2ETestEnvironment.IsEnabled(new StubHostEnvironment(E2ETestEnvironment.EnvironmentName)));
    }

    /// <summary>
    /// The composition root must not merely prefer the real scheme outside the E2E host: the test
    /// scheme must not be registered at all, so there is nothing for a request, a configuration
    /// value or a later code change to select.
    /// </summary>
    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    [InlineData("Testing")]
    [InlineData("Staging")]
    [InlineData("e2etest")]
    public void Only_the_real_Entra_scheme_is_registered_outside_the_dedicated_environment(string environmentName)
    {
        var options = AuthenticationOptionsFor(environmentName);

        // DefaultScheme, which the authenticate/challenge defaults fall back to when they are not
        // set separately - the real registration sets exactly this one.
        Assert.Equal("Bearer", options.DefaultScheme);
        Assert.DoesNotContain(
            E2ETestAuthenticationHandler.SchemeName,
            options.Schemes.Select(scheme => scheme.Name));
    }

    [Fact]
    public void Only_the_synthetic_scheme_is_registered_in_the_dedicated_environment()
    {
        var options = AuthenticationOptionsFor(E2ETestEnvironment.EnvironmentName);

        Assert.Equal(E2ETestAuthenticationHandler.SchemeName, options.DefaultScheme);
        Assert.Equal(
            [E2ETestAuthenticationHandler.SchemeName],
            options.Schemes.Select(scheme => scheme.Name).ToArray());
    }

    /// <summary>
    /// Customer identity configuration must not reach into this host (issue #710). The E2E host
    /// registers the synthetic scheme and nothing else whatever is configured: a filled
    /// <c>ExternalId</c> section adds no second real scheme here, so the isolation of issue #46 is
    /// not something a configuration value can widen.
    /// </summary>
    [Fact]
    public void A_configured_customer_identity_tenant_adds_no_real_scheme_to_the_dedicated_environment()
    {
        var options = AuthenticationOptionsFor(E2ETestEnvironment.EnvironmentName, CustomerIdentityConfiguration);

        Assert.Equal(
            [E2ETestAuthenticationHandler.SchemeName],
            options.Schemes.Select(scheme => scheme.Name).ToArray());
    }

    /// <summary>
    /// The mirror image, for the same reason: outside that host a configured customer identity
    /// tenant registers its own pinned scheme beside the workforce one and still never the
    /// synthetic one.
    /// </summary>
    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    [InlineData("Testing")]
    [InlineData("e2etest")]
    public void A_configured_customer_identity_tenant_never_registers_the_synthetic_scheme(string environmentName)
    {
        var options = AuthenticationOptionsFor(environmentName, CustomerIdentityConfiguration);
        var names = options.Schemes.Select(scheme => scheme.Name).ToArray();

        Assert.DoesNotContain(E2ETestAuthenticationHandler.SchemeName, names);
        Assert.Contains("Bearer", names);
        Assert.Contains("ExternalIdBearer", names);
    }

    /// <summary>
    /// Selecting a synthetic actor is a closed, server-defined choice. An unknown key, a blank
    /// one, and a value that differs only in case name no actor at all, so a request cannot
    /// invent an identity even inside the E2E host.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Business-A-Owner")]
    [InlineData("business-a-owner;business-b-owner")]
    [InlineData("unknown-actor")]
    public void An_unrecognised_actor_key_names_no_synthetic_actor(string? key)
    {
        Assert.Null(E2ETestActors.Find(key));
    }

    [Fact]
    public void The_synthetic_actors_are_a_fixed_set_of_distinct_deterministic_identities()
    {
        Assert.Equal(
            ["business-a-owner", "business-b-owner", "no-membership"],
            E2ETestActors.All.Select(actor => actor.Key).Order(StringComparer.Ordinal));
        Assert.Equal(
            E2ETestActors.All.Count,
            E2ETestActors.All.Select(actor => actor.ObjectId).Distinct(StringComparer.Ordinal).Count());
        Assert.All(E2ETestActors.All, actor =>
        {
            Assert.True(Guid.TryParse(actor.DirectoryTenantId, out _));
            Assert.True(Guid.TryParse(actor.ObjectId, out _));
            Assert.Same(actor, E2ETestActors.Find(actor.Key));
        });
    }

    /// <summary>One complete customer identity tenant (issue #710); no real identifier.</summary>
    private static readonly KeyValuePair<string, string?>[] CustomerIdentityConfiguration =
    [
        new("ExternalId:Instance", "https://contoso.ciamlogin.com/"),
        new("ExternalId:TenantId", "9d6ff2a1-1f3e-4c6b-9a15-7b2c4f8e0d31"),
        new("ExternalId:ClientId", "4f1c8e55-27b4-4a0e-9d7c-6a3f2b1e5c08"),
    ];

    private static AuthenticationOptions AuthenticationOptionsFor(
        string environmentName, KeyValuePair<string, string?>[]? configuration = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInventoryApiAuthentication(
            new ConfigurationBuilder().AddInMemoryCollection(configuration ?? []).Build(),
            new StubHostEnvironment(environmentName));

        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<AuthenticationOptions>>().Value;
    }

    private sealed class StubHostEnvironment : IHostEnvironment
    {
        public StubHostEnvironment(string environmentName) => EnvironmentName = environmentName;

        public string EnvironmentName { get; set; }

        public string ApplicationName { get; set; } = "InventoryApi.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
