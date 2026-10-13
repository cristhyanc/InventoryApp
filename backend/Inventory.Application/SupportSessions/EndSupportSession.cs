using Inventory.Application.PlatformAdmin;
using Inventory.Application.Tenancy;
using Inventory.Application.Time;
using Inventory.Domain.Tenancy;

namespace Inventory.Application.SupportSessions;

/// <summary>
/// Closes the caller's own support session (issue #550): the second of the three ways a session ends,
/// beside its expiry and the deactivation of the business it was reading.
///
/// <para><strong>It takes no session id, and that is the point.</strong> The session it ends is the
/// one belonging to the authenticated actor - the configured platform administrator the policy at the
/// boundary has already established - so there is no value a request could supply to end somebody
/// else's session, and no way to end one by guessing an identifier. For the same reason it is phrased
/// as "every active session of mine": under the one-active-session rule that is exactly one, and a row
/// written by hand must not be able to leave an administrator unable to close their own access.</para>
///
/// <para><strong>Ending nothing is a success.</strong> A session that has already expired, one that a
/// deactivation closed, and having none at all are all answered the same way - nothing written,
/// nothing audited, and the caller told there was nothing active. Re-stamping a session that was
/// already closed would claim a transition that never happened, and an audit event for it would
/// record a change the database does not have.</para>
///
/// <para>The write runs inside issue #522's shared <c>BEGIN IMMEDIATE</c> transaction, so the read of
/// what is active and the conditional write that closes it are one operation, and the audit events
/// come after the commit.</para>
/// </summary>
public sealed class EndSupportSession
{
    private readonly IAuthenticatedActorAccessor _actorAccessor;
    private readonly IPlatformBusinessDirectoryStore _businesses;
    private readonly ISupportSessionStore _sessions;
    private readonly MembershipWriteGuard _guard;
    private readonly ISupportSessionAudit _audit;
    private readonly IClock _clock;

    public EndSupportSession(
        IAuthenticatedActorAccessor actorAccessor,
        IPlatformBusinessDirectoryStore businesses,
        ISupportSessionStore sessions,
        MembershipWriteGuard guard,
        ISupportSessionAudit audit,
        IClock clock)
    {
        _actorAccessor = actorAccessor;
        _businesses = businesses;
        _sessions = sessions;
        _guard = guard;
        _audit = audit;
        _clock = clock;
    }

    /// <returns>
    /// The session as the end left it, or <see langword="null"/> when the caller had no active
    /// session to end.
    /// </returns>
    /// <exception cref="BusinessAccessDeniedException">
    /// The request carries no identifiable actor, so there is no session to look for.
    /// </exception>
    public async Task<EndedSupportSession?> Handle(CancellationToken cancellationToken)
    {
        var actor = _actorAccessor.GetCurrentActor().Actor
            ?? throw new BusinessAccessDeniedException(BusinessAccessDenialReason.NotAuthenticated);

        var intent = default(MembershipWriteIntent);

        var ended = await _guard
            .ExecuteAsync(
                intent,
                token => _sessions.EndAllForActorAsync(actor, _clock.UtcNow, token),
                cancellationToken)
            .ConfigureAwait(false);

        // After the commit, one event per session the statement actually closed - from the rows it
        // returned rather than from what this code expected to close.
        foreach (var session in ended)
        {
            _audit.Record(
                SupportSessionAuditEntry.Ended(session, SupportSessionEndReason.EndedByAdministrator));
        }

        // The newest, which under the one-active-session rule is the only one. A hand-written second
        // row is closed and audited with the rest; reporting one of them is a presentation decision,
        // and reporting the latest is the one that matches what the administrator was looking at.
        var reported = ended.OrderByDescending(session => session.Key).FirstOrDefault();
        if (reported is null)
        {
            return null;
        }

        // Read after the write: the name is for the response alone, so it is deliberately not read
        // inside the transaction that closes the session - nothing is decided from it.
        var business = await _businesses
            .FindLifecycleTargetAsync(reported.BusinessId, cancellationToken)
            .ConfigureAwait(false);

        return new EndedSupportSession(
            reported.BusinessId,
            business?.Name,
            reported.StartedAtUtc,
            reported.ExpiresAtUtc,
            reported.EndedAtUtc!.Value);
    }
}
