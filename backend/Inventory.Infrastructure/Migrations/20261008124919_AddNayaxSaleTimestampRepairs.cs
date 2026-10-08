using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddNayaxSaleTimestampRepairs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NayaxSaleTimestampRepairPreviewDrafts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    BusinessId = table.Column<int>(type: "INTEGER", nullable: false),
                    PlanJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AppliedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NayaxSaleTimestampRepairPreviewDrafts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NayaxSaleTimestampRepairs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BusinessId = table.Column<int>(type: "INTEGER", nullable: false),
                    TransactionId = table.Column<long>(type: "INTEGER", nullable: false),
                    MachineId = table.Column<long>(type: "INTEGER", nullable: false),
                    PreviousInstantUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RepairedInstantUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    PreviousBusinessDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RepairedBusinessDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EvidenceSource = table.Column<int>(type: "INTEGER", nullable: false),
                    EvidenceReference = table.Column<string>(type: "TEXT", nullable: false),
                    PreviewId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AppliedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AppliedByDirectoryTenantId = table.Column<string>(type: "TEXT", nullable: false),
                    AppliedByObjectId = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NayaxSaleTimestampRepairs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NayaxSaleTimestampRepairPreviewDrafts_BusinessId",
                table: "NayaxSaleTimestampRepairPreviewDrafts",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_NayaxSaleTimestampRepairs_BusinessId",
                table: "NayaxSaleTimestampRepairs",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_NayaxSaleTimestampRepairs_BusinessId_TransactionId_AppliedAt",
                table: "NayaxSaleTimestampRepairs",
                columns: new[] { "BusinessId", "TransactionId", "AppliedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NayaxSaleTimestampRepairPreviewDrafts");

            migrationBuilder.DropTable(
                name: "NayaxSaleTimestampRepairs");
        }
    }
}
