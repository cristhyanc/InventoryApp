using Inventory.Domain.Stock;

namespace Inventory.Domain.Nayax;

/// <summary>
/// What the operator says happened to the units that came <em>out</em> of the slot when the product
/// in it was replaced (issue #1326). It is the operator's statement about physical units, not a new
/// adjustment reason: <see cref="ReturnedToStorage"/>, <see cref="Damaged"/> and
/// <see cref="Expired"/> all begin with the same return of those units to storage, and only the two
/// disposals then write the established write-off beside it.
/// </summary>
public enum NayaxReplacementOutgoingDisposition
{
    /// <summary>Nothing is recorded for the outgoing product: no movement at all.</summary>
    None = 0,

    /// <summary>
    /// The units went back into the storeroom. One <see cref="StockAdjustmentReason.MachineRefill"/>
    /// movement with a positive storage change - the reverse transfer of
    /// <see cref="MachineStock.MachineStockMovementKind.ReturnToStorage"/> - which is costing-neutral
    /// because the business still owns the units.
    /// </summary>
    ReturnedToStorage = 1,

    /// <summary>
    /// The units came out damaged and were discarded: the same return, and then one
    /// <see cref="StockAdjustmentReason.Damaged"/> write-off of the same quantity, so storage ends
    /// where it started, machine stock falls, and the loss is costed exactly once.
    /// </summary>
    Damaged = 2,

    /// <summary>The same, as an <see cref="StockAdjustmentReason.Expired"/> write-off.</summary>
    Expired = 3
}

/// <summary>
/// One operator's confirmed product replacement (issue #1326): which product was loaded and how
/// many units of it, and what happened to the units of the product that came out.
///
/// <para><see cref="IncomingQuantity"/> may be <c>0</c> - the replacement happened but nothing was
/// loaded - and <see cref="IncomingProductId"/> is required either way, because the replacement
/// records which product the slot now holds. <see cref="OutgoingProductId"/> and
/// <see cref="OutgoingQuantity"/> are required for every <see cref="OutgoingDisposition"/> but
/// <see cref="NayaxReplacementOutgoingDisposition.None"/>, and are absent for that one.</para>
/// </summary>
public readonly record struct NayaxProductReplacementChoice(
    long IncomingProductId,
    int IncomingQuantity,
    NayaxReplacementOutgoingDisposition OutgoingDisposition,
    long? OutgoingProductId,
    int? OutgoingQuantity);

/// <summary>Whether one confirmed replacement may be written, and why not when it may not.</summary>
public enum NayaxReplacementWriteDecision
{
    /// <summary>Write it: the movements <see cref="NayaxProductReplacementWritePolicy.Movements"/> describes.</summary>
    Apply,

    /// <summary>The choice itself does not make sense; nothing was read and nothing is written.</summary>
    Invalid,

    /// <summary>A named product is not an active product of this business: retired, foreign or gone.</summary>
    ProductUnavailable,

    /// <summary>Storage does not hold the units the incoming side says were loaded.</summary>
    InsufficientStorage
}

/// <summary>A decision plus the sentence shown to the operator.</summary>
public readonly record struct NayaxReplacementWriteRuling(
    NayaxReplacementWriteDecision Decision, string Message);

/// <summary>
/// Which movements one accepted replacement writes. Each flag is the presence of exactly one
/// movement, so "what is written" is answered in one place rather than inferred from the quantities
/// again at the persistence boundary.
/// </summary>
public readonly record struct NayaxReplacementMovementPlan(
    bool WritesIncomingRefill,
    bool WritesOutgoingReturn,
    bool WritesOutgoingWriteOff,
    StockAdjustmentReason? WriteOffReason);

/// <summary>
/// The deterministic rule deciding what an operator's confirmed Nayax product replacement
/// (Event 510) may write (issue #1326). It reads and writes nothing itself: the caller supplies the
/// choice and the two products as they stand, and this type answers whether the replacement may be
/// applied and exactly which movements it then consists of.
///
/// <para><strong>Why the outgoing units are returned before they are written off.</strong> The units
/// that came out of the slot are in the <em>machine</em>, not in the storeroom, and a plain
/// <see cref="StockAdjustmentReason.Damaged"/> or <see cref="StockAdjustmentReason.Expired"/>
/// adjustment removes units from <em>storage</em>. Writing one off on its own would therefore take
/// units out of a storeroom that never held them - making physical storage stock wrong, and refused
/// outright once storage is empty - while leaving the machine's own ledger still expecting them.
/// Each disposal is consequently the established pair: the costing-neutral
/// <see cref="StockAdjustmentReason.MachineRefill"/> return that moves the units out of the machine
/// and into storage, and then the established write-off of exactly those units, so storage ends
/// unchanged, machine stock falls by the quantity, and the loss is costed once by the existing cost
/// replay. No new adjustment reason is introduced, and no existing reason changes meaning.</para>
///
/// <para><strong>Storage is checked on the incoming side only.</strong> A load takes units out of
/// the storeroom and is refused when the storeroom does not hold them, exactly as a Sync Restock
/// refill is. The outgoing quantity never needs storage to hold it first, because the return puts
/// those units in and the write-off takes the same number straight back out.</para>
/// </summary>
public static class NayaxProductReplacementWritePolicy
{
    /// <summary>The loaded quantity is a count of units: zero or more, never negative.</summary>
    public const string IncomingQuantityMessage =
        "The quantity loaded must be zero or more units.";

    /// <summary>The replacement records which product the slot now holds, loaded or not.</summary>
    public const string IncomingProductRequiredMessage =
        "Choose the product that was loaded into the machine.";

