using System.Xml.Linq;
using Xunit;

namespace InventoryApi.Tests.Architecture;

// These tests read the .csproj files directly rather than compiled assemblies: a skeleton
// project whose code does not yet use a referenced project would have its assembly reference
// trimmed by the compiler, so only the declared <ProjectReference>/<PackageReference> items
// reliably reflect the intended dependency graph.
public class ProjectDependencyDirectionTests
{
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

    private static string[] ProjectReferencesOf(string projectFolder, string csprojFileName)
    {
        var path = Path.Combine(BackendRoot, projectFolder, csprojFileName);
        var document = XDocument.Load(path);
        return document.Descendants("ProjectReference")
            .Select(e => Path.GetFileNameWithoutExtension(e.Attribute("Include")!.Value.Replace('\\', '/')))
            .ToArray();
    }

    private static string[] PackageReferencesOf(string projectFolder, string csprojFileName)
    {
        var path = Path.Combine(BackendRoot, projectFolder, csprojFileName);
        var document = XDocument.Load(path);
        return document.Descendants("PackageReference")
            .Select(e => e.Attribute("Include")?.Value ?? string.Empty)
            .ToArray();
    }

    [Fact]
    public void Domain_references_no_other_project()
    {
        var references = ProjectReferencesOf("Inventory.Domain", "Inventory.Domain.csproj");

        Assert.Empty(references);
    }

