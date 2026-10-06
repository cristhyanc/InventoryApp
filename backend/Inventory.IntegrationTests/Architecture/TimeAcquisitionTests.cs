using System.Text.RegularExpressions;
using Xunit;

namespace InventoryApi.Tests.Architecture;

/// <summary>
/// Issue #310: <c>Inventory.Domain</c> and <c>Inventory.Application</c> acquire the current time only
/// through the <c>Inventory.Application.Time.IClock</c>/<c>IBusinessCalendar</c> ports, never from the
/// host's own clock. A server-local or raw-UTC read inside a use case is not a style preference: the
/// API host runs in UTC while the business reporting timezone is <c>Australia/Sydney</c>, so a direct
/// read silently decides the wrong business day for a dashboard period or an effective-dated
/// commission/fee lookup, and it makes the use case untestable at a date or daylight-saving boundary.
/// See docs/architecture.md § Time.
///
/// The rule is checked against the source text rather than the compiled assemblies, because these are
/// property reads on <see cref="DateTime"/> itself: the inner layers legitimately depend on the
/// <c>DateTime</c> type everywhere, so a type-level dependency rule like the ones in
/// <see cref="CleanArchitectureDependencyTests"/> cannot distinguish them. The same source-scanning
/// precedent is used by
/// <see cref="ProjectDependencyDirectionTests.No_other_source_file_references_the_removed_legacy_reporting_service"/>.
/// </summary>
public class TimeAcquisitionTests
{
    private static readonly string[] GuardedProjects = ["Inventory.Domain", "Inventory.Application"];

    /// <summary>
    /// Matches a member access on the <c>DateTime</c> type itself, in code or in a comment. Whitespace
    /// around the dot is tolerated so a reformatted or line-wrapped read cannot slip past the guard,
    /// and <c>DateTimeOffset</c> does not match because the character after <c>DateTime</c> must be the
    /// member access.
    /// </summary>
    private static readonly Regex HostClockRead = new(
        @"\bDateTime\s*\.\s*(Now|UtcNow|Today)\b", RegexOptions.Compiled);

    [Fact]
    public void Domain_and_Application_acquire_the_current_time_only_through_the_time_ports()
    {
        var offenders = GuardedProjects
            .SelectMany(project => SourceFilesOf(project).Select(path => (Project: project, Path: path)))
            .SelectMany(file => HostClockReadsIn(file.Path).Select(read => $"{file.Project}: {read}"))
            .OrderBy(offender => offender, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "Inventory.Domain and Inventory.Application must acquire the current time only through "
                + "Inventory.Application.Time.IClock (the current UTC instant) or IBusinessCalendar "
                + "(the Australia/Sydney business date), so the business day - not the host's "
                + "timezone - decides a dashboard period or an effective-dated commission/fee "
                + "lookup, and so the behaviour is testable with a fixed clock. Inject the port "
                + "instead of reading the host clock at: "
                + string.Join("; ", offenders)
                + ". See docs/architecture.md § Time.");
    }

    private static IEnumerable<string> SourceFilesOf(string project) =>
        Directory.EnumerateFiles(Path.Combine(BackendRoot, project), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static IEnumerable<string> HostClockReadsIn(string path) =>
        File.ReadAllLines(path)
            .Select((line, index) => (Line: line, Number: index + 1))
            .Where(line => HostClockRead.IsMatch(line.Line))
            .Select(line => $"{Path.GetFileName(path)}:{line.Number} ({HostClockRead.Match(line.Line).Value})");

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
