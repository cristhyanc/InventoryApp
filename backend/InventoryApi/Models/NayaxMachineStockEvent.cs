using System.Text.Json.Serialization;
using Inventory.Domain.Nayax;

namespace InventoryApi.Models;

/// <summary>
/// A tenant-owned imported fact: one Nayax Event 501 "Stock Adjust for Machine" alert (issue #183).
/// Nayax remains the source of truth for the raw event; this row preserves it verbatim alongside
/// the parsed/matched interpretation and the local processing outcome, so the reconciliation can be
/// audited and safely re-run.
/// </summary>
public class NayaxMachineStockEvent : IBusinessOwned
{
    /// <summary>
    /// The owning business (issue #64). Stamped from the caller's resolved business on insert
    /// and immutable afterwards; never read from API input, and never serialised out.
    /// </summary>
    [JsonIgnore]
    public int BusinessId { get; set; }

    /// <summary>
    /// The application's own primary key. <see cref="NayaxEventLogId"/> is Nayax's identifier and is
    /// only unique within one business (see AppDbContext), matching the established
    /// <c>NayaxSales</c>/<c>TransactionID</c> pattern.
    /// </summary>
    public int Id { get; set; }

    /// <summary>The alert's Nayax <c>EventLogID</c>: the upstream event identity and idempotency key.</summary>
    public long NayaxEventLogId { get; set; }
    public long MachineId { get; set; }
    public int EventCode { get; set; }

    /// <summary>The alert's Nayax <c>EventDateTimeGMT</c>, stored as UTC: the canonical event instant.</summary>
    public DateTime EventDateTimeGmt { get; set; }

    /// <summary>
    /// The alert's Nayax <c>EventDateTimeVMC</c> (the machine clock), preserved as source data.
    /// Null only for rows imported before this column existed.
    /// </summary>
    public DateTime? EventDateTimeVmc { get; set; }

    /// <summary>The raw, unmodified EventData text as reported by Nayax.</summary>
    public string RawEventData { get; set; } = string.Empty;

    /// <summary>
    /// The complete source alert, serialised as JSON with Nayax's documented Get Machine Last Alerts
    /// field names (EventDescription, EventSourceName, EventGroupName, JSONData, ...), kept for audit.
    /// </summary>
    public string? RawSourceMetadata { get; set; }

    public int? ParsedMdb { get; set; }
    public string? ParsedProductName { get; set; }

    /// <summary>The signed quantity Nayax reported. Positive is a refill; negative is a discrepancy.</summary>
    public int? ParsedQuantity { get; set; }

    public long? MatchedProductId { get; set; }
    [JsonIgnore]
    public virtual Product? MatchedProduct { get; set; }

    public NayaxStockEventMatchStatus MatchStatus { get; set; } = NayaxStockEventMatchStatus.NeedsReview;
    public string? NeedsReviewReason { get; set; }

    public NayaxStockEventProcessingStatus ProcessingStatus { get; set; } = NayaxStockEventProcessingStatus.Unprocessed;
    public DateTime? ProcessedAt { get; set; }

    /// <summary>The movement this event's application created, once <see cref="ProcessingStatus"/> is Applied.</summary>
    public int? StockAdjustmentId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
