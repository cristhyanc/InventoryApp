using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InventoryApi.Migrations
{
    /// <summary>
    /// Adds the BusinessId ownership column and its index to every tenant-owned table
    /// (issue #64, checkpoint 2).
    ///
    /// This migration assigns no existing row to a business. Every pre-existing record keeps the
    /// column default of 0, which is not a valid business key, so it is owned by nobody and is
    /// invisible to every scoped caller until the separate, human-reviewed backfill assigns it.
    /// That is deliberate: a schema change that silently handed all existing financial and
    /// inventory history to whichever business signed in first would be the exact accident this
    /// ownership boundary exists to prevent.
    /// </summary>
    public partial class AddBusinessOwnershipToTenantOwnedEntities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "BusinessId",
                table: "Suppliers",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "BusinessId",
                table: "SupplierOrders",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "BusinessId",
                table: "SupplierOrderReceiptAllocations",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "BusinessId",
                table: "SupplierOrderLines",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "BusinessId",
                table: "StockAdjustments",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "BusinessId",
                table: "SiteCommissionAgreements",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "BusinessId",
                table: "Receipts",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "BusinessId",
                table: "ReceiptItems",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "BusinessId",
                table: "Products",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "BusinessId",
                table: "OperatingExpenses",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "BusinessId",
                table: "NayaxSales",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "BusinessId",
                table: "NayaxProcessingFeeRates",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "BusinessId",
                table: "InventoryCostTransitionPreviewDrafts",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "BusinessId",
                table: "InventoryCostTransitionMachineStocks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "BusinessId",
                table: "InventoryCostTransitionBaselines",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "BusinessId",
                table: "ImportedReimbursements",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "BusinessId",
                table: "ImportedReimbursementDevices",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "BusinessId",
                table: "ImportedPaymentMethods",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "BusinessId",
                table: "ImportedFiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "BusinessId",
                table: "ImportedFees",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "BusinessId",
                table: "ImportedDevicePayments",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "BusinessId",
                table: "CommissionPayments",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "BusinessId",
                table: "Categories",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_Suppliers_BusinessId",
                table: "Suppliers",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierOrders_BusinessId",
                table: "SupplierOrders",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierOrderReceiptAllocations_BusinessId",
                table: "SupplierOrderReceiptAllocations",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierOrderLines_BusinessId",
                table: "SupplierOrderLines",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_StockAdjustments_BusinessId",
                table: "StockAdjustments",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_SiteCommissionAgreements_BusinessId",
                table: "SiteCommissionAgreements",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_Receipts_BusinessId",
                table: "Receipts",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_ReceiptItems_BusinessId",
                table: "ReceiptItems",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_Products_BusinessId",
                table: "Products",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_OperatingExpenses_BusinessId",
                table: "OperatingExpenses",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_NayaxSales_BusinessId",
                table: "NayaxSales",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_NayaxProcessingFeeRates_BusinessId",
                table: "NayaxProcessingFeeRates",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryCostTransitionPreviewDrafts_BusinessId",
                table: "InventoryCostTransitionPreviewDrafts",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryCostTransitionMachineStocks_BusinessId",
                table: "InventoryCostTransitionMachineStocks",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryCostTransitionBaselines_BusinessId",
                table: "InventoryCostTransitionBaselines",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_ImportedReimbursements_BusinessId",
                table: "ImportedReimbursements",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_ImportedReimbursementDevices_BusinessId",
                table: "ImportedReimbursementDevices",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_ImportedPaymentMethods_BusinessId",
                table: "ImportedPaymentMethods",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_ImportedFiles_BusinessId",
                table: "ImportedFiles",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_ImportedFees_BusinessId",
                table: "ImportedFees",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_ImportedDevicePayments_BusinessId",
                table: "ImportedDevicePayments",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_CommissionPayments_BusinessId",
                table: "CommissionPayments",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_Categories_BusinessId",
                table: "Categories",
                column: "BusinessId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Suppliers_BusinessId",
                table: "Suppliers");

            migrationBuilder.DropIndex(
                name: "IX_SupplierOrders_BusinessId",
                table: "SupplierOrders");

            migrationBuilder.DropIndex(
                name: "IX_SupplierOrderReceiptAllocations_BusinessId",
                table: "SupplierOrderReceiptAllocations");

            migrationBuilder.DropIndex(
                name: "IX_SupplierOrderLines_BusinessId",
                table: "SupplierOrderLines");

            migrationBuilder.DropIndex(
                name: "IX_StockAdjustments_BusinessId",
                table: "StockAdjustments");

            migrationBuilder.DropIndex(
                name: "IX_SiteCommissionAgreements_BusinessId",
                table: "SiteCommissionAgreements");

            migrationBuilder.DropIndex(
                name: "IX_Receipts_BusinessId",
                table: "Receipts");

            migrationBuilder.DropIndex(
                name: "IX_ReceiptItems_BusinessId",
                table: "ReceiptItems");

            migrationBuilder.DropIndex(
                name: "IX_Products_BusinessId",
                table: "Products");

            migrationBuilder.DropIndex(
                name: "IX_OperatingExpenses_BusinessId",
                table: "OperatingExpenses");

            migrationBuilder.DropIndex(
                name: "IX_NayaxSales_BusinessId",
                table: "NayaxSales");

            migrationBuilder.DropIndex(
                name: "IX_NayaxProcessingFeeRates_BusinessId",
                table: "NayaxProcessingFeeRates");

            migrationBuilder.DropIndex(
                name: "IX_InventoryCostTransitionPreviewDrafts_BusinessId",
                table: "InventoryCostTransitionPreviewDrafts");

            migrationBuilder.DropIndex(
                name: "IX_InventoryCostTransitionMachineStocks_BusinessId",
                table: "InventoryCostTransitionMachineStocks");

            migrationBuilder.DropIndex(
                name: "IX_InventoryCostTransitionBaselines_BusinessId",
                table: "InventoryCostTransitionBaselines");

            migrationBuilder.DropIndex(
                name: "IX_ImportedReimbursements_BusinessId",
                table: "ImportedReimbursements");

            migrationBuilder.DropIndex(
                name: "IX_ImportedReimbursementDevices_BusinessId",
                table: "ImportedReimbursementDevices");

            migrationBuilder.DropIndex(
                name: "IX_ImportedPaymentMethods_BusinessId",
                table: "ImportedPaymentMethods");

            migrationBuilder.DropIndex(
                name: "IX_ImportedFiles_BusinessId",
                table: "ImportedFiles");

            migrationBuilder.DropIndex(
                name: "IX_ImportedFees_BusinessId",
                table: "ImportedFees");

            migrationBuilder.DropIndex(
                name: "IX_ImportedDevicePayments_BusinessId",
                table: "ImportedDevicePayments");

            migrationBuilder.DropIndex(
                name: "IX_CommissionPayments_BusinessId",
                table: "CommissionPayments");

            migrationBuilder.DropIndex(
                name: "IX_Categories_BusinessId",
                table: "Categories");

            migrationBuilder.DropColumn(
                name: "BusinessId",
                table: "Suppliers");

            migrationBuilder.DropColumn(
                name: "BusinessId",
                table: "SupplierOrders");

            migrationBuilder.DropColumn(
                name: "BusinessId",
                table: "SupplierOrderReceiptAllocations");

            migrationBuilder.DropColumn(
                name: "BusinessId",
                table: "SupplierOrderLines");

            migrationBuilder.DropColumn(
                name: "BusinessId",
                table: "StockAdjustments");

            migrationBuilder.DropColumn(
                name: "BusinessId",
                table: "SiteCommissionAgreements");

            migrationBuilder.DropColumn(
                name: "BusinessId",
                table: "Receipts");

            migrationBuilder.DropColumn(
                name: "BusinessId",
                table: "ReceiptItems");

            migrationBuilder.DropColumn(
                name: "BusinessId",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "BusinessId",
                table: "OperatingExpenses");

            migrationBuilder.DropColumn(
                name: "BusinessId",
                table: "NayaxSales");

            migrationBuilder.DropColumn(
                name: "BusinessId",
                table: "NayaxProcessingFeeRates");

            migrationBuilder.DropColumn(
                name: "BusinessId",
                table: "InventoryCostTransitionPreviewDrafts");

            migrationBuilder.DropColumn(
                name: "BusinessId",
                table: "InventoryCostTransitionMachineStocks");

            migrationBuilder.DropColumn(
                name: "BusinessId",
                table: "InventoryCostTransitionBaselines");

            migrationBuilder.DropColumn(
                name: "BusinessId",
                table: "ImportedReimbursements");

            migrationBuilder.DropColumn(
                name: "BusinessId",
                table: "ImportedReimbursementDevices");

            migrationBuilder.DropColumn(
                name: "BusinessId",
                table: "ImportedPaymentMethods");

            migrationBuilder.DropColumn(
                name: "BusinessId",
                table: "ImportedFiles");

            migrationBuilder.DropColumn(
                name: "BusinessId",
                table: "ImportedFees");

            migrationBuilder.DropColumn(
                name: "BusinessId",
                table: "ImportedDevicePayments");

            migrationBuilder.DropColumn(
                name: "BusinessId",
                table: "CommissionPayments");

            migrationBuilder.DropColumn(
                name: "BusinessId",
                table: "Categories");
        }
    }
}
