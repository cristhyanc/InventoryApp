using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Migrations
{
    /// <summary>
    /// Takes <c>Active</c> away from the <c>Businesses.Status</c> column default, so an insert that
    /// states no status can no longer produce a business whose data is reachable (issue #584).
    ///
    /// <para><strong>What was wrong with the default.</strong>
    /// <c>AddBusinessLifecycleStatusAndProfile</c> (issue #540) added the column with
    /// <c>DEFAULT 20</c> - <c>Active</c> - because every row it mapped ended up there anyway. That
    /// made the one value deciding whether a business's financial data may be reached the one value
    /// a write could leave to the schema. The application half of that gap is closed in code (the
    /// entity's <c>Status</c> is now <c>required</c>, and <c>BusinessStatusEnforcer</c> refuses an
    /// undeclared status on <c>SaveChanges</c>); this closes the half that lives in the database,
    /// for an <c>INSERT</c> that never goes through EF at all.</para>
    ///
    /// <para><strong>The new default is not a status.</strong> The column keeps a default, and it is
    /// <c>0</c>, which <c>Inventory.Domain.Tenancy.BusinessStatuses</c> declares no status at:
    /// <c>BusinessMembershipResolutionPolicy</c> denies it exactly as it denies every other
    /// non-<c>Active</c> value, so a row inserted without a status arrives unreachable rather than
    /// trading. <c>0</c> rather than <c>Pending</c> deliberately - <c>Pending</c> is a real business
    /// event ("this business applied and is awaiting a decision", which
    /// <c>GET /api/me/account-state</c> reports to its members), and a row that merely omitted a
    /// column has not applied for anything.</para>
    ///
    /// <para><strong>Why a default at all.</strong> No default would be stronger still - the insert
    /// would fail outright on the <c>NOT NULL</c> column - but SQLite cannot drop a column default
    /// in place, and the only way there is the provider's table rebuild: copy <c>Businesses</c> into
    /// a new table, move every row, drop the original and rename. That is the table every
    /// tenant-owned row's foreign key points at, and rebuilding the table that decides who may reach
    /// a business's financial data is risk this change does not need - the same judgement issue #540
    /// recorded when it dropped <c>IsActive</c> in place. A rebuild would also silently discard the
    /// <c>TimeZoneId</c> default, which this change has no business touching.</para>
    ///
    /// <para><strong>How every stored status is preserved.</strong> Four in-place statements and no
    /// table rebuild: add the replacement column carrying the new default, copy each row's status
    /// into it, drop the old column, and rename the replacement into its place. SQLite's
    /// <c>RENAME COLUMN</c> carries the column's default with it, so the result is
    /// <c>Status INTEGER NOT NULL DEFAULT 0</c> holding exactly the statuses the rows already had -
    /// an <c>Active</c> business stays <c>Active</c> and keeps its access, a <c>Deactivated</c> one
    /// stays denied, and a <c>Pending</c> or <c>Rejected</c> one is untouched. Only the column's
    /// position in the table changes, which nothing reads. The statements run inside EF's migration
    /// transaction, so a failure part-way through leaves the original column exactly as it was.</para>
    ///
    /// <para><strong>Reversible, and no row changes in either direction.</strong> <c>Down</c> is the
    /// same four statements with <c>DEFAULT 20</c> restored, so a downgrade returns the schema issue
    /// #540 left, again preserving every stored status. Reverting restores the accidental-<c>Active</c>
    /// default; it reactivates, deactivates or reclassifies no business.</para>
    /// </summary>
    public partial class RemoveBusinessStatusActiveDatabaseDefault : Migration
    {
        /// <summary>
        /// The column each status is moved through. Named for what it is, so a database that
        /// something outside EF's transaction managed to leave half-migrated is recognisable rather
        /// than mysterious.
        /// </summary>
        private const string ReplacementColumn = "StatusWithoutActiveDefault";

        /// <summary>
        /// The new default: not <c>BusinessStatus.Active</c>, and not any declared status. Written
        /// out rather than referenced because a migration must keep describing the schema change it
        /// made even if that enum is renumbered later.
        /// </summary>
        private const int UndeclaredStatus = 0;

        /// <summary><c>BusinessStatus.Active</c>'s stored value, the default being removed.</summary>
        private const int ActiveStatus = 20;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) =>
            ReplaceStatusDefault(migrationBuilder, UndeclaredStatus);

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            ReplaceStatusDefault(migrationBuilder, ActiveStatus);

        private static void ReplaceStatusDefault(MigrationBuilder migrationBuilder, int defaultValue)
        {
            migrationBuilder.Sql(
                $"ALTER TABLE \"Businesses\" ADD COLUMN \"{ReplacementColumn}\" INTEGER NOT NULL "
                    + $"DEFAULT {defaultValue};");

            migrationBuilder.Sql($"UPDATE \"Businesses\" SET \"{ReplacementColumn}\" = \"Status\";");

            migrationBuilder.Sql("ALTER TABLE \"Businesses\" DROP COLUMN \"Status\";");

            migrationBuilder.Sql(
                $"ALTER TABLE \"Businesses\" RENAME COLUMN \"{ReplacementColumn}\" TO \"Status\";");
        }
    }
}
