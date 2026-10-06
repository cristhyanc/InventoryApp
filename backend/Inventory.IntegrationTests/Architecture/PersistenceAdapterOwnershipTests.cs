using Inventory.Application.CatalogReconciliation;
using Inventory.Application.Categories;
using Inventory.Application.Commissions;
using Inventory.Application.Costing;
using Inventory.Application.Expenses;
using Inventory.Application.Imports;
using Inventory.Application.InventoryCounting;
using Inventory.Application.Machines;
using Inventory.Application.MachineStockSync;
using Inventory.Application.NayaxFeeSettings;
using Inventory.Application.PickList;
using Inventory.Application.Products;
using Inventory.Application.Purchases;
using Inventory.Application.Reorder;
using Inventory.Application.Reporting.ProductProfitability;
using Inventory.Application.SalesSync;
using Inventory.Application.Sites;
using Inventory.Application.Stock;
using Inventory.Application.SupplierOrders;
using Inventory.Application.Suppliers;
using Inventory.Application.Tenancy;
using Inventory.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InventoryApi.Tests.Architecture;

/// <summary>
/// Issue #309, Persistence 8/8 of #153: the last EF adapters leave
/// <c>InventoryApi/Adapters/Persistence</c> for <c>Inventory.Infrastructure/Persistence</c>, so the
/// composition root owns no persistence implementation at all. Issue #307 moved
/// <c>AppDbContext</c>, the EF entities and the migrations, and issue #308 the reporting fact
/// providers; this slice removes the folder those two left behind.
///
/// This is the non-reporting counterpart of <see cref="ReportingAdapterOwnershipTests"/> and uses
/// the same shape for the same reasons: the adapters are named as strings so that "an adapter went
/// back to InventoryApi" fails as a named test rather than as a compile error in this file, and so
/// that the assertion is expressible for the one <c>internal</c> type in the family.
/// </summary>
public class PersistenceAdapterOwnershipTests
{
    private static readonly System.Reflection.Assembly InfrastructureAssembly =
        typeof(Inventory.Infrastructure.AssemblyMarker).Assembly;

    private static readonly System.Reflection.Assembly ApiAssembly = typeof(Program).Assembly;

    /// <summary>
    /// Every class issue #309 relocates: the 29 <c>Ef*</c> adapters behind the Application's
    /// non-reporting persistence ports, plus the two sale-costing mapping types that only those
    /// adapters use (<c>NayaxSaleCosting</c> and its <c>internal</c> <c>NayaxCostableSale</c>).
    /// </summary>
    private static readonly string[] RelocatedAdapters =
    [
        "EfBusinessMembershipStore",
        "EfCategoryStore",
        "EfImportedReimbursementStore",
        "EfInventoryCostLedgerStore",
        "EfInventoryCostRepairStore",
        "EfInventoryCostTransitionStore",
        "EfInventoryCountAdjustmentStore",
        "EfInventoryMovementStore",
        "EfLatestNayaxSalesStore",
        "EfLocalCatalogSnapshotProvider",
        "EfMachineDashboardFactsStore",
        "EfMachineStockEventStore",
        "EfNayaxFeeRateStore",
        "EfNayaxProductCatalogImportStore",
        "EfNayaxSalesImportStore",
        "EfOperatingExpenseStore",
        "EfOutstandingSupplierOrderQuantityStore",
        "EfPickListStorageStockStore",
        "EfProductCatalogStore",
        "EfProductPurchaseCostFactsProvider",
        "EfProductPurchasePriceHistoryProvider",
        "EfProductStore",
        "EfPurchaseStore",
        "EfSaleCostingStore",
        "EfSiteCommissionStore",
        "EfSiteFactsStore",
        "EfStockAdjustmentStore",
        "EfSupplierOrderStore",
        "EfSupplierStore",
        "NayaxCostableSale",
        "NayaxSaleCosting",
    ];

