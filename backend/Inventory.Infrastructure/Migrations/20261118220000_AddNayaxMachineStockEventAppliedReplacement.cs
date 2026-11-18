using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Migrations
{
    /// <summary>
    /// Lets an imported Nayax machine-stock event record the product replacement an operator applied
    /// (issue #1326): seven additive, nullable columns on <c>NayaxMachineStockEvents</c> holding what
    /// was confirmed - the product loaded and how many units of it, what happened to the units taken
    /// out and how many - and the movements the apply created for it. The existing
    /// <c>StockAdjustmentId</c> keeps its meaning and holds the <em>incoming</em> refill, so the
    /// outgoing return and the <c>Damaged</c>/<c>Expired</c> write-off beside it get their own two
    /// columns.
    ///
    /// <para><strong>There is no backfill, and nothing historical changes.</strong> Every existing
    /// row is either a 501 stock adjustment, which has no replacement to record, or a 510 alert that
    /// could only be marked "already recorded manually" before this issue; <c>NULL</c> is exactly
    /// right for both and their meaning is unchanged. No movement, quantity, cost or inventory value
    /// is touched by this migration.</para>
    ///
    /// <para><strong>Why no foreign key on the two product columns.</strong> They are the record of
    /// a decision rather than a key anything joins on, exactly like <c>StockAdjustmentId</c> and the
    /// two movement columns added here, and the apply validates each product as an <em>active
    /// product of the caller's own business</em> inside its own write transaction, through the
    /// central tenant query filters, before a single movement is staged. A constraint would make
    /// SQLite rebuild the whole imported-event table for an audit column, which this additive
    /// migration deliberately avoids; <c>MatchedProductId</c>'s own foreign key is unchanged.</para>
    ///
    /// <para><strong>Why <c>ALTER TABLE ... DROP COLUMN</c> on the way back.</strong> The provider's
    /// own <c>DropColumn</c> would rebuild the whole table to remove a nullable column. SQLite has
    /// dropped such a column in place since 3.35, which the bundled library provides, exactly as
    /// <c>AddNayaxMachineStockEventOutgoingProductName</c> and
    /// <c>AddStockAdjustmentMachineLedgerEffectiveAt</c> already do. Rolling back loses the record of
    /// which products and quantities an applied replacement confirmed - the movements it created
    /// remain, as ordinary stock movements - which is inherent: the older schema has nowhere to keep
    /// it.</para>
    /// </summary>
    public partial class AddNayaxMachineStockEventAppliedReplacement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "ReplacementIncomingProductId",
                table: "NayaxMachineStockEvents",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReplacementIncomingQuantity",
                table: "NayaxMachineStockEvents",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReplacementOutgoingDisposition",
                table: "NayaxMachineStockEvents",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ReplacementOutgoingProductId",
                table: "NayaxMachineStockEvents",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReplacementOutgoingQuantity",
                table: "NayaxMachineStockEvents",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReplacementOutgoingReturnStockAdjustmentId",
                table: "NayaxMachineStockEvents",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReplacementOutgoingWriteOffStockAdjustmentId",
                table: "NayaxMachineStockEvents",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "ALTER TABLE \"NayaxMachineStockEvents\" DROP COLUMN \"ReplacementIncomingProductId\";");
            migrationBuilder.Sql(
                "ALTER TABLE \"NayaxMachineStockEvents\" DROP COLUMN \"ReplacementIncomingQuantity\";");
            migrationBuilder.Sql(
                "ALTER TABLE \"NayaxMachineStockEvents\" DROP COLUMN \"ReplacementOutgoingDisposition\";");
            migrationBuilder.Sql(
                "ALTER TABLE \"NayaxMachineStockEvents\" DROP COLUMN \"ReplacementOutgoingProductId\";");
            migrationBuilder.Sql(
                "ALTER TABLE \"NayaxMachineStockEvents\" DROP COLUMN \"ReplacementOutgoingQuantity\";");
            migrationBuilder.Sql(
                "ALTER TABLE \"NayaxMachineStockEvents\" DROP COLUMN \"ReplacementOutgoingReturnStockAdjustmentId\";");
            migrationBuilder.Sql(
                "ALTER TABLE \"NayaxMachineStockEvents\" DROP COLUMN \"ReplacementOutgoingWriteOffStockAdjustmentId\";");
        }
    }
}
