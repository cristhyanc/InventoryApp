using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Migrations
{
    /// <summary>
    /// Takes <c>Owner</c> away from the <c>BusinessMemberships.Role</c> column default, so an
    /// insert that states no role can no longer produce a member who may manage members, the Nayax
    /// integration and data repairs (issue #679).
    ///
    /// <para><strong>What was wrong with the default.</strong>
    /// <c>AddBusinessMembershipRole</c> (issue #521) added the column with <c>DEFAULT 40</c> -
    /// <c>Owner</c> - because that is what every row it backfilled had to become: the members who
    /// already existed held exactly that access, and backfilling them with anything else would
    /// have locked them out on the first request after deployment. The backfill was right; the
    /// default outliving it was not. EF never removes a default the model does not declare, so it
    /// stayed in the schema permanently, and the column deciding what a member may do became one a
    /// write could leave to the database. The application half of that gap is closed in code (the
    /// entity's <c>Role</c> is now <c>required</c>, and <c>BusinessMembershipRoleEnforcer</c>
    /// refuses an undeclared role on <c>SaveChanges</c>); this closes the half that lives in the
    /// database, for an <c>INSERT</c> that never goes through EF at all.</para>
    ///
    /// <para><strong>The new default is not a role.</strong> The column keeps a default, and it is
    /// <c>0</c>, which <c>Inventory.Domain.Tenancy.BusinessRole</c> declares no role at:
    /// <c>BusinessMembershipResolutionPolicy</c> denies it as <c>RoleUnrecognised</c>, exactly as
    /// it denies every other undeclared stored value, so a row inserted without a role arrives
    /// granting nothing rather than owning the business. <c>0</c> rather than <c>Operator</c>
    /// deliberately - the least privileged declared role is still a role somebody was granted, and
    /// a row that merely omitted a column has been granted nothing.</para>
    ///
    /// <para><strong>Why a default at all.</strong> No default would be stronger still - the
    /// insert would fail outright on the <c>NOT NULL</c> column, which is what the neighbouring
    /// <c>IsActive</c> column has always done - but SQLite cannot drop a column default in place,
    /// and the only way there is the provider's table rebuild. This is the table that decides who
    /// may sign in, it carries the filtered unique index enforcing one active membership per
    /// identity (issue #522) and the restricted foreign key <c>MemberInvites</c> points at, and
    /// rebuilding it is risk this change does not need - the same judgement issues #521 and #584
    /// both recorded.</para>
    ///
    /// <para><strong>How every stored role is preserved.</strong> Four in-place statements and no
    /// table rebuild, the shape <c>RemoveBusinessStatusActiveDatabaseDefault</c> established: add
    /// the replacement column carrying the new default, copy each row's role into it, drop the old
    /// column, and rename the replacement into its place. <c>Role</c> is in no index, so the drop
    /// is a plain column drop. SQLite's <c>RENAME COLUMN</c> carries the column's default with it,
    /// so the result is <c>Role INTEGER NOT NULL DEFAULT 0</c> holding exactly the roles the rows
    /// already had - an Owner stays an Owner, a Manager a Manager, an Operator an Operator, and a
    /// value this code does not declare stays exactly as it was and keeps being denied. Only the
    /// column's position in the table changes, which nothing reads. The statements run inside EF's
    /// migration transaction, so a failure part-way through leaves the original column exactly as
    /// it was.</para>
    ///
    /// <para><strong>Reversible, and no row changes in either direction.</strong> <c>Down</c> is
    /// the same four statements with <c>DEFAULT 40</c> restored, so a downgrade returns the schema
    /// issue #521 left, again preserving every stored role. Reverting restores the
    /// accidental-<c>Owner</c> default; it promotes, demotes or revokes no member.</para>
    /// </summary>
    public partial class RemoveBusinessMembershipRoleOwnerDatabaseDefault : Migration
    {
        /// <summary>
        /// The column each role is moved through. Named for what it is, so a database that
        /// something outside EF's transaction managed to leave half-migrated is recognisable rather
        /// than mysterious.
        /// </summary>
        private const string ReplacementColumn = "RoleWithoutOwnerDefault";

        /// <summary>
        /// The new default: not <c>BusinessRole.Owner</c>, and not any declared role. Written out
        /// rather than referenced because a migration must keep describing the schema change it
        /// made even if that enum is renumbered later.
        /// </summary>
        private const int UndeclaredRole = 0;

        /// <summary><c>BusinessRole.Owner</c>'s stored value, the default being removed.</summary>
        private const int OwnerRole = 40;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) =>
            ReplaceRoleDefault(migrationBuilder, UndeclaredRole);

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            ReplaceRoleDefault(migrationBuilder, OwnerRole);

        private static void ReplaceRoleDefault(MigrationBuilder migrationBuilder, int defaultValue)
        {
            migrationBuilder.Sql(
                $"ALTER TABLE \"BusinessMemberships\" ADD COLUMN \"{ReplacementColumn}\" INTEGER NOT NULL "
                    + $"DEFAULT {defaultValue};");

            migrationBuilder.Sql(
                $"UPDATE \"BusinessMemberships\" SET \"{ReplacementColumn}\" = \"Role\";");

            migrationBuilder.Sql("ALTER TABLE \"BusinessMemberships\" DROP COLUMN \"Role\";");

            migrationBuilder.Sql(
                $"ALTER TABLE \"BusinessMemberships\" RENAME COLUMN \"{ReplacementColumn}\" TO \"Role\";");
        }
    }
}
