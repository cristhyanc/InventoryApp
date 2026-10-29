using Inventory.Domain.Subscriptions;

namespace Inventory.Application.Subscriptions;

/// <summary>
/// The narrow persistence port over a business's <strong>billing subscription</strong> (issue #966),
/// owned by the Application layer.
///
/// <para><strong>One record per business, and this task only reads it.</strong> The record is
/// written exclusively by verified billing events (#969); nothing in this slice, no endpoint and no
/// Super Admin action writes a subscription, so there is no write method here to be reached by
/// mistake. Issue #982's Super Admin writes the separate manual grant
/// (<see cref="IManualPlanGrantStore"/>) and never this - "two separate records, neither ever
/// changes the other".</para>
///
/// <para><strong>The business is never a parameter.</strong> The read is for the business the
/// request already resolved, through the ordinary tenant query filters, so no route, query, body or
/// header value can choose whose subscription is reported (AGENTS.md § Tenant ownership and data
/// isolation). Inside a support session the resolved business is the one being viewed, which is why
/// the entitlement a session reports is the viewed business's own. No method takes a predicate, a
/// column list, a page or a sort.</para>
/// </summary>
public interface IBillingSubscriptionStore
{
    /// <summary>
    /// The current business's billing subscription, or <see langword="null"/> when it has none -
    /// which is every business today, and every business that has never paid.
    ///
    /// <para>The adapter reports the row as stored and interprets nothing: whether a status, a plan
    /// or a period entitles anything is <see cref="EffectiveEntitlementPolicy"/>'s decision, in one
    /// place.</para>
    /// </summary>
    Task<BillingSubscriptionState?> FindForCurrentBusinessAsync(CancellationToken cancellationToken);
}
