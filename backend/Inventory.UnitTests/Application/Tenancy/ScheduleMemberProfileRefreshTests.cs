using Inventory.Application.Tenancy;
using Inventory.Domain.Tenancy;
using InventoryApi.Tests.Application.Time;
using Xunit;

namespace InventoryApi.Tests.Application.Tenancy;

/// <summary>
/// What a request does about the member's display name and email address (issue #524): it decides
/// whether to schedule a refresh, and hands it over. It never writes, never waits and never throws.
/// </summary>
public class ScheduleMemberProfileRefreshTests
{
    private static readonly DateTime Now = new(2026, 10, 11, 1, 0, 0, DateTimeKind.Utc);

    private const string Tid = "11111111-1111-1111-1111-111111111111";
    private const string Oid = "22222222-2222-2222-2222-222222222222";

    private static ActorIdentity Actor(string oid = Oid)
    {
        Assert.True(ActorIdentity.TryCreate(Tid, oid, out var actor));
        return actor!;
    }

    private static MemberProfile Profile() => MemberProfile.Create("Ada Lovelace", "ada@vending.example");

    private static (ScheduleMemberProfileRefresh Schedule, MemberProfileRefreshQueue Queue, FakeClock Clock) Build()
    {
        var clock = new FakeClock(Now);
        var queue = new MemberProfileRefreshQueue();

        return (new ScheduleMemberProfileRefresh(new MemberProfileRefreshThrottle(clock), queue), queue, clock);
    }

    private static async Task<List<MemberProfileRefreshRequest>> DrainAsync(MemberProfileRefreshQueue queue)
    {
        queue.Complete();

        var queued = new List<MemberProfileRefreshRequest>();
        await foreach (var request in queue.ReadAllAsync())
        {
            queued.Add(request);
        }

        return queued;
    }

    [Fact]
    public async Task A_members_claims_are_queued_for_their_own_identity()
    {
        var (schedule, queue, _) = Build();

        Assert.True(schedule.Handle(Actor(), Profile()));

        var queued = Assert.Single(await DrainAsync(queue));
        Assert.Equal(Actor(), queued.Identity);
        Assert.Equal(Profile(), queued.Profile);
    }

    /// <summary>
    /// The hourly rule, through the scheduler: a member's second request in the same hour queues
    /// nothing at all, so the drain has one write to make rather than one per request.
    /// </summary>
    [Fact]
    public async Task A_second_request_inside_the_hour_queues_nothing()
    {
        var (schedule, queue, clock) = Build();

        Assert.True(schedule.Handle(Actor(), Profile()));
        Assert.False(schedule.Handle(Actor(), Profile()));

        clock.UtcNow = Now + TimeSpan.FromMinutes(30);
        Assert.False(schedule.Handle(Actor(), Profile()));

        Assert.Single(await DrainAsync(queue));
    }

    [Fact]
    public async Task A_request_after_the_hour_queues_a_fresh_refresh()
    {
        var (schedule, queue, clock) = Build();

        Assert.True(schedule.Handle(Actor(), Profile()));

        clock.UtcNow = Now + MemberProfileRefreshThrottle.Interval;
        var renamed = MemberProfile.Create("Ada Byron", "ada@vending.example");
        Assert.True(schedule.Handle(Actor(), renamed));

        var queued = await DrainAsync(queue);
        Assert.Equal(2, queued.Count);
        Assert.Equal(renamed, queued[1].Profile);
    }

    /// <summary>
    /// A token carrying neither claim - the missing-claims case - queues nothing, and must not
    /// consume the membership's hourly slot either: the next token that does carry a claim has to
    /// be able to store it immediately.
    /// </summary>
    [Fact]
    public async Task A_token_with_no_display_claims_queues_nothing_and_keeps_the_slot_free()
    {
        var (schedule, queue, _) = Build();

        Assert.False(schedule.Handle(Actor(), MemberProfile.None));
        Assert.False(schedule.Handle(Actor(), MemberProfile.Create("   ", null)));

        Assert.Empty(await DrainAsync(queue));

        var (scheduleAgain, queueAgain, _) = Build();
        Assert.False(scheduleAgain.Handle(Actor(), MemberProfile.None));
        Assert.True(scheduleAgain.Handle(Actor(), Profile()));
        Assert.Single(await DrainAsync(queueAgain));
    }

    /// <summary>
    /// A full queue drops the newest refresh and says so, rather than throwing or waiting. This is
    /// the burst case: scheduling runs on the request thread, so it must not be able to block it,
    /// and a dropped display refresh costs nothing but an hour of staleness.
    /// </summary>
    [Fact]
    public async Task A_full_queue_drops_the_refresh_instead_of_blocking_or_throwing()
    {
        var (schedule, queue, _) = Build();

        for (var i = 0; i < MemberProfileRefreshQueue.Capacity; i++)
        {
            Assert.True(schedule.Handle(Actor($"33333333-3333-3333-3333-{i:D12}"), Profile()));
        }

        Assert.False(schedule.Handle(Actor(), Profile()));

        Assert.Equal(MemberProfileRefreshQueue.Capacity, (await DrainAsync(queue)).Count);
    }

    /// <summary>
    /// After the host has stopped accepting refreshes, scheduling one is a no-op rather than an
    /// exception: a request in flight during shutdown must still be answered normally.
    /// </summary>
    [Fact]
    public void Scheduling_after_shutdown_is_harmless()
    {
        var (schedule, queue, _) = Build();
        queue.Complete();

        Assert.False(schedule.Handle(Actor(), Profile()));
    }
}
