using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Migrations
{
    /// <summary>
    /// Issue #1104. Two additive changes and <strong>no backfill of any kind</strong>: the
    /// tenant-owned, append-only <c>NayaxSaleProviderEvidences</c> table that records what each
    /// business's own Nayax <c>lastSales</c> said about a queue-delivered sale, and one nullable
    /// column on <c>BusinessNayaxSqsSenders</c> that says when that business's hourly reconciliation
    /// last fully succeeded.
    ///
    /// <para><strong>The third change the issue lists has no schema in it, deliberately.</strong>
    /// <c>NayaxSales.SqsVerification</c> gains the <c>Verified</c>, <c>Conflict</c> and
    /// <c>NeedsInvestigation</c> values, and the column is a nullable integer with no check
    /// constraint and no lookup table - so the new values need nothing migrated. Every sale this
    /// database already holds keeps exactly the value it has: <c>NULL</c> for a sale the queue never
    /// delivered, and <c>Unverified</c> for one it did. Nothing moves a row to a new state except a
    /// reconciliation run that observed provider evidence for it.</para>
    ///
    /// <para>No stored status, cost, quantity, instant or amount is changed by this migration, and a
    /// deployment in which no business has an enabled SQS sender recorded behaves identically with it
    /// applied: the hourly runner sweeps nobody and the evidence table stays empty.</para>
    ///
    /// <para><c>Down</c> drops the table and the column. What that loses is the <em>evidence</em> and
    /// the reconciliation's own success instant - never a sale, a cost or a figure - and the
    /// verification states already written to <c>NayaxSales.SqsVerification</c> are left exactly as
    /// they are, because they are values in a column that predates this migration. An older build
    /// reads a state it does not declare and, by the fail-closed rule in
    /// <c>NayaxSqsVerificationTransitions</c>, moves nothing.</para>
    /// </summary>
    public partial class AddNayaxSaleProviderEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastReconciliationSucceededAtUtc",
                table: "BusinessNayaxSqsSenders",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "NayaxSaleProviderEvidences",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BusinessId = table.Column<int>(type: "INTEGER", nullable: false),
                    NayaxSaleId = table.Column<long>(type: "INTEGER", nullable: false),
                    RunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ObservedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    ResultingState = table.Column<int>(type: "INTEGER", nullable: false),
                    ProviderMachineId = table.Column<long>(type: "INTEGER", nullable: true),
                    ProviderSettlementValue = table.Column<decimal>(type: "TEXT", nullable: true),
                    ProviderAuthorizationInstantUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ProviderCurrencyCode = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    ProviderNayaxProductId = table.Column<long>(type: "INTEGER", nullable: true),
                    ProviderProductName = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    MachineComparison = table.Column<int>(type: "INTEGER", nullable: true),
                    AmountComparison = table.Column<int>(type: "INTEGER", nullable: true),
                    InstantComparison = table.Column<int>(type: "INTEGER", nullable: true),
                    CurrencyComparison = table.Column<int>(type: "INTEGER", nullable: true),
                    ProductComparison = table.Column<int>(type: "INTEGER", nullable: true),
                    ReadMachineId = table.Column<long>(type: "INTEGER", nullable: true),
                    ReturnedEarliestInstantUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ReturnedLatestInstantUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ReturnedItemCount = table.Column<int>(type: "INTEGER", nullable: true),
                    SaleAuthorizationInstantUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SaleReceivedViaSqsAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    OutsideReceiptGrace = table.Column<bool>(type: "INTEGER", nullable: true),
                    Bracketed = table.Column<bool>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NayaxSaleProviderEvidences", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NayaxSaleProviderEvidences_BusinessId",
                table: "NayaxSaleProviderEvidences",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_NayaxSaleProviderEvidences_BusinessId_NayaxSaleId_Kind_Id",
                table: "NayaxSaleProviderEvidences",
                columns: new[] { "BusinessId", "NayaxSaleId", "Kind", "Id" },
                descending: new[] { false, false, false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NayaxSaleProviderEvidences");

            migrationBuilder.DropColumn(
                name: "LastReconciliationSucceededAtUtc",
                table: "BusinessNayaxSqsSenders");
        }
    }
}
