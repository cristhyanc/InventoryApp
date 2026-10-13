using System.Security.Claims;
using Inventory.Application.Tenancy;
using Inventory.Domain.Tenancy;

namespace InventoryApi.Auth;

/// <summary>
/// Reads the display-only <c>name</c> and <c>preferred_username</c>/<c>email</c> claims off the
/// current request's validated principal (issue #524).
///
/// It is a separate adapter from <see cref="EntraActorIdentityAccessor"/>, and the separation is
/// the point: that class answers who is calling, and these claims must never contribute to that
/// answer. The identity half of what this accessor returns therefore comes from that accessor
/// rather than being parsed a second time here, so there is exactly one implementation of "who is
/// calling" in the application and no possibility of two that disagree.
/// </summary>
public sealed class EntraActorProfileAccessor : IActorProfileAccessor
{
    /// <summary>
    /// The display-name claim. Microsoft.Identity.Web leaves the short JWT claim name in place when
    /// inbound claim mapping is off, while the classic WS-* mapping rewrites <c>name</c> to the
    /// xmlsoap URI, so both spellings are accepted - exactly as the identity accessor accepts both
    /// spellings of <c>tid</c> and <c>oid</c>.
    /// </summary>
    private static readonly string[] DisplayNameClaimTypes =
    [
        "name",
        ClaimTypes.Name,
    ];

    /// <summary>
    /// The email claim, in preference order. <c>preferred_username</c> is what an Entra v2 token
    /// carries for a work or school account and is not rewritten by the WS-* mapping; <c>email</c>
    /// is the optional claim some tenants issue instead, and its mapped spelling. The first one
    /// present wins rather than being merged: these are two spellings of one fact, and a token that
    /// carries both carries the same address twice.
    /// </summary>
    private static readonly string[] EmailClaimTypes =
    [
        "preferred_username",
        "email",
        ClaimTypes.Email,
    ];

    private readonly IAuthenticatedActorAccessor _actorAccessor;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public EntraActorProfileAccessor(
        IAuthenticatedActorAccessor actorAccessor,
        IHttpContextAccessor httpContextAccessor)
    {
        _actorAccessor = actorAccessor;
        _httpContextAccessor = httpContextAccessor;
    }

    /// <inheritdoc />
    public ActorProfile? GetCurrentActorProfile()
    {
        // No identity, no profile. A display name and an email address are not a weaker identity to
        // fall back to: they are not an identity at all, so a token this application cannot
        // attribute to an actor has nothing to refresh.
        if (_actorAccessor.GetCurrentActor().Actor is not { } actor)
        {
            return null;
        }

        var principal = _httpContextAccessor.HttpContext?.User;
        if (principal is null)
        {
            return null;
        }

        return new ActorProfile(
            actor,
            MemberProfile.Create(
                FirstClaimValue(principal, DisplayNameClaimTypes),
                FirstClaimValue(principal, EmailClaimTypes)));
    }

    /// <summary>
    /// The value of the first claim present, in the order the spellings are listed.
    ///
    /// Unlike the identity accessor's claim reading, this does not require every spelling present to
    /// agree, and it must not: these are display values, so disagreement between two spellings of a
    /// person's name is not the security question that one is for, and refusing to store anything
    /// because a token carried both <c>preferred_username</c> and a different <c>email</c> would
    /// leave a members list empty for no benefit. A blank first value is skipped rather than
    /// winning, so a token carrying an empty <c>name</c> and a real mapped one still shows a name.
    /// </summary>
    private static string? FirstClaimValue(ClaimsPrincipal principal, string[] claimTypes)
    {
        foreach (var claimType in claimTypes)
        {
            var value = principal.FindAll(claimType)
                .Select(claim => claim.Value)
                .FirstOrDefault(claimValue => !string.IsNullOrWhiteSpace(claimValue));

            if (value is not null)
            {
                return value;
            }
        }

        return null;
    }
}
