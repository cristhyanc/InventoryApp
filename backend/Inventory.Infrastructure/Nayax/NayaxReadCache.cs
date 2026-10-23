using System.Collections.Concurrent;
using Inventory.Application.Nayax;
using Inventory.Application.Time;

namespace Inventory.Infrastructure.Nayax;

/// <summary>
/// The in-memory, single-flight, explicitly ordered cache one Nayax list read is held in, for
/// <paramref name="lifetime"/> at a time, under a key its subclass decides (issues #840 and #842,
/// steps 1 and 2 of #839).
///
/// <para><strong>Why it is one mechanism and not one per read.</strong> The machine list (#840) and
/// each machine's products (#842) are held under different keys for different lengths of time, but
/// the hard part is identical and is all about correctness rather than about which endpoint was
/// read: one request for simultaneous callers, a failure held by nobody, an expiry taken under the
/// lock that would otherwise hand the entry out, and a strict ordering between an older read, a
/// newer fresh read and an invalidation. Writing that twice would mean maintaining two copies of the
/// reasoning below, and a divergence between them would be a stale stock figure rather than a
/// compile error. <see cref="NayaxMachineListCache"/> and <see cref="NayaxMachineProductsCache"/>
/// are what the composition root registers and what states each read's own key and lifetime.</para>
///
/// <para><strong>What it deliberately is not.</strong> It is not an authorization boundary and makes
/// no access decision at all: <see cref="CachingNayaxLynxClient"/> resolves the current business's
/// connection, status gate and support-session refusal through
/// <see cref="INayaxRequestCredentialProvider"/> before it ever reaches this class, so a business
/// whose connection is now disconnected or needs attention, and a support session, are refused
/// exactly as they are today even while an entry for that key is held here. This class only ever
/// sees a key somebody has already been allowed to read.</para>
///
/// <para><strong>Memory, not a record.</strong> It is in-process, like
/// <c>NayaxSalesSyncRateLimiter</c> and <c>MemberProfileRefreshThrottle</c>: what Nayax reported
/// minutes ago is not part of the business's record, and a restart simply reads it again. On a
/// single instance (docs/architecture.md § SQLite operating assumptions) that is the whole truth; a
/// second instance would mean one cached entry per instance, which is still bounded and still
/// correct.</para>
///
/// <para><strong>Ordering is explicit, because results arrive out of order.</strong> Every read
/// takes a sequence number before it starts, and a result is stored only while no later read has
/// stored one, no fresh read has begun and no invalidation has happened since. That is what stops a
/// slow read that started before a fresh read from overwriting the fresher result it finished after -
/// and it holds whichever of the two finishes first, and whether the fresh read succeeds, fails or is
/// cancelled.</para>
/// </summary>
/// <param name="clock">The application clock the lifetime is measured on.</param>
/// <param name="lifetime">How long one answer is reused.</param>
/// <param name="maxTrackedKeys">
/// How many keys may be remembered before the ones holding nothing usable are cleared out. It is a
/// hint about memory, never a cap: crossing it forgets keys whose entry has expired and that have no
/// read in flight, and it never makes a live key answer differently.
/// </param>
public abstract class NayaxReadCache<TKey, TValue>(IClock clock, TimeSpan lifetime, int maxTrackedKeys)
    where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, Entry> _entries = new();

    /// <summary>How many keys are currently remembered.</summary>
    public int TrackedKeys => _entries.Count;

    /// <summary>
    /// The list held for <paramref name="key"/>, or the result of one <paramref name="read"/> of
    /// Nayax shared by every caller that asked while it was running.
    ///
    /// <para>The single-flight is what makes a Dashboard load cost one request per key rather than
    /// three: the calls start at the same moment, so a cache that only stored finished answers would
    /// let all of them miss. The read runs under the cancellation token of the caller that started it
    /// and every caller observes its outcome, so a failure reaches all of them and nothing is stored;
    /// a caller that joined may still leave early on its own token.</para>
    /// </summary>
    public Task<List<TValue>> GetOrReadAsync(
        TKey key,
        Func<CancellationToken, Task<List<TValue>>> read,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(read);

        var entry = _entries.GetOrAdd(key, _ => new Entry());
        Task<List<TValue>> shared;

        lock (entry.Gate)
        {
            if (entry.Items is { } held)
            {
                if (clock.UtcNow - entry.StoredAtUtc < lifetime)
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
    /// Reads Nayax now - never the held entry, never a read already in flight - and replaces what is
    /// held for <paramref name="key"/> with the result.
    ///
    /// <para>A failed read stores nothing and leaves the held entry alone: the exception is the
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
    public async Task<List<TValue>> ReadFreshAsync(
        TKey key,
        Func<CancellationToken, Task<List<TValue>>> read,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(read);

        var entry = _entries.GetOrAdd(key, _ => new Entry());
        long sequence;
        lock (entry.Gate)
        {
            sequence = ++entry.NextSequence;

            // Every read that started before this one is barred from storing from the moment this
            // fresh read *begins*, not from the moment it finishes. That is what issues #840 and #842
            // ask for - an older in-flight result is not stored once a fresh read has happened for the
            // key - and it has to hold however the two end: a fresh read that fails or is cancelled
            // must not leave an older read's result as the answer simply because it had nothing to
            // store itself.
            //
            // What is held is deliberately left alone: a failed fresh read changes nothing, and the
            // entry from before it is still the most recent answer anybody got.
            entry.StoredSequence = Math.Max(entry.StoredSequence, sequence - 1);
        }

        var items = await read(cancellationToken).ConfigureAwait(false);
        Store(entry, sequence, items);
        Prune();
        return items;
    }

    /// <summary>
    /// Forgets whatever is held for <paramref name="key"/>, bars every read that started before now
    /// from storing its result, and stops any such read that is still running from being joined by a
    /// caller that arrives afterwards.
    ///
    /// <para>All three matter together: a caller after an invalidation must not be handed the
    /// pre-invalidation entry, whether it is sitting in the entry or about to arrive from a read that
    /// began before the invalidation. The callers that read already keep the answer they were
    /// given.</para>
    /// </summary>
    public void Invalidate(TKey key)
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
    private async Task<List<TValue>> ReadAndStoreAsync(
        Entry entry,
        long sequence,
        Func<CancellationToken, Task<List<TValue>>> read,
        CancellationToken cancellationToken)
    {
        await Task.Yield();

        try
        {
            var items = await read(cancellationToken).ConfigureAwait(false);
            Store(entry, sequence, items);
            Prune();
            return items;
        }
        finally
        {
            // Whether it succeeded or failed, the next caller must be able to start a new read: a
            // failure is never remembered, as a cached failure would turn one bad moment into a whole
            // lifetime of refusals.
            lock (entry.Gate)
            {
                if (entry.InFlightSequence == sequence)
                {
                    entry.InFlight = null;
                }
            }
        }
    }

    private static async Task<List<TValue>> AwaitSharedAsync(
        Task<List<TValue>> shared,
        CancellationToken cancellationToken)
    {
        // Every caller gets its own list over the same items, so one caller sorting or trimming what
        // it was handed cannot change what the next one sees.
        var items = await shared.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new List<TValue>(items);
    }

    private void Store(Entry entry, long sequence, List<TValue> items)
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
            entry.Items = items.ToArray();
            entry.StoredAtUtc = clock.UtcNow;
        }
    }

    private static void InvalidateLocked(Entry entry)
    {
        entry.Items = null;
        entry.StoredSequence = entry.NextSequence;

        // The read still running is unpublished as well as barred. Barring it stops it from being
        // stored; unpublishing it stops a caller that arrives after this moment from joining it and
        // being answered with the very result the invalidation decided was not to be kept - which,
        // for the expiry path above, is an entry that is already too old to hand out, for a
        // credential change would be the previous connection's data, and for a machine-product write
        // is the selection list that write has just replaced. The callers it already has still
        // receive what they asked for, and the read that published it is the only one that may clear
        // a newer one (ReadAndStoreAsync compares InFlightSequence), so the next caller starts its
        // own.
        entry.InFlight = null;
    }

    private static List<TValue> Copy(TValue[] items) => new(items);

    // Keeps the dictionary from growing without bound across businesses, credential revisions and
    // machines. It only ever removes keys that hold nothing usable, so it neither serves a stale
    // entry nor forces a fresh read on a key that was about to be answered from memory.
    private void Prune()
    {
        if (_entries.Count <= maxTrackedKeys)
        {
            return;
        }

        var now = clock.UtcNow;
        foreach (var pair in _entries)
        {
            var entry = pair.Value;
            lock (entry.Gate)
            {
                if (entry.InFlight is not null)
                {
                    continue;
                }

                if (entry.Items is not null && now - entry.StoredAtUtc < lifetime)
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

        /// <summary>The held items, or <c>null</c> when nothing is held.</summary>
        public TValue[]? Items { get; set; }

        /// <summary>When <see cref="Items"/> was stored.</summary>
        public DateTime StoredAtUtc { get; set; }

        /// <summary>The shared read every cached caller joins while it is running.</summary>
        public Task<List<TValue>>? InFlight { get; set; }

        /// <summary>
        /// The sequence number of the read <see cref="InFlight"/> belongs to, so the read that
        /// published it is the only one that clears it.
        /// </summary>
        public long InFlightSequence { get; set; }
    }
}
