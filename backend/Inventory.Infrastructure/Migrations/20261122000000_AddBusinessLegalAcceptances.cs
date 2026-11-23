using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBusinessLegalAcceptances : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BusinessLegalAcceptances",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BusinessId = table.Column<int>(type: "INTEGER", nullable: false),
                    DirectoryTenantId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ObjectId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Version = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    AcceptedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Context = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BusinessLegalAcceptances", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BusinessLegalAcceptances_BusinessId",
                table: "BusinessLegalAcceptances",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_BusinessLegalAcceptances_BusinessId_AcceptedAtUtc",
                table: "BusinessLegalAcceptances",
                columns: new[] { "BusinessId", "AcceptedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BusinessLegalAcceptances");
        }
    }
}
