using Inventory.Domain.Exceptions;
using Inventory.Domain.Tenancy;

namespace Inventory.Application.PlatformAdmin;

/// <summary>
/// Switches one deactivated business back on (issue #545, one slice of #509 under #328): its status
/// becomes <see cref="BusinessStatus.Active"/> again, so the memberships that are still active start
/// resolving on their next request.
///
/// <para><strong>It requires at least one active Owner membership, and refuses otherwise.</strong>
/// Only an Owner may manage members, roles, the integration and the business record
/// (<c>RoleCapabilities</c>), so a trading business without one cannot be administered by anybody
/// inside it and has no repair path short of a human editing the database. The rule is
/// <see cref="BusinessOwnerRetention"/>'s, re-checked inside the write transaction on the
/// memberships the business actually holds, and a business that fails it is refused with
/// <see cref="BusinessLifecyclePolicy.NoActiveOwnerMessage"/> having changed nothing.</para>
///
/// <para><strong>It restores only the business.</strong> A membership released while the business
/// was off, or revoked by its Owner before that, stays inactive; a cancelled invite stays cancelled.
/// Both are deliberate decisions about a person, and a business returning is not a reason to undo
/// them - a released person may by now hold an active membership somewhere else, and reviving their
/// old one would deny them both businesses (<see cref="MembershipEligibility"/>).</para>
///
/// <para>The transaction, the status rule and the audit event are
/// <see cref="BusinessLifecycleWriter"/>'s, shared with the two actions beside it.</para>
/// </summary>
public sealed class ReactivateBusiness
{
    private readonly BusinessLifecycleWriter _writer;

    public ReactivateBusiness(BusinessLifecycleWriter writer)
    {
        _writer = writer;
    }

    /// <returns>
    /// The reactivation as it was recorded, or <see langword="null"/> when no such business exists.
    /// </returns>
    /// <exception cref="DomainConflictException">
    /// The business is not deactivated, or it has no active Owner membership. Nothing is committed in
    /// either case.
    /// </exception>
    public Task<PlatformBusinessReactivation?> Handle(int businessId, CancellationToken cancellationToken) =>
        _writer.ReactivateAsync(businessId, cancellationToken);
}
