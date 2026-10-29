namespace Inventory.Domain.Subscriptions;

/// <summary>
/// Which <see cref="PlanFeature"/> one report belongs to (issue #967), by the report name the export
/// route carries.
///
/// <para>It exists because <c>GET /api/reports/{report}/export</c> is one endpoint serving every
/// report family, so its plan requirement cannot be a constant on the action: Bookkeeping and GST are
/// <see cref="PlanFeature.BookkeepingAndGstReports"/> and the rest are
/// <see cref="PlanFeature.ProviderSalesReports"/>, and a download is the same figures in another
/// container. Being one table read by both the report actions' declared features and the export's
/// per-report check is what stops a report and its own export coming to need different plans - the
/// specific drift AGENTS.md § Reporting dates and filters forbids for every other property of an
/// export.</para>
///
/// <para><strong>An unrecognised name is the most restricted answer, deliberately.</strong> The
/// export's own row builder falls through to product profitability for any name it does not match
/// (<c>Inventory.Application.Reporting.Export.GetReportExportRows</c>), so a name nothing recognises
/// really does serve provider sales data, and answering
/// <see cref="PlanFeature.BookkeepingAndGstReports"/> there would hand a Starter business a Pro
/// report by misspelling a URL. The vocabulary below is that row builder's own, aliases included.</para>
/// </summary>
public static class ReportPlanRequirements
{
    /// <summary>The export route's name for the bookkeeping report.</summary>
    public const string Bookkeeping = "bookkeeping";

    /// <summary>The export route's name for the GST accounting aid.</summary>
    public const string Gst = "gst";

    /// <summary>
    /// The GST accounting aid's second accepted name, as the export's row builder spells it. Both
    /// reach the same report, so both are the same feature.
    /// </summary>
    public const string GstAccounting = "gst-accounting";

    /// <summary>
    /// Every report name that is included in <see cref="PlanFeature.BookkeepingAndGstReports"/>.
    /// Written out as the complete list, because what makes this safe is that it is a closed
    /// allow-list and everything else is the more restricted feature.
    /// </summary>
    public static readonly IReadOnlyList<string> ProviderFreeReportNames = [Bookkeeping, Gst, GstAccounting];

    /// <summary>
    /// The feature <paramref name="reportName"/> belongs to. Trimmed and compared without case, the
    /// way the export's row builder reads the same route value; a null, blank or unrecognised name is
    /// <see cref="PlanFeature.ProviderSalesReports"/>.
    /// </summary>
    public static PlanFeature FeatureOf(string? reportName) =>
        ProviderFreeReportNames.Contains(
            (reportName ?? string.Empty).Trim(),
            StringComparer.OrdinalIgnoreCase)
            ? PlanFeature.BookkeepingAndGstReports
            : PlanFeature.ProviderSalesReports;
}
