using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Migrations
{
    /// <summary>
    /// Replaces the business active flag with the lifecycle status, and adds the profile columns a
    /// business application collects (issue #540).
    ///
    /// <para><strong>The mapping.</strong> <c>Status</c> is added first, every existing row is
    /// mapped from the flag it already carries - <c>IsActive = 1</c> becomes <c>Active</c>,
    /// <c>IsActive = 0</c> becomes <c>Deactivated</c> - and only then is <c>IsActive</c> dropped.
    /// The order is the point: the flag is still readable when the status is derived from it, so no
    /// business's access changes across the upgrade and nothing is inferred from anything else. The
    /// literals are the stored values of <c>Inventory.Domain.Tenancy.BusinessStatus</c>, written
    /// out rather than referenced because a migration must keep describing the schema change it
    /// made even if that enum is renumbered later.</para>
    ///
    /// <para><strong>No mirror is left behind.</strong> <c>IsActive</c> is removed rather than kept
    /// in step, because two columns describing whether a business may be reached is one too many:
    /// the next write path to forget one of them would leave the access decision reading a value
    /// nobody updated. The status is the single source of truth, and
    /// <c>BusinessMembershipResolutionPolicy</c> is the only thing that turns it into access.</para>
    ///
    /// <para><strong>The profile columns invent nothing.</strong> <c>ContactEmail</c>,
    /// <c>GstRegistered</c>, <c>Abn</c>, <c>AppliedAtUtc</c>, <c>DecidedAtUtc</c> and
    /// <c>RejectionReason</c> are all nullable and are backfilled with nothing at all. The
    /// application's existing business predates the application form: it has no contact address of
    /// its own to copy (a member's address is a different fact about a different party), no
    /// recorded GST registration, no ABN this migration could verify, and no application or
    /// decision instant - deriving one from <c>CreatedAtUtc</c> would claim a business event that
    /// never happened. <c>GstRegistered</c> is deliberately three-state for the same reason:
    /// "unknown" is the truth for that row, and assuming registration would feed a wrong fact into
    /// GST reporting.</para>
    ///
    /// <para><strong>Why <c>ALTER TABLE ... DROP COLUMN</c>.</strong> Dropping the flag through the
    /// provider's own <c>DropColumn</c> would rebuild the <c>Businesses</c> table - create a copy,
    /// move every row into it, drop the original and rename - because that is how SQLite used to
    /// have to do it. SQLite has supported dropping an unindexed, unconstrained column in place
    /// since 3.35, which is what the bundled library this application runs on provides, so the
    /// in-place form is used instead: rebuilding the table that decides who may reach a business's
    /// financial data is risk this change does not need. <c>IsActive</c> carries no index, no
    /// constraint and no default expression anything else reads, which is what makes the in-place
    /// drop available.</para>
    ///
    /// <para><strong>Reversible.</strong> <c>Down</c> restores the flag from the status by the same
    /// mapping read backwards - only <c>Active</c> becomes <c>1</c>, so a pending or rejected
    /// business reverts to inactive, which is the fail-closed direction - and then drops the status
    /// and the profile columns, discarding the profile values with them.</para>
    /// </summary>
    public partial class AddBusinessLifecycleStatusAndProfile : Migration
    {
        /// <summary><c>BusinessStatus.Active</c>'s stored value.</summary>
        private const int ActiveStatus = 20;

        /// <summary><c>BusinessStatus.Deactivated</c>'s stored value.</summary>
        private const int DeactivatedStatus = 40;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Active by default, which is the value every row the mapping below touches ends up
            // with and the value the entity and `bootstrap-business` both state, so a migrated and
            // a freshly bootstrapped database describe the existing business identically.
            migrationBuilder.AddColumn<int>(
                name: "Status",
                table: "Businesses",
                type: "INTEGER",
                nullable: false,
                defaultValue: ActiveStatus);

            migrationBuilder.AddColumn<string>(
                name: "Abn",
                table: "Businesses",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "AppliedAtUtc",
                table: "Businesses",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContactEmail",
                table: "Businesses",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DecidedAtUtc",
                table: "Businesses",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "GstRegistered",
                table: "Businesses",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RejectionReason",
                table: "Businesses",
                type: "TEXT",
                nullable: true);

            // Derived from the flag the row already carries, while it is still there to read.
            migrationBuilder.Sql(
                "UPDATE \"Businesses\" SET \"Status\" = CASE WHEN \"IsActive\" = 1 THEN "
                    + ActiveStatus + " ELSE " + DeactivatedStatus + " END;");

            migrationBuilder.Sql("ALTER TABLE \"Businesses\" DROP COLUMN \"IsActive\";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsActive",
                table: "Businesses",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            // Only Active maps back to an active business: a pending or rejected one reverts to
            // inactive, because the older schema has no way to say what it actually is and
            // reporting it as active would grant access the status refused.
            migrationBuilder.Sql(
                "UPDATE \"Businesses\" SET \"IsActive\" = CASE WHEN \"Status\" = "
                    + ActiveStatus + " THEN 1 ELSE 0 END;");

            migrationBuilder.Sql("ALTER TABLE \"Businesses\" DROP COLUMN \"RejectionReason\";");
            migrationBuilder.Sql("ALTER TABLE \"Businesses\" DROP COLUMN \"GstRegistered\";");
            migrationBuilder.Sql("ALTER TABLE \"Businesses\" DROP COLUMN \"DecidedAtUtc\";");
            migrationBuilder.Sql("ALTER TABLE \"Businesses\" DROP COLUMN \"ContactEmail\";");
            migrationBuilder.Sql("ALTER TABLE \"Businesses\" DROP COLUMN \"AppliedAtUtc\";");
            migrationBuilder.Sql("ALTER TABLE \"Businesses\" DROP COLUMN \"Abn\";");
            migrationBuilder.Sql("ALTER TABLE \"Businesses\" DROP COLUMN \"Status\";");
        }
    }
}
