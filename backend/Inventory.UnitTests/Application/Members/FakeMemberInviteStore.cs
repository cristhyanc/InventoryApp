using Inventory.Application.Members;
using Inventory.Domain.Tenancy;

namespace InventoryApi.Tests.Application.Members;

/// <summary>
/// In-memory fake of the Owner-facing invite port (issue #531, re-keyed by the ownership decision
/// on issue #532).
///
/// It is keyed by business, because the real adapter now names the business in every statement:
/// <c>MemberInvite</c> is one of the five structural non-tenant-owned tables, so there is no central
/// query filter for it to defer to. A fake that ignored the business id would be modelling a
/// mechanism the adapter does not have, and would let a use case that passed the wrong business - or
/// none - still pass its tests. The relational proof stays where it belongs
/// (<c>EfMemberInviteStoreTests</c> and <c>MemberInvitesApiTests</c>); what this models is that each
/// use case hands the store the business its own caller resolved.
///
/// Its cancellation reproduces the one property that matters about the real statement: it cancels
/// only while the invite is live at the instant it is given, and reports whether it did. That is
/// what lets a test stage a redemption between a use case's read and its write and assert the use
/// case does not write on the strength of the stale read.
/// </summary>
public sealed class FakeMemberInviteStore : IMemberInviteStore
{
    /// <summary>
    /// The business a seeded invite belongs to unless a test says otherwise - the same one the test
    /// fixtures resolve for their caller, so the ordinary case reads without repeating it.
    /// </summary>
    public const int OwningBusinessId = 7;

    public const string Create = "invite-create";
    public const string List = "invite-list";
    public const string Find = "invite-find";
    public const string Cancel = "invite-cancel";
    public const string CancelAll = "invite-cancel-all";

    private readonly Dictionary<int, StoredMemberInvite> _rows = [];
    private readonly Dictionary<int, int> _owners = [];

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

    /// <summary>The business the last created invite was stored under.</summary>
    public int? LastCreatedForBusinessId { get; private set; }

    /// <summary>The row as it stands now, for asserting what a write did or did not do.</summary>
    public StoredMemberInvite Row(int inviteId) => _rows[inviteId];

    /// <summary>The business a stored row belongs to, for asserting the owner a creation wrote.</summary>
    public int OwnerOf(int inviteId) => _owners[inviteId];

    public IReadOnlyList<StoredMemberInvite> Rows => [.. _rows.Values.OrderBy(row => row.InviteId)];

    public int Add(StoredMemberInvite invite, int businessId = OwningBusinessId)
    {
        ArgumentNullException.ThrowIfNull(invite);
        _rows[invite.InviteId] = invite;
        _owners[invite.InviteId] = businessId;
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

    public Task<StoredMemberInvite> CreateAsync(
        BusinessId businessId,
        NewMemberInvite invite,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invite);
        Observer?.Invoke(Create);
        LastTokenHash = invite.TokenHash;
        LastCreatedForBusinessId = businessId.Value;

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
        _owners[stored.InviteId] = businessId.Value;

        return Task.FromResult(stored);
    }

    public Task<IReadOnlyList<StoredMemberInvite>> ListAsync(
        BusinessId businessId,
        CancellationToken cancellationToken)
    {
        Observer?.Invoke(List);

        return Task.FromResult<IReadOnlyList<StoredMemberInvite>>(
            [.. Rows.Where(row => Owns(businessId, row.InviteId))]);
    }

    public Task<StoredMemberInvite?> FindAsync(
        BusinessId businessId,
        int inviteId,
        CancellationToken cancellationToken)
    {
        Observer?.Invoke(Find);

        // Another business's invite key is the same answer as one that exists nowhere, exactly as
        // the real statement's WHERE clause makes it.
        var answer = Owns(businessId, inviteId) && _rows.TryGetValue(inviteId, out var row)
            ? row
            : null;

        var staged = OnceAfterFind;
        OnceAfterFind = null;
        staged?.Invoke();

        return Task.FromResult(answer);
    }

    public Task<bool> CancelAsync(
        BusinessId businessId,
        int inviteId,
        DateTime cancelledAtUtc,
        CancellationToken cancellationToken)
    {
        Observer?.Invoke(Cancel);

        if (!Owns(businessId, inviteId)
            || !_rows.TryGetValue(inviteId, out var row)
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

    public Task<int> CancelAllLiveAsync(
        BusinessId businessId,
        DateTime cancelledAtUtc,
        CancellationToken cancellationToken)
    {
        Observer?.Invoke(CancelAll);

        var live = _rows.Values
            .Where(row => Owns(businessId, row.InviteId))
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

    private bool Owns(BusinessId businessId, int inviteId) =>
        _owners.TryGetValue(inviteId, out var owner) && owner == businessId.Value;
}
