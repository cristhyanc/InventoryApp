using Inventory.Application.Tenancy;
using Inventory.Domain.Tenancy;

namespace InventoryApi.Tests.Application.Tenancy;

/// <summary>
/// In-memory fake of the actor port, so Application tests exercise resolution without an HTTP
/// pipeline or any ClaimsPrincipal - which is the point of the port in the first place.
/// </summary>
public sealed class FakeAuthenticatedActorAccessor : IAuthenticatedActorAccessor
{
    private readonly ActorIdentityResult _result;

    private FakeAuthenticatedActorAccessor(ActorIdentityResult result)
    {
        _result = result;
    }

    public int CallCount { get; private set; }

    public static FakeAuthenticatedActorAccessor Identified(ActorIdentity actor) =>
        new(ActorIdentityResult.Identified(actor));

    public static FakeAuthenticatedActorAccessor NotAuthenticated() =>
        new(ActorIdentityResult.NotAuthenticated());

    public static FakeAuthenticatedActorAccessor Unidentifiable() =>
        new(ActorIdentityResult.Unidentifiable());

    public ActorIdentityResult GetCurrentActor()
    {
        CallCount++;
        return _result;
    }
}
