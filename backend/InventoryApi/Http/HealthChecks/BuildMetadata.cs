using System.Reflection;

namespace InventoryApi.Http.HealthChecks;

/// <summary>
/// Issue #1308 (audit OPS-04, #1228/#1231): the commit this build was made from, so a health
/// probe can tell a reply from the old process during a restart apart from the new release
/// instead of trusting the first 200/503. <see cref="BuildSha"/> is a static field so it is read
/// from the assembly exactly once; <c>InventoryApi.csproj</c> stamps the git commit into
/// <see cref="AssemblyInformationalVersionAttribute"/> as the SDK's <c>SourceRevisionId</c>
/// (<c>"{Version}+{sha}"</c>) with no change to the deploy workflow or the <c>dotnet publish</c>
/// command. A build made outside a git checkout has no revision to stamp, and
/// <see cref="BuildSha"/> is <c>"unknown"</c> in that case.
/// </summary>
internal static class BuildMetadata
{
    public static string BuildSha { get; } = ResolveBuildSha();

    private const string Unknown = "unknown";

    private static string ResolveBuildSha()
    {
        var informationalVersion = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (string.IsNullOrEmpty(informationalVersion))
        {
            return Unknown;
        }

        var plusIndex = informationalVersion.IndexOf('+');
        if (plusIndex < 0 || plusIndex == informationalVersion.Length - 1)
        {
            return Unknown;
        }

        var candidate = informationalVersion[(plusIndex + 1)..];
        return IsFullCommitSha(candidate) ? candidate : Unknown;
    }

    private static bool IsFullCommitSha(string candidate)
    {
        if (candidate.Length != 40)
        {
            return false;
        }

        foreach (var c in candidate)
        {
            var isLowerHexDigit = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
            if (!isLowerHexDigit)
            {
                return false;
            }
        }

        return true;
    }
}
