namespace Inventory.Application.Reporting.Gst;

/// <summary>
/// The imported-summary data-quality facts the GST accounting-aid report still needs directly:
/// whether any imported reimbursement rows cover the requested period, and whether any of them
/// carry a GST/VAT classification. Contains no financial formulas.
/// </summary>
public sealed record GstReportFacts(
    bool ImportedContainsRows,
    bool ImportedContainsGstClassification);
