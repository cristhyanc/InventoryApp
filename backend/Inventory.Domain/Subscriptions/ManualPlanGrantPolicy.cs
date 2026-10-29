namespace Inventory.Domain.Subscriptions;

/// <summary>
/// What a Super Admin may decide about a business's <strong>manual grant</strong> (issue #982, child
/// of #965, after #966), and the one conversion between the end date they choose and the exclusive
/// end instant a grant is stored with.
///
/// <para>It is a pure set of rules: no clock, no database, no actor and no revision. The use case
/// that calls them supplies the instant and compares the revision, which is what keeps every
/// boundary below testable at the tick.</para>
///
/// <para><strong>What it deliberately does not decide.</strong> Which plan a business is
/// <em>entitled</em> to - that is <see cref="EffectiveEntitlementPolicy"/>, which reads this grant
/// beside the paid subscription and writes neither - and nothing at all about billing, a payment or
/// a subscription status. A manual grant is a plan somebody gave for a reason; it never makes a
/// business a paying customer and never reads the record that says one is.</para>
/// </summary>
public static class ManualPlanGrantPolicy
{
    /// <summary>The longest reason that may be recorded, matching the stored column's length.</summary>
    public const int MaxReasonLength = 500;

    /// <summary>
    /// The plans a Super Admin may grant, least entitled first.
    ///
    /// <para><see cref="SubscriptionPlan.Free"/> is deliberately absent: a business with no record
    /// at all is already on Free (<see cref="EffectiveEntitlementPolicy"/>), so a Free grant would be
    /// a row that entitles nothing while looking like a decision. Ending the grant is how a business
    /// returns to Free.</para>
    /// </summary>
    public static readonly IReadOnlyList<SubscriptionPlan> GrantablePlans =
    [
        SubscriptionPlan.Starter,
        SubscriptionPlan.Pro,
    ];

    /// <summary>The refusal for a plan that is not one of those two, or is not declared at all.</summary>
    public const string UnsupportedPlanMessage = "A manual grant must be for the Starter or the Pro plan.";

    /// <summary>The refusal for a missing or blank reason. A grant is always given for a reason.</summary>
    public const string ReasonRequiredMessage = "A reason is required for a manual plan grant.";

    /// <summary>The refusal for a reason longer than the stored column.</summary>
    public static readonly string ReasonTooLongMessage =
        $"A manual plan grant's reason must be {MaxReasonLength} characters or fewer.";

    /// <summary>The refusal for an end date that is already over.</summary>
    public const string EndDateInThePastMessage =
        "A manual plan grant's end date cannot be in the past.";

    /// <summary>
    /// The refusal for a write that carries a revision other than the stored one: another
    /// administrator - or this one's other tab - has changed the grant since it was read.
    /// </summary>
    public const string StaleRevisionMessage =
        "This business's plan grant has changed since it was read. Reload it and decide again.";

    /// <summary>The refusal for ending a grant the business does not currently hold.</summary>
    public const string NoGrantToEndMessage = "This business has no manual plan grant to end.";

    /// <summary>
    /// The refusal for a business that somehow holds more than one complimentary grant. This surface
    /// maintains exactly one and only ever updates it, so the state is unreachable through the
    /// application; refusing it is the fail-closed direction, because picking one of two active
    /// grants would quietly decide an entitlement nobody decided.
    /// </summary>
    public const string AmbiguousGrantMessage =
        "This business holds more than one manual plan grant, so none of them can be changed here.";

    /// <summary>Whether <paramref name="plan"/> is one a Super Admin may grant.</summary>
    public static bool IsGrantable(SubscriptionPlan plan) => GrantablePlans.Contains(plan);

    /// <summary>
    /// The reason as it is stored: trimmed, and <see langword="null"/> when there is nothing left -
    /// so whitespace is no reason rather than a 3-character one.
    /// </summary>
    public static string? NormaliseReason(string? reason)
    {
        var trimmed = reason?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    /// <summary>Whether <paramref name="reason"/> is longer than the stored column allows.</summary>
    public static bool IsReasonTooLong(string? reason) => reason?.Length > MaxReasonLength;

    /// <summary>
    /// The instant a grant that runs to the end of <paramref name="lastDayUtc"/> stops entitling -
    /// the <strong>exclusive</strong> end of <see cref="EntitlementPeriod"/>, which is midnight at
    /// the start of the following UTC day. A grant ending on 31 December therefore covers every
    /// moment of 31 December.
    /// </summary>
    public static DateTime ExclusiveEndOf(DateOnly lastDayUtc) =>
        DateTime.SpecifyKind(lastDayUtc.AddDays(1).ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);

    /// <summary>
    /// The same conversion read backwards: the last UTC day a grant ending at
    /// <paramref name="endsAtUtc"/> applied to, so the page an administrator edits on shows the day
    /// they picked rather than the exclusive instant underneath it.
    ///
    /// <para>Midnight is the previous day's end, which is what <see cref="ExclusiveEndOf"/> wrote.
    /// Any other instant is a grant that stopped <em>during</em> its own day - which is what ending
    /// one immediately produces - and that day is the last one it applied to.</para>
    /// </summary>
    public static DateOnly InclusiveLastDayOf(DateTime endsAtUtc)
    {
        var day = DateOnly.FromDateTime(endsAtUtc);
        return endsAtUtc.TimeOfDay == TimeSpan.Zero ? day.AddDays(-1) : day;
    }

    /// <summary>
    /// Whether a grant ending on <paramref name="lastDayUtc"/> still entitles anything at
    /// <paramref name="nowUtc"/> - the one check an end date has to pass. A date already over
    /// entitles nothing at any instant, so it is refused rather than stored as a grant that was
    /// dead when it was written.
    /// </summary>
    public static bool EndsAfter(DateOnly lastDayUtc, DateTime nowUtc) =>
        ExclusiveEndOf(lastDayUtc) > nowUtc;
}
