using Inventory.Infrastructure.Nayax;
using Xunit;

namespace InventoryApi.Tests.Infrastructure.Nayax;

/// <summary>
/// The single typed Nayax Lynx configuration contract (issue #49): required non-secret fields
/// fail fast at startup with a clear message, and the bearer token resolves without ever being
/// exposed in a validation error.
/// </summary>
public sealed class NayaxLynxConfigurationTests
{
    #region Non-secret field validation

    [Fact]
    public void A_fully_configured_options_object_passes_validation()
    {
        var options = new NayaxLynxOptions
        {
            BaseUrl = "https://lynx.nayax.com",
            OperatorId = "2002736764",
        };

        var exception = Record.Exception(() => NayaxLynxConfiguration.ValidateNonSecretFields(options));

        Assert.Null(exception);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_base_url_is_refused(string? baseUrl)
    {
        var options = new NayaxLynxOptions { BaseUrl = baseUrl!, OperatorId = "2002736764" };

        var failure = Assert.Throws<InvalidOperationException>(
            () => NayaxLynxConfiguration.ValidateNonSecretFields(options));

        Assert.Contains("NayaxLynx:BaseUrl", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not a uri")]
    [InlineData("ftp://lynx.nayax.com")]
    [InlineData("http://lynx.nayax.com")]
    public void A_non_https_or_unparsable_base_url_is_refused(string baseUrl)
    {
        var options = new NayaxLynxOptions { BaseUrl = baseUrl, OperatorId = "2002736764" };

        var failure = Assert.Throws<InvalidOperationException>(
            () => NayaxLynxConfiguration.ValidateNonSecretFields(options));

        Assert.Contains("NayaxLynx:BaseUrl", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_operator_id_is_refused(string? operatorId)
    {
        var options = new NayaxLynxOptions { BaseUrl = "https://lynx.nayax.com", OperatorId = operatorId! };

        var failure = Assert.Throws<InvalidOperationException>(
            () => NayaxLynxConfiguration.ValidateNonSecretFields(options));

        Assert.Contains("NayaxLynx:OperatorId", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The access token is a secret and is deliberately excluded from non-secret validation - a
    /// missing token must never surface in a startup error message.
    /// </summary>
    [Fact]
    public void Validation_never_mentions_the_access_token()
    {
        var options = new NayaxLynxOptions { BaseUrl = "", OperatorId = "2002736764", AccessToken = "should-never-appear" };

        var failure = Assert.Throws<InvalidOperationException>(
            () => NayaxLynxConfiguration.ValidateNonSecretFields(options));

        Assert.DoesNotContain("should-never-appear", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("AccessToken", failure.Message, StringComparison.Ordinal);
    }

    #endregion

    #region Access token resolution

    [Fact]
    public void The_consolidated_key_is_preferred_when_present()
    {
        Assert.Equal(
            "new-token",
            NayaxLynxConfiguration.ResolveAccessToken(consolidatedKeyValue: "new-token", legacyKeyValue: "legacy-token"));
    }

    /// <summary>
    /// The already-deployed Key Vault/App Service secret (<c>Nayax__Token</c>) must keep working
    /// without a coordinated rollout.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void The_legacy_key_is_used_when_the_consolidated_key_is_absent(string? consolidatedKeyValue)
    {
        Assert.Equal(
            "legacy-token",
            NayaxLynxConfiguration.ResolveAccessToken(consolidatedKeyValue, legacyKeyValue: "legacy-token"));
    }

    [Fact]
    public void A_missing_token_anywhere_resolves_to_null()
    {
        Assert.Null(NayaxLynxConfiguration.ResolveAccessToken(consolidatedKeyValue: null, legacyKeyValue: null));
    }

    #endregion
}
