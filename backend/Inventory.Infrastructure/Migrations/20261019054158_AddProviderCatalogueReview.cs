using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddProviderCatalogueReview : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProviderCatalogueFieldDecisions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BusinessId = table.Column<int>(type: "INTEGER", nullable: false),
                    ProductId = table.Column<long>(type: "INTEGER", nullable: false),
                    Field = table.Column<int>(type: "INTEGER", nullable: false),
                    ProviderValue = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Decision = table.Column<int>(type: "INTEGER", nullable: false),
                    DecidedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProviderCatalogueFieldDecisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProviderCatalogueFieldDecisions_Products_BusinessId_ProductId",
                        columns: x => new { x.BusinessId, x.ProductId },
                        principalTable: "Products",
                        principalColumns: new[] { "BusinessId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProviderCatalogueReviews",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BusinessId = table.Column<int>(type: "INTEGER", nullable: false),
                    Provider = table.Column<int>(type: "INTEGER", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AppliedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    AppliedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProviderCatalogueReviews", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProviderCatalogueReviewItems",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BusinessId = table.Column<int>(type: "INTEGER", nullable: false),
                    ReviewId = table.Column<long>(type: "INTEGER", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    ExternalId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ProviderName = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    ProviderGroupExternalId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    ProviderUnitPrice = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ProviderUnitVolume = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    LinkedProductId = table.Column<long>(type: "INTEGER", nullable: true),
                    LinkedCategoryId = table.Column<long>(type: "INTEGER", nullable: true),
                    UnresolvedSaleCount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProviderCatalogueReviewItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProviderCatalogueReviewItems_Categories_BusinessId_LinkedCategoryId",
                        columns: x => new { x.BusinessId, x.LinkedCategoryId },
                        principalTable: "Categories",
                        principalColumns: new[] { "BusinessId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProviderCatalogueReviewItems_Products_BusinessId_LinkedProductId",
                        columns: x => new { x.BusinessId, x.LinkedProductId },
                        principalTable: "Products",
                        principalColumns: new[] { "BusinessId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProviderCatalogueReviewItems_ProviderCatalogueReviews_ReviewId",
                        column: x => x.ReviewId,
                        principalTable: "ProviderCatalogueReviews",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProviderCatalogueFieldDecisions_BusinessId",
                table: "ProviderCatalogueFieldDecisions",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_ProviderCatalogueFieldDecisions_BusinessId_ProductId_Field",
                table: "ProviderCatalogueFieldDecisions",
                columns: new[] { "BusinessId", "ProductId", "Field" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProviderCatalogueReviewItems_BusinessId",
                table: "ProviderCatalogueReviewItems",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_ProviderCatalogueReviewItems_BusinessId_LinkedCategoryId",
                table: "ProviderCatalogueReviewItems",
                columns: new[] { "BusinessId", "LinkedCategoryId" });

            migrationBuilder.CreateIndex(
                name: "IX_ProviderCatalogueReviewItems_BusinessId_LinkedProductId",
                table: "ProviderCatalogueReviewItems",
                columns: new[] { "BusinessId", "LinkedProductId" });

            migrationBuilder.CreateIndex(
                name: "IX_ProviderCatalogueReviewItems_BusinessId_ReviewId_Kind",
                table: "ProviderCatalogueReviewItems",
                columns: new[] { "BusinessId", "ReviewId", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_ProviderCatalogueReviewItems_ReviewId",
                table: "ProviderCatalogueReviewItems",
                column: "ReviewId");

            migrationBuilder.CreateIndex(
                name: "IX_ProviderCatalogueReviews_BusinessId",
                table: "ProviderCatalogueReviews",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_ProviderCatalogueReviews_BusinessId_Pending",
                table: "ProviderCatalogueReviews",
                column: "BusinessId",
                unique: true,
                filter: "\"AppliedAtUtc\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProviderCatalogueFieldDecisions");

            migrationBuilder.DropTable(
                name: "ProviderCatalogueReviewItems");

            migrationBuilder.DropTable(
                name: "ProviderCatalogueReviews");
        }
    }
}
