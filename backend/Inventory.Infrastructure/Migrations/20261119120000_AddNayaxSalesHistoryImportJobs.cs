using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddNayaxSalesHistoryImportJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NayaxSalesHistoryImportJobs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BusinessId = table.Column<int>(type: "INTEGER", nullable: false),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    UploadedByDirectoryTenantId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    UploadedByObjectId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    UploadedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalRows = table.Column<int>(type: "INTEGER", nullable: false),
                    RowsRead = table.Column<int>(type: "INTEGER", nullable: false),
                    Created = table.Column<int>(type: "INTEGER", nullable: false),
                    Updated = table.Column<int>(type: "INTEGER", nullable: false),
                    Skipped = table.Column<int>(type: "INTEGER", nullable: false),
                    Invalid = table.Column<int>(type: "INTEGER", nullable: false),
                    KeptStoredValues = table.Column<int>(type: "INTEGER", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    File = table.Column<byte[]>(type: "BLOB", nullable: true),
                    FinishedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NayaxSalesHistoryImportJobs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NayaxSalesHistoryImportJobs_BusinessId",
                table: "NayaxSalesHistoryImportJobs",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_NayaxSalesHistoryImportJobs_BusinessId_UploadedAtUtc",
                table: "NayaxSalesHistoryImportJobs",
                columns: new[] { "BusinessId", "UploadedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_NayaxSalesHistoryImportJobs_State_UploadedAtUtc",
                table: "NayaxSalesHistoryImportJobs",
                columns: new[] { "State", "UploadedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NayaxSalesHistoryImportJobs");
        }
    }
}
