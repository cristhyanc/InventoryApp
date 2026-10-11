using Inventory.Domain.Tenancy;

namespace Inventory.Application.Tenancy;

/// <summary>
/// Decides whether this request's token should refresh the member's stored display name and email
/// address, and hands the write to the queue if so (issue #524).
///
/// Everything it does is synchronous and in memory, because it runs on the request thread after the
/// response has been decided: the decision is "is there anything to store, and is this membership's
/// hourly slot free", and the write itself happens in the drain.
/// </summary>
public sealed class ScheduleMemberProfileRefresh
{
    private readonly MemberProfileRefreshThrottle _throttle;
    private readonly MemberProfileRefreshQueue _queue;

    public ScheduleMemberProfileRefresh(
        MemberProfileRefreshThrottle throttle,
        MemberProfileRefreshQueue queue)
    {
        _throttle = throttle;
        _queue = queue;
    }

    /// <summary>
    /// Queues a refresh for <paramref name="identity"/>, unless there is nothing to store, the
    /// membership's hourly slot is already taken, or the queue is full.
    ///
    /// A refresh the full queue drops still consumes that membership's hour. That is deliberate: the
    /// queue is only full when the drain is already behind, so queuing more of the same work is the
    /// wrong answer, and the member's next request after the hour is the retry.
    /// </summary>
    /// <returns><c>true</c> when a refresh was queued.</returns>
    public bool Handle(ActorIdentity identity, MemberProfile profile)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(profile);

        // Asked before the hourly slot is claimed, so a token carrying neither claim does not keep
        // the next token that carries one out for an hour.
        if (!profile.HasAnyValue)
        {
            return false;
        }

        if (!_throttle.TryClaim(identity))
        {
            return false;
        }

        return _queue.TryEnqueue(new MemberProfileRefreshRequest(identity, profile));
    }
}
