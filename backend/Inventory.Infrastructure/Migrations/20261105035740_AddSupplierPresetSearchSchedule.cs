using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Migrations
{
    /// <summary>
    /// Gives every platform supplier preset its own search schedule (issue #1108, the owner's
    /// decision of 5 November 2026): four additive columns on <c>SupplierPresets</c>, an
    /// <strong>explicit mapping</strong> from the freshness limit each row already stored, and the
    /// freshness then <strong>recalculated from the mapped schedule</strong>.
    ///
    /// <para><strong>The mapping is from the stored value and never from a name.</strong> The
    /// repository owner's recorded cadences are the only thing the old column holds, so they are what
    /// the schedule is read back out of:</para>
    /// <list type="bullet">
    ///   <item><c>30</c> becomes Daily, whose derived limit is 30 - the shipped default.</item>
    ///   <item><c>102</c> becomes Weekly on Thursday and Sunday, whose derived limit is 102 - the
    ///   27 October 2026 twice-weekly decision.</item>
    ///   <item><c>174</c> becomes Weekly on Thursday, whose derived limit is 174 - the 1 November
    ///   2026 once-a-week decision.</item>
    ///   <item>any other value becomes Daily, and its limit therefore becomes 30.</item>
    /// </list>
    ///
    /// <para>The last rule is the only one that changes a stored limit, and it changes it
    /// <em>downwards</em>, which can make some of that supplier's stored prices stale for comparison
    /// immediately. That is deliberate and is the fail-closed direction: a schedule nobody recorded
    /// is a schedule nothing can be derived from, and leaving an unexplained limit in place would
    /// leave a row whose schedule and whose freshness said different things - exactly the
    /// disagreement this issue exists to end. No row of the seeded database falls to it (Coles,
    /// Woolworths and Costco hold 30 and Big W holds 174), so it only ever reaches a preset a Super
    /// Admin added or edited after the seed.</para>
    ///
    /// <para><strong>Why the freshness is rewritten at all</strong> when three of the four rules
    /// write back the value that was already there: because what makes the pair consistent is that
    /// the limit is <em>derived</em> from the schedule, not that it happens to match. Writing it in
    /// the same statement as the schedule is what makes "no row ends with a schedule and a freshness
    /// that disagree" true of this migration rather than of a later audit.
    /// <c>AddSupplierPresetSearchScheduleMigrationTests</c> asserts each literal below equals
    /// <c>SupplierSearchSchedulePolicy.FreshnessHoursFor</c> of the schedule beside it, which is how
    /// the two stay in step without this migration calling into application code that may change.</para>
    ///
    /// <para><strong><c>ScheduleEffectiveFromUtc</c> is the instant this migration runs.</strong>
    /// It is read from the database's own clock rather than written as a literal, and that choice is
    /// the safe one in the only direction that matters: an instant in the <em>past</em> relative to
    /// the apply would let an answer recorded before the schedules existed cover a pair, and an
    /// instant in the <em>future</em> would make every pair of every supplier permanently due and
    /// paid for on every run. The apply instant is neither.</para>
    ///
    /// <para><strong>Nothing outside this one table is touched</strong>, no row is inserted or
    /// deleted, and no name, website, enabled state, searching state, market, GST basis, connector or
    /// <c>UpdatedAtUtc</c> is read or rewritten: this migration gives the presets columns they never
    /// had rather than making an edit on a Super Admin's behalf. There is no business data to touch -
    /// <c>SupplierPresets</c> is a platform table with no <c>BusinessId</c>, no query filter and no
    /// ownership stamp (docs/architecture.md § Tenant ownership) - and this migration adds none of
    /// those.</para>
    /// </summary>
    /// <inheritdoc />
    public partial class AddSupplierPresetSearchSchedule : Migration
    {
        /// <summary>
        /// The freshness limits the four mapping rules write, as literals, so this migration keeps
        /// recording the values it wrote whatever the policy later says. Each one is the longest gap
        /// between two consecutive occurrences of the schedule beside it, times 24, plus the six-hour
        /// margin - 1 day, 4 days and 7 days respectively.
        /// </summary>
        private const int DailyFreshnessHours = 30;

        private const int TwiceWeeklyFreshnessHours = 102;

        private const int WeeklyFreshnessHours = 174;

        /// <summary>
        /// The stored frequency numbers, written out because <c>SupplierSearchFrequency</c> is
        /// application code a migration must not depend on: <c>1</c> is Daily and <c>2</c> is Weekly.
        /// Nothing here maps to Fortnightly - no stored limit the owner recorded was a fortnightly
        /// one, and inventing a fortnight start date for a row nobody chose one for would be a claim
        /// about when that supplier is searched.
        /// </summary>
        private const int DailyFrequency = 1;

        private const int WeeklyFrequency = 2;

        /// <summary>
        /// The stored weekday lists, in <c>SupplierSearchSchedulePolicy</c>'s own canonical
        /// Monday-to-Sunday order - which is what makes one set of days have exactly one stored
        /// spelling, and therefore what lets a later save tell whether the schedule actually changed.
        /// </summary>
        private const string NoDays = "";

        private const string ThursdayAndSunday = "THU,SUN";

        private const string Thursday = "THU";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "FortnightStartDate",
                table: "SupplierPresets",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ScheduleEffectiveFromUtc",
                table: "SupplierPresets",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<string>(
                name: "SearchDaysOfWeek",
                table: "SupplierPresets",
                type: "TEXT",
                maxLength: 27,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "SearchFrequency",
                table: "SupplierPresets",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            // Every row's coverage boundary starts at the instant the schedules arrived, so no answer
            // recorded before this migration counts. SQLite's 'now' is UTC, and the format is the one
            // the provider writes DateTime values in.
            migrationBuilder.Sql(
                """
                UPDATE SupplierPresets
                SET ScheduleEffectiveFromUtc = strftime('%Y-%m-%d %H:%M:%f', 'now');
                """);

            // The explicit mapping, each statement writing the schedule and the freshness that
            // schedule derives together. The three recognised values write back the limit the row
            // already held, which is the point: after this the limit is derived rather than kept.
            migrationBuilder.Sql(
                $"""
                UPDATE SupplierPresets
                SET SearchFrequency = {WeeklyFrequency},
                    SearchDaysOfWeek = '{ThursdayAndSunday}',
                    FortnightStartDate = NULL,
                    PriceCheckFreshnessHours = {TwiceWeeklyFreshnessHours}
                WHERE PriceCheckFreshnessHours = {TwiceWeeklyFreshnessHours};
                """);

            migrationBuilder.Sql(
                $"""
                UPDATE SupplierPresets
                SET SearchFrequency = {WeeklyFrequency},
                    SearchDaysOfWeek = '{Thursday}',
                    FortnightStartDate = NULL,
                    PriceCheckFreshnessHours = {WeeklyFreshnessHours}
                WHERE PriceCheckFreshnessHours = {WeeklyFreshnessHours};
                """);

            migrationBuilder.Sql(
                $"""
                UPDATE SupplierPresets
                SET SearchFrequency = {DailyFrequency},
                    SearchDaysOfWeek = '{NoDays}',
                    FortnightStartDate = NULL,
                    PriceCheckFreshnessHours = {DailyFreshnessHours}
                WHERE PriceCheckFreshnessHours = {DailyFreshnessHours};
                """);

            // The fallback, stated as its own statement rather than folded into the one above so the
            // rule that can change a stored limit is visible on its own. The three statements before
            // it leave PriceCheckFreshnessHours exactly as they found it, so this predicate still
            // names the rows none of them recognised.
            migrationBuilder.Sql(
                $"""
                UPDATE SupplierPresets
                SET SearchFrequency = {DailyFrequency},
                    SearchDaysOfWeek = '{NoDays}',
                    FortnightStartDate = NULL,
                    PriceCheckFreshnessHours = {DailyFreshnessHours}
                WHERE PriceCheckFreshnessHours NOT IN (
                    {DailyFreshnessHours}, {TwiceWeeklyFreshnessHours}, {WeeklyFreshnessHours});
                """);
        }

        /// <summary>
        /// Drops the four columns again.
        ///
        /// <para><strong>Why it is hand-written SQL.</strong> EF's SQLite generator answers
        /// <c>DropColumnOperation</c> with a model-driven table rebuild, which needs the
        /// point-in-time model of the <em>preceding</em> migration - and
        /// <c>20261104090000_DropProviderIdentityLinkTables</c> deliberately records none, exactly as
        /// <c>AddSupplierPresetSearchPrices</c>' <c>Down</c> describes. SQLite has supported
        /// <c>ALTER TABLE ... DROP COLUMN</c> since 3.35, none of these columns is in an index, a
        /// unique constraint or a check constraint, so the statements below are both exact and
        /// independent of any recorded model. The table's check constraint on
        /// <c>PriceCheckFreshnessHours</c> names a different column and is unaffected.</para>
        ///
        /// <para><strong>What reversing does not restore.</strong> A row the fallback rule above
        /// moved to 30 hours keeps that limit: the value it held before is not recorded anywhere, so
        /// inventing one on the way back would be a different guess rather than a restoration. The
        /// three recognised limits were written back unchanged and so come back unchanged.</para>
        /// </summary>
        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"ALTER TABLE ""SupplierPresets"" DROP COLUMN ""FortnightStartDate"";");
            migrationBuilder.Sql(@"ALTER TABLE ""SupplierPresets"" DROP COLUMN ""ScheduleEffectiveFromUtc"";");
            migrationBuilder.Sql(@"ALTER TABLE ""SupplierPresets"" DROP COLUMN ""SearchDaysOfWeek"";");
            migrationBuilder.Sql(@"ALTER TABLE ""SupplierPresets"" DROP COLUMN ""SearchFrequency"";");
        }
    }
}
