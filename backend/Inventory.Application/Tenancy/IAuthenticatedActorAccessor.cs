namespace Inventory.Application.Tenancy;

/// <summary>
/// Narrow port for "who is calling", owned by the Application layer and implemented at the
/// InventoryApi boundary (see <c>InventoryApi.Auth.EntraActorIdentityAccessor</c>).
///
/// Claims parsing stays in InventoryApi: this port hands Application a Domain
/// <see cref="Inventory.Domain.Tenancy.ActorIdentity"/> instead of a <c>ClaimsPrincipal</c>, so
/// no use case ever depends on ASP.NET Core or Microsoft.Identity.Web.
/// </summary>
public interface IAuthenticatedActorAccessor
{
    ActorIdentityResult GetCurrentActor();
}
