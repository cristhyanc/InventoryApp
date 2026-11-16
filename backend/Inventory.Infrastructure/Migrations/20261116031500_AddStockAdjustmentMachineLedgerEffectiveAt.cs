using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Migrations
{
    /// <summary>
    /// Records the instant the machine-stock ledger replays a refill at, when that is not the instant
    /// the movement was recorded (issue #1243, finding INV-06 of audit #1222): one additive, nullable
    /// <c>MachineLedgerEffectiveAt</c> column on <c>StockAdjustments</c>, and nothing else.
    ///
    /// <para><strong>Why the column exists.</strong> A Nayax Sync Restock event is applied by an
    /// operator days after the machine was physically refilled. The movement it writes took the apply
    /// instant, the machine-stock ledger replays movements by effective instant, and a machine count
    /// sets the expected level from its own instant onward - so a 10 November event applied on 13
    /// November was replayed after a 12 November count and added its units on top of the level that
    /// count had already observed. Expected machine stock became 35 where the operator had counted 15,
    /// and the next comparison reported 20 units of shrinkage that never happened. The apply now
    /// records the event's own <c>EventDateTimeGMT</c> here, and
    /// <c>EfMachineStockLedgerStore</c> - the only reader of this column - replays the refill there.
    /// <c>EffectiveAt</c> is deliberately untouched and stays the apply instant, because it is what
    /// the cost replay and Stock History read: dating the storage movement earlier could take units
    /// out of storage before they were there and make physical stock negative at that instant.</para>
    ///
    /// <para><strong>Nullable, with no backfill.</strong> A movement recorded before this migration
    /// keeps today's behaviour exactly: the ledger falls back to <c>EffectiveAt</c> when the column is
    /// <c>NULL</c>, which is also what every manual refill records for ever. Re-dating movements that
    /// were already applied is explicitly a separate repair, so nothing here reads, writes or infers a
    /// value for an existing row - no quantity, cost, value, movement or event is touched.</para>
    ///
    /// <para><strong>Why <c>ALTER TABLE ... DROP COLUMN</c> on the way back.</strong> Dropping the
    /// column through the provider's own <c>DropColumn</c> would rebuild <c>StockAdjustments</c> - the
    /// table every stock movement, cost replay and machine refill hangs off - to remove one unindexed,
    /// unconstrained column, and it would need a point-in-time model to rebuild from. SQLite has
    /// dropped such a column in place since 3.35, which the bundled library this application runs on
    /// provides, exactly as <c>AddProductCostingVersion</c> and <c>AddPurchaseInvoiceNumber</c>
    /// already do. Rolling back loses the recorded event instants and returns the ledger to replaying
    /// every refill at its apply time, which is the behaviour of the release before this one.</para>
    /// </summary>
    public partial class AddStockAdjustmentMachineLedgerEffectiveAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "MachineLedgerEffectiveAt",
                table: "StockAdjustments",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE \"StockAdjustments\" DROP COLUMN \"MachineLedgerEffectiveAt\";");
        }
    }
}
