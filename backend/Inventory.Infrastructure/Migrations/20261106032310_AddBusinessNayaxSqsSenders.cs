using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBusinessNayaxSqsSenders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BusinessNayaxSqsSenderAudits",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BusinessId = table.Column<int>(type: "INTEGER", nullable: false),
                    FromAwsSenderId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    ToAwsSenderId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    FromIsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    ToIsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Revision = table.Column<int>(type: "INTEGER", nullable: false),
                    ActorDirectoryTenantId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ActorObjectId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    OccurredAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BusinessNayaxSqsSenderAudits", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BusinessNayaxSqsSenders",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BusinessId = table.Column<int>(type: "INTEGER", nullable: false),
                    AwsSenderId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastMessageReceivedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Revision = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BusinessNayaxSqsSenders", x => x.Id);
                    table.CheckConstraint("CK_BusinessNayaxSqsSenders_EnabledNeedsSenderId", "\"IsEnabled\" = 0 OR \"AwsSenderId\" IS NOT NULL");
                });

            migrationBuilder.CreateIndex(
                name: "IX_BusinessNayaxSqsSenderAudits_BusinessId",
                table: "BusinessNayaxSqsSenderAudits",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_BusinessNayaxSqsSenderAudits_BusinessId_OccurredAtUtc",
                table: "BusinessNayaxSqsSenderAudits",
                columns: new[] { "BusinessId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_BusinessNayaxSqsSenders_AwsSenderId",
                table: "BusinessNayaxSqsSenders",
                column: "AwsSenderId",
                unique: true,
                filter: "\"AwsSenderId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_BusinessNayaxSqsSenders_BusinessId",
                table: "BusinessNayaxSqsSenders",
                column: "BusinessId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BusinessNayaxSqsSenderAudits");

            migrationBuilder.DropTable(
                name: "BusinessNayaxSqsSenders");
        }
    }
}
