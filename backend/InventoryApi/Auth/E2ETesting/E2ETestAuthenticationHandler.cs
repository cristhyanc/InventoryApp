using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Web;

namespace InventoryApi.Auth.E2ETesting;

/// <summary>
/// The test-only authentication scheme of the dedicated end-to-end host (issue #46).
///
/// It exists so a browser-level suite can reach protected pages and endpoints without an
/// interactive Microsoft Entra sign-in, real credentials, a token, or any network call to an
/// identity provider. It is registered <em>only</em> when
/// <see cref="E2ETestEnvironment.IsEnabled"/> is true; see
/// <see cref="InventoryApiAuthenticationExtensions"/>.
///
/// What it does <em>not</em> do matters as much as what it does:
///
/// <list type="bullet">
///   <item>It is not a bypass. It authenticates a caller, and nothing more: the principal it
///   issues carries the same <c>(tid, oid)</c> claim pair and the same <c>scp</c> API scope a
///   real Entra access token carries, so <c>[Authorize]</c>,
///   <c>[RequiredScope("access_as_user")]</c>, <c>BusinessScopeMiddleware</c>, the membership
///   lookup, and the tenant query filters all run exactly as they do in production.</item>
///   <item>It authorises nothing. An actor with no <c>BusinessMembership</c> row authenticates
///   and is then refused with 403 by the business-scope middleware, and no actor can read
///   another business's data.</item>
///   <item>It cannot be enabled by a request. The header below only chooses between synthetic
///   identities that are already allowed in this host; an unknown value, a missing value, or a
///   repeated header authenticates nobody and the request is answered 401.</item>
/// </list>
/// </summary>
public sealed class E2ETestAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    /// <summary>
    /// The scheme name. In the E2E host it is also the default authenticate/challenge scheme, and
    /// it is the only registered scheme, so no request can select a different one.
    /// </summary>
    public const string SchemeName = "E2ETest";

    public E2ETestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(E2ETestActors.ActorHeaderName, out var headerValues))
        {
            // No actor named at all: anonymous, which the authorization middleware answers with
            // 401. NoResult rather than Fail so an anonymous request to the health endpoints
            // behaves exactly as it does under the real scheme.
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        // Exactly one value, or nobody: two actor headers are an ambiguity, and resolving it by
        // precedence would let the request decide which identity wins.
        var actor = headerValues.Count == 1 ? E2ETestActors.Find(headerValues[0]) : null;

        if (actor is null)
        {
            return Task.FromResult(AuthenticateResult.Fail(
                $"The {E2ETestActors.ActorHeaderName} header does not name a known synthetic E2E test actor."));
        }

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimConstants.Tid, actor.DirectoryTenantId),
                new Claim(ClaimConstants.Oid, actor.ObjectId),
                new Claim(ClaimConstants.Scp, E2ETestActors.ApiScope),
            ],
            SchemeName);

        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
