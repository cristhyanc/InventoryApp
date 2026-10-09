using Inventory.Application.NayaxProcessingFees;
using Inventory.Application.Reporting.Bookkeeping;
using Inventory.Application.Reporting.Dashboard;
using Inventory.Application.Reporting.Daily;
using Inventory.Application.Reporting.Gst;
using Inventory.Application.Reporting.MachineProfitability;
using Inventory.Application.Reporting.ProductProfitability;
using Inventory.Application.Reporting.Reconciliation;
using Inventory.Application.Reporting.Transactions;
using Inventory.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InventoryApi.Tests.Architecture;

/// <summary>
/// Issue #308, Persistence 7/8 of #153: the reporting EF fact providers and their shared query
/// helpers are owned by <c>Inventory.Infrastructure</c> and registered by
/// <c>AddInfrastructureServices()</c>, not by the composition root. Issue #307 moved
/// <c>AppDbContext</c>, the EF entities and the migrations ahead of them, which removed the only
/// dependency reason these adapters had to be API-owned at all.
///
/// The adapters are named here as strings rather than as type references on purpose. These are
/// placement assertions, and a compile-time reference would turn "the adapter went back to
/// InventoryApi" - the regression this guards - into a build error in this file instead of a named
/// test failure, and would make the same assertion impossible to state for the two <c>internal</c>
/// helpers. The same source/metadata-scanning precedent is used by
/// <see cref="ProjectDependencyDirectionTests.InventoryApi_owns_no_db_context_persistence_model_or_migration"/>
/// and <see cref="TimeAcquisitionTests"/>.
/// </summary>
public class ReportingAdapterOwnershipTests
{
    private static readonly System.Reflection.Assembly InfrastructureAssembly =
        typeof(Inventory.Infrastructure.AssemblyMarker).Assembly;

    private static readonly System.Reflection.Assembly DomainAssembly =
        typeof(Inventory.Domain.AssemblyMarker).Assembly;

    private static readonly System.Reflection.Assembly ApiAssembly = typeof(Program).Assembly;

    /// <summary>
    /// The twelve classes issue #308 names: the ten report facts providers plus the two shared EF
    /// query helpers they and the costing/commission adapters call (those were still API-owned at
    /// the time; issue #309 moved them into <c>Inventory.Infrastructure.Persistence</c> too).
    /// </summary>
    private static readonly string[] RelocatedAdapters =
    [
        "EfBookkeepingReportFactsProvider",
        "EfDailyReportFactsProvider",
        "EfDashboardReportFactsProvider",
        "EfGstReportFactsProvider",
        "EfInventoryValuationFactsProvider",
        "EfMachineProfitabilityReportFactsProvider",
        "EfNayaxProcessingFeeFactsProvider",
        "EfNayaxSalesQueries",
        "EfProductProfitabilityReportFactsProvider",
        "EfReconciliationReportFactsProvider",
        "EfReportingSharedQueries",
        "EfTransactionSalesReportFactsProvider",
    ];

    /// <summary>
    /// Each Application-owned reporting port and the adapter <c>AddInfrastructureServices()</c> must
    /// satisfy it with. <c>EfReportingSharedQueries</c>/<c>EfNayaxSalesQueries</c> are absent because
    /// they are static helpers behind no port.
    /// </summary>
    public static TheoryData<Type, string> RegisteredPorts() => new()
    {
        { typeof(IBookkeepingReportFactsProvider), "EfBookkeepingReportFactsProvider" },
        { typeof(IDailyReportFactsProvider), "EfDailyReportFactsProvider" },
        { typeof(IDashboardReportFactsProvider), "EfDashboardReportFactsProvider" },
        { typeof(IGstReportFactsProvider), "EfGstReportFactsProvider" },
        { typeof(IInventoryValuationFactsProvider), "EfInventoryValuationFactsProvider" },
        { typeof(IMachineProfitabilityReportFactsProvider), "EfMachineProfitabilityReportFactsProvider" },
        { typeof(INayaxProcessingFeeFactsProvider), "EfNayaxProcessingFeeFactsProvider" },
        { typeof(IProductProfitabilityReportFactsProvider), "EfProductProfitabilityReportFactsProvider" },
        { typeof(IReconciliationReportFactsProvider), "EfReconciliationReportFactsProvider" },
        { typeof(ITransactionSalesReportFactsProvider), "EfTransactionSalesReportFactsProvider" },
    };

