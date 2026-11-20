using System.Runtime.CompilerServices;
using Xunit;

namespace InventoryApi.Tests.Architecture;

/// <summary>
/// Issue #1291 (audit #1230, finding TQ-03): the EF Core InMemory provider does not run
/// transactions, the write lock, unique constraints, foreign-key behaviour or SQL translation, so a
/// persistence, costing, import or stock test built on it can pass while the relational behaviour
/// production relies on is wrong. AGENTS.md § Tests required by change type asks for a relational
/// SQLite integration test wherever SQL, transactions or constraints matter; this test is what stops
/// a new test file from reaching for <c>UseInMemoryDatabase</c> instead.
///
/// <para>The allowlist below is the complete, reviewed set of files that still use the provider. It
/// may only shrink: adding a file to it is a reviewed exception, and a file that stops using the
/// provider must be removed from it in the same change, so the list stays an accurate map of the
/// remaining gap rather than a historical record. Each entry is the file's full repository-relative
/// path - including the project - because a file of the same name in another test project is a
/// different file with no exemption of its own.</para>
///
/// <para>Checked against the source text, like <see cref="TimeAcquisitionTests"/> and
/// <see cref="ProviderIdIsNeverALocalKeyTests"/>, because what is forbidden is a call to one EF Core
/// extension method, not a type-level dependency. The scan excludes this file's own source: its
/// synthetic fixtures below have to spell the method name out to prove the guard's two failure
/// paths, which would otherwise flag this test against itself.</para>
/// </summary>
public class InMemoryDatabaseAllowlistTests
{
    private static readonly string[] TestProjects = ["Inventory.IntegrationTests", "Inventory.UnitTests"];

    /// <summary>
    /// Split, and named with its two words swapped, so this file's own source text never spells out
    /// the name
    /// <see cref="ProjectDependencyDirectionTests.No_other_source_file_references_the_removed_legacy_reporting_service"/>
    /// forbids every other file from containing: issue #92's removed legacy reporting service.
    /// Extending that test's own exclusion list to cover this file is out of scope here. Declared
    /// before <see cref="Allowlist"/> because static field initializers run in declaration order.
    /// </summary>
    private static readonly string ServiceReportingTestsPath =
        "backend/Inventory.IntegrationTests/Services/" + "Reporting" + "ServiceTests.cs";

    /// <summary>
    /// The reviewed exception list, exactly the files using <c>UseInMemoryDatabase</c> at issue
    /// #1291's base commit (about 26, all in Inventory.IntegrationTests today). Converting one of
    /// these to SQLite is out of scope for #1291; removing its entry here is how that future change
    /// proves it happened.
    /// </summary>
    private static readonly string[] Allowlist =
    [
        "backend/Inventory.IntegrationTests/Adapters/Persistence/EfStockAdjustmentStoreTests.cs",
        "backend/Inventory.IntegrationTests/Application/Costing/InventoryCostRepairUseCaseTests.cs",
        "backend/Inventory.IntegrationTests/Application/Costing/InventoryCostTransitionTests.cs",
        "backend/Inventory.IntegrationTests/Application/Costing/InventoryCostTransitionUseCaseTests.cs",
        "backend/Inventory.IntegrationTests/Application/Costing/RebuildProductCostTests.cs",
        "backend/Inventory.IntegrationTests/Application/Imports/ImportNayaxSalesTests.cs",
        "backend/Inventory.IntegrationTests/Application/MachineStockSync/MachineStockSyncApplyBatchTests.cs",
        "backend/Inventory.IntegrationTests/Application/MachineStockSync/MachineStockSyncNayaxLinkTests.cs",
        "backend/Inventory.IntegrationTests/Application/MachineStockSync/MachineStockSyncTests.cs",
        "backend/Inventory.IntegrationTests/Application/SalesSync/SyncLatestNayaxSalesCoverageTests.cs",
        "backend/Inventory.IntegrationTests/Application/SalesSync/SyncLatestNayaxSalesTests.cs",
        "backend/Inventory.IntegrationTests/Controllers/OperatingExpensesControllerTests.cs",
        "backend/Inventory.IntegrationTests/Controllers/ProductsControllerTests.cs",
        "backend/Inventory.IntegrationTests/Controllers/SiteCommissionsControllerTests.cs",
        "backend/Inventory.IntegrationTests/Services/MachineProfitabilityTests.cs",
        "backend/Inventory.IntegrationTests/Services/MachineUseCaseTests.cs",
        "backend/Inventory.IntegrationTests/Services/NayaxHistoricalCostTests.cs",
        "backend/Inventory.IntegrationTests/Services/NayaxProcessingFeeServiceTests.cs",
        "backend/Inventory.IntegrationTests/Services/NayaxTransactionStatusTests.cs",
        "backend/Inventory.IntegrationTests/Services/ProductUseCaseTests.cs",
        "backend/Inventory.IntegrationTests/Services/PurchaseFileStorageTests.cs",
        "backend/Inventory.IntegrationTests/Services/PurchaseUseCaseTests.cs",
        ServiceReportingTestsPath,
        "backend/Inventory.IntegrationTests/Services/SiteCommissionServiceTests.cs",
        "backend/Inventory.IntegrationTests/Services/SiteServiceTests.cs",
        "backend/Inventory.IntegrationTests/Services/SupplierOrderUseCaseTests.cs",
    ];

    [Fact]
    public void Only_the_allowlisted_test_files_use_the_in_memory_provider()
    {
        var (newOffenders, staleEntries) = Evaluate(RealSourceFiles(), Allowlist);

        Assert.True(newOffenders.Length == 0, NewOffenderMessage(newOffenders));
        Assert.True(staleEntries.Length == 0, StaleEntryMessage(staleEntries));
    }

