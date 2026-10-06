using Inventory.Application.Reporting.Shared;

namespace Inventory.Application.Reporting.Gst;

/// <summary>
/// The GST accounting-aid report contract, shared by <c>GET api/reports/gst</c> and the CSV/XLSX
/// export so both present identical figures.
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
}
