using Inventory.Application.Nayax;
using Inventory.Infrastructure;
using Inventory.Infrastructure.Nayax;
using Inventory.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InventoryApi.Tests.Infrastructure.Nayax;

/// <summary>
/// Issue #518's wiring, which the behaviour tests cannot see: how the composition root binds the
/// encryption keys and which protector each state of that configuration produces.
///
/// The binding test matters because the key map is a read-only property: a configuration binder
/// that silently failed to populate it would leave a configured environment running on the
/// fail-closed protector, and every other test here - which constructs the options directly - would
/// still pass.
/// </summary>
public sealed class NayaxTokenProtectionDependencyInjectionTests
{
    private const string ActiveKeyId = "2026-10";

    [Fact]
    public void The_configuration_section_binds_the_active_key_id_and_the_key_map()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{NayaxTokenProtectionOptions.SectionName}:{nameof(NayaxTokenProtectionOptions.ActiveKeyId)}"] = ActiveKeyId,
                [$"{NayaxTokenProtectionOptions.SectionName}:{nameof(NayaxTokenProtectionOptions.Keys)}:{ActiveKeyId}"] = Key(1),
                [$"{NayaxTokenProtectionOptions.SectionName}:{nameof(NayaxTokenProtectionOptions.Keys)}:2026-11"] = Key(2),
            })
            .Build();

        var options = configuration.GetSection(NayaxTokenProtectionOptions.SectionName)
            .Get<NayaxTokenProtectionOptions>();

        Assert.NotNull(options);
        Assert.Equal(ActiveKeyId, options!.ActiveKeyId);
        Assert.Equal([ActiveKeyId, "2026-11"], options.Keys.Keys.Order(StringComparer.Ordinal));
        Assert.True(options.IsConfigured);
    }

    /// <summary>
    /// The shipped state: the committed section is empty, so nothing is configured and the API must
    /// still start. Read from the real <c>appsettings.json</c>, because an empty section that
    /// accidentally bound as "configured" would fail startup for every environment.
    /// </summary>
    [Fact]
    public void The_committed_configuration_leaves_the_section_unconfigured()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json")
            .Build();

        var options = configuration.GetSection(NayaxTokenProtectionOptions.SectionName)
            .Get<NayaxTokenProtectionOptions>();

        Assert.NotNull(options);
        Assert.False(options!.IsConfigured);
        Assert.Empty(options.Keys);
    }

    [Fact]
    public void A_configured_section_registers_the_encrypting_protector_as_a_singleton()
    {
        var services = new ServiceCollection();
        var options = new NayaxTokenProtectionOptions { ActiveKeyId = ActiveKeyId };
        options.Keys[ActiveKeyId] = Key(1);

        services.AddNayaxTokenProtection(options);

        var registration = Assert.Single(services.Where(descriptor =>
            descriptor.ServiceType == typeof(INayaxTokenProtector)));
        Assert.Equal(ServiceLifetime.Singleton, registration.Lifetime);
        using var provider = services.BuildServiceProvider();
        Assert.IsType<AesGcmNayaxTokenProtector>(provider.GetRequiredService<INayaxTokenProtector>());
    }

    [Fact]
    public void An_unconfigured_section_registers_the_fail_closed_protector_instead_of_failing_startup()
    {
        var services = new ServiceCollection();

        services.AddNayaxTokenProtection(new NayaxTokenProtectionOptions());

        using var provider = services.BuildServiceProvider();
        Assert.IsType<UnconfiguredNayaxTokenProtector>(provider.GetRequiredService<INayaxTokenProtector>());
    }

    [Fact]
    public void A_half_configured_section_fails_at_registration_rather_than_at_the_first_save()
    {
        var services = new ServiceCollection();
        var options = new NayaxTokenProtectionOptions { ActiveKeyId = ActiveKeyId };
        options.Keys["2026-11"] = Key(1);

        Assert.Throws<InvalidOperationException>(() => services.AddNayaxTokenProtection(options));
    }

    [Fact]
    public void AddInfrastructureServices_registers_the_connection_store_per_request()
    {
        var services = new ServiceCollection();

        services.AddInfrastructureServices();

        var registration = Assert.Single(services.Where(descriptor =>
            descriptor.ServiceType == typeof(INayaxConnectionStore)));
        Assert.Equal(typeof(EfNayaxConnectionStore), registration.ImplementationType);
        // Scoped like every other EF adapter: it shares the request's AppDbContext, and therefore
        // its change tracker and transaction.
        Assert.Equal(ServiceLifetime.Scoped, registration.Lifetime);
    }

    private static string Key(byte seed) =>
        Convert.ToBase64String(
            Enumerable.Repeat(seed, NayaxTokenProtectionConfiguration.KeySizeInBytes).ToArray());
}
