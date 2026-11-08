using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Migrations
{
    /// <summary>
    /// Issue #1107. One additive table and <strong>no backfill</strong>: the tenant-owned,
    /// append-only <c>NayaxSaleStatusChangeAudits</c> trail of stored sale statuses a Nayax SQS
    /// message changed - today only applying a refund, which moves a sale from status <c>12</c> to
    /// status <c>62</c>.
    ///
    /// <para><strong>The rule change needs no schema at all and no row is rewritten.</strong> A
    /// refunded sale now consumes physical machine stock and costing inventory
    /// (<c>NayaxSaleCountingPolicy.ConsumesStock</c>) while staying out of every completed-sale
    /// total, and both answers are computed from the <c>TransactionStatusId</c> and
    /// <c>SqsVerification</c> columns that already exist. Existing status-<c>62</c> rows therefore
    /// follow the new rule with no migration touching them: each one is picked up the next time its
    /// product's inventory cost is replayed, which is what makes the change restartable and
    /// reviewable rather than a mass update nobody can check. The owner's decision of 8 November 2026
    /// is that the rule applies to all of them, with no cut-off date.</para>
    ///
    /// <para><strong>What that means on deployment.</strong> A product with historical status-62
    /// sales loses one unit of <c>CostingQuantity</c> (and its value) per such sale when it is next
    /// replayed, because those units are now recognised as having left the business. No sale's money,
    /// no report total and no physical <c>QuantityInStock</c> changes. The read-only per-business,
    /// per-product count of existing status-62 rows is reported on the pull request so the owner
    /// knows which products those are before release.</para>
    ///
    /// <para><c>Down</c> drops the table. What that loses is the audit trail, never a sale or a
    /// figure: a status already moved to <c>62</c> stays in its column, and an older build reads it
    /// as a refund that consumes no stock - the behaviour it had before this change - so rolling back
    /// past this migration after a refund was applied needs a human decision about the costing
    /// position it would move back.</para>
    /// </summary>
    public partial class AddNayaxSaleStatusChangeAudits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NayaxSaleStatusChangeAudits",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BusinessId = table.Column<int>(type: "INTEGER", nullable: false),
                    NayaxSaleId = table.Column<long>(type: "INTEGER", nullable: false),
                    TransactionId = table.Column<long>(type: "INTEGER", nullable: false),
                    PreviousTransactionStatusId = table.Column<int>(type: "INTEGER", nullable: true),
                    NewTransactionStatusId = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceMessageId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    OccurredAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NayaxSaleStatusChangeAudits", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NayaxSaleStatusChangeAudits_BusinessId",
                table: "NayaxSaleStatusChangeAudits",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_NayaxSaleStatusChangeAudits_BusinessId_NayaxSaleId_Id",
                table: "NayaxSaleStatusChangeAudits",
                columns: new[] { "BusinessId", "NayaxSaleId", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NayaxSaleStatusChangeAudits");
        }
    }
}
