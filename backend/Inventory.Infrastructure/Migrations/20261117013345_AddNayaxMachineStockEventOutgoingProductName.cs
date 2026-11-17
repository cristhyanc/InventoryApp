using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Migrations
{
    /// <summary>
    /// Lets an imported Nayax machine-stock event keep both names a product replacement reports
    /// (issue #1325): one additive, nullable <c>ParsedOutgoingProductName</c> column on
    /// <c>NayaxMachineStockEvents</c>. For a 510 "Machine Product Changed" alert the existing
    /// <c>ParsedProductName</c> holds the incoming product and this column the outgoing one.
    ///
    /// <para><strong>There is no backfill.</strong> Every existing row is a 501 stock adjustment,
    /// which reports one product, so <c>NULL</c> is exactly right for it and its meaning is
    /// unchanged. Earlier 510 alerts were never stored, and <c>lastAlerts</c> returns only recent
    /// alerts, so there is nothing to recover them from.</para>
    ///
    /// <para><strong>Why <c>ALTER TABLE ... DROP COLUMN</c> on the way back.</strong> The provider's
    /// own <c>DropColumn</c> would rebuild the whole table to remove one nullable column. SQLite has
    /// dropped such a column in place since 3.35, which the bundled library provides, exactly as
    /// <c>AddStockAdjustmentMachineLedgerEffectiveAt</c> already does. Rolling back loses the
    /// outgoing names of any replacement imported since, which is inherent: the older schema has
    /// nowhere to keep them.</para>
    /// </summary>
    public partial class AddNayaxMachineStockEventOutgoingProductName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ParsedOutgoingProductName",
                table: "NayaxMachineStockEvents",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "ALTER TABLE \"NayaxMachineStockEvents\" DROP COLUMN \"ParsedOutgoingProductName\";");
        }
    }
}
