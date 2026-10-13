using System.Buffers.Text;
using System.Security.Cryptography;
using Inventory.Application.PlatformAdmin;
using Inventory.Application.Tenancy;
using Inventory.Application.Time;
using Inventory.Domain.Exceptions;
using Inventory.Domain.Tenancy;

namespace Inventory.Application.SupportSessions;

/// <summary>
/// Opens one time-limited, audited support session for one active business (issue #550, a slice of
/// #512 under #328): the Super Admin says which business they need to look at and why, and gets back
/// an opaque session id, the business's name and the instant the session expires.
///
/// <para><strong>What a session is, and what this slice of it does.</strong> A support session is the
/// record that a person from outside a business is allowed to <em>read</em> that business's data for
/// an hour. Storing that record, reporting it and ending it is all this task does: no request resolves
/// a business from it yet, and nothing in the pipeline reads it (issue #551 adds that, and issue #552
/// the dashboard's behaviour under it). A session that exists today therefore grants no access at all,
/// which is deliberately the fail-closed order to build this in.</para>
///
/// <para><strong>The session id is a credential.</strong> It is drawn from the platform's
/// cryptographic generator rather than from a counter, a key or a <c>Guid</c>, rendered URL-safe, and
/// returned exactly once - in this response. What crosses the persistence port is
/// <see cref="SupportSessionId.Hash"/>'s output, so no store, adapter or row ever holds the plaintext,
/// and what crosses the audit port is neither. An administrator who loses it starts another session,
/// which is audited like the first.</para>
///
/// <para><strong>The business id names the subject, and resolves nothing.</strong> It comes from the
/// route (docs/architecture.md § Super Admin businesses API), the request still carries a
/// <em>denied</em> business scope, and nothing this writes is stamped with it. The business is read
/// through <see cref="IPlatformBusinessDirectoryStore"/> - the one port a platform administrator reads
/// businesses through - rather than through a second read path of this feature's own, so what the
/// Super Admin can reach stays one reviewable surface.</para>
///
/// <para><strong>The authoritative read, both rules and the write are one operation.</strong> The
/// status gate and the one-active-session rule are both re-checked inside issue #522's shared
/// <c>BEGIN IMMEDIATE</c> transaction, after the write lock is held, so a deactivation committing
/// while this start waits refuses it and two starts arriving together resolve to one session and one
/// <c>409</c>. The read before the transaction opens decides nothing: it answers "no such business"
/// with a <c>404</c> and refuses a stale view before a write lock is taken on the caller's behalf.
/// Nothing is audited until the session is committed.</para>
/// </summary>
public sealed class StartSupportSession
{
    private readonly IAuthenticatedActorAccessor _actorAccessor;
    private readonly IPlatformBusinessDirectoryStore _businesses;
    private readonly ISupportSessionStore _sessions;
    private readonly MembershipWriteGuard _guard;
    private readonly ISupportSessionAudit _audit;
    private readonly IClock _clock;

