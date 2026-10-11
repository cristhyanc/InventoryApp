using System.Reflection;
using Inventory.Application.Nayax;
using NetArchTest.Rules;
using Xunit;

namespace InventoryApi.Tests.Architecture;

/// <summary>
/// The re-test credential boundary of issue #520, enforced against the compiled assemblies.
///
/// <see cref="INayaxConnectionRetestCredentialProvider"/> deliberately ignores the status gate, so
/// that ordinary Nayax consumers cannot call it is a security property rather than a convention: a
/// consumer that could reach it would have a way to use credentials the gate refuses, including a
/// connection an operator has been told needs attention. The parent contract (#500) asks for an
/// architecture or unit test that proves it, and this is it - a review habit would not survive the
/// next person who needs a token.
///
/// The allow-list is exactly three types: the port, its one implementation, and the composition
/// that registers them. Issue #506's Owner-only re-test endpoint will add itself here, as a
/// conscious, reviewed edit alongside this comment; nothing else may.
/// </summary>
public class NayaxRetestCredentialBoundaryTests
{
    private const string InstrumentationNamespacePrefix = "Coverlet";

    private static readonly string RetestPort = typeof(INayaxConnectionRetestCredentialProvider).FullName!;
    private static readonly string RetestProvider = typeof(NayaxConnectionRetestCredentialProvider).FullName!;

    private static readonly string[] AllowedDependents =
    [
        "Inventory.Application.Nayax.INayaxConnectionRetestCredentialProvider",
        "Inventory.Application.Nayax.NayaxConnectionRetestCredentialProvider",
        // The composition root of Inventory.Application: it registers the port and must name it.
        "Inventory.Application.ApplicationServiceCollectionExtensions",
    ];

    private static readonly (string Layer, Assembly Assembly)[] ProductionAssemblies =
    [
        ("Inventory.Application", typeof(Inventory.Application.AssemblyMarker).Assembly),
        ("Inventory.Infrastructure", typeof(Inventory.Infrastructure.AssemblyMarker).Assembly),
        ("InventoryApi", typeof(Program).Assembly),
    ];

    [Fact]
    public void No_ordinary_consumer_may_depend_on_the_gate_free_re_test_port()
    {
        foreach (var (layer, assembly) in ProductionAssemblies)
        {
            AssertOnlyAllowedDependents(layer, assembly, RetestPort);
            AssertOnlyAllowedDependents(layer, assembly, RetestProvider);
        }
    }

    /// <summary>
    /// Proves the rule above can actually see a dependent, rather than passing because the
    /// dependency analysis found nothing at all: the two types that legitimately name the port are
    /// detected, and are passing only because they are on the allow-list.
    /// </summary>
    [Fact]
    public void The_dependency_rule_detects_the_types_that_legitimately_name_the_port()
    {
        var detected = Dependents(typeof(Inventory.Application.AssemblyMarker).Assembly, RetestPort);

        Assert.Contains(RetestProvider, detected);
        Assert.Contains("Inventory.Application.ApplicationServiceCollectionExtensions", detected);
    }

    /// <summary>
    /// The Nayax HTTP client is the one adapter every ordinary consumer reaches Nayax through, so it
    /// is named explicitly: if it ever gained the gate-free read, every consumer would have it.
    /// </summary>
    [Fact]
    public void The_nayax_http_client_depends_only_on_the_gated_provider()
    {
        var clientDependencies = Types
            .InAssembly(typeof(Inventory.Infrastructure.Nayax.NayaxLynxClient).Assembly)
            .That()
            .HaveName(nameof(Inventory.Infrastructure.Nayax.NayaxLynxClient))
            .Should()
            .HaveDependencyOn(typeof(INayaxRequestCredentialProvider).FullName)
            .GetResult();

        Assert.True(
            clientDependencies.IsSuccessful,
            "NayaxLynxClient must resolve its credentials from INayaxRequestCredentialProvider, the "
                + "gated port, so the status gate applies to every ordinary Nayax call.");
    }

    /// <summary>
    /// Freezes the gated port's surface. The separation only holds while the gated provider has no
    /// gate-free member of its own, and "add one small method here" is exactly how such a boundary
    /// is lost.
    /// </summary>
    [Fact]
    public void The_gated_provider_exposes_only_its_two_declared_members()
    {
        var members = typeof(INayaxRequestCredentialProvider)
            .GetMethods()
            .Select(method => method.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[]
            {
                nameof(INayaxRequestCredentialProvider.GetForOperationAsync),
                nameof(INayaxRequestCredentialProvider.ReportUnauthorizedAsync),
            },
            members);
    }

    private static IReadOnlyCollection<string> Dependents(Assembly assembly, string dependency) =>
        Types.InAssembly(assembly)
            .That()
            .DoNotResideInNamespaceStartingWith(InstrumentationNamespacePrefix)
            .ShouldNot()
            .HaveDependencyOn(dependency)
            .GetResult()
            .FailingTypeNames?.ToArray() ?? [];

    private static void AssertOnlyAllowedDependents(string layer, Assembly assembly, string dependency)
    {
        var offenders = Dependents(assembly, dependency)
            .Where(name => !AllowedDependents.Contains(name, StringComparer.Ordinal))
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            $"'{dependency}' bypasses the Nayax connection status gate, so only the port, its "
                + $"implementation and the registration may name it. In {layer}, these types do: "
                + $"{string.Join(", ", offenders)}. Depend on INayaxRequestCredentialProvider "
                + "instead, or add the type to the allow-list in this test as a conscious, reviewed "
                + "edit (see docs/architecture.md § Per-business Nayax credentials).");
    }
}
