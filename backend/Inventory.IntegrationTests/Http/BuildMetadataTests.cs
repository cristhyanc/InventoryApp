using InventoryApi.Http.HealthChecks;
using Xunit;

namespace InventoryApi.Tests.Http;

/// <summary>
/// Issue #1308, acceptance criterion 3: "A build with no commit information (for example a local
/// build outside git) returns <c>unknown</c>, and the endpoints still work."
///
/// These are the focused tests for the first half of it. The HTTP tests in
/// <see cref="HealthEndpointsTests"/> cannot reach it: every test host runs from this repository's
/// own git checkout, where <c>InventoryApi.csproj</c> always stamps a real commit into the
/// informational version, so they only ever exercise the 40-hex-character path. The second half -
/// that the endpoints still answer normally while reporting <c>unknown</c> - is
/// <see cref="HealthEndpointsWithoutBuildMetadataTests"/>.
///
/// The expected values are written out as literals rather than taken from
/// <see cref="BuildMetadata.Unknown"/>, because "unknown" is the published header contract and not
/// merely whatever the constant happens to say.
/// </summary>
public sealed class BuildMetadataTests
{
    private const string Sha = "2b99419798a002d75134ae4eca3f1e6b920b4d6f";

    /// <summary>
    /// Every way a build can carry no usable commit: no
    /// <c>AssemblyInformationalVersionAttribute</c> at all (<c>null</c>), an empty one, a version
    /// the SDK never stamped (no <c>'+'</c>), a <c>'+'</c> with nothing after it, and a suffix
    /// that is not a full commit - abbreviated, too long, uppercase, non-hex, or padded.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1.0.0")]
    [InlineData("1.0.0+")]
    [InlineData("+")]
    [InlineData("1.0.0+2b99419")]
    [InlineData("1.0.0+2b99419798a002d75134ae4eca3f1e6b920b4d6")]
    [InlineData("1.0.0+2b99419798a002d75134ae4eca3f1e6b920b4d6ff")]
    [InlineData("1.0.0+2B99419798A002D75134AE4ECA3F1E6B920B4D6F")]
    [InlineData("1.0.0+2b99419798a002d75134ae4eca3f1e6b920b4d6g")]
    [InlineData("1.0.0+2b99419798a002d75134ae4eca3f1e6b920b4d6 ")]
    [InlineData("1.0.0+not-a-commit")]
    public void Reports_unknown_when_the_build_carries_no_full_commit(string? informationalVersion)
    {
        Assert.Equal("unknown", BuildMetadata.ResolveBuildSha(informationalVersion));
    }

    /// <summary>
    /// The shape the SDK actually produces from <c>SourceRevisionId</c>, <c>"{Version}+{sha}"</c>,
    /// including a prerelease version whose own label contains no <c>'+'</c>.
    /// </summary>
    [Theory]
    [InlineData("1.0.0+" + Sha)]
    [InlineData("1.0.0-beta.1+" + Sha)]
    [InlineData("+" + Sha)]
    public void Reports_the_stamped_commit(string informationalVersion)
    {
        Assert.Equal(Sha, BuildMetadata.ResolveBuildSha(informationalVersion));
    }

    /// <summary>
    /// The value this build actually reports is one of exactly those two things and nothing else -
    /// no environment name, machine name, configuration or dependency version.
    /// </summary>
    [Fact]
    public void Build_sha_is_a_full_commit_or_unknown()
    {
        var buildSha = BuildMetadata.BuildSha;

        if (buildSha != "unknown")
        {
            Assert.Matches("^[0-9a-f]{40}$", buildSha);
        }
    }
}