    public StartSupportSession(
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

    /// <summary>
    /// Starts a session for <paramref name="businessId"/>, recording
    /// <paramref name="reason"/>.
    /// </summary>
    /// <param name="businessId">
    /// The business to be read. It is the subject of the session, never a tenancy scope.
    /// </param>
    /// <param name="reason">
    /// Why the administrator is looking. Required, trimmed, and bounded by
    /// <see cref="SupportSessionPolicy.MaxReasonLength"/>.
    /// </param>
    /// <param name="cancellationToken">Cancellation for the whole operation, transaction included.</param>
    /// <returns>
    /// The started session, including the one and only copy of its id, or <see langword="null"/> when
    /// no such business exists.
    /// </returns>
    /// <exception cref="DomainValidationException">
    /// The reason is missing, blank or too long. Nothing is read, generated or written.
    /// </exception>
    /// <exception cref="BusinessAccessDeniedException">
    /// The request carries no identifiable actor, so there is nobody to grant the session to or hold
    /// to it.
    /// </exception>
    /// <exception cref="DomainConflictException">
    /// The business is not <see cref="BusinessStatus.Active"/>, or this administrator already has an
    /// active session. Nothing is committed in either case.
    /// </exception>
    public async Task<StartedSupportSession?> Handle(
        int businessId,
        string? reason,
        CancellationToken cancellationToken)
    {
        // First, before anything is read, generated or locked: an invalid request must not be able
        // to reach a business read, a session id or the audit trail.
        var recordedReason = SupportSessionPolicy.RequireReason(reason);

        var actor = _actorAccessor.GetCurrentActor().Actor
            ?? throw new BusinessAccessDeniedException(BusinessAccessDenialReason.NotAuthenticated);

        var existing = await _businesses.FindLifecycleTargetAsync(businessId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is null)
        {
            return null;
        }

        RequireActive(existing.Status);

        // URL-safe, so the whole id can travel in a header and a link without being re-encoded on
        // the way, and from the cryptographic generator rather than Random or a Guid: a guessable
        // session id is a way into somebody else's business.
        var sessionId = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(SupportSessionId.EntropyBytes));

        // The guard is here for its BEGIN IMMEDIATE transaction, which is the one write path this
        // application serialises state-dependent writes on (issue #522). No membership is created,
        // reactivated or revoked by a support session, so the intent handed to it is the empty one:
        // there is no identity for MembershipEligibility to be re-checked for, and no business whose
        // active Owner has to be retained.
        var intent = default(MembershipWriteIntent);

        var started = await _guard
            .ExecuteAsync(
                intent,
                async token =>
                {
                    var current = await _businesses.FindLifecycleTargetAsync(businessId, token)
                        .ConfigureAwait(false);
                    if (current is null)
                    {
                        // The business row stopped existing between the two reads. No path deletes
                        // one, so this is unreachable in practice; answering "not found" rather
                        // than starting a session against nothing is the fail-closed direction.
                        return null;
                    }

                    // Both rules, inside the lock, on what the database holds now - which is what
                    // makes a deactivation that committed while this start waited refuse it, and two
                    // simultaneous starts resolve to one session.
                    RequireActive(current.Status);

                    var active = await _sessions
                        .FindActiveForActorAsync(actor, _clock.UtcNow, token)
                        .ConfigureAwait(false);
                    if (active is not null)
                    {
                        throw new DomainConflictException(SupportSessionPolicy.AlreadyActiveMessage);
                    }

                    // Sampled inside the lock rather than when the request arrived, so the row
                    // records when the session really began rather than when the caller started
                    // waiting for it - and so the expiry is an hour of access, not an hour from
                    // whenever the queue cleared.
                    var startedAtUtc = _clock.UtcNow;

                    var stored = await _sessions
                        .StartAsync(
                            new NewSupportSession(
                                actor,
                                current.BusinessId,
                                recordedReason,
                                SupportSessionId.Hash(sessionId),
                                startedAtUtc,
                                SupportSessionPolicy.ExpiresAt(startedAtUtc)),
                            token)
                        .ConfigureAwait(false);

                    return new Started(stored, current.Name);
                },
                cancellationToken)
            .ConfigureAwait(false);

        if (started is null)
        {
            return null;
        }

        // After the commit, because a session that was not stored was not started. The event carries
        // the actor, the business, the reason and both instants, and never the session id.
        _audit.Record(SupportSessionAuditEntry.Started(started.Session));

        return new StartedSupportSession(
            sessionId,
            started.Session.BusinessId,
            started.BusinessName,
            started.Session.StartedAtUtc,
            started.Session.ExpiresAtUtc);
    }

    private static void RequireActive(BusinessStatus status)
    {
        if (!SupportSessionPolicy.CanStartFor(status))
        {
            throw new DomainConflictException(SupportSessionPolicy.BusinessNotActiveMessage);
        }
    }

    /// <summary>
    /// What the in-transaction write produced: the stored session, and the name of the business the
    /// same transaction read it against - so the response needs no second read afterwards.
    /// </summary>
    private sealed record Started(StoredSupportSession Session, string BusinessName);
}
