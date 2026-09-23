using System.Security.Claims;
using Inventory.Application.Tenancy;
using Inventory.Domain.Tenancy;

namespace InventoryApi.Auth;

/// <summary>
/// The one place in the application where Microsoft Entra claims are turned into an application
/// actor identity (issue #64).
///
/// Claims parsing stays at the InventoryApi boundary: this adapter reads the validated
/// <c>(tid, oid)</c> pair off the current <see cref="ClaimsPrincipal"/> and hands the Application
/// layer a Domain <see cref="ActorIdentity"/>, so no use case, policy, or persistence adapter
/// ever sees ASP.NET Core or Microsoft.Identity.Web types.
///
/// It reads nothing else. Email address, display name, and the directory tenant on its own are
/// explicitly not identity here: they are mutable or shared, and issue #64 requires the stable
/// claim pair.
/// </summary>
public sealed class EntraActorIdentityAccessor : IAuthenticatedActorAccessor
{
    /// <summary>
    /// The Entra directory tenant id claim. Microsoft.Identity.Web leaves short JWT claim names
    /// in place when inbound claim mapping is off, while the classic WS-* mapping rewrites them
    /// to the schemas.microsoft.com URI, so both spellings are accepted.
    /// </summary>
    private static readonly string[] DirectoryTenantIdClaimTypes =
    [
        "http://schemas.microsoft.com/identity/claims/tenantid",
        "tid",
    ];

    /// <summary>The Entra object id claim, in the same two spellings.</summary>
    private static readonly string[] ObjectIdClaimTypes =
    [
        "http://schemas.microsoft.com/identity/claims/objectidentifier",
        "oid",
    ];

    private readonly IHttpContextAccessor _httpContextAccessor;

    public EntraActorIdentityAccessor(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public ActorIdentityResult GetCurrentActor()
    {
        var principal = _httpContextAccessor.HttpContext?.User;

        if (principal?.Identity is not { IsAuthenticated: true })
        {
            return ActorIdentityResult.NotAuthenticated();
        }

        // Both halves are required, both must be well-formed GUIDs, and every recognized
        // spelling present must agree. A validated token that fails any of those identifies no
        // actor, so the caller is denied rather than resolved from a weaker substitute.
        if (!TryResolveGuidClaim(principal, DirectoryTenantIdClaimTypes, out var directoryTenantId)
            || !TryResolveGuidClaim(principal, ObjectIdClaimTypes, out var objectId))
        {
            return ActorIdentityResult.Unidentifiable();
        }

        return ActorIdentity.TryCreate(directoryTenantId.ToString("D"), objectId.ToString("D"), out var actor)
            && actor is not null
                ? ActorIdentityResult.Identified(actor)
                : ActorIdentityResult.Unidentifiable();
    }

    /// <summary>
    /// Collapses every claim carrying this identifier - across both supported spellings, and
    /// across repeated claims of the same type - into the single GUID they all agree on.
    ///
    /// It fails on a malformed value and on disagreement rather than preferring one spelling.
    /// A token whose short and mapped claims name two different directories or two different
    /// actors is not a token this application can attribute ownership from, and silently taking
    /// whichever one happened to be checked first would let the choice of claim spelling decide
    /// whose data the caller reads.
    /// </summary>
    private static bool TryResolveGuidClaim(ClaimsPrincipal principal, string[] claimTypes, out Guid value)
    {
        Guid? agreed = null;

        foreach (var claimType in claimTypes)
        {
            foreach (var claim in principal.FindAll(claimType))
            {
                // Guid.TryParse rejects null, blank, and anything that is not a GUID.
                if (!Guid.TryParse(claim.Value?.Trim(), out var parsed))
                {
                    value = default;
                    return false;
                }

                if (agreed is null)
                {
                    agreed = parsed;
                }
                else if (agreed.Value != parsed)
                {
                    value = default;
                    return false;
                }
            }
        }

        value = agreed ?? default;
        return agreed.HasValue;
    }
}
