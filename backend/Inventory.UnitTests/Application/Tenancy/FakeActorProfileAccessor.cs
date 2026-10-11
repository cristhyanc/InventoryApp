using Inventory.Application.Tenancy;
using Inventory.Domain.Tenancy;

namespace InventoryApi.Tests.Application.Tenancy;

/// <summary>
/// In-memory fake of the display-only profile port (issue #524), so a test can decide what the
/// caller's token said without building a <c>ClaimsPrincipal</c> - and can make the accessor throw,
/// which is how "scheduling a refresh never fails the request" is proved.
/// </summary>
public sealed class FakeActorProfileAccessor : IActorProfileAccessor
{
    private readonly ActorProfile? _profile;
    private readonly bool _throws;

    private FakeActorProfileAccessor(ActorProfile? profile, bool throws)
    {
        _profile = profile;
        _throws = throws;
    }

    public int CallCount { get; private set; }

    public static FakeActorProfileAccessor For(ActorIdentity identity, MemberProfile profile) =>
        new(new ActorProfile(identity, profile), throws: false);

    /// <summary>A request with no identifiable actor: no profile to read at all.</summary>
    public static FakeActorProfileAccessor Unidentifiable() => new(null, throws: false);

    public static FakeActorProfileAccessor Throwing() => new(null, throws: true);

    public ActorProfile? GetCurrentActorProfile()
    {
        CallCount++;

        return _throws
            ? throw new InvalidOperationException("The profile accessor failed.")
            : _profile;
    }
}
