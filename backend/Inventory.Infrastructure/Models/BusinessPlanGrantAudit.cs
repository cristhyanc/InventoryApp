using System.Text.Json.Serialization;
using Inventory.Application.PlatformPlans;
using Inventory.Domain.Subscriptions;

namespace Inventory.Infrastructure.Models;

/// <summary>
/// One decision a Super Admin made about one business's manual plan grant (issue #982): append-only
/// evidence of who granted, changed or ended what, and why.
///
/// <para><strong>It is written in the same transaction as the grant it is about</strong>, in the same
/// <c>SaveChanges</c>, so there is no moment at which a database holds a granted plan with no record
/// of who gave it or a record of a grant that was rolled back. That is the whole reason it is a row
/// here rather than only a log event: a log is delivered by a pipeline nobody can roll back, and a
/// plan somebody was given for a reason is exactly the kind of fact that has to be explainable
/// years later.</para>
///
/// <para><strong>Tenant-owned, with nothing added</strong>, like <see cref="BusinessPlanGrant"/>
/// itself: a decision is a fact about one business, so <c>AppDbContext</c> gives it the business key,
/// the index, the query filter and the ownership stamp. Nothing updates or deletes a row - there is
/// no edit path and no reversal path, because an audit trail that could be rewritten is not
/// one.</para>
///
/// <para><strong>What it deliberately does not carry.</strong> The business's name, its contact
/// address, anybody's display name or email address, and any part of a payment, subscription or
/// billing record. The actor's Entra <c>(tid, oid)</c> pair is an identifier rather than personal
/// detail and is what an investigation needs. The reason text is here, in the database, where it is
/// the evidence; it is deliberately absent from <see cref="PlatformPlanGrantAuditEntry"/>, which is
/// what reaches the host's log pipeline.</para>
/// </summary>
public class BusinessPlanGrantAudit : IBusinessOwned
{
    /// <summary>The row's own key, assigned by the database.</summary>
    public int Id { get; set; }

    /// <inheritdoc />
    [JsonIgnore]
    public int BusinessId { get; set; }

    /// <summary>
    /// Which kind of decision it was. Stated rather than inferred from the plans on either side: a
    /// change that kept the plan and moved the end date moved no plan at all, and an end leaves the
    /// plan exactly as it was.
    /// </summary>
    public required PlatformPlanGrantChange Change { get; set; }

    /// <summary>
    /// The plan the grant carried before this decision, or <c>null</c> when the business held no
    /// grant at all.
    /// </summary>
    public SubscriptionPlan? FromPlan { get; set; }

    /// <summary>
    /// The plan it carries now, or <c>null</c> when the grant was ended - which entitles nothing
    /// from that instant rather than granting <see cref="SubscriptionPlan.Free"/>.
    /// </summary>
    public SubscriptionPlan? ToPlan { get; set; }

    /// <summary>
    /// When the grant stops entitling after this decision, <strong>exclusive</strong>, or <c>null</c>
    /// for an open-ended grant - the same convention as <see cref="BusinessPlanGrant.EndsAtUtc"/>.
    /// </summary>
    public DateTime? EndsAtUtc { get; set; }

    /// <summary>
    /// Why the administrator decided it, as they typed it. Required: a grant is always given, changed
    /// or ended for a reason, and the reason is the half of this record a plan name cannot replace.
    /// </summary>
    public required string Reason { get; set; }

    /// <summary>The revision the write produced, so a trail can be read in the order it happened.</summary>
    public required int Revision { get; set; }

    /// <summary>The acting platform administrator's Entra <c>tid</c>.</summary>
    public required string ActorDirectoryTenantId { get; set; }

    /// <summary>Their Entra <c>oid</c>.</summary>
    public required string ActorObjectId { get; set; }

    /// <summary>When the decision was committed, from the application clock.</summary>
    public DateTime OccurredAtUtc { get; set; }
}
