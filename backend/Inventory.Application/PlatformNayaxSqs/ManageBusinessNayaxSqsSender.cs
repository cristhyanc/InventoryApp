using Inventory.Application.Tenancy;
using Inventory.Application.Time;
using Inventory.Domain.Exceptions;
using Inventory.Domain.NayaxSqs;
using Inventory.Domain.Tenancy;

namespace Inventory.Application.PlatformNayaxSqs;

/// <summary>
/// One business's Nayax SQS sender identity, as the Super Admin reads it (issue #1101).
/// </summary>
/// <param name="BusinessId">The business, from the stored row the read found.</param>
/// <param name="Sender">Its recorded sender, with the revision the next write must present.</param>
public sealed record BusinessNayaxSqsSenderView(
    int BusinessId,
    PlatformNayaxSqsSenderState Sender);

/// <summary>
/// The Super Admin's Nayax SQS sender identity on one business (issue #1101, child of #1100): read
/// it, and record, change, switch or remove it. Two operations over one record, and the whole of
/// what this surface may do.
///
/// <para><strong>Why only a Super Admin.</strong> The recorded AWS IAM user id is what tells the
/// queue reader of issue #1103 which business a message on the shared queue belongs to. A business
/// that could choose its own sender id could claim another business's sales, so this record is on no
/// member-facing surface at all - not to read and not to write.</para>
///
/// <para><strong>What it writes, and what it cannot.</strong> One <c>BusinessNayaxSqsSenders</c> row
/// of one business and one audit row beside it, through
/// <see cref="IPlatformNayaxSqsSenderStore"/>. No sale, stock, cost, purchase, product, machine,
/// site, document or report value is reachable here, no Nayax Lynx credential and no
/// <c>BusinessNayaxConnection</c> field, and <strong>nothing is ever deleted</strong>: "remove" is an
/// update that clears the sender id, switches the mapping off, increments the revision and appends an
/// audit row, so the trail of what was recorded survives and the id becomes free for another
/// business.</para>
///
/// <para><strong>The authoritative read, the comparison and the write are one operation.</strong>
/// Each decision opens the store's <c>BEGIN IMMEDIATE</c> transaction, re-reads the business row and
/// its sender <em>inside</em> it, checks the business is still trading, compares the revision the
/// caller presented against the stored one, writes the row and its audit row in one
/// <c>SaveChanges</c>, and commits. The read before the transaction decides nothing: it exists to
/// answer "no such business" without taking a write lock on the caller's behalf. A revision that is
/// not the stored one is refused with
/// <see cref="NayaxSqsSenderPolicy.StaleRevisionMessage"/> as a conflict, so two Super Admin tabs
/// cannot overwrite each other - the later one is told to reload rather than silently winning.</para>
///
/// <para><strong>Uniqueness is the database's answer, not a pre-check's.</strong> A sender id another
/// business already holds is refused by the global unique index and reported as
/// <see cref="NayaxSqsSenderPolicy.DuplicateSenderIdMessage"/>, which names no other business. There
/// is deliberately no "is this id taken?" read here: that would be a cross-business read from the
/// platform-admin surface, and the index answers the question without one.</para>
///
/// <para><strong>The business id is the subject of the decision, not a tenancy scope.</strong> The
/// request carries a denied <c>BusinessScope</c> throughout; the id is checked against the stored
/// business row inside the transaction, and the write is addressed to the key that row carries
/// rather than to the route value (AGENTS.md § Tenant ownership and data isolation).</para>
///
/// <para><strong>Who may reach it is decided at the boundary, and not here.</strong> The
/// platform-admin policy and its marker authorize the endpoints (class 2), the authorization
/// middleware and <c>BusinessScopeMiddleware</c> each evaluate that policy, a role preview is refused
/// by <c>RolePreviewMiddleware</c> like every other write, and a read-only support session is refused
/// by its own HTTP-method rule and again by <c>BusinessOwnershipEnforcer</c>. A business Owner,
/// Manager or Operator is refused for the one reason every Super Admin surface refuses them: a
/// business role is not platform authority.</para>
/// </summary>
public sealed class ManageBusinessNayaxSqsSender
{
    private readonly IAuthenticatedActorAccessor _actorAccessor;
    private readonly IPlatformNayaxSqsSenderStore _store;
    private readonly IPlatformNayaxSqsSenderAudit _audit;
    private readonly IClock _clock;

    public ManageBusinessNayaxSqsSender(
        IAuthenticatedActorAccessor actorAccessor,
        IPlatformNayaxSqsSenderStore store,
        IPlatformNayaxSqsSenderAudit audit,
        IClock clock)
    {
        _actorAccessor = actorAccessor;
        _store = store;
        _audit = audit;
        _clock = clock;
    }

    /// <summary>
    /// One business's recorded sender as it stands, with the revision a write has to present.
    /// </summary>
    /// <returns>
    /// The recorded sender, or <see langword="null"/> when no business has that key <em>or</em> no
    /// row has ever existed for it - both of which the endpoint answers as <c>404</c>, because
    /// "nothing is recorded" is the absence of a resource rather than an empty one.
    /// </returns>
    public async Task<BusinessNayaxSqsSenderView?> GetAsync(
        int businessId,
        CancellationToken cancellationToken)
    {
        var target = await _store.FindAsync(businessId, cancellationToken).ConfigureAwait(false);

        return target?.Sender is null ? null : new BusinessNayaxSqsSenderView(target.BusinessId, target.Sender);
    }

