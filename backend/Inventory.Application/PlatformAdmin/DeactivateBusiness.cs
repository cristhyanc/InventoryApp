using Inventory.Domain.Exceptions;
using Inventory.Domain.Tenancy;

namespace Inventory.Application.PlatformAdmin;

/// <summary>
/// Switches one trading business off (issue #545, one slice of #509 under #328): its status becomes
/// <see cref="BusinessStatus.Deactivated"/>, which is not <see cref="BusinessStatus.Active"/>, so
/// nothing resolves it and every member of it is refused on their very next request.
///
/// <para><strong>Nothing is deleted, and no membership is touched.</strong> The business's records
/// and its memberships stay exactly as they are - an offboarded business fails closed rather than
/// being dropped - which is also what lets <see cref="ReactivateBusiness"/> put it back.</para>
///
/// <para><strong>Its live invites are cancelled permanently, in the same transaction.</strong> A
/// deactivated business whose outstanding invite links still worked would be a way back in after
/// the door was closed, and an invite cancelled by a deactivation that then rolled back would have
/// been withdrawn for nothing - so the two are one operation
/// (<c>IMemberInviteStore.CancelAllLiveAsync</c>, written for this caller in issue #531). Invites
/// that were already used, cancelled or expired are left alone, and a reactivation never brings a
/// cancelled one back.</para>
///
/// <para>The transaction, the status rule, the hook and the audit event are
/// <see cref="BusinessLifecycleWriter"/>'s, shared with the two actions beside it.</para>
/// </summary>
public sealed class DeactivateBusiness
{
    private readonly BusinessLifecycleWriter _writer;

    public DeactivateBusiness(BusinessLifecycleWriter writer)
    {
        _writer = writer;
    }

    /// <returns>
    /// The deactivation as it was recorded, or <see langword="null"/> when no such business exists.
    /// </returns>
    /// <exception cref="DomainConflictException">
    /// The business is not active, so there is nothing to switch off. Nothing is committed.
    /// </exception>
    public Task<PlatformBusinessDeactivation?> Handle(int businessId, CancellationToken cancellationToken) =>
        _writer.DeactivateAsync(businessId, cancellationToken);
}
