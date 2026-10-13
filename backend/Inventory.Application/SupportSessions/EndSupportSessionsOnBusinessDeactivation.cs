using Inventory.Application.PlatformAdmin;
using Inventory.Domain.Tenancy;

namespace Inventory.Application.SupportSessions;

/// <summary>
/// What happens to support sessions when the business they are reading is switched off (issue #550,
/// on the hook point issue #545 declared for exactly this): every session of that business that is
/// still active ends, in the deactivation's own transaction.
///
/// <para><strong>Why it has to be inside that transaction.</strong> A session left running by a
/// deactivation that committed would be precisely the access the deactivation exists to stop - the
/// business's own members are refused on their next request, and a supporter would carry on reading
/// it - while a session ended by a deactivation that then rolled back would have been cut off for
/// nothing. The two are therefore one operation, which is what
/// <c>IBusinessDeactivationHook</c> exists to make possible: the lifecycle transition of issue #545
/// calls this last inside its write transaction, after the status and the invites, and this class is
/// the single registration that replaces its shipped no-op.</para>
///
/// <para><strong>It ends sessions by the business the transition read, not by anything a request
/// supplied.</strong> The business id on <see cref="BusinessDeactivated"/> is the one the deactivation
/// read inside its own transaction, which is also why this hook is handed a business id and an instant
/// and nothing else.</para>
///
/// <para><strong>A reactivation never revives a session.</strong> Nothing here is undone later: the
/// rows carry an ended instant, which no path rewrites, so a business coming back does not restore
/// somebody else's access to it. An administrator who still needs to look starts a new session, which
/// is audited as a new one.</para>
///
/// <para><strong>Where the audit events sit, and the one trade-off in this feature.</strong> Each
/// ended session is audited here, inside the deactivation's transaction, rather than after its commit
/// - the one audit trail in this application that is not strictly post-commit. The hook has no
/// post-commit callback to use (the only step left after it is the commit itself), and the
/// alternative - a deactivation that ended somebody's access and recorded nothing about whose - would
/// leave the worse gap. What a failed commit therefore leaves behind is an event about an end that did
/// not happen, in a request that also failed: the business is still active, the session is still live,
/// and the administrator sees the error rather than a silent partial result.</para>
/// </summary>
public sealed class EndSupportSessionsOnBusinessDeactivation : IBusinessDeactivationHook
{
    private readonly ISupportSessionStore _sessions;
    private readonly ISupportSessionAudit _audit;

    public EndSupportSessionsOnBusinessDeactivation(
        ISupportSessionStore sessions,
        ISupportSessionAudit audit)
    {
        _sessions = sessions;
        _audit = audit;
    }

    /// <summary>
    /// Ends every support session of the deactivated business that is still active, at the instant
    /// the deactivation is being recorded at, and records one audit event per session it closed.
    ///
    /// <para>An already-ended or expired session is left exactly as it is, which is also what makes a
    /// repeated deactivation idempotent, and a deactivation with no session to end writes and audits
    /// nothing at all - so an ordinary deactivation never looks like one that cut somebody off.</para>
    /// </summary>
    public async Task OnBusinessDeactivatedAsync(
        BusinessDeactivated deactivated,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(deactivated);

        var ended = await _sessions
            .EndAllForBusinessAsync(
                BusinessId.From(deactivated.BusinessId),
                deactivated.DeactivatedAtUtc,
                cancellationToken)
            .ConfigureAwait(false);

        // One event per session the statement actually closed, each attributed to the administrator
        // whose session it was rather than to whoever deactivated the business: the question this
        // trail answers is "whose access stopped, and when".
        foreach (var session in ended)
        {
            _audit.Record(
                SupportSessionAuditEntry.Ended(session, SupportSessionEndReason.BusinessDeactivated));
        }
    }
}
