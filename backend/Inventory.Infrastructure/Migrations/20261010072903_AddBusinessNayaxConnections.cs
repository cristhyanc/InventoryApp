using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Migrations
{
    /// <summary>
    /// Gives each business a place to hold its own Nayax Lynx credentials (issue #518). Purely
    /// additive: one new table, one unique index on its ownership column, and no other table
    /// touched.
    ///
    /// It stores nothing and infers nothing. The existing business keeps using the single
    /// configured <c>NayaxLynx</c> operator/token - no credential is copied out of configuration
    /// here, because a migration must never move a secret, and moving the existing one is the
    /// human-run command of issue #519. Every business therefore starts with no row, which is the
    /// <c>NotConfigured</c> state, and nothing reads the table until issue #520.
    ///
    /// The unique index is on <c>BusinessId</c> alone: one connection per business is a schema
    /// guarantee, so a credential save can only create the first row or update the existing one.
    /// </summary>
    public partial class AddBusinessNayaxConnections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BusinessNayaxConnections",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BusinessId = table.Column<int>(type: "INTEGER", nullable: false),
                    OperatorId = table.Column<string>(type: "TEXT", nullable: false),
                    AccessTokenCiphertext = table.Column<string>(type: "TEXT", nullable: false),
                    EncryptionKeyId = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    CredentialRevision = table.Column<int>(type: "INTEGER", nullable: false),
                    LastTestedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BusinessNayaxConnections", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BusinessNayaxConnections_BusinessId",
                table: "BusinessNayaxConnections",
                column: "BusinessId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BusinessNayaxConnections");
        }
    }
}
