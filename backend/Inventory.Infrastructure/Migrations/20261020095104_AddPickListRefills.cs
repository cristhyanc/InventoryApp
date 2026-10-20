using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPickListRefills : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PickListRefills",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BusinessId = table.Column<int>(type: "INTEGER", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    RequestFingerprint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    MachineId = table.Column<long>(type: "INTEGER", nullable: true),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    RecordedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PickListRefills", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PickListRefills_Machines_BusinessId_MachineId",
                        columns: x => new { x.BusinessId, x.MachineId },
                        principalTable: "Machines",
                        principalColumns: new[] { "BusinessId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PickListRefillLines",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BusinessId = table.Column<int>(type: "INTEGER", nullable: false),
                    RefillId = table.Column<long>(type: "INTEGER", nullable: false),
                    ProductId = table.Column<long>(type: "INTEGER", nullable: false),
                    QuantityTaken = table.Column<int>(type: "INTEGER", nullable: false),
                    StockAdjustmentId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PickListRefillLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PickListRefillLines_PickListRefills_RefillId",
                        column: x => x.RefillId,
                        principalTable: "PickListRefills",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PickListRefillLines_BusinessId",
                table: "PickListRefillLines",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_PickListRefillLines_BusinessId_RefillId_Id",
                table: "PickListRefillLines",
                columns: new[] { "BusinessId", "RefillId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_PickListRefillLines_RefillId",
                table: "PickListRefillLines",
                column: "RefillId");

            migrationBuilder.CreateIndex(
                name: "IX_PickListRefills_BusinessId",
                table: "PickListRefills",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_PickListRefills_BusinessId_IdempotencyKey",
                table: "PickListRefills",
                columns: new[] { "BusinessId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PickListRefills_BusinessId_MachineId",
                table: "PickListRefills",
                columns: new[] { "BusinessId", "MachineId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PickListRefillLines");

            migrationBuilder.DropTable(
                name: "PickListRefills");
        }
    }
}
