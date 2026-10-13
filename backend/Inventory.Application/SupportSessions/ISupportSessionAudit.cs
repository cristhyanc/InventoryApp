namespace Inventory.Application.SupportSessions;

/// <summary>Which half of a support session's life one audit event is about (issue #550).</summary>
public enum SupportSessionEvent
{
    /// <summary>A session was started for one business.</summary>
    Started = 1,

    /// <summary>A session stopped being usable because something ended it.</summary>
    Ended = 2,
}

/// <summary>
/// Why a support session ended (issue #550). It is recorded rather than inferred, because the two
/// cases are investigated differently: an administrator closing their own session is routine, and a
/// session closed by a business being switched off is part of the record of that deactivation.
///
/// <para>Expiry is deliberately <em>not</em> a member. Nothing happens when a session expires - the
/// rule simply stops answering "active", with no sweep and no row written - so there is no event to
/// record and nothing that could claim one. When the session ended, had nobody closed it, is already
/// in the trail: the start event carries the expiry instant.</para>
/// </summary>
public enum SupportSessionEndReason
{
    /// <summary>The administrator ended their own session.</summary>
    EndedByAdministrator = 1,

    /// <summary>
    /// The business the session was reading was deactivated, which ends every session of it in the
    /// same transaction (issue #545's hook).
    /// </summary>
    BusinessDeactivated = 2,
}

/// <summary>
/// One audit event about one support session (issue #550): who was looking, which business, why,
/// when the session ran, and - for an end - when and why it stopped.
///
/// <para><strong>The reason text is deliberately in this trail.</strong> Every other platform audit
/// entry in this application excludes free text, and this one carries it because the free text
/// <em>is</em> the point: a support session is a person from outside a business reading that
/// business's data, so "why" is the one fact that makes the access reviewable. It is the
/// administrator's own typed justification, not the business's data and not a member's personal
/// detail.</para>
///
/// <para><strong>What it does not carry.</strong> The session id, in any form - plaintext or hash -
/// because a trail is read by more people than a response is, and a credential in a log is a
/// credential. The business <em>name</em>, the names and addresses of its members, and the Entra
/// identity of anybody but the acting administrator, for the reason every entry beside it omits
/// them. <see cref="SupportSessionAuditEntry"/> has no field for any of those, so even a sink that
/// printed the whole entry could not emit one.</para>
/// </summary>
/// <param name="ActorDirectoryTenantId">The administrator's Entra <c>tid</c>, as the session stored it.</param>
/// <param name="ActorObjectId">Their Entra <c>oid</c>.</param>
/// <param name="BusinessId">The business the session reads.</param>
/// <param name="SessionKey">
/// The session row's own key, which is what ties an end event to the start event it belongs to. It is
/// an internal identifier and never the session id a request presents.
/// </param>
/// <param name="Event">Whether the session started or ended.</param>
/// <param name="Reason">The reason recorded when it started.</param>
/// <param name="StartedAtUtc">When it started.</param>
/// <param name="ExpiresAtUtc">When it would stop being usable on its own.</param>
/// <param name="EndedAtUtc">
/// When it was ended, for an end event, and <see langword="null"/> for a start - never <c>0</c> or
/// the start instant, so a start cannot read as an end that happened immediately.
/// </param>
/// <param name="EndReason">What ended it, for an end event, and <see langword="null"/> for a start.</param>
public sealed record SupportSessionAuditEntry(
    string ActorDirectoryTenantId,
    string ActorObjectId,
    int BusinessId,
    int SessionKey,
    SupportSessionEvent Event,
    string Reason,
    DateTime StartedAtUtc,
    DateTime ExpiresAtUtc,
    DateTime? EndedAtUtc,
    SupportSessionEndReason? EndReason)
{
    /// <summary>
    /// The event for a session that has just been stored. Every value comes off the stored row, so
    /// the trail describes what the database holds rather than what the caller asked for.
    /// </summary>
    public static SupportSessionAuditEntry Started(StoredSupportSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        return new SupportSessionAuditEntry(
            session.ActorDirectoryTenantId,
            session.ActorObjectId,
            session.BusinessId,
            session.Key,
            SupportSessionEvent.Started,
            session.Reason,
            session.StartedAtUtc,
            session.ExpiresAtUtc,
            EndedAtUtc: null,
            EndReason: null);
    }

    /// <summary>
    /// The event for a session that has just been ended, built from the row the end wrote. One
    /// factory for both ways a session can be closed, so the administrator's own end and a
    /// deactivation's cannot drift into two differently shaped trails.
    /// </summary>
    /// <param name="session">The session as the end left it; its ended instant is required.</param>
    /// <param name="endReason">Which of the two closed it.</param>
    /// <exception cref="ArgumentException">
    /// The session carries no ended instant, so it was not ended and there is nothing to record.
    /// </exception>
    public static SupportSessionAuditEntry Ended(
        StoredSupportSession session,
        SupportSessionEndReason endReason)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (session.EndedAtUtc is not { } endedAtUtc)
        {
            throw new ArgumentException(
                "A support session end event needs the instant the session was ended at.",
                nameof(session));
        }

        return new SupportSessionAuditEntry(
            session.ActorDirectoryTenantId,
            session.ActorObjectId,
            session.BusinessId,
            session.Key,
            SupportSessionEvent.Ended,
            session.Reason,
            session.StartedAtUtc,
            session.ExpiresAtUtc,
            endedAtUtc,
            endReason);
    }
}

/// <summary>
/// The audit sink for support sessions, implemented at the InventoryApi boundary over <c>ILogger</c>
/// (issue #550), for the same reasons as the platform decision and lifecycle trails beside it: the
/// event carries the request's correlation id, which is a transport fact, and an audit row in the
/// same database the session reads would be evidence kept inside the thing it is evidence about.
/// </summary>
public interface ISupportSessionAudit
{
    void Record(SupportSessionAuditEntry entry);
}
