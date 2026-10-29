using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Migrations
{
    /// <summary>
    /// Adds the attachment storage accounting of issue #968: the <c>AttachmentStorageReservations</c>
    /// table, and an <c>AttachmentVersion</c> column on <c>OperatingExpenses</c>.
    ///
    /// <para><strong>Both additions are additive and nothing is backfilled.</strong> Every expense
    /// that already exists starts at version <c>0</c> - the column's default - and no attachment's
    /// recorded size, stored name or content type is read or rewritten. A migration that touched an
    /// attachment's metadata would be a migration that could mis-charge a business for its own
    /// receipts. The reservations table starts empty, so no business is charged for an upload that
    /// never happened.</para>
    ///
    /// <para><strong>Why a reservation is a row.</strong> A document's bytes live outside the
    /// database, so writing the file and recording the row that refers to it cannot be one
    /// transaction. The reservation is what makes that gap safe: the bytes are charged to the
    /// business before anything is written, and on success the reservation becomes the attachment's
    /// own recorded size in the same transaction as the row. <c>RecordId</c> is deliberately
    /// <strong>not</strong> a foreign key - the purchase or expense it names may never come into
    /// existence, and one that is deleted while an upload is in progress must stay deletable.</para>
    ///
    /// <para><strong>Why the version is only on the expense.</strong> The operating-expense update
    /// is the one path that <em>replaces</em> an attachment; a purchase's document is attach-only-if-empty
    /// (issue #976), and that write makes its own decision with SQLite's write lock held. So this is
    /// the one row whose attachment can move on under an upload, and the one that needs a version to
    /// decide between two replacements.</para>
    ///
    /// <para>Rolling back drops both, which loses nothing a business recorded: a reservation is
    /// accounting about an upload in progress, and a version is derived from the writes that already
    /// happened.</para>
    /// </summary>
    /// <inheritdoc />
    public partial class AddAttachmentStorageQuota : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AttachmentVersion",
                table: "OperatingExpenses",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "AttachmentStorageReservations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BusinessId = table.Column<int>(type: "INTEGER", nullable: false),
                    Category = table.Column<int>(type: "INTEGER", nullable: false),
                    RecordId = table.Column<int>(type: "INTEGER", nullable: true),
                    UploadBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    ChargeableBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    LimitBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    RecordVersion = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AttachmentStorageReservations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AttachmentStorageReservations_BusinessId",
                table: "AttachmentStorageReservations",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_AttachmentStorageReservations_BusinessId_ExpiresAtUtc",
                table: "AttachmentStorageReservations",
                columns: new[] { "BusinessId", "ExpiresAtUtc" });
        }

        /// <summary>
        /// Drops both additions again.
        ///
        /// <para><strong>Why the column is dropped with hand-written SQL.</strong> EF's SQLite
        /// generator refuses <c>DropColumnOperation</c> and offers a model-driven table rebuild
        /// instead - which needs a point-in-time model, and for a <c>Down</c> that is the
        /// <em>preceding</em> migration's, which <c>AllowPurchaseWithoutDocument</c> deliberately
        /// records none of. SQLite itself has supported <c>ALTER TABLE ... DROP COLUMN</c> since
        /// 3.35, the column is in no index and no constraint, and the statement below is therefore
        /// both exact and independent of any recorded model.</para>
        /// </summary>
        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AttachmentStorageReservations");

            migrationBuilder.Sql(@"ALTER TABLE ""OperatingExpenses"" DROP COLUMN ""AttachmentVersion"";");
        }
    }
}
