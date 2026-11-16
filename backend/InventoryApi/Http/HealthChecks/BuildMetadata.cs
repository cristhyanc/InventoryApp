using System.Reflection;

namespace InventoryApi.Http.HealthChecks;

/// <summary>
/// Issue #1308 (audit OPS-04, #1228/#1231): the commit this build was made from, so a health
/// probe can tell a reply from the old process during a restart apart from the new release
/// instead of trusting the first 200/503. <see cref="BuildSha"/> is computed in a static
/// initializer so it is read from the assembly exactly once; <c>InventoryApi.csproj</c> stamps
/// the git commit into <see cref="AssemblyInformationalVersionAttribute"/> as the SDK's
/// <c>SourceRevisionId</c> (<c>"{Version}+{sha}"</c>) with no change to the deploy workflow or
/// the <c>dotnet publish</c> command. A build made outside a git checkout has no revision to stamp, and
/// <see cref="BuildSha"/> is <c>"unknown"</c> in that case.
/// </summary>
public static class BuildMetadata
{
    /// <summary>
    /// What the health endpoints report when the build carries no usable commit. Part of the
    /// published header contract (issue #1308): the value is this literal or a full commit SHA,
    /// and never anything else.
    /// </summary>
    public const string Unknown = "unknown";

    public static string BuildSha { get; } = ResolveBuildSha(
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion);

    /// <summary>
    /// Reads the commit out of an informational version, or returns <see cref="Unknown"/> when the
    /// build has none to report: no attribute at all, an empty one, nothing stamped after the
    /// SDK's <c>'+'</c>, or a suffix that is not a full 40-character lowercase hex commit.
    ///
    /// <para>It takes the version rather than reading the assembly so the fallback can be pinned
    /// deterministically (<c>BuildMetadataTests</c>). Every test host in this solution runs from
    /// this repository's own git checkout, where <c>InventoryApi.csproj</c> always stamps a real
    /// commit, so nothing reached through <see cref="BuildSha"/> can exercise these branches.</para>
    /// </summary>
    public static string ResolveBuildSha(string? informationalVersion)
    {
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

/// <summary>
/// The commit <see cref="BuildShaHeaderMiddleware"/> reports, as one registered singleton
/// (issue #1308). <c>Program.cs</c> registers it from <see cref="BuildMetadata.BuildSha"/> at
/// startup, which is where the assembly is read; the middleware takes it from there rather than
/// touching the static itself.
///
/// <para>That is also what makes the <see cref="BuildMetadata.Unknown"/> fallback testable end to
/// end: the attribute of an already-loaded assembly cannot be changed, so a test host substitutes
/// this registration with what <see cref="BuildMetadata.ResolveBuildSha"/> produces for a build
/// with no revision, and the real endpoints, routing and middleware answer the probe unchanged
/// (<c>HealthEndpointsWithoutBuildMetadataTests</c>).</para>
/// </summary>
public sealed class BuildShaSource
{
    public BuildShaSource(string buildSha)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(buildSha);
        BuildSha = buildSha;
    }

    public string BuildSha { get; }
}
