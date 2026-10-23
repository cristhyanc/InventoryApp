using System.Collections.Concurrent;
using Inventory.Application.Nayax;
using Inventory.Application.Time;

namespace Inventory.Infrastructure.Nayax;

/// <summary>
/// The business and credential revision one cached machine list belongs to (issue #840).
///
/// <para>The business id is in the key because a cache that held one list for the whole process
/// would be the one place in this application where one business could be served another's data.
/// The credential revision is in the key because replacing a connection may point the same business
/// at a different Nayax operator account: a saved connection therefore produces a new key, and the
/// next read goes to Nayax rather than answering from a list the previous credentials fetched.</para>
/// </summary>
public readonly record struct NayaxMachineListCacheKey(int BusinessId, int CredentialRevision);

/// <summary>
/// The in-memory machine lists <c>GET /v1/machines</c> answered with, one per business and
/// credential revision, kept for <see cref="Lifetime"/> (issue #840, step 1 of #839).
///
/// <para><strong>Why it exists.</strong> Twelve use cases read the machine list and nothing cached
/// it, so one Dashboard visit spent five requests on it - several of them simultaneously - against
/// somebody else's API. Machine ids, names and site <c>CustomerID</c>s change rarely, so those
/// repeats bought nothing.</para>
///
/// <para><strong>What it deliberately is not.</strong> It is not an authorization boundary and makes
/// no access decision at all: <c>MachineListCachingNayaxLynxClient</c> resolves the current
/// business's connection, status gate and support-session refusal through
/// <see cref="INayaxRequestCredentialProvider"/> before it ever reaches this class, so a business
/// whose connection is now disconnected or needs attention, and a support session, are refused
/// exactly as they are today even while a list for that key is held here. This class only ever sees
/// a key somebody has already been allowed to read.</para>
///
/// <para><strong>Memory, not a record.</strong> It is in-process, like
/// <c>NayaxSalesSyncRateLimiter</c> and <c>MemberProfileRefreshThrottle</c>: which machines Nayax
/// reported ten minutes ago is not part of the business's record, and a restart simply reads the
/// list again. On a single instance (docs/architecture.md § SQLite operating assumptions) that is
/// the whole truth; a second instance would mean one cached list per instance, which is still
/// bounded and still correct.</para>
///
/// <para><strong>Ordering is explicit, because results arrive out of order.</strong> Every read
/// takes a sequence number before it starts, and a result is stored only while no later read has
/// stored one, no fresh read has begun and no invalidation has happened since. That is what stops a
/// slow read that started before a fresh read from overwriting the fresher list it finished after -
/// and it holds whichever of the two finishes first, and whether the fresh read succeeds, fails or is
/// cancelled.</para>
/// </summary>
public sealed class NayaxMachineListCache
{
    /// <summary>
    /// How long one answer is reused. Decided by the repository owner on 23 October 2026: a machine
    /// added in Nayax can take up to ten minutes to appear on the pages that read through this
    /// cache, while provider machine review and the latest-sales sync - which read fresh - see it at
    /// once.
    /// </summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How many keys may be remembered before the ones holding nothing usable are cleared out. It is
    /// a hint about memory, never a cap: crossing it forgets keys whose list has expired and that
    /// have no read in flight, and it never makes a live key answer differently.
    /// </summary>
    public const int MaxTrackedKeys = 256;

    private readonly IClock _clock;
    private readonly ConcurrentDictionary<NayaxMachineListCacheKey, Entry> _entries = new();

    public NayaxMachineListCache(IClock clock) => _clock = clock;

    /// <summary>How many keys are currently remembered.</summary>
    public int TrackedKeys => _entries.Count;

