using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAttachmentStorageQuota : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AttachmentVersion",
                table: "OperatingExpenses",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "AttachmentStorageReservations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BusinessId = table.Column<int>(type: "INTEGER", nullable: false),
                    Category = table.Column<int>(type: "INTEGER", nullable: false),
                    RecordId = table.Column<int>(type: "INTEGER", nullable: true),
                    UploadBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    ChargeableBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    LimitBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    RecordVersion = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AttachmentStorageReservations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AttachmentStorageReservations_BusinessId",
                table: "AttachmentStorageReservations",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_AttachmentStorageReservations_BusinessId_ExpiresAtUtc",
                table: "AttachmentStorageReservations",
                columns: new[] { "BusinessId", "ExpiresAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AttachmentStorageReservations");

            migrationBuilder.DropColumn(
                name: "AttachmentVersion",
                table: "OperatingExpenses");
        }
    }
}
