using System.Reflection;
using Inventory.Application.Categories;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NetArchTest.Rules;
using Xunit;

namespace InventoryApi.Tests.Architecture;

/// <summary>
/// Issue #154, the final enforcement step of the Clean Architecture migration. Every slice of
/// #145-#153 has landed, so the temporary API-owned exception that #145 only froze is now closed,
/// and these tests replace that freeze with the rule the target architecture actually wants:
/// <c>InventoryApi</c> is the HTTP boundary and the composition root, and nothing else.
///
/// The #145 guard was an allow-list - "these legacy service files may remain" - which could only
/// ever prove that the exception was not growing. What is enforced here instead is that the API
/// holds no business service at all, declares no persistence implementation, reaches a
/// <c>DbContext</c> only from the composition root and the human-invoked operator commands, and
/// never lets an EF entity become a controller's dependency or its published shape. A new slice's
/// use-case logic has to go to <c>Inventory.Application</c>/<c>Inventory.Domain</c> and a new
/// adapter to <c>Inventory.Infrastructure</c>, because there is no longer anywhere in this project
/// for either to live.
///
/// These rules complement, rather than restate, the per-slice ownership tests that already exist:
/// <see cref="ProjectDependencyDirectionTests"/> reads the <c>.csproj</c> files and the git-tracked
/// controller sources, <see cref="CleanArchitectureDependencyTests"/> checks the compiled
/// layer-to-layer direction, and <see cref="PersistenceAdapterOwnershipTests"/>/
/// <see cref="ReportingAdapterOwnershipTests"/> pin where each relocated adapter ended up. See
/// docs/architecture.md § InventoryApi and § Temporary API-owned exception and its enforcement.
/// </summary>
public class ApiLayerOwnershipTests
{
    private static readonly Assembly ApiAssembly = typeof(Program).Assembly;

    private static readonly Assembly DomainAssembly = typeof(Inventory.Domain.AssemblyMarker).Assembly;
    private static readonly Assembly ApplicationAssembly = typeof(Inventory.Application.AssemblyMarker).Assembly;
    private static readonly Assembly InfrastructureAssembly = typeof(Inventory.Infrastructure.AssemblyMarker).Assembly;

    /// <summary>
    /// Coverlet weaves a per-assembly tracker type into every instrumented assembly when the
    /// validation scripts collect coverage. It is not application code, and it uses System.IO, so it
    /// is excluded here for the same reason <see cref="CleanArchitectureDependencyTests"/> excludes
    /// it: these rules must give the same answer with and without coverage collection.
    /// </summary>
    private const string InstrumentationNamespacePrefix = "Coverlet";

    /// <summary>
    /// The members a type declares itself, public or not, instance or static - never the ones it
    /// inherits from <c>ControllerBase</c>, whose own surface is MVC's rather than this project's.
    /// </summary>
    private const BindingFlags DeclaredMembers = BindingFlags.Public
        | BindingFlags.NonPublic
        | BindingFlags.Instance
        | BindingFlags.Static
        | BindingFlags.DeclaredOnly;

    #region No business service remains in the API (acceptance criterion 1)

