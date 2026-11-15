using Inventory.Application.Reporting.Shared;

namespace Inventory.Application.Reporting.Gst;

/// <summary>
/// The GST accounting-aid report contract, shared by <c>GET api/reports/gst</c> and the CSV/XLSX
/// export so both present identical figures.
///
/// <c>TaxableSales</c> is the GST-<em>exclusive</em> value of the period's taxable sales, and since
/// issue #1255 "taxable" is each sold product's own GST rule: sales of products whose rule is
/// explicitly GST-free are in <c>GstFreeSales</c> instead and bear no GST, while a product with no
/// rule and a sale whose product could not be resolved stay taxable and are also reported, as the
/// GST-inclusive <see cref="NoGstRuleSales"/>, in <c>TaxableSales</c>.
///
/// <c>InventoryPurchaseGst</c> is the period's total purchase input GST: <see cref="PurchaseLineGst"/>
/// plus <see cref="PurchaseChargeGst"/>. It contains only GST that an explicit classification
/// resolved (issue #432); unclassified components are reported by
/// <see cref="PurchaseUnresolvedComponentCount"/>/<see cref="PurchaseUnresolvedAmount"/> instead and
/// never contribute an inferred amount.
/// </summary>
public record GstAccountingAidDto(
    DateTime From,
    DateTime To,
    decimal TaxableSales,
    decimal GstOnSales,
    decimal TaxableFees,
    decimal GstOnFees,
    decimal NetGst,
    ReportingDataQualityDto DataQuality,
    decimal GstFreeSales = 0m,
    decimal InventoryPurchaseGst = 0m)
{
    /// <summary>
    /// The part of <see cref="TaxableSales"/> that is taxable only because those products carry no
    /// GST rule - the default every product starts at - plus any sale whose product could not be
    /// resolved (issue #1255). It is GST-inclusive, like the sales figures bookkeeping shows, and is
    /// reported on its own so the aid never presents that default as a settled classification.
    /// Governed by <see cref="ReportProviderAvailabilityDto.Sales"/>, like the taxable and GST-free
    /// sales it describes.
    /// </summary>
    public decimal NoGstRuleSales { get; init; }

    public decimal OperatingExpenseGst { get; init; }

    /// <summary>Input GST from taxable purchase product lines in the period.</summary>
    public decimal PurchaseLineGst { get; init; }

    /// <summary>Input GST from taxable purchase delivery and package charges in the period.</summary>
    public decimal PurchaseChargeGst { get; init; }

    /// <summary>
    /// How many purchase components (lines plus present delivery/package charges) in the period
    /// carry no GST classification. An absent or zero charge is not a component and is never
    /// counted (parent issue #62, decision D3).
    /// </summary>
    public int PurchaseUnresolvedComponentCount { get; init; }

    /// <summary>The GST-inclusive value of those unresolved components.</summary>
    public decimal PurchaseUnresolvedAmount { get; init; }

    /// <summary>
    /// Whether the purchase input GST shown is known to be incomplete, and therefore whether
    /// <see cref="NetGst"/> is provisional. This is a purchase-classification flag and is
    /// deliberately separate from <c>DataQuality.GstClassificationMissing</c>, which is about
    /// imported Nayax reimbursement rows. It is set when
    /// <see cref="PurchaseUnresolvedComponentCount"/> is above zero, and also for a machine-filtered
    /// report, where whole-business purchases are not allocated to a machine and are excluded
    /// altogether. The accompanying <c>DataQuality.Notes</c> entry says which cause applies.
    /// </summary>
    public bool PurchaseGstIncomplete { get; init; }

    /// <summary>
    /// How well recorded provider coverage accounts for this period's provider-sourced GST figures
    /// (issue #730, parent #329 decision P9). <see cref="ReportProviderAvailabilityDto.Sales"/>
    /// governs <see cref="TaxableSales"/>, <see cref="GstFreeSales"/>,
    /// <see cref="NoGstRuleSales"/> and <see cref="GstOnSales"/>;
    /// <see cref="ReportProviderAvailabilityDto.Fees"/> governs <see cref="TaxableFees"/> and
    /// <see cref="GstOnFees"/>.
    ///
    /// <see cref="OperatingExpenseGst"/>, <see cref="PurchaseLineGst"/>,
    /// <see cref="PurchaseChargeGst"/> and <see cref="InventoryPurchaseGst"/> are this business's
    /// own recorded data, never a provider's, so they stay visible and exact however incomplete the
    /// provider figures are.
    /// </summary>
    public ReportProviderAvailabilityDto ProviderAvailability { get; init; } =
        ReportProviderAvailabilityDto.Undecided;

    /// <summary>
    /// The availability of <see cref="NetGst"/>, which subtracts both provider figures and is
    /// therefore only as complete as the weaker of them - the value
    /// <see cref="ReportProviderAvailabilityDto.Combined"/> already carries, reported again here
    /// under the name of the figure a reader is looking at. It is separate from
    /// <see cref="PurchaseGstIncomplete"/>, which is about unclassified purchase components: net GST
    /// can be incomplete for either reason, or both.
    /// </summary>
    public ReportAvailabilityDto NetGstAvailability { get; init; } = ReportAvailabilityDto.Undecided;
}
