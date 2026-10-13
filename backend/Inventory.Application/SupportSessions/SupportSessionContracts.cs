using Inventory.Domain.Tenancy;

namespace Inventory.Application.SupportSessions;

/// <summary>
/// One support session as it is about to be stored (issue #550). Every value on it is the server's
/// own: the actor comes from the validated token, the business from the row the start transaction
/// read, the reason from the administrator's submitted text after
/// <see cref="SupportSessionPolicy.RequireReason"/> has normalised it, and both instants from the
/// application clock and <see cref="SupportSessionPolicy.ExpiresAt"/>.
/// </summary>
/// <param name="Actor">The platform administrator the session belongs to.</param>
/// <param name="BusinessId">The business it grants read access to.</param>
/// <param name="Reason">Why the administrator is looking, as the audit trail will record it.</param>
/// <param name="SessionIdHash">
/// <see cref="SupportSessionId.Hash"/>'s output for the id generated for this session. The id's
/// plaintext deliberately does not cross this port: no store, adapter or row ever sees it.
/// </param>
/// <param name="StartedAtUtc">When the session started.</param>
/// <param name="ExpiresAtUtc">When it stops being usable, which no request may choose or extend.</param>
public sealed record NewSupportSession(
    ActorIdentity Actor,
    int BusinessId,
    string Reason,
    string SessionIdHash,
    DateTime StartedAtUtc,
    DateTime ExpiresAtUtc);

/// <summary>
/// One support session as persistence reads it back (issue #550) - the row's own facts, and nothing
/// derived.
///
/// <para>The session id is absent in both directions: the hash is write-only as far as this contract
/// is concerned, and the plaintext exists for one response. Whether the session is still usable is
/// not a field either - it is <see cref="SupportSessionPolicy.IsActive"/> applied to
/// <see cref="ExpiresAtUtc"/> and <see cref="EndedAtUtc"/>, so one rule answers it everywhere
/// instead of a stored flag that a clock could make wrong.</para>
/// </summary>
/// <param name="Key">
/// The row's own key. It is an internal identifier, not the session id: it is never a credential, it
/// cannot be presented by a request, and it is what the audit trail uses to tie an end event to the
/// start event it belongs to.
/// </param>
/// <param name="BusinessId">The business the session grants read access to.</param>
/// <param name="ActorDirectoryTenantId">
/// The Entra <c>tid</c> of the administrator it belongs to, exactly as stored. It is carried as the
/// stored string rather than a parsed <see cref="ActorIdentity"/> because a hand-written row must
/// still be reportable and endable: a value that cannot be parsed is not a reason to make a session
/// impossible to close.
/// </param>
/// <param name="ActorObjectId">Their Entra <c>oid</c>, on the same terms.</param>
/// <param name="Reason">The reason recorded when it started.</param>
/// <param name="StartedAtUtc">When it started.</param>
/// <param name="ExpiresAtUtc">When it stops being usable.</param>
/// <param name="EndedAtUtc">
/// When something decided to end it - the administrator's own end call, or the deactivation of its
/// business - or <see langword="null"/>. A session that merely expired keeps <see langword="null"/>
/// here: expiry is derived, never swept (see <see cref="SupportSessionPolicy.IsActive"/>).
/// </param>
public sealed record StoredSupportSession(
    int Key,
    int BusinessId,
    string ActorDirectoryTenantId,
    string ActorObjectId,
    string Reason,
    DateTime StartedAtUtc,
    DateTime ExpiresAtUtc,
    DateTime? EndedAtUtc);

/// <summary>
/// A started support session, as the start endpoint answers (issue #550) - and the one and only time
/// <see cref="SessionId"/> exists outside the request that generated it.
/// </summary>
/// <param name="SessionId">
/// The opaque session id, in plaintext. The administrator either keeps it from this response or
/// starts another session: nothing reports it again, and the stored row holds only its hash.
/// </param>
/// <param name="BusinessId">The business the session reads.</param>
/// <param name="BusinessName">
/// Its display name, so the caller - and the banner issue #513 will build on this - can say whose
/// data is being looked at without a second call.
/// </param>
/// <param name="StartedAtUtc">When the session started.</param>
/// <param name="ExpiresAtUtc">When it stops being usable.</param>
public sealed record StartedSupportSession(
    string SessionId,
    int BusinessId,
    string BusinessName,
    DateTime StartedAtUtc,
    DateTime ExpiresAtUtc);

/// <summary>
/// The caller's own active support session, as the "get active" endpoint answers (issue #550).
///
/// <para>It deliberately does not carry the session id. That value is a credential, and a read that
/// re-published it would turn every "do I have a session?" call into a way to recover one - so a lost
/// id means starting a new session, which is audited, rather than asking for the old one back.</para>
/// </summary>
/// <param name="BusinessId">The business the session reads.</param>
/// <param name="BusinessName">
/// Its display name, or <see langword="null"/> when the business row could not be read at all. No
/// path in this application deletes a business, so that is a state nothing produces; reporting the
/// session without a name rather than withholding the session is the useful direction, because the
/// administrator can still end it.
/// </param>
/// <param name="Reason">The reason recorded when the session started.</param>
/// <param name="StartedAtUtc">When it started.</param>
/// <param name="ExpiresAtUtc">When it stops being usable.</param>
public sealed record ActiveSupportSession(
    int BusinessId,
    string? BusinessName,
    string Reason,
    DateTime StartedAtUtc,
    DateTime ExpiresAtUtc);

/// <summary>
/// A support session as the end endpoint leaves it (issue #550): which business it was reading, when
/// it ran, and the instant it was closed at.
/// </summary>
/// <param name="BusinessId">The business the session was reading.</param>
/// <param name="BusinessName">
/// Its display name, or <see langword="null"/> when the business row could not be read at all - the
/// same state <see cref="ActiveSupportSession.BusinessName"/> describes. The session is ended either
/// way: closing access must never depend on a display name being available.
/// </param>
/// <param name="StartedAtUtc">When the session started.</param>
/// <param name="ExpiresAtUtc">When it would have expired on its own.</param>
/// <param name="EndedAtUtc">The instant it was ended at, which is now recorded on the row.</param>
public sealed record EndedSupportSession(
    int BusinessId,
    string? BusinessName,
    DateTime StartedAtUtc,
    DateTime ExpiresAtUtc,
    DateTime EndedAtUtc);