    /// <summary>
    /// Records the business's sender, or changes, switches or removes the one it holds.
    /// </summary>
    /// <param name="businessId">The business being recorded - the subject of the decision.</param>
    /// <param name="awsSenderId">
    /// The AWS IAM user id, trimmed and otherwise stored exactly as entered, or
    /// <see langword="null"/>/blank to remove it. An administrator reads the value with
    /// <c>aws iam get-user --user-name &lt;user&gt; --query User.UserId</c>.
    /// </param>
    /// <param name="isEnabled">
    /// Whether the mapping is in force. <see langword="true"/> with no sender id is refused, because
    /// a mapping switched on with nothing to match on would claim the queue is attributed when it is
    /// not.
    /// </param>
    /// <param name="expectedRevision">
    /// The revision the caller read, and <see langword="null"/> to create the first row. Anything
    /// else is a stale view and is refused - including <see langword="null"/> when a row already
    /// exists, and a number when none does.
    /// </param>
    /// <param name="cancellationToken">Cancellation for the whole operation, transaction included.</param>
    /// <returns>
    /// The sender as it was written, or <see langword="null"/> when no such business exists.
    /// </returns>
    /// <exception cref="DomainValidationException">
    /// The sender id is not 16 to 128 letters and digits, or the mapping was switched on with no
    /// sender id. Nothing is read, locked or written.
    /// </exception>
    /// <exception cref="DomainConflictException">
    /// The revision is not the stored one, the business is not active, or the sender id is already
    /// recorded for another business. Nothing is written in any of the three cases.
    /// </exception>
    public async Task<BusinessNayaxSqsSenderView?> SetAsync(
        int businessId,
        string? awsSenderId,
        bool isEnabled,
        int? expectedRevision,
        CancellationToken cancellationToken)
    {
        // Checked here, before anything is read or locked: an invalid request must not be able to
        // reach a business read, a write lock or the audit trail.
        var senderId = NayaxSqsSenderPolicy.NormaliseSenderId(awsSenderId);
        if (senderId is not null && !NayaxSqsSenderPolicy.IsWellFormedSenderId(senderId))
        {
            throw new DomainValidationException(NayaxSqsSenderPolicy.InvalidSenderIdMessage);
        }

        if (isEnabled && senderId is null)
        {
            throw new DomainValidationException(NayaxSqsSenderPolicy.EnabledNeedsSenderIdMessage);
        }

        var actor = RequireActor();

        var existing = await _store.FindAsync(businessId, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return null;
        }

        BusinessNayaxSqsSenderView view;
        PlatformNayaxSqsSenderWrite write;

        await using (var transaction = await _store
            .BeginWriteTransactionAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            // Re-read with the write lock held. This is the read every rule below is decided on: the
            // business row proves the route value still names a trading business, and the sender row
            // carries the revision the caller's view is compared against.
            var current = await _store.FindAsync(businessId, cancellationToken).ConfigureAwait(false);
            if (current is null)
            {
                // The business row stopped existing between the two reads. No path deletes one, so
                // this is unreachable in practice; answering "not found" is the fail-closed
                // direction.
                return null;
            }

            if (!ActiveBusinessWritePolicy.AllowsWrite(current.Status))
            {
                // The shared predicate, with this surface's own sentence: a platform administrator
                // is told the business is not active, which is a fact their own directory already
                // reports to them.
                throw new DomainConflictException(NayaxSqsSenderPolicy.BusinessNotActiveMessage);
            }

            var sender = current.Sender;

            // The whole of the guard, in one comparison. `null` presented against an existing row,
            // a number presented against no row, and a number that is not the stored one are all the
            // same answer: the caller is deciding against a state that is not the stored one.
            if (expectedRevision != sender?.Revision)
            {
                throw new DomainConflictException(NayaxSqsSenderPolicy.StaleRevisionMessage);
            }

            var occurredAtUtc = _clock.UtcNow;

            write = new PlatformNayaxSqsSenderWrite(
                // The key of the row this transaction read, never the route value: that is what
                // makes the id a subject rather than something a request chose.
                current.BusinessId,
                senderId,
                isEnabled,
                Revision: (sender?.Revision ?? 0) + 1,
                FromAwsSenderId: sender?.AwsSenderId,
                FromIsEnabled: sender?.IsEnabled ?? false,
                actor,
                occurredAtUtc);

            await _store.SaveAsync(write, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            view = new BusinessNayaxSqsSenderView(
                current.BusinessId,
                new PlatformNayaxSqsSenderState(
                    senderId,
                    isEnabled,
                    // Reported from the stored row rather than written: when a message was last
                    // received is issue #1103's fact and this decision changes none of it.
                    sender?.LastMessageReceivedAtUtc,
                    write.Revision,
                    CreatedAtUtc: sender?.CreatedAtUtc ?? occurredAtUtc,
                    UpdatedAtUtc: occurredAtUtc));

            _audit.Record(new PlatformNayaxSqsSenderAuditEntry(
                actor.DirectoryTenantId,
                actor.ObjectId,
                current.BusinessId,
                HadSenderId: write.FromAwsSenderId is not null,
                HasSenderId: senderId is not null,
                WasEnabled: write.FromIsEnabled,
                IsEnabled: isEnabled,
                write.Revision,
                occurredAtUtc));
        }

        return view;
    }

    /// <summary>
    /// Read before anything is written, so a sender can never be committed for a request whose actor
    /// the audit trail could not name. The policy at the boundary has already established that this
    /// is the configured platform administrator; this is who they are.
    /// </summary>
    private ActorIdentity RequireActor() =>
        _actorAccessor.GetCurrentActor().Actor
            ?? throw new BusinessAccessDeniedException(BusinessAccessDenialReason.NotAuthenticated);
}
