using Inventory.Domain.Subscriptions;

namespace Inventory.Application.Subscriptions;

/// <summary>
/// The narrow persistence port over a business's <strong>manual grants</strong> (issue #966), owned
/// by the Application layer.
///
/// <para><strong>Two writers, and neither is a billing event.</strong> A grant is written by a Super
/// Admin (#982) or by the grandfathering step of this issue, and by nothing else. The billing
/// subscription beside it (<see cref="IBillingSubscriptionStore"/>) is written only by verified
/// billing events, and neither record ever changes the other: the one write this port offers today
/// is the grandfathering grant, and it only ever <em>adds</em> a grant that is missing.</para>
///
/// <para><strong>The business is never a parameter</strong>, for either method. Both act on the
/// business the caller's scope already resolved, through the ordinary tenant query filters and the
/// central ownership stamp, so nothing a request or a command line carries can choose whose grants
/// are read or written (AGENTS.md § Tenant ownership and data isolation). No method takes a
/// predicate, a column list, a page or a sort.</para>
/// </summary>
public interface IManualPlanGrantStore
{
    /// <summary>
    /// Every manual grant the current business holds, as stored and in no meaningful order.
    ///
    /// <para>All of them, not "the active one": which grant applies at an instant is
    /// <see cref="EffectiveEntitlementPolicy"/>'s decision, and a store that filtered by date would
    /// be a second place where the half-open period rule lives. A business holds at most a handful
    /// of these - one grandfathered grant and the complimentary ones a Super Admin decided.</para>
    /// </summary>
    Task<IReadOnlyList<ManualPlanGrantState>> ListForCurrentBusinessAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Grants the current business the grandfathered plan, unless it already holds a grandfathered
    /// grant - the one write the grandfathering step of issue #966 makes, and the whole of what
    /// makes that step safe to rerun.
    ///
    /// <para>It is <strong>one transaction</strong>: the check for an existing grandfathered grant
    /// and the insert are one operation, so two runs cannot both find none and both insert. A
    /// business that already holds one is left exactly as it is - the plan, the start, an end date an
    /// owner has since set and the reason are not rewritten - because a rerun must change nothing.
    /// It never writes a complimentary grant, never removes or edits a grant, and never touches the
    /// billing subscription.</para>
    /// </summary>
    /// <param name="startsAtUtc">
    /// The instant the grant applies from, inclusive: the recorded grandfathering cutoff, so every
    /// business granted by the first run and by a resumed one is entitled from the same instant.
    /// </param>
    /// <param name="grantedAtUtc">When the grant was written, from the application clock.</param>
    /// <param name="cancellationToken">The command's own token.</param>
    /// <returns>
    /// <see langword="true"/> when this call wrote the grant, and <see langword="false"/> when the
    /// business already held one - which is what lets a rerun report that it changed nothing.
    /// </returns>
    Task<bool> GrantGrandfatheredPlanIfMissingAsync(
        DateTime startsAtUtc,
        DateTime grantedAtUtc,
        CancellationToken cancellationToken);
}