    /// <summary>
    /// The list held for <paramref name="key"/>, or the result of one <paramref name="read"/> of
    /// Nayax shared by every caller that asked while it was running.
    ///
    /// <para>The single-flight is what makes a Dashboard load cost one request rather than five: the
    /// calls start at the same moment, so a cache that only stored finished answers would let all of
    /// them miss. The read runs under the cancellation token of the caller that started it and every
    /// caller observes its outcome, so a failure reaches all of them and nothing is stored; a caller
    /// that joined may still leave early on its own token.</para>
    /// </summary>
    public Task<List<NayaxMachine>> GetOrReadAsync(
        NayaxMachineListCacheKey key,
        Func<CancellationToken, Task<List<NayaxMachine>>> read,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(read);

        var entry = _entries.GetOrAdd(key, _ => new Entry());
        Task<List<NayaxMachine>> shared;

        lock (entry.Gate)
        {
            if (entry.Machines is { } held)
            {
                if (_clock.UtcNow - entry.StoredAtUtc < Lifetime)
                {
                    return Task.FromResult(Copy(held));
                }

                // Expired. Dropped here, under the same lock that would otherwise hand it out, so a
                // read that started before this moment can never store its result over the read that
                // is about to replace it.
                InvalidateLocked(entry);
            }

            if (entry.InFlight is null)
            {
                var sequence = ++entry.NextSequence;
                entry.InFlightSequence = sequence;
                entry.InFlight = ReadAndStoreAsync(entry, sequence, read, cancellationToken);

                // Observes a failure even if every caller has left on its own token by the time it
                // arrives, so an abandoned shared read never surfaces as an unobserved task
                // exception. It stores nothing and decides nothing.
                _ = entry.InFlight.ContinueWith(
                    static task => _ = task.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }

            shared = entry.InFlight;
        }

        return AwaitSharedAsync(shared, cancellationToken);
    }

    /// <summary>
    /// Reads Nayax now - never the held list, never a read already in flight - and replaces what is
    /// held for <paramref name="key"/> with the result.
    ///
    /// <para>A failed read stores nothing and leaves the held list alone: the exception is the
    /// caller's answer, and an entry that was valid a moment ago is not evidence of anything
    /// new.</para>
    ///
    /// <para>A read that was already in flight stays joinable, unlike after an
    /// <see cref="Invalidate"/>. It can no longer become what the next caller is <em>held</em> an
    /// answer from - the watermark below bars it - but it is a live read of this same key that
    /// started seconds ago, and refusing to share it would make a cached page load that overlaps a
    /// fresh caller cost the extra Nayax request this cache exists to remove. An invalidation is the
    /// opposite case: there, what that read is about to answer with is exactly what must not be
    /// handed out.</para>
    /// </summary>
    public async Task<List<NayaxMachine>> ReadFreshAsync(
        NayaxMachineListCacheKey key,
        Func<CancellationToken, Task<List<NayaxMachine>>> read,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(read);

        var entry = _entries.GetOrAdd(key, _ => new Entry());
        long sequence;
        lock (entry.Gate)
        {
            sequence = ++entry.NextSequence;

            // Every read that started before this one is barred from storing from the moment this
            // fresh read *begins*, not from the moment it finishes. That is what issue #840 asks for -
            // an older in-flight result is not stored once a fresh read has happened for the key - and
            // it has to hold however the two end: a fresh read that fails or is cancelled must not
            // leave an older read's list as the answer simply because it had nothing to store itself.
            //
            // What is held is deliberately left alone: a failed fresh read changes nothing, and the
            // list from before it is still the most recent answer anybody got.
            entry.StoredSequence = Math.Max(entry.StoredSequence, sequence - 1);
        }

        var machines = await read(cancellationToken).ConfigureAwait(false);
        Store(entry, sequence, machines);
        Prune();
        return machines;
    }

    /// <summary>
    /// Forgets whatever is held for <paramref name="key"/>, bars every read that started before now
    /// from storing its result, and stops any such read that is still running from being joined by a
    /// caller that arrives afterwards.
    ///
    /// <para>All three matter together: a caller after an invalidation must not be handed the
    /// pre-invalidation list, whether it is sitting in the entry or about to arrive from a read that
    /// began before the invalidation. The callers that read already keep the answer they were
    /// given.</para>
    /// </summary>
    public void Invalidate(NayaxMachineListCacheKey key)
    {
        if (!_entries.TryGetValue(key, out var entry))
        {
            return;
        }

        lock (entry.Gate)
        {
            InvalidateLocked(entry);
        }
    }

    // Runs one shared read and stores its result if it is still the newest answer for the key.
    //
    // It yields first so the read itself never runs while the entry's lock is held: the caller
    // publishes this task into the entry under that lock, and nothing that talks to Nayax, to the
    // connection store or to a clock may happen inside it.
    private async Task<List<NayaxMachine>> ReadAndStoreAsync(
        Entry entry,
        long sequence,
        Func<CancellationToken, Task<List<NayaxMachine>>> read,
        CancellationToken cancellationToken)
    {
        await Task.Yield();

        try
        {
            var machines = await read(cancellationToken).ConfigureAwait(false);
            Store(entry, sequence, machines);
            Prune();
            return machines;
        }
        finally
        {
            // Whether it succeeded or failed, the next caller must be able to start a new read: a
            // failure is never remembered, as a cached failure would turn one bad moment into ten
            // minutes of refusals.
            lock (entry.Gate)
            {
                if (entry.InFlightSequence == sequence)
                {
                    entry.InFlight = null;
                }
            }
        }
    }

    private static async Task<List<NayaxMachine>> AwaitSharedAsync(
        Task<List<NayaxMachine>> shared,
        CancellationToken cancellationToken)
    {
        // Every caller gets its own list over the same machines, so one caller sorting or trimming
        // what it was handed cannot change what the next one sees.
        var machines = await shared.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new List<NayaxMachine>(machines);
    }

    private void Store(Entry entry, long sequence, List<NayaxMachine> machines)
    {
        lock (entry.Gate)
        {
            if (sequence <= entry.StoredSequence)
            {
                // A later read, or an invalidation, has already had the last word for this key. This
                // result is older news however recently it arrived, so it is answered with and
                // forgotten.
                return;
            }

            entry.StoredSequence = sequence;
            entry.Machines = machines.ToArray();
            entry.StoredAtUtc = _clock.UtcNow;
        }
    }

    private static void InvalidateLocked(Entry entry)
    {
        entry.Machines = null;
        entry.StoredSequence = entry.NextSequence;

        // The read still running is unpublished as well as barred. Barring it stops it from being
        // stored; unpublishing it stops a caller that arrives after this moment from joining it and
        // being answered with the very list the invalidation decided was not to be kept - which, for
        // the expiry path above, is a list that is already too old to hand out, and for a credential
        // change would be the previous connection's fleet. The callers it already has still receive
        // what they asked for, and the read that published it is the only one that may clear a
        // newer one (ReadAndStoreAsync compares InFlightSequence), so the next caller starts its own.
        entry.InFlight = null;
    }

    private static List<NayaxMachine> Copy(NayaxMachine[] machines) => new(machines);

    // Keeps the dictionary from growing without bound across businesses and credential revisions.
    // It only ever removes keys that hold nothing usable, so it neither serves a stale list nor
    // forces a fresh read on a key that was about to be answered from memory.
    private void Prune()
    {
        if (_entries.Count <= MaxTrackedKeys)
        {
            return;
        }

        var now = _clock.UtcNow;
        foreach (var pair in _entries)
        {
            var entry = pair.Value;
            lock (entry.Gate)
            {
                if (entry.InFlight is not null)
                {
                    continue;
                }

                if (entry.Machines is not null && now - entry.StoredAtUtc < Lifetime)
                {
                    continue;
                }

                // Barred as well as removed: a read still running against this entry must not store
                // its result into an object the dictionary no longer publishes and a later caller
                // would never see.
                InvalidateLocked(entry);
            }

            _entries.TryRemove(pair);
        }
    }

    private sealed class Entry
    {
        /// <summary>Guards every field below; never held across an await or a Nayax call.</summary>
        public object Gate { get; } = new();

        /// <summary>The sequence number of the last read to start for this key.</summary>
        public long NextSequence { get; set; }

        /// <summary>
        /// The highest sequence number that has had the last word - the read whose result is held,
        /// the moment of the last invalidation, or the read that started just before a fresh read
        /// began. A result may be stored only above it.
        /// </summary>
        public long StoredSequence { get; set; }

        /// <summary>The held machine list, or <c>null</c> when nothing is held.</summary>
        public NayaxMachine[]? Machines { get; set; }

        /// <summary>When <see cref="Machines"/> was stored.</summary>
        public DateTime StoredAtUtc { get; set; }

        /// <summary>The shared read every cached caller joins while it is running.</summary>
        public Task<List<NayaxMachine>>? InFlight { get; set; }

        /// <summary>
        /// The sequence number of the read <see cref="InFlight"/> belongs to, so the read that
        /// published it is the only one that clears it.
        /// </summary>
        public long InFlightSequence { get; set; }
    }
}
