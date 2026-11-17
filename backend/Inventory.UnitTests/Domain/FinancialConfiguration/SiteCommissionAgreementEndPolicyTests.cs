using Inventory.Domain.FinancialConfiguration;
using Xunit;

namespace InventoryApi.Tests.Domain.FinancialConfiguration;

/// <summary>
/// The one rule that decides whether an agreement may be ended on a date (issue #1254).
///
/// <para>Ending is a narrowing: an open-ended agreement gains an end date, and an agreement that
/// already carries one may be brought forward but never pushed out. That is what makes ending safe
/// to allow a Manager - every date the old agreement used to cover either still belongs to it or
/// belongs to nothing, so no sale silently changes the agreement it is calculated under, and no
/// overlap can be created by the narrowing itself.</para>
/// </summary>
public class SiteCommissionAgreementEndPolicyTests
{
    private static readonly DateTime Start = new(2026, 1, 1);

    [Fact]
    public void An_open_ended_agreement_may_be_ended_on_a_date_after_its_start()
    {
        Assert.Equal(
            SiteCommissionAgreementEndRefusal.None,
            SiteCommissionAgreementEndPolicy.Decide(Start, null, new DateTime(2026, 6, 30)));
    }

    [Fact]
    public void An_open_ended_agreement_may_be_ended_on_its_own_start_date()
    {
        Assert.Equal(
            SiteCommissionAgreementEndRefusal.None,
            SiteCommissionAgreementEndPolicy.Decide(Start, null, Start));
    }

    [Fact]
    public void An_end_date_before_the_start_date_is_refused()
    {
        Assert.Equal(
            SiteCommissionAgreementEndRefusal.BeforeStart,
            SiteCommissionAgreementEndPolicy.Decide(Start, null, Start.AddDays(-1)));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public void An_agreement_that_already_ends_may_be_ended_earlier_or_on_the_same_date(int dayOffset)
    {
        var existingEnd = new DateTime(2026, 6, 30);

        Assert.Equal(
            SiteCommissionAgreementEndRefusal.None,
            SiteCommissionAgreementEndPolicy.Decide(Start, existingEnd, existingEnd.AddDays(dayOffset)));
    }

    [Fact]
    public void An_agreement_that_already_ends_cannot_be_extended()
    {
        var existingEnd = new DateTime(2026, 6, 30);

        Assert.Equal(
            SiteCommissionAgreementEndRefusal.LaterThanExistingEnd,
            SiteCommissionAgreementEndPolicy.Decide(Start, existingEnd, existingEnd.AddDays(1)));
    }

    /// <summary>
    /// An agreement is effective-dated by calendar date, so a time of day on either side of the
    /// comparison decides nothing: 30 June at 23:59 is the same end date as 30 June at midnight.
    /// </summary>
    [Fact]
    public void The_comparison_is_by_date_and_ignores_the_time_of_day()
    {
        Assert.Equal(
            SiteCommissionAgreementEndRefusal.None,
            SiteCommissionAgreementEndPolicy.Decide(
                Start.AddHours(9),
                new DateTime(2026, 6, 30, 23, 59, 0),
                new DateTime(2026, 6, 30, 1, 0, 0)));
    }

    /// <summary>
    /// Both refusals carry a sentence written for the caller, because they are published verbatim as
    /// the <c>400</c> response body; neither names a business, a site or a rate.
    /// </summary>
    [Theory]
    [InlineData(SiteCommissionAgreementEndRefusal.BeforeStart)]
    [InlineData(SiteCommissionAgreementEndRefusal.LaterThanExistingEnd)]
    public void Every_refusal_carries_a_message(SiteCommissionAgreementEndRefusal refusal)
    {
        Assert.False(string.IsNullOrWhiteSpace(SiteCommissionAgreementEndPolicy.Message(refusal)));
    }

    [Fact]
    public void An_allowed_end_date_carries_no_message()
    {
        Assert.Null(SiteCommissionAgreementEndPolicy.Message(SiteCommissionAgreementEndRefusal.None));
    }

    /// <summary>
    /// What ending an agreement does to the commission the report calculates, which is the criterion
    /// "commission due for dates up to the end date is unchanged": the resolution every money report
    /// uses still answers the ended agreement for every date up to and including its end date, and
    /// the replacement for the day after. Commission is calculated on read, so nothing stored is
    /// rewritten - the rate a date is calculated under simply stops changing after the end date.
    /// </summary>
    [Theory]
    [InlineData(2026, 1, 1, 0.10)]
    [InlineData(2026, 6, 29, 0.10)]
    [InlineData(2026, 6, 30, 0.10)]
    [InlineData(2026, 7, 1, 0.12)]
    [InlineData(2026, 12, 31, 0.12)]
    public void Ending_the_old_agreement_leaves_every_date_up_to_the_end_date_on_the_old_rate(
        int year, int month, int day, double expectedRate)
    {
        var ended = new CommissionAgreement(
            1, 50, Start, new DateTime(2026, 6, 30), 0.10m, CommissionFrequency.Monthly,
            CommissionBasis.GrossSales, 14, Start, Start);
        var replacement = new CommissionAgreement(
            2, 50, new DateTime(2026, 7, 1), null, 0.12m, CommissionFrequency.Monthly,
            CommissionBasis.GrossSales, 14, Start, Start);

        var resolved = EffectiveFinancialConfiguration.ResolveAgreement(
            [ended, replacement], 50, new DateTime(year, month, day));

        Assert.Equal((decimal)expectedRate, resolved!.CommissionRate);
    }
}
