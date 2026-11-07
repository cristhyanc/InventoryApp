using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Migrations
{
    /// <summary>
    /// Issue #1105. One additive table and <strong>no backfill</strong>: the tenant-owned,
    /// append-only <c>NayaxSaleOwnerDecisionAudits</c> trail of business Owner decisions about flagged
    /// queue-delivered sales, each naming the issue #1104 evidence row it relied on, and of later
    /// evidence superseding them.
    ///
    /// <para><strong>The three new states need no schema.</strong> <c>NayaxSales.SqsVerification</c>
    /// gains <c>OwnerConfirmed</c>, <c>OwnerCorrected</c> and <c>OwnerExcluded</c>; the column is a
    /// nullable integer with no check constraint, so nothing is migrated. Every existing sale keeps the
    /// value it has: <c>NULL</c> for a sale the queue never delivered, which is decided by its status
    /// alone exactly as before.</para>
    ///
    /// <para><c>Down</c> drops the table. What that loses is the audit trail, never a sale or a figure;
    /// an Owner state already written stays in its column, and an older build reads a state it does
    /// not declare and, by the fail-closed rule in <c>NayaxSqsVerificationTransitions</c>, moves
    /// nothing. An older build would count an <c>OwnerExcluded</c> sale again, which is why rolling
    /// back past this migration after a decision was made needs a human decision.</para>
    /// </summary>
    public partial class AddNayaxSaleOwnerDecisions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NayaxSaleOwnerDecisionAudits",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BusinessId = table.Column<int>(type: "INTEGER", nullable: false),
                    NayaxSaleId = table.Column<long>(type: "INTEGER", nullable: false),
                    TransactionId = table.Column<long>(type: "INTEGER", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Decision = table.Column<int>(type: "INTEGER", nullable: true),
                    EvidenceId = table.Column<long>(type: "INTEGER", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    ActorDirectoryTenantId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    ActorObjectId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    OccurredAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    PreviousState = table.Column<int>(type: "INTEGER", nullable: false),
                    ResultingState = table.Column<int>(type: "INTEGER", nullable: false),
                    PreviousMachineId = table.Column<long>(type: "INTEGER", nullable: true),
                    ResultingMachineId = table.Column<long>(type: "INTEGER", nullable: true),
                    PreviousSettlementValue = table.Column<decimal>(type: "TEXT", nullable: true),
                    ResultingSettlementValue = table.Column<decimal>(type: "TEXT", nullable: true),
                    PreviousAuthorizationInstantUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ResultingAuthorizationInstantUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    PreviousNayaxProductId = table.Column<long>(type: "INTEGER", nullable: true),
                    ResultingNayaxProductId = table.Column<long>(type: "INTEGER", nullable: true),
                    PreviousProductId = table.Column<long>(type: "INTEGER", nullable: true),
                    ResultingProductId = table.Column<long>(type: "INTEGER", nullable: true),
                    PreviousCountedAmount = table.Column<decimal>(type: "TEXT", nullable: false),
                    ResultingCountedAmount = table.Column<decimal>(type: "TEXT", nullable: false),
                    ReconciledPeriodFrom = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ReconciledPeriodTo = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ReconciledPeriodDifference = table.Column<decimal>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NayaxSaleOwnerDecisionAudits", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NayaxSaleOwnerDecisionAudits_BusinessId",
                table: "NayaxSaleOwnerDecisionAudits",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_NayaxSaleOwnerDecisionAudits_BusinessId_NayaxSaleId_Id",
                table: "NayaxSaleOwnerDecisionAudits",
                columns: new[] { "BusinessId", "NayaxSaleId", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NayaxSaleOwnerDecisionAudits");
        }
    }
}
