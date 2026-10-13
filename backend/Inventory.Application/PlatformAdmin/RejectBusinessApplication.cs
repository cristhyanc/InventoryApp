using Inventory.Domain.Exceptions;
using Inventory.Domain.Tenancy;

namespace Inventory.Application.PlatformAdmin;

/// <summary>
/// Refuses a business application (issue #544, one slice of #509 under #328): the business becomes
/// <see cref="BusinessStatus.Rejected"/> with the operator's optional reason and the decision
/// instant, and the applicant's membership in it is released - made inactive, with the same instant
/// as its state-change instant.
///
/// <para><strong>Releasing the membership is what lets the person apply again.</strong> While they
/// hold an active membership anywhere, <see cref="MembershipEligibility"/> refuses them a second
/// one, so a rejection that left the membership active would refuse the applicant their own next
/// application and leave them attached to a business nobody can reach. The rejected business row is
/// kept rather than deleted, and <c>GET /api/me/account-state</c> reports
/// <see cref="AccountState.ApplicationRejected"/> with the stored reason - to that applicant's own
/// identity and nobody else's.</para>
///
/// <para>The reason is validated by <see cref="BusinessDecisionPolicy"/> before anything is read or
/// written, so a reason nothing could store refuses the whole decision rather than being silently
/// truncated. The rules, the transaction and the audit event are
/// <see cref="BusinessApplicationDecisionWriter"/>'s, shared with the approval beside it.</para>
/// </summary>
public sealed class RejectBusinessApplication
{
    private readonly BusinessApplicationDecisionWriter _writer;

    public RejectBusinessApplication(BusinessApplicationDecisionWriter writer)
    {
        _writer = writer;
    }

    /// <param name="businessId">The application being refused.</param>
    /// <param name="reason">
    /// The operator's reason, which may be absent. A blank one is stored as "no reason", which has
    /// exactly one representation; a rejection with no reason is still a rejection.
    /// </param>
    /// <param name="cancellationToken">Cancellation for the whole operation.</param>
    /// <returns>
    /// The decision as it was recorded, or <see langword="null"/> when no such business exists.
    /// </returns>
    /// <exception cref="DomainValidationException">
    /// The reason is longer than <see cref="BusinessDecisionPolicy.MaxRejectionReasonLength"/>.
    /// Nothing is read or written.
    /// </exception>
    /// <exception cref="DomainConflictException">
    /// The application is no longer waiting for a decision. Nothing is committed.
    /// </exception>
    public Task<PlatformBusinessDecision?> Handle(
        int businessId,
        string? reason,
        CancellationToken cancellationToken) =>
        _writer.DecideAsync(
            businessId,
            approved: false,
            BusinessDecisionPolicy.ValidateRejectionReason(reason),
            cancellationToken);
}
