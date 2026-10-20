using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Migrations
{
    /// <summary>
    /// Makes <c>StockAdjustments.MachineId</c> and <c>MachineStockCounts.MachineId</c> foreign keys on
    /// the local machine (issue #741), once <c>RepointStockMachineReferences</c> has moved both onto
    /// <c>Machines.Id</c>.
    ///
    /// <para><strong>Why this is a migration of its own.</strong> SQLite cannot add or drop a foreign
    /// key in place, so EF Core rebuilds each table, and it does that at the end of the migration
    /// that asks for it, after every other statement in it. Kept in one migration with the repoint,
    /// <c>Down</c> would write the provider's numbers back while the foreign key still stood - a
    /// provider number is not a local machine, so the downgrade would fail on every database with a
    /// machine reference. Split, the constraint is dropped and the tables rebuilt here first, and the
    /// repoint's own <c>Down</c> then writes the numbers back into columns nothing constrains. Up
    /// runs the other way round: the repoint has already verified every reference names a machine
    /// of its own business, so the constraints added here cannot fail on the data.</para>
    /// </summary>
    public partial class AddStockMachineForeignKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // MachineStockCounts already leads an index with (BusinessId, MachineId), which serves its
            // foreign key; StockAdjustments has none.
            migrationBuilder.CreateIndex(
                name: "IX_StockAdjustments_BusinessId_MachineId",
                table: "StockAdjustments",
                columns: new[] { "BusinessId", "MachineId" });

            migrationBuilder.AddForeignKey(
                name: "FK_MachineStockCounts_Machines_BusinessId_MachineId",
                table: "MachineStockCounts",
                columns: new[] { "BusinessId", "MachineId" },
                principalTable: "Machines",
                principalColumns: new[] { "BusinessId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_StockAdjustments_Machines_BusinessId_MachineId",
                table: "StockAdjustments",
                columns: new[] { "BusinessId", "MachineId" },
                principalTable: "Machines",
                principalColumns: new[] { "BusinessId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MachineStockCounts_Machines_BusinessId_MachineId",
                table: "MachineStockCounts");

            migrationBuilder.DropForeignKey(
                name: "FK_StockAdjustments_Machines_BusinessId_MachineId",
                table: "StockAdjustments");

            migrationBuilder.DropIndex(
                name: "IX_StockAdjustments_BusinessId_MachineId",
                table: "StockAdjustments");
        }
    }
}
