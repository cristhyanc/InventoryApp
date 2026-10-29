namespace Inventory.Domain.Subscriptions;

/// <summary>
/// Why a business is entitled to what it is entitled to (issue #966). It is reported beside the
/// plan so an answer is never a bare plan name whose provenance a reader has to guess at.
/// </summary>
public enum EntitlementSource
{
    /// <summary>
    /// The rollout setting is off, which is the shipped state: every business is unrestricted and
    /// behaves exactly as it does today. No plan is reported at all, because there is none to
    /// report - not because the business is on Pro.
    /// </summary>
    EnforcementDisabled = 10,

    /// <summary>
    /// The rollout setting is on but the grandfathering step has not recorded a completion marker.
    /// Every business is unrestricted, and the application logs a startup error naming the missing
    /// step: enforcing plans before the businesses that were already trading hold their grant would
    /// downgrade every one of them at once.
    /// </summary>
    GrandfatheringNotCompleted = 20,

    /// <summary>The effective plan comes from an active manual grant (#982, or grandfathering).</summary>
    ManualGrant = 30,

    /// <summary>The effective plan comes from an active paid subscription (#969).</summary>
    BillingSubscription = 40,

    /// <summary>
    /// Neither record entitles anything right now - there is none, it has expired, it was cancelled,
    /// or it cannot be read - so the business is on <see cref="SubscriptionPlan.Free"/> for paid
    /// features. Free's manual features are never locked.
    /// </summary>
    NoActiveRecord = 50,
}

/// <summary>
/// What one business may currently use (issue #966), as <c>GET /api/me/access</c> publishes it.
/// </summary>
/// <param name="Enforced">
/// Whether plan entitlement applies at all. <see langword="false"/> is today's behaviour -
/// unrestricted - and is the shipped state; a client that reads <see langword="false"/> offers
/// everything, exactly as it does now.
/// </param>
/// <param name="Plan">
/// The effective plan while <paramref name="Enforced"/> is <see langword="true"/>, and
/// <see langword="null"/> while it is not. Deliberately null rather than
/// <see cref="SubscriptionPlan.Pro"/>: "unrestricted" is not a plan anybody is on, and reporting
/// Free there would lock a business that nothing is enforcing anything for.
/// </param>
/// <param name="Source">Where <paramref name="Plan"/> came from, or why there is none.</param>
/// <param name="EndsAtUtc">
/// When the record the plan came from stops entitling it, exclusive, or <see langword="null"/> when
/// it has no recorded end - which is what a grandfathered grant has until an owner sets one. It is
/// the end of the one record that decided the plan, never a combination of both records' dates.
/// </param>
public sealed record EffectiveEntitlement(
    bool Enforced,
    SubscriptionPlan? Plan,
    EntitlementSource Source,
    DateTime? EndsAtUtc);

/// <summary>
/// The single effective-entitlement rule (issue #966, states decided 29 October 2026 on #965).
///
/// <para>It is a pure function of the rollout switch, the grandfathering marker, the business's two
/// records and the current instant: no clock of its own, no database, no configuration and no
/// caller identity. Everything that decides what a business may use is therefore one table of cases
/// that can be - and is - tested row by row.</para>
///
/// <para><strong>The order of the rules is the design.</strong>
/// <list type="number">
///   <item>The rollout setting off → unrestricted, whatever either record says. Nothing is read, so
///   a half-written record cannot change today's behaviour.</item>
///   <item>On, but no grandfathering completion marker → still unrestricted. Enforcement cannot
///   start before the businesses that were already trading hold their grant.</item>
///   <item>Otherwise the effective plan is <strong>the higher of</strong> the active manual grant and
///   the active paid plan (<see cref="SubscriptionPlans.Higher"/>). Neither record changes the
///   other, and neither is consulted to validate the other.</item>
///   <item>Nothing active → <see cref="SubscriptionPlan.Free"/>. An expired period, a cancelled
///   subscription, a missing record and a record this code cannot read all land here, which is the
///   fail-closed direction for a <em>paid</em> feature. Free's manual features are never locked, so
///   failing closed here withholds nothing a business was doing before plans existed.</item>
/// </list></para>
///
/// <para><strong>Unreadable means ignored, never guessed.</strong> A plan or status value this code
/// does not declare, and a period whose end is at or before its start, make that record unusable;
/// the policy drops it and reads the other one. It never substitutes the nearest plan it knows or
/// treats a malformed period as open-ended.</para>
///
/// <para><strong>Times are UTC instants, and the period is half-open</strong>
/// (<see cref="EntitlementPeriod"/>): a record entitles from its start instant inclusive and stops
/// at its end instant exclusive. The instant itself comes from the application clock through the
/// use case that calls this, never from here.</para>
///
/// <para>A support session and a role preview are deliberately absent from this signature. A session
/// resolves the viewed business, so the records read are that business's own and the answer is its
/// own plan (issue #551 makes the scope that business); a role preview narrows role capabilities and
/// has nothing to say about what the business bought. Neither can reach this rule to change it.</para>
/// </summary>
public static class EffectiveEntitlementPolicy
{
    /// <summary>The entitlement every business has while nothing is enforced.</summary>
    public static EffectiveEntitlement Unrestricted(EntitlementSource reason) =>
        new(Enforced: false, Plan: null, reason, EndsAtUtc: null);

