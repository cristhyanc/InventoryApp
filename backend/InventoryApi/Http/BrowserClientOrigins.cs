namespace InventoryApi.Http;

/// <summary>
/// The browser origins the API's one CORS policy allows to call it.
///
/// <para><strong>The two fixed origins are unchanged.</strong> The local Angular dev server and the
/// production Static Web App are written here, as they always were in <c>Program.cs</c>, so no
/// configuration can remove the production frontend or swap it for another one.</para>
///
/// <para><strong>A deployment may add origins, and only by configuration.</strong> The hosted testing
/// environment's frontend is a separate Static Web App on a different host (see
/// <c>docs/testing-environment.md</c>), so its API lists that host in
/// <c>Cors:AdditionalAllowedOrigins</c> (App Service settings <c>Cors__AdditionalAllowedOrigins__0</c>,
/// <c>__1</c>, ...). The section is absent in source control, which is the shipped state: production
/// allows exactly the two fixed origins. Each entry must be an <c>https</c> origin - scheme and host,
/// optionally a port - with no path, query, fragment, user information or wildcard. Anything else
/// fails startup instead of being ignored, because a typo that silently allowed nothing (or more than
/// intended) would only show up as a broken sign-in in the browser.</para>
/// </summary>
public static class BrowserClientOrigins
{
    /// <summary>The CORS policy name <c>Program.cs</c> registers and applies.</summary>
    public const string PolicyName = "AllowAngularDevClient";

    /// <summary>The configuration section holding a deployment's additional origins.</summary>
    public const string AdditionalOriginsSection = "Cors:AdditionalAllowedOrigins";

    /// <summary>The origins every deployment allows: local development and the production frontend.</summary>
    public static readonly IReadOnlyList<string> FixedOrigins =
    [
        "http://localhost:4200",
        "https://red-island-0c128c000.7.azurestaticapps.net",
    ];

    /// <summary>
    /// The fixed origins followed by the configured additional ones, normalised and without duplicates.
    /// </summary>
    /// <exception cref="InvalidOperationException">An additional origin is not an https origin.</exception>
    public static IReadOnlyList<string> Resolve(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var origins = new List<string>(FixedOrigins);
        foreach (var child in configuration.GetSection(AdditionalOriginsSection).GetChildren())
        {
            var value = child.Value;
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var origin = NormaliseAdditionalOrigin(value);
            if (!origins.Contains(origin, StringComparer.OrdinalIgnoreCase))
            {
                origins.Add(origin);
            }
        }

        return origins;
    }

    private static string NormaliseAdditionalOrigin(string value)
    {
        var trimmed = value.Trim();
        var valid = !trimmed.Contains('*', StringComparison.Ordinal)
            && Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && uri.UserInfo.Length == 0
            && uri.AbsolutePath == "/"
            && uri.Query.Length == 0
            && uri.Fragment.Length == 0
            && !trimmed.EndsWith("//", StringComparison.Ordinal);

        if (!valid)
        {
            throw new InvalidOperationException(
                $"{AdditionalOriginsSection} contains '{trimmed}', which is not an https origin. Each entry must be "
                + "https://<host> (optionally with a port) and nothing else: no path, query, fragment, user "
                + "information or wildcard. See docs/testing-environment.md.");
        }

        return new Uri(trimmed).GetLeftPart(UriPartial.Authority);
    }
}
