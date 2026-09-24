using System.Reflection;
using NetArchTest.Rules;
using Xunit;

namespace InventoryApi.Tests.Architecture;

/// <summary>
/// Enforces the Clean Architecture dependency direction described in docs/architecture.md against
/// the <em>compiled</em> assemblies.
///
/// This complements <see cref="ProjectDependencyDirectionTests"/>, which reads the
/// <c>.csproj</c> files. The two catch different mistakes:
/// <list type="bullet">
///   <item>The project-file tests catch a forbidden reference that is declared but not yet used -
///   the compiler would trim it from the assembly metadata, so type-level analysis cannot see it.</item>
///   <item>These tests catch a forbidden dependency that arrives without a new
///   <c>ProjectReference</c>: through a transitive package, a shared source file, or an
///   <c>InternalsVisibleTo</c>-style back door.</item>
/// </list>
/// </summary>
public class CleanArchitectureDependencyTests
{
    private static readonly Assembly DomainAssembly = typeof(Inventory.Domain.AssemblyMarker).Assembly;
    private static readonly Assembly ApplicationAssembly = typeof(Inventory.Application.AssemblyMarker).Assembly;
    private static readonly Assembly InfrastructureAssembly = typeof(Inventory.Infrastructure.AssemblyMarker).Assembly;
    private static readonly Assembly ApiAssembly = typeof(Program).Assembly;

    private const string ApplicationNamespace = "Inventory.Application";
    private const string InfrastructureNamespace = "Inventory.Infrastructure";
    private const string ApiNamespace = "InventoryApi";

    /// <summary>
    /// Web/transport namespaces that must never reach the inner layers. Domain calculations and
    /// application use cases have to stay runnable without an HTTP pipeline (AGENTS.md: "Keep
    /// domain calculations deterministic and free of EF Core, ASP.NET Core, HTTP, filesystem,
    /// ClosedXML, and configuration dependencies").
    /// </summary>
    private static readonly string[] WebAndTransportNamespaces =
    [
        "Microsoft.AspNetCore",
        "Microsoft.Extensions.Hosting",
        "System.Web",
    ];

    /// <summary>
    /// Persistence, external-IO and reporting-format namespaces that must never reach the Domain.
    /// </summary>
    private static readonly string[] PersistenceAndIoNamespaces =
    [
        "Microsoft.EntityFrameworkCore",
        "System.Net.Http",
        "System.IO",
        "ClosedXML",
        "Microsoft.Extensions.Configuration",
    ];

    #region Layer-to-layer rules

    [Fact]
    public void Domain_must_not_depend_on_Application_Infrastructure_or_Api()
    {
        AssertNoDependency(
            DomainAssembly,
            "Inventory.Domain",
            [ApplicationNamespace, InfrastructureNamespace, ApiNamespace]);
    }

    [Fact]
    public void Application_must_not_depend_on_Infrastructure_or_Api()
    {
        AssertNoDependency(
            ApplicationAssembly,
            "Inventory.Application",
            [InfrastructureNamespace, ApiNamespace]);
    }

    [Fact]
    public void Infrastructure_must_not_depend_on_Api()
    {
        AssertNoDependency(
            InfrastructureAssembly,
            "Inventory.Infrastructure",
            [ApiNamespace]);
    }

    [Fact]
    public void Api_may_depend_on_Application_and_Infrastructure()
    {
        // The outermost layer is the composition root, so this rule is a positive assertion: it
        // documents the one direction that is allowed and fails if the wiring disappears (which
        // would mean the API had stopped going through the use cases at all).
        var apiTypes = Types.InAssembly(ApiAssembly);

        Assert.True(
            apiTypes.That().HaveDependencyOn(ApplicationNamespace).GetTypes().Any(),
            "InventoryApi is expected to depend on Inventory.Application; no type in the API "
                + "assembly references it. The composition root should resolve use cases from "
                + "Inventory.Application rather than reimplementing them.");

        Assert.True(
            apiTypes.That().HaveDependencyOn(InfrastructureNamespace).GetTypes().Any(),
            "InventoryApi is expected to depend on Inventory.Infrastructure; no type in the API "
                + "assembly references it. The composition root should register the infrastructure "
                + "adapters.");
    }

    #endregion

    #region Web/transport leakage rules

