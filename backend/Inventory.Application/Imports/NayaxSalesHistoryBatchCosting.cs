using Inventory.Application.Costing;

namespace Inventory.Application.Imports;

/// <summary>
/// Gives each batch of the background sales history import (issue #1304) the sale costing it
/// applies its rows with.
/// </summary>
public interface INayaxSalesHistoryBatchCosting
{
    /// <summary>
    /// The sale costing for one batch's row pass. It may rely on nothing being saved until the pass
    /// ends, so a caller asks for a new one for every batch and never keeps one past its batch.
    /// </summary>
    ICostSale ForOneBatch();
}

/// <summary>
/// The production <see cref="INayaxSalesHistoryBatchCosting"/>: the ordinary <see cref="CostSale"/>
/// over the ordinary <see cref="RebuildProductCost"/>, whose ledger reads go through a
/// <see cref="SnapshotInventoryCostLedgerReads"/> made for that one batch.
///
/// <para>The rules are <see cref="CostSale"/>'s and the replay is <see cref="RebuildProductCost"/>'s,
/// unchanged; the only difference from the costing <see cref="ImportNayaxSales"/> is given is how
/// often a product's ledger is read from the database. A batch's row pass saves nothing until it
/// ends, so every as-of read inside it sees the same rows whether it is made against the database or
/// against the one read the snapshot took - and a product's ledger is then read once per batch rather
/// than once per row, which is what kept the write-lock hold growing with the sales already stored
/// for the product. The snapshot is thrown away with the batch, so the next batch reads the rows the
/// previous one committed.</para>
/// </summary>
public sealed class NayaxSalesHistoryBatchCosting : INayaxSalesHistoryBatchCosting
{
    private readonly ISaleCostingStore _saleCosts;
    private readonly IInventoryCostLedgerStore _ledgers;

    public NayaxSalesHistoryBatchCosting(ISaleCostingStore saleCosts, IInventoryCostLedgerStore ledgers)
    {
        _saleCosts = saleCosts;
        _ledgers = ledgers;
    }

    public ICostSale ForOneBatch() =>
        new CostSale(_saleCosts, new RebuildProductCost(new SnapshotInventoryCostLedgerReads(_ledgers)));
}
