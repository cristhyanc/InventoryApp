using Microsoft.AspNetCore.Authentication.JwtBearer;

namespace InventoryApi.Auth.ExternalIdentity;

/// <summary>
/// The second named bearer scheme: the one pinned to the configured Microsoft Entra External ID
/// tenant (issue #710, design § 5.1 and § 5.2).
///
/// It is single-tenant on purpose, and that asymmetry with the workforce scheme is the whole
/// design. The workforce scheme stays deliberately multi-tenant - any Entra tenant's issuer, bound
/// to that token's own <c>tid</c>, exactly as the <c>common</c> authority has always worked - while
/// this one accepts one issuer, one audience and one tenant. Neither scheme accepts the other's
/// values, and the two sets are never pooled.
///
/// The issuer is not written here. The scheme discovers the configured tenant's OpenID Connect
/// metadata document and the bearer handler then accepts exactly the issuer that document
/// publishes, which is what "pinned, not templated" means: no wildcard, no pattern, no second
/// tenant, and no issuer taken from the contents of a token. The signing keys come from the same
/// document, so a key published by one authority can never validate a token claiming the other's
/// issuer.
/// </summary>
public static class ExternalIdBearerScheme
{
    /// <summary>
    /// The scheme name. It is registered only when the <c>ExternalId</c> section is complete and
    /// well formed, so in the shipped state there is no scheme by this name at all.
    /// </summary>
    public const string Name = "ExternalIdBearer";

    internal static void Configure(JwtBearerOptions options, ExternalIdAuthority authority)
    {
        options.Authority = authority.MetadataAuthority;

        // Metadata, and therefore the issuer and the signing keys, is read over HTTPS from the one
        // configured authority and from nowhere else. An authority that is unreachable, or whose
        // key set cannot be refreshed, leaves this scheme unable to validate anything, which is a
        // 401 - validation never degrades to trusting an unverified token (design § 9.3, T10).
        options.RequireHttpsMetadata = true;

        // The same claim shape the workforce scheme produces: short JWT claim names, so one
        // identity accessor reads both schemes' principals without a per-issuer special case.
        options.MapInboundClaims = false;

        var parameters = options.TokenValidationParameters;

        // Deliberately no ValidIssuer/ValidIssuers of this repository's own: leaving them unset is
        // what makes the bearer handler accept exactly the issuer the configured tenant's metadata
        // document publishes.
        parameters.ValidateIssuer = true;
        parameters.ValidIssuer = null;
        parameters.ValidIssuers = null;

        parameters.ValidateIssuerSigningKey = true;

        // Framework default clock skew, unchanged and never widened.
        parameters.ValidateLifetime = true;

        BearerTokenValidationRules.PinAudiences(options, authority.ValidAudiences);
        BearerTokenValidationRules.RequireDelegatedAccessToken(options);
        BearerTokenValidationRules.RequireDirectoryTenant(options, authority.TenantId);
    }

    /// <summary>
    /// Wraps whatever metadata source this scheme ended up with - the one the framework builds
    /// from <see cref="Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerOptions.Authority"/>
    /// - so that an authority this API cannot read refuses tokens instead of failing requests. It
    /// runs as a post-configuration step because that source does not exist until the framework's
    /// own post-configuration has created it.
    /// </summary>
    internal static void FailClosedOnUnreadableMetadata(JwtBearerOptions options, ILoggerFactory loggerFactory)
    {
        if (options.ConfigurationManager is { } authority and not FailClosedIdentityMetadata)
        {
            options.ConfigurationManager = new FailClosedIdentityMetadata(
                authority,
                loggerFactory.CreateLogger<FailClosedIdentityMetadata>());
        }
    }
}
