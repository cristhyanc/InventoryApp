namespace Inventory.Domain.Subscriptions;

/// <summary>
/// Where a business's paid subscription stands with the billing provider (issue #966).
///
/// <para>Only <see cref="Active"/> entitles anything, and - exactly as
/// <see cref="Tenancy.BusinessStatus"/> does - no status is <c>0</c>, so an unfilled column and a
/// value a newer deployment wrote both fail closed. The complete set of provider statuses, and what
/// a dunning or trialling subscription means, is the billing integration's own work (#969); these
/// two are what the entitlement rule needs to be written and tested now.</para>
/// </summary>
public enum BillingSubscriptionStatus
{
    /// <summary>
    /// Paid and running. The plan applies for as long as the recorded period also covers the
    /// instant being asked about.
    /// </summary>
    Active = 10,

    /// <summary>
    /// Cancelled. It entitles nothing from the moment it is recorded, whatever its period says -
    /// "expired, cancelled, missing or unreadable → Free for paid features" (#965, decided 29
    /// October 2026). Whether a cancellation should instead run to the end of the paid period is an
    /// open launch decision on #965; until it is settled, the record that cancels is the record
    /// that stops entitling, and #969 decides what it writes into the period end.
    /// </summary>
    Cancelled = 20,
}

/// <summary>
/// Why a manual grant exists (issue #966). A manual grant is written by a Super Admin (#982) or by
/// the grandfathering step, and by nothing else - never by a billing event.
/// </summary>
public enum ManualPlanGrantSource
{
    /// <summary>
    /// The grant every business that was already trading when enforcement was first switched on
    /// receives: Pro, with no end date until the owner sets one. Written only by the idempotent
    /// grandfathering command, once.
    /// </summary>
    Grandfathered = 10,

    /// <summary>
    /// A complimentary grant a Super Admin decided (#982), with an optional end date and a recorded
    /// reason.
    /// </summary>
    Complimentary = 20,
}

/// <summary>
/// A half-open period of entitlement, in UTC instants (issue #966).
///
/// <para><strong>The start is inclusive and the end is exclusive</strong>, which is the same
/// convention the provider data coverage ranges of issue #730 use: a grant that runs to the end of
/// a month ends at midnight on the first of the next one, and the two periods of a plan change
/// neither overlap nor leave a gap. <see langword="null"/> is no recorded end - an open-ended grant,
/// which is what grandfathered Pro is until an owner sets one - and never "already over".</para>
/// </summary>
/// <param name="StartsAtUtc">The instant from which the record entitles, inclusive.</param>
/// <param name="EndsAtUtc">The instant at which it stops, exclusive, or <see langword="null"/>.</param>
public sealed record EntitlementPeriod(DateTime StartsAtUtc, DateTime? EndsAtUtc)
{
    /// <summary>
    /// Whether this period covers <paramref name="nowUtc"/>: at or after the start, and strictly
    /// before the end when there is one.
    /// </summary>
    public bool Covers(DateTime nowUtc) =>
        nowUtc >= StartsAtUtc && (EndsAtUtc is null || nowUtc < EndsAtUtc);

    /// <summary>
    /// Whether this period is well formed. An end at or before its start entitles nothing at any
    /// instant, so it is a record that cannot be read rather than a period: the policy ignores it
    /// instead of silently treating it as open-ended.
    /// </summary>
    public bool IsWellFormed => EndsAtUtc is null || EndsAtUtc > StartsAtUtc;
}

/// <summary>
/// The business's <strong>billing subscription</strong> as stored (issue #966): the record only a
/// verified billing event writes (#969).
///
/// <para>It is deliberately a separate record from <see cref="ManualPlanGrantState"/>, and neither
/// ever changes the other. A Super Admin granting Pro does not make a business a paying customer,
/// and a payment does not remove a complimentary grant; collapsing the two into one "current plan"
/// column is how a refund would silently revoke a grant somebody gave for a reason.</para>
/// </summary>
/// <param name="Plan">
/// The plan that was paid for. It may be a value this code does not declare - a newer deployment's
/// plan, read by an older one - which <see cref="EffectiveEntitlementPolicy"/> treats as a record it
/// cannot read.
/// </param>
/// <param name="Status">Where the subscription stands with the provider.</param>
/// <param name="Period">The paid period this record covers, half-open and in UTC.</param>
public sealed record BillingSubscriptionState(
    SubscriptionPlan Plan,
    BillingSubscriptionStatus Status,
    EntitlementPeriod Period);

/// <summary>
/// One <strong>manual grant</strong> as stored (issue #966): the record only a Super Admin (#982) or
/// the grandfathering step writes.
/// </summary>
/// <param name="Plan">
/// The granted plan, with the same "may be undeclared" caveat as a subscription's.
/// </param>
/// <param name="Source">Whether it is the grandfathered grant or a complimentary one.</param>
/// <param name="Period">
/// When it applies, half-open and in UTC. A grandfathered grant has no end until an owner sets one.
/// </param>
public sealed record ManualPlanGrantState(
    SubscriptionPlan Plan,
    ManualPlanGrantSource Source,
    EntitlementPeriod Period);
