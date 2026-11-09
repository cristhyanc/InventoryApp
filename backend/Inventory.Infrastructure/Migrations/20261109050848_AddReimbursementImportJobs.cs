using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddReimbursementImportJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ReimbursementImportJobs",
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
                    NextAttemptAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    Archive = table.Column<byte[]>(type: "BLOB", nullable: true),
                    FinishedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReimbursementImportJobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ReimbursementImportJobFiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BusinessId = table.Column<int>(type: "INTEGER", nullable: false),
                    ReimbursementImportJobId = table.Column<int>(type: "INTEGER", nullable: false),
                    EntryIndex = table.Column<int>(type: "INTEGER", nullable: false),
                    EntryPath = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    Outcome = table.Column<int>(type: "INTEGER", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    RecordedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReimbursementImportJobFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReimbursementImportJobFiles_ReimbursementImportJobs_ReimbursementImportJobId",
                        column: x => x.ReimbursementImportJobId,
                        principalTable: "ReimbursementImportJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReimbursementImportJobFiles_BusinessId",
                table: "ReimbursementImportJobFiles",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_ReimbursementImportJobFiles_BusinessId_ReimbursementImportJobId_EntryIndex",
                table: "ReimbursementImportJobFiles",
                columns: new[] { "BusinessId", "ReimbursementImportJobId", "EntryIndex" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReimbursementImportJobFiles_ReimbursementImportJobId",
                table: "ReimbursementImportJobFiles",
                column: "ReimbursementImportJobId");

            migrationBuilder.CreateIndex(
                name: "IX_ReimbursementImportJobs_BusinessId",
                table: "ReimbursementImportJobs",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_ReimbursementImportJobs_BusinessId_UploadedAtUtc",
                table: "ReimbursementImportJobs",
                columns: new[] { "BusinessId", "UploadedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ReimbursementImportJobs_State_NextAttemptAtUtc_UploadedAtUtc",
                table: "ReimbursementImportJobs",
                columns: new[] { "State", "NextAttemptAtUtc", "UploadedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReimbursementImportJobFiles");

            migrationBuilder.DropTable(
                name: "ReimbursementImportJobs");
        }
    }
}
