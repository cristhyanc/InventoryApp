using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.JsonWebTokens;

namespace InventoryApi.Auth.ExternalIdentity;

/// <summary>
/// The policy scheme in front of the two bearer schemes, which reads a token's unvalidated issuer
/// to decide which scheme validates it (issue #710, design § 5.2).
///
/// <para>
/// Picking a scheme is not trusting a claim, and this class validates nothing. Each scheme
/// performs its own complete validation - signature, issuer, audience, tenant binding, lifetime -
/// under its own rule, so a token routed to the wrong scheme is refused rather than accepted:
/// an External ID token handed to the workforce scheme fails its issuer rule, a workforce token
/// handed to the pinned scheme fails that scheme's, and a token with no issuer, a malformed one or
/// an unparseable bearer value fails whichever scheme it lands in. There is no issuer-agnostic
/// path and no scheme that accepts the union of the two rules.
/// </para>
///
/// <para>
/// The default is the workforce scheme, and that is the safe default in both directions: it is the
/// scheme that answers the challenge when there is no token at all, and it is the scheme that
/// keeps answering for every token that does not name the configured customer identity tenant - so
/// a routing mistake cannot take existing Microsoft sign-in down, which is this task's first
/// constraint.
/// </para>
/// </summary>
public static class IssuerSelectingPolicyScheme
{
    /// <summary>
    /// The scheme name, and the application's default scheme while a customer identity tenant is
    /// configured. With none configured this scheme is not registered either, and the default is
    /// the workforce bearer scheme itself.
    /// </summary>
    public const string Name = "IssuerSelectingBearer";

    private const string BearerPrefix = "Bearer ";

    /// <summary>
    /// The scheme that validates the request's bearer token: the pinned External ID scheme when
    /// the token's unvalidated <c>iss</c> names the configured tenant, and the workforce scheme in
    /// every other case, including no token, several, or an unreadable one.
    /// </summary>
    internal static string SelectScheme(HttpContext context, ExternalIdAuthority authority) =>
        authority.NamesThisTenant(ReadUnvalidatedIssuer(context))
            ? ExternalIdBearerScheme.Name
            : JwtBearerDefaults.AuthenticationScheme;

    /// <summary>
    /// Reads <c>iss</c> out of the request's bearer token without validating anything about it.
    /// Nothing here throws, and nothing here is remembered: a request carrying no token, two
    /// authorization headers, a non-bearer credential or a value that is not a JWT at all simply
    /// has no issuer to route on.
    /// </summary>
    private static string? ReadUnvalidatedIssuer(HttpContext context)
    {
        var header = context.Request.Headers.Authorization;

        if (header.Count != 1)
        {
            return null;
        }

        var value = header[0];

        if (value is null || !value.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var token = value[BearerPrefix.Length..].Trim();

        try
        {
            var handler = new JsonWebTokenHandler();
            return handler.CanReadToken(token) ? handler.ReadJsonWebToken(token).Issuer : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (Microsoft.IdentityModel.Tokens.SecurityTokenException)
        {
            return null;
        }
    }
}
