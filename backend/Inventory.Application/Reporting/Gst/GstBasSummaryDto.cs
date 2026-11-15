using Inventory.Application.Reporting.Shared;
using Inventory.Domain.Reporting.Gst;

namespace Inventory.Application.Reporting.Gst;

/// <summary>
/// Why a period has no BAS summary to show, or that it has one (issue #1318). A client branches on
/// the code and never on the presence of a figure, because "no summary" and "a summary of zero" are
/// different statements.
/// </summary>
public static class GstBasSummaryStatus
{
    /// <summary>The business is registered for GST and the summary below is its estimate.</summary>
    public const string Available = "Available";

    /// <summary>
    /// The business's GST registration is <c>No</c> or has never been recorded. There is nothing to
    /// show and nothing to prompt for: a business that is not registered has no BAS GST section.
    /// </summary>
    public const string NotGstRegistered = "NotGstRegistered";

    /// <summary>
    /// A machine filter is active. Whole-business purchases and operating expenses are not allocated
    /// to a machine, so a per-machine GST credit figure would be wrong rather than partial; the
    /// client asks the reader to select all machines instead.
    /// </summary>
    public const string MachineFiltered = "MachineFiltered";
}

/// <summary>
/// The GST and BAS summary shown in Bookkeeping for a GST-registered business (issue #1318), over
/// the selected whole-business date range.
///
/// <para>Every amount comes from the shared GST accounting-aid calculation
/// (<see cref="GstAccountingAidComposition"/>) through <see cref="BasGstSummaryPolicy"/>, so this
/// screen and the GST accounting aid report the same period identically. It is an estimate drawn
/// from the records this application holds, for reviewing the GST section of a BAS - never a
/// lodgement figure.</para>
/// </summary>
/// <param name="From">The first business date included, as the report resolved it.</param>
/// <param name="To">The last business date included.</param>
/// <param name="Status">One of the <see cref="GstBasSummaryStatus"/> codes.</param>
/// <param name="Figures">
/// The estimate, present only while <paramref name="Status"/> is
/// <see cref="GstBasSummaryStatus.Available"/>. It is <see langword="null"/> rather than a set of
/// zeros in every other state: a summary nobody calculated must never read as a nil BAS.
/// </param>
/// <param name="DataQuality">
/// The conditional data-quality notes this period earned, from the same shared calculation the GST
/// accounting aid reports them from, plus the BAS summary's own. Empty while there is no summary.
/// </param>
public sealed record GstBasSummaryDto(
    DateTime From,
    DateTime To,
    string Status,
    GstBasSummaryFiguresDto? Figures,
    ReportingDataQualityDto DataQuality);

/// <summary>
/// The BAS labels themselves, with the availability of each provider-sourced figure beside them
/// (issue #730): a figure whose state is not <c>Complete</c> is shown as that state by every
/// presentation, never as <c>$0</c> and never as a final balance.
/// </summary>
/// <param name="TotalSales">
/// G1 - the period's whole GST-inclusive sales, GST-free sales included. Governed by
/// <paramref name="ProviderAvailability"/>'s sales state.
/// </param>
/// <param name="GstOnSales">1A - GST on the period's taxable sales. Sales state.</param>
/// <param name="GstCredits">
/// 1B - the eligible credits, each source counted once. Governed by the fee state, because one of
/// the three sources is the Nayax fee GST: a 1B that silently omitted unmeasured fee credits would
/// be read as a complete credit total.
/// </param>
/// <param name="PurchaseGstCredits">
/// The eligible stock purchase input GST inside 1B, including eligible delivery and packaging
/// charges. It is this business's own recorded data and carries no provider state;
/// <paramref name="PurchaseUnresolvedComponentCount"/> is what says it is not all of it.
/// </param>
/// <param name="OperatingExpenseGstCredits">
/// The operating-expense GST inside 1B. This business's own recorded data; no provider state.
/// </param>
/// <param name="NayaxFeeGstCredits">The Nayax fee GST inside 1B. Fee state.</param>
/// <param name="NetGst">
/// 1A less 1B, signed. Governed by <paramref name="NetGstAvailability"/>, the least complete of the
/// two provider families.
/// </param>
/// <param name="NetGstOutcome">
/// <c>Payable</c>, <c>Credit</c> or <c>Nil</c> - the name of the <see cref="BasNetGstOutcome"/> the
/// Domain policy decided, so no presentation reads the sign itself. It is <see langword="null"/>
/// when <paramref name="NetGst"/> is an amount no presentation may show - the internal zero of a
/// period whose provider coverage never measured it - because there is no payable, credit or nil
/// answer about an amount nobody has. A presentation names the figure without an outcome then.
/// </param>
/// <param name="IsIncomplete">
/// Whether the estimate rests on something missing or estimated. The notes say which; the figures
/// are shown either way.
/// </param>
/// <param name="GstFreeSales">
/// The part of G1 that is sales of products whose GST rule is explicitly GST-free, which bear no
/// GST. Sales state.
/// </param>
/// <param name="NoGstRuleSales">
/// The part of G1 that is taxable only because those products carry no GST rule, plus any sale whose
/// product could not be resolved (issue #1255). It is the figure that says how much of 1A rests on
/// an assumption. Sales state.
/// </param>
/// <param name="PurchaseUnresolvedComponentCount">
/// How many purchase components in the period carry no GST classification. They contribute no credit
/// and are never estimated from an amount.
/// </param>
/// <param name="PurchaseUnresolvedAmount">The GST-inclusive value of those components.</param>
/// <param name="EstimatedFeeGst">
/// How much of <paramref name="NayaxFeeGstCredits"/> was estimated from the configured rate rather
/// than taken from an imported settlement.
/// </param>
/// <param name="FeeGstIncludesEstimates">Whether any of the fee GST was estimated that way.</param>
/// <param name="ProviderAvailability">
/// The two provider families and their combination, exactly as the GST accounting aid decided them.
/// </param>
/// <param name="NetGstAvailability">The state governing <paramref name="NetGst"/>.</param>
public sealed record GstBasSummaryFiguresDto(
    decimal TotalSales,
    decimal GstOnSales,
    decimal GstCredits,
    decimal PurchaseGstCredits,
    decimal OperatingExpenseGstCredits,
    decimal NayaxFeeGstCredits,
    decimal NetGst,
    string? NetGstOutcome,
    bool IsIncomplete,
    decimal GstFreeSales,
    decimal NoGstRuleSales,
    int PurchaseUnresolvedComponentCount,
    decimal PurchaseUnresolvedAmount,
    decimal EstimatedFeeGst,
    bool FeeGstIncludesEstimates,
    ReportProviderAvailabilityDto ProviderAvailability,
    ReportAvailabilityDto NetGstAvailability);
