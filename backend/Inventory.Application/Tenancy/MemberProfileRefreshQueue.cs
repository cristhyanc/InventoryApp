using System.Threading.Channels;
using Inventory.Domain.Tenancy;

namespace Inventory.Application.Tenancy;

/// <summary>One pending refresh: whose membership row, and what to put in its two display columns.</summary>
public sealed record MemberProfileRefreshRequest(ActorIdentity Identity, MemberProfile Profile);

/// <summary>
/// The hand-off that keeps the member-profile refresh off the request (issue #524). The request
/// thread writes to it after the response is decided and returns; the write itself happens later,
/// in its own service scope, outside the request's business scope.
///
/// Three properties are the reason it is a bounded channel rather than a detached task per request:
///
/// <list type="bullet">
///   <item><b>It cannot delay a request.</b> <see cref="TryEnqueue"/> is synchronous, lock-free and
///   returns immediately.</item>
///   <item><b>It cannot fail a request.</b> It never throws - not when the queue is full, and not
///   after it has been completed at shutdown; it answers <c>false</c> instead. A refresh that is
///   dropped costs nothing but freshness: the next request an hour later schedules it again.</item>
///   <item><b>It cannot flood the writer.</b> The production store is a single SQLite file with one
///   writer (docs/architecture.md § SQLite operating assumptions), so the refreshes are drained one
///   at a time by a single reader instead of racing interactive requests with a task per sign-in.
///   Under a burst the capacity below is reached and the newest refreshes are dropped rather than
///   queued without limit.</item>
/// </list>
/// </summary>
public sealed class MemberProfileRefreshQueue
{
    /// <summary>
    /// How many refreshes may wait at once. It is small on purpose: one entry per member per hour
    /// is the steady state, so a backlog this deep already means the drain is not keeping up, and in
    /// that situation dropping a cosmetic write is the correct outcome rather than holding memory
    /// for it.
    /// </summary>
    public const int Capacity = 64;

    private readonly Channel<MemberProfileRefreshRequest> _channel =
        Channel.CreateBounded<MemberProfileRefreshRequest>(
            new BoundedChannelOptions(Capacity)
            {
                // Nothing ever waits: the only write this queue offers is the synchronous
                // TryEnqueue below, which answers false when the queue is full instead of
                // blocking. This mode rather than DropWrite because DropWrite accepts the write
                // and silently discards it, which would hide the drop from the caller and from a
                // test - and "the newest refresh was dropped" is a fact worth being able to see.
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
            });

    /// <summary>
    /// How many refreshes are queued and not yet read. It is the drain's backlog, bounded by
    /// <see cref="Capacity"/>.
    /// </summary>
    public int Pending => _channel.Reader.Count;

    /// <summary>
    /// Queues one refresh, or drops it. Never blocks and never throws.
    /// </summary>
    /// <returns><c>true</c> when the refresh was queued, <c>false</c> when it was dropped.</returns>
    public bool TryEnqueue(MemberProfileRefreshRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return _channel.Writer.TryWrite(request);
    }

    /// <summary>
    /// The queued refreshes, in order, for the one reader that drains them. The sequence ends once
    /// <see cref="Complete"/> has been called and everything already queued has been read.
    /// </summary>
    public IAsyncEnumerable<MemberProfileRefreshRequest> ReadAllAsync(
        CancellationToken cancellationToken = default) =>
        _channel.Reader.ReadAllAsync(cancellationToken);

    /// <summary>
    /// Accepts no further refreshes and lets the reader finish the ones already queued. Called when
    /// the host is shutting down; calling it twice is harmless.
    /// </summary>
    public void Complete() => _channel.Writer.TryComplete();
}
