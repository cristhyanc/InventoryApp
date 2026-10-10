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

    /// <summary>
    /// Issue #520 deliberately reversed this: a missing global operator id used to be refused, and
    /// now it must be accepted, because the operator id is each business's own and is resolved per
    /// call from its stored connection. Requiring one here would make a business-specific setting a
    /// condition for the whole API - including every non-Nayax feature - to start.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_operator_id_is_accepted_because_the_operator_id_is_per_business(string? operatorId)
    {
        var options = new NayaxLynxOptions { BaseUrl = "https://lynx.nayax.com", OperatorId = operatorId! };

        var exception = Record.Exception(() => NayaxLynxConfiguration.ValidateNonSecretFields(options));

        Assert.Null(exception);
    }

    /// <summary>
    /// Nothing but the base URL is needed to start: an empty <c>NayaxLynx</c> section binds to the
    /// defaults and validates, so a deployment that has already removed the global operator id and
    /// token starts normally.
    /// </summary>
    [Fact]
    public void The_default_options_with_no_credential_at_all_validate()
    {
        var exception = Record.Exception(
            () => NayaxLynxConfiguration.ValidateNonSecretFields(new NayaxLynxOptions()));

        Assert.Null(exception);
    }

    [Fact]
    public void The_base_address_is_the_configured_host_plus_the_operational_api_path()
    {
        var options = new NayaxLynxOptions { BaseUrl = " https://qa-lynx.nayax.com/ " };

        Assert.Equal(
            new Uri("https://qa-lynx.nayax.com/operational/v1/"),
            NayaxLynxConfiguration.BuildBaseAddress(options));
    }

    /// <summary>
    /// The trailing slash is load-bearing: <see cref="Uri"/> resolution against a base address
    /// without one drops its last segment, so every endpoint would silently lose <c>/v1</c>.
    /// </summary>
    [Fact]
    public void A_relative_endpoint_resolves_under_the_operational_api_path()
    {
        var baseAddress = NayaxLynxConfiguration.BuildBaseAddress(
            new NayaxLynxOptions { BaseUrl = "https://lynx.nayax.com" });

        Assert.Equal(
            "https://lynx.nayax.com/operational/v1/operators/123/products",
            new Uri(baseAddress, "operators/123/products").ToString());
    }

    [Fact]
    public void An_invalid_base_url_is_refused_before_a_base_address_is_built()
    {
        var failure = Assert.Throws<InvalidOperationException>(
            () => NayaxLynxConfiguration.BuildBaseAddress(new NayaxLynxOptions { BaseUrl = "http://lynx.nayax.com" }));

        Assert.Contains("NayaxLynx:BaseUrl", failure.Message, StringComparison.Ordinal);
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
