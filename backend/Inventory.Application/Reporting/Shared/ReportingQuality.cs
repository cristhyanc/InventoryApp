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

    /// <summary>
    /// The conditional form (issue #476): the flags behave exactly as in <see cref="Quality"/> and
    /// still map 1:1 onto the identically named <see cref="ReportingDataQualityDto"/> field, but the
    /// returned notes are only the caller's own fact-derived ones. A period with no actual problem
    /// therefore returns an empty note list, so a report can hide its data-quality section instead of
    /// showing permanent disclaimers that say nothing about the requested period.
    ///
    /// A caller that moves to this form owes its reader two things elsewhere: a conditional note for
    /// every real problem in the requested scope, and its normal calculation methodology (persisted
    /// historical sale cost, effective-dated commission agreements, the indicative GST-on-sales
    /// calculation) presented as report help beside the figures it explains. Methodology is not a
    /// data-quality warning, and an empty note list must never be read as "GST classification is
    /// verified" - it only means this period holds no detected problem. Report families that still
    /// want the four standard disclaimers keep calling <see cref="Quality"/>; nothing here changes
    /// for them.
    /// </summary>
    public static ReportingDataQualityDto Conditional(
        bool missingStatus,
        bool historicalCostUnavailable,
        bool gstClassificationMissing,
        bool commissionNotPersisted,
        bool containsUnmappedProducts,
        IEnumerable<string>? notes = null) =>
        new(missingStatus, historicalCostUnavailable, gstClassificationMissing, commissionNotPersisted, containsUnmappedProducts,
            (notes ?? Enumerable.Empty<string>()).Where(note => !string.IsNullOrEmpty(note)).ToList());
}
