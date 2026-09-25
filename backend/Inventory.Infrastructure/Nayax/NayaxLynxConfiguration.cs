using System.Globalization;

namespace Inventory.Infrastructure.Nayax;

/// <summary>
/// Validates the non-secret <see cref="NayaxLynxOptions"/> fields at startup and resolves the
/// bearer token from its two possible configuration sources.
///
/// The failure mode matters more than the success one: an incomplete configuration must fail
/// fast at startup with a message naming the setting to fix, not surface as an obscure runtime
/// failure the first time a Nayax call is made.
/// </summary>
public static class NayaxLynxConfiguration
{
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

        if (string.IsNullOrWhiteSpace(options.OperatorId))
        {
            throw new InvalidOperationException(Invalid(
                nameof(NayaxLynxOptions.OperatorId),
                "it is required."));
        }
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
