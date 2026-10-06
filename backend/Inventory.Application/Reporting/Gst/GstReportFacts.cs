using Inventory.Domain.Reporting.Gst;

namespace Inventory.Application.Reporting.Gst;

/// <summary>
/// The facts the GST accounting-aid report still needs directly: the imported-summary data-quality
/// flags (whether any imported reimbursement rows cover the requested period, and whether any of
/// them carry a GST/VAT classification), and the period's purchase GST components.
///
/// Contains no financial formulas. <see cref="Purchases"/> carries each purchase's raw
/// quantity/unit-cost/charge amounts and their stored classifications exactly as persisted; the
/// input GST is derived from them by <see cref="PurchaseInputGstPolicy"/> in the use case, so the
/// rounding rules stay in the Domain (issue #432).
/// </summary>
public sealed record GstReportFacts(
    bool ImportedContainsRows,
    bool ImportedContainsGstClassification,
    IReadOnlyList<PurchaseGstComponents> Purchases);
