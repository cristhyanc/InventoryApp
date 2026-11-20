using System.Text.RegularExpressions;
using Xunit;

namespace InventoryApi.Tests.Architecture;

/// <summary>
/// Issue #1289 (audit #1226): a controller action that passes <c>CancellationToken.None</c> to a
/// use case keeps that work running after a closed tab or a timed-out request, because
/// <c>CancellationToken.None</c> can never be cancelled. A handful of writes legitimately keep it -
/// once a document's bytes are staged in storage, the clean-up that would undo a cancelled write
/// re-uses the same token, so cancelling it would stop the clean-up rather than the write - and
/// those are documented individually rather than exempted as a class, so a future one has to be a
/// conscious decision, pinned by its own one-line comment immediately beside the exception.
///
/// The rule is checked against the source text, the same precedent
/// <see cref="TimeAcquisitionTests"/> and
/// <see cref="ProjectDependencyDirectionTests.No_other_source_file_references_the_removed_legacy_reporting_service"/>
/// use: a comment is not a construct the compiled assembly carries at all.
/// </summary>
public class ControllerCancellationTokenTests
{
    private static readonly Regex CancellationTokenNone = new(@"\bCancellationToken\s*\.\s*None\b", RegexOptions.Compiled);

    [Fact]
    public void Every_CancellationToken_None_in_a_controller_action_carries_a_justifying_comment()
    {
        var offenders = ControllerFiles()
            .SelectMany(path => UnjustifiedUsesIn(path))
            .OrderBy(offender => offender, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "A controller action may only pass CancellationToken.None instead of the request's own "
                + "token when the write must finish once it has started - and then only with a "
                + "one-line comment, on the line above or beside the call, saying why. Pass the "
                + "action's own CancellationToken parameter instead, or add that comment, at: "
                + string.Join("; ", offenders)
                + ". See issue #1289.");
    }

    private static IEnumerable<string> UnjustifiedUsesIn(string path)
    {
        var lines = File.ReadAllLines(path);
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var trimmed = line.TrimStart();

            // A documentation line mentioning the token by name is not a use of it.
            if (trimmed.StartsWith("///", StringComparison.Ordinal) || trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            if (!CancellationTokenNone.IsMatch(line))
            {
                continue;
            }

            var hasTrailingComment = line.Contains("//", StringComparison.Ordinal);
            var previousLineIsComment = index > 0 && lines[index - 1].TrimStart().StartsWith("//", StringComparison.Ordinal);
            if (hasTrailingComment || previousLineIsComment)
            {
                continue;
            }

            yield return $"{Path.GetFileName(path)}:{index + 1}";
        }
    }

    private static IEnumerable<string> ControllerFiles() =>
        Directory.EnumerateFiles(ControllersDirectory, "*.cs", SearchOption.TopDirectoryOnly);

    private static readonly string ControllersDirectory = FindControllersDirectory();

    private static string FindControllersDirectory()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !Directory.Exists(Path.Combine(current.FullName, "InventoryApi")))
        {
            current = current.Parent;
        }

        var backendRoot = current
            ?? throw new InvalidOperationException("Could not locate the backend directory containing InventoryApi.");
        return Path.Combine(backendRoot.FullName, "InventoryApi", "Controllers");
    }
}
