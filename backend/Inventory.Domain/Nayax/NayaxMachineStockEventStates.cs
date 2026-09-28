namespace Inventory.Domain.Nayax;

/// <summary>Whether a Nayax machine-stock event's EventData was parsed and its product resolved.</summary>
public enum NayaxStockEventMatchStatus
{
    /// <summary>Parsed successfully and matched to exactly one local product via Machine + MDB.</summary>
    Matched = 0,

    /// <summary>
    /// Malformed EventData, an unknown MDB for this machine, or a material product-name mismatch.
    /// Never causes an inventory movement; the event carries the reason.
    /// </summary>
    NeedsReview = 1
}

/// <summary>Whether a Nayax machine-stock event has been applied to storage inventory.</summary>
public enum NayaxStockEventProcessingStatus
{
    /// <summary>Not yet applied. Covers pending positive refills, insufficient-stock cases,
    /// negative discrepancies (which are never "applied"), and Needs Review events.</summary>
    Unprocessed = 0,

    /// <summary>A positive adjustment was applied as a machine refill.</summary>
    Applied = 1
}
