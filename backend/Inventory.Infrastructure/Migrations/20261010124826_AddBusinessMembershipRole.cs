using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Migrations
{
    /// <summary>
    /// Gives every business membership a role (issue #521). Purely additive: one new column on
    /// BusinessMemberships, no other table touched, no row added or removed, and no approval
    /// changed.
    ///
    /// The column's default backfills every existing membership with <c>Owner</c>, the access
    /// those members already have, so nobody loses access and nobody gains any across the
    /// upgrade. The literal is <c>Inventory.Domain.Tenancy.BusinessRole.Owner</c>'s stored value;
    /// it is written out rather than referenced because a migration must keep describing the
    /// schema change it made even if that enum is renumbered later.
    ///
    /// <c>0</c> - what the scaffolded default would have been, and what an unfilled integer column
    /// holds - is deliberately not a declared role: <c>BusinessMembershipResolutionPolicy</c>
    /// denies access for it. Backfilling with it would therefore have locked every existing member
    /// out on the first request after deployment.
    ///
    /// The column carries no CHECK constraint: SQLite cannot add one to an existing table without
    /// rebuilding it, and rebuilding the table that decides who may sign in is not worth the
    /// additional risk when an undeclared stored value already fails closed in the resolution
    /// policy - which is where a value written by a newer deployment has to be handled anyway.
    /// </summary>
    public partial class AddBusinessMembershipRole : Migration
    {
        /// <summary><c>BusinessRole.Owner</c>'s stored value.</summary>
        private const int OwnerRole = 40;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Role",
                table: "BusinessMemberships",
                type: "INTEGER",
                nullable: false,
                defaultValue: OwnerRole);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Role",
                table: "BusinessMemberships");
        }
    }
}