    /// <summary>
    /// Each Application-owned port and the adapter <c>AddInfrastructureServices()</c> must satisfy
    /// it with. The two <c>NayaxSaleCosting</c> types are absent because they sit behind no port.
    /// </summary>
    public static TheoryData<Type, string> RegisteredPorts() => new()
    {
        { typeof(IBusinessMembershipStore), "EfBusinessMembershipStore" },
        { typeof(ICategoryStore), "EfCategoryStore" },
        { typeof(IImportedReimbursementStore), "EfImportedReimbursementStore" },
        { typeof(IInventoryCostLedgerStore), "EfInventoryCostLedgerStore" },
        { typeof(IInventoryCostRepairStore), "EfInventoryCostRepairStore" },
        { typeof(IInventoryCostTransitionStore), "EfInventoryCostTransitionStore" },
        { typeof(IInventoryCountAdjustmentStore), "EfInventoryCountAdjustmentStore" },
        { typeof(IInventoryMovementStore), "EfInventoryMovementStore" },
        { typeof(ILatestNayaxSalesStore), "EfLatestNayaxSalesStore" },
        { typeof(ILocalCatalogSnapshotProvider), "EfLocalCatalogSnapshotProvider" },
        { typeof(IMachineDashboardFactsStore), "EfMachineDashboardFactsStore" },
        { typeof(IMachineStockEventStore), "EfMachineStockEventStore" },
        { typeof(INayaxFeeRateStore), "EfNayaxFeeRateStore" },
        { typeof(INayaxProductCatalogImportStore), "EfNayaxProductCatalogImportStore" },
        { typeof(INayaxSalesImportStore), "EfNayaxSalesImportStore" },
        { typeof(IOperatingExpenseStore), "EfOperatingExpenseStore" },
        { typeof(IOutstandingSupplierOrderQuantityStore), "EfOutstandingSupplierOrderQuantityStore" },
        { typeof(IPickListStorageStockStore), "EfPickListStorageStockStore" },
        { typeof(IProductCatalogStore), "EfProductCatalogStore" },
        { typeof(IProductPurchaseCostFactsProvider), "EfProductPurchaseCostFactsProvider" },
        { typeof(IProductPurchasePriceHistoryProvider), "EfProductPurchasePriceHistoryProvider" },
        { typeof(IProductStore), "EfProductStore" },
        { typeof(IPurchaseStore), "EfPurchaseStore" },
        { typeof(ISaleCostingStore), "EfSaleCostingStore" },
        { typeof(ISiteCommissionStore), "EfSiteCommissionStore" },
        { typeof(ISiteFactsStore), "EfSiteFactsStore" },
        { typeof(IStockAdjustmentStore), "EfStockAdjustmentStore" },
        { typeof(ISupplierOrderStore), "EfSupplierOrderStore" },
        { typeof(ISupplierStore), "EfSupplierStore" },
    };

    [Theory]
    [MemberData(nameof(RelocatedAdapterNames))]
    public void Every_persistence_adapter_is_declared_in_Inventory_Infrastructure(string adapter)
    {
        var declared = TypesNamed(InfrastructureAssembly, adapter);

        Assert.True(
            declared.Length == 1,
            $"Inventory.Infrastructure must declare exactly one {adapter}; found {declared.Length}. "
                + "Every EF persistence adapter is Infrastructure-owned since issue #309 "
                + "(docs/architecture.md § Inventory.Infrastructure).");

        Assert.StartsWith("Inventory.Infrastructure.", declared[0].Namespace, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(RelocatedAdapterNames))]
    public void No_persistence_adapter_is_declared_in_InventoryApi(string adapter)
    {
        var declared = TypesNamed(ApiAssembly, adapter);

        Assert.True(
            declared.Length == 0,
            $"InventoryApi must not declare {adapter}: the EF persistence adapters left "
                + "InventoryApi/Adapters/Persistence for Inventory.Infrastructure/Persistence in "
                + "issue #309, which completes #153's persistence move. Offending type(s): "
                + $"{string.Join(", ", declared.Select(type => type.FullName))}.");
    }

