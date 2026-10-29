using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPlanGrantRevisionAndAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Revision",
                table: "BusinessPlanGrants",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "BusinessPlanGrantAudits",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BusinessId = table.Column<int>(type: "INTEGER", nullable: false),
                    Change = table.Column<int>(type: "INTEGER", nullable: false),
                    FromPlan = table.Column<int>(type: "INTEGER", nullable: true),
                    ToPlan = table.Column<int>(type: "INTEGER", nullable: true),
                    EndsAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Revision = table.Column<int>(type: "INTEGER", nullable: false),
                    ActorDirectoryTenantId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ActorObjectId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    OccurredAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BusinessPlanGrantAudits", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BusinessPlanGrantAudits_BusinessId",
                table: "BusinessPlanGrantAudits",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_BusinessPlanGrantAudits_BusinessId_OccurredAtUtc",
                table: "BusinessPlanGrantAudits",
                columns: new[] { "BusinessId", "OccurredAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BusinessPlanGrantAudits");

            migrationBuilder.DropColumn(
                name: "Revision",
                table: "BusinessPlanGrants");
        }
    }
}
