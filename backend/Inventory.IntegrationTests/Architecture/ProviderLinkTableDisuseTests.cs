using System.Text.RegularExpressions;
using Xunit;

namespace InventoryApi.Tests.Architecture;

/// <summary>
/// Issue #759 moved a provider's identifier out of the <c>ProductExternalIds</c>/
/// <c>CategoryExternalIds</c> link tables of issue #732 and onto the local row, and
/// <strong>nothing reads or writes those tables any more</strong>.
///
/// That is not a tidiness rule, it is the precondition of the next change: the tables are still in
/// the schema only because Production applies migrations at startup, so the code already running
/// there is still reading them while this release lands. #760 drops them once this release is in
/// production, and it may only do that while nothing has started using them again. A read added back
/// would also be reading a table nothing keeps up to date, which is worse than reading nothing.
///
/// The rule is checked against the source text, like <see cref="TimeAcquisitionTests"/>, because what
/// it forbids is a <c>DbSet</c> access and a row construction rather than a type-level dependency:
/// the two entities and their mapping legitimately still exist, and the migrations that created and
/// then copied out of them are historical statements that must not change.
/// </summary>
public class ProviderLinkTableDisuseTests
{
    private static readonly string[] ProductionProjects =
        ["Inventory.Domain", "Inventory.Application", "Inventory.Infrastructure", "InventoryApi"];

    /// <summary>
    /// A read or write through either link <c>DbSet</c> (<c>db.ProductExternalIds…</c>), or the
    /// construction of a link row. Prose that merely mentions a table by name does not match, which
    /// is deliberate: the comments explaining why the tables still exist have to be allowed to say
    /// so.
    /// </summary>
    private static readonly Regex LinkTableUse = new(
        @"\.\s*(Product|Category)ExternalIds\b|new\s+(Product|Category)ExternalId\s*[({]",
        RegexOptions.Compiled);

    [Fact]
    public void No_production_code_reads_or_writes_the_provider_link_tables()
    {
        var offenders = ProductionProjects
            .SelectMany(project => SourceFilesOf(project).Select(path => (Project: project, Path: path)))
            .SelectMany(file => LinkTableUsesIn(file.Path).Select(use => $"{file.Project}: {use}"))
            .OrderBy(offender => offender, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "Nothing may read or write ProductExternalIds or CategoryExternalIds since issue #759: a "
                + "provider's identifier lives on the local row (Product.ExternalProductId, "
                + "Category.ExternalCategoryId), and those tables are kept only until #760 drops "
                + "them, unread and no longer maintained. Read or write the column instead, at: "
                + string.Join("; ", offenders)
                + ". See docs/architecture.md § Provider identity on the local row.");
    }

    private static IEnumerable<string> SourceFilesOf(string project) =>
        Directory.EnumerateFiles(Path.Combine(BackendRoot, project), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            // The migrations that created the tables, backfilled them and copied out of them are
            // history: they are the one place those names legitimately appear in statements.
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static IEnumerable<string> LinkTableUsesIn(string path) =>
        File.ReadAllLines(path)
            .Select((line, index) => (Line: line, Number: index + 1))
            .Where(line => LinkTableUse.IsMatch(line.Line))
            .Select(line => $"{Path.GetFileName(path)}:{line.Number} ({LinkTableUse.Match(line.Line).Value.Trim()})");

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