    [Theory]
    [MemberData(nameof(RelocatedAdapterNames))]
    public void Every_reporting_adapter_is_declared_in_Inventory_Infrastructure(string adapter)
    {
        var declared = TypesNamed(InfrastructureAssembly, adapter);

        Assert.True(
            declared.Length == 1,
            $"Inventory.Infrastructure must declare exactly one {adapter}; found {declared.Length}. "
                + "The reporting EF adapters are Infrastructure-owned since issue #308 "
                + "(docs/architecture.md § Inventory.Infrastructure).");

        Assert.StartsWith("Inventory.Infrastructure.", declared[0].Namespace, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(RelocatedAdapterNames))]
    public void No_reporting_adapter_is_declared_in_InventoryApi(string adapter)
    {
        var declared = TypesNamed(ApiAssembly, adapter);

        Assert.True(
            declared.Length == 0,
            $"InventoryApi must not declare {adapter}: the reporting EF adapters left "
                + "InventoryApi/Adapters/Persistence for Inventory.Infrastructure in issue #308, and "
                + "the composition root owns no persistence adapter for them. Offending type(s): "
                + $"{string.Join(", ", declared.Select(type => type.FullName))}.");
    }

    [Theory]
    [MemberData(nameof(RegisteredPorts))]
    public void AddInfrastructureServices_registers_every_reporting_facts_provider(Type port, string adapter)
    {
        var services = new ServiceCollection();
        services.AddInfrastructureServices();

        var registrations = services.Where(descriptor => descriptor.ServiceType == port).ToArray();

        Assert.True(
            registrations.Length == 1,
            $"AddInfrastructureServices() must register exactly one {port.Name}; found "
                + $"{registrations.Length}. Issue #308 moved these registrations out of Program.cs, "
                + "so a host that calls AddInfrastructureServices() gets the reporting adapters "
                + "without wiring each one itself.");

        var registration = registrations[0];

        Assert.Equal(adapter, registration.ImplementationType?.Name);
        Assert.Equal(InfrastructureAssembly, registration.ImplementationType?.Assembly);

        // Scoped, exactly as the Program.cs registrations these replace were: the adapters take the
        // request-scoped AppDbContext, so relocating them must not change a lifetime a consumer
        // could notice.
        Assert.Equal(ServiceLifetime.Scoped, registration.Lifetime);
    }

    /// <summary>
    /// Acceptance criterion: <c>Program.cs</c> no longer registers these adapters. Checked against
    /// the source text because the composition root could still name a public Infrastructure type
    /// and register it a second time, which metadata alone would not reveal.
    /// </summary>
    [Fact]
    public void Program_registers_no_reporting_facts_provider()
    {
        var program = File.ReadAllText(Path.Combine(BackendRoot, "InventoryApi", "Program.cs"));

        var offenders = RelocatedAdapters
            .Where(adapter => program.Contains(adapter, StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "Program.cs must not name the reporting EF adapters: AddInfrastructureServices() "
                + "registers them since issue #308. Remove the registration(s) for "
                + $"{string.Join(", ", offenders)}.");
    }

    /// <summary>
    /// Acceptance criterion: the completed-sale predicate lives in Infrastructure persistence, not
    /// in Domain. <c>Inventory.Domain.FinancialConfiguration.NayaxTransactionStatusIds.Completed</c>
    /// stays the one authoritative "status 12 is an approved sale" rule (AGENTS.md § Sales, payment
    /// methods and statuses); what must not follow it into Domain is the EF
    /// <c>Expression&lt;Func&lt;NayaxSales, bool&gt;&gt;</c> that applies it to a persistence entity
    /// inside a translated query.
    /// </summary>
    [Fact]
    public void Completed_sale_predicate_lives_in_Infrastructure_persistence_not_in_Domain()
    {
        var queries = Assert.Single(TypesNamed(InfrastructureAssembly, "EfNayaxSalesQueries"));

        Assert.Equal("Inventory.Infrastructure.Data", queries.Namespace);
        Assert.NotEmpty(queries.GetMember(
            "CompletedSalePredicate",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static));

        var domainOffenders = DomainAssembly.GetTypes()
            .SelectMany(type => type.GetMembers(
                System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Static
                    | System.Reflection.BindingFlags.Instance))
            .Where(member => member.Name == "CompletedSalePredicate")
            .Select(member => $"{member.DeclaringType?.FullName}.{member.Name}")
            .ToArray();

        Assert.True(
            domainOffenders.Length == 0,
            "Inventory.Domain must not declare the completed-sale query predicate: a predicate over "
                + "the EF NayaxSales entity is persistence, and Domain calculations stay free of EF "
                + $"Core (AGENTS.md § Architecture rules). Offending member(s): {string.Join(", ", domainOffenders)}.");
    }

    public static TheoryData<string> RelocatedAdapterNames()
    {
        var data = new TheoryData<string>();
        foreach (var adapter in RelocatedAdapters)
        {
            data.Add(adapter);
        }

        return data;
    }

    private static Type[] TypesNamed(System.Reflection.Assembly assembly, string typeName) =>
        assembly.GetTypes()
            .Where(type => type.Name == typeName)
            .ToArray();

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
