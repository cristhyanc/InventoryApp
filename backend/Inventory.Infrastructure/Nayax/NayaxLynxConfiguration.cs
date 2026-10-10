using System.Globalization;

namespace Inventory.Infrastructure.Nayax;

/// <summary>
/// Validates the non-secret <see cref="NayaxLynxOptions"/> fields at startup, builds the Lynx API
/// base address from them, and resolves the bearer token from its two possible configuration
/// sources.
///
/// The failure mode matters more than the success one: an incomplete configuration must fail
/// fast at startup with a message naming the setting to fix, not surface as an obscure runtime
/// failure the first time a Nayax call is made.
///
/// Since issue #520 the only setting the HTTP client needs is <see cref="NayaxLynxOptions.BaseUrl"/>:
/// the operator id and the bearer token are per business, resolved per call from the current
/// business's own stored connection. <see cref="ValidateNonSecretFields"/> therefore no longer
/// requires an operator id, and <see cref="ResolveAccessToken"/> is left for the human-run
/// <c>migrate-nayax-connection</c> command, which is what still reads the global credential until a
/// person removes it from the environment.
/// </summary>
public static class NayaxLynxConfiguration
{
    /// <summary>
    /// The Lynx API's operational path prefix, appended to the configured base URL. The trailing
    /// slash matters: without it, resolving a relative endpoint against the base address would drop
    /// the last segment.
    /// </summary>
    private const string OperationalPathPrefix = "/operational/v1/";

    /// <summary>
    /// Validates <paramref name="options"/> and returns the base address every Nayax request is
    /// resolved against (<c>https://host/operational/v1/</c>).
    /// </summary>
    /// <exception cref="InvalidOperationException">The base URL is missing or invalid.</exception>
    public static Uri BuildBaseAddress(NayaxLynxOptions options)
    {
        ValidateNonSecretFields(options);

        return new Uri(options.BaseUrl.Trim().TrimEnd('/') + OperationalPathPrefix);
    }

    /// <exception cref="InvalidOperationException">A required non-secret field is missing or invalid.</exception>
    public static void ValidateNonSecretFields(NayaxLynxOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            throw new InvalidOperationException(Invalid(
                nameof(NayaxLynxOptions.BaseUrl),
                "it is required, for example 'https://lynx.nayax.com'."));
        }

        if (!Uri.TryCreate(options.BaseUrl.Trim(), UriKind.Absolute, out var baseUri) ||
            !string.Equals(baseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(Invalid(
                nameof(NayaxLynxOptions.BaseUrl),
                "it must be an absolute https URI, for example 'https://lynx.nayax.com'."));
        }

        // No operator id check, deliberately (issue #520): the operator id is per business now, so
        // requiring a global one at startup would make a business-specific setting a condition for
        // the whole API - and for every non-Nayax feature - to start.
    }

    /// <summary>
    /// Resolves the bearer token, preferring <paramref name="consolidatedKeyValue"/> (the
    /// <c>NayaxLynx:AccessToken</c> configuration key) and falling back to
    /// <paramref name="legacyKeyValue"/> (the pre-issue-#49 <c>Nayax:Token</c> key) so the already
    /// deployed Key Vault/App Service setting (<c>Nayax__Token</c>) keeps working without a
    /// coordinated secret-rotation rollout. Takes plain strings, not <c>IConfiguration</c>, so
    /// this stays a pure function: the composition root reads configuration and this only
    /// decides precedence. Never logged: the returned value is a secret.
    /// </summary>
    public static string? ResolveAccessToken(string? consolidatedKeyValue, string? legacyKeyValue) =>
        string.IsNullOrWhiteSpace(consolidatedKeyValue) ? legacyKeyValue : consolidatedKeyValue;

    private static string Invalid(string setting, string problem) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{NayaxLynxOptions.SectionName}:{setting} is invalid: {problem}");
}
