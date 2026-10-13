using Inventory.Application.PlatformAdmin;
using Inventory.Application.Tenancy;
using Inventory.Application.Time;
using Inventory.Domain.Tenancy;

namespace Inventory.Application.SupportSessions;

/// <summary>
/// The caller's own active support session, if they have one (issue #550).
///
/// <para>It answers about the authenticated actor and nobody else, which is the same boundary the
/// start and the end rest on: a session is only ever visible to the administrator it was granted to,
/// and no value a request supplies can select one. That is also what makes a change of platform
/// administrator the end of every session it granted - the configured <c>(tid, oid)</c> pair is the
/// only thing <c>PlatformAdminPolicy</c> admits, so the previous administrator can no longer reach
/// this endpoint at all and the new one's own session list does not contain the old row.</para>
///
/// <para>"Active" is <see cref="SupportSessionPolicy.IsActive"/>: not ended, and not past its expiry.
/// An expired session is therefore reported as nothing without anything having had to run - no sweep,
/// no job, and no row left claiming otherwise.</para>
///
/// <para>It is a read: no transaction, and no audit event. The session's existence was audited when
/// it started, and reporting it back to the administrator who holds it is not a second access to the
/// business. It deliberately does not re-issue the session id either - that value exists in one
/// response only.</para>
/// </summary>
public sealed class GetActiveSupportSession
{
    private readonly IAuthenticatedActorAccessor _actorAccessor;
    private readonly IPlatformBusinessDirectoryStore _businesses;
    private readonly ISupportSessionStore _sessions;
    private readonly IClock _clock;

    public GetActiveSupportSession(
        IAuthenticatedActorAccessor actorAccessor,
        IPlatformBusinessDirectoryStore businesses,
        ISupportSessionStore sessions,
        IClock clock)
    {
        _actorAccessor = actorAccessor;
        _businesses = businesses;
        _sessions = sessions;
        _clock = clock;
    }

    /// <returns>
    /// The caller's active session, or <see langword="null"/> when they have none.
    /// </returns>
    /// <exception cref="BusinessAccessDeniedException">
    /// The request carries no identifiable actor, so there is no session to look for.
    /// </exception>
    public async Task<ActiveSupportSession?> Handle(CancellationToken cancellationToken)
    {
        var actor = _actorAccessor.GetCurrentActor().Actor
            ?? throw new BusinessAccessDeniedException(BusinessAccessDenialReason.NotAuthenticated);

        var session = await _sessions
            .FindActiveForActorAsync(actor, _clock.UtcNow, cancellationToken)
            .ConfigureAwait(false);
        if (session is null)
        {
            return null;
        }

        var business = await _businesses
            .FindLifecycleTargetAsync(session.BusinessId, cancellationToken)
            .ConfigureAwait(false);

        return new ActiveSupportSession(
            session.BusinessId,
            business?.Name,
            session.Reason,
            session.StartedAtUtc,
            session.ExpiresAtUtc);
    }
}
