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

        var directoryTenantId = FindFirstClaimValue(principal, DirectoryTenantIdClaimTypes);
        var objectId = FindFirstClaimValue(principal, ObjectIdClaimTypes);

        // Both halves are required. A validated token that carries only one of them identifies
        // no actor, so the caller is denied rather than resolved from a weaker substitute.
        return ActorIdentity.TryCreate(directoryTenantId, objectId, out var actor) && actor is not null
            ? ActorIdentityResult.Identified(actor)
            : ActorIdentityResult.Unidentifiable();
    }

    private static string? FindFirstClaimValue(ClaimsPrincipal principal, string[] claimTypes)
    {
        foreach (var claimType in claimTypes)
        {
            var value = principal.FindFirst(claimType)?.Value;
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }
}
