using Inventory.Application.Members;
using Inventory.Domain.Tenancy;

namespace InventoryApi.Tests.Application.Members;

/// <summary>
/// In-memory fake of the invite port (issue #531).
///
/// It is deliberately <em>not</em> keyed by business: the real adapter adds no business predicate
/// either, because an invite is ordinary tenant-owned data whose owner the <c>AppDbContext</c>
/// query filters apply centrally. A fake that filtered by a business id would be modelling a
/// mechanism the adapter does not have, and the tenant isolation it stands for is proved where it
/// actually lives - <c>EfMemberInviteStoreTests</c> and <c>MemberInvitesApiTests</c>.
///
/// Its cancellation reproduces the one property that matters about the real statement: it cancels
/// only while the invite is live at the instant it is given, and reports whether it did. That is
/// what lets a test stage a redemption between a use case's read and its write and assert the use
/// case does not write on the strength of the stale read.
/// </summary>
public sealed class FakeMemberInviteStore : IMemberInviteStore
{
    public const string Create = "invite-create";
    public const string List = "invite-list";
    public const string Find = "invite-find";
    public const string Cancel = "invite-cancel";
    public const string CancelAll = "invite-cancel-all";

    private readonly Dictionary<int, StoredMemberInvite> _rows = [];

    private int _nextId = 1;

    /// <summary>Where every call is reported, so one test can see the whole order.</summary>
    public Action<string>? Observer { get; set; }

    /// <summary>
    /// Runs once, immediately after the next <see cref="FindAsync"/> has answered. It is how a test
    /// stages a change that lands between a use case's read and its conditional write.
    /// </summary>
    public Action? OnceAfterFind { get; set; }

    /// <summary>The hash the last created invite was stored with.</summary>
    public string? LastTokenHash { get; private set; }

    /// <summary>The row as it stands now, for asserting what a write did or did not do.</summary>
    public StoredMemberInvite Row(int inviteId) => _rows[inviteId];

    public IReadOnlyList<StoredMemberInvite> Rows => [.. _rows.Values.OrderBy(row => row.InviteId)];

    public int Add(StoredMemberInvite invite)
    {
        ArgumentNullException.ThrowIfNull(invite);
        _rows[invite.InviteId] = invite;
        _nextId = Math.Max(_nextId, invite.InviteId + 1);
        return invite.InviteId;
    }

    /// <summary>
    /// Replaces a row without going through this store's own write path, standing in for another
    /// caller's committed change. Deliberately unobserved: it is not something the use case did.
    /// </summary>
    public void ChangeBehindTheCallersBack(int inviteId, Func<StoredMemberInvite, StoredMemberInvite> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        _rows[inviteId] = change(_rows[inviteId]);
    }

    public Task<StoredMemberInvite> CreateAsync(NewMemberInvite invite, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invite);
        Observer?.Invoke(Create);
        LastTokenHash = invite.TokenHash;

        var stored = new StoredMemberInvite(
            _nextId++,
            invite.Role,
            invite.Note,
            MemberInviteStatus.Pending,
            invite.CreatedAtUtc,
            invite.ExpiresAtUtc,
            null,
            null,
            null);
        _rows[stored.InviteId] = stored;

        return Task.FromResult(stored);
    }

    public Task<IReadOnlyList<StoredMemberInvite>> ListAsync(CancellationToken cancellationToken)
    {
        Observer?.Invoke(List);
        return Task.FromResult(Rows);
    }

    public Task<StoredMemberInvite?> FindAsync(int inviteId, CancellationToken cancellationToken)
    {
        Observer?.Invoke(Find);
        var answer = _rows.TryGetValue(inviteId, out var row) ? row : null;

        var staged = OnceAfterFind;
        OnceAfterFind = null;
        staged?.Invoke();

        return Task.FromResult(answer);
    }

    public Task<bool> CancelAsync(int inviteId, DateTime cancelledAtUtc, CancellationToken cancellationToken)
    {
        Observer?.Invoke(Cancel);

        if (!_rows.TryGetValue(inviteId, out var row)
            || !MemberInvitePolicy.IsLive(row.StoredStatus, row.ExpiresAtUtc, cancelledAtUtc))
        {
            return Task.FromResult(false);
        }

        _rows[inviteId] = row with
        {
            StoredStatus = MemberInviteStatus.Cancelled,
            CancelledAtUtc = cancelledAtUtc,
        };

        return Task.FromResult(true);
    }

    public Task<int> CancelAllLiveAsync(DateTime cancelledAtUtc, CancellationToken cancellationToken)
    {
        Observer?.Invoke(CancelAll);

        var live = _rows.Values
            .Where(row => MemberInvitePolicy.IsLive(row.StoredStatus, row.ExpiresAtUtc, cancelledAtUtc))
            .ToArray();

        foreach (var row in live)
        {
            _rows[row.InviteId] = row with
            {
                StoredStatus = MemberInviteStatus.Cancelled,
                CancelledAtUtc = cancelledAtUtc,
            };
        }

        return Task.FromResult(live.Length);
    }
}
