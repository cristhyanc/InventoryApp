using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Migrations
{
    /// <summary>
    /// Adds the per-allocation machine a multi-machine refill batch records (issue #897):
    /// <c>PickListRefillLines.MachineId</c>, nullable, with the same composite
    /// <c>(BusinessId, MachineId)</c> foreign key on the machines' <c>(BusinessId, Id)</c> key,
    /// restricted on delete, that every other column naming a local machine carries (AGENTS.md §
    /// Stock machine references).
    ///
    /// <para><strong>Purely additive, and nothing is backfilled.</strong> Every line recorded before
    /// this migration belongs to a single-machine or no-machine refill whose machine is on the batch
    /// row, so it keeps <c>NULL</c> here: copying the batch's machine down onto its lines would
    /// rewrite what those submissions recorded, and leave no way to tell a refill that named one
    /// machine from a batch that happened to name the same one. The idempotency keys, the batches and
    /// their lines are untouched.</para>
    ///
    /// <para>SQLite cannot add a foreign key to an existing table, so EF Core applies this one by
    /// rebuilding <c>PickListRefillLines</c> and copying its rows across - which is why the upgrade
    /// test asserts the existing refills, lines and keys survive it, and why the tables historical
    /// costing is derived from (<c>Products</c>, <c>StockAdjustments</c>, <c>Machines</c>) are proved
    /// byte-identical before and after. Production applies migrations at startup (AGENTS.md § Database
    /// and migrations), and the code already running there neither reads nor writes this column: it
    /// writes <c>PickListRefillLines</c> without naming it, which a nullable column accepts.</para>
    /// </summary>
    /// <inheritdoc />
    public partial class AddPickListRefillLineMachine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "MachineId",
                table: "PickListRefillLines",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PickListRefillLines_BusinessId_MachineId",
                table: "PickListRefillLines",
                columns: new[] { "BusinessId", "MachineId" });

            migrationBuilder.AddForeignKey(
                name: "FK_PickListRefillLines_Machines_BusinessId_MachineId",
                table: "PickListRefillLines",
                columns: new[] { "BusinessId", "MachineId" },
                principalTable: "Machines",
                principalColumns: new[] { "BusinessId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PickListRefillLines_Machines_BusinessId_MachineId",
                table: "PickListRefillLines");

            migrationBuilder.DropIndex(
                name: "IX_PickListRefillLines_BusinessId_MachineId",
                table: "PickListRefillLines");

            migrationBuilder.DropColumn(
                name: "MachineId",
                table: "PickListRefillLines");
        }
    }
}
