namespace Inventory.Application.Tenancy;

/// <summary>
/// The one call the business-scope middleware makes after the response to schedule a member's
/// display name and email refresh (issue #524): read the current caller's profile, and hand it to
/// the queue if there is one. Combining the two dependencies here, rather than injecting
/// <see cref="IActorProfileAccessor"/> and <see cref="ScheduleMemberProfileRefresh"/> separately
/// into the middleware, is what keeps the middleware's own dependency list at the one new
/// parameter this feature needs.
///
/// Scoped, like <see cref="IActorProfileAccessor"/>: it reads one request's caller, never cached
/// across callers.
/// </summary>
public sealed class MemberProfileRefreshRequestScheduler
{
    private readonly IActorProfileAccessor _actorProfileAccessor;
    private readonly ScheduleMemberProfileRefresh _scheduleMemberProfileRefresh;

    public MemberProfileRefreshRequestScheduler(
        IActorProfileAccessor actorProfileAccessor,
        ScheduleMemberProfileRefresh scheduleMemberProfileRefresh)
    {
        _actorProfileAccessor = actorProfileAccessor;
        _scheduleMemberProfileRefresh = scheduleMemberProfileRefresh;
    }

    /// <summary>
    /// Schedules a refresh for the current caller, when their validated token identifies one.
    /// </summary>
    public void ScheduleForCurrentActor()
    {
        if (_actorProfileAccessor.GetCurrentActorProfile() is { } actor)
        {
            _scheduleMemberProfileRefresh.Handle(actor.Identity, actor.Profile);
        }
    }
}
