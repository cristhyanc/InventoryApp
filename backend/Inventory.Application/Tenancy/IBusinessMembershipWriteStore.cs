using Inventory.Domain.Tenancy;

namespace Inventory.Application.Tenancy;

/// <summary>
/// The write transaction a membership change runs in (issue #522). Disposing it without
/// <see cref="CommitAsync"/> rolls back every change saved through the database connection it
/// shares since it began - the membership row the caller wrote included.
///
/// On SQLite it is a <c>BEGIN IMMEDIATE</c> transaction, so the write lock is taken when the
/// transaction opens rather than at its first write. That is what makes the rule re-checks inside
/// it meaningful: a second caller cannot read the same "no active membership" answer while this
/// one is deciding on it.
/// </summary>
public interface IBusinessMembershipWriteTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Narrow persistence port for the shared membership write path (issue #522). It owns no
/// membership rule and writes no membership row: it opens the write transaction, reads back the
/// two sets of rows the rules are decided from, and translates the one provider error that is
/// part of the contract.
///
/// Writing the row itself deliberately stays with the operation doing it -
/// <see cref="MembershipWriteGuard"/> takes the write as a delegate - because what a creation, a
/// reactivation, a revocation or a role change has to put in the row belongs to that operation
/// (#504, #507, #509), while the rules around it must not. The reads are on this port rather than
/// on <see cref="IBusinessMembershipStore"/> because they answer different questions: that port
/// resolves one actor's current business for a request, and these two read the membership state a
/// pending change is judged against.
/// </summary>
public interface IBusinessMembershipWriteStore
{
    /// <summary>
    /// Opens the write transaction every membership change runs in. The caller's own writes must
    /// be saved inside it, so the rule re-checks see them and a refusal rolls them back.
    /// </summary>
    Task<IBusinessMembershipWriteTransaction> BeginWriteTransactionAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Every membership recorded for one identity, in every business, active or revoked - what
    /// <see cref="MembershipEligibility"/> needs to be complete. It is not filtered by business
    /// state: a membership in a Pending or Deactivated business still occupies the identity's one
    /// active membership.
    /// </summary>
    Task<IReadOnlyList<IdentityMembership>> FindIdentityMembershipsAsync(
        ActorIdentity identity,
        CancellationToken cancellationToken);

    /// <summary>
    /// Every membership recorded for one business, active or revoked - what
    /// <see cref="BusinessOwnerRetention"/> needs. Read after the change so the rule is applied to
    /// the state the business is actually left in.
    /// </summary>
    Task<IReadOnlyList<BusinessMembershipRole>> FindBusinessMembershipRolesAsync(
        BusinessId businessId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Whether <paramref name="exception"/> is the database refusing a second active membership for
    /// one identity through the filtered unique index.
    ///
    /// It is answered by the adapter because the exception is provider-specific, and a provider's
    /// exception type must not cross into the Application layer (docs/architecture.md § Domain and
    /// application error mapping). The guard asks rather than catching, so the index violation
    /// becomes the same stable refusal as the eligibility check it backs up, while any other
    /// database failure keeps propagating untranslated.
    /// </summary>
    bool IsDuplicateActiveMembership(Exception exception);
}
