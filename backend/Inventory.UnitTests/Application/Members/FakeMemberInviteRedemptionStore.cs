using Inventory.Application.Members;
using Inventory.Domain.Tenancy;

namespace InventoryApi.Tests.Application.Members;

/// <summary>
/// In-memory fake of the redemption port (issue #532).
///
/// It reproduces the two properties of the real adapter a decision can go wrong against, and
/// nothing else:
///
/// <list type="bullet">
///   <item><b>Every statement is keyed by business.</b> The lookup answers by token hash alone,
///   because that is all the caller presents, and every other method matches only rows of the
///   business it was given - so a use case that passed a business the invite does not belong to
///   would write nothing here, exactly as it would write nothing in the database.</item>
///   <item><b>The two writes are conditional.</b> The reactivation matches only a revoked row and
///   the close matches only a live invite - judged at the instant the caller hands it, exactly as
///   the real <c>UPDATE</c>'s <c>WHERE</c> clause is - each reporting whether it did anything. That
///   is what lets a test stage another redemption, or an expiry, landing between this one's read and
///   its write and assert that nothing is reported as granted on the strength of the stale read.</item>
/// </list>
///
/// The relational and concurrency proof stays where it belongs -
/// <c>EfMemberInviteRedemptionStoreTests</c> and <c>MemberInviteRedemptionConcurrencyTests</c>, which
/// run two real writers against a real SQLite file.
/// </summary>
public sealed class FakeMemberInviteRedemptionStore : IMemberInviteRedemptionStore
{
    public const string Lookup = "invite-by-token";
    public const string FindMembership = "membership-find";
    public const string CreateMembership = "membership-create";
    public const string ReactivateMembership = "membership-reactivate";
    public const string MarkUsed = "invite-mark-used";

    private readonly Dictionary<string, Invite> _invites = new(StringComparer.Ordinal);
    private readonly Dictionary<(int BusinessId, string Tid, string Oid), Membership> _memberships = [];
    private readonly List<string> _operations = [];

    private int _nextMembershipId = 1;

    /// <summary>Every call this store saw, in order.</summary>
    public IReadOnlyList<string> Operations => _operations;

    /// <summary>
    /// Where every call is also reported, so one test can see this store's calls and the write
    /// guard's in a single order - which is the only way "the invite was read inside the
    /// transaction" is assertable at all.
    /// </summary>
    public Action<string>? Observer { get; set; }

    /// <summary>
    /// Runs once, immediately after the next <see cref="FindByTokenHashAsync"/> has answered - how a
    /// test stages a change landing between the authoritative read and the conditional write.
    /// </summary>
    public Action? OnceAfterLookup { get; set; }

    /// <summary>
    /// Runs once, immediately after the next <see cref="FindMembershipAsync"/> has answered - how a
    /// test stages a change landing between the in-transaction read of the caller's own membership
    /// row and the conditional write that acts on it (issue #681). <see cref="OnceAfterLookup"/>
    /// fires too early for that: a change made there is still seen by the membership read.
    /// </summary>
    public Action? OnceAfterMembershipLookup { get; set; }

    /// <summary>The business ids the store was asked to write in, so a test can assert the owner.</summary>
    public List<int> WrittenBusinessIds { get; } = [];

    public void AddInvite(
        string tokenHash,
        int inviteId,
        int businessId,
        BusinessRole role,
        MemberInviteStatus status,
        DateTime expiresAtUtc,
        BusinessStatus businessStatus = BusinessStatus.Active) =>
        _invites[tokenHash] = new Invite(
            inviteId,
            businessId,
            role,
            status,
            expiresAtUtc,
            businessStatus,
            UsedByMembershipId: null,
            UsedAtUtc: null);

    public void AddMembership(int businessId, ActorIdentity identity, int membershipId, bool isActive)
    {
        ArgumentNullException.ThrowIfNull(identity);
        _memberships[Key(businessId, identity)] = new Membership(
            membershipId,
            isActive,
            BusinessRole.Operator,
            DisplayName: null,
            Email: null,
            StatusChangedAtUtc: null);
        _nextMembershipId = Math.Max(_nextMembershipId, membershipId + 1);
    }

    /// <summary>
    /// Changes a row without going through this store's own write path, standing in for another
    /// caller's committed change. Deliberately unobserved: it is not something the use case did.
    /// </summary>
    public void ChangeBehindTheCallersBack(string tokenHash, Func<Invite, Invite> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        _invites[tokenHash] = change(_invites[tokenHash]);
    }

    public void ActivateBehindTheCallersBack(int businessId, ActorIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var key = Key(businessId, identity);
        _memberships[key] = _memberships[key] with { IsActive = true };
    }

    /// <summary>
    /// Takes a membership row away without going through this store's own write path - the second
    /// reason the conditional reactivation can match nothing, which is not "that person is now a
    /// member" (issue #681). Deliberately unobserved, like the activation above.
    /// </summary>
    public void RemoveBehindTheCallersBack(int businessId, ActorIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        _memberships.Remove(Key(businessId, identity));
    }