    public const string OutgoingProductRequiredMessage =
        "Choose the product that was taken out of the machine.";

    public const string OutgoingQuantityMessage =
        "The quantity taken out must be at least one unit.";

    /// <summary>
    /// "None" means nothing is recorded for the outgoing product, so a product or quantity sent with
    /// it would be stored as part of a decision that wrote no movement for it.
    /// </summary>
    public const string NoOutgoingDetailsMessage =
        "Choose what happened to the units taken out before entering an outgoing product or quantity.";

    public const string UnknownDispositionMessage =
        "Choose what happened to the units taken out of the machine.";

    /// <summary>
    /// Refused for a retired product, a product of another business and a product that no longer
    /// exists alike, deliberately in one sentence: which of the three it is would say whether a
    /// product id another business owns exists.
    /// </summary>
    public const string IncomingProductUnavailableMessage =
        "The product that was loaded is not an active product of this business.";

    public const string OutgoingProductUnavailableMessage =
        "The product that was taken out is not an active product of this business.";

    /// <summary>
    /// The same refusal a Sync Restock refill gives, written for a quantity the operator entered
    /// rather than one Nayax reported.
    /// </summary>
    public static string InsufficientStorageMessage(int quantity, int available) =>
        $"{quantity} was entered as loaded but only {available} is available in storage.";

    /// <summary>
    /// What is wrong with the choice itself, or <see langword="null"/> when nothing is. It needs no
    /// product and no storage quantity, so a caller can refuse a nonsensical request before taking a
    /// write lock or reading anything.
    /// </summary>
    public static string? Refusal(NayaxProductReplacementChoice choice)
    {
        if (choice.OutgoingDisposition is not (NayaxReplacementOutgoingDisposition.None
            or NayaxReplacementOutgoingDisposition.ReturnedToStorage
            or NayaxReplacementOutgoingDisposition.Damaged
            or NayaxReplacementOutgoingDisposition.Expired))
        {
            return UnknownDispositionMessage;
        }

        if (choice.IncomingQuantity < 0)
            return IncomingQuantityMessage;

        if (choice.IncomingProductId <= 0)
            return IncomingProductRequiredMessage;

        if (choice.OutgoingDisposition == NayaxReplacementOutgoingDisposition.None)
        {
            return choice.OutgoingProductId is not null || choice.OutgoingQuantity is not null
                ? NoOutgoingDetailsMessage
                : null;
        }

        if (choice.OutgoingProductId is not > 0)
            return OutgoingProductRequiredMessage;

        return choice.OutgoingQuantity is not > 0 ? OutgoingQuantityMessage : null;
    }

    /// <summary>
    /// Whether this replacement may be written, decided from the choice and from the two products as
    /// the caller read them under its write lock. <paramref name="incomingProduct"/> and
    /// <paramref name="outgoingProduct"/> are the <em>active</em> products of the caller's own
    /// business, so <see langword="null"/> is the one answer that covers retired, foreign and
    /// missing alike; the outgoing one is only looked at when the choice records an outgoing
    /// movement.
    /// </summary>
    public static NayaxReplacementWriteRuling Decide(
        NayaxProductReplacementChoice choice,
        NayaxStockSyncProduct? incomingProduct,
        NayaxStockSyncProduct? outgoingProduct)
    {
        if (Refusal(choice) is { } refusal)
            return new(NayaxReplacementWriteDecision.Invalid, refusal);

        if (incomingProduct is not { } incoming)
            return new(NayaxReplacementWriteDecision.ProductUnavailable, IncomingProductUnavailableMessage);

        if (choice.OutgoingDisposition != NayaxReplacementOutgoingDisposition.None && outgoingProduct is null)
            return new(NayaxReplacementWriteDecision.ProductUnavailable, OutgoingProductUnavailableMessage);

        if (choice.IncomingQuantity > incoming.QuantityInStock)
        {
            return new(
                NayaxReplacementWriteDecision.InsufficientStorage,
                InsufficientStorageMessage(choice.IncomingQuantity, incoming.QuantityInStock));
        }

        return new(NayaxReplacementWriteDecision.Apply, "Applied.");
    }

    /// <summary>
    /// The movements one accepted choice consists of. Nothing is written for a quantity of zero and
    /// nothing at all for <see cref="NayaxReplacementOutgoingDisposition.None"/>, so a confirmed
    /// replacement that moved no units is recorded as one without a movement to show for it.
    /// </summary>
    public static NayaxReplacementMovementPlan Movements(NayaxProductReplacementChoice choice) =>
        new(
            WritesIncomingRefill: choice.IncomingQuantity > 0,
            WritesOutgoingReturn: choice.OutgoingDisposition != NayaxReplacementOutgoingDisposition.None,
            WritesOutgoingWriteOff: WriteOffReasonOf(choice.OutgoingDisposition) is not null,
            WriteOffReason: WriteOffReasonOf(choice.OutgoingDisposition));

    /// <summary>
    /// The established write-off reason a disposal uses, or <see langword="null"/> when the choice
    /// discards nothing. It is a mapping onto the two reasons that already exist; issue #1326 adds
    /// no reason and changes neither one's meaning.
    /// </summary>
    private static StockAdjustmentReason? WriteOffReasonOf(NayaxReplacementOutgoingDisposition disposition) =>
        disposition switch
        {
            NayaxReplacementOutgoingDisposition.Damaged => StockAdjustmentReason.Damaged,
            NayaxReplacementOutgoingDisposition.Expired => StockAdjustmentReason.Expired,
            _ => null,
        };
}
