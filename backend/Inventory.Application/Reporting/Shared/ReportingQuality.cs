namespace Inventory.Application.Reporting.Shared;

/// <summary>
/// The standard reporting data-quality disclaimer notes shared by every report family, plus any
/// report-specific notes appended to them. Every boolean parameter maps 1:1 onto the identically
/// named <see cref="ReportingDataQualityDto"/> flag it produces: callers must derive each one from
/// their own facts (or an explicit, documented conservative default), never assume a shared meaning
/// across report families.
/// </summary>
public static class ReportingQuality
{
    public static ReportingDataQualityDto Quality(
        bool missingStatus,
        bool historicalCostUnavailable,
        bool gstClassificationMissing,
        bool commissionNotPersisted,
        bool containsUnmappedProducts,
        IEnumerable<string>? notes = null) =>
        new(missingStatus, historicalCostUnavailable, gstClassificationMissing, commissionNotPersisted, containsUnmappedProducts,
            new[]
            {
                "Nayax status IDs are stored raw; existing historical rows were backfilled to status 12 by migration; rows still missing a status are excluded.",
                "Historical COGS uses the persisted sale cost; unresolved completed sales are reported as incomplete.",
                "Commission is calculated from effective-dated site commission agreements.",
                "GST classification is not persisted on sales; GST amounts are an indicative 10% inclusive calculation."
            }.Concat((notes ?? Enumerable.Empty<string>()).Where(note => !string.IsNullOrEmpty(note))).ToList());
}
