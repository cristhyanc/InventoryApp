using InventoryApi.Auth.ExternalIdentity;
using Microsoft.AspNetCore.Authentication.JwtBearer;

namespace InventoryApi.Auth.PriceCollector;

/// <summary>
/// The third named bearer scheme: the one that accepts the scheduled price collector's app-only
/// token, and only on the collector endpoints (issue #924, parent #817).
///
/// <para><strong>It is reached by endpoint, not by issuer.</strong> The two schemes of issue #710 are
/// chosen by <see cref="IssuerSelectingPolicyScheme"/> from a token's unvalidated issuer, which is
/// right for two authorities that both serve people. This one is different: the collector's token
/// comes from the <em>same</em> workforce authority a member's does, so an issuer could not tell them
/// apart. What tells them apart is where the request is going - the collector endpoints declare
/// <c>[Authorize(AuthenticationSchemes = </c><see cref="Name"/><c>)]</c>, so this scheme authenticates
/// those endpoints and nothing else, and the default scheme keeps answering every other request
/// exactly as it did. That is the issue's second criterion made structural rather than checked: an
/// app-only token presented to any other endpoint is refused by
/// <see cref="BearerTokenValidationRules.RequireDelegatedAccessToken"/>, which is untouched, and this
/// scheme cannot be reached from a route that does not name it.</para>
///
/// <para><strong>It is pinned in four directions at once, and all four are required.</strong> The
/// library owns the signature and the lifetime; the authority owns the issuer and the signing keys;
/// and three rules of this repository's own own the rest - the audience is this API's own
/// registration and nothing else, the <c>tid</c> is the configured collector tenant, and the
/// <c>azp</c>/<c>appid</c> is the configured collector application. The token must also carry the
/// <see cref="PriceCollectorRole.Name"/> application role and <em>no</em> <c>scp</c> at all. Drop any
/// one of those and the surface is reachable by somebody else: the workforce authority is
/// multi-tenant, so another tenant can consent to this API and assign the role to an app of its own,
/// and Entra puts an assigned app role in a <em>user's</em> token too.</para>
///
/// <para><strong>With no collector configured this scheme accepts nothing.</strong> It is still
/// registered, because an endpoint naming an unregistered scheme is a <c>500</c> rather than a
/// <c>401</c> and "the collector is not configured" must not look like a server fault. What it is
/// given instead is no authority and a message rule that refuses the request before validation is
/// attempted, so every collector request is answered <c>401</c> - the shipped state, and the
/// rollback state.</para>
/// </summary>
public static class PriceCollectorBearerScheme
{
    /// <summary>
    /// The scheme name, named by the collector endpoints and by nothing else. It is never the
    /// application's default scheme: making it one would offer an app-only path to every endpoint in
    /// the API, which is precisely what this design refuses.
    /// </summary>
    public const string Name = "PriceCollectorBearer";

    /// <summary>
    /// Configures the scheme for a trusted collector: the workforce authority of its own tenant, this
    /// API's own audience, and the three claim pins.
    /// </summary>
    /// <param name="options">The scheme's options.</param>
    /// <param name="metadataAuthority">
    /// The authority whose OpenID Connect metadata document publishes the issuer and the signing keys
    /// this scheme accepts - the collector tenant's own workforce authority. The issuer is read from
    /// that document rather than written here, which is what "pinned, not templated" means.
    /// </param>
    /// <param name="audiences">
    /// The audiences this API answers to, which are this API's own registration's and never the
    /// collector's: a token the collector obtained for some other resource is not a token for this
    /// API.
    /// </param>
    /// <param name="tenantId">The collector tenant, which an accepted token's <c>tid</c> must be.</param>
    /// <param name="clientId">
    /// The collector's application, which an accepted token's <c>azp</c> or <c>appid</c> must be.
    /// </param>
    internal static void Configure(
        JwtBearerOptions options,
        string metadataAuthority,
        IReadOnlyList<string> audiences,
        Guid tenantId,
        Guid clientId)
    {
        options.Authority = metadataAuthority;

        // Metadata, and therefore the issuer and the signing keys, is read over HTTPS from the one
        // configured authority and from nowhere else. An authority that is unreachable, or whose key
        // set cannot be refreshed, leaves this scheme unable to validate anything, which is a 401 -
        // validation never degrades to trusting an unverified token.
        options.RequireHttpsMetadata = true;

        // The same claim shape the other two schemes produce: short JWT claim names, so one set of
        // rules reads every scheme's principal without a per-issuer special case.
        options.MapInboundClaims = false;

        var parameters = options.TokenValidationParameters;

        // Deliberately no ValidIssuer of this repository's own: leaving it unset is what makes the
        // bearer handler accept exactly the issuer the configured tenant's metadata document
        // publishes - one tenant, not the multi-tenant `common` template the workforce scheme uses.
        parameters.ValidateIssuer = true;
        parameters.ValidIssuer = null;
        parameters.ValidIssuers = null;

        parameters.ValidateIssuerSigningKey = true;

        // Framework default clock skew, unchanged and never widened.
        parameters.ValidateLifetime = true;

        BearerTokenValidationRules.PinAudiences(options, audiences);
        BearerTokenValidationRules.RequireApplicationRole(options, PriceCollectorRole.Name);
        BearerTokenValidationRules.RequireDirectoryTenant(options, tenantId);
        BearerTokenValidationRules.RequireClientApplication(options, clientId);
    }

    /// <summary>
    /// Configures the scheme for a deployment that trusts no collector: nothing is validated, because
    /// nothing is accepted. Every request that names this scheme is answered with the bearer
    /// challenge, so an unconfigured collector is a <c>401</c> and never a <c>500</c>.
    /// </summary>
    internal static void ConfigureRefusingEverything(JwtBearerOptions options)
    {
        options.Events ??= new JwtBearerEvents();

        // Before validation rather than inside it: with no authority there is no metadata document
        // to read, and a handler that attempted validation would fault the request instead of
        // refusing it. NoResult() means "this scheme did not authenticate anybody", which the
        // authorization middleware answers with the challenge below.
        options.Events.OnMessageReceived = context =>
        {
            context.NoResult();
            return Task.CompletedTask;
        };
    }

    /// <summary>
    /// The authority whose metadata publishes the issuer and keys of one Entra tenant's workforce
    /// tokens: the configured instance's origin, the tenant id, and the v2 path.
    ///
    /// <para>It is built from the same <c>AzureAd:Instance</c> the workforce scheme uses rather than
    /// hard-coded, so a sovereign or national cloud deployment configures one host and both schemes
    /// follow it; a blank or unusable instance falls back to the public cloud's, which is the host
    /// the workforce section carries in source control.</para>
    /// </summary>
    internal static string MetadataAuthorityFor(string? instance, Guid tenantId)
    {
        var origin = Uri.TryCreate(instance?.Trim(), UriKind.Absolute, out var instanceUri)
            && string.Equals(instanceUri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
                ? instanceUri.GetLeftPart(UriPartial.Authority)
                : "https://login.microsoftonline.com";

        return $"{origin}/{tenantId:D}/v2.0";
    }
}
