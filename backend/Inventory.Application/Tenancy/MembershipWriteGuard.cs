using Inventory.Domain.Exceptions;
using Inventory.Domain.Tenancy;

namespace Inventory.Application.Tenancy;

/// <summary>
/// The one write path for a membership change (issue #522): it opens the SQLite
/// <c>BEGIN IMMEDIATE</c> write transaction, re-checks the membership rules inside it, runs the
/// caller's write, and commits only if the rules still hold afterwards.
///
/// Every operation that creates, reactivates or deactivates a membership, or changes a role, goes
/// through here - the invite redemption, reinstatement, revocation and role change of #504, the
/// self-service application of #507, and the approval and rejection of #509. The reason is
/// concurrency, and it is not theoretical: checking a rule and then writing in two steps is
/// exactly how two simultaneous requests both pass the check and both write. A person would end
/// up with an active membership in two businesses - which denies them both - or a business would
/// lose its last Owner because two Owners demoted each other at the same time. The authoritative
/// read, the rule and the write have to be one operation, and this is where that happens
/// (docs/architecture.md § One active membership per identity).
///
/// It deliberately writes no row itself. What belongs in a membership row for a creation, an
/// approval, a revocation or a role change is the operation's own business, and those operations
/// arrive in later tasks; what must be true around it is this class's business, and that is fixed.
/// The caller's write is therefore a delegate, saved inside the transaction this guard opened, and
/// rolled back with it when a rule refuses.
///
/// A refusal is a <see cref="DomainConflictException"/>, which the API boundary's
/// <c>DomainExceptionHandler</c> returns as <c>409 Conflict</c> with the message verbatim
/// (docs/architecture.md § Domain and application error mapping). The two messages are fixed
/// sentences from the Domain rules, so every endpoint that uses this path refuses the same way,
/// and neither message names another business, member or role.
/// </summary>
public sealed class MembershipWriteGuard
{
    private readonly IBusinessMembershipWriteStore _store;

    public MembershipWriteGuard(IBusinessMembershipWriteStore store)
    {
        _store = store;
    }

    /// <summary>
    /// Runs <paramref name="write"/> inside the write transaction, with the rules
    /// <paramref name="intent"/> declares re-checked around it.
    /// </summary>
    /// <param name="intent">What the write means; see <see cref="MembershipWriteIntent"/>.</param>
    /// <param name="write">
    /// The membership change. It must persist its own changes (its store's <c>SaveChangesAsync</c>)
    /// before returning, because the guard re-reads the business's memberships afterwards to judge
    /// the state the write left behind, and an unsaved change would leave it judging the old one.
    /// </param>
    /// <param name="cancellationToken">Cancellation for the whole operation, transaction included.</param>
    /// <returns>Whatever <paramref name="write"/> returned, once the change is committed.</returns>
    /// <exception cref="DomainConflictException">
    /// The identity already holds an active membership elsewhere (including the database refusing
    /// the write through the filtered unique index), or the change would leave the business with no
    /// active Owner. Nothing is committed in either case.
    /// </exception>
    public async Task<T> ExecuteAsync<T>(
        MembershipWriteIntent intent,
        Func<CancellationToken, Task<T>> write,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);

        if (intent.ExcludedMembershipId is not null && intent.IdentityGainingAnActiveMembership is null)
        {
            // A developer error rather than a caller's: an exclusion only ever applies to the
            // eligibility check, so an exclusion without an identity is a check that was meant to
            // run and will not. Refusing it outright is the only way that mistake is noticed.
            throw new ArgumentException(
                "A membership write that excludes a membership from the eligibility check must also "
                    + "state the identity gaining an active membership.",
                nameof(intent));
        }

        await using var transaction = await _store.BeginWriteTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        // Re-checked here, inside the write transaction, not wherever the caller last looked. The
        // write lock is already held, so this answer cannot be overtaken by another membership
        // write between the check and the commit.
        if (intent.IdentityGainingAnActiveMembership is { } identity)
        {
            var memberships = await _store
                .FindIdentityMembershipsAsync(identity, cancellationToken)
                .ConfigureAwait(false);

            if (!MembershipEligibility.AllowsActiveMembership(memberships, intent.ExcludedMembershipId))
            {
                throw new DomainConflictException(MembershipEligibility.AlreadyAMemberMessage);
            }
        }

        T result;
        try
        {
            result = await write(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (_store.IsDuplicateActiveMembership(exception))
        {
            // The index refusing the write, mapped to the same refusal the eligibility check
            // above produces. It is reachable even with that check in place - a database this
            // process did not serialise with, a membership row written by hand - and when it is
            // reached it must not surface as an unexplained 500.
            throw new DomainConflictException(MembershipEligibility.AlreadyAMemberMessage);
        }

        // Judged on the state the write left the business in, which is why it is read back rather
        // than predicted: a revocation, a demotion and anything a later operation does are all
        // covered without this class knowing what any of them are.
        if (intent.BusinessKeepingAnActiveOwner is { } businessId)
        {
            var memberships = await _store
                .FindBusinessMembershipRolesAsync(businessId, cancellationToken)
                .ConfigureAwait(false);

            if (!BusinessOwnerRetention.RetainsActiveOwner(memberships))
            {
                throw new DomainConflictException(BusinessOwnerRetention.LastActiveOwnerMessage);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// The same thing for a write that returns nothing - a revocation or a role change, where the
    /// changed row is not the operation's answer.
    /// </summary>
    public Task ExecuteAsync(
        MembershipWriteIntent intent,
        Func<CancellationToken, Task> write,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);

        return ExecuteAsync<object?>(
            intent,
            async token =>
            {
                await write(token).ConfigureAwait(false);
                return null;
            },
            cancellationToken);
    }
}
