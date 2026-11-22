using Inventory.Domain.Exceptions;

namespace Inventory.Domain.Stock;

/// <summary>
/// The deterministic validation an operator-entered manual stock adjustment must pass before any
/// movement is applied (issue #282), mirroring the former
/// <c>InventoryApi.Services.StockService.Adjust</c> inline checks and their exact messages: a
/// Correction must remove stock, and a positive Restock must carry a non-negative unit cost.
/// Validation never reads or writes persisted state; the caller has already confirmed the product
/// exists.
///
/// <para><b>Each reason moves stock in one direction only</b> (issue #1242). Damaged, Expired and
/// Sale must be negative and Restock must be positive, beside the Correction rule that was always
/// negative-only, because the cost replay gives a positive write-off and a negative Restock no
/// costing effect at all: "Damaged 5" added 5 units to storage and recorded no loss, and
/// "Restock -4" lowered physical stock while costing quantity and inventory value stayed where they
/// were. MachineRefill is deliberately unconstrained: it is an internal transfer whose sign is
/// storage's point of view, negative out to a machine and positive back, and it is costing-neutral
/// in both directions.</para>
///
/// <para><b>A manual Machine refill must name its machine</b> (issue #1331). The sign rules above
/// are unchanged, but a refill with no machine changed storage stock and never reached any machine's
/// stock ledger - the ledger reads only the refills that recorded a machine - so the transfer had
/// one half and no other. The reasons that name no machine are unaffected.</para>
/// </summary>
public static class ManualStockAdjustmentPolicy
{
    /// <summary>
    /// What a caller is told when a manual adjustment names a machine that is not one of this
    /// business's machines (issue #741). Since #741 the field holds a local machine id, so a provider
    /// machine number sent by an older client is one of the values refused. The rule needs the
    /// business's machines, so the use case applies it; the message lives here with the
    /// adjustment's other refusals. It names no machine and no business.
    /// </summary>
    public const string UnknownMachineMessage =
        "The selected machine is not a machine of this business.";

    /// <summary>
    /// What a caller is told when a manual Machine refill names no machine (issue #1331). It names
    /// the field to fill rather than any machine: the refill is a transfer between storage and one
    /// named machine, and without the machine only storage's half of it would be recorded.
    /// </summary>
    public const string MachineRequiredMessage =
        "A Machine refill must name the machine it moves stock to or from: choose a machine.";

    /// <summary>
    /// What a caller is told when a Correction does not remove stock. The exact sentence the policy
    /// has always thrown, now named so the matrix test and the API test state one message.
    /// </summary>
    public const string CorrectionRemovesStockMessage = "Correction quantity must remove stock.";

    /// <summary>
    /// What a caller is told when a Restock does not add stock (issue #1242). It names the sign a
    /// Restock expects and the reasons that do remove stock, because a negative Restock is the
    /// mistake this refusal exists for: it lowered physical stock while the cost replay gave it no
    /// costing effect at all, so storage and costing quantities drifted apart for ever.
    /// </summary>
    public const string RestockAddsStockMessage =
        "A Restock adjustment must add stock: enter a positive quantity. "
        + "Use Correction, Damaged or Expired to remove stock.";

    /// <summary>
    /// What a caller is told when a write-off reason does not remove stock (issue #1242), naming
    /// the reason and the sign expected of it.
    /// </summary>
    public static string RemovesStockMessage(StockAdjustmentReason reason) =>
        $"A {reason} adjustment must remove stock: enter a negative quantity.";

    public static void Validate(
        StockAdjustmentReason reason, int quantityChange, decimal? unitCost, long? machineId)
    {
        // A refill keeps both signs, and both of them move stock between storage and one machine
        // (issue #1331). Every other reason records no machine and is unaffected.
        if (reason == StockAdjustmentReason.MachineRefill && machineId is null)
            throw new DomainValidationException(MachineRequiredMessage);

        if (reason == StockAdjustmentReason.Correction && quantityChange >= 0)
            throw new DomainValidationException(CorrectionRemovesStockMessage);

        if (reason is (StockAdjustmentReason.Damaged or StockAdjustmentReason.Expired or StockAdjustmentReason.Sale)
            && quantityChange >= 0)
        {
            throw new DomainValidationException(RemovesStockMessage(reason));
        }

        if (reason == StockAdjustmentReason.Restock)
        {
            if (quantityChange <= 0)
                throw new DomainValidationException(RestockAddsStockMessage);
            if (!unitCost.HasValue)
                throw new DomainValidationException("Unit cost is required for a positive Restock adjustment.");
            if (unitCost.Value < 0)
                throw new DomainValidationException("Unit cost cannot be negative for a positive Restock adjustment.");
        }
    }
}
