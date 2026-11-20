using System.Text.Json.Serialization;
using Inventory.Domain.Nayax;

namespace Inventory.Infrastructure.Models;

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

    /// <summary>
    /// The product name the alert reported. For a 501 stock adjustment it is the adjusted product;
    /// for a 510 product replacement (issue #1325) it is the <em>incoming</em> product.
    /// </summary>
    public string? ParsedProductName { get; set; }

    /// <summary>
    /// The <em>outgoing</em> product name of a 510 product replacement (issue #1325); always null
    /// for a 501 stock adjustment.
    /// </summary>
    public string? ParsedOutgoingProductName { get; set; }

    /// <summary>The signed quantity Nayax reported. Positive is a refill; negative is a discrepancy.</summary>
    public int? ParsedQuantity { get; set; }

    public long? MatchedProductId { get; set; }
    [JsonIgnore]
    public virtual Product? MatchedProduct { get; set; }

    public NayaxStockEventMatchStatus MatchStatus { get; set; } = NayaxStockEventMatchStatus.NeedsReview;
    public string? NeedsReviewReason { get; set; }

    public NayaxStockEventProcessingStatus ProcessingStatus { get; set; } = NayaxStockEventProcessingStatus.Unprocessed;
    public DateTime? ProcessedAt { get; set; }

    /// <summary>
    /// The movement this event's application created, once <see cref="ProcessingStatus"/> is Applied.
    /// For an applied product replacement (issue #1326) it is the <em>incoming</em> refill, and it is
    /// null when nothing was loaded; the outgoing movements are the two columns below.
    /// </summary>
    public int? StockAdjustmentId { get; set; }

    /// <summary>
    /// The product the operator confirmed was loaded into the slot, for an applied product
    /// replacement (issue #1326); null on every other row, including a pending replacement. It is
    /// the record of what was applied, deliberately not a match:
    /// <see cref="MatchedProductId"/>/<see cref="MatchStatus"/> are left exactly as the import left
    /// them, because which product a slot holds keeps being read live from Nayax.
    /// </summary>
    public long? ReplacementIncomingProductId { get; set; }

    /// <summary>How many units of it were loaded; <c>0</c> is a replacement that loaded nothing.</summary>
    public int? ReplacementIncomingQuantity { get; set; }

    /// <summary>
    /// What the operator said happened to the units taken out (issue #1326). Null on every row but
    /// an applied replacement; <c>None</c> records that nothing at all was recorded for them.
    /// </summary>
    public NayaxReplacementOutgoingDisposition? ReplacementOutgoingDisposition { get; set; }

    /// <summary>
    /// The product taken out, and how many units of it, for every disposition but <c>None</c>
    /// (issue #1326).
    /// </summary>
    public long? ReplacementOutgoingProductId { get; set; }

    public int? ReplacementOutgoingQuantity { get; set; }

    /// <summary>
    /// The <c>MachineRefill</c> movement that returned the outgoing units to storage, for every
    /// disposition but <c>None</c> (issue #1326).
    /// </summary>
    public int? ReplacementOutgoingReturnStockAdjustmentId { get; set; }

    /// <summary>
    /// The <c>Damaged</c>/<c>Expired</c> write-off of exactly those returned units, for a disposal
    /// only (issue #1326).
    /// </summary>
    public int? ReplacementOutgoingWriteOffStockAdjustmentId { get; set; }

    /// <summary>
    /// The operator's explicit resolution of a flagged possible duplicate (issue #196), distinct
    /// from <see cref="ProcessingStatus"/>: <c>ReconciledManually</c> never creates a movement,
    /// while <c>AppliedAsSeparateRestock</c> is an explicit, auditable override that does.
    /// </summary>
    public NayaxDuplicateResolution DuplicateResolution { get; set; } = NayaxDuplicateResolution.None;

    /// <summary>When the operator made that explicit choice; null while unresolved.</summary>
    public DateTime? DuplicateResolvedAt { get; set; }

    /// <summary>
    /// The manual <see cref="StockAdjustment"/> this event was resolved against, for either
    /// resolution, kept for auditability (issue #196).
    /// </summary>
    public int? MatchedManualStockAdjustmentId { get; set; }
    [JsonIgnore]
    public virtual StockAdjustment? MatchedManualStockAdjustment { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
