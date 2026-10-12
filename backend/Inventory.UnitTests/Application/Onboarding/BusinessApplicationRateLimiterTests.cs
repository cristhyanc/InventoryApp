using Inventory.Application.Onboarding;
using Inventory.Domain.Tenancy;
using InventoryApi.Tests.Application.Time;
using Xunit;

namespace InventoryApi.Tests.Application.Onboarding;

/// <summary>
/// The per-applicant submission limit of issue #541: a sliding window over one identity's attempts,
/// with no wall clock anywhere - every instant comes from a <see cref="FakeClock"/> the test moves.
/// </summary>
public class BusinessApplicationRateLimiterTests
{
    private const string Tid = "11111111-1111-1111-1111-111111111111";

    private static readonly DateTime Start = new(2026, 10, 12, 1, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void An_identity_may_submit_up_to_the_limit_and_is_then_refused()
    {
        var (limiter, _) = Limiter();
        var identity = Actor("22222222-2222-2222-2222-222222222222");

        for (var attempt = 0; attempt < BusinessApplicationRateLimiter.MaxApplicationsPerWindow; attempt++)
        {
            Assert.True(limiter.TryClaim(identity, out var granted));
            Assert.Equal(TimeSpan.Zero, granted);
        }

        Assert.False(limiter.TryClaim(identity, out var retryAfter));
        Assert.Equal(BusinessApplicationRateLimiter.Window, retryAfter);
    }

    /// <summary>
    /// The window slides: the oldest attempt leaving it frees exactly one slot, and the reported
    /// wait is the time until that happens rather than a whole window.
    /// </summary>
    [Fact]
    public void An_attempt_leaving_the_window_frees_exactly_one_slot()
    {
        var (limiter, clock) = Limiter();
        var identity = Actor("22222222-2222-2222-2222-222222222222");

        Assert.True(limiter.TryClaim(identity, out _));

        // The remaining allowance, spent a minute later, so the first attempt is the oldest.
        clock.UtcNow = Start.AddMinutes(1);
        for (var attempt = 1; attempt < BusinessApplicationRateLimiter.MaxApplicationsPerWindow; attempt++)
        {
            Assert.True(limiter.TryClaim(identity, out _));
        }

        Assert.False(limiter.TryClaim(identity, out var retryAfter));
        Assert.Equal(BusinessApplicationRateLimiter.Window - TimeSpan.FromMinutes(1), retryAfter);

        // One tick before the first attempt leaves the window it is still refused; at the window's
        // edge exactly one slot opens, and the next attempt after that is refused again.
        clock.UtcNow = Start + BusinessApplicationRateLimiter.Window - TimeSpan.FromTicks(1);
        Assert.False(limiter.TryClaim(identity, out _));

        clock.UtcNow = Start + BusinessApplicationRateLimiter.Window;
        Assert.True(limiter.TryClaim(identity, out _));
        Assert.False(limiter.TryClaim(identity, out _));
    }

    /// <summary>
    /// The bucket is the identity. Two people applying at the same moment each get their own
    /// allowance, which is what makes the limit a protection rather than a queue.
    /// </summary>
    [Fact]
    public void One_identitys_exhausted_allowance_does_not_refuse_another()
    {
        var (limiter, _) = Limiter();
        var first = Actor("22222222-2222-2222-2222-222222222222");
        var second = Actor("33333333-3333-3333-3333-333333333333");

        for (var attempt = 0; attempt < BusinessApplicationRateLimiter.MaxApplicationsPerWindow; attempt++)
        {
            Assert.True(limiter.TryClaim(first, out _));
        }

        Assert.False(limiter.TryClaim(first, out _));
        Assert.True(limiter.TryClaim(second, out _));
    }

    /// <summary>
    /// The identity is the normalised <c>(tid, oid)</c> pair, so the same person signing in with a
    /// differently-spelled object id claims from one allowance rather than a fresh one.
    /// </summary>
    [Fact]
    public void The_same_identity_in_another_guid_spelling_is_the_same_bucket()
    {
        var (limiter, _) = Limiter();
        var dashed = Actor("22222222-2222-2222-2222-222222222222");
        var braced = Actor("{22222222-2222-2222-2222-222222222222}");

        for (var attempt = 0; attempt < BusinessApplicationRateLimiter.MaxApplicationsPerWindow; attempt++)
        {
            Assert.True(limiter.TryClaim(dashed, out _));
        }

        Assert.False(limiter.TryClaim(braced, out _));
        Assert.Equal(1, limiter.TrackedIdentities);
    }

    /// <summary>
    /// A clock that moved backwards never expires an attempt: "negative elapsed time" is not "the
    /// window has passed", so the allowance cannot be reset by one.
    /// </summary>
    [Fact]
    public void A_clock_that_moved_backwards_does_not_free_a_slot()
    {
        var (limiter, clock) = Limiter();
        var identity = Actor("22222222-2222-2222-2222-222222222222");

        for (var attempt = 0; attempt < BusinessApplicationRateLimiter.MaxApplicationsPerWindow; attempt++)
        {
            Assert.True(limiter.TryClaim(identity, out _));
        }

        clock.UtcNow = Start - BusinessApplicationRateLimiter.Window;

        Assert.False(limiter.TryClaim(identity, out _));
    }

    /// <summary>
    /// Clearing out forgets only the identities with nothing left inside the window, so it can
    /// neither grant nor deny an attempt: the identity whose attempts are still live keeps its
    /// exhausted allowance across a prune that drops hundreds of idle ones.
    /// </summary>
    [Fact]
    public void Clearing_out_idle_identities_never_resets_a_live_allowance()
    {
        var (limiter, clock) = Limiter();

        // Enough identities to cross the tracking hint, each with one attempt at Start.
        for (var index = 0; index <= BusinessApplicationRateLimiter.MaxTrackedIdentities; index++)
        {
            Assert.True(limiter.TryClaim(Actor($"44444444-4444-4444-4444-{index:D12}"), out _));
        }

        // A window later every one of those has left it, so the next claim clears them all out -
        // and the identity doing the claiming spends its whole allowance inside that same instant.
        clock.UtcNow = Start + BusinessApplicationRateLimiter.Window;
        var live = Actor("22222222-2222-2222-2222-222222222222");
        for (var attempt = 0; attempt < BusinessApplicationRateLimiter.MaxApplicationsPerWindow; attempt++)
        {
            Assert.True(limiter.TryClaim(live, out _));
        }

        // The clear-out dropped the idle identities and left this one's live attempts counted, so
        // it is refused rather than handed the allowance a second time.
        Assert.False(limiter.TryClaim(live, out _));
        Assert.Equal(1, limiter.TrackedIdentities);
    }

    private static (BusinessApplicationRateLimiter Limiter, FakeClock Clock) Limiter()
    {
        var clock = new FakeClock(Start);
        return (new BusinessApplicationRateLimiter(clock), clock);
    }

    private static ActorIdentity Actor(string objectId)
    {
        Assert.True(ActorIdentity.TryCreate(Tid, objectId, out var actor));
        return actor!;
    }
}