    [Fact]
    public void Domain_has_no_forbidden_package_dependencies()
    {
        var forbiddenPrefixes = new[]
        {
            "Microsoft.AspNetCore",
            "Microsoft.EntityFrameworkCore",
            "ClosedXML",
            "System.Net.Http",
            "Microsoft.Extensions.Configuration",
        };

        foreach (var package in PackageReferencesOf("Inventory.Domain", "Inventory.Domain.csproj"))
        {
            Assert.DoesNotContain(forbiddenPrefixes, prefix => package.StartsWith(prefix, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Application_references_only_domain()
    {
        var references = ProjectReferencesOf("Inventory.Application", "Inventory.Application.csproj");

        Assert.Equal(new[] { "Inventory.Domain" }, references);
    }

    [Fact]
    public void Application_has_no_forbidden_package_dependencies()
    {
        var forbiddenPrefixes = new[]
        {
            "Microsoft.AspNetCore",
            "Microsoft.EntityFrameworkCore",
            "ClosedXML",
            "System.Net.Http",
            "Microsoft.Extensions.Configuration",
        };

        foreach (var package in PackageReferencesOf("Inventory.Application", "Inventory.Application.csproj"))
        {
            Assert.DoesNotContain(forbiddenPrefixes, prefix => package.StartsWith(prefix, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Infrastructure_references_only_application_and_domain()
    {
        var references = ProjectReferencesOf("Inventory.Infrastructure", "Inventory.Infrastructure.csproj");

        Assert.Equal(
            new[] { "Inventory.Application", "Inventory.Domain" },
            references.OrderBy(name => name, StringComparer.Ordinal));
    }

    [Fact]
    public void Api_references_only_application_and_infrastructure()
    {
        var references = ProjectReferencesOf("InventoryApi", "InventoryApi.csproj");

        Assert.Equal(
            new[] { "Inventory.Application", "Inventory.Infrastructure" },
            references.OrderBy(name => name, StringComparer.Ordinal));
    }

    [Fact]
    public void No_other_source_file_references_the_removed_legacy_reporting_service()
    {
        // Issue #92 removed every caller of the legacy InventoryApi.Services.ReportingService/
        // IReportingService (DI registration, ReportsController, and every test) in favour of the
        // migrated Inventory.Application.Reporting.* use cases and GetReportExportRows. The two
        // legacy files themselves are excluded here because their own class/interface declaration
        // necessarily contains their own name; this file is excluded because it names them in this
        // comment and in the exclusion list itself. Together they prove nothing else depends on them.
        var legacyFiles = new[]
        {
            Path.GetFullPath(Path.Combine(BackendRoot, "InventoryApi", "Services", "ReportingService.cs")),
            Path.GetFullPath(Path.Combine(BackendRoot, "InventoryApi", "Services", "Interfaces", "IReportingService.cs")),
            Path.GetFullPath(Path.Combine(BackendRoot, "InventoryApi.Tests", "Architecture", "ProjectDependencyDirectionTests.cs")),
        };

        var offendingFiles = Directory.EnumerateFiles(BackendRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !legacyFiles.Contains(Path.GetFullPath(path), StringComparer.OrdinalIgnoreCase))
            .Where(path => File.ReadAllText(path).Contains("ReportingService", StringComparison.Ordinal))
            .ToArray();

        Assert.Empty(offendingFiles);
    }

    [Fact]
    public void No_production_project_references_the_api_project()
    {
        foreach (var (folder, file) in new[]
        {
            ("Inventory.Domain", "Inventory.Domain.csproj"),
            ("Inventory.Application", "Inventory.Application.csproj"),
            ("Inventory.Infrastructure", "Inventory.Infrastructure.csproj"),
        })
        {
            Assert.DoesNotContain("InventoryApi", ProjectReferencesOf(folder, file));
        }
    }

    /// <summary>
    /// Issue #145: <c>InventoryApi/Services</c> is use-case/domain business logic (import,
    /// costing, commissions, machine/site/product/purchase/stock orchestration, ...) that predates
    /// the <c>Inventory.Domain</c>/<c>Inventory.Application</c> split and has not migrated yet - the
    /// temporary, explicitly documented exception described in docs/architecture.md § Backend
    /// target. Migrating all of it is tracked feature-by-feature by issues #146-#151, with full
    /// removal of this exception tracked by #153/#154; ripping it out here would be a much larger,
    /// riskier change than "strengthen the architecture tests" and is explicitly out of scope for
    /// this issue.
    ///
    /// What this issue does require is that the exception stop growing silently. This test freezes
    /// the exact set of files this migration found already in that folder. A new slice's use-case
    /// or domain logic must go into <c>Inventory.Application</c>/<c>Inventory.Domain</c> instead of
    /// copying the legacy pattern; the moment any file is added to, removed from, or renamed in
    /// <c>InventoryApi/Services</c>, this test fails and names the mismatch, forcing that change to
    /// be a conscious update to both this allow-list and the docs/architecture.md exception it
    /// documents, rather than a silent expansion of code Clean Architecture no longer allows to grow.
    /// </summary>
    [Fact]
    public void Only_the_documented_legacy_services_remain_in_InventoryApi_Services()
    {
        string[] allowedRelativePaths =
        [
            "CategoryService.cs",
            "EffectiveFinancialConfiguration.cs",
            "ImportService.cs",
            "ImportService.NayaxSales.cs",
            "ImportService.Products.cs",
            "ImportService.Xml.cs",
            "InsufficientStockException.cs",
            "Interfaces/ICategoryService.cs",
            "Interfaces/IImportService.cs",
            "Interfaces/IInventoryCostRebuildService.cs",
            "Interfaces/IInventoryCostService.cs",
            "Interfaces/IInventoryCostTransitionService.cs",
            "Interfaces/IMachineService.cs",
            "Interfaces/INayaxProcessingFeeService.cs",
            "Interfaces/IProductService.cs",
            "Interfaces/IPurchaseService.cs",
            "Interfaces/ISaleCostingService.cs",
            "Interfaces/ISiteCommissionService.cs",
            "Interfaces/ISiteService.cs",
            "Interfaces/IStockService.cs",
            "Interfaces/ISupplierOrderService.cs",
            "Interfaces/ISupplierService.cs",
            "InventoryCostRebuildResult.cs",
            "InventoryCostRebuildService.cs",
            "InventoryCostService.cs",
            "InventoryCostTransitionService.cs",
            "MachineService.cs",
            "NayaxProcessingFeeService.cs",
            "NayaxProductMatcher.cs",
            "NayaxSalesWorkbook.cs",
            "NayaxTransactionStatusClassifier.cs",
            "PaymentMethodClassifier.cs",
            "ProductService.cs",
            "PurchaseService.cs",
            "SaleCostingService.cs",
            "SiteCommissionCalculator.cs",
            "SiteCommissionService.cs",
            "SiteNameResolver.cs",
            "SiteService.cs",
            "StockService.cs",
            "SupplierOrderService.cs",
            "SupplierService.cs",
        ];

        var actualRelativePaths = GitTrackedFiles(Path.Combine("InventoryApi", "Services"))
            .Where(path => path.EndsWith(".cs", StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(allowedRelativePaths.OrderBy(name => name, StringComparer.Ordinal), actualRelativePaths);
    }

    /// <summary>
    /// Lists the git-tracked files under <paramref name="repoRelativeDirectory"/> (relative to
    /// <see cref="BackendRoot"/>'s parent, the repository root), rather than walking the raw
    /// filesystem. This freeze exists to catch a reviewed, committed change - a local build
    /// artifact, IDE scratch file, or OS metadata file left in the working tree must not trip it
    /// (and must not silently satisfy it either).
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
}
