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
}
