using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Migrations
{
    /// <summary>
    /// Adds the short-lived, encrypted Nayax connection-test draft table (issue #536, a slice of
    /// #506).
    ///
    /// Purely additive: one new table and its unique ownership index. No existing table, column,
    /// index or row is touched, nothing is backfilled, and no credential is moved - a business with
    /// no draft simply has no row, which is the state every business starts in and returns to.
    ///
    /// The index on <c>BusinessId</c> is unique, so "one draft per business, replaced by the next
    /// test" is a schema guarantee rather than a convention: a second decryptable token cannot exist
    /// for a business however the write path is called.
    /// </summary>
    public partial class AddNayaxConnectionTestDrafts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NayaxConnectionTestDrafts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    BusinessId = table.Column<int>(type: "INTEGER", nullable: false),
                    OperatorId = table.Column<string>(type: "TEXT", nullable: false),
                    AccessTokenCiphertext = table.Column<string>(type: "TEXT", nullable: false),
                    EncryptionKeyId = table.Column<string>(type: "TEXT", nullable: false),
                    ResultsJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedByDirectoryTenantId = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedByObjectId = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NayaxConnectionTestDrafts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NayaxConnectionTestDrafts_BusinessId",
                table: "NayaxConnectionTestDrafts",
                column: "BusinessId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NayaxConnectionTestDrafts");
        }
    }
}
