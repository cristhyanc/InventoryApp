using Inventory.Application.Members;
using Inventory.Domain.Tenancy;

namespace InventoryApi.Tests.Application.Members;

/// <summary>
/// In-memory fake of the member roster port.
///
/// It keys rows by <c>(business, membership)</c> exactly as the real adapter's statements do, so a
/// test can prove that another business's membership key answers nothing without a database. Its
/// writes mutate the stored row, which is what lets a test assert that the writer's second,
/// in-transaction read sees the state the write left - the property the real adapter gets from
/// writing inside the transaction.
///
/// Every call is also reported to <see cref="Observer"/>, so a test can interleave this store's
/// operations with <c>FakeBusinessMembershipWriteStore</c>'s and assert the order the whole
/// write path runs in. The order is the point: the authoritative read has to be inside the
/// transaction, and a fake that only returned rows could not tell a correct writer from one that
/// read before the lock was taken.
/// </summary>
public sealed class FakeBusinessMemberStore : IBusinessMemberStore
{
    public const string List = "member-list";
    public const string Find = "member-find";
    public const string WriteRole = "member-write-role";
    public const string WriteActive = "member-write-active";

    private readonly Dictionary<(int BusinessId, int MembershipId), MemberMembership> _rows = [];

    /// <summary>Where every call is reported, so one test can see the whole order.</summary>
    public Action<string>? Observer { get; set; }

    /// <summary>
    /// Runs once, immediately after the next <see cref="FindAsync"/> has answered. It is how a test
    /// stages a change that lands between the writer's two reads, which is the window the
    /// in-transaction read exists to close.
    /// </summary>
    public Action? OnceAfterFind { get; set; }

    /// <summary>The instant the last state change was written with.</summary>
    public DateTime? LastStatusChangedAtUtc { get; private set; }

    public void Add(int businessId, MemberMembership membership)
    {
        ArgumentNullException.ThrowIfNull(membership);
        _rows[(businessId, membership.Member.MembershipId)] = membership;
    }

    /// <summary>The row as it stands now, for asserting what a write did or did not do.</summary>
    public MemberMembership Row(int businessId, int membershipId) => _rows[(businessId, membershipId)];

    /// <summary>
    /// Revokes a row without going through this store's own write path, standing in for another
    /// caller's committed change. It is deliberately unobserved: it is not something the writer
    /// under test did.
    /// </summary>
    public void RevokeBehindTheWritersBack(int businessId, int membershipId)
    {
        var row = _rows[(businessId, membershipId)];
        _rows[(businessId, membershipId)] = row with { Member = row.Member with { IsActive = false } };
    }

    public Task<IReadOnlyList<BusinessMember>> ListAsync(
        BusinessId businessId,
        CancellationToken cancellationToken)
    {
        Observer?.Invoke(List);
        return Task.FromResult<IReadOnlyList<BusinessMember>>(
        [
            .. _rows
                .Where(row => row.Key.BusinessId == businessId.Value)
                .OrderBy(row => row.Key.MembershipId)
                .Select(row => row.Value.Member),
        ]);
    }

    public Task<MemberMembership?> FindAsync(
        BusinessId businessId,
        int membershipId,
        CancellationToken cancellationToken)
    {
        Observer?.Invoke(Find);
        var answer = _rows.TryGetValue((businessId.Value, membershipId), out var row) ? row : null;

        var staged = OnceAfterFind;
        OnceAfterFind = null;
        staged?.Invoke();

        return Task.FromResult(answer);
    }

    public Task ChangeRoleAsync(
        BusinessId businessId,
        int membershipId,
        BusinessRole role,
        CancellationToken cancellationToken)
    {
        Observer?.Invoke(WriteRole);
        var row = _rows[(businessId.Value, membershipId)];
        _rows[(businessId.Value, membershipId)] = row with
        {
            StoredRole = role,
            Member = row.Member with { Role = BusinessRoles.IsSupported(role) ? role : null },
        };
        return Task.CompletedTask;
    }

    public Task ChangeActiveAsync(
        BusinessId businessId,
        int membershipId,
        bool isActive,
        DateTime statusChangedAtUtc,
        CancellationToken cancellationToken)
    {
        Observer?.Invoke(WriteActive);
        LastStatusChangedAtUtc = statusChangedAtUtc;
        var row = _rows[(businessId.Value, membershipId)];
        _rows[(businessId.Value, membershipId)] = row with
        {
            Member = row.Member with { IsActive = isActive },
        };
        return Task.CompletedTask;
    }
}
