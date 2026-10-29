using Inventory.Domain.Subscriptions;
using Xunit;

namespace InventoryApi.Tests.Domain.Subscriptions;

/// <summary>
/// Which plan feature each report belongs to (issue #967), by the name the export route carries.
///
/// <para>Every accepted name of the export's own row builder is listed, aliases included, so a
/// download cannot reach a report under a name this table does not classify the same way.</para>
/// </summary>
public class ReportPlanRequirementsTests
{
    /// <summary>
    /// The two reports a Starter business includes - and both of the GST aid's accepted names, so
    /// <c>gst-accounting</c> is not a way to download a Starter report under the Pro feature or the
    /// other way round.
    /// </summary>
    [Theory]
    [InlineData("bookkeeping")]
    [InlineData("gst")]
    [InlineData("gst-accounting")]
    public void The_provider_free_reports_are_the_starter_feature(string report) =>
        Assert.Equal(PlanFeature.BookkeepingAndGstReports, ReportPlanRequirements.FeatureOf(report));

    /// <summary>Every other report the export serves, under each name it accepts.</summary>
    [Theory]
    [InlineData("daily")]
    [InlineData("reconciliation")]
    [InlineData("machine-profitability")]
    [InlineData("machines")]
    [InlineData("product-profitability")]
    [InlineData("dashboard")]
    [InlineData("transactions")]
    [InlineData("transaction-sales")]
    public void Every_other_report_is_the_provider_sales_feature(string report) =>
        Assert.Equal(PlanFeature.ProviderSalesReports, ReportPlanRequirements.FeatureOf(report));

    /// <summary>
    /// Read the way the export's row builder reads the same route value: trimmed, and without case.
    /// </summary>
    [Theory]
    [InlineData("Bookkeeping")]
    [InlineData("BOOKKEEPING")]
    [InlineData("  bookkeeping  ")]
    public void A_name_is_matched_trimmed_and_without_case(string report) =>
        Assert.Equal(PlanFeature.BookkeepingAndGstReports, ReportPlanRequirements.FeatureOf(report));

    /// <summary>
    /// The fail-closed direction, and the reason it is that way round: the export's row builder
    /// falls through to <em>product profitability</em> for a name it does not match, so an
    /// unrecognised name really does serve provider sales data. Answering the Starter feature there
    /// would hand a Starter business a Pro report by misspelling a URL.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("not-a-report")]
    [InlineData("book-keeping")]
    public void An_unrecognised_name_is_the_most_restricted_feature(string? report) =>
        Assert.Equal(PlanFeature.ProviderSalesReports, ReportPlanRequirements.FeatureOf(report));

    /// <summary>
    /// The allow-list is closed and is what makes the rule above safe, so it is asserted as the
    /// complete list rather than only through the rows above.
    /// </summary>
    [Fact]
    public void The_provider_free_report_names_are_exactly_the_three() =>
        Assert.Equal(
            ["bookkeeping", "gst", "gst-accounting"],
            ReportPlanRequirements.ProviderFreeReportNames);
}
