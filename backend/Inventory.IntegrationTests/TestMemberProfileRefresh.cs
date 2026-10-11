using Inventory.Application.Tenancy;
using InventoryApi.Tests.Application.Tenancy;
using InventoryApi.Tests.Application.Time;

namespace InventoryApi.Tests;

/// <summary>
/// Builds the member-profile refresh scheduler a test hands to the business-scope middleware, and
/// reads back what the middleware queued (issue #524).
///
/// The queue is the observable half of the arrangement: the middleware only ever schedules, so what
/// a test asserts is which refreshes - if any - ended up queued. It lives in the root test namespace
/// beside <see cref="TestAppDbContext"/> because both the middleware tests and the full-pipeline API
/// tests need the same answer from the same real queue.
/// </summary>
internal static class TestMemberProfileRefresh
{
    private static readonly DateTime DefaultNow = new(2026, 10, 11, 1, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// A scheduler over <paramref name="queue"/> (a fresh one when none is given) and a clock the
    /// test controls, so the hourly rule is deterministic rather than wall-clock dependent.
    /// </summary>
    public static ScheduleMemberProfileRefresh Scheduler(
        MemberProfileRefreshQueue? queue = null,
        DateTime? utcNow = null) =>
        new(
            new MemberProfileRefreshThrottle(new FakeClock(utcNow ?? DefaultNow)),
            queue ?? new MemberProfileRefreshQueue());

    /// <summary>
    /// The combined scheduler the middleware injects: <paramref name="profileAccessor"/> (a fresh,
    /// unidentifiable fake when none is given) over <see cref="Scheduler"/>.
    /// </summary>
    public static MemberProfileRefreshRequestScheduler RequestScheduler(
        FakeActorProfileAccessor? profileAccessor = null,
        MemberProfileRefreshQueue? queue = null,
        DateTime? utcNow = null) =>
        new(
            profileAccessor ?? FakeActorProfileAccessor.Unidentifiable(),
            Scheduler(queue, utcNow));

    /// <summary>
    /// Everything queued so far, in order. The queue is completed first, so the read finishes
    /// instead of waiting for a refresh that will never arrive.
    /// </summary>
    public static async Task<List<MemberProfileRefreshRequest>> DrainAsync(MemberProfileRefreshQueue queue)
    {
        queue.Complete();

        var queued = new List<MemberProfileRefreshRequest>();
        await foreach (var request in queue.ReadAllAsync())
        {
            queued.Add(request);
        }

        return queued;
    }
}
