using System.Collections.Concurrent;
using Inventory.Domain.Tenancy;

namespace Inventory.Application.SalesSync;

/// <summary>
/// One business's claim on its own latest-sales synchronization, held for as long as the run lasts
/// and released by the caller that took it - and by nobody else (issue #1104).
///
/// <para>A lease that was <em>not</em> acquired is a real object with
/// <see cref="Acquired"/> <see langword="false"/>, so a caller can write the ordinary
/// <c>using</c> and get the right behaviour in both branches. Disposing it releases nothing, which is
/// the whole point: a waiter that timed out must never hand back a lock the holder is still working
/// under, and a <c>finally</c> that released unconditionally would do exactly that - letting a second
/// synchronization start beside the first and both insert the same transaction.</para>
/// </summary>
public sealed class NayaxSalesSyncLease : IDisposable
{
    /// <summary>The answer a waiter that did not get the lock receives. Disposing it does nothing.</summary>
    public static NayaxSalesSyncLease NotAcquired { get; } = new(null);

    private readonly SemaphoreSlim? _gate;
    private bool _released;

    internal NayaxSalesSyncLease(SemaphoreSlim? gate)
    {
        _gate = gate;
    }

    /// <summary>
    /// Whether this caller holds the business's synchronization lock. It is the explicit flag the
    /// release is conditional on, rather than something inferred from how the call ended.
    /// </summary>
    public bool Acquired => _gate is not null;

    /// <summary>
    /// Releases the lock, once, and only when this lease actually holds it.
    ///
    /// <para>The <see cref="_released"/> guard is not defensive tidiness: a <c>SemaphoreSlim</c>
    /// released twice raises its count above its maximum and would let two runs in at once, which is
    /// a far worse failure than a lock held slightly too long.</para>
    /// </summary>
    public void Dispose()
    {
        if (_gate is null || _released)
        {
            return;
        }

        _released = true;
        _gate.Release();
    }
}

/// <summary>
/// "Only one latest-sales synchronization per business at a time" (issue #1104, child of #1100) -
/// the one lock the hourly reconciliation and the request-driven Dashboard sync both take.
///
/// <para><strong>Why the two paths have to share one lock.</strong> Each run reads the machine list
/// and then every machine's <c>lastSales</c>, and then saves. Two runs for the same business at once
/// read the same rolling window, both decide "this transaction is not stored" and both try to insert
/// it; the write-locked save of issue #1103 turns that into the enrich path rather than a unique-index
/// error, but they still spend the business's Nayax allowance twice, replay the same products twice,
/// and - since this issue - would record two pieces of evidence about one observation. The lock is
/// what makes a run a run.</para>
///
/// <para><strong>The two callers wait differently, deliberately.</strong> A person pressing Refresh
/// waits up to <see cref="RequestWait"/>, because an hourly run that started a moment earlier will
/// normally finish well inside that and the operator then gets the fresh data they asked for. The
/// hourly run itself uses <see cref="TryAcquireAsync"/> and skips the business for this hour: it has
/// no one waiting on it, and the next hour is a better answer than a queue of background runs behind
/// one slow sync.</para>
///
/// <para><strong>It is in-process, like every other bound on this endpoint.</strong>
/// <see cref="NayaxSalesSyncRateLimiter"/> and <see cref="NayaxSalesSyncFreshness"/> are memory too,
/// and for the same reason: the production store is one SQLite file served by one instance
/// (docs/architecture.md § SQLite operating assumptions), so in-process state is the whole truth. A
/// second instance would mean one concurrent run per instance, which the write-locked save still
/// handles correctly - it would cost the upstream, not the data.</para>
///
/// <para><strong>Nothing is ever removed from the table of gates.</strong> That is not an oversight:
/// forgetting a gate is the one way two callers could end up holding <em>different</em> semaphores
/// for the same business, which is precisely the race this class exists to prevent. A gate is a few
/// dozen bytes and there is at most one per business this process has ever synchronized, so the
/// memory is bounded by the number of businesses rather than by traffic - unlike the limiters, whose
/// entries are values and are therefore safe to prune.</para>
/// </summary>
public sealed class NayaxSalesSyncLock
{
    /// <summary>
    /// How long a request-driven synchronization waits for a run already in progress: sixty seconds,
    /// the repository owner's value of 5 November 2026.
    ///
    /// <para>Long enough that an ordinary run finishes inside it and the caller gets the data, short
    /// enough that a request never hangs: past it the use case answers
    /// <see cref="NayaxSalesSyncOutcome.Busy"/> and the endpoint answers <c>409</c>.</para>
    /// </summary>
    public static readonly TimeSpan DefaultRequestWait = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<BusinessId, SemaphoreSlim> _gatesByBusiness = new();

    /// <summary>The composition root's lock, waiting <see cref="DefaultRequestWait"/>.</summary>
    public NayaxSalesSyncLock()
        : this(DefaultRequestWait)
    {
    }

    /// <summary>
    /// A lock with a stated request wait. It is a constructor parameter so a test can assert "the
    /// waiter gave up and released nothing" without waiting a real minute for it; the production
    /// value is <see cref="DefaultRequestWait"/> and is stated in one place.
    /// </summary>
    public NayaxSalesSyncLock(TimeSpan requestWait)
    {
        RequestWait = requestWait;
    }

    /// <summary>How long <see cref="AcquireForRequestAsync"/> waits.</summary>
    public TimeSpan RequestWait { get; }

    /// <summary>How many businesses currently have a gate. Operational evidence, never a bound.</summary>
    public int TrackedBusinesses => _gatesByBusiness.Count;

    /// <summary>
    /// Waits up to <paramref name="wait"/> for <paramref name="business"/>'s synchronization lock.
    ///
    /// <para>A cancellation while waiting raises <see cref="OperationCanceledException"/> and acquires
    /// nothing, so there is nothing for the caller to release - which is the other half of the
    /// "released only by the holder" rule.</para>
    /// </summary>
    public async Task<NayaxSalesSyncLease> AcquireAsync(
        BusinessId business, TimeSpan wait, CancellationToken cancellationToken)
    {
        var gate = _gatesByBusiness.GetOrAdd(business, _ => new SemaphoreSlim(1, 1));

        return await gate.WaitAsync(wait, cancellationToken).ConfigureAwait(false)
            ? new NayaxSalesSyncLease(gate)
            : NayaxSalesSyncLease.NotAcquired;
    }

    /// <summary>
    /// Takes the lock only if it is free right now - the hourly reconciliation's acquisition. A
    /// business whose sync is already running is skipped for this hour, with no Nayax call and no
    /// write.
    /// </summary>
    public Task<NayaxSalesSyncLease> TryAcquireAsync(
        BusinessId business, CancellationToken cancellationToken) =>
        AcquireAsync(business, TimeSpan.Zero, cancellationToken);

    /// <summary>
    /// Waits <see cref="RequestWait"/> for the lock - the request-driven synchronization's
    /// acquisition. A lease that was not acquired becomes
    /// <see cref="NayaxSalesSyncOutcome.Busy"/> and then <c>409 Conflict</c>.
    /// </summary>
    public Task<NayaxSalesSyncLease> AcquireForRequestAsync(
        BusinessId business, CancellationToken cancellationToken) =>
        AcquireAsync(business, RequestWait, cancellationToken);
}
