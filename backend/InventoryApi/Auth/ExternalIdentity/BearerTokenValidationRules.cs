using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;

namespace InventoryApi.Auth.ExternalIdentity;

/// <summary>
/// The validation rules this API states for itself on top of the identity library's own, applied
/// to each bearer scheme (issue #710, design § 5.1 rules 3, 4 and 6).
///
/// Each rule is additive and fail-closed: it can only turn an otherwise valid token into a
/// <c>401</c>, never the other way round, and a token a previous rule already refused is not
/// re-examined. The library keeps owning signature, lifetime and - per scheme - issuer validation;
/// these are the three things a reviewer should be able to read in one place because getting one
/// of them wrong is how a second trusted issuer becomes an impersonation path.
/// </summary>
internal static class BearerTokenValidationRules
{
    /// <summary>The delegated-scope claim, in the short and the WS-* mapped spelling.</summary>
    private static readonly string[] ScopeClaimTypes =
    [
        "scp",
        "http://schemas.microsoft.com/identity/claims/scope",
    ];

    /// <summary>
    /// Claims that exist to bind an <em>ID</em> token to the sign-in that produced it. An access
    /// token for this API has no reason to carry one, and an ID token presented as a bearer token
    /// is the classic substitution (design § 4 and § 11, T3).
    /// </summary>
    private static readonly string[] IdTokenOnlyClaimTypes = ["nonce", "at_hash", "c_hash"];

    /// <summary>The directory tenant claim, in the same two spellings.</summary>
    private static readonly string[] DirectoryTenantIdClaimTypes =
    [
        "tid",
        "http://schemas.microsoft.com/identity/claims/tenantid",
    ];

    /// <summary>
    /// Pins the audiences this scheme accepts, replacing anything inferred from a client id.
    ///
    /// The <c>AudienceValidator</c> is cleared deliberately. <c>Microsoft.Identity.Web</c>
    /// registers one, and the token-validation library calls that delegate <em>instead of</em>
    /// comparing the token's audience against the valid-audience list - so pinning a list while
    /// leaving the delegate in place would be configuration that decides nothing. Clearing it is
    /// what makes this list the rule, and the list a caller passes here is the set that delegate
    /// accepted.
    /// </summary>
    public static void PinAudiences(JwtBearerOptions options, IReadOnlyList<string> audiences)
    {
        options.Audience = null;

        var parameters = options.TokenValidationParameters;
        parameters.ValidateAudience = true;
        parameters.AudienceValidator = null;
        parameters.ValidAudience = null;
        parameters.ValidAudiences = audiences;
    }

    /// <summary>
    /// Requires the token to be a <em>delegated access token for this API</em>, which is the only
    /// kind of token any endpoint here has ever been reachable with.
    ///
    /// A delegated access token carries the scopes it was granted in <c>scp</c>; an ID token and
    /// an app-only token do not, and this API has no app-only surface to add a branch for. Which
    /// scope is required stays exactly where it was - <c>[RequiredScope("access_as_user")]</c> on
    /// every controller, answering <c>403</c> for a delegated token that lacks it - so the
    /// <c>401</c>/<c>403</c> separation is unchanged for every token this API was ever meant to
    /// accept. What changes is that a token with no delegated purpose at all is now refused by
    /// authentication rather than by authorization.
    /// </summary>
    public static void RequireDelegatedAccessToken(JwtBearerOptions options) =>
        AddRule(options, context =>
        {
            if (!HasAnyClaim(context.Principal, ScopeClaimTypes)
                || HasAnyClaim(context.Principal, IdTokenOnlyClaimTypes))
            {
                // No token content in the message: a failure reason is logged, and a bearer token
                // and its claims never are.
                context.Fail("The bearer token is not a delegated access token for this API.");
            }

            return Task.CompletedTask;
        });

    /// <summary>
    /// Requires the token's <c>tid</c> to be the one tenant this scheme is pinned to (design
    /// § 5.1 rule 3).
    ///
    /// The scheme's issuer validation already restricts the token to that tenant's authority; this
    /// asserts the other direction explicitly, so the tenant a validated token asserts can never
    /// be a value the token chose. A token with no <c>tid</c> at all identifies no actor either
    /// way - External ID issues <c>oid</c> and <c>tid</c> only when the request asks for the
    /// <c>profile</c> scope (design § 4) - and is refused here rather than allowed to authenticate
    /// as a caller nobody can attribute.
    /// </summary>
    public static void RequireDirectoryTenant(JwtBearerOptions options, Guid tenantId) =>
        AddRule(options, context =>
        {
            var claims = DirectoryTenantIdClaimTypes
                .SelectMany(claimType => context.Principal?.FindAll(claimType) ?? [])
                .ToArray();

            if (claims.Length == 0
                || claims.Any(claim => !Guid.TryParse(claim.Value?.Trim(), out var claimed) || claimed != tenantId))
            {
                context.Fail("The bearer token does not come from the configured customer identity tenant.");
            }

            return Task.CompletedTask;
        });

    private static void AddRule(JwtBearerOptions options, Func<TokenValidatedContext, Task> rule)
    {
        options.Events ??= new JwtBearerEvents();

        var alreadyConfigured = options.Events.OnTokenValidated;

        options.Events.OnTokenValidated = async context =>
        {
            await alreadyConfigured(context);

            // Nothing re-examines a token an earlier rule - or the library's own handler - has
            // already refused or answered.
            if (context.Result is null)
            {
                await rule(context);
            }
        };
    }

    private static bool HasAnyClaim(ClaimsPrincipal? principal, string[] claimTypes) =>
        principal is not null
        && claimTypes.Any(claimType => principal.FindAll(claimType)
            .Any(claim => !string.IsNullOrWhiteSpace(claim.Value)));
}
