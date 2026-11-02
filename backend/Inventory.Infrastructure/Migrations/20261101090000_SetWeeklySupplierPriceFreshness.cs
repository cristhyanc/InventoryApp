using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Migrations
{
    /// <summary>
    /// Records the owner's decision of 1 November 2026 that BIG W and Kmart are checked once a week
    /// (BIG W on Thursdays, when its catalogues start; Kmart on Mondays), because both charge per
    /// searched result: each record's price freshness limit becomes 174 hours, the 168-hour weekly
    /// gap plus the six-hour margin (<c>SupplierPriceFreshnessPolicy.WeeklyCheckLimitHours</c>).
    ///
    /// <para>A data-only change to two shipped rows, matched by normalised name exactly as
    /// <c>AddSupplierPriceObservations</c> set BIG W's twice-weekly 102: at most one row matches each
    /// name, and a database whose record was renamed or removed matches none and keeps its value. No
    /// schema, model or other row changes. <c>Down</c> restores the values the previous migration
    /// left (102 for BIG W, the daily 30 for Kmart).</para>
    /// </summary>
    public partial class SetWeeklySupplierPriceFreshness : Migration
    {
        /// <summary>
        /// The weekly limit in hours, written as a literal so this migration keeps recording the value
        /// it wrote whatever the policy later says; <c>SetWeeklySupplierPriceFreshnessMigrationTests</c>
        /// asserts the two agree.
        /// </summary>
        private const int WeeklyFreshnessHours = 174;

        private const int PreviousBigWFreshnessHours = 102;
        private const int PreviousKmartFreshnessHours = 30;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                $"""
                UPDATE SupplierPresets
                SET PriceCheckFreshnessHours = {WeeklyFreshnessHours}
                WHERE NormalisedName IN ('BIG W', 'KMART');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                $"""
                UPDATE SupplierPresets
                SET PriceCheckFreshnessHours = {PreviousBigWFreshnessHours}
                WHERE NormalisedName = 'BIG W';
                """);
            migrationBuilder.Sql(
                $"""
                UPDATE SupplierPresets
                SET PriceCheckFreshnessHours = {PreviousKmartFreshnessHours}
                WHERE NormalisedName = 'KMART';
                """);
        }
    }
}
