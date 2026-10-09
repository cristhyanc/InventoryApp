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
            Path.GetFullPath(Path.Combine(BackendRoot, "Inventory.IntegrationTests", "Architecture", "ProjectDependencyDirectionTests.cs")),
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

    // Issue #145's legacy-services allow-list used to live here. It froze the exact set of
    // git-tracked files in `InventoryApi/Services` - the use-case and domain logic that predated the
    // `Inventory.Domain`/`Inventory.Application` split - so that the temporary exception could shrink
    // as issues #146-#153 migrated each slice but never grow silently. Every slice has landed and
    // the folder is gone, so issue #154 replaced the allow-list with the enforcement it was a
    // placeholder for: `ApiLayerOwnershipTests` asserts that InventoryApi declares no business
    // service at all, holds neither retired layer folder, reaches a DbContext only from the
    // composition root and the operator commands, and keeps the Domain financial rules out of the
    // HTTP boundary. An empty allow-list could only ever prove that one folder name stayed unused.

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
