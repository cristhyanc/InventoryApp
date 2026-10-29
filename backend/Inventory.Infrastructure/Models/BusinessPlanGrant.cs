using System.Text.Json.Serialization;
using Inventory.Domain.Subscriptions;

namespace Inventory.Infrastructure.Models;

/// <summary>
/// A plan granted to a business by a person rather than by a payment (issue #966): the
/// grandfathered Pro grant every established business receives when enforcement is first switched
/// on, and the complimentary grants a Super Admin decides (#982).
///
/// <para><strong>Tenant-owned, with nothing added</strong>, exactly as
/// <see cref="BusinessBillingSubscription"/> is: a grant is a fact about one business, so the
/// central mechanism gives it the business key, the index, the query filter and the ownership stamp.
/// Unlike the subscription there may be several rows - a grandfathered grant and the complimentary
/// ones that followed - and the one uniqueness rule is that a business holds <strong>at most one
/// grandfathered grant</strong>, which a filtered unique index enforces. That index is what makes
/// the grandfathering command safe to rerun even if two of them ran at once.</para>
///
/// <para><strong>Written by two paths, and never by a billing event.</strong> The grandfathering
/// command of this issue writes the grandfathered row; the Super Admin surface of #982 writes a
/// complimentary one. Neither path touches the billing subscription, and no billing event touches
/// this table. A grant is never edited by the grandfathering step: a rerun that finds one leaves its
/// plan, its start, an end date an owner has since set and its source exactly as they are.</para>
/// </summary>
public class BusinessPlanGrant : IBusinessOwned
{
    /// <summary>The row's own key, assigned by the database.</summary>
    public int Id { get; set; }

    /// <inheritdoc />
    [JsonIgnore]
    public int BusinessId { get; set; }

    /// <summary>
    /// The granted plan. <c>required</c> and with no default, for the reason the subscription's is:
    /// an undeclared value - <c>0</c> included - is read as a record that cannot be read rather than
    /// as a plan.
    /// </summary>
    public required SubscriptionPlan Plan { get; set; }

    /// <summary>
    /// Why the grant exists: the grandfathering step, or a Super Admin's complimentary decision. It
    /// is also what the grandfathering command identifies its own row by, so a business that has a
    /// complimentary grant is still granted the grandfathered one it is entitled to, and a business
    /// that already has the grandfathered one is left alone.
    /// </summary>
    public required ManualPlanGrantSource Source { get; set; }

    /// <summary>
    /// The instant the grant applies from, inclusive. The grandfathering step writes the recorded
    /// cutoff, so every business it grants - on the first run, or on a resumed one days later - is
    /// entitled from the same instant.
    /// </summary>
    public DateTime StartsAtUtc { get; set; }

    /// <summary>
    /// The instant it stops applying, <strong>exclusive</strong>, or <c>null</c> for no recorded
    /// end - which is what a grandfathered grant has until an owner sets one, and what a
    /// complimentary grant has when a Super Admin sets none (#982). <c>null</c> is never read as
    /// "already over"; see <see cref="EntitlementPeriod"/>.
    /// </summary>
    public DateTime? EndsAtUtc { get; set; }

    /// <summary>
    /// Why a Super Admin granted it, as they typed it (#982), and <c>null</c> for the grandfathered
    /// grant - whose <see cref="Source"/> is the whole reason, recorded in a column rather than as
    /// prose nobody can query.
    /// </summary>
    public string? Reason { get; set; }

    /// <summary>When the grant was written, from the application clock.</summary>
    public DateTime GrantedAtUtc { get; set; }
}
