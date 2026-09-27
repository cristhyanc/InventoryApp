using System.Text.Json.Serialization;

namespace InventoryApi.Models;

/// <summary>Whether a Nayax machine-stock event's EventData was parsed and its product resolved.</summary>
public enum NayaxStockEventMatchStatus
{
    /// <summary>Parsed successfully and matched to exactly one local product via Machine + MDB.</summary>
    Matched = 0,

    /// <summary>
    /// Malformed EventData, an unknown MDB for this machine, or a material product-name mismatch.
    /// Never causes an inventory movement; <see cref="NayaxMachineStockEvent.NeedsReviewReason"/>
    /// carries the reason.
    /// </summary>
    NeedsReview = 1
}

/// <summary>Whether a Nayax machine-stock event has been applied to storage inventory.</summary>
public enum NayaxStockEventProcessingStatus
{
    /// <summary>Not yet applied. Covers pending positive refills, insufficient-stock cases,
    /// negative discrepancies (which are never "applied"), and Needs Review events.</summary>
    Unprocessed = 0,

    /// <summary>A positive adjustment was applied as a <see cref="StockAdjustmentReason.MachineRefill"/>.</summary>
    Applied = 1
}

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
    /// The application's own primary key. <see cref="NayaxEventId"/> is Nayax's identifier and is
    /// only unique within one business (see AppDbContext), matching the established
    /// <c>NayaxSales</c>/<c>TransactionID</c> pattern.
    /// </summary>
    public int Id { get; set; }

    public long NayaxEventId { get; set; }
    public long MachineId { get; set; }
    public int EventCode { get; set; }
    public DateTime EventTimestamp { get; set; }

    /// <summary>The raw, unmodified EventData text as reported by Nayax.</summary>
    public string RawEventData { get; set; } = string.Empty;

    /// <summary>Additional raw source metadata (for example the alert's EventName) kept for audit.</summary>
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
