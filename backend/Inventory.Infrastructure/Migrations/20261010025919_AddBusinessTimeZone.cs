using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Migrations
{
    /// <summary>
    /// Gives every business its own IANA time zone (issue #499). Purely additive: one new column
    /// on Businesses, no other table touched, and not one stored instant reinterpreted or
    /// rewritten - only the zone the derivation on read uses.
    ///
    /// The column's default backfills the existing row(s) with <c>Australia/Sydney</c>, the zone
    /// this application's single business has always reported in, so the business day a report,
    /// dashboard period or effective-dated lookup means is unchanged across the upgrade.
    /// </summary>
    public partial class AddBusinessTimeZone : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TimeZoneId",
                table: "Businesses",
                type: "TEXT",
                nullable: false,
                defaultValue: "Australia/Sydney");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TimeZoneId",
                table: "Businesses");
        }
    }
}
