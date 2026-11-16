namespace Inventory.Domain.FinancialConfiguration;

/// <summary>Why an agreement may not be ended on the submitted date (issue #1254).</summary>
public enum SiteCommissionAgreementEndRefusal
{
    /// <summary>The end date is allowed.</summary>
    None = 0,

    /// <summary>The end date falls before the agreement's own start date.</summary>
    BeforeStart = 1,

    /// <summary>The agreement already ends earlier, and ending is never an extension.</summary>
    LaterThanExistingEnd = 2,
}

/// <summary>
/// The one rule that decides whether a site commission agreement may be ended on a date
/// (issue #1254).
///
/// <para><strong>Ending only ever narrows.</strong> An open-ended agreement gains an end date, and
/// an agreement that already carries one may be brought forward but never pushed out. That is what
/// makes the action safe for the role that may already add an agreement: every date the agreement
/// used to cover either still belongs to it or belongs to no agreement at all, so no sale quietly
/// moves onto a different rate, and the narrowing itself cannot create the overlap
/// <c>SaveSiteCommissionAgreement</c> refuses.</para>
///
/// <para>Both comparisons are by calendar date, because an agreement is effective-dated by date
/// rather than by instant (see <see cref="EffectiveFinancialConfiguration.ResolveAgreement"/>): 30
/// June at 23:59 and 30 June at midnight are the same end date.</para>
///
/// <para>Correcting an agreement's rate or start date is deliberately not here, and is not an
/// end-date rule: it would rewrite commission already reported for periods that have been paid, and
/// the repository owner's decision of 14 November 2026 keeps it out.</para>
/// </summary>
public static class SiteCommissionAgreementEndPolicy
{
    /// <summary>
    /// The refusal for an end date before the agreement's start. Published verbatim as the
    /// <c>400</c> response body, so it describes the dates and names no business, site or rate.
    /// </summary>
    public const string BeforeStartMessage =
        "The end date must be on or after the agreement's start date.";

    /// <summary>
    /// The refusal for an end date later than the one the agreement already carries. Published
    /// verbatim as the <c>400</c> response body, for the same reason.
    /// </summary>
    public const string LaterThanExistingEndMessage =
        "This agreement already ends earlier, and an agreement can only be ended sooner, never extended.";

    /// <summary>
    /// Whether the agreement starting on <paramref name="effectiveFrom"/> and currently ending on
    /// <paramref name="effectiveTo"/> (null when it is open-ended) may be ended on
    /// <paramref name="endDate"/>.
    /// </summary>
    public static SiteCommissionAgreementEndRefusal Decide(
        DateTime effectiveFrom,
        DateTime? effectiveTo,
        DateTime endDate)
    {
        if (endDate.Date < effectiveFrom.Date)
        {
            return SiteCommissionAgreementEndRefusal.BeforeStart;
        }

        if (effectiveTo.HasValue && endDate.Date > effectiveTo.Value.Date)
        {
            return SiteCommissionAgreementEndRefusal.LaterThanExistingEnd;
        }

        return SiteCommissionAgreementEndRefusal.None;
    }

    /// <summary>
    /// The caller-safe sentence for a refusal, and <see langword="null"/> for
    /// <see cref="SiteCommissionAgreementEndRefusal.None"/>.
    /// </summary>
    public static string? Message(SiteCommissionAgreementEndRefusal refusal) => refusal switch
    {
        SiteCommissionAgreementEndRefusal.BeforeStart => BeforeStartMessage,
        SiteCommissionAgreementEndRefusal.LaterThanExistingEnd => LaterThanExistingEndMessage,
        _ => null,
    };
}
