using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Migrations
{
    /// <summary>
    /// Creates the storage for issued collector work (issue #924, parent #817): the
    /// <c>PriceCollectorWorkItems</c> platform table, the expiry instant a run carries, and the index
    /// that finds the run still open.
    ///
    /// <para><strong>Additive, and it writes nothing.</strong> One new table, one new column and one
    /// new index on an existing table; no existing column is dropped or rewritten, no business,
    /// product, purchase, cost, inventory or report row is read or changed, and nothing is
    /// backfilled. The runs issue #925 created the table for keep a null expiry, which is a run
    /// nothing expires automatically - a lifetime is not something this migration may invent for a
    /// run that was issued before lifetimes existed.</para>
    ///
    /// <para><strong>A platform table, so there is no business key and no filter.</strong> A piece of
    /// work is a product description and a supplier's shelf, which are facts about products and about
    /// no business - the classification <c>MasterProduct</c> states and
    /// <c>BusinessOwnershipCoverageTests</c> asserts. Nothing here is tenant-owned, so nothing here is
    /// stamped or filtered.</para>
    ///
    /// <para><strong>Every reference restricts, and two rules are the database's.</strong> A run, a
    /// supplier master record and a master product cannot be deleted from under the work issued
    /// against them. The exactly-one-product rule and the outcome-with-its-instant rule are check
    /// constraints as well as <c>PriceCollectorWorkItemPolicy</c> rules, because a stored row is not
    /// something an application-level check can be trusted to have prevented; the outcome
    /// <em>vocabulary</em> is deliberately left to the Domain, because expressing it in SQL would
    /// hard-code the enum's numbers into migration history.</para>
    ///
    /// <para><strong>"One work item per product and supplier in a run" is two filtered unique indexes
    /// rather than one over four columns</strong>, because SQLite treats <c>NULL</c>s as distinct in a
    /// unique index. A work item names exactly one product, so one of the two key columns is always
    /// <c>NULL</c> - and a single index over both would have compared two identical master-product
    /// rows as different, enforcing the rule it appeared to state for nothing. Each filtered index
    /// covers the rows whose own key column is present.</para>
    ///
    /// <para><c>Down</c> drops the table, the index and the column, which is lossless for everything
    /// that existed before this migration: no run loses a count, a status or an observation, and the
    /// only thing that cannot survive a downgrade is work this release issued - which a release that
    /// predates work items has nothing to do with anyway.</para>
    /// </summary>
    public partial class AddPriceCollectorWorkItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ExpiresAtUtc",
                table: "PriceCollectorRuns",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PriceCollectorWorkItems",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PriceCollectorRunId = table.Column<long>(type: "INTEGER", nullable: false),
                    SupplierPresetId = table.Column<int>(type: "INTEGER", nullable: false),
                    MasterProductId = table.Column<long>(type: "INTEGER", nullable: true),
                    WaitingKey = table.Column<string>(type: "TEXT", maxLength: 509, nullable: true),
                    IssuedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Outcome = table.Column<int>(type: "INTEGER", nullable: true),
                    OutcomeAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PriceCollectorWorkItems", x => x.Id);
                    table.CheckConstraint("CK_PriceCollectorWorkItems_OneProduct", "(\"MasterProductId\" IS NOT NULL AND \"WaitingKey\" IS NULL) OR (\"MasterProductId\" IS NULL AND \"WaitingKey\" IS NOT NULL)");
                    table.CheckConstraint("CK_PriceCollectorWorkItems_OutcomeAndInstantTogether", "(\"Outcome\" IS NULL AND \"OutcomeAtUtc\" IS NULL) OR (\"Outcome\" IS NOT NULL AND \"OutcomeAtUtc\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_PriceCollectorWorkItems_MasterProducts_MasterProductId",
                        column: x => x.MasterProductId,
                        principalTable: "MasterProducts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PriceCollectorWorkItems_PriceCollectorRuns_PriceCollectorRunId",
                        column: x => x.PriceCollectorRunId,
                        principalTable: "PriceCollectorRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PriceCollectorWorkItems_SupplierPresets_SupplierPresetId",
                        column: x => x.SupplierPresetId,
                        principalTable: "SupplierPresets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PriceCollectorRuns_Status",
                table: "PriceCollectorRuns",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_PriceCollectorWorkItems_MasterProductId",
                table: "PriceCollectorWorkItems",
                column: "MasterProductId");

            migrationBuilder.CreateIndex(
                name: "IX_PriceCollectorWorkItems_PriceCollectorRunId",
                table: "PriceCollectorWorkItems",
                column: "PriceCollectorRunId");

            migrationBuilder.CreateIndex(
                name: "IX_PriceCollectorWorkItems_PriceCollectorRunId_SupplierPresetId_MasterProductId",
                table: "PriceCollectorWorkItems",
                columns: new[] { "PriceCollectorRunId", "SupplierPresetId", "MasterProductId" },
                unique: true,
                filter: "\"MasterProductId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PriceCollectorWorkItems_PriceCollectorRunId_SupplierPresetId_WaitingKey",
                table: "PriceCollectorWorkItems",
                columns: new[] { "PriceCollectorRunId", "SupplierPresetId", "WaitingKey" },
                unique: true,
                filter: "\"WaitingKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PriceCollectorWorkItems_SupplierPresetId",
                table: "PriceCollectorWorkItems",
                column: "SupplierPresetId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PriceCollectorWorkItems");

            migrationBuilder.DropIndex(
                name: "IX_PriceCollectorRuns_Status",
                table: "PriceCollectorRuns");

            migrationBuilder.DropColumn(
                name: "ExpiresAtUtc",
                table: "PriceCollectorRuns");
        }
    }
}
