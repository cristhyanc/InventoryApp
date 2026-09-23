using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InventoryApi.Migrations
{
    /// <summary>
    /// Creates the table the business bootstrap writes its evidence to (issue #64, checkpoint 3).
    ///
    /// This migration creates an empty table and touches no existing row. Like every migration
    /// here it assigns no tenant ownership and performs no business backfill - that is exclusively
    /// the human-invoked `bootstrap-business` command. (Other migrations in this repository do
    /// rebuild tables and copy rows; this one does not.)
    ///
    /// The table is empty until that command runs. A populated row per table per run is the
    /// durable record of what the backfill did: rows unassigned before, rows assigned, rows left
    /// unassigned, and the table's total row count on both sides.
    /// </summary>
    public partial class AddBusinessBackfillAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BusinessBackfillAudits",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    BusinessId = table.Column<int>(type: "INTEGER", nullable: false),
                    TableName = table.Column<string>(type: "TEXT", nullable: false),
                    UnassignedRowsBefore = table.Column<long>(type: "INTEGER", nullable: false),
                    RowsAssigned = table.Column<long>(type: "INTEGER", nullable: false),
                    UnassignedRowsAfter = table.Column<long>(type: "INTEGER", nullable: false),
                    TotalRowsBefore = table.Column<long>(type: "INTEGER", nullable: false),
                    TotalRowsAfter = table.Column<long>(type: "INTEGER", nullable: false),
                    RecordedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BusinessBackfillAudits", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BusinessBackfillAudits_RunId",
                table: "BusinessBackfillAudits",
                column: "RunId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BusinessBackfillAudits");
        }
    }
}
