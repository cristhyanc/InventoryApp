using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Migrations
{
    /// <summary>
    /// Adds the single-use member invite table (issue #531, a slice of #504 under #328).
    ///
    /// Purely additive: one new table, its tenant-ownership index, the per-business unique index on
    /// the token hash, and the index backing the foreign key to the membership a redemption creates.
    /// No existing table, column, index or row is touched, nothing is backfilled, and no access
    /// changes - a business with no invites simply has no row, which is the state every business
    /// starts in.
    ///
    /// The table stores the invite token only as a SHA-256 hash, so a copy of this database yields
    /// no usable invite link, and it holds no plaintext secret of any kind. The unique index is on
    /// <c>(BusinessId, TokenHash)</c> rather than on the hash alone, which is the repository-wide
    /// rule for uniqueness on tenant-owned data: a global constraint would let one business's row
    /// deny another's.
    /// </summary>
    public partial class AddMemberInvites : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MemberInvites",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BusinessId = table.Column<int>(type: "INTEGER", nullable: false),
                    Role = table.Column<int>(type: "INTEGER", nullable: false),
                    Note = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    TokenHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedByDirectoryTenantId = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedByObjectId = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UsedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CancelledAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RedeemedByMembershipId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MemberInvites", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MemberInvites_BusinessMemberships_RedeemedByMembershipId",
                        column: x => x.RedeemedByMembershipId,
                        principalTable: "BusinessMemberships",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MemberInvites_BusinessId",
                table: "MemberInvites",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_MemberInvites_BusinessId_TokenHash",
                table: "MemberInvites",
                columns: new[] { "BusinessId", "TokenHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MemberInvites_RedeemedByMembershipId",
                table: "MemberInvites",
                column: "RedeemedByMembershipId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MemberInvites");
        }
    }
}
