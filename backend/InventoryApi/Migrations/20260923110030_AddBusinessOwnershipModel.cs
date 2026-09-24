using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InventoryApi.Migrations
{
    /// <inheritdoc />
    public partial class AddBusinessOwnershipModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Businesses",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Businesses", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BusinessMemberships",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BusinessId = table.Column<int>(type: "INTEGER", nullable: false),
                    DirectoryTenantId = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    ObjectId = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BusinessMemberships", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BusinessMemberships_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BusinessMemberships_BusinessId",
                table: "BusinessMemberships",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_BusinessMemberships_DirectoryTenantId_ObjectId",
                table: "BusinessMemberships",
                columns: new[] { "DirectoryTenantId", "ObjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_BusinessMemberships_DirectoryTenantId_ObjectId_BusinessId",
                table: "BusinessMemberships",
                columns: new[] { "DirectoryTenantId", "ObjectId", "BusinessId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BusinessMemberships");

            migrationBuilder.DropTable(
                name: "Businesses");
        }
    }
}
