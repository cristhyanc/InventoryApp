using Inventory.Application.SupportSessions;

namespace InventoryApi.DTOs;

/// <summary>
/// The body of <c>POST /api/super-admin/support-sessions/{businessId}</c> (issue #550).
///
/// <para>The reason is the only thing a caller may send, and it is required: a support session is
/// read access to somebody else's business, so the trail has to say why. Nothing here names a
/// business, an actor, an instant or a duration - the business is the route value, the actor is the
/// validated token, and the server decides both instants. A caller-supplied duration would make "how
/// long was this business readable?" a property of the request rather than of the feature.</para>
/// </summary>
/// <param name="Reason">
/// Why the administrator needs to look, at most 500 characters once trimmed. It is recorded on the
/// session and in the audit trail, and it is the one free-text value those trails carry.
/// </param>
public sealed record SuperAdminStartSupportSessionRequest(string? Reason);

/// <summary>
/// A started support session, as <c>POST /api/super-admin/support-sessions/{businessId}</c> answers
/// (issue #550).
///
/// <para><see cref="SessionId"/> appears here and nowhere else in the application: not in the active
/// session read, not in a log, not in an audit event, and not in the stored row, which holds only its
/// hash. An administrator who loses it starts another session.</para>
/// </summary>
/// <param name="SessionId">
/// The opaque session id. Under issue #551 a request joins the session by presenting it in the
/// <c>X-Support-Session</c> header; in this slice nothing reads it yet, so a session grants no access
/// of its own.
/// </param>
/// <param name="BusinessId">The business the session may read.</param>
/// <param name="BusinessName">Its display name, so the caller can say whose data is being looked at.</param>
/// <param name="StartedAtUtc">When the session started.</param>
/// <param name="ExpiresAtUtc">
/// When it stops being usable - 60 minutes after it started, which is both the default and the server
/// maximum, and which no request may extend.
/// </param>
public sealed record SuperAdminStartedSupportSessionResponse(
    string SessionId,
    int BusinessId,
    string BusinessName,
    DateTime StartedAtUtc,
    DateTime ExpiresAtUtc)
{
    public static SuperAdminStartedSupportSessionResponse From(StartedSupportSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        return new SuperAdminStartedSupportSessionResponse(
            session.SessionId,
            session.BusinessId,
            session.BusinessName,
            session.StartedAtUtc,
            session.ExpiresAtUtc);
    }
}

/// <summary>
/// The caller's own active support session, as <c>GET /api/super-admin/support-sessions/active</c>
/// answers (issue #550). It deliberately carries no session id: that value exists in the start
/// response only, so a read cannot be used to recover one.
/// </summary>
/// <param name="BusinessId">The business the session may read.</param>
/// <param name="BusinessName">
/// Its display name, or <c>null</c> when the business row could not be read at all - a state no path
/// in this application produces. The session is still reported, because the administrator can then
/// end it.
/// </param>
/// <param name="Reason">The reason recorded when the session started.</param>
/// <param name="StartedAtUtc">When it started.</param>
/// <param name="ExpiresAtUtc">When it stops being usable.</param>
public sealed record SuperAdminActiveSupportSessionResponse(
    int BusinessId,
    string? BusinessName,
    string Reason,
    DateTime StartedAtUtc,
    DateTime ExpiresAtUtc)
{
    public static SuperAdminActiveSupportSessionResponse From(ActiveSupportSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        return new SuperAdminActiveSupportSessionResponse(
            session.BusinessId,
            session.BusinessName,
            session.Reason,
            session.StartedAtUtc,
            session.ExpiresAtUtc);
    }
}

/// <summary>
/// A support session as the end left it, as <c>POST /api/super-admin/support-sessions/active/end</c>
/// answers (issue #550).
/// </summary>
/// <param name="BusinessId">The business the session was reading.</param>
/// <param name="BusinessName">Its display name, on the same terms as the active read's.</param>
/// <param name="StartedAtUtc">When the session started.</param>
/// <param name="ExpiresAtUtc">When it would have expired on its own.</param>
/// <param name="EndedAtUtc">The instant it was ended at, which is now recorded on the row.</param>
public sealed record SuperAdminEndedSupportSessionResponse(
    int BusinessId,
    string? BusinessName,
    DateTime StartedAtUtc,
    DateTime ExpiresAtUtc,
    DateTime EndedAtUtc)
{
    public static SuperAdminEndedSupportSessionResponse From(EndedSupportSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        return new SuperAdminEndedSupportSessionResponse(
            session.BusinessId,
            session.BusinessName,
            session.StartedAtUtc,
            session.ExpiresAtUtc,
            session.EndedAtUtc);
    }
}
