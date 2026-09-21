namespace Inventory.Application.Reporting.Shared;

/// <summary>
/// The standard reporting data-quality disclaimer notes shared by every report family, plus any
/// report-specific note appended to them.
/// </summary>
public static class ReportingQuality
{
    public static ReportingDataQualityDto Quality(bool importedRows, bool gstClassification, bool unmapped, string? note = null) =>
        new(false, true, true, true, unmapped,
            new[]
            {
                "Nayax status IDs are stored raw; existing historical rows were backfilled to status 12 by migration; rows still missing a status are excluded.",
                "Historical COGS uses the persisted sale cost; unresolved completed sales are reported as incomplete.",
                "Commission is calculated from effective-dated site commission agreements.",
                "GST classification is not persisted on sales; GST amounts are an indicative 10% inclusive calculation."
            }.Concat(note is null ? Array.Empty<string>() : new[] { note }).ToList());
}
