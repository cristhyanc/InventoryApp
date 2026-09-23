using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InventoryApi.Migrations
{
    /// <summary>
    /// Makes every uniqueness rule on tenant-owned data per-business (issue #64, checkpoint 2).
    ///
    /// Each dropped index was globally unique, which under a shared database means one business
    /// can deny another a legitimate row: the same imported file, the same fee effective date,
    /// the same site agreement period. The replacements lead with BusinessId, so the rule applies
    /// within a business rather than across the whole table.
    ///
    /// NayaxSales additionally gains its own primary key. TransactionID is a remote Nayax
    /// identifier whose uniqueness is only guaranteed inside the operator account that issued it,
    /// so it becomes a unique value per business rather than the local key.
    ///
    /// No row is assigned an owner here; the backfill remains a separate, human-reviewed change.
    /// </summary>
    public partial class ScopeUniqueConstraintsByBusiness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SiteCommissionAgreements_SiteId_EffectiveFrom",
                table: "SiteCommissionAgreements");

            migrationBuilder.DropPrimaryKey(
                name: "PK_NayaxSales",
                table: "NayaxSales");

            migrationBuilder.DropIndex(
                name: "IX_NayaxProcessingFeeRates_EffectiveFrom",
                table: "NayaxProcessingFeeRates");

            migrationBuilder.DropIndex(
                name: "IX_InventoryCostTransitionBaselines_ProductId",
                table: "InventoryCostTransitionBaselines");

            migrationBuilder.DropIndex(
                name: "IX_ImportedFiles_FileHash",
                table: "ImportedFiles");

            migrationBuilder.AlterColumn<long>(
                name: "TransactionID",
                table: "NayaxSales",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "INTEGER")
                .OldAnnotation("Sqlite:Autoincrement", true);

            migrationBuilder.AddColumn<long>(
                name: "Id",
                table: "NayaxSales",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L)
                .Annotation("Sqlite:Autoincrement", true);

            migrationBuilder.AddPrimaryKey(
                name: "PK_NayaxSales",
                table: "NayaxSales",
                column: "Id");

            migrationBuilder.CreateIndex(
                name: "IX_SiteCommissionAgreements_BusinessId_SiteId_EffectiveFrom",
                table: "SiteCommissionAgreements",
                columns: new[] { "BusinessId", "SiteId", "EffectiveFrom" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NayaxSales_BusinessId_TransactionID",
                table: "NayaxSales",
                columns: new[] { "BusinessId", "TransactionID" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NayaxProcessingFeeRates_BusinessId_EffectiveFrom",
                table: "NayaxProcessingFeeRates",
                columns: new[] { "BusinessId", "EffectiveFrom" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InventoryCostTransitionBaselines_BusinessId_ProductId",
                table: "InventoryCostTransitionBaselines",
                columns: new[] { "BusinessId", "ProductId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InventoryCostTransitionBaselines_ProductId",
                table: "InventoryCostTransitionBaselines",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_ImportedFiles_BusinessId_FileHash",
                table: "ImportedFiles",
                columns: new[] { "BusinessId", "FileHash" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SiteCommissionAgreements_BusinessId_SiteId_EffectiveFrom",
                table: "SiteCommissionAgreements");

            migrationBuilder.DropPrimaryKey(
                name: "PK_NayaxSales",
                table: "NayaxSales");

            migrationBuilder.DropIndex(
                name: "IX_NayaxSales_BusinessId_TransactionID",
                table: "NayaxSales");

            migrationBuilder.DropIndex(
                name: "IX_NayaxProcessingFeeRates_BusinessId_EffectiveFrom",
                table: "NayaxProcessingFeeRates");

            migrationBuilder.DropIndex(
                name: "IX_InventoryCostTransitionBaselines_BusinessId_ProductId",
                table: "InventoryCostTransitionBaselines");

            migrationBuilder.DropIndex(
                name: "IX_InventoryCostTransitionBaselines_ProductId",
                table: "InventoryCostTransitionBaselines");

            migrationBuilder.DropIndex(
                name: "IX_ImportedFiles_BusinessId_FileHash",
                table: "ImportedFiles");

            migrationBuilder.DropColumn(
                name: "Id",
                table: "NayaxSales");

            migrationBuilder.AlterColumn<long>(
                name: "TransactionID",
                table: "NayaxSales",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "INTEGER")
                .Annotation("Sqlite:Autoincrement", true);

            migrationBuilder.AddPrimaryKey(
                name: "PK_NayaxSales",
                table: "NayaxSales",
                column: "TransactionID");

            migrationBuilder.CreateIndex(
                name: "IX_SiteCommissionAgreements_SiteId_EffectiveFrom",
                table: "SiteCommissionAgreements",
                columns: new[] { "SiteId", "EffectiveFrom" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NayaxProcessingFeeRates_EffectiveFrom",
                table: "NayaxProcessingFeeRates",
                column: "EffectiveFrom",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InventoryCostTransitionBaselines_ProductId",
                table: "InventoryCostTransitionBaselines",
                column: "ProductId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ImportedFiles_FileHash",
                table: "ImportedFiles",
                column: "FileHash",
                unique: true);
        }
    }
}
