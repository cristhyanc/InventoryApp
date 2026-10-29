using System.Text.Json.Serialization;
using Inventory.Domain.Subscriptions;

namespace Inventory.Infrastructure.Models;

/// <summary>
/// A business's paid subscription (issue #966, under the agreed proposal of #965): the record that
/// only a verified billing event writes.
///
/// <para><strong>Tenant-owned, with nothing added.</strong> It implements
/// <see cref="IBusinessOwned"/>, so <c>AppDbContext</c> gives it the business key, the index, the
/// global query filter and the <c>SaveChanges</c> ownership stamp automatically - a subscription is
/// a fact about one business, and that is exactly what tenant ownership is for. At most one row per
/// business, which a unique index on the business key enforces rather than a convention.</para>
///
/// <para><strong>It is deliberately a different table from <see cref="BusinessPlanGrant"/>, and
/// neither row ever writes the other</strong> (decided 29 October 2026 on #965). A Super Admin
/// granting Pro does not make a business a paying customer and must not be able to look like one; a
/// refund or a failed payment must not silently revoke a grant somebody gave for a reason. One
/// "current plan" column could not keep those two facts apart, which is why there are two rows and
/// one rule - <see cref="EffectiveEntitlementPolicy"/> - that reads both and writes neither.</para>
///
/// <para><strong>Nothing writes this table yet.</strong> Issue #966 defines it, reads it and
/// publishes the entitlement it feeds; the checkout, renewals and connected-machine billing that
/// write it are issue #969's work, and the payment-provider identifiers that work needs are its own
/// additive columns. The grandfathering step of this issue writes grants and never a subscription,
/// and no endpoint writes either.</para>
/// </summary>
public class BusinessBillingSubscription : IBusinessOwned
{
    /// <summary>The row's own key, assigned by the database.</summary>
    public int Id { get; set; }

    /// <inheritdoc />
    [JsonIgnore]
    public int BusinessId { get; set; }

    /// <summary>
    /// The plan that was paid for. <c>required</c> and with no default, for the reason
    /// <see cref="Business.Status"/> has none: the one thing about a subscription that must never be
    /// decided by forgetting to decide it is which plan it entitles. The value <c>0</c> is declared
    /// by no plan and is read by <see cref="EffectiveEntitlementPolicy"/> as a record it cannot
    /// read - Free for paid features - rather than as a plan.
    /// </summary>
    public required SubscriptionPlan Plan { get; set; }

    /// <summary>
    /// Where the subscription stands with the billing provider. Only
    /// <see cref="BillingSubscriptionStatus.Active"/> entitles anything, and a value this code does
    /// not declare entitles nothing.
    /// </summary>
    public required BillingSubscriptionStatus Status { get; set; }

    /// <summary>
    /// The start of the paid period, inclusive, as a UTC instant.
    /// </summary>
    public DateTime CurrentPeriodStartUtc { get; set; }

    /// <summary>
    /// The end of the paid period, <strong>exclusive</strong>, or <c>null</c> when the provider has
    /// recorded no end. Exclusive so a renewal's new period neither overlaps the old one nor leaves
    /// a gap; see <see cref="EntitlementPeriod"/>.
    /// </summary>
    public DateTime? CurrentPeriodEndUtc { get; set; }

    /// <summary>When this row was last written, from the application clock.</summary>
    public DateTime UpdatedAtUtc { get; set; }
}
