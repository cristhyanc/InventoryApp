using System.Collections.Concurrent;
using Inventory.Application.Time;
using Inventory.Domain.Tenancy;

namespace Inventory.Application.SalesSync;

/// <summary>
/// "When did this business's sales last finish synchronizing, under the credentials it holds now?"
/// (issue #843, part of parent #839) - the one fact <see cref="SyncLatestNayaxSales"/> needs to
/// decide that opening the Dashboard again seconds later has nothing left to read.
///
/// <para><strong>Why a second bound beside the rate limiter.</strong>
/// <see cref="NayaxSalesSyncRateLimiter"/> answers a different question - "has this business started
/// too many runs?" - and ten runs per two minutes is deliberately invisible to people, so a visit
/// seconds after the last one still spends one Nayax request per machine plus one, persists, costs
/// and rebuilds inventory costs. This is the freshness answer instead: inside
/// <see cref="RecentWindow"/> the previous run's result is what the operator is looking at already,
/// so the run is skipped rather than rationed, and the allowance is not touched at all. The two
/// compose - a forced Refresh is still rationed - and neither replaces the other.</para>
///
/// <para><strong>The key is the business <em>and</em> its credential revision.</strong> A sync is
/// only "already done" for the credentials it was done with: saving or replacing the business's
/// Nayax connection is exactly the moment an operator needs to see whether sales now arrive, and
/// the new revision is a key nothing has recorded, so the next visit runs in full. Keying by
/// business alone would have made a credential change wait out the window.</para>
///
/// <para><strong>Only a fully successful run is recorded</strong>, by the use case, after everything
/// it does has succeeded - the same boundary issue #730 gives the coverage record. A run that
/// failed, was cancelled or was refused by the limiter records nothing, because a skip claims that
/// the sales in front of the operator are current, and a run that did not finish is no evidence of
/// that.</para>
///
/// <para>It is kept in memory rather than in a column, like both rate limiters and
/// <c>MemberProfileRefreshThrottle</c>: when a Dashboard was last opened is not part of the
/// business's record, and after a restart the first visit simply syncs. On a single instance
/// (docs/architecture.md § SQLite operating assumptions) in-process state is the whole truth; a
/// second instance would mean one extra full run per instance per window, which is bounded.</para>
/// </summary>
public sealed class NayaxSalesSyncFreshness
{
    /// <summary>
    /// How recently a fully successful synchronization must have finished for the next unforced one
    /// to be skipped: three minutes, decided by the repository owner on 23 October 2026.
    /// </summary>
    public static readonly TimeSpan RecentWindow = TimeSpan.FromMinutes(3);

    /// <summary>
    /// How many business/credential-revision keys may be remembered before the ones whose window has
    /// fully passed are cleared out. It is a hint about memory, never a cap: a key is dropped only
    /// once it can no longer make anything skip, so clearing out neither grants nor denies a skip -
    /// the rule issue #606 established for the rate limiters' own tracking.
    /// </summary>
    public const int MaxTrackedKeys = 256;

    private readonly ConcurrentDictionary<SyncKey, DateTime> _lastSyncedAtUtcByKey = new();
    private readonly IClock _clock;

    public NayaxSalesSyncFreshness(IClock clock)
    {
        _clock = clock;
    }

    /// <summary>How many business/credential-revision keys are currently remembered.</summary>
    public int TrackedKeys => _lastSyncedAtUtcByKey.Count;

    /// <summary>
    /// When this business last finished a fully successful synchronization under
    /// <paramref name="credentialRevision"/>, if that was less than <see cref="RecentWindow"/> ago;
    /// otherwise <see langword="null"/>, which means "run in full".
    ///
    /// <para>A key nothing has recorded - a business whose first visit this is since startup, or one
    /// whose credentials have since been replaced - is <see langword="null"/>, so the absence of
    /// knowledge always means run. So does a recorded instant that is somehow in the future, which a
    /// clock moved backwards can produce: an unexplained instant must not be read as evidence that
    /// the sales are current.</para>
    /// </summary>
    public DateTime? FindRecentSync(BusinessId business, int credentialRevision)
    {
        if (!_lastSyncedAtUtcByKey.TryGetValue(new SyncKey(business, credentialRevision), out var lastSyncedAtUtc))
        {
            return null;
        }

        var elapsed = _clock.UtcNow - lastSyncedAtUtc;

        return elapsed >= TimeSpan.Zero && elapsed < RecentWindow ? lastSyncedAtUtc : null;
    }

    /// <summary>
    /// Records that a synchronization for this business and credential revision has just finished
    /// successfully. Called only by <see cref="SyncLatestNayaxSales"/>, and only once every step of
    /// the run has succeeded.
    /// </summary>
    /// <returns>The instant recorded, which is the instant the run finished.</returns>
    public DateTime RecordSuccessfulSync(BusinessId business, int credentialRevision)
    {
        var finishedAtUtc = _clock.UtcNow;
        _lastSyncedAtUtcByKey[new SyncKey(business, credentialRevision)] = finishedAtUtc;
        PrunePassed(finishedAtUtc);

        return finishedAtUtc;
    }

    /// <summary>
    /// Forgets the keys whose window has fully passed, once more than
    /// <see cref="MaxTrackedKeys"/> are remembered. Such a key already answers "run in full", so
    /// dropping it changes no answer - which is what makes clearing out safe to do opportunistically
    /// here rather than on a timer.
    /// </summary>
    private void PrunePassed(DateTime now)
    {
        if (_lastSyncedAtUtcByKey.Count <= MaxTrackedKeys)
        {
            return;
        }

        foreach (var entry in _lastSyncedAtUtcByKey)
        {
            if (now - entry.Value >= RecentWindow)
            {
                // Removed by key and value together, so a run that recorded a newer instant between
                // the enumeration and here keeps it.
                _lastSyncedAtUtcByKey.TryRemove(entry);
            }
        }
    }

    /// <summary>
    /// What a remembered synchronization belongs to: one business, under one revision of its stored
    /// Nayax credentials. Never a business alone, and never anything a request supplied.
    /// </summary>
    private readonly record struct SyncKey(BusinessId Business, int CredentialRevision);
}
