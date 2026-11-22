using System.Text.Json;
using System.Xml.Linq;
using Xunit;
using Xunit.Abstractions;

namespace InventoryApi.Tests.Infrastructure.Sqlite;

/// <summary>
/// The whole SQLitePCLRaw family resolves to one stable version across the backend (issue #1290).
///
/// <para><strong>Why a test and not a reviewed restore log.</strong>
/// <c>SQLitePCLRaw.core</c>, <c>lib.e_sqlite3</c>, <c>bundle_e_sqlite3</c> and
/// <c>provider.e_sqlite3</c> are four independent NuGet package ids, and NuGet enforces no
/// version equality between them: only two of them are pinned directly (<c>lib.e_sqlite3</c> in
/// <c>InventoryApi.csproj</c>, <c>core</c> in <c>Inventory.Infrastructure.csproj</c>), while the
/// other two arrive transitively through <c>Microsoft.Data.Sqlite</c>. A green build therefore
/// proves only that no downgrade conflict was raised, not that the four landed on the same
/// version - and a mixed family is how the managed provider and the native engine drift apart.
/// <see cref="NativeSqliteLibraryTests"/> holds the engine floor and the native-file presence; this
/// is the alignment invariant beside them, checked on every <c>scripts/validate.sh</c> run instead
/// of by a one-time manual grep.</para>
///
/// <para><strong>What it reads.</strong> The <c>obj/project.assets.json</c> that <c>dotnet
/// restore</c> writes for every project of <c>backend/InventoryApi/InventoryApi.slnx</c> - the
/// authoritative record of what restore actually resolved, in both its <c>targets</c> (per target
/// framework) and <c>libraries</c> (flattened) sections. A missing assets file fails the test
/// rather than passing it vacuously, and the resolved package/version set is written to the test
/// output, which is where a pull request's figures can be quoted from.</para>
/// </summary>
public sealed class SqlitePclRawPackageAlignmentTests
{
    /// <summary>
    /// Every package of the family. All four must be in the restored graph of the projects listed
    /// in <see cref="ProjectsRequiringFamilyMembers"/>; naming them explicitly is what stops the
    /// check from passing because a package silently left the graph altogether.
    /// </summary>
    private static readonly string[] ExpectedFamily =
    [
        "SQLitePCLRaw.bundle_e_sqlite3",
        "SQLitePCLRaw.core",
        "SQLitePCLRaw.lib.e_sqlite3",
        "SQLitePCLRaw.provider.e_sqlite3",
    ];

    /// <summary>The one package <c>Inventory.Infrastructure</c> needs, real and synthetic alike.</summary>
    private static readonly string[] CoreOnly = ["SQLitePCLRaw.core"];

    /// <summary>The family minus the two packages that arrive transitively, for the negative case.</summary>
    private static readonly string[] CoreAndNativeLibraryOnly =
        ["SQLitePCLRaw.core", "SQLitePCLRaw.lib.e_sqlite3"];

    /// <summary>
    /// <c>Inventory.Infrastructure</c> deliberately references only the raw API the platform
    /// diagnostics authorizer needs (<c>SQLitePCLRaw.core</c>) and the <c>.Core</c> ADO.NET driver,
    /// so no second native library is bundled there. The full family belongs to
    /// <c>InventoryApi</c>, whose publish output carries
    /// <c>runtimes/&lt;rid&gt;/native/[lib]e_sqlite3.*</c>, and to the test project, which is the
    /// process that actually opens a connection.
    /// </summary>
    private static readonly (string Project, string[] RequiredPackages)[] ProjectsRequiringFamilyMembers =
    [
        ("InventoryApi", ExpectedFamily),
        ("Inventory.IntegrationTests", ExpectedFamily),
        ("Inventory.Infrastructure", CoreOnly),
    ];

    private static readonly string BackendRoot = FindBackendRoot();

    private readonly ITestOutputHelper _output;

    public SqlitePclRawPackageAlignmentTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Every_restored_backend_project_resolves_the_SQLitePCLRaw_family_to_one_stable_version()
    {
        var resolvedByProject = new Dictionary<string, IReadOnlyList<ResolvedPackage>>(StringComparer.Ordinal);

        foreach (var (project, directory) in SolutionProjectDirectories())
        {
            var assetsPath = Path.Combine(directory, "obj", "project.assets.json");

            Assert.True(
                File.Exists(assetsPath),
                $"{project} has no restored assets file at {assetsPath}, so the SQLitePCLRaw "
                    + "alignment invariant cannot be checked. Run 'dotnet restore "
                    + "backend/InventoryApi/InventoryApi.slnx' (scripts/validate.sh does) before "
                    + "this test.");

            resolvedByProject[project] = ReadSqlitePclRawPackages(File.ReadAllText(assetsPath));
        }

        Assert.NotEmpty(resolvedByProject);

        foreach (var (project, packages) in resolvedByProject.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            _output.WriteLine(packages.Count == 0
                ? $"{project}: no SQLitePCLRaw package in the restored graph"
                : $"{project}: {Describe(packages)}");
        }

        var problems = FindMisalignments(resolvedByProject);

        Assert.True(
            problems.Count == 0,
            string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void The_check_accepts_a_graph_whose_whole_family_is_on_one_stable_version()
    {
        Assert.Empty(FindMisalignments(SyntheticBackend("2.1.13")));
    }

    [Fact]
    public void The_check_rejects_a_graph_whose_family_versions_do_not_match()
    {
        var backend = SyntheticBackend("2.1.13");
        backend["InventoryApi"] = SyntheticAssets("2.1.14", ExpectedFamily);

        var problems = FindMisalignments(backend);

        Assert.Contains(problems, problem => problem.Contains("exactly one version", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains("2.1.14", StringComparison.Ordinal));
    }

    [Fact]
    public void The_check_rejects_a_graph_resolved_to_a_prerelease_version()
    {
        var problems = FindMisalignments(SyntheticBackend("2.1.12-pre20260709125052"));

        Assert.Contains(problems, problem => problem.Contains("prerelease", StringComparison.Ordinal));
    }

    [Fact]
    public void The_check_rejects_a_graph_that_lost_a_family_member()
    {
        var backend = SyntheticBackend("2.1.13");
        backend["InventoryApi"] = SyntheticAssets("2.1.13", CoreAndNativeLibraryOnly);

        var problems = FindMisalignments(backend);

        Assert.Contains(
            problems,
            problem => problem.Contains("SQLitePCLRaw.bundle_e_sqlite3", StringComparison.Ordinal));
    }

    [Fact]
    public void The_check_rejects_a_backend_with_no_assets_for_a_project_that_must_carry_the_family()
    {
        var backend = SyntheticBackend("2.1.13");
        backend.Remove("InventoryApi");

        var problems = FindMisalignments(backend);

        Assert.Contains(problems, problem => problem.Contains("InventoryApi", StringComparison.Ordinal));
    }

    [Fact]
    public void The_check_rejects_a_backend_with_no_SQLitePCLRaw_package_at_all()
    {
        var problems = FindMisalignments(new Dictionary<string, IReadOnlyList<ResolvedPackage>>(StringComparer.Ordinal)
        {
            ["Inventory.Domain"] = [],
        });

        Assert.Contains(problems, problem => problem.Contains("No SQLitePCLRaw package", StringComparison.Ordinal));
    }

    /// <summary>One package id and the single version restore resolved it to.</summary>
    private sealed record ResolvedPackage(string Id, string Version);

    /// <summary>
    /// The alignment rules, kept free of the filesystem so the negative cases above can prove the
    /// check really fails for a mismatched, prerelease or incomplete graph without touching the
    /// repository's real package references. Returns one message per violation; empty means aligned.
    /// </summary>
    private static List<string> FindMisalignments(
        IReadOnlyDictionary<string, IReadOnlyList<ResolvedPackage>> resolvedByProject)
    {
        var problems = new List<string>();

        var resolved = resolvedByProject
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .SelectMany(pair => pair.Value.Select(package => (Project: pair.Key, Package: package)))
            .ToList();

        if (resolved.Count == 0)
        {
            problems.Add(
                "No SQLitePCLRaw package was found in any restored project.assets.json. The native "
                    + "SQLite engine must come from the pinned SQLitePCLRaw packages (issue #1290), "
                    + "never from a system library, so an empty graph is a failure and not a pass.");
            return problems;
        }

        var versions = resolved
            .Select(entry => entry.Package.Version)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(version => version, StringComparer.Ordinal)
            .ToList();

        if (versions.Count > 1)
        {
            problems.Add(
                "The SQLitePCLRaw family must resolve to exactly one version across the backend, "
                    + $"but restore resolved {versions.Count}: {string.Join(", ", versions)}. "
                    + "Bump the SQLitePCLRaw.lib.e_sqlite3 pin in InventoryApi.csproj and the "
                    + "SQLitePCLRaw.core pin in Inventory.Infrastructure.csproj together, onto the "
                    + "version Microsoft.Data.Sqlite's bundle package resolves to. Resolved: "
                    + string.Join("; ", resolved
                        .GroupBy(entry => entry.Project, StringComparer.Ordinal)
                        .Select(group => $"{group.Key}: {Describe(group.Select(entry => entry.Package))}")));
        }

        foreach (var version in versions.Where(IsPrerelease))
        {
            problems.Add(
                $"SQLitePCLRaw resolved to the prerelease version {version}. The native SQLite "
                    + "engine the API deploys must come from an exact stable version: a prerelease "
                    + "can be unlisted at any time, which breaks restore (issue #1290).");
        }

        foreach (var (project, requiredPackages) in ProjectsRequiringFamilyMembers)
        {
            if (!resolvedByProject.TryGetValue(project, out var packages))
            {
                problems.Add(
                    $"{project} is missing from the restored projects, so its SQLitePCLRaw packages "
                        + "were never checked.");
                continue;
            }

            foreach (var requiredPackage in requiredPackages.Where(required =>
                !packages.Any(package => string.Equals(package.Id, required, StringComparison.OrdinalIgnoreCase))))
            {
                problems.Add(
                    $"{project}'s restored graph does not contain {requiredPackage}. The managed "
                        + "provider and the native engine must both come from the same pinned "
                        + "SQLitePCLRaw version (issue #1290).");
            }
        }

        return problems;
    }

    /// <summary>
    /// Reads every <c>SQLitePCLRaw*</c> entry out of a restored <c>project.assets.json</c>. Both the
    /// <c>targets</c> and the <c>libraries</c> sections are read and unioned, so a disagreement
    /// between the per-target-framework resolution and the flattened list shows up as two versions
    /// rather than being hidden by reading only one of them.
    /// </summary>
    private static IReadOnlyList<ResolvedPackage> ReadSqlitePclRawPackages(string assetsJson)
    {
        using var document = JsonDocument.Parse(assetsJson);
        var root = document.RootElement;

        var entries = new SortedSet<string>(StringComparer.Ordinal);

        if (root.TryGetProperty("libraries", out var libraries) && libraries.ValueKind == JsonValueKind.Object)
        {
            foreach (var library in libraries.EnumerateObject())
            {
                entries.Add(library.Name);
            }
        }

        if (root.TryGetProperty("targets", out var targets) && targets.ValueKind == JsonValueKind.Object)
        {
            foreach (var target in targets.EnumerateObject().Where(t => t.Value.ValueKind == JsonValueKind.Object))
            {
                foreach (var library in target.Value.EnumerateObject())
                {
                    entries.Add(library.Name);
                }
            }
        }

        return entries
            .Where(entry => entry.StartsWith("SQLitePCLRaw", StringComparison.OrdinalIgnoreCase))
            .Select(ParseAssetsEntry)
            .OfType<ResolvedPackage>()
            .ToList();
    }

    /// <summary>
    /// Splits an assets entry key, which is always <c>Id/Version</c>. Anything else is ignored
    /// rather than guessed at.
    /// </summary>
    private static ResolvedPackage? ParseAssetsEntry(string entry)
    {
        var separator = entry.IndexOf('/', StringComparison.Ordinal);

        return separator <= 0 || separator == entry.Length - 1
            ? null
            : new ResolvedPackage(entry[..separator], entry[(separator + 1)..]);
    }

    /// <summary>A SemVer prerelease label is what the replaced open range used to resolve to.</summary>
    private static bool IsPrerelease(string version) => version.Contains('-', StringComparison.Ordinal);

    private static string Describe(IEnumerable<ResolvedPackage> packages) =>
        string.Join(
            ", ",
            packages
                .OrderBy(package => package.Id, StringComparer.Ordinal)
                .Select(package => $"{package.Id}/{package.Version}"));

    /// <summary>
    /// Maps every project of <c>backend/InventoryApi/InventoryApi.slnx</c> to its directory, so a
    /// project added to the solution later is checked without editing this test.
    /// </summary>
    private static SortedDictionary<string, string> SolutionProjectDirectories()
    {
        var solutionDirectory = Path.Combine(BackendRoot, "InventoryApi");
        var document = XDocument.Load(Path.Combine(solutionDirectory, "InventoryApi.slnx"));

        var directories = new SortedDictionary<string, string>(StringComparer.Ordinal);

        foreach (var project in document.Descendants("Project"))
        {
            var relativePath = project.Attribute("Path")?.Value.Replace('\\', '/');

            if (string.IsNullOrWhiteSpace(relativePath))
            {
                continue;
            }

            var projectPath = Path.GetFullPath(Path.Combine(solutionDirectory, relativePath));
            directories[Path.GetFileNameWithoutExtension(projectPath)] = Path.GetDirectoryName(projectPath)!;
        }

        return directories;
    }

    // Mirrors the walk-up in Architecture/ProjectDependencyDirectionTests: the test binaries live
    // under <project>/bin/<configuration>/<tfm>, so the backend directory is the first ancestor
    // that holds the Inventory.Domain project folder.
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

    /// <summary>
    /// A synthetic backend shaped like the real one: the API and the test project carry the whole
    /// family, Infrastructure carries only the raw API, and the remaining projects carry none.
    /// </summary>
    private static Dictionary<string, IReadOnlyList<ResolvedPackage>> SyntheticBackend(string version) =>
        new(StringComparer.Ordinal)
        {
            ["InventoryApi"] = SyntheticAssets(version, ExpectedFamily),
            ["Inventory.IntegrationTests"] = SyntheticAssets(version, ExpectedFamily),
            ["Inventory.Infrastructure"] = SyntheticAssets(version, CoreOnly),
            ["Inventory.Application"] = [],
            ["Inventory.Domain"] = [],
            ["Inventory.UnitTests"] = [],
        };

    /// <summary>
    /// Serialises a minimal <c>project.assets.json</c> and reads it back through the real parser, so
    /// the negative cases exercise the same code path the restored files take.
    /// </summary>
    private static IReadOnlyList<ResolvedPackage> SyntheticAssets(string version, IEnumerable<string> packageIds)
    {
        var entries = packageIds.ToDictionary(
            id => $"{id}/{version}",
            _ => new Dictionary<string, string>(StringComparer.Ordinal) { ["type"] = "package" },
            StringComparer.Ordinal);

        var assets = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["version"] = 3,
            ["targets"] = new Dictionary<string, object>(StringComparer.Ordinal) { ["net10.0"] = entries },
            ["libraries"] = entries,
        };

        return ReadSqlitePclRawPackages(JsonSerializer.Serialize(assets));
    }
}
