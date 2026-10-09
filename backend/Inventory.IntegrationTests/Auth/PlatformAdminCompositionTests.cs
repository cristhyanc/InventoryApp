using System.Net;
using System.Net.Http.Json;
using Inventory.Application.PlatformDiagnostics;
using InventoryApi.Auth.PlatformAdmin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InventoryApi.Tests.Auth;

/// <summary>
/// The shipped state of the platform diagnostics API: no administrator configured, so it is
/// unreachable for everybody (issue #336).
///
/// This is the half that is easy to get wrong by omission. A policy that treats "nobody is
/// configured" as "there is nothing to check" would pass every authorisation test written against a
/// configured host, and would hand the API to the first authenticated caller on a deployment where
/// the setting was never filled in.
/// </summary>
public sealed class PlatformAdminCompositionTests
    : IClassFixture<UnconfiguredPlatformDiagnosticsHostFactory>
{
    private readonly UnconfiguredPlatformDiagnosticsHostFactory _factory;

    public PlatformAdminCompositionTests(UnconfiguredPlatformDiagnosticsHostFactory factory) =>
        _factory = factory;

    [Fact]
    public void With_nothing_configured_the_host_recognises_no_platform_administrator()
    {
        var configured = _factory.Services.GetRequiredService<ConfiguredPlatformAdmin>();

        Assert.False(configured.IsConfigured);
        Assert.Null(configured.Identity);
    }

    [Fact]
    public async Task The_named_policy_is_registered_and_requires_an_authenticated_platform_admin()
    {
        var policies = _factory.Services.GetRequiredService<IAuthorizationPolicyProvider>();

        var policy = await policies.GetPolicyAsync(PlatformAdminPolicy.Name);

        Assert.NotNull(policy);
        Assert.Contains(policy.Requirements, requirement => requirement is PlatformAdminRequirement);
        Assert.Contains(
            policy.Requirements,
            requirement => requirement.GetType().Name == "DenyAnonymousAuthorizationRequirement");
    }

    /// <summary>
    /// The default policy must be exactly what it was: controllers rely on <c>[Authorize]</c> with
    /// no policy name, and adding a named one must not have changed what that means.
    /// </summary>
    [Fact]
    public async Task The_default_authorization_policy_is_unchanged()
    {
        var policies = _factory.Services.GetRequiredService<IAuthorizationPolicyProvider>();

        var defaultPolicy = await policies.GetDefaultPolicyAsync();

        Assert.DoesNotContain(defaultPolicy.Requirements, requirement => requirement is PlatformAdminRequirement);
    }

    [Theory]
    [InlineData("/api/admin/diagnostics/access")]
    public async Task With_nothing_configured_every_authenticated_caller_is_forbidden(string route)
    {
        var member = await _factory.As(PlatformDiagnosticsHostFactory.BusinessMember).GetAsync(route);
        var wouldBeAdmin = await _factory.As(PlatformDiagnosticsHostFactory.PlatformAdministrator).GetAsync(route);

        Assert.Equal(HttpStatusCode.Forbidden, member.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, wouldBeAdmin.StatusCode);
    }

    [Fact]
    public async Task With_nothing_configured_the_data_path_is_forbidden_rather_than_simply_empty()
    {
        var response = await _factory.As(PlatformDiagnosticsHostFactory.PlatformAdministrator)
            .PostAsJsonAsync("/api/admin/diagnostics/query", new { sql = "SELECT Id FROM Products" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task With_nothing_configured_no_diagnostics_query_is_ever_audited()
    {
        const string sql = "SELECT Id FROM Products WHERE Id > 0 AND 'unconfiguredprobe' <> ''";

        await _factory.As(PlatformDiagnosticsHostFactory.PlatformAdministrator)
            .PostAsJsonAsync("/api/admin/diagnostics/query", new { sql });

        Assert.DoesNotContain(
            _factory.LogMessages,
            message => message.Contains(DiagnosticsSqlShape.Fingerprint(sql), StringComparison.Ordinal));
    }

    /// <summary>
    /// The resolution rules, checked where they are decided rather than over HTTP: both halves
    /// required, both well-formed GUIDs, and every spelling of the same GUID matching - so a
    /// hand-entered braced or differently cased setting is not quietly a different person, and a
    /// malformed one is nobody rather than something unintended.
    /// </summary>
    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("11111111-1111-1111-1111-111111111111", null)]
    [InlineData(null, "22222222-2222-2222-2222-222222222222")]
    [InlineData("not-a-guid", "22222222-2222-2222-2222-222222222222")]
    [InlineData("11111111-1111-1111-1111-111111111111", "not-a-guid")]
    [InlineData("admin@example.com", "admin@example.com")]
    public void An_incomplete_or_malformed_setting_configures_nobody(string? directoryTenantId, string? objectId)
    {
        var configured = ConfiguredPlatformAdmin.From(new PlatformAdminOptions
        {
            DirectoryTenantId = directoryTenantId,
            ObjectId = objectId,
        });

        Assert.False(configured.IsConfigured);
        Assert.False(configured.Matches(null));
    }

    [Theory]
    [InlineData("11111111-1111-1111-1111-111111111111", "22222222-2222-2222-2222-222222222222")]
    [InlineData("11111111111111111111111111111111", "22222222222222222222222222222222")]
    [InlineData("{11111111-1111-1111-1111-111111111111}", "{22222222-2222-2222-2222-222222222222}")]
    [InlineData("11111111-1111-1111-1111-111111111111 ", " 22222222-2222-2222-2222-222222222222")]
    public void Every_spelling_of_the_same_pair_configures_the_same_administrator(
        string directoryTenantId,
        string objectId)
    {
        var canonical = ConfiguredPlatformAdmin.From(new PlatformAdminOptions
        {
            DirectoryTenantId = "11111111-1111-1111-1111-111111111111",
            ObjectId = "22222222-2222-2222-2222-222222222222",
        });

        var configured = ConfiguredPlatformAdmin.From(new PlatformAdminOptions
        {
            DirectoryTenantId = directoryTenantId,
            ObjectId = objectId,
        });

        Assert.True(configured.IsConfigured);
        Assert.True(configured.Matches(canonical.Identity));
    }

    [Fact]
    public void A_different_actor_does_not_match_the_configured_administrator()
    {
        var configured = ConfiguredPlatformAdmin.From(new PlatformAdminOptions
        {
            DirectoryTenantId = "11111111-1111-1111-1111-111111111111",
            ObjectId = "22222222-2222-2222-2222-222222222222",
        });

        var otherDirectory = ConfiguredPlatformAdmin.From(new PlatformAdminOptions
        {
            DirectoryTenantId = "99999999-9999-9999-9999-999999999999",
            ObjectId = "22222222-2222-2222-2222-222222222222",
        });

        var otherActor = ConfiguredPlatformAdmin.From(new PlatformAdminOptions
        {
            DirectoryTenantId = "11111111-1111-1111-1111-111111111111",
            ObjectId = "99999999-9999-9999-9999-999999999999",
        });

        Assert.False(configured.Matches(otherDirectory.Identity));
        Assert.False(configured.Matches(otherActor.Identity));
    }
}
