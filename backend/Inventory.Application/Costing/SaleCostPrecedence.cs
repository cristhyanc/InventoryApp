namespace Inventory.Application.Costing;

/// <summary>
/// How many of the four historical sale-cost precedence steps a <see cref="ICostSale"/> call applies
/// (issue #1261).
/// </summary>
public enum SaleCostPrecedenceScope
{
    /// <summary>
    /// Steps 1, 3 and 4: the internal ledger cost, else the sale's own Nayax cost, else uncosted.
    /// This is the sale-time path, where the inventory-cost rebuild that follows the stored sale -
    /// the pending-rebuild drain, or an import's own rebuild - replays step 2 straight afterwards.
    /// </summary>
    WithoutLatestPurchase = 0,

    /// <summary>
    /// All four steps, including step 2 - the latest costed purchase replayed before the sale, stored
    /// as <see cref="SaleCostOrigin.Estimated"/>. It is what a forced backfill applies: no rebuild
    /// runs inside its transaction to apply step 2 for it, and costing a step-2 sale through step 3
    /// is what let the tool downgrade a cost it re-computed.
    /// </summary>
    Complete = 1,
}

/// <summary>
/// The historical sale-cost precedence of <c>AGENTS.md</c> § Inventory and historical costing
/// invariants, as the one question a backfill asks before it replaces a sale's stored cost (issue
/// #1261).
///
/// <para><strong>Why it exists.</strong> <c>force</c> used to mean "overwrite", so a repair tool
/// could replace a better cost with a worse one: <see cref="BackfillNayaxHistoricalSaleCosts"/> wrote
/// the transaction-level Nayax cost (step 3) over a sale costed from the inventory ledger (step 1),
/// and <see cref="BackfillSaleCosts"/> re-costed a latest-purchase estimate (step 2) through a path
/// that landed on step 3. A forced backfill may now only improve a sale's cost - or correct a cost of
/// the same provenance, which is what a corrected Nayax export cost is.</para>
///
/// <para>It decides nothing about <em>which</em> cost a step produces: <see cref="ICostSale"/> and
/// <see cref="Inventory.Domain.Costing.WeightedAverageCostReplay"/> stay the only places a cost is
/// computed. This compares the provenance of what is stored with the provenance of what was
/// decided.</para>
/// </summary>
public static class SaleCostPrecedence
{
    /// <summary>Precedence step 4: the sale is left uncosted.</summary>
    private const int Uncosted = 4;

    /// <summary>
    /// The precedence step of a cost with this provenance, lower being better: the internal
    /// AVCO/ledger cost (1), the latest costed purchase before the sale (2), the transaction-level
    /// Nayax <c>Product Cost Price</c> (3), and uncosted (4) - which is also where a cost with no
    /// recorded provenance sits, because a number nobody can account for is the weakest claim of all.
    /// </summary>
    public static int Step(SaleCostOrigin source) => source switch
    {
        SaleCostOrigin.InventoryLedger => 1,
        SaleCostOrigin.Estimated => 2,
        SaleCostOrigin.NayaxTransactionExport => 3,
        _ => Uncosted,
    };

    /// <summary>
    /// Whether <paramref name="decided"/> may be written over <paramref name="stored"/>'s current
    /// cost: only a better precedence step, or the same step with a different value. A sale that
    /// carries no final cost yet takes the decision whatever it is, including one that leaves the
    /// sale uncosted - that records a status and a provenance rather than losing a cost - unless the
    /// decision is already exactly what the row holds, in which case there is nothing to write.
    /// </summary>
    public static bool Replaces(CostableSale stored, SaleCostAssignment decided)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(decided);

        if (stored.CostingStatus is not (SaleCostStatus.Costed or SaleCostStatus.LegacyEstimated) ||
            stored.UnitCostAtSale is null)
        {
            // Nothing final is stored, so any decision may be written - unless it is exactly what is
            // on the row already, which would record a change that did not happen and ask for a cost
            // rebuild nothing needs.
            return stored.CostingStatus != decided.Status ||
                stored.CostSource != decided.Source ||
                stored.UnitCostAtSale != decided.UnitCost ||
                stored.CostOfGoodsSold != decided.UnitCost;
        }

        // Uncosted is step 4 - the weakest step there is - so a decision that costs nothing never
        // clears a cost that is stored: a backfill is a repair tool, not a way to erase cost history.
        if (decided.UnitCost is null)
            return false;

        var storedStep = Step(stored.CostSource);
        var decidedStep = Step(decided.Source);

        return decidedStep == storedStep
            ? stored.UnitCostAtSale != decided.UnitCost
            : decidedStep < storedStep;
    }
}
