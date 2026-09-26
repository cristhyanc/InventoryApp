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

    /// <summary>
    /// Issue #145: <c>Inventory.Infrastructure</c> is the adapter layer (EF Core, Nayax HTTP
    /// client, file storage, exports), so unlike Domain/Application it legitimately needs
    /// persistence and outbound-HTTP-client namespaces. What it must never gain is the ASP.NET
    /// Core web-host surface (<c>HttpContext</c>, middleware, MVC types, ...) - that would mean an
    /// adapter reaching back into the request pipeline instead of exposing a narrow port for
    /// InventoryApi to call, and would make the adapter untestable without a running host.
    /// </summary>
    [Fact]
    public void Infrastructure_must_not_depend_on_ASP_NET_HTTP_types()
    {
        AssertNoDependency(InfrastructureAssembly, "Inventory.Infrastructure", ["Microsoft.AspNetCore"]);
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

    /// <summary>
    /// The Azure Blob SDK and its credential chain are an adapter detail (issue #39). A use case
    /// that took a <c>BlobClient</c>, or a controller that reached for one, would make the
    /// storage decision impossible to change and impossible to test without Azure - and would
    /// put the tenant-prefix rule somewhere other than the one adapter that enforces it.
    /// </summary>
    [Fact]
    public void Azure_storage_and_credential_types_stay_in_Infrastructure()
    {
        string[] azureNamespaces = ["Azure.Storage", "Azure.Identity"];

        AssertNoDependency(DomainAssembly, "Inventory.Domain", azureNamespaces);
        AssertNoDependency(ApplicationAssembly, "Inventory.Application", azureNamespaces);
        AssertNoDependency(ApiAssembly, "InventoryApi", azureNamespaces);
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

    #region Exception ownership rules

    /// <summary>
    /// Issue #163: InventoryApi's job in the exception story is translation - an
    /// <see cref="Microsoft.AspNetCore.Diagnostics.IExceptionHandler"/> that maps an already-thrown
    /// exception to a <c>ProblemDetails</c> response - never definition. A business exception
    /// belongs to whichever layer owns the failure it reports: <c>Inventory.Domain</c> for a domain
    /// invariant, <c>Inventory.Application</c> for a use-case-specific failure that is not a domain
    /// invariant, <c>Inventory.Infrastructure</c> for a provider-specific failure (EF Core, Azure
    /// Blob, filesystem, HTTP, Nayax) translated at that layer's own boundary. See
    /// docs/architecture.md § Domain and application error mapping for the ownership table.
    ///
    /// The types below predate that rule and are pinned here as a deliberate, reviewed exception
    /// rather than removed by this change, exactly like
    /// <see cref="ProjectDependencyDirectionTests.Only_the_documented_legacy_services_remain_in_InventoryApi_Services"/>
    /// freezes the legacy services folder: <see cref="InventoryApi.Bootstrap.PendingMigrationsException"/>
    /// and <see cref="InventoryApi.Data.CrossBusinessAccessException"/> are startup/persistence
    /// guards intimately coupled to <c>AppDbContext</c>, which itself still lives in InventoryApi
    /// (see docs/architecture.md's temporary API-owned exception); moving them means moving
    /// AppDbContext first, which is out of this issue's scope. <c>InventoryCostDataQualityException</c>
    /// (nested in <c>InventoryApi/Services/InventoryCostService.cs</c>) is an internal
    /// data-integrity invariant with the same developer-facing-message shape as
    /// <see cref="InvalidOperationException"/>, which it derives from, not a caller-safe business
    /// exception the HTTP boundary maps by type.
    ///
    /// What this test enforces is that the set does not grow silently: a new exception type landing
    /// in InventoryApi fails here and must be moved to the layer that owns it, or added to the
    /// allow-list as a conscious, reviewed edit alongside this comment.
    /// </summary>
    [Fact]
    public void No_new_business_exception_is_defined_in_InventoryApi()
    {
        string[] allowedLegacyExceptionTypeNames =
        [
            "InventoryApi.Bootstrap.PendingMigrationsException",
            "InventoryApi.Data.CrossBusinessAccessException",
            "InventoryApi.Services.InventoryCostDataQualityException",
        ];

        var offenders = ApiAssembly
            .GetTypes()
            .Where(type => typeof(Exception).IsAssignableFrom(type))
            .Where(type => type.Namespace is null
                || !type.Namespace.StartsWith(InstrumentationNamespacePrefix, StringComparison.Ordinal))
            .Where(type => !allowedLegacyExceptionTypeNames.Contains(type.FullName))
            .Select(type => type.FullName ?? type.Name)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "InventoryApi must contain no business exception definitions; exception ownership "
                + "belongs to Inventory.Domain, Inventory.Application, or Inventory.Infrastructure "
                + "depending on which layer owns the failure (see docs/architecture.md). New "
                + $"exception type(s) found: {string.Join(", ", offenders)}. Move the exception to "
                + "the layer that owns it, or add it to the allow-list above as a conscious, "
                + "reviewed edit if it is a deliberate exception.");
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
