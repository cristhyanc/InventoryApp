using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddNayaxMachineStockEventDuplicateResolution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DuplicateResolution",
                table: "NayaxMachineStockEvents",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "DuplicateResolvedAt",
                table: "NayaxMachineStockEvents",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MatchedManualStockAdjustmentId",
                table: "NayaxMachineStockEvents",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_NayaxMachineStockEvents_MatchedManualStockAdjustmentId",
                table: "NayaxMachineStockEvents",
                column: "MatchedManualStockAdjustmentId");

            migrationBuilder.AddForeignKey(
                name: "FK_NayaxMachineStockEvents_StockAdjustments_MatchedManualStockAdjustmentId",
                table: "NayaxMachineStockEvents",
                column: "MatchedManualStockAdjustmentId",
                principalTable: "StockAdjustments",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_NayaxMachineStockEvents_StockAdjustments_MatchedManualStockAdjustmentId",
                table: "NayaxMachineStockEvents");

            migrationBuilder.DropIndex(
                name: "IX_NayaxMachineStockEvents_MatchedManualStockAdjustmentId",
                table: "NayaxMachineStockEvents");

            migrationBuilder.DropColumn(
                name: "DuplicateResolution",
                table: "NayaxMachineStockEvents");

            migrationBuilder.DropColumn(
                name: "DuplicateResolvedAt",
                table: "NayaxMachineStockEvents");

            migrationBuilder.DropColumn(
                name: "MatchedManualStockAdjustmentId",
                table: "NayaxMachineStockEvents");
        }
    }
}
