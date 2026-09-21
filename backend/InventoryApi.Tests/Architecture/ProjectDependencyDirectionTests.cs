using System;
using System.IO;
using System.Linq;
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
