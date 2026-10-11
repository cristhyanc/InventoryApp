using Inventory.Application.Tenancy;
using Inventory.Domain.Tenancy;

namespace InventoryApi.Tests.Application.Tenancy;

/// <summary>
/// In-memory fake of the narrow member-profile write path (issue #524). It records every write it
/// is asked to make, so a test can prove both what was written and - more often - that nothing was.
/// </summary>
public sealed class FakeMemberProfileWriteStore : IMemberProfileWriteStore
{
    private readonly int _rowsChanged;

    public FakeMemberProfileWriteStore(int rowsChanged = 1)
    {
        _rowsChanged = rowsChanged;
    }

    public List<(ActorIdentity Identity, MemberProfile Profile)> Writes { get; } = [];

    public Task<int> RefreshActiveMembershipProfileAsync(
        ActorIdentity identity,
        MemberProfile profile,
        CancellationToken cancellationToken)
    {
        Writes.Add((identity, profile));
        return Task.FromResult(_rowsChanged);
    }
}