    [Fact]
    public void Domain_must_not_depend_on_web_or_transport_types()
    {
        AssertNoDependency(DomainAssembly, "Inventory.Domain", WebAndTransportNamespaces);
    }

    [Fact]
    public void Application_must_not_depend_on_web_or_transport_types()
    {
        AssertNoDependency(ApplicationAssembly, "Inventory.Application", WebAndTransportNamespaces);
    }

    [Fact]
    public void Domain_must_not_depend_on_persistence_or_external_io()
    {
        AssertNoDependency(DomainAssembly, "Inventory.Domain", PersistenceAndIoNamespaces);
    }

    [Fact]
    public void Application_must_not_depend_on_EF_Core_or_HTTP_clients()
    {
        // The Application layer owns ports (IReadOnly...FactsProvider, INayaxClient, ...); the
        // adapters that implement them with EF Core or HttpClient live in the outer layers.
        AssertNoDependency(
            ApplicationAssembly,
            "Inventory.Application",
            ["Microsoft.EntityFrameworkCore", "System.Net.Http", "ClosedXML"]);
    }

    /// <summary>
    /// Identity-provider and claims types must stay at the InventoryApi boundary (issue #64).
    /// <c>System.Security.Claims</c> needs its own rule: it lives in System.Runtime rather than
    /// under a Microsoft.AspNetCore namespace, so the web/transport rules above would not catch a
    /// <c>ClaimsPrincipal</c> leaking into a use case or a tenancy policy.
    /// </summary>
    [Fact]
    public void Domain_and_Application_must_not_depend_on_claims_or_identity_provider_types()
    {
        string[] identityNamespaces = ["System.Security.Claims", "Microsoft.Identity"];

        AssertNoDependency(DomainAssembly, "Inventory.Domain", identityNamespaces);
        AssertNoDependency(ApplicationAssembly, "Inventory.Application", identityNamespaces);
    }

    [Fact]
    public void Domain_and_Application_must_not_reference_controllers()
    {
        foreach (var (assembly, layer) in new[]
        {
            (DomainAssembly, "Inventory.Domain"),
            (ApplicationAssembly, "Inventory.Application"),
        })
        {
            var offenders = Types.InAssembly(assembly)
                .That()
                .DoNotResideInNamespaceStartingWith(InstrumentationNamespacePrefix)
                .And()
                .HaveNameEndingWith("Controller")
                .GetTypes()
                .Select(type => type.FullName ?? type.Name)
                .ToArray();

            Assert.True(
                offenders.Length == 0,
                $"{layer} must not declare MVC controllers. The HTTP boundary belongs in "
                    + $"InventoryApi/Controllers. Offending type(s): {string.Join(", ", offenders)}.");
        }
    }

    #endregion

    /// <summary>
    /// Coverlet weaves a per-assembly <c>Coverlet.Core.Instrumentation.Tracker.*</c> type into every
    /// instrumented assembly when the validation scripts run tests with coverage collection. That
    /// injected type uses System.IO and is not application code, so it is excluded from the rules
    /// below - otherwise the architecture tests would pass without coverage and fail with it.
    /// </summary>
    private const string InstrumentationNamespacePrefix = "Coverlet";

    /// <summary>
    /// Runs one NetArchTest rule per forbidden namespace so a failure names exactly which
    /// boundary broke and which types crossed it, rather than reporting a single opaque
    /// "architecture violated".
    /// </summary>
    private static void AssertNoDependency(Assembly assembly, string layer, string[] forbiddenNamespaces)
    {
        foreach (var forbidden in forbiddenNamespaces)
        {
            var result = Types.InAssembly(assembly)
                .That()
                .DoNotResideInNamespaceStartingWith(InstrumentationNamespacePrefix)
                .ShouldNot()
                .HaveDependencyOn(forbidden)
                .GetResult();

            var offenders = result.FailingTypeNames ?? [];

            Assert.True(
                result.IsSuccessful,
                $"Architecture boundary violated: {layer} must not depend on '{forbidden}'. "
                    + $"Offending type(s): {string.Join(", ", offenders)}. "
                    + "See docs/architecture.md for the allowed dependency direction "
                    + "(Domain <- Application <- Infrastructure <- InventoryApi).");
        }
    }
}