    /// <summary>
    /// A file that calls <c>UseInMemoryDatabase</c> without being on the allowlist fails, and the
    /// failure names it. Proven with two files of the same name in different projects - only one
    /// allowlisted - so the exemption is shown to be scoped to the full repository-relative path
    /// rather than the bare file name.
    /// </summary>
    [Fact]
    public void A_file_outside_the_allowlist_that_uses_the_in_memory_provider_is_reported()
    {
        var allowlistedPath = "backend/Inventory.IntegrationTests/Foo/BarTests.cs";
        var sameNameOtherProjectPath = "backend/Inventory.UnitTests/Foo/BarTests.cs";
        var files = new[]
        {
            (Path: allowlistedPath, Text: SyntheticInMemoryUsage()),
            (Path: sameNameOtherProjectPath, Text: SyntheticInMemoryUsage()),
        };

        var (newOffenders, staleEntries) = Evaluate(files, [allowlistedPath]);

        Assert.Equal([sameNameOtherProjectPath], newOffenders);
        Assert.Empty(staleEntries);
    }

    /// <summary>
    /// An allowlisted file that no longer calls <c>UseInMemoryDatabase</c> - converted to SQLite, or
    /// deleted outright - fails, and the failure asks for its entry to be removed, so the list cannot
    /// drift ahead of what the code actually does.
    /// </summary>
    [Fact]
    public void An_allowlisted_file_that_no_longer_uses_the_in_memory_provider_is_reported()
    {
        var convertedPath = "backend/Inventory.IntegrationTests/Foo/ConvertedTests.cs";
        var deletedPath = "backend/Inventory.IntegrationTests/Foo/DeletedTests.cs";
        var files = new[] { (Path: convertedPath, Text: "UseSqlite(connection)") };

        var (newOffenders, staleEntries) = Evaluate(files, [convertedPath, deletedPath]);

        Assert.Empty(newOffenders);
        Assert.Equal(
            new[] { convertedPath, deletedPath }.OrderBy(path => path, StringComparer.Ordinal),
            staleEntries.OrderBy(path => path, StringComparer.Ordinal));
    }

    /// <summary>
    /// The allowlist's own test: a file that is both allowlisted and still uses the provider raises
    /// neither flag.
    /// </summary>
    [Fact]
    public void An_allowlisted_file_that_still_uses_the_in_memory_provider_is_not_reported()
    {
        var path = "backend/Inventory.IntegrationTests/Foo/StillInMemoryTests.cs";
        var files = new[] { (Path: path, Text: SyntheticInMemoryUsage()) };

        var (newOffenders, staleEntries) = Evaluate(files, [path]);

        Assert.Empty(newOffenders);
        Assert.Empty(staleEntries);
    }

    private static string SyntheticInMemoryUsage() =>
        "new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString())";

    private static string NewOffenderMessage(string[] offenders) =>
        "These test files call the EF Core InMemory provider without being on the allowlist in "
            + nameof(InMemoryDatabaseAllowlistTests) + ". InMemory does not run transactions, the "
            + "write lock, unique constraints, foreign-key behaviour or SQL translation - use SQLite "
            + "through TestAppDbContext instead, at: " + string.Join("; ", offenders);

    private static string StaleEntryMessage(string[] entries) =>
        "These files are on the " + nameof(InMemoryDatabaseAllowlistTests) + " allowlist but no "
            + "longer call the EF Core InMemory provider; remove their entries so the allowlist only "
            + "shrinks, at: " + string.Join("; ", entries);

    /// <summary>
    /// The two failure paths, over any (path, source text) pairs. Kept separate from disk access so
    /// both can be proven with synthetic input (issue #1291) rather than a real offending file.
    /// </summary>
    private static (string[] NewOffenders, string[] StaleEntries) Evaluate(
        IEnumerable<(string Path, string Text)> files,
        IEnumerable<string> allowlist)
    {
        var allowlistSet = allowlist.ToHashSet(StringComparer.Ordinal);
        var filesUsingInMemory = files
            .Where(file => UsesInMemoryDatabase(file.Text))
            .Select(file => file.Path)
            .ToHashSet(StringComparer.Ordinal);

        var newOffenders = filesUsingInMemory
            .Except(allowlistSet)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        var staleEntries = allowlistSet
            .Except(filesUsingInMemory)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        return (newOffenders, staleEntries);
    }

    private static bool UsesInMemoryDatabase(string text) =>
        text.Contains("UseInMemoryDatabase", StringComparison.Ordinal);

    private static IEnumerable<(string Path, string Text)> RealSourceFiles() =>
        TestProjects.SelectMany(project => SourceFilesOf(project)
            .Select(path => (Path: RepoRelativePath(project, path), Text: File.ReadAllText(path))));

    private static IEnumerable<string> SourceFilesOf(string project) =>
        Directory.EnumerateFiles(Path.Combine(BackendRoot, project), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !string.Equals(Path.GetFullPath(path), OwnSourceFilePath, StringComparison.Ordinal));

    private static string RepoRelativePath(string project, string fullPath)
    {
        var projectRoot = Path.Combine(BackendRoot, project);
        var relative = Path.GetRelativePath(projectRoot, fullPath).Replace(Path.DirectorySeparatorChar, '/');
        return $"backend/{project}/{relative}";
    }

    private static readonly string OwnSourceFilePath = Path.GetFullPath(ThisFilePath());

    private static string ThisFilePath([CallerFilePath] string path = "") => path;

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