    [Theory]
    [MemberData(nameof(RegisteredPorts))]
    public void AddInfrastructureServices_registers_every_persistence_port(Type port, string adapter)
    {
        var services = new ServiceCollection();
        services.AddInfrastructureServices();

        var registrations = services.Where(descriptor => descriptor.ServiceType == port).ToArray();

        Assert.True(
            registrations.Length == 1,
            $"AddInfrastructureServices() must register exactly one {port.Name}; found "
                + $"{registrations.Length}. Issue #309 moved these registrations out of Program.cs, "
                + "so a host that calls AddInfrastructureServices() gets the persistence adapters "
                + "without wiring each one itself.");

        var registration = registrations[0];

        Assert.Equal(adapter, registration.ImplementationType?.Name);
        Assert.Equal(InfrastructureAssembly, registration.ImplementationType?.Assembly);

        // Scoped, exactly as the Program.cs registrations these replace were: every adapter takes
        // the request-scoped AppDbContext, and the use cases that compose several of them rely on
        // sharing one context - and therefore one transaction and one change tracker - per request.
        Assert.Equal(ServiceLifetime.Scoped, registration.Lifetime);
    }

    /// <summary>
    /// Acceptance criterion: <c>Program.cs</c> no longer registers these adapters. Checked against
    /// the source text, because the composition root could still name a public Infrastructure type
    /// and register it a second time, which metadata alone would not reveal.
    /// </summary>
    [Fact]
    public void Program_registers_no_persistence_adapter()
    {
        var program = File.ReadAllText(Path.Combine(BackendRoot, "InventoryApi", "Program.cs"));

        var offenders = RelocatedAdapters
            .Where(adapter => program.Contains(adapter, StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "Program.cs must not name the EF persistence adapters: AddInfrastructureServices() "
                + "registers them since issue #309. Remove the registration(s) for "
                + $"{string.Join(", ", offenders)}.");
    }

    /// <summary>
    /// Acceptance criterion: <c>InventoryApi/Adapters/Persistence</c> no longer exists. Checked over
    /// git-tracked files for the same reason
    /// <see cref="ProjectDependencyDirectionTests.InventoryApi_owns_no_db_context_persistence_model_or_migration"/>
    /// is: an untracked scratch file must neither trip this freeze nor satisfy it.
    /// </summary>
    [Fact]
    public void InventoryApi_owns_no_persistence_adapter_folder()
    {
        var tracked = GitTrackedFiles(Path.Combine("InventoryApi", "Adapters", "Persistence"));

        Assert.True(
            tracked.Length == 0,
            "InventoryApi/Adapters/Persistence must not exist: the EF adapters are owned by "
                + "Inventory.Infrastructure (issue #309, docs/architecture.md § "
                + "Inventory.Infrastructure), and a new adapter belongs there beside AppDbContext. "
                + $"Tracked file(s) found: {string.Join(", ", tracked)}.");
    }

    /// <summary>
    /// The positive half of the rule above: the relocated adapters really are under
    /// <c>Inventory.Infrastructure/Persistence</c>. Without this, deleting them outright would
    /// satisfy the negative assertion.
    /// </summary>
    [Fact]
    public void Inventory_Infrastructure_owns_the_persistence_adapter_folder()
    {
        var tracked = GitTrackedFiles(Path.Combine("Inventory.Infrastructure", "Persistence"));

        var missing = RelocatedAdapters
            .Where(adapter => adapter != "NayaxCostableSale")
            .Where(adapter => !tracked.Contains($"{adapter}.cs", StringComparer.Ordinal))
            .ToArray();

        Assert.True(
            missing.Length == 0,
            "Inventory.Infrastructure/Persistence must hold one file per relocated adapter "
                + $"(issue #309). Missing: {string.Join(", ", missing.Select(name => $"{name}.cs"))}.");
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
