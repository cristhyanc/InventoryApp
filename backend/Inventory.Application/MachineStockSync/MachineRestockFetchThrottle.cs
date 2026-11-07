using Inventory.Application.Time;
using Inventory.Domain.Tenancy;

namespace Inventory.Application.MachineStockSync;

/// <summary>
/// One business's claim on the Dashboard's hourly restock-event fetch (issue #1122): who claimed it,
/// under which credential revision, and when. <see cref="MachineRestockFetchThrottle.Release"/>
/// gives back exactly this claim and nothing newer.
/// </summary>
public readonly record struct MachineRestockFetchClaim(
    BusinessId Business, int CredentialRevision, DateTime ClaimedAtUtc);

/// <summary>
/// "Has this business's machine restock events been fetched from Nayax in the last hour, under the
/// credentials it holds now?" (issue #1122) - the bound that lets the Dashboard fetch on every visit
/// while spending at most one round of Nayax restock calls per business per hour, by the owner's
/// decision of 7 November 2026.
///
/// <para><strong>The claim is taken before the first Nayax call, not recorded after the last
/// one.</strong> A Dashboard opened while a fetch is still running finds the business already
/// claimed and starts no second fetch; it is shown the reminder as it stands. A fetch that ran - to
/// the end, or until Nayax refused it - keeps its claim for the hour either way, so a business whose
/// Nayax account is refusing is not asked again on every visit. Only a fetch cancelled before it
/// finished (the visitor left) gives its claim back, so the next visit fetches.</para>
///
/// <para><strong>Keyed by business and credential revision</strong>, exactly as
/// <c>NayaxSalesSyncFreshness</c> is: saving new credentials increments the revision, so the first
/// visit under the new ones fetches at once rather than waiting out an hour earned by the old.</para>
///
/// <para><strong>In memory, per API instance.</strong> Nothing is persisted, so a restart forgets
/// the claims and allows one extra fetch. That is the accepted trade-off for needing no schema
/// change: the fetch itself is idempotent (already-imported alerts are skipped by their Nayax event
/// id), so an extra one costs Nayax calls and nothing else.</para>
/// </summary>
public sealed class MachineRestockFetchThrottle
{
    /// <summary>How long a claim lasts: one fetch per business per hour.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromHours(1);

    /// <summary>Above this many tracked keys, expired claims are pruned on the next claim.</summary>
    public const int MaxTrackedKeys = 256;

    private readonly object _gate = new();
    private readonly Dictionary<(BusinessId Business, int CredentialRevision), DateTime> _claimedAtUtc = new();
    private readonly IClock _clock;

    public MachineRestockFetchThrottle(IClock clock)
    {
        _clock = clock;
    }

    /// <summary>The number of keys currently held, for tests of the pruning bound.</summary>
    public int TrackedKeys
    {
        get
        {
            lock (_gate)
            {
                return _claimedAtUtc.Count;
            }
        }
    }

    /// <summary>
    /// Claims the hour for <paramref name="business"/> under <paramref name="credentialRevision"/>,
    /// or returns <see langword="false"/> when a claim taken less than <see cref="Window"/> ago -
    /// including one whose fetch is still running - already holds it. The check and the claim are
    /// one step, so two concurrent visits cannot both claim.
    /// </summary>
    public bool TryClaim(BusinessId business, int credentialRevision, out MachineRestockFetchClaim claim)
    {
        var now = _clock.UtcNow;
        var key = (business, credentialRevision);

        lock (_gate)
        {
            if (_claimedAtUtc.TryGetValue(key, out var claimedAtUtc)
                && now - claimedAtUtc >= TimeSpan.Zero
                && now - claimedAtUtc < Window)
            {
                claim = default;
                return false;
            }

            PruneExpired(now);
            _claimedAtUtc[key] = now;
            claim = new MachineRestockFetchClaim(business, credentialRevision, now);
            return true;
        }
    }

    /// <summary>
    /// Gives back <paramref name="claim"/>, so the next visit may fetch at once. Only that claim is
    /// removed: a newer one taken under the same key is left in place.
    /// </summary>
    public void Release(MachineRestockFetchClaim claim)
    {
        var key = (claim.Business, claim.CredentialRevision);

        lock (_gate)
        {
            if (_claimedAtUtc.TryGetValue(key, out var claimedAtUtc) && claimedAtUtc == claim.ClaimedAtUtc)
            {
                _claimedAtUtc.Remove(key);
            }
        }
    }

    private void PruneExpired(DateTime now)
    {
        if (_claimedAtUtc.Count < MaxTrackedKeys)
        {
            return;
        }

        foreach (var expired in _claimedAtUtc.Where(entry => now - entry.Value >= Window).Select(entry => entry.Key).ToList())
        {
            _claimedAtUtc.Remove(expired);
        }
    }
}
