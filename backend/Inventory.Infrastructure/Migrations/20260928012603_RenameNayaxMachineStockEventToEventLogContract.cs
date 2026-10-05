using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RenameNayaxMachineStockEventToEventLogContract : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "NayaxEventId",
                table: "NayaxMachineStockEvents",
                newName: "NayaxEventLogId");

            migrationBuilder.RenameColumn(
                name: "EventTimestamp",
                table: "NayaxMachineStockEvents",
                newName: "EventDateTimeGmt");

            migrationBuilder.RenameIndex(
                name: "IX_NayaxMachineStockEvents_BusinessId_NayaxEventId",
                table: "NayaxMachineStockEvents",
                newName: "IX_NayaxMachineStockEvents_BusinessId_NayaxEventLogId");

            migrationBuilder.AddColumn<DateTime>(
                name: "EventDateTimeVmc",
                table: "NayaxMachineStockEvents",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EventDateTimeVmc",
                table: "NayaxMachineStockEvents");

            migrationBuilder.RenameColumn(
                name: "NayaxEventLogId",
                table: "NayaxMachineStockEvents",
                newName: "NayaxEventId");

            migrationBuilder.RenameColumn(
                name: "EventDateTimeGmt",
                table: "NayaxMachineStockEvents",
                newName: "EventTimestamp");

            migrationBuilder.RenameIndex(
                name: "IX_NayaxMachineStockEvents_BusinessId_NayaxEventLogId",
                table: "NayaxMachineStockEvents",
                newName: "IX_NayaxMachineStockEvents_BusinessId_NayaxEventId");
        }
    }
}
