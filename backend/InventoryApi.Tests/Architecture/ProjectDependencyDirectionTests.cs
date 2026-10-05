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
    /// costing, machine/site/product/purchase/stock orchestration, ...) that predates
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
    ///
    /// Shrinking it follows the same rule: issue #299 removed <c>ImportService.Xml.cs</c> in the same
    /// change that migrated the pending reimbursement XML import to
    /// <c>Inventory.Application.Imports.ImportPendingReimbursementXmlFiles</c>, issue #300
    /// removed <c>ImportService.Products.cs</c> in the same change that migrated the Nayax product
    /// catalogue import to <c>Inventory.Application.Imports.ImportNayaxProductCatalog</c>, and issue
    /// #303 removed <c>ProductService.cs</c>/<c>Interfaces/IProductService.cs</c> in the same change
    /// that pointed <c>ProductsController</c> straight at the Products use cases and gave it an
    /// API-owned response DTO, and issue #304 removed <c>PurchaseService.cs</c>/<c>SupplierOrderService.cs</c>
    /// and their interfaces in the same change that did the same for
    /// <c>PurchasesController</c>/<c>SupplierOrdersController</c>. Issue #301, the last child of #151,
    /// removed the import feature entirely: <c>ImportService.cs</c>, <c>ImportService.NayaxSales.cs</c>,
    /// <c>Interfaces/IImportService.cs</c> and <c>NayaxSalesWorkbook.cs</c> left with the uploaded
    /// Nayax sales import's move to <c>Inventory.Application.Imports.ImportNayaxSales</c>, and
    /// <c>NayaxProductMatcher.cs</c> left with them because its last callers now use the Domain
    /// <c>Inventory.Domain.Reporting.ProductMatching.ProductMatcher</c> directly.
    /// Issue #302, the first child of #153, removed <c>MachineService.cs</c>, <c>SiteService.cs</c>
    /// and <c>Interfaces/IMachineService.cs</c>/<c>Interfaces/ISiteService.cs</c> in the same change
    /// that pointed <c>MachinesController</c>/<c>SitesController</c> straight at the Machines and
    /// Sites use cases and gave the machine endpoints API-owned response DTOs, which also emptied
    /// the <c>Interfaces</c> folder.
    ///
    /// Nothing is left. The last entry, <c>SiteNameResolver.cs</c>, went with the non-EF adapter
    /// relocation (issue #306): the pure site-name-from-machine-names helper and the
    /// <c>Adapters/Persistence/SiteNameResolverAdapter</c> wrapper that implemented
    /// <c>Inventory.Application.Sites.ISiteNameResolver</c> for it merged into
    /// <c>Inventory.Infrastructure.Sites.SiteNameResolver</c>, which
    /// <c>AddInfrastructureServices()</c> registers and which
    /// <c>EfTransactionSalesReportFactsProvider</c> calls. <c>InventoryApi/Services</c> is gone, so
    /// the allow-list is empty and this test now asserts that the folder stays gone: a new file
    /// under it fails here and must go to <c>Inventory.Application</c>/<c>Inventory.Domain</c> (use
    /// case or domain logic) or <c>Inventory.Infrastructure</c> (an adapter) instead. Reviving the
    /// folder has to be a conscious edit to this list and to the docs/architecture.md exception it
    /// documents. What remains of that exception is the API-owned EF adapter family under
    /// <c>InventoryApi/Adapters/Persistence</c>: <c>AppDbContext</c>, the EF entities and the
    /// migrations went ahead of it into <c>Inventory.Infrastructure</c> in issue #307, and the
    /// adapters follow in Persistence 7/8 and 8/8 of #153.
    /// </summary>
    [Fact]
    public void Only_the_documented_legacy_services_remain_in_InventoryApi_Services()
    {
        string[] allowedRelativePaths = [];

        var actualRelativePaths = GitTrackedFiles(Path.Combine("InventoryApi", "Services"))
            .Where(path => path.EndsWith(".cs", StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(allowedRelativePaths.OrderBy(name => name, StringComparer.Ordinal), actualRelativePaths);
    }

    /// <summary>
    /// Issue #305, the last controller slice of #153: no file under <c>InventoryApi/Controllers</c>
    /// names the EF entity namespace any more. The HTTP boundary binds and serialises API-owned
    /// contracts from <c>InventoryApi.DTOs</c> and calls
    /// <c>Inventory.Application</c>/<c>Inventory.Domain</c>; reaching for a persistence entity there
    /// is what let an EF model become the published wire shape in the first place, and it is exactly
    /// the coupling that would have made moving <c>AppDbContext</c> into
    /// <c>Inventory.Infrastructure</c> a contract change instead of the relocation issue #307 was
    /// able to make it.
    ///
    /// The namespace searched for is the post-#307 one, <c>Inventory.Infrastructure.Models</c>. Both
    /// the <c>using</c> directive and a fully qualified <c>Inventory.Infrastructure.Models.X</c>
    /// reference fail here, so the rule cannot be satisfied by qualifying the type instead of
    /// importing it. The check is over git-tracked files for the same reason the legacy-services
    /// freeze is: an untracked scratch controller must neither trip it nor satisfy it.
    /// </summary>
    [Fact]
    public void No_controller_references_the_persistence_models()
    {
        var controllersDirectory = Path.Combine("InventoryApi", "Controllers");

        var offendingFiles = GitTrackedFiles(controllersDirectory)
            .Where(path => path.EndsWith(".cs", StringComparison.Ordinal))
            .Where(path => File.ReadAllText(Path.Combine(BackendRoot, "InventoryApi", "Controllers", path))
                .Contains("Inventory.Infrastructure.Models", StringComparison.Ordinal))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            offendingFiles.Length == 0,
            "A controller must not reach for the EF persistence model: bind and serialise the "
                + "API-owned contracts in InventoryApi.DTOs and call the Application use cases "
                + "instead (docs/architecture.md § InventoryApi). Offending file(s) under "
                + $"{controllersDirectory}: {string.Join(", ", offendingFiles)}.");
    }

    /// <summary>
    /// Issue #307, Persistence 6/8 of #153: the persistence layer is owned by
    /// <c>Inventory.Infrastructure</c>, not by the API. <c>AppDbContext</c>, the
    /// <c>BusinessOwnershipEnforcer</c> that guards every write, the EF entities and the EF
    /// migrations all left <c>InventoryApi/Data</c>, <c>InventoryApi/Models</c> and
    /// <c>InventoryApi/Migrations</c>, so those three folders must stay gone: the composition root
    /// configures the provider and the connection string, and owns no persistence model.
    ///
    /// Reviving any of them - a "just one entity" model class, a second context, a migration
    /// generated with the wrong <c>--project</c> - fails here and must instead go to
    /// <c>Inventory.Infrastructure</c>, where the migrations path both validation scripts exclude
    /// from <c>dotnet format</c> also points.
    /// </summary>
    [Fact]
    public void InventoryApi_owns_no_db_context_persistence_model_or_migration()
    {
        foreach (var folder in new[] { "Data", "Models", "Migrations" })
        {
            var tracked = GitTrackedFiles(Path.Combine("InventoryApi", folder));

            Assert.True(
                tracked.Length == 0,
                $"InventoryApi/{folder} must not exist: AppDbContext, the EF entities and the EF "
                    + "migrations are owned by Inventory.Infrastructure (issue #307, "
                    + "docs/architecture.md § Inventory.Infrastructure). Tracked file(s) found: "
                    + $"{string.Join(", ", tracked)}.");
        }
    }

    /// <summary>
    /// The positive half of the rule above: the relocated persistence files really are in
    /// <c>Inventory.Infrastructure</c>. Without this, deleting them outright would satisfy the
    /// negative assertion.
    /// </summary>
    [Fact]
    public void Inventory_Infrastructure_owns_the_db_context_the_entities_and_the_migrations()
    {
        var dataFiles = GitTrackedFiles(Path.Combine("Inventory.Infrastructure", "Data"));
        Assert.Contains("AppDbContext.cs", dataFiles);
        Assert.Contains("BusinessOwnershipEnforcer.cs", dataFiles);

        var modelFiles = GitTrackedFiles(Path.Combine("Inventory.Infrastructure", "Models"));
        Assert.Contains("IBusinessOwned.cs", modelFiles);
        Assert.Contains("Product.cs", modelFiles);

        var migrationFiles = GitTrackedFiles(Path.Combine("Inventory.Infrastructure", "Migrations"));
        Assert.Contains("AppDbContextModelSnapshot.cs", migrationFiles);

        // EF Core is an adapter-layer dependency (AGENTS.md § Architecture rules): the project
        // that owns the context owns the package reference too.
        Assert.Contains(
            "Microsoft.EntityFrameworkCore",
            PackageReferencesOf("Inventory.Infrastructure", "Inventory.Infrastructure.csproj"));
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
