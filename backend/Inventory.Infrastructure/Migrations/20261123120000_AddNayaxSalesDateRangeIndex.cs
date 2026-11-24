using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Migrations
{
    /// <summary>
    /// The sales date-range index of issue #1287 (audit #1226, finding DB-02): one additive
    /// <c>CREATE INDEX</c> on <c>NayaxSales (BusinessId, MachineAuthorizationTime)</c>, with no data
    /// change and no backfill. On a large table it holds the write lock while it builds, which is why
    /// the pull request reports the measured duration at the audit's size (0.80 s over 1,000,000
    /// sales, <c>NayaxSalesDateRangeIndexSizeBenchmark</c>).
    ///
    /// <para>The index's planner statistics are deliberately <strong>not</strong> collected here.
    /// Collecting is not a schema change, it has to happen again as history grows, and an applied
    /// migration never runs a second time - so it belongs to the application rather than to this
    /// file: <c>Inventory.Infrastructure.Persistence.SqliteSalesDateRangeStatistics</c>, asked by
    /// startup immediately after this migration is applied and by a finished sales history import,
    /// bounded by <c>Inventory.Domain.Maintenance.SalesDateRangeStatisticsPolicy</c> (issue #1287, the
    /// repository owner's decision of 24 November 2026).</para>
    ///
    /// <para>It is written by hand rather than scaffolded; the migration id, the context it belongs
    /// to and why it records no point-in-time model are in its designer file beside this one, the
    /// shape <c>AddPriceCollectorResults</c> already uses.</para>
    /// </summary>
    public partial class AddNayaxSalesDateRangeIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_NayaxSales_BusinessId_MachineAuthorizationTime",
                table: "NayaxSales",
                columns: new[] { "BusinessId", "MachineAuthorizationTime" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_NayaxSales_BusinessId_MachineAuthorizationTime",
                table: "NayaxSales");
        }
    }
}
