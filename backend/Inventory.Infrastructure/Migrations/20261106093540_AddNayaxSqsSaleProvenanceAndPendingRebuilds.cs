using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Migrations
{
    /// <summary>
    /// Issue #1103. Two additive changes and <strong>no backfill of any kind</strong>: three nullable
    /// columns on <c>NayaxSales</c> that say a sale arrived from the shared Nayax SQS queue, and the
    /// tenant-owned <c>PendingInventoryCostRebuilds</c> table that records which products still have
    /// to have their inventory cost replayed, with the <c>Revision</c> an in-flight replay's
    /// acknowledgement is conditional on.
    ///
    /// <para>Every sale this database already holds keeps <c>NULL</c> in all three columns, which is
    /// exactly what keeps "did not arrive from the queue" and "arrived from the queue" apart. No
    /// stored status, cost, quantity or instant is changed by this migration, and a deployment that
    /// never configures a queue behaves identically with it applied.</para>
    ///
    /// <para><c>Down</c> drops the table and the three columns, which loses only provenance and
    /// transient replay work - never a sale, a status or a cost. It is safe because nothing read the
    /// three columns before this release and nothing else refers to the table.</para>
    /// </summary>
    public partial class AddNayaxSqsSaleProvenanceAndPendingRebuilds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ReceivedViaSqsAtUtc",
                table: "NayaxSales",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SqsFirstMessageId",
                table: "NayaxSales",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SqsVerification",
                table: "NayaxSales",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PendingInventoryCostRebuilds",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BusinessId = table.Column<int>(type: "INTEGER", nullable: false),
                    ProductId = table.Column<long>(type: "INTEGER", nullable: false),
                    FromInstantUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastAttemptAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastFailureCode = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PendingInventoryCostRebuilds", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PendingInventoryCostRebuilds_BusinessId",
                table: "PendingInventoryCostRebuilds",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_PendingInventoryCostRebuilds_BusinessId_CreatedAtUtc",
                table: "PendingInventoryCostRebuilds",
                columns: new[] { "BusinessId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PendingInventoryCostRebuilds_BusinessId_ProductId",
                table: "PendingInventoryCostRebuilds",
                columns: new[] { "BusinessId", "ProductId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PendingInventoryCostRebuilds");

            migrationBuilder.DropColumn(
                name: "ReceivedViaSqsAtUtc",
                table: "NayaxSales");

            migrationBuilder.DropColumn(
                name: "SqsFirstMessageId",
                table: "NayaxSales");

            migrationBuilder.DropColumn(
                name: "SqsVerification",
                table: "NayaxSales");
        }
    }
}