    public Invite InviteFor(string tokenHash) => _invites[tokenHash];

    public Membership MembershipFor(int businessId, ActorIdentity identity) =>
        _memberships[Key(businessId, identity)];

    public bool HasMembership(int businessId, ActorIdentity identity) =>
        _memberships.ContainsKey(Key(businessId, identity));

    public Task<RedeemableMemberInvite?> FindByTokenHashAsync(
        string tokenHash,
        CancellationToken cancellationToken)
    {
        Observe(Lookup);

        var answer = _invites.TryGetValue(tokenHash, out var invite)
            ? new RedeemableMemberInvite(
                invite.InviteId,
                BusinessId.From(invite.BusinessId),
                invite.Role,
                invite.StoredStatus,
                invite.ExpiresAtUtc,
                invite.BusinessStatus)
            : null;

        var staged = OnceAfterLookup;
        OnceAfterLookup = null;
        staged?.Invoke();

        return Task.FromResult(answer);
    }

    public Task<RedeemingMemberMembership?> FindMembershipAsync(
        BusinessId businessId,
        ActorIdentity identity,
        CancellationToken cancellationToken)
    {
        Observe(FindMembership);

        var answer = _memberships.TryGetValue(Key(businessId.Value, identity), out var membership)
            ? new RedeemingMemberMembership(membership.MembershipId, membership.IsActive)
            : (RedeemingMemberMembership?)null;

        var staged = OnceAfterMembershipLookup;
        OnceAfterMembershipLookup = null;
        staged?.Invoke();

        return Task.FromResult(answer);
    }

    public Task<int> CreateActiveMembershipAsync(
        BusinessId businessId,
        ActorIdentity identity,
        MemberProfile profile,
        BusinessRole role,
        DateTime createdAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        Observe(CreateMembership);
        WrittenBusinessIds.Add(businessId.Value);

        var membershipId = _nextMembershipId++;
        _memberships[Key(businessId.Value, identity)] = new Membership(
            membershipId,
            IsActive: true,
            role,
            profile.DisplayName,
            profile.Email,
            createdAtUtc);

        return Task.FromResult(membershipId);
    }

    public Task<bool> ReactivateMembershipAsync(
        BusinessId businessId,
        int membershipId,
        BusinessRole role,
        MemberProfile profile,
        DateTime reactivatedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        Observe(ReactivateMembership);
        WrittenBusinessIds.Add(businessId.Value);

        var found = _memberships
            .Where(entry => entry.Key.BusinessId == businessId.Value)
            .Where(entry => entry.Value.MembershipId == membershipId && !entry.Value.IsActive)
            .ToArray();

        if (found.Length != 1)
        {
            return Task.FromResult(false);
        }

        var (key, existing) = found[0];
        _memberships[key] = existing with
        {
            IsActive = true,
            Role = role,
            // An absent claim leaves the stored value alone rather than erasing it (issue #524).
            DisplayName = profile.DisplayName ?? existing.DisplayName,
            Email = profile.Email ?? existing.Email,
            StatusChangedAtUtc = reactivatedAtUtc,
        };

        return Task.FromResult(true);
    }

    public Task<bool> MarkUsedAsync(
        BusinessId businessId,
        int inviteId,
        int membershipId,
        DateTime usedAtUtc,
        CancellationToken cancellationToken)
    {
        Observe(MarkUsed);
        WrittenBusinessIds.Add(businessId.Value);

        var found = _invites
            .Where(entry => entry.Value.InviteId == inviteId && entry.Value.BusinessId == businessId.Value)
            .Where(entry => MemberInvitePolicy.IsLive(
                entry.Value.StoredStatus,
                entry.Value.ExpiresAtUtc,
                usedAtUtc))
            .ToArray();

        if (found.Length != 1)
        {
            return Task.FromResult(false);
        }

        var (hash, invite) = found[0];
        _invites[hash] = invite with
        {
            StoredStatus = MemberInviteStatus.Used,
            UsedByMembershipId = membershipId,
            UsedAtUtc = usedAtUtc,
        };

        return Task.FromResult(true);
    }

    private void Observe(string operation)
    {
        _operations.Add(operation);
        Observer?.Invoke(operation);
    }

    private static (int, string, string) Key(int businessId, ActorIdentity identity) =>
        (businessId, identity.DirectoryTenantId, identity.ObjectId);

    /// <summary>One stored invite, as this fake holds it.</summary>
    public sealed record Invite(
        int InviteId,
        int BusinessId,
        BusinessRole Role,
        MemberInviteStatus StoredStatus,
        DateTime ExpiresAtUtc,
        BusinessStatus BusinessStatus,
        int? UsedByMembershipId,
        DateTime? UsedAtUtc);

    /// <summary>One stored membership, as this fake holds it.</summary>
    public sealed record Membership(
        int MembershipId,
        bool IsActive,
        BusinessRole Role,
        string? DisplayName,
        string? Email,
        DateTime? StatusChangedAtUtc);
}