    /// <summary>
    /// The enforcement that replaces #145's legacy-services allow-list. That list named the service
    /// files the migration found in <c>InventoryApi/Services</c> and failed when the set changed, in
    /// either direction; with every slice migrated the list is empty, and freezing an empty list only
    /// proves one folder name stays unused. What matters now is the rule the folder was a symptom of:
    /// the API declares no business service.
    ///
    /// Any type whose name ends in <c>Service</c> - a class or the interface in front of it - is a
    /// service by this project's own naming convention, which is how every migrated slice named the
    /// thing it removed. The composition root's own extension methods are named
    /// <c>...ServiceCollectionExtensions</c>/<c>...Extensions</c> and are not matched, which is
    /// deliberate: registering services is composition, implementing one is not.
    /// </summary>
    [Fact]
    public void InventoryApi_declares_no_business_service()
    {
        var offenders = ApiAssembly.GetTypes()
            .Where(type => !IsInstrumentation(type))
            .Where(type => type.Name.EndsWith("Service", StringComparison.Ordinal))
            .Select(FullNameOf)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "InventoryApi must declare no business service: use-case orchestration belongs in "
                + "Inventory.Application, deterministic rules in Inventory.Domain, and an adapter "
                + "behind an Application-owned port in Inventory.Infrastructure. The HTTP boundary "
                + "binds transport input, invokes a use case and maps the result (AGENTS.md "
                + "§ Architecture rules, docs/architecture.md § InventoryApi). Offending type(s): "
                + $"{string.Join(", ", offenders)}.");
    }

    /// <summary>
    /// Acceptance criterion 3, first two clauses: the two folders the temporary exception lived in
    /// must not come back. <c>Services</c> held the pre-split use-case and domain logic and
    /// <c>Adapters/Persistence</c> the API-owned EF adapters; issues #306 and #309 emptied them.
    /// Checked over git-tracked files, like every other freeze in this directory, so that an
    /// untracked scratch file neither trips the rule nor satisfies it.
    ///
    /// <see cref="PersistenceAdapterOwnershipTests.InventoryApi_owns_no_persistence_adapter_folder"/>
    /// asserts the adapter half with the relocated-adapter evidence beside it; this rule is #154's
    /// own, single statement of both halves, so the API's boundary does not depend on a reader
    /// finding the slice test that happened to introduce it.
    /// </summary>
    [Theory]
    [InlineData("Services", "use-case and domain logic belongs in Inventory.Application/Inventory.Domain")]
    [InlineData("Adapters/Persistence", "an EF adapter belongs in Inventory.Infrastructure, beside AppDbContext")]
    public void InventoryApi_holds_no_file_in_a_retired_layer_folder(string folder, string wherePointsInstead)
    {
        var tracked = GitTrackedFiles(Path.Combine("InventoryApi", Path.Combine(folder.Split('/'))));

        Assert.True(
            tracked.Length == 0,
            $"InventoryApi/{folder} must not exist: {wherePointsInstead} (issue #154, "
                + "docs/architecture.md § InventoryApi). Reviving the folder is not a refactor - it "
                + $"reopens the exception the migration closed. Tracked file(s) found: {string.Join(", ", tracked)}.");
    }

    #endregion

    #region The API holds only HTTP boundary and composition responsibilities (acceptance criterion 2)

    /// <summary>
    /// Acceptance criterion 2, expressed as the one thing about layer ownership that can be checked
    /// structurally: every top-level folder of the project maps to one of the responsibilities
    /// docs/architecture.md § InventoryApi lists, and there are no others. The responsibilities are
    /// spelled out per folder below so that adding one is a decision about what the HTTP boundary
    /// is for, made here and in the documentation together, rather than a directory that quietly
    /// appears.
    ///
    /// The set is asserted as an equality rather than a subset on purpose: a folder disappearing is
    /// as much a change to the composition root's shape as one arriving, and the two previous
    /// freezes this replaces (#145's services list, #307's and #309's removed folders) worked the
    /// same way.
    /// </summary>
    [Fact]
    public void InventoryApi_owns_only_HTTP_boundary_and_composition_folders()
    {
        var documentedResponsibilities = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Adapters"] = "response-DTO projection, the E2E Nayax test double and the diagnostics audit adapter",
            ["App_Data"] = "the App Service WebJob that triggers the database backup command",
            ["Auth"] = "authentication, authorization and the per-request business scope middleware",
            ["Bootstrap"] = "the startup schema decision and the human-invoked operator commands",
            ["Controllers"] = "the HTTP boundary",
            ["DTOs"] = "API-owned transport contracts",
            ["Http"] = "HTTP error/result mapping and the health checks",
            ["Observability"] = "telemetry registration",
            ["Properties"] = "launch and service-dependency settings",
            ["Swagger"] = "OpenAPI configuration and the published-schema compatibility boundary",
        };

        var actual = GitTrackedFiles("InventoryApi")
            .Where(path => path.Contains('/', StringComparison.Ordinal))
            .Select(path => path[..path.IndexOf('/', StringComparison.Ordinal)])
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            documentedResponsibilities.Keys.OrderBy(name => name, StringComparer.Ordinal).ToArray(),
            actual);
    }

    /// <summary>
    /// Acceptance criterion 2, the other half: the API delegates rather than merely abstaining.
    /// Every controller is constructed with at least one <c>Inventory.Application</c> dependency, so
    /// each endpoint demonstrably goes through a use case. Without this, a controller that
    /// reimplemented a rule inline - taking nothing but an <c>ILogger</c> and computing the answer
    /// itself - would satisfy every negative rule above, which is the same reason each relocation
    /// test in this directory has a positive counterpart.
    ///
    /// Only the constructor parameters count, which is what <see cref="ConstructorDependenciesOf"/>
    /// returns. Asking the question over a controller's whole declared surface would let an
    /// Application type appearing in an action's parameter or return type satisfy it, and that is
    /// not a dependency: a controller computing the answer itself can still accept or return an
    /// Application record, so the broader question answers "yes" for exactly the controller this
    /// rule exists to catch. <see cref="An_Inventory_Application_type_in_an_action_signature_does_not_satisfy_the_rule"/>
    /// pins that distinction.
    /// </summary>
    [Fact]
    public void Every_controller_is_constructed_with_an_Inventory_Application_dependency()
    {
        var controllers = ApiAssembly.GetTypes()
            .Where(type => !IsInstrumentation(type))
            .Where(type => type.Name.EndsWith("Controller", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(controllers);

        var offenders = controllers
            .Where(controller => !IsConstructedWithAnApplicationDependency(controller))
            .Select(FullNameOf)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "Every controller must take its work from an Inventory.Application use case: the HTTP "
                + "boundary binds transport input, invokes the use case and maps its result "
                + "(docs/architecture.md § InventoryApi). A controller that depends on no use case "
                + "is either computing the answer itself or reaching past the Application layer. "
                + $"Offending controller(s): {string.Join(", ", offenders)}.");
    }

    /// <summary>
    /// The regression fixture for the rule above. It first asked the question over
    /// <see cref="DeclaredSurfaceOf"/>, which also holds the base type, interfaces, fields,
    /// properties and action signatures, so a controller that injected no use case at all passed as
    /// soon as one action accepted or returned an <c>Inventory.Application</c> type - the inline-rule
    /// controller the rule exists to catch, since the Application records a use case returns are
    /// exactly what such an endpoint would still bind and serialise. That contradicted what both the
    /// rule's name and docs/architecture.md § InventoryApi state, so the question is now asked over
    /// the constructor parameters alone and this test holds that answer in place.
    ///
    /// The three fixtures are private nested types in the test assembly: MVC discovers only public
    /// controllers, and every rule in this file reads <see cref="ApiAssembly"/>, so they are
    /// invisible to both.
    /// </summary>
    [Fact]
    public void An_Inventory_Application_type_in_an_action_signature_does_not_satisfy_the_rule()
    {
        var actionSignatureOnly = typeof(ApplicationTypeOnlyInItsActionController);

        Assert.False(
            IsConstructedWithAnApplicationDependency(actionSignatureOnly),
            "A controller whose only Inventory.Application type is an action parameter or return "
                + "type is constructed with no use case, so it must not satisfy the rule.");

        Assert.Contains(
            ApplicationAssembly,
            DeclaredSurfaceOf(actionSignatureOnly).Select(used => used.Assembly));

        Assert.True(
            IsConstructedWithAnApplicationDependency(typeof(InjectedUseCaseController)),
            "An injected use case must satisfy the rule.");

        Assert.True(
            IsConstructedWithAnApplicationDependency(typeof(GenericallyInjectedUseCaseController)),
            "A use case injected through a generic wrapper must satisfy the rule: Unwrap() has to "
                + "see past the wrapper, or the rule would fail a compliant controller.");
    }

    /// <summary>
    /// A controller that takes no use case - only an <c>ILogger</c>, like the inline-rule controller
    /// the rule above describes - and names an <c>Inventory.Application</c> type only in an action
    /// signature. It must fail the rule.
    /// </summary>
    private sealed class ApplicationTypeOnlyInItsActionController : ControllerBase
    {
        private readonly ILogger<ApplicationTypeOnlyInItsActionController> _logger;

        public ApplicationTypeOnlyInItsActionController(ILogger<ApplicationTypeOnlyInItsActionController> logger)
        {
            _logger = logger;
        }

        public ActionResult<CategoryRecord> Rename(CategoryRecord category)
        {
            _logger.LogInformation("Stands in for a rule applied at the HTTP boundary.");
            return Ok(category);
        }
    }

    /// <summary>The compliant shape: the use case arrives through the constructor.</summary>
    private sealed class InjectedUseCaseController : ControllerBase
    {
        private readonly ListCategories _listCategories;

        public InjectedUseCaseController(ListCategories listCategories)
        {
            _listCategories = listCategories;
        }

        public Task<IReadOnlyList<CategoryRecord>> Get(CancellationToken cancellationToken) =>
            _listCategories.Handle(cancellationToken);
    }

    /// <summary>
    /// Compliant too, through a generic wrapper - the shape dependency injection uses when a
    /// controller asks for every registration of a type. The rule must see past the wrapper.
    /// </summary>
    private sealed class GenericallyInjectedUseCaseController : ControllerBase
    {
        private readonly IEnumerable<ListCategories> _listCategories;

        public GenericallyInjectedUseCaseController(IEnumerable<ListCategories> listCategories)
        {
            _listCategories = listCategories;
        }

        public Task<IReadOnlyList<CategoryRecord>> Get(CancellationToken cancellationToken) =>
            _listCategories.First().Handle(cancellationToken);
    }

    #endregion

    #region Persistence stays out of the API (acceptance criterion 3)

    /// <summary>
    /// The namespaces inside <c>InventoryApi</c> that may know a <c>DbContext</c> exists, and why.
    /// Everything else in the project - controllers, DTOs, response mappers, middleware, exception
    /// handlers, Swagger, telemetry - must reach data through an <c>Inventory.Application</c> use
    /// case, so a request path cannot acquire a context at all.
    ///
    /// The empty entry is the global namespace, where top-level <c>Program.cs</c> statements compile
    /// to: that is the startup registration itself, the <c>AddDbContext</c>/<c>UseSqlite</c> provider
    /// decision and the connection string a host calling <c>AddInfrastructureServices()</c> must
    /// still make, plus the one scope it opens to run the startup schema step.
    ///
    /// <c>Bootstrap</c> is the documented operator-only boundary (issue #309): the schema decision
    /// and the human-invoked <c>migrate-database</c>, <c>bootstrap-business</c>,
    /// <c>migrate-documents</c> and backup commands, reachable only from the argument branches
    /// before the web host is built. Three of them are the only code in the repository permitted to
    /// pass <c>UnscopedBusinessScope.Instance</c>, and keeping them in the project no request is
    /// served from is what keeps that opt-in visibly exceptional (AGENTS.md § Tenant ownership and
    /// data isolation). <c>Http.HealthChecks</c> is the readiness probe, which proves database
    /// connectivity and so has to hold the request-scoped context it probes. <c>Auth.E2ETesting</c>
    /// is the disposable-host fixture that seeds the end-to-end suite's two synthetic businesses;
    /// it is unreachable outside the <c>E2ETest</c> hosting environment and writes every
    /// tenant-owned row through a resolved <c>BusinessScope</c>.
    ///
    /// Widening this set is a decision about where unrestricted or unmediated data access may live,
    /// which is a human one: it must be a conscious edit here, to docs/architecture.md § InventoryApi
    /// and to the invariant in AGENTS.md, never a side effect of adding a feature.
    /// </summary>
    private static readonly string[] PersistenceAwareApiNamespaces =
    [
        "",
        "InventoryApi.Bootstrap",
        "InventoryApi.Http.HealthChecks",
        "InventoryApi.Auth.E2ETesting",
    ];

    /// <summary>
    /// Acceptance criterion 3, third clause. Checked against the compiled assembly rather than the
    /// source text, because that is what distinguishes a type that <em>uses</em> EF Core from a doc
    /// comment that explains why it must not - <c>BusinessScopeMiddleware</c>, for instance,
    /// documents that a diagnostics request reads nothing through <c>AppDbContext</c>, and a text
    /// scan would read that sentence as the violation it forbids.
    ///
    /// <c>Inventory.Infrastructure.Data</c> is checked alongside <c>Microsoft.EntityFrameworkCore</c>
    /// so the rule cannot be satisfied by holding <c>AppDbContext</c>, the
    /// <c>BusinessOwnershipEnforcer</c> or the completed-sale predicate without naming an EF type
    /// directly.
    /// </summary>
    [Fact]
    public void InventoryApi_uses_a_DbContext_only_in_the_composition_root_and_the_operator_commands()
    {
        var offenders = Types.InAssembly(ApiAssembly)
            .That()
            .HaveDependencyOnAny("Microsoft.EntityFrameworkCore", "Inventory.Infrastructure.Data")
            .GetTypes()
            .Where(type => !IsInstrumentation(type))
            .Where(type => !PersistenceAwareApiNamespaces.Contains(type.Namespace ?? string.Empty, StringComparer.Ordinal))
            .Select(FullNameOf)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "A DbContext is reachable inside InventoryApi only from the composition root's startup "
                + "registration and from the human-invoked Bootstrap commands, the readiness health "
                + "check and the E2E host fixture. Everything else must go through an "
                + "Inventory.Application use case and its narrow port, so no request path can "
                + "acquire a context (AGENTS.md § Tenant ownership and data isolation, "
                + "docs/architecture.md § InventoryApi). Offending type(s): "
                + $"{string.Join(", ", offenders)}.");
    }

    /// <summary>
    /// Acceptance criterion 3, third clause for <c>DbSet</c> specifically. No file in the project
    /// names one today, in code or in a comment, so this is enforced as an absolute: a
    /// <c>DbSet&lt;T&gt;</c> is the model's own query surface, and the one type that may expose it is
    /// <c>Inventory.Infrastructure.Data.AppDbContext</c>. Even the operator commands above work
    /// through the context's navigation properties rather than declaring a set of their own, which is
    /// why this rule needs no exception where the <c>DbContext</c> rule does.
    /// </summary>
    [Fact]
    public void InventoryApi_names_no_DbSet()
    {
        var offenders = GitTrackedFiles("InventoryApi")
            .Where(path => path.EndsWith(".cs", StringComparison.Ordinal))
            .Where(path => File.ReadAllText(Path.Combine(BackendRoot, "InventoryApi", path))
                .Contains("DbSet", StringComparison.Ordinal))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "InventoryApi must not name a DbSet: the mapped model's query surface belongs to "
                + "Inventory.Infrastructure.Data.AppDbContext, and the API reads data through an "
                + "Inventory.Application port. Offending file(s) under InventoryApi: "
                + $"{string.Join(", ", offenders)}.");
    }

    /// <summary>
    /// Acceptance criterion 3, fourth clause, checked over the controllers' declared metadata: no
    /// constructor parameter, injected field, property, action parameter or return type - including
    /// the ones wrapped in <c>Task&lt;&gt;</c>/<c>ActionResult&lt;&gt;</c>/<c>IEnumerable&lt;&gt;</c> -
    /// is an EF entity or the context itself. That is the shape of the regression this guards: an EF
    /// model reached the wire on these endpoints only because a controller could take one and hand it
    /// back, which is also what would have made relocating <c>AppDbContext</c> a client-visible
    /// contract change rather than the move issue #307 was able to make it.
    ///
    /// It is the metadata counterpart of
    /// <see cref="ProjectDependencyDirectionTests.No_controller_references_the_persistence_models"/>,
    /// which scans the controller sources for the namespace and so catches a <c>using</c> directive
    /// or a fully qualified name in a method body. Neither subsumes the other: a source scan cannot
    /// see an entity arriving through an aliased or generic type, and a declared-surface scan cannot
    /// see a local variable.
    ///
    /// The two stock wire enums are not an exception to this rule and are deliberately not listed as
    /// one. <c>StockController</c>/<c>StockHistoryController</c> reach
    /// <c>Inventory.Infrastructure.Models.StockAdjustmentReason</c>/<c>StockAdjustmentSource</c> only
    /// inside a method body, through an API-owned DTO's member, while the Swagger compatibility
    /// boundary still publishes those two components - see docs/architecture.md § InventoryApi. What
    /// this rule forbids is an entity in a controller's own signature, which no documented exception
    /// covers.
    /// </summary>
    [Fact]
    public void No_controller_declares_a_persistence_model_in_its_surface()
    {
        var offenders = ApiAssembly.GetTypes()
            .Where(type => !IsInstrumentation(type))
            .Where(type => type.Name.EndsWith("Controller", StringComparison.Ordinal))
            .SelectMany(controller => DeclaredSurfaceOf(controller)
                .Where(IsPersistenceType)
                .Select(used => $"{controller.Name} -> {FullNameOf(used)}"))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(offender => offender, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "A controller must not declare a persistence type in its surface: bind the API-owned "
                + "contracts in InventoryApi.DTOs, invoke an Inventory.Application use case and "
                + "serialise an API-owned response DTO projected by a mapper in "
                + "InventoryApi/Adapters/Mapping (docs/architecture.md § InventoryApi). Offending "
                + $"reference(s): {string.Join(", ", offenders)}.");
    }

    #endregion

    #region Financial and classification rules are Domain-owned (acceptance criterion 4)

    /// <summary>
    /// Acceptance criterion 4. These four were temporary <c>InventoryApi.Services</c> residents that
    /// issue #150 moved into <c>Inventory.Domain.FinancialConfiguration</c>, and #241's transitional
    /// use of them through the API services was never a permanent exception. They are named
    /// individually because each is an authoritative financial or classification rule that the
    /// bookkeeping invariants in AGENTS.md require exactly one implementation of: the effective-dated
    /// configuration lookup, the commission basis calculation, the central card/cash/unknown
    /// classification, and the Nayax transaction-status classification where only status 12 is a
    /// completed sale. A second copy in the API would not be a layering blemish - it would be a
    /// second answer to "what did this sale earn", reachable from an endpoint.
    /// </summary>
    [Theory]
    [InlineData("EffectiveFinancialConfiguration")]
    [InlineData("SiteCommissionCalculator")]
    [InlineData("PaymentMethodClassifier")]
    [InlineData("NayaxTransactionStatusClassifier")]
    public void Financial_and_classification_rules_are_declared_only_in_Inventory_Domain(string rule)
    {
        var declaredInDomain = TypesNamed(DomainAssembly, rule);

        Assert.True(
            declaredInDomain.Length == 1,
            $"Inventory.Domain must declare exactly one {rule}; found {declaredInDomain.Length}. "
                + "Issue #150 moved these effective-date, commission, payment-method and "
                + "transaction-status rules into the Domain, over Domain-owned inputs rather than EF "
                + "entities (AGENTS.md § Core bookkeeping and reporting invariants).");

        Assert.Equal("Inventory.Domain.FinancialConfiguration", declaredInDomain[0].Namespace);

        foreach (var (assembly, layer) in new[]
        {
            (ApiAssembly, "InventoryApi"),
            (ApplicationAssembly, "Inventory.Application"),
            (InfrastructureAssembly, "Inventory.Infrastructure"),
        })
        {
            var duplicates = TypesNamed(assembly, rule).Select(FullNameOf).ToArray();

            Assert.True(
                duplicates.Length == 0,
                $"{layer} must not declare {rule}: the Domain rule is the one authoritative "
                    + "implementation, and a second copy is a second financial answer rather than a "
                    + $"layering detail. Offending type(s): {string.Join(", ", duplicates)}.");
        }
    }

    /// <summary>
    /// The reference side of the rule above: the HTTP boundary does not reach the Domain financial
    /// rules directly either. A controller that classified a payment method or resolved a commission
    /// itself would duplicate a business formula in a presentation layer, which AGENTS.md
    /// § Architecture rules forbids regardless of which project the formula is declared in; the
    /// migrated <c>Inventory.Application</c> use cases are what the endpoints call.
    /// </summary>
    [Fact]
    public void No_InventoryApi_source_file_names_the_Domain_financial_rules()
    {
        string[] rules =
        [
            "EffectiveFinancialConfiguration",
            "SiteCommissionCalculator",
            "PaymentMethodClassifier",
            "NayaxTransactionStatusClassifier",
        ];

        var offenders = GitTrackedFiles("InventoryApi")
            .Where(path => path.EndsWith(".cs", StringComparison.Ordinal))
            .SelectMany(path =>
            {
                var text = File.ReadAllText(Path.Combine(BackendRoot, "InventoryApi", path));
                return rules
                    .Where(rule => text.Contains(rule, StringComparison.Ordinal))
                    .Select(rule => $"{path} ({rule})");
            })
            .OrderBy(offender => offender, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "No file under InventoryApi may name the Domain financial/classification rules: the "
                + "endpoints call the migrated Inventory.Application use cases, which apply them. "
                + "Recomputing a commission, a fee or a payment-method classification at the HTTP "
                + "boundary duplicates a business formula in a presentation layer (AGENTS.md "
                + $"§ Architecture rules). Offending file(s): {string.Join(", ", offenders)}.");
    }

    #endregion

    #region Entity-specific queries stay in Infrastructure persistence (acceptance criterion 5)

    /// <summary>
    /// Acceptance criterion 5. An entity-specific query expression is persistence: it is written
    /// against the EF model, it only means anything inside a translated query, and it cannot be
    /// evaluated without a provider. <c>Inventory.Domain</c> and <c>Inventory.Application</c>
    /// therefore declare no <c>IQueryable</c> and no <c>Expression&lt;Func&lt;...&gt;&gt;</c> at all -
    /// a port returns already-materialised facts, which is what lets a use case be tested with a
    /// fake and a Domain rule with plain values.
    ///
    /// Checked against the source text, because the restriction is on the <em>declaration</em>: both
    /// types live in <c>System.Linq.Expressions</c>/<c>System.Linq</c>, which the inner layers
    /// legitimately depend on everywhere for in-memory LINQ, so a type-level dependency rule like
    /// the ones in <see cref="CleanArchitectureDependencyTests"/> cannot distinguish them. This is
    /// the same precedent <see cref="TimeAcquisitionTests"/> follows for host-clock reads.
    /// </summary>
    [Theory]
    [InlineData("Inventory.Domain")]
    [InlineData("Inventory.Application")]
    public void The_inner_layers_declare_no_entity_query_expression(string project)
    {
        string[] persistenceQueryTypes = ["IQueryable", "Expression<"];

        var offenders = SourceFilesOf(project)
            .SelectMany(path => File.ReadAllLines(path)
                .Select((line, index) => (Line: line, Number: index + 1))
                .Where(line => persistenceQueryTypes.Any(token => line.Line.Contains(token, StringComparison.Ordinal)))
                .Select(line => $"{Path.GetFileName(path)}:{line.Number}"))
            .OrderBy(offender => offender, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            $"{project} must declare no IQueryable or LINQ expression tree: a query over the EF "
                + "model belongs to the Infrastructure persistence adapter behind the port, and the "
                + "port returns already-materialised facts (AGENTS.md § Architecture rules, "
                + $"docs/architecture.md § {project}). Offending line(s): {string.Join(", ", offenders)}.");
    }

    /// <summary>
    /// Acceptance criterion 5 for the named case: the completed-sale predicate over the persistence
    /// <c>NayaxSales</c> entity. <c>ReportingAdapterOwnershipTests</c> already pins that it lives in
    /// <c>Inventory.Infrastructure.Data</c> and not in the Domain; what #154 adds is that it cannot
    /// be called from outside that assembly at all. Issue #308 had to make it <c>public</c> because
    /// the costing and commission adapters that call it were still API-owned at the time, and issue
    /// #309 brought them into the same assembly without tightening the modifier again.
    ///
    /// <c>Inventory.Domain.FinancialConfiguration.NayaxTransactionStatusIds.Completed</c> stays the
    /// one authoritative "status 12 is an approved sale" rule the expression applies, which is why
    /// the expression itself is persistence and not a second status rule.
    /// </summary>
    [Fact]
    public void The_completed_sale_predicate_is_not_visible_outside_Inventory_Infrastructure()
    {
        var queries = Assert.Single(TypesNamed(InfrastructureAssembly, "EfNayaxSalesQueries"));

        Assert.Equal("Inventory.Infrastructure.Data", queries.Namespace);

        Assert.False(
            queries.IsVisible,
            "Inventory.Infrastructure.Data.EfNayaxSalesQueries must not be visible outside its own "
                + "assembly: an expression over the EF NayaxSales entity is only meaningful inside a "
                + "translated query, so every caller is an Infrastructure persistence adapter. Issue "
                + "#308 made it public for the three costing/commission adapters that were still "
                + "API-owned then; issue #309 relocated them, so the modifier can be - and is - "
                + "internal again (docs/architecture.md § Backend migration track, issue #309).");
    }

    #endregion

    #region No stale legacy service registration or reference remains (acceptance criterion 6)

    /// <summary>
    /// Acceptance criterion 6. Nothing in the project may still name the retired
    /// <c>InventoryApi.Services</c> namespace - not a dependency-injection registration in
    /// <c>Program.cs</c>, not a <c>using</c> directive, not a doc comment pointing a reader at code
    /// that no longer exists. The inner layers keep naming it in their own doc comments on purpose,
    /// recording which legacy implementation each migrated use case replaced; the point of this rule
    /// is that the composition root, which is where a stale registration would actually do harm, is
    /// free of it.
    /// </summary>
    [Fact]
    public void InventoryApi_holds_no_reference_to_the_retired_service_namespace()
    {
        var offenders = GitTrackedFiles("InventoryApi")
            .Where(path => path.EndsWith(".cs", StringComparison.Ordinal))
            .Where(path => File.ReadAllText(Path.Combine(BackendRoot, "InventoryApi", path))
                .Contains("InventoryApi.Services", StringComparison.Ordinal))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "No file under InventoryApi may reference InventoryApi.Services: the namespace is "
                + "retired, and a registration or using directive naming it is either dead or a "
                + "revived legacy service. Offending file(s): "
                + $"{string.Join(", ", offenders)}.");
    }

    #endregion

    private static bool IsInstrumentation(Type type) =>
        type.Namespace?.StartsWith(InstrumentationNamespacePrefix, StringComparison.Ordinal) == true;

    private static bool IsPersistenceType(Type type) =>
        type.Assembly == InfrastructureAssembly
            && (type.Namespace?.StartsWith("Inventory.Infrastructure.Models", StringComparison.Ordinal) == true
                || type.Namespace?.StartsWith("Inventory.Infrastructure.Data", StringComparison.Ordinal) == true);

    /// <summary>
    /// Every type a controller names in its own declared metadata: what it inherits and implements,
    /// what it is constructed with, what it stores, and what its actions accept and return. Generic
    /// arguments are unwrapped recursively, because an entity returned as
    /// <c>Task&lt;ActionResult&lt;List&lt;Product&gt;&gt;&gt;</c> is just as much the published shape
    /// as one returned bare, and arrays and by-ref parameters are reduced to their element type for
    /// the same reason.
    /// </summary>
    private static IEnumerable<Type> DeclaredSurfaceOf(Type controller)
    {
        var declared = new List<Type>();

        if (controller.BaseType is not null)
        {
            declared.Add(controller.BaseType);
        }

        declared.AddRange(controller.GetInterfaces());
        declared.AddRange(controller.GetFields(DeclaredMembers).Select(field => field.FieldType));
        declared.AddRange(controller.GetProperties(DeclaredMembers).Select(property => property.PropertyType));

        foreach (var method in controller.GetMethods(DeclaredMembers))
        {
            declared.Add(method.ReturnType);
            declared.AddRange(method.GetParameters().Select(parameter => parameter.ParameterType));
        }

        declared.AddRange(ConstructorParameterTypesOf(controller));

        return declared.SelectMany(Unwrap).Distinct();
    }

    /// <summary>
    /// Whether a controller is constructed with an <c>Inventory.Application</c> dependency: the
    /// predicate <see cref="Every_controller_is_constructed_with_an_Inventory_Application_dependency"/>
    /// applies to the API's controllers and
    /// <see cref="An_Inventory_Application_type_in_an_action_signature_does_not_satisfy_the_rule"/>
    /// applies to its fixtures, so the rule and its regression test cannot drift apart.
    /// </summary>
    private static bool IsConstructedWithAnApplicationDependency(Type controller) =>
        ConstructorDependenciesOf(controller).Any(dependency => dependency.Assembly == ApplicationAssembly);

    /// <summary>
    /// What a controller is constructed with: the parameter types of its declared constructors,
    /// unwrapped the same way as <see cref="DeclaredSurfaceOf"/>, so a dependency injected as
    /// <c>IEnumerable&lt;T&gt;</c>, <c>Lazy&lt;T&gt;</c> or an array counts as the <c>T</c> it
    /// carries. Deliberately narrower than the declared surface, which also holds what the
    /// controller inherits, stores and publishes - see the rule above for why only the constructor
    /// can answer "does this controller delegate".
    /// </summary>
    private static IEnumerable<Type> ConstructorDependenciesOf(Type controller) =>
        ConstructorParameterTypesOf(controller).SelectMany(Unwrap).Distinct();

    private static IEnumerable<Type> ConstructorParameterTypesOf(Type controller) =>
        controller.GetConstructors(DeclaredMembers)
            .SelectMany(constructor => constructor.GetParameters())
            .Select(parameter => parameter.ParameterType);

    private static IEnumerable<Type> Unwrap(Type type)
    {
        var element = type.HasElementType ? type.GetElementType() : null;
        if (element is not null)
        {
            return Unwrap(element);
        }

        return type.IsGenericType
            ? type.GetGenericArguments().SelectMany(Unwrap).Append(type.GetGenericTypeDefinition())
            : [type];
    }

    private static Type[] TypesNamed(Assembly assembly, string typeName) =>
        assembly.GetTypes()
            .Where(type => type.Name == typeName)
            .ToArray();

    private static string FullNameOf(Type type) => type.FullName ?? type.Name;

    private static IEnumerable<string> SourceFilesOf(string project) =>
        Directory.EnumerateFiles(Path.Combine(BackendRoot, project), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    /// <summary>
    /// Lists the git-tracked files under <paramref name="repoRelativeDirectory"/>, relative to that
    /// directory, rather than walking the raw filesystem - the same helper and the same reason as the
    /// sibling freezes in this directory: these rules exist to catch a reviewed, committed change, so
    /// a local build artifact or IDE scratch file must neither trip one nor satisfy one.
    /// </summary>
    private static string[] GitTrackedFiles(string repoRelativeDirectory)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo("git", $"ls-files -- {repoRelativeDirectory}")
        {
            WorkingDirectory = BackendRoot,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };

        using var process = System.Diagnostics.Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start 'git ls-files'.");

        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"'git ls-files -- {repoRelativeDirectory}' exited with code {process.ExitCode}.");
        }

        return output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim().Replace('\\', '/'))
            .Select(path => path[(repoRelativeDirectory.Replace('\\', '/').Length + 1)..])
            .ToArray();
    }

    private static readonly string BackendRoot = FindBackendRoot();

    private static string FindBackendRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !Directory.Exists(Path.Combine(current.FullName, "Inventory.Domain")))
        {
            current = current.Parent;
        }

        return current?.FullName
            ?? throw new InvalidOperationException("Could not locate the backend directory containing the Inventory.* projects.");
    }
}
