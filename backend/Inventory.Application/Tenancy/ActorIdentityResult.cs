using Inventory.Domain.Tenancy;

namespace Inventory.Application.Tenancy;

/// <summary>
/// What the API boundary was able to say about the caller: either an identified actor, or the
/// reason no actor could be identified. Deliberately a neutral Application type so the boundary
/// can report "no token" and "token without a usable (tid, oid) pair" separately without
/// exposing ASP.NET claims to Application or Domain.
/// </summary>
public sealed record ActorIdentityResult
{
    private ActorIdentityResult(ActorIdentity? actor, BusinessAccessDenialReason? denialReason)
    {
        Actor = actor;
        DenialReason = denialReason;
    }

    public ActorIdentity? Actor { get; }

    public BusinessAccessDenialReason? DenialReason { get; }

    public static ActorIdentityResult Identified(ActorIdentity actor) => new(actor, null);

    /// <summary>No authenticated actor on the current request.</summary>
    public static ActorIdentityResult NotAuthenticated() => new(null, BusinessAccessDenialReason.NotAuthenticated);

    /// <summary>Authenticated, but the token carries no usable <c>(tid, oid)</c> pair.</summary>
    public static ActorIdentityResult Unidentifiable() => new(null, BusinessAccessDenialReason.UnidentifiableActor);
}
