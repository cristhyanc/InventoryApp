using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InventoryApi.Migrations
{
    /// <summary>
    /// Creates the table the business bootstrap writes its evidence to (issue #64, checkpoint 3).
    ///
    /// Like every other migration in this repository, it is schema only. It assigns no owner to
    /// any row and moves no data: the backfill is performed exclusively by the human-invoked
    /// `bootstrap-business` command, so applying migrations - including the Database.Migrate()
    /// call at API startup - can never initiate one.
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
