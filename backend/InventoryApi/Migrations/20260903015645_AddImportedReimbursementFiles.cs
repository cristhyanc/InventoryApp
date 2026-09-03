using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InventoryApi.Migrations
{
    /// <inheritdoc />
    public partial class AddImportedReimbursementFiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ImportedFiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    FileName = table.Column<string>(type: "TEXT", nullable: false),
                    FileHash = table.Column<string>(type: "TEXT", nullable: false),
                    ImportedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportedFiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ImportedReimbursements",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ImportedFileId = table.Column<int>(type: "INTEGER", nullable: false),
                    ReportType = table.Column<string>(type: "TEXT", nullable: true),
                    ReimbursementStartDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ReimbursementEndDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ReimbursementPayoutDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CompanyName = table.Column<string>(type: "TEXT", nullable: true),
                    CustomerId = table.Column<string>(type: "TEXT", nullable: true),
                    IsReimbursement = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsInvoice = table.Column<bool>(type: "INTEGER", nullable: false),
                    Email = table.Column<string>(type: "TEXT", nullable: true),
                    ActiveDevices = table.Column<int>(type: "INTEGER", nullable: true),
                    TotalDevices = table.Column<int>(type: "INTEGER", nullable: true),
                    InvoicePaymentMethod = table.Column<string>(type: "TEXT", nullable: true),
                    InvoicePaymentMethodId = table.Column<string>(type: "TEXT", nullable: true),
                    Total = table.Column<decimal>(type: "decimal(18,4)", nullable: true),
                    SupportEmail = table.Column<string>(type: "TEXT", nullable: true),
                    ErpId = table.Column<string>(type: "TEXT", nullable: true),
                    DistributorActorId = table.Column<string>(type: "TEXT", nullable: true),
                    FinanceEntityId = table.Column<string>(type: "TEXT", nullable: true),
                    RawAttributesJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportedReimbursements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ImportedReimbursements_ImportedFiles_ImportedFileId",
                        column: x => x.ImportedFileId,
                        principalTable: "ImportedFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ImportedDevicePayments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ImportedReimbursementId = table.Column<int>(type: "INTEGER", nullable: false),
                    EntityId = table.Column<string>(type: "TEXT", nullable: true),
                    PaymentMethodDescription = table.Column<string>(type: "TEXT", nullable: true),
                    RecognitionDescription = table.Column<string>(type: "TEXT", nullable: true),
                    SalesCount = table.Column<int>(type: "INTEGER", nullable: true),
                    TotalSum = table.Column<decimal>(type: "decimal(18,4)", nullable: true),
                    ProcessingFees = table.Column<decimal>(type: "decimal(18,4)", nullable: true),
                    ServiceFees = table.Column<decimal>(type: "decimal(18,4)", nullable: true),
                    RawAttributesJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportedDevicePayments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ImportedDevicePayments_ImportedReimbursements_ImportedReimbursementId",
                        column: x => x.ImportedReimbursementId,
                        principalTable: "ImportedReimbursements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ImportedFees",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ImportedReimbursementId = table.Column<int>(type: "INTEGER", nullable: false),
                    FeesTypeId = table.Column<string>(type: "TEXT", nullable: true),
                    FeeTypeDescription = table.Column<string>(type: "TEXT", nullable: true),
                    IsService = table.Column<bool>(type: "INTEGER", nullable: false),
                    TotalSum = table.Column<decimal>(type: "decimal(18,4)", nullable: true),
                    TotalSumWithVat = table.Column<decimal>(type: "decimal(18,4)", nullable: true),
                    VatPercentage = table.Column<decimal>(type: "decimal(18,4)", nullable: true),
                    AverageFeeAmount = table.Column<decimal>(type: "decimal(18,4)", nullable: true),
                    TotalCount = table.Column<decimal>(type: "decimal(18,4)", nullable: true),
                    IsPreviousPeriod = table.Column<bool>(type: "INTEGER", nullable: false),
                    RawAttributesJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportedFees", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ImportedFees_ImportedReimbursements_ImportedReimbursementId",
                        column: x => x.ImportedReimbursementId,
                        principalTable: "ImportedReimbursements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ImportedPaymentMethods",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ImportedReimbursementId = table.Column<int>(type: "INTEGER", nullable: false),
                    PaymentMethodId = table.Column<string>(type: "TEXT", nullable: true),
                    PaymentMethodDescription = table.Column<string>(type: "TEXT", nullable: true),
                    IsNayaxReimbursement = table.Column<bool>(type: "INTEGER", nullable: false),
                    BillingProvider = table.Column<string>(type: "TEXT", nullable: true),
                    TotalSalesCount = table.Column<int>(type: "INTEGER", nullable: true),
                    TotalSalesSum = table.Column<decimal>(type: "decimal(18,4)", nullable: true),
                    RecognitionDescription = table.Column<string>(type: "TEXT", nullable: true),
                    IsPreviousPeriod = table.Column<bool>(type: "INTEGER", nullable: false),
                    RawAttributesJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportedPaymentMethods", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ImportedPaymentMethods_ImportedReimbursements_ImportedReimbursementId",
                        column: x => x.ImportedReimbursementId,
                        principalTable: "ImportedReimbursements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ImportedReimbursementDevices",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ImportedReimbursementId = table.Column<int>(type: "INTEGER", nullable: false),
                    EntityId = table.Column<string>(type: "TEXT", nullable: true),
                    MachineType = table.Column<string>(type: "TEXT", nullable: true),
                    VerticalTypeName = table.Column<string>(type: "TEXT", nullable: true),
                    VerticalProductTypeName = table.Column<string>(type: "TEXT", nullable: true),
                    HardwareSerial = table.Column<string>(type: "TEXT", nullable: true),
                    MachineNumber = table.Column<string>(type: "TEXT", nullable: true),
                    ActorCode = table.Column<string>(type: "TEXT", nullable: true),
                    Location = table.Column<string>(type: "TEXT", nullable: true),
                    TotalBillableTransactionCount = table.Column<int>(type: "INTEGER", nullable: true),
                    TotalBillableTransactionAmount = table.Column<decimal>(type: "decimal(18,4)", nullable: true),
                    TotalNotBillableTransactionCount = table.Column<int>(type: "INTEGER", nullable: true),
                    TotalNotBillableTransactionAmount = table.Column<decimal>(type: "decimal(18,4)", nullable: true),
                    ServiceFee = table.Column<decimal>(type: "decimal(18,4)", nullable: true),
                    ProcessingFee = table.Column<decimal>(type: "decimal(18,4)", nullable: true),
                    HasServicePerTransaction = table.Column<bool>(type: "INTEGER", nullable: false),
                    TotalExtraCharge = table.Column<decimal>(type: "decimal(18,4)", nullable: true),
                    NetAmount = table.Column<decimal>(type: "decimal(18,4)", nullable: true),
                    RawAttributesJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportedReimbursementDevices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ImportedReimbursementDevices_ImportedReimbursements_ImportedReimbursementId",
                        column: x => x.ImportedReimbursementId,
                        principalTable: "ImportedReimbursements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ImportedDevicePayments_ImportedReimbursementId",
                table: "ImportedDevicePayments",
                column: "ImportedReimbursementId");

            migrationBuilder.CreateIndex(
                name: "IX_ImportedFees_ImportedReimbursementId",
                table: "ImportedFees",
                column: "ImportedReimbursementId");

            migrationBuilder.CreateIndex(
                name: "IX_ImportedFiles_FileHash",
                table: "ImportedFiles",
                column: "FileHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ImportedPaymentMethods_ImportedReimbursementId",
                table: "ImportedPaymentMethods",
                column: "ImportedReimbursementId");

            migrationBuilder.CreateIndex(
                name: "IX_ImportedReimbursementDevices_ImportedReimbursementId",
                table: "ImportedReimbursementDevices",
                column: "ImportedReimbursementId");

            migrationBuilder.CreateIndex(
                name: "IX_ImportedReimbursements_ImportedFileId",
                table: "ImportedReimbursements",
                column: "ImportedFileId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ImportedDevicePayments");

            migrationBuilder.DropTable(
                name: "ImportedFees");

            migrationBuilder.DropTable(
                name: "ImportedPaymentMethods");

            migrationBuilder.DropTable(
                name: "ImportedReimbursementDevices");

            migrationBuilder.DropTable(
                name: "ImportedReimbursements");

            migrationBuilder.DropTable(
                name: "ImportedFiles");
        }
    }
}
