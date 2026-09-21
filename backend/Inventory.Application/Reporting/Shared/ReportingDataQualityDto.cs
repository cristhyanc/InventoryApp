namespace Inventory.Application.Reporting.Shared;

public record ReportingDataQualityDto(
    bool MissingStatus = true,
    bool HistoricalCostUnavailable = true,
    bool GstClassificationMissing = true,
    bool CommissionNotPersisted = true,
    bool ContainsUnmappedProducts = false,
    IReadOnlyList<string>? Notes = null)
{
    public bool MissingStatusData => MissingStatus;
    public bool HistoricalCostMissing => HistoricalCostUnavailable;
    public bool GstClassificationMissingData => GstClassificationMissing;
    public bool CommissionPersistenceLimited => CommissionNotPersisted;
}
