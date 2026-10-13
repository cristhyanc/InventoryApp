namespace Inventory.Application.SupportSessions;

/// <summary>
/// Whether this request is being made inside a support session, and which business it reads
/// (issue #551).
///
/// <para><strong>Why it is a port.</strong> Joining a session is a transport fact - the caller
/// presents the session id in the <c>X-Support-Session</c> header, and whether they are the
/// configured platform administrator is answered by the API boundary's own authorization policy -
/// so the implementation belongs at that boundary, exactly as <c>IAuthenticatedActorAccessor</c>'s
/// does. What the inner layers need is the answer: the business, its name, and when the session
/// runs out.</para>
///
/// <para><strong>It is resolved once per request and memoised</strong>, like
/// <see cref="Tenancy.ICurrentBusinessProvider"/>, so the authorization handler that decides
/// whether the call is allowed, the middleware that publishes the business scope, and the use
/// cases that then read data cannot disagree about which session this is - or hash and look up the
/// same presented id three times.</para>
///
/// <para><strong>It is the only thing in the application that reads that header's value.</strong>
/// Everything downstream reads either this answer or the business scope published from it; nothing
/// re-derives a business from a request value.</para>
/// </summary>
public interface ISupportSessionAccessor
{
    /// <summary>
    /// Resolves - once per request - what the presented session id, if any, entitles this caller
    /// to.
    /// </summary>
    Task<SupportSessionAccess> ResolveAsync(CancellationToken cancellationToken);
}

/// <summary>
/// What a request presented, as far as support sessions are concerned (issue #551). There are
/// exactly three outcomes and no "unknown but proceed" state.
/// </summary>
public enum SupportSessionAccessState
{
    /// <summary>
    /// This request is not trying to join a session: it carries no <c>X-Support-Session</c> header,
    /// or the caller is not the configured platform administrator, in which case the header is not
    /// read at all. Such a request takes the ordinary membership path and is completely unaffected
    /// by this feature - which is what keeps an ordinary member who sets the header an ordinary
    /// member.
    /// </summary>
    NotRequested = 0,

    /// <summary>
    /// A session id was presented by a platform administrator and is not usable: it matches no
    /// session of theirs, the session was ended, it has expired, or the business it was granted for
    /// is no longer active. The request is refused with
    /// <see cref="SupportSessionErrorCodes.SessionEnded"/> and never falls back to the
    /// administrator's own membership.
    /// </summary>
    Ended = 1,

    /// <summary>
    /// The presented id names an active, unexpired session of this caller's, for a business that is
    /// still active. The request reads that business, read-only.
    /// </summary>
    Active = 2,
}

/// <summary>
/// The resolved support-session state of one request (issue #551).
/// </summary>
/// <param name="State">Which of the three outcomes this request is in.</param>
/// <param name="BusinessId">
/// The business the session reads, or <see langword="null"/> unless
/// <see cref="SupportSessionAccessState.Active"/>. It is the id stored on the matched session row,
/// which the start transaction took from the business row it read - never a request value.
/// </param>
/// <param name="BusinessName">
/// That business's display name, so <c>GET /api/me/access</c> can say whose data is being looked
/// at without a second read. <see langword="null"/> unless the session is active.
/// </param>
/// <param name="ExpiresAtUtc">
/// When the session stops being usable, or <see langword="null"/> unless it is active. The server
/// fixed it when the session started; no request can choose or extend it.
/// </param>
/// <param name="SessionKey">
/// The matched session row's own key, or <see langword="null"/> unless it is active. It is an
/// internal identifier and never the session id: it is what ties the per-request audit event to
/// the start event of the session it belongs to.
/// </param>
public sealed record SupportSessionAccess(
    SupportSessionAccessState State,
    int? BusinessId,
    string? BusinessName,
    DateTime? ExpiresAtUtc,
    int? SessionKey)
{
    /// <summary>No session was presented, so nothing about this request changes.</summary>
    public static SupportSessionAccess NotRequested { get; } =
        new(SupportSessionAccessState.NotRequested, null, null, null, null);

    /// <summary>A session was presented and is not usable.</summary>
    public static SupportSessionAccess Ended { get; } =
        new(SupportSessionAccessState.Ended, null, null, null, null);

    /// <summary>A usable session, for a business that is still active.</summary>
    public static SupportSessionAccess Active(
        int businessId,
        string businessName,
        DateTime expiresAtUtc,
        int sessionKey) =>
        new(SupportSessionAccessState.Active, businessId, businessName, expiresAtUtc, sessionKey);

    /// <summary>Whether this request is inside a usable support session.</summary>
    public bool IsActive => State == SupportSessionAccessState.Active;
}
