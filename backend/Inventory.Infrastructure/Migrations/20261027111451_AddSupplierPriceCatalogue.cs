using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Migrations
{
    /// <summary>
    /// Creates the shared supplier price catalogue (issue #923, parent #817): three platform tables
    /// about products, one tenant-owned table about a business's own decision, their indexes and
    /// their check constraints.
    ///
    /// <para><strong>Purely additive, and it writes no data at all.</strong> Four new tables and
    /// nothing else: no column of an existing table is added, dropped or rewritten, no row is
    /// inserted, updated or deleted, and nothing is backfilled. No existing product, purchase, cost,
    /// inventory or report behaviour can therefore change - which is issue #923's last criterion, and
    /// why there is no seed here to reason about (unlike <c>AddSupplierPresets</c>, whose rows are a
    /// platform list somebody has to be given).</para>
    ///
    /// <para><strong>The constraints are the point of the schema.</strong>
    /// <c>CK_SupplierListings_OneOwner</c> is the issue's own criterion - a listing references exactly
    /// one of a master product and an active waiting key - and it is a database check rather than only
    /// an application rule because a row with both or neither is not something an application-level
    /// check can be trusted to have prevented. <c>OriginWaitingKey</c> is a separate column precisely
    /// so a listing that has been matched can still record which search found it without violating
    /// that constraint. The pack, size, barcode-and-scope, one-proposal and confidence checks are the
    /// same kind of fact: a pack size of zero divides, a barcode with no scope prices one can at the
    /// price of thirty, and a suggestion proposing both an existing master product and a new one
    /// leaves a reviewer unable to tell which was meant.</para>
    ///
    /// <para><strong>Every reference restricts except one.</strong> A supplier master record, a master
    /// product and a proposed master product all have rows depending on them, so none can be deleted
    /// from under those rows - which is also what keeps a rejected suggestion's evidence from being
    /// removed by removing the product it was about. The one cascade is
    /// <c>ProductPriceTrackings</c>'s own product, on the products' <c>(BusinessId, Id)</c> business
    /// key: a mapping is a statement about a product and has no meaning without it, and restricting
    /// there would change what deleting a product does.</para>
    ///
    /// <para><strong>Reversing it drops the four tables and nothing else</strong>, which is what
    /// reversing a purely additive migration means. There is no data to restore because none was
    /// written.</para>
    /// </summary>
    /// <inheritdoc />
    public partial class AddSupplierPriceCatalogue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MasterProducts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Brand = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    SizeValue = table.Column<decimal>(type: "decimal(18,3)", nullable: false),
                    SizeUnit = table.Column<int>(type: "INTEGER", nullable: false),
                    NormalisedKey = table.Column<string>(type: "TEXT", maxLength: 683, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MasterProducts", x => x.Id);
                    table.CheckConstraint("CK_MasterProducts_SizeValuePositive", "\"SizeValue\" > 0");
                });

            migrationBuilder.CreateTable(
                name: "ProductMatchSuggestions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WaitingKey = table.Column<string>(type: "TEXT", maxLength: 1001, nullable: false),
                    MasterProductId = table.Column<long>(type: "INTEGER", nullable: true),
                    SuggestedBrand = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    SuggestedName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    SuggestedSizeValue = table.Column<decimal>(type: "decimal(18,3)", nullable: true),
                    SuggestedSizeUnit = table.Column<int>(type: "INTEGER", nullable: true),
                    Confidence = table.Column<decimal>(type: "decimal(5,4)", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    CollectorRun = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductMatchSuggestions", x => x.Id);
                    table.CheckConstraint("CK_ProductMatchSuggestions_ConfidenceIsAProportion", "\"Confidence\" >= 0 AND \"Confidence\" <= 1");
                    table.CheckConstraint("CK_ProductMatchSuggestions_OneProposal", "(\"MasterProductId\" IS NOT NULL AND \"SuggestedBrand\" IS NULL AND \"SuggestedName\" IS NULL AND \"SuggestedSizeValue\" IS NULL AND \"SuggestedSizeUnit\" IS NULL) OR (\"MasterProductId\" IS NULL AND \"SuggestedBrand\" IS NOT NULL AND \"SuggestedName\" IS NOT NULL AND \"SuggestedSizeValue\" IS NOT NULL AND \"SuggestedSizeUnit\" IS NOT NULL)");
                    table.CheckConstraint("CK_ProductMatchSuggestions_SuggestedSizePositive", "\"SuggestedSizeValue\" IS NULL OR \"SuggestedSizeValue\" > 0");
                    table.ForeignKey(
                        name: "FK_ProductMatchSuggestions_MasterProducts_MasterProductId",
                        column: x => x.MasterProductId,
                        principalTable: "MasterProducts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProductPriceTrackings",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BusinessId = table.Column<int>(type: "INTEGER", nullable: false),
                    ProductId = table.Column<long>(type: "INTEGER", nullable: false),
                    MasterProductId = table.Column<long>(type: "INTEGER", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    MatchSource = table.Column<int>(type: "INTEGER", nullable: true),
                    WaitingKey = table.Column<string>(type: "TEXT", maxLength: 1001, nullable: false),
                    SearchName = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    UnitLabel = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductPriceTrackings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProductPriceTrackings_MasterProducts_MasterProductId",
                        column: x => x.MasterProductId,
                        principalTable: "MasterProducts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProductPriceTrackings_Products_BusinessId_ProductId",
                        columns: x => new { x.BusinessId, x.ProductId },
                        principalTable: "Products",
                        principalColumns: new[] { "BusinessId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SupplierListings",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SupplierPresetId = table.Column<int>(type: "INTEGER", nullable: false),
                    MasterProductId = table.Column<long>(type: "INTEGER", nullable: true),
                    WaitingKey = table.Column<string>(type: "TEXT", maxLength: 1001, nullable: true),
                    OriginWaitingKey = table.Column<string>(type: "TEXT", maxLength: 1001, nullable: true),
                    SupplierProductId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Url = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    UnitsPerPack = table.Column<int>(type: "INTEGER", nullable: true),
                    Barcode = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    BarcodeScope = table.Column<int>(type: "INTEGER", nullable: true),
                    MatchStatus = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierListings", x => x.Id);
                    table.CheckConstraint("CK_SupplierListings_BarcodeAndScopeTogether", "(\"Barcode\" IS NULL AND \"BarcodeScope\" IS NULL) OR (\"Barcode\" IS NOT NULL AND \"BarcodeScope\" IS NOT NULL)");
                    table.CheckConstraint("CK_SupplierListings_OneOwner", "(\"MasterProductId\" IS NOT NULL AND \"WaitingKey\" IS NULL) OR (\"MasterProductId\" IS NULL AND \"WaitingKey\" IS NOT NULL)");
                    table.CheckConstraint("CK_SupplierListings_UnitsPerPackPositive", "\"UnitsPerPack\" IS NULL OR \"UnitsPerPack\" > 0");
                    table.ForeignKey(
                        name: "FK_SupplierListings_MasterProducts_MasterProductId",
                        column: x => x.MasterProductId,
                        principalTable: "MasterProducts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupplierListings_SupplierPresets_SupplierPresetId",
                        column: x => x.SupplierPresetId,
                        principalTable: "SupplierPresets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MasterProducts_NormalisedKey",
                table: "MasterProducts",
                column: "NormalisedKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductMatchSuggestions_MasterProductId_WaitingKey",
                table: "ProductMatchSuggestions",
                columns: new[] { "MasterProductId", "WaitingKey" });

            migrationBuilder.CreateIndex(
                name: "IX_ProductMatchSuggestions_WaitingKey_Status",
                table: "ProductMatchSuggestions",
                columns: new[] { "WaitingKey", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ProductPriceTrackings_BusinessId",
                table: "ProductPriceTrackings",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductPriceTrackings_BusinessId_MasterProductId",
                table: "ProductPriceTrackings",
                columns: new[] { "BusinessId", "MasterProductId" });

            migrationBuilder.CreateIndex(
                name: "IX_ProductPriceTrackings_BusinessId_ProductId",
                table: "ProductPriceTrackings",
                columns: new[] { "BusinessId", "ProductId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductPriceTrackings_MasterProductId",
                table: "ProductPriceTrackings",
                column: "MasterProductId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductPriceTrackings_WaitingKey",
                table: "ProductPriceTrackings",
                column: "WaitingKey");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierListings_MasterProductId",
                table: "SupplierListings",
                column: "MasterProductId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierListings_SupplierPresetId_SupplierProductId",
                table: "SupplierListings",
                columns: new[] { "SupplierPresetId", "SupplierProductId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SupplierListings_WaitingKey",
                table: "SupplierListings",
                column: "WaitingKey");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProductMatchSuggestions");

            migrationBuilder.DropTable(
                name: "ProductPriceTrackings");

            migrationBuilder.DropTable(
                name: "SupplierListings");

            migrationBuilder.DropTable(
                name: "MasterProducts");
        }
    }
}