    /// <summary>
    /// Decides the effective entitlement.
    /// </summary>
    /// <param name="enforcementEnabled">
    /// The rollout setting (<c>Subscriptions:EnforcementEnabled</c>), read at startup. Blank,
    /// absent and unparseable are all off, which the configuration adapter decides, not this rule.
    /// </param>
    /// <param name="grandfatheringCompleted">
    /// Whether the grandfathering step has recorded its completion marker.
    /// </param>
    /// <param name="manualGrants">
    /// Every manual grant the business holds, in any order. The highest-plan active one decides;
    /// grants that have ended, have not started, or cannot be read are ignored.
    /// </param>
    /// <param name="billingSubscription">
    /// The business's billing subscription, or <see langword="null"/> when it has none.
    /// </param>
    /// <param name="nowUtc">The instant being asked about, from the application clock.</param>
    public static EffectiveEntitlement Decide(
        bool enforcementEnabled,
        bool grandfatheringCompleted,
        IReadOnlyList<ManualPlanGrantState> manualGrants,
        BillingSubscriptionState? billingSubscription,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(manualGrants);

        if (!enforcementEnabled)
        {
            return Unrestricted(EntitlementSource.EnforcementDisabled);
        }

        if (!grandfatheringCompleted)
        {
            return Unrestricted(EntitlementSource.GrandfatheringNotCompleted);
        }

        var grant = ActiveGrant(manualGrants, nowUtc);
        var paid = ActiveSubscription(billingSubscription, nowUtc);

        return (grant, paid) switch
        {
            (null, null) => new EffectiveEntitlement(
                Enforced: true,
                SubscriptionPlan.Free,
                EntitlementSource.NoActiveRecord,
                EndsAtUtc: null),
            (not null, null) => FromGrant(grant),
            (null, not null) => FromSubscription(paid, billingSubscription!.Period),
            _ => Better(grant, paid, billingSubscription!.Period),
        };
    }

    /// <summary>
    /// The entitlement both records could support, and which of them is reported.
    ///
    /// <para>The <em>plan</em> is simply the higher of the two. Which record is reported as the
    /// source matters only when the plans are equal, and then the one that entitles for longer is
    /// reported: no recorded end beats any end, and a later end beats an earlier one. That is what
    /// makes <see cref="EffectiveEntitlement.EndsAtUtc"/> answerable - a grandfathered Pro grant
    /// with no end date beside a Pro subscription ending next week keeps Pro after next week, so
    /// reporting the subscription's end would state the opposite. A genuine tie - equal plans, equal
    /// ends - reports the paid subscription, because that is the record the business is paying
    /// for.</para>
    /// </summary>
    private static EffectiveEntitlement Better(
        ManualPlanGrantState grant,
        BillingSubscriptionState paid,
        EntitlementPeriod paidPeriod)
    {
        if (SubscriptionPlans.Higher(grant.Plan, paid.Plan) != grant.Plan)
        {
            return FromSubscription(paid, paidPeriod);
        }

        if (grant.Plan != paid.Plan)
        {
            return FromGrant(grant);
        }

        return EntitlesForLonger(grant.Period, paidPeriod)
            ? FromGrant(grant)
            : FromSubscription(paid, paidPeriod);
    }

    /// <summary>
    /// Whether <paramref name="first"/> entitles strictly longer than <paramref name="second"/>. An
    /// open-ended period does; otherwise the later end does; an equal end does not, which is what
    /// makes the paid record win a genuine tie.
    /// </summary>
    private static bool EntitlesForLonger(EntitlementPeriod first, EntitlementPeriod second) =>
        (first.EndsAtUtc, second.EndsAtUtc) switch
        {
            (null, null) => false,
            (null, not null) => true,
            (not null, null) => false,
            var (mine, theirs) => mine > theirs,
        };

    private static EffectiveEntitlement FromGrant(ManualPlanGrantState grant) =>
        new(Enforced: true, grant.Plan, EntitlementSource.ManualGrant, grant.Period.EndsAtUtc);

    private static EffectiveEntitlement FromSubscription(
        BillingSubscriptionState paid,
        EntitlementPeriod period) =>
        new(Enforced: true, paid.Plan, EntitlementSource.BillingSubscription, period.EndsAtUtc);

    /// <summary>
    /// The highest-plan grant that is readable and covers <paramref name="nowUtc"/>, or
    /// <see langword="null"/>. Between two active grants of the same plan the one that entitles for
    /// longer is kept, so a complimentary grant cannot shorten an open-ended grandfathered one.
    /// </summary>
    private static ManualPlanGrantState? ActiveGrant(
        IReadOnlyList<ManualPlanGrantState> grants,
        DateTime nowUtc)
    {
        ManualPlanGrantState? best = null;

        foreach (var grant in grants)
        {
            if (!IsReadable(grant.Plan, grant.Period) || !grant.Period.Covers(nowUtc))
            {
                continue;
            }

            if (best is null
                || SubscriptionPlans.Higher(grant.Plan, best.Plan) == grant.Plan
                    && (grant.Plan != best.Plan || EntitlesForLonger(grant.Period, best.Period)))
            {
                best = grant;
            }
        }

        return best;
    }

    /// <summary>
    /// The subscription if it is readable, <see cref="BillingSubscriptionStatus.Active"/> and covers
    /// <paramref name="nowUtc"/>; otherwise <see langword="null"/>.
    /// </summary>
    private static BillingSubscriptionState? ActiveSubscription(
        BillingSubscriptionState? subscription,
        DateTime nowUtc)
    {
        if (subscription is null
            || !IsReadable(subscription.Plan, subscription.Period)
            || subscription.Status != BillingSubscriptionStatus.Active
            || !subscription.Period.Covers(nowUtc))
        {
            return null;
        }

        return subscription;
    }

    private static bool IsReadable(SubscriptionPlan plan, EntitlementPeriod period) =>
        SubscriptionPlans.IsDeclared(plan) && period.IsWellFormed;
}
